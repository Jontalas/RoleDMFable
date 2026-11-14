using System.Text;
using System.Text.Json;

namespace RoleDMFable.Core;

/// <summary>Una herramienta que el máster puede pedir. El esquema es JSON Schema.</summary>
public sealed record MasterTool(string Name, string Description, string SchemaJson);

/// <summary>Resultado de una herramienta: lo que se le devuelve al máster.</summary>
public sealed record ToolOutcome(string Result, bool IsError = false);

/// <summary>
/// Lo que el máster puede hacer en la mesa. El máster nunca calcula ni cambia nada
/// por su cuenta: pide una de estas acciones y aquí se ejecuta contra el estado real.
/// </summary>
public sealed class MasterToolbox
{
    public const string MasterName = "Máster";

    private readonly Campaign _game;
    private readonly IRandomSource _random;

    public MasterToolbox(Campaign game, IRandomSource random)
    {
        _game = game;
        _random = random;
    }

    /// <summary>Algo que los jugadores deben ver en el registro (una tirada, un daño, un movimiento).</summary>
    public event Action<string, string>? Announced;

    /// <summary>El grupo ha pasado a otro lugar por una acción del máster.</summary>
    public event Action<TravelResult>? Travelled;

    public static IReadOnlyList<MasterTool> Tools { get; } = new[]
    {
        new MasterTool("consultar_mesa",
            "Devuelve el estado actual de la mesa: plano del lugar, fichas con su posición y puntos de vida, y salidas. Úsala si has hecho cambios y necesitas ver cómo ha quedado todo.",
            """{"type":"object","properties":{}}"""),
        new MasterTool("tirar_dados",
            "Tira dados de verdad y devuelve el resultado. Úsala siempre que haga falta un número al azar: nunca inventes una tirada. Notación: 1d20+5, 2d6+3, 4d6kh3 (quedarse con los 3 más altos), 1d20+5-1d4.",
            """{"type":"object","properties":{"expresion":{"type":"string","description":"La tirada, por ejemplo 1d20+3"},"motivo":{"type":"string","description":"Para qué es la tirada, en pocas palabras. Lo ven los jugadores."},"quien":{"type":"string","description":"Id o nombre de la ficha que tira. Si se omite, tira el máster."}},"required":["expresion","motivo"]}"""),
        new MasterTool("mover_ficha",
            "Mueve una ficha a una casilla. Las coordenadas son columna x (0 a la izquierda) y fila y (0 arriba). Falla si la casilla está ocupada, fuera del mapa o no se puede pisar. Mueve a los héroes solo cuando su jugador lo haya declarado.",
            """{"type":"object","properties":{"ficha":{"type":"string","description":"Id de la ficha"},"x":{"type":"integer"},"y":{"type":"integer"}},"required":["ficha","x","y"]}"""),
        new MasterTool("cambiar_pv",
            "Suma o resta puntos de vida a una ficha. Cantidad negativa para daño, positiva para curación. El resultado nunca baja de 0 ni pasa del máximo.",
            """{"type":"object","properties":{"ficha":{"type":"string","description":"Id de la ficha"},"cantidad":{"type":"integer","description":"Negativa para daño, positiva para curación"},"motivo":{"type":"string","description":"Causa, en pocas palabras. Lo ven los jugadores."}},"required":["ficha","cantidad","motivo"]}"""),
        new MasterTool("medir_distancia",
            "Mide la distancia en pies entre dos fichas, contando las diagonales como en el tablero.",
            """{"type":"object","properties":{"desde":{"type":"string","description":"Id de una ficha"},"hasta":{"type":"string","description":"Id de la otra ficha"}},"required":["desde","hasta"]}"""),
    };

    public ToolOutcome Execute(string name, IReadOnlyDictionary<string, JsonElement> input)
    {
        try
        {
            return name switch
            {
                "consultar_mesa" => new ToolOutcome(MasterPrompt.DescribeTable(_game.Current)),
                "tirar_dados" => Roll(input),
                "mover_ficha" => Move(input),
                "cambiar_pv" => ChangeHp(input),
                "medir_distancia" => Measure(input),
                _ => Fail($"No existe la herramienta «{name}»."),
            };
        }
        catch (ArgumentException e)
        {
            return Fail(e.Message);
        }
    }

