namespace RoleDMFable.Core;

/// <summary>Lo que se enseña de una partida guardada sin tener que abrirla.</summary>
public sealed record SavedGameInfo(string Id, string Name, string Place, IReadOnlyList<string> Heroes, DateTime LastPlayed, bool Own);

/// <summary>Las partidas guardadas en este PC, un archivo por partida.</summary>
public sealed class CampaignStore
{
    public const int MaxGames = 200;

    private readonly string _folder;

    public CampaignStore(string folder) => _folder = folder;

    /// <summary>Todas las partidas, la última jugada primero. Los archivos ilegibles se saltan.</summary>
    public IReadOnlyList<SavedGameInfo> List()
    {
        var games = new List<SavedGameInfo>();
        if (!Directory.Exists(_folder)) return games;
        foreach (var file in Directory.GetFiles(_folder, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!IsValidId(id)) continue;
            try
            {
                var game = Campaign.FromJson(File.ReadAllText(file));
                games.Add(new SavedGameInfo(
                    id,
                    game.Name,
                    game.Current.SceneName,
                    game.Scenes.SelectMany(s => s.Tokens).Where(t => t.Kind == TokenKind.Hero).Select(t => t.Name).ToList(),
                    File.GetLastWriteTime(file),
                    CampaignSetup.IsOwnAdventure(game)));
            }
            catch (Exception e) when (e is FormatException or IOException)
            {
                // Una partida dañada no impide ver las demás.
            }
        }
        return games.OrderByDescending(g => g.LastPlayed).ThenBy(g => g.Id, StringComparer.Ordinal).ToList();
    }

    public bool Exists(string id) => IsValidId(id) && File.Exists(PathFor(id));

    /// <summary>
    /// Abre una partida. Lanza <see cref="FormatException"/> si no existe o está dañada;
    /// una dañada se aparta como «.roto» para poder recuperarla a mano.
    /// </summary>
    public Campaign Load(string id)
    {
        if (!Exists(id)) throw new FormatException("Esa partida ya no existe.");
        var path = PathFor(id);
        try
        {
            return Campaign.FromJson(File.ReadAllText(path));
        }
        catch (FormatException)
        {
            File.Move(path, path + ".roto", overwrite: true);
            throw;
        }
        catch (IOException e)
        {
            throw new FormatException($"No se pudo leer la partida: {e.Message}", e);
        }
    }

    /// <summary>Guarda la partida con ese id, sin dejar nunca el archivo a medias.</summary>
    public void Save(string id, Campaign game)
    {
        if (!IsValidId(id)) throw new ArgumentException("El id de la partida no vale.", nameof(id));
        Directory.CreateDirectory(_folder);
        var target = PathFor(id);
        var temp = target + ".tmp";
        File.WriteAllText(temp, game.ToJson());
        File.Move(temp, target, overwrite: true);
    }

    public void Delete(string id)
    {
        if (Exists(id)) File.Delete(PathFor(id));
    }

    /// <summary>Un id libre para una partida nueva, sacado de su nombre: «La torre hundida» → la_torre_hundida.</summary>
    public string NewId(string name)
    {
        var slug = new string(RulesText.Fold(name).Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_').ToArray()).Trim('_');
        while (slug.Contains("__")) slug = slug.Replace("__", "_");
        if (slug.Length == 0) slug = "partida";
        if (slug.Length > 40) slug = slug[..40].Trim('_');
        var id = slug;
        for (var n = 2; File.Exists(PathFor(id)); n++) id = $"{slug}_{n}";
        return id;
    }

    /// <summary>
    /// Pasa a esta carpeta la partida única de las versiones anteriores, si la hay.
    /// Devuelve su id nuevo, o null si no había nada que traer o estaba dañada (entonces se deja donde está).
    /// </summary>
    public string? ImportLegacy(string legacyFile)
    {
        if (!File.Exists(legacyFile)) return null;
        try
        {
            var game = Campaign.FromJson(File.ReadAllText(legacyFile));
            var id = NewId(game.Name);
            Save(id, game);
            File.Delete(legacyFile);
            return id;
        }
        catch (Exception e) when (e is FormatException or IOException)
        {
            return null;
        }
    }

    private static bool IsValidId(string id) =>
        id.Length is > 0 and <= 60 && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private string PathFor(string id) => Path.Combine(_folder, id + ".json");
}
