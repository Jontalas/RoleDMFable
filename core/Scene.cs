using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoleDMFable.Core;

[JsonConverter(typeof(JsonStringEnumConverter<Terrain>))]
public enum Terrain
{
    Floor,
    Wall,
    Water,
    Bridge,
    Rock,
}

[JsonConverter(typeof(JsonStringEnumConverter<TokenKind>))]
public enum TokenKind
{
    Hero,
    Enemy,
    Npc,
}

/// <summary>Un rectángulo de casillas, para paredes, agua o zonas sin explorar.</summary>
public sealed record CellRect(int X, int Y, int W = 1, int H = 1);

public sealed record FeatureSpec(Terrain Type, int X, int Y, int W = 1, int H = 1);

/// <summary>Casillas que llevan a otro lugar. El grupo aparece junto a (ToX, ToY) del destino.</summary>
public sealed record ExitSpec(int X, int Y, string To, int ToX, int ToY, string Label = "", int W = 1, int H = 1);

/// <summary>Una línea de la hoja de personaje: «Fuerza» → «16», «Espada larga» → «+6, 1d8+3».</summary>
public sealed record SheetEntry(string Name, string Value);

/// <summary>Un participante del combate con su iniciativa ya tirada.</summary>
public sealed record CombatantSpec(string Token, int Initiative);

/// <summary>Un combate en marcha: ronda, a quién le toca y el orden de turnos.</summary>
public sealed record CombatSpec(int Round, int Turn, IReadOnlyList<CombatantSpec> Order);

public sealed record TokenSpec(
    string Id,
    string Name,
    TokenKind Kind,
    int X,
    int Y,
    int Hp,
    int MaxHp,
    string Color = "#888888",
    string Icon = "",
    string Role = "",
    int? Seat = null,
    IReadOnlyList<SheetEntry>? Sheet = null);

/// <summary>
/// Un lugar descrito con datos, no con un dibujo. El máster describe la escena así
/// y el juego monta el mapa, de modo que lo narrado y lo que se ve coinciden siempre.
/// </summary>
public sealed record SceneSpec(
    string Id,
    string Name,
    int Width,
    int Height,
    int CellFeet = 5,
    Terrain Base = Terrain.Floor,
    IReadOnlyList<FeatureSpec>? Features = null,
    IReadOnlyList<TokenSpec>? Tokens = null,
    IReadOnlyList<CellRect>? Unexplored = null,
    IReadOnlyList<ExitSpec>? Exits = null,
    CombatSpec? Combat = null)
{
    public const int MaxSide = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SceneSpec FromJson(string json)
    {
        var spec = JsonSerializer.Deserialize<SceneSpec>(json, JsonOptions)
                   ?? throw new FormatException("La escena está vacía.");
        spec.Validate();
        return spec;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new FormatException("La escena necesita un id.");
        if (Width < 1 || Height < 1 || Width > MaxSide || Height > MaxSide)
            throw new FormatException($"El tamaño del mapa debe estar entre 1 y {MaxSide} casillas por lado.");
        if (CellFeet < 1) throw new FormatException("Cada casilla debe medir al menos 1 pie.");

        foreach (var exit in Exits ?? Array.Empty<ExitSpec>())
        {
            if (string.IsNullOrWhiteSpace(exit.To)) throw new FormatException("Hay una salida sin destino.");
            if (exit.W < 1 || exit.H < 1 || exit.X < 0 || exit.Y < 0 || exit.X + exit.W > Width || exit.Y + exit.H > Height)
                throw new FormatException($"La salida hacia «{exit.To}» está fuera del mapa.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Tokens ?? Array.Empty<TokenSpec>())
        {
            if (string.IsNullOrWhiteSpace(token.Id)) throw new FormatException("Hay una ficha sin id.");
            if (!ids.Add(token.Id)) throw new FormatException($"El id de ficha «{token.Id}» está repetido.");
            if (token.X < 0 || token.Y < 0 || token.X >= Width || token.Y >= Height)
                throw new FormatException($"La ficha «{token.Id}» está fuera del mapa.");
        }
    }
}