    private ToolOutcome Roll(IReadOnlyDictionary<string, JsonElement> input)
    {
        var text = Text(input, "expresion");
        var reason = Text(input, "motivo");
        if (!DiceExpression.TryParse(text, out var expression, out var error)) return Fail(error);

        var who = MasterName;
        if (OptionalText(input, "quien") is { } wanted)
        {
            var token = FindToken(wanted);
            if (token is null) return Fail(UnknownToken(wanted));
            who = token.Name;
        }

        var result = expression!.Roll(_random);
        Announced?.Invoke(who, $"tira {result.Describe()} ({reason})");
        return new ToolOutcome($"{result.Describe()}. Total: {result.Total}.");
    }

    private ToolOutcome Move(IReadOnlyDictionary<string, JsonElement> input)
    {
        var wanted = Text(input, "ficha");
        var token = FindToken(wanted);
        if (token is null) return Fail(UnknownToken(wanted));

        var move = _game.Current.MoveToken(token.Id, Int(input, "x"), Int(input, "y"));
        if (!move.Ok) return Fail($"{token.Name} no se mueve. {move.Explain()}");

        Announced?.Invoke(token.Name, $"se mueve {move.Feet} pies.");
        var message = $"{token.Name} está ahora en ({token.X}, {token.Y}) tras moverse {move.Feet} pies.";

        var travel = _game.TryTravel(token.Id);
        if (travel is not null)
        {
            Travelled?.Invoke(travel);
            message += $" Ha pisado una salida y el grupo ha pasado de {travel.FromScene} a {travel.ToScene}.\n\n"
                + MasterPrompt.DescribeTable(_game.Current);
        }
        return new ToolOutcome(message);
    }

    private ToolOutcome ChangeHp(IReadOnlyDictionary<string, JsonElement> input)
    {
        var wanted = Text(input, "ficha");
        var token = FindToken(wanted);
        if (token is null) return Fail(UnknownToken(wanted));
        var amount = Int(input, "cantidad");
        var reason = Text(input, "motivo");
        if (amount == 0) return Fail("La cantidad no puede ser 0.");

        var before = token.Hp;
        var after = _game.Current.SetHp(token.Id, before + amount);
        var verb = amount < 0 ? $"pierde {before - after} PV" : $"recupera {after - before} PV";
        Announced?.Invoke(token.Name, $"{verb} ({reason}). Le quedan {after} de {token.MaxHp}.");
        var fallen = after == 0 ? " Ha caído a 0." : "";
        return new ToolOutcome($"{token.Name} {verb}: de {before} a {after} de {token.MaxHp}.{fallen}");
    }

    private ToolOutcome Measure(IReadOnlyDictionary<string, JsonElement> input)
    {
        var fromName = Text(input, "desde");
        var toName = Text(input, "hasta");
        var from = FindToken(fromName);
        var to = FindToken(toName);
        if (from is null) return Fail(UnknownToken(fromName));
        if (to is null) return Fail(UnknownToken(toName));
        var feet = GridDistance.Feet(from.X, from.Y, to.X, to.Y, _game.Current.CellFeet);
        return new ToolOutcome($"Entre {from.Name} y {to.Name} hay {feet} pies.");
    }

