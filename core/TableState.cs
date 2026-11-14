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
            _tokens.Add(token.Id, token);
            _order.Add(token);
        }
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
