namespace RoleDMFable.Core;

public sealed class Token
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required TokenKind Kind { get; init; }
    public int X { get; internal set; }
    public int Y { get; internal set; }
    public int Hp { get; internal set; }
    public int MaxHp { get; init; }
    public string Color { get; init; } = "#888888";
    public string Icon { get; init; } = "";
    public string Role { get; init; } = "";
    /// <summary>Asiento local que lo controla, o null si lo lleva el máster u otro PC.</summary>
    public int? Seat { get; init; }

    internal List<SheetEntry> SheetEntries { get; } = new();

    /// <summary>Hoja de personaje: datos libres, para que valga con cualquier reglamento.</summary>
    public IReadOnlyList<SheetEntry> Sheet => SheetEntries;
}

/// <summary>Un combate en marcha. Solo <see cref="TableState"/> lo cambia.</summary>
public sealed class CombatState
{
    internal CombatState(IReadOnlyList<CombatantSpec> order, int round, int turn)
    {
        Order = order;
        Round = round;
        Turn = turn;
    }

    /// <summary>De mayor a menor iniciativa.</summary>
    public IReadOnlyList<CombatantSpec> Order { get; }
    public int Round { get; internal set; }
    /// <summary>Posición en <see cref="Order"/> de quien tiene el turno.</summary>
    public int Turn { get; internal set; }
    public string CurrentTokenId => Order[Turn].Token;
}

public enum MoveError
{
    None,
    UnknownToken,
    OutOfBounds,
    Blocked,
    Occupied,
}

public readonly record struct MoveResult(MoveError Error, int Feet)
{
    public bool Ok => Error == MoveError.None;

    public string Explain() => Error switch
    {
        MoveError.None => "",
        MoveError.UnknownToken => "Esa ficha no existe.",
        MoveError.OutOfBounds => "Eso queda fuera del mapa.",
        MoveError.Blocked => "Ahí no se puede pisar.",
        MoveError.Occupied => "Esa casilla ya está ocupada.",
        _ => "Movimiento no válido.",
    };
}

/// <summary>
/// El estado de la mesa: terreno, fichas y niebla. Es la única autoridad sobre
/// dónde está cada cosa; la interfaz y, más adelante, el máster y la red solo le piden cambios.
/// </summary>
public sealed class TableState
{
    private readonly Terrain[,] _terrain;
    private readonly bool[,] _unexplored;
    private readonly Dictionary<string, Token> _tokens = new(StringComparer.Ordinal);
    private readonly List<Token> _order = new();

    public string SceneId { get; }
    public string SceneName { get; }
    public int Width { get; }
    public int Height { get; }
    public int CellFeet { get; }
    public IReadOnlyList<Token> Tokens => _order;
    public IReadOnlyList<ExitSpec> Exits { get; }

    public const int MaxSheetEntries = 60;
    public const int MaxSheetNameLength = 40;
    public const int MaxSheetValueLength = 200;

    /// <summary>El combate en marcha, o null si no hay.</summary>
    public CombatState? Combat { get; private set; }

    /// <summary>La ficha que tiene el turno, o null si no hay combate.</summary>
    public Token? TurnToken => Combat is null ? null : FindToken(Combat.CurrentTokenId);

    /// <summary>Casillas a la redonda que un héroe deja exploradas al pasar.</summary>
    public const int SightCells = 4;

    /// <summary>Se dispara tras cada cambio aplicado, para que la interfaz se redibuje.</summary>
    public event Action? Changed;

