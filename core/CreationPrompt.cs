using System.Text;
using System.Text.Json;

namespace RoleDMFable.Core;

/// <summary>
/// Lo que se le pide al máster para que lea una vez el reglamento cargado y prepare
/// la guía de creación de personajes: instrucciones, herramientas y cómo se recoge el resultado.
/// </summary>
public static class CreationGuidePrompt
{
    public const string SearchTool = "buscar_reglas";
    public const string SaveTool = "guardar_guia";
    public const int FirstPassages = 24;

    /// <summary>Con lo que se busca el capítulo de creación antes de preguntar nada al máster.</summary>
    private static readonly string[] Queries =
    {
        "creación de personaje pasos crear personaje", "características atributos puntuaciones tirar dados", "razas pueblos elegir raza",
        "clases profesiones arquetipos elegir clase", "puntos de vida puntos de golpe iniciales nivel 1", "equipo inicial dinero inicial",
        "habilidades iniciales competencias trasfondo",
    };

    public const string Instructions = """
        Vas a preparar el asistente de creación de personajes de un juego de rol de mesa a partir del reglamento que ha cargado el grupo. El asistente es una pantalla por pasos, con botones y dados, que el jugador recorre sin hablar con nadie: tú solo escribes los pasos, una vez.

        Cómo trabajar:
        - Lee los pasajes que recibes. Si falta algo del proceso de creación (la lista de razas o clases, cómo se sacan las características, los puntos de vida iniciales, el equipo), búscalo con buscar_reglas usando las palabras del propio manual. Haz pocas búsquedas y concretas.
        - Sigue el orden de creación que marca el manual. No inventes reglas ni opciones que no hayas leído: si algo no aparece, deja que el jugador lo escriba en un paso de texto.
        - Cuando lo tengas, llama una sola vez a guardar_guia. No escribas la guía como texto.

        Tipos de paso:
        - choice: el jugador elige una opción de una lista (raza, clase, alineamiento, trasfondo). Pon todas las opciones del manual, cada una con un detalle de una línea que diga qué aporta, con tus palabras.
        - roll: el juego tira los dados que indiques en dice (por ejemplo 3d6, 4d6kh3, 1d8+2) y apunta el total. Con targets se hace una tirada para cada dato de la lista, en orden (por ejemplo las seis características).
        - number: el jugador pone un número entre min y max. Úsalo cuando el valor se calcula o se reparte y no basta una tirada; explica en help cómo se saca.
        - text: el jugador escribe algo libre (nombre de un conjuro, equipo, historia).

        Reglas del asistente:
        - Cada paso apunta su resultado en la ficha con el nombre de entry (o el título si no pones entry). Usa los nombres del manual.
        - No pidas el nombre del personaje ni su aspecto: eso ya lo pregunta el juego antes del primer paso.
        - Si un paso solo toca según una elección anterior (los puntos de vida de cada clase, los conjuros del mago), pon en when el nombre de esa elección y en whenValues las opciones con las que aparece. Puedes hacer varios pasos para el mismo dato, uno por grupo de opciones.
        - Marca con hp el paso que da los puntos de vida máximos iniciales. Tiene que dar un solo número.
        - Marca optional lo que el manual deja a gusto del jugador.
        - help es lo que lee el jugador en ese paso: dos o tres frases claras, en español, con tus palabras, sin copiar párrafos del manual.
        - Como mucho 30 pasos. Prefiere pocos pasos claros a muchos pequeños.
        """;

    public static IReadOnlyList<MasterTool> Tools { get; } = new[]
    {
        new MasterTool(SearchTool,
            "Busca en el reglamento cargado y devuelve los pasajes que mejor encajan, con libro y página.",
            """{"type":"object","properties":{"consulta":{"type":"string","description":"Palabras clave, como las usaría el manual"}},"required":["consulta"]}"""),
        new MasterTool(SaveTool,
            "Guarda la guía de creación de personajes terminada. Se llama una sola vez.",
            """
            {"type":"object","properties":{
              "title":{"type":"string","description":"Nombre del juego o del reglamento"},
              "steps":{"type":"array","items":{"type":"object","properties":{
                "title":{"type":"string"},
                "kind":{"type":"string","enum":["choice","roll","number","text"]},
                "help":{"type":"string"},
                "entry":{"type":"string","description":"Nombre del dato en la ficha, si no es el título"},
                "options":{"type":"array","items":{"type":"object","properties":{"label":{"type":"string"},"detail":{"type":"string"}},"required":["label"]}},
                "dice":{"type":"string"},
                "targets":{"type":"array","items":{"type":"string"}},
                "min":{"type":"integer"},
                "max":{"type":"integer"},
                "optional":{"type":"boolean"},
                "hp":{"type":"boolean"},
                "when":{"type":"string"},
                "whenValues":{"type":"array","items":{"type":"string"}}},
                "required":["title","kind"]}}},
             "required":["title","steps"]}
            """),
    };

    /// <summary>Los títulos de los reglamentos cargados: sirve para saber de qué libros salió una guía.</summary>
    public static string SourceOf(RulesLibrary rules) => string.Join(" + ", rules.BooksOf(BookKind.Rules).Select(b => b.Title));

    /// <summary>El primer mensaje: los pasajes del reglamento que más hablan de crear personajes.</summary>
    public static string FirstMessage(RulesLibrary rules)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hits = new List<RuleHit>();
        // Por turnos, para que todas las búsquedas aporten algo aunque una acapare resultados.
        var lists = Queries.Select(q => rules.Search(q, FirstPassages)).ToList();
        for (var rank = 0; rank < FirstPassages && hits.Count < FirstPassages; rank++)
            foreach (var list in lists)
                if (rank < list.Count && hits.Count < FirstPassages && seen.Add(list[rank].Source + "\n" + list[rank].Text))
                    hits.Add(list[rank]);

        var text = new StringBuilder();
        text.AppendLine($"Reglamentos cargados: {SourceOf(rules)}.");
        text.AppendLine("Estos son los pasajes que más hablan de crear personajes. Prepara la guía de creación y guárdala con guardar_guia.");
        foreach (var hit in hits)
        {
            text.AppendLine();
            text.AppendLine($"[{hit.Source}]");
            text.AppendLine(hit.Text);
        }
        return text.ToString();
    }

    /// <summary>Resultado de buscar_reglas.</summary>
    public static ToolOutcome Search(RulesLibrary rules, IReadOnlyDictionary<string, JsonElement> input)
    {
        var query = input.TryGetValue("consulta", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        var hits = rules.Search(query, 6);
        if (hits.Count == 0) return new ToolOutcome("No hay nada sobre eso en el reglamento. Prueba con otras palabras.");
        return new ToolOutcome(string.Join("\n\n", hits.Select(h => $"[{h.Source}]\n{h.Text}")));
    }

    /// <summary>
    /// Recoge lo que el máster manda a guardar_guia. Si no vale, <paramref name="error"/> dice por qué,
    /// en palabras que el máster puede usar para corregirla.
    /// </summary>
    public static CreationGuide? Accept(IReadOnlyDictionary<string, JsonElement> input, string source, out string error)
    {
        error = "";
        try
        {
            return CreationGuide.FromJson(JsonSerializer.Serialize(input)) with { Source = source };
        }
        catch (FormatException e)
        {
            error = $"La guía no vale: {e.Message} Corrígela y vuelve a llamar a guardar_guia.";
            return null;
        }
    }
}