    /// <summary>Busca por id y, si no, por nombre sin distinguir mayúsculas (solo si es único).</summary>
    private Token? FindToken(string idOrName)
    {
        var table = _game.Current;
        if (table.FindToken(idOrName) is { } byId) return byId;
        var byName = table.Tokens.Where(t => string.Equals(t.Name, idOrName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Id, idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    private string UnknownToken(string wanted) =>
        $"No hay ninguna ficha «{wanted}» en este lugar. Fichas: {string.Join(", ", _game.Current.Tokens.Select(t => t.Id))}.";

    private static ToolOutcome Fail(string message) => new(message, IsError: true);

    private static string Text(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        OptionalText(input, key) ?? throw new ArgumentException($"Falta el dato «{key}».");

    private static string? OptionalText(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static int Int(IReadOnlyDictionary<string, JsonElement> input, string key)
    {
        if (!input.TryGetValue(key, out var value)) throw new ArgumentException($"Falta el dato «{key}».");
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
        throw new ArgumentException($"«{key}» tiene que ser un número entero.");
    }
}

/// <summary>Los textos que recibe el máster: sus instrucciones fijas y la situación de cada turno.</summary>
public static class MasterPrompt
{
    /// <summary>Cuántas entradas recientes del registro se le recuerdan al máster en cada turno.</summary>
    public const int RecentLogEntries = 30;

    /// <summary>Instrucciones fijas. No cambian entre turnos para que se puedan reutilizar en caché.</summary>
    public const string Instructions = """
        Eres el máster de una partida de rol de mesa que se juega en un tablero virtual, en español. Los jugadores llevan a los héroes; tú llevas el mundo, los enemigos y los personajes no jugadores.

        Cómo funciona la mesa
        - Los jugadores ven siempre el mapa con las fichas. Lo que narres tiene que coincidir con lo que hay en la mesa: no inventes salas, criaturas ni objetos que no aparezcan en el estado que se te da. Puedes añadir detalles de ambiente (olores, sonidos, luz) que no contradigan el mapa.
        - En cada turno recibes el estado de la mesa, lo último que ha pasado y lo que dice o hace un jugador.
        - El mapa es una cuadrícula. Las coordenadas son (x, y): x es la columna, empezando en 0 por la izquierda; y es la fila, empezando en 0 por arriba. Cada casilla mide lo que indique el estado, normalmente 5 pies.
        - Las zonas marcadas como no exploradas y las fichas marcadas como ocultas todavía no las ven los jugadores: no las describas ni las reveles hasta que las descubran.

        Herramientas
        - Tú no calculas números ni cambias la mesa de palabra. Para cualquier tirada usa tirar_dados; para mover una ficha, mover_ficha; para daño o curación, cambiar_pv. Si narras que algo se mueve o recibe daño, tiene que haber ocurrido con la herramienta.
        - Narra el resultado después de ver lo que devuelve la herramienta, nunca antes.
        - Si una herramienta falla, lee el motivo y corrige o cambia de plan. No finjas que ha funcionado.
        - Los jugadores pueden mover sus fichas y tirar dados por su cuenta: lo verás en lo último que ha pasado. Dalo por bueno.

        Reglas
        - Todavía no hay un reglamento cargado. Mientras tanto resuelve las acciones inciertas con una tirada de 1d20 contra una dificultad que decides antes de tirar (10 fácil, 15 normal, 20 difícil), y el daño con dados pequeños (1d4 a 1d8). Di la dificultad con naturalidad al pedir o hacer la tirada. No cites reglas de ningún juego concreto como si fueran oficiales.
        - Las acciones sin riesgo ni incertidumbre no necesitan tirada.
        - Sé justo: los enemigos actúan con sentido, pero no hagas trampas ni a favor ni en contra de los jugadores.

        Cómo narrar
        - Solo los jugadores deciden lo que piensan, dicen y hacen sus héroes. Tú describes el mundo y las consecuencias, y te detienes cuando toca decidir a un jugador.
        - Respuestas breves: de dos a cinco frases en lo normal, algo más en un momento importante. Prosa en segunda persona, sin listas, sin encabezados y sin formato.
        - Tono de aventura de fantasía apto para todos los públicos, con detalles concretos y algo de humor cuando encaje.
        - No termines siempre con la misma pregunta. Que la propia situación deje claro que les toca a los jugadores.
        - Si un jugador pregunta algo fuera del juego (cómo funciona la mesa, una duda), respóndele con claridad y brevedad.
        """;

    /// <summary>El mensaje de un turno: estado de la mesa, registro reciente y lo que dice el jugador.</summary>
    public static string BuildTurn(Campaign game, string speaker, string text)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ESTADO DE LA MESA");
        builder.AppendLine(DescribeTable(game.Current));
        builder.AppendLine();

        // Lo que acaba de decir el jugador suele estar ya apuntado en el registro: no se repite.
        var log = game.Log.Count > 0 && game.Log[^1] == new LogEntry(speaker, text) ? game.Log.Take(game.Log.Count - 1).ToList() : game.Log;
        var recent = log.Skip(Math.Max(0, log.Count - RecentLogEntries)).ToList();
        if (recent.Count > 0)
        {
            builder.AppendLine("LO ÚLTIMO QUE HA PASADO (lo más reciente al final)");
            foreach (var entry in recent) builder.AppendLine($"- {entry.Who}: {entry.Text}");
            builder.AppendLine();
        }

        builder.AppendLine("AHORA");
        builder.Append($"{speaker} dice: {text}");
        return builder.ToString();
    }

    public static string DescribeTable(TableState table)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Lugar: {table.SceneName} (id {table.SceneId}). Cuadrícula de {table.Width} columnas por {table.Height} filas; cada casilla mide {table.CellFeet} pies.");
        builder.AppendLine("Plano (cada carácter es una casilla; la primera línea es la fila y=0 y el primer carácter la columna x=0):");
        for (var y = 0; y < table.Height; y++)
        {
            for (var x = 0; x < table.Width; x++) builder.Append(Symbol(table.TerrainAt(x, y)));
            builder.AppendLine();
        }
        builder.AppendLine("Leyenda: . suelo, # muro, ~ agua, = puente, o roca. Solo se puede pisar suelo y puente.");

        var hidden = 0;
        for (var y = 0; y < table.Height; y++)
            for (var x = 0; x < table.Width; x++)
                if (table.IsUnexplored(x, y)) hidden++;
        if (hidden > 0)
        {
            var columns = Enumerable.Range(0, table.Width)
                .Where(x => Enumerable.Range(0, table.Height).All(y => table.IsUnexplored(x, y)))
                .ToList();
            var detail = columns.Count > 0 ? $" Columnas enteras sin explorar: {string.Join(", ", columns)}." : "";
            builder.AppendLine($"Zona sin explorar por los jugadores: {hidden} casillas.{detail}");
        }

        builder.AppendLine("Fichas:");
        foreach (var token in table.Tokens)
        {
            var kind = token.Kind switch
            {
                TokenKind.Hero => token.Seat is { } seat ? $"héroe, jugador del asiento {seat}" : "héroe, sin jugador ahora mismo",
                TokenKind.Enemy => "enemigo, lo llevas tú",
                _ => "personaje no jugador, lo llevas tú",
            };
            var role = token.Role.Length > 0 ? $", {token.Role}" : "";
            var state = token.Hp == 0 ? ", fuera de combate" : "";
            var unseen = table.IsUnexplored(token.X, token.Y) ? ", oculta a los jugadores" : "";
            builder.AppendLine($"- {token.Id}: {token.Name} ({kind}{role}) en ({token.X}, {token.Y}), PV {token.Hp}/{token.MaxHp}{state}{unseen}");
        }

        if (table.Exits.Count > 0)
        {
            builder.AppendLine("Salidas (si un héroe la pisa, todo el grupo pasa al otro lugar):");
            foreach (var exit in table.Exits)
            {
                var label = exit.Label.Length > 0 ? $" «{exit.Label}»" : "";
                var unseen = table.IsUnexplored(exit.X, exit.Y) ? ", todavía no descubierta" : "";
                builder.AppendLine($"- en ({exit.X}, {exit.Y}){label}, lleva a {exit.To}{unseen}");
            }
        }
        return builder.ToString().TrimEnd();
    }

    private static char Symbol(Terrain terrain) => terrain switch
    {
        Terrain.Floor => '.',
        Terrain.Wall => '#',
        Terrain.Water => '~',
        Terrain.Bridge => '=',
        Terrain.Rock => 'o',
        _ => '?',
    };
}

/// <summary>Lo que ha costado un turno del máster, en tokens.</summary>
public sealed record MasterUsage(long InputTokens, long OutputTokens, long CachedTokens);

/// <summary>Fallo al hablar con el máster, con un mensaje que se puede enseñar tal cual al jugador.</summary>
public sealed class MasterException : Exception
{
    public MasterException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Un máster: recibe lo que dice un jugador y responde usando la caja de herramientas.</summary>
public interface IMaster
{
    /// <param name="narrate">Se llama con cada trozo de narración, en orden.</param>
    Task<MasterUsage> PlayTurnAsync(Campaign game, MasterToolbox tools, string speaker, string text, Action<string> narrate, CancellationToken cancel = default);
}
