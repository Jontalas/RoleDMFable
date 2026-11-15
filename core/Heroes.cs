using System.Text.Json;

namespace RoleDMFable.Core;

/// <summary>
/// Un personaje guardado fuera de cualquier partida: puede entrar en varias aventuras
/// y conserva lo que va ganando (hoja, puntos de vida máximos).
/// </summary>
public sealed record SavedHero(
    string Id,
    string Name,
    string Role = "",
    string Icon = "",
    string Color = "#3f6fbf",
    int MaxHp = 10,
    IReadOnlyList<SheetEntry>? Sheet = null,
    string LastAdventure = "")
{
    public static SavedHero FromToken(Token token, string adventure) =>
        new(token.Id, token.Name, token.Role, token.Icon, token.Color, token.MaxHp, token.Sheet.ToList(), adventure);
}

/// <summary>Los personajes guardados en este PC, un archivo por personaje.</summary>
public sealed class HeroStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };

    private readonly string _folder;
    // Lo último que se escribió de cada uno, para no reescribir el disco si nada ha cambiado.
    private readonly Dictionary<string, string> _written = new(StringComparer.Ordinal);

    public HeroStore(string folder) => _folder = folder;

    /// <summary>Todos los personajes guardados, por orden de nombre. Los archivos ilegibles se saltan.</summary>
    public IReadOnlyList<SavedHero> LoadAll()
    {
        var heroes = new List<SavedHero>();
        if (!Directory.Exists(_folder)) return heroes;
        foreach (var file in Directory.GetFiles(_folder, "*.json"))
        {
            try
            {
                var text = File.ReadAllText(file);
                var hero = JsonSerializer.Deserialize<SavedHero>(text, Json);
                if (hero is null || string.IsNullOrWhiteSpace(hero.Id) || string.IsNullOrWhiteSpace(hero.Name)) continue;
                heroes.Add(hero);
                _written[hero.Id] = text;
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                // Un archivo dañado no impide usar los demás.
            }
        }
        return heroes.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool Exists(string id) => File.Exists(PathFor(id));

    /// <summary>Guarda el personaje. Devuelve false si ya estaba guardado tal cual.</summary>
    public bool Save(SavedHero hero)
    {
        var text = JsonSerializer.Serialize(hero, Json);
        if (_written.TryGetValue(hero.Id, out var before) && before == text) return false;
        Directory.CreateDirectory(_folder);
        var target = PathFor(hero.Id);
        var temp = target + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, target, overwrite: true);
        _written[hero.Id] = text;
        return true;
    }

    public void Delete(string id)
    {
        _written.Remove(id);
        var path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Guarda cómo están ahora los héroes ya creados de una partida propia, para que lo ganado
    /// en esta aventura les acompañe a la siguiente. Devuelve cuántos han cambiado.
    /// </summary>
    public int SaveFrom(Campaign game)
    {
        if (!CampaignSetup.IsOwnAdventure(game)) return 0;
        var changed = 0;
        foreach (var token in game.Scenes.SelectMany(s => s.Tokens))
            if (token.Kind == TokenKind.Hero && token.Created && Save(SavedHero.FromToken(token, game.Name)))
                changed++;
        return changed;
    }

    private string PathFor(string id) => Path.Combine(_folder, id + ".json");
}