    public TableState(SceneSpec spec)
    {
        spec.Validate();
        SceneId = spec.Id;
        SceneName = spec.Name;
        Width = spec.Width;
        Height = spec.Height;
        CellFeet = spec.CellFeet;
        Exits = (spec.Exits ?? Array.Empty<ExitSpec>()).ToArray();
        _terrain = new Terrain[Width, Height];
        _unexplored = new bool[Width, Height];

        for (var x = 0; x < Width; x++)
        for (var y = 0; y < Height; y++)
            _terrain[x, y] = spec.Base;

        // Las piezas posteriores pisan a las anteriores: un puente sobre el agua, por ejemplo.
        foreach (var feature in spec.Features ?? Array.Empty<FeatureSpec>())
            Fill(feature.X, feature.Y, feature.W, feature.H, (x, y) => _terrain[x, y] = feature.Type);
        foreach (var rect in spec.Unexplored ?? Array.Empty<CellRect>())
            Fill(rect.X, rect.Y, rect.W, rect.H, (x, y) => _unexplored[x, y] = true);

        foreach (var t in spec.Tokens ?? Array.Empty<TokenSpec>())
        {
            var token = new Token
            {
                Id = t.Id, Name = t.Name, Kind = t.Kind, X = t.X, Y = t.Y,
                Hp = Math.Clamp(t.Hp, 0, Math.Max(t.MaxHp, 0)), MaxHp = Math.Max(t.MaxHp, 0),
                Color = t.Color, Icon = t.Icon, Role = t.Role, Seat = t.Seat,
            };
            foreach (var entry in t.Sheet ?? Array.Empty<SheetEntry>())
                if (CleanEntry(entry.Name, entry.Value) is { } clean && token.SheetEntries.Count < MaxSheetEntries)
                    token.SheetEntries.Add(clean);
            _tokens.Add(token.Id, token);
            _order.Add(token);
        }

        foreach (var hero in _order.Where(t => t.Kind == TokenKind.Hero))
            RevealAround(hero.X, hero.Y, notify: false);

        // Un combate guardado solo se retoma si sus participantes siguen aquí.
        if (spec.Combat is { Order.Count: > 0 } combat)
        {
            var order = combat.Order.Where(c => _tokens.ContainsKey(c.Token)).ToList();
            if (order.Count > 0)
                Combat = new CombatState(order, Math.Max(1, combat.Round), Math.Clamp(combat.Turn, 0, order.Count - 1));
        }
    }

    // ---------- Combate ----------

    /// <summary>
    /// Empieza un combate con las iniciativas ya tiradas. Ordena de mayor a menor; en los empates
    /// se respeta el orden en que llegan. Lanza <see cref="ArgumentException"/> si algo no cuadra.
    /// </summary>
    public CombatState StartCombat(IEnumerable<CombatantSpec> combatants)
    {
        var list = combatants.ToList();
        if (list.Count == 0) throw new ArgumentException("Un combate necesita al menos un participante.");
        foreach (var combatant in list)
            if (!_tokens.ContainsKey(combatant.Token)) throw new ArgumentException($"No existe la ficha «{combatant.Token}».");
        if (list.Select(c => c.Token).Distinct().Count() != list.Count)
            throw new ArgumentException("Hay una ficha repetida entre los participantes.");

        var order = list.OrderByDescending(c => c.Initiative).ToList();
        Combat = new CombatState(order, 1, 0);
        if (!CanAct(Combat.CurrentTokenId)) Advance();
        Changed?.Invoke();
        return Combat;
    }

    /// <summary>
    /// Pasa el turno al siguiente que siga en pie; al dar la vuelta empieza otra ronda.
    /// Devuelve a quién le toca, o null si no hay combate o no queda nadie en pie.
    /// </summary>
    public Token? NextTurn()
    {
        if (Combat is null) return null;
        var moved = Advance();
        Changed?.Invoke();
        return moved ? TurnToken : null;
    }

    public bool EndCombat()
    {
        if (Combat is null) return false;
        Combat = null;
        Changed?.Invoke();
        return true;
    }

    private bool CanAct(string tokenId) => FindToken(tokenId) is { Hp: > 0 };

    private bool Advance()
    {
        var combat = Combat!;
        for (var step = 0; step < combat.Order.Count; step++)
        {
            combat.Turn++;
            if (combat.Turn >= combat.Order.Count)
            {
                combat.Turn = 0;
                combat.Round++;
            }
            if (CanAct(combat.CurrentTokenId)) return true;
        }
        return false;
    }

    // ---------- Hojas de personaje ----------

    /// <summary>
    /// Apunta un dato en la hoja de una ficha. Si el dato ya existe (sin distinguir mayúsculas) lo cambia;
    /// con valor vacío lo borra. Devuelve false si la hoja está llena o el nombre no vale.
    /// </summary>
    public bool SetSheetEntry(string tokenId, string name, string value)
    {
        var token = FindToken(tokenId) ?? throw new ArgumentException($"No existe la ficha «{tokenId}».", nameof(tokenId));
        var cleanName = Clean(name, MaxSheetNameLength);
        if (cleanName.Length == 0) return false;
        var index = token.SheetEntries.FindIndex(e => string.Equals(e.Name, cleanName, StringComparison.OrdinalIgnoreCase));
        var clean = CleanEntry(name, value);
        if (clean is null)
        {
            if (index < 0) return true;
            token.SheetEntries.RemoveAt(index);
        }
        else if (index >= 0)
        {
            // Se conserva el nombre tal como se apuntó la primera vez.
            clean = clean with { Name = token.SheetEntries[index].Name };
            if (token.SheetEntries[index] == clean) return true;
            token.SheetEntries[index] = clean;
        }
        else
        {
            if (token.SheetEntries.Count >= MaxSheetEntries) return false;
            token.SheetEntries.Add(clean);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Sustituye la hoja entera, por ejemplo al editarla a mano. Las líneas que no valen se descartan.</summary>
    public void ReplaceSheet(string tokenId, IEnumerable<SheetEntry> entries)
    {
        var token = FindToken(tokenId) ?? throw new ArgumentException($"No existe la ficha «{tokenId}».", nameof(tokenId));
        var fresh = new List<SheetEntry>();
        foreach (var entry in entries)
        {
            if (CleanEntry(entry.Name, entry.Value) is not { } clean || fresh.Count >= MaxSheetEntries) continue;
            var index = fresh.FindIndex(e => string.Equals(e.Name, clean.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) fresh[index] = clean;
            else fresh.Add(clean);
        }
        if (fresh.SequenceEqual(token.SheetEntries)) return;
        token.SheetEntries.Clear();
        token.SheetEntries.AddRange(fresh);
        Changed?.Invoke();
    }

    private static SheetEntry? CleanEntry(string? name, string? value)
    {
        var cleanName = Clean(name, MaxSheetNameLength);
        var cleanValue = Clean(value, MaxSheetValueLength);
        return cleanName.Length == 0 || cleanValue.Length == 0 ? null : new SheetEntry(cleanName, cleanValue);
    }

    private static string Clean(string? text, int max)
    {
        var clean = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > max ? clean[..max] : clean;
    }

    /// <summary>La mesa tal como está ahora, lista para guardarse y volver a cargarse.</summary>
    public SceneSpec ToSpec()
    {
        var features = new List<FeatureSpec>();
        var unexplored = new List<CellRect>();
        for (var y = 0; y < Height; y++)
        {
            // Tramos horizontales de casillas iguales, para que el archivo no crezca casilla a casilla.
            for (var x = 0; x < Width;)
            {
                var terrain = _terrain[x, y];
                var end = x;
                while (end + 1 < Width && _terrain[end + 1, y] == terrain) end++;
                if (terrain != Terrain.Floor) features.Add(new FeatureSpec(terrain, x, y, end - x + 1));
                x = end + 1;
            }
            for (var x = 0; x < Width;)
            {
                if (!_unexplored[x, y])
                {
                    x++;
                    continue;
                }
                var end = x;
                while (end + 1 < Width && _unexplored[end + 1, y]) end++;
                unexplored.Add(new CellRect(x, y, end - x + 1));
                x = end + 1;
            }
        }
        var tokens = _order.Select(t => new TokenSpec(t.Id, t.Name, t.Kind, t.X, t.Y, t.Hp, t.MaxHp, t.Color, t.Icon, t.Role, t.Seat,
            t.SheetEntries.Count > 0 ? t.SheetEntries.ToList() : null)).ToList();
        var combat = Combat is null ? null : new CombatSpec(Combat.Round, Combat.Turn, Combat.Order);
        return new SceneSpec(SceneId, SceneName, Width, Height, CellFeet, Terrain.Floor, features, tokens, unexplored, Exits, combat);
    }

    public ExitSpec? ExitAt(int x, int y) =>
        Exits.FirstOrDefault(e => x >= e.X && y >= e.Y && x < e.X + e.W && y < e.Y + e.H);

    /// <summary>
    /// Las casillas libres y pisables más cercanas a un punto, hasta <paramref name="count"/>.
    /// Sirve para colocar al grupo cuando llega a un lugar.
    /// </summary>
    public IReadOnlyList<(int X, int Y)> FreeCellsNear(int x, int y, int count)
    {
        var found = new List<(int, int)>();
        if (!InBounds(x, y) || count <= 0) return found;
        var seen = new bool[Width, Height];
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((x, y));
        seen[x, y] = true;
        while (queue.Count > 0 && found.Count < count)
        {
            var cell = queue.Dequeue();
            var walkable = IsWalkable(_terrain[cell.X, cell.Y]);
            if (walkable && TokenAt(cell.X, cell.Y) is null) found.Add(cell);
            // Se parte de la casilla pedida aunque no sea pisable, pero no se atraviesan paredes ni agua.
            if (!walkable && cell != (x, y)) continue;
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var (nx, ny) = (cell.X + dx, cell.Y + dy);
                if (!InBounds(nx, ny) || seen[nx, ny]) continue;
                seen[nx, ny] = true;
                queue.Enqueue((nx, ny));
            }
        }
        return found;
    }

    internal Token RemoveToken(string id)
    {
        var token = _tokens[id];
        _tokens.Remove(id);
        _order.Remove(token);
        return token;
    }

    internal void AddToken(Token token, int x, int y)
    {
        token.X = x;
        token.Y = y;
        _tokens.Add(token.Id, token);
        _order.Add(token);
        if (token.Kind == TokenKind.Hero) RevealAround(x, y, notify: false);
    }

    internal void NotifyChanged() => Changed?.Invoke();

    private bool RevealAround(int cx, int cy, bool notify)
    {
        var changed = false;
        Fill(cx - SightCells, cy - SightCells, SightCells * 2 + 1, SightCells * 2 + 1, (x, y) =>
        {
            changed |= _unexplored[x, y];
            _unexplored[x, y] = false;
        });
        if (changed && notify) Changed?.Invoke();
        return changed;
    }

    private void Fill(int x0, int y0, int w, int h, Action<int, int> set)
    {
        for (var x = Math.Max(x0, 0); x < Math.Min(x0 + w, Width); x++)
        for (var y = Math.Max(y0, 0); y < Math.Min(y0 + h, Height); y++)
            set(x, y);
    }

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public Terrain TerrainAt(int x, int y) => _terrain[x, y];

    public bool IsUnexplored(int x, int y) => _unexplored[x, y];

    public static bool IsWalkable(Terrain terrain) => terrain is Terrain.Floor or Terrain.Bridge;

    public Token? FindToken(string id) => _tokens.GetValueOrDefault(id);

    public Token? TokenAt(int x, int y) => _order.LastOrDefault(t => t.X == x && t.Y == y);

    /// <summary>Comprueba un movimiento sin aplicarlo. La distancia es en línea recta, en pies.</summary>
    public MoveResult CheckMove(string tokenId, int x, int y)
    {
        var token = FindToken(tokenId);
        if (token is null) return new MoveResult(MoveError.UnknownToken, 0);
        if (!InBounds(x, y)) return new MoveResult(MoveError.OutOfBounds, 0);
        var feet = GridDistance.Feet(token.X, token.Y, x, y, CellFeet);
        if (!IsWalkable(_terrain[x, y])) return new MoveResult(MoveError.Blocked, feet);
        var other = TokenAt(x, y);
        if (other is not null && other != token) return new MoveResult(MoveError.Occupied, feet);
        return new MoveResult(MoveError.None, feet);
    }

    public MoveResult MoveToken(string tokenId, int x, int y)
    {
        var result = CheckMove(tokenId, x, y);
        if (!result.Ok) return result;
        var token = _tokens[tokenId];
        if (token.X == x && token.Y == y) return result;
        token.X = x;
        token.Y = y;
        if (token.Kind == TokenKind.Hero) RevealAround(x, y, notify: false);
        Changed?.Invoke();
        return result;
    }

    /// <summary>Cambia los puntos de vida, sin pasar de 0 ni del máximo. Devuelve el valor final.</summary>
    public int SetHp(string tokenId, int hp)
    {
        var token = FindToken(tokenId) ?? throw new ArgumentException($"No existe la ficha «{tokenId}».", nameof(tokenId));
        var clamped = Math.Clamp(hp, 0, token.MaxHp);
        if (clamped != token.Hp)
        {
            token.Hp = clamped;
            Changed?.Invoke();
        }
        return clamped;
    }

    public void Reveal(int x0, int y0, int w, int h)
    {
        var changed = false;
        Fill(x0, y0, w, h, (x, y) =>
        {
            changed |= _unexplored[x, y];
            _unexplored[x, y] = false;
        });
        if (changed) Changed?.Invoke();
    }
}

public static class GridDistance
{
    /// <summary>
    /// Distancia sobre la rejilla contando las diagonales como en Pathfinder y D&amp;D 3.5:
    /// la primera cuesta una casilla, la segunda dos, y así alternando.
    /// </summary>
    public static int Feet(int x1, int y1, int x2, int y2, int cellFeet = 5)
    {
        var dx = Math.Abs(x2 - x1);
        var dy = Math.Abs(y2 - y1);
        var diagonals = Math.Min(dx, dy);
        var straights = Math.Max(dx, dy) - diagonals;
        return (straights + diagonals + diagonals / 2) * cellFeet;
    }
}
