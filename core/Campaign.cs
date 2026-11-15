using System.Text.Json;

namespace RoleDMFable.Core;

public sealed record LogEntry(string Who, string Text);

public sealed record CampaignSave(int Version, string Name, string CurrentScene, IReadOnlyList<SceneSpec> Scenes, IReadOnlyList<LogEntry>? Log = null, StorySave? Story = null);

public sealed record TravelResult(string FromScene, string ToScene, string Label, IReadOnlyList<string> Travellers);

/// <summary>
/// Una partida: varios lugares enlazados por salidas, el lugar donde está el grupo
/// y el registro de lo ocurrido. Se guarda y se carga entera como JSON.
/// </summary>
public sealed class Campaign
{
    public const int SaveVersion = 1;
    public const int MaxLogEntries = 300;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Dictionary<string, TableState> _scenes = new(StringComparer.Ordinal);
    private readonly List<string> _sceneOrder = new();
    private readonly List<LogEntry> _log = new();

    public string Name { get; }
    public TableState Current { get; private set; }
    public IReadOnlyList<LogEntry> Log => _log;

    /// <summary>El hilo de la historia: escenas, objetivos, cuentas atrás y oráculo.</summary>
    public StoryState Story { get; }
    public IEnumerable<TableState> Scenes => _sceneOrder.Select(id => _scenes[id]);

    /// <summary>Algo cambió en la partida (una ficha, la niebla, el lugar o el registro).</summary>
    public event Action? Changed;

    public Campaign(string name, IEnumerable<SceneSpec> scenes, string startScene, IEnumerable<LogEntry>? log = null, StorySave? story = null)
    {
        Story = new StoryState(story);
        Story.Changed += () => Changed?.Invoke();
        Name = string.IsNullOrWhiteSpace(name) ? "Partida" : name;
        foreach (var spec in scenes)
        {
            var table = new TableState(spec);
            if (!_scenes.TryAdd(table.SceneId, table))
                throw new FormatException($"El lugar «{table.SceneId}» está repetido.");
            _sceneOrder.Add(table.SceneId);
            table.Changed += () => Changed?.Invoke();
        }
        if (_scenes.Count == 0) throw new FormatException("La partida no tiene ningún lugar.");
        if (!_scenes.TryGetValue(startScene, out var current))
            throw new FormatException($"El lugar inicial «{startScene}» no existe.");
        Current = current;

        foreach (var table in _scenes.Values)
        foreach (var exit in table.Exits)
        {
            if (!_scenes.TryGetValue(exit.To, out var target))
                throw new FormatException($"«{table.SceneName}» tiene una salida hacia «{exit.To}», que no existe.");
            if (!target.InBounds(exit.ToX, exit.ToY))
                throw new FormatException($"La salida de «{table.SceneName}» llega fuera del mapa de «{target.SceneName}».");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in _scenes.Values.SelectMany(t => t.Tokens))
            if (token.Kind == TokenKind.Hero && !ids.Add(token.Id))
                throw new FormatException($"El héroe «{token.Id}» aparece en dos lugares.");

        if (log is not null) _log.AddRange(log.TakeLast(MaxLogEntries));
    }

    public void AddLog(string who, string text)
    {
        _log.Add(new LogEntry(who, text));
        if (_log.Count > MaxLogEntries) _log.RemoveRange(0, _log.Count - MaxLogEntries);
        Changed?.Invoke();
    }

    /// <summary>
    /// Si la ficha es un héroe y está sobre una salida, todo el grupo pasa al otro lugar.
    /// Devuelve null si no hay salida ahí o si en el destino no cabe el grupo.
    /// </summary>
    public TravelResult? TryTravel(string tokenId)
    {
        var token = Current.FindToken(tokenId);
        if (token is null || token.Kind != TokenKind.Hero) return null;
        var exit = Current.ExitAt(token.X, token.Y);
        return exit is null ? null : MoveParty(exit.To, exit.ToX, exit.ToY, exit.Label, token);
    }

    public const int MaxScenes = 80;

    public TableState? FindScene(string idOrName)
    {
        if (_scenes.TryGetValue(idOrName, out var byId)) return byId;
        var matches = _scenes.Values.Where(s => string.Equals(s.SceneName, idOrName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.SceneId, idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Un id libre para un lugar nuevo, sacado de su nombre: «Cueva del oso» → cueva_del_oso.</summary>
    public string NewSceneId(string name)
    {
        var slug = new string(RulesText.Fold(name).Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        while (slug.Contains("__")) slug = slug.Replace("__", "_");
        if (slug.Length == 0) slug = "lugar";
        if (slug.Length > 40) slug = slug[..40].Trim('_');
        var id = slug;
        for (var n = 2; _scenes.ContainsKey(id); n++) id = $"{slug}_{n}";
        return id;
    }

    /// <summary>
    /// Añade un lugar nuevo a la partida. Lanza <see cref="FormatException"/> si el id está repetido,
    /// alguna salida lleva a un lugar que no existe o ya no caben más lugares.
    /// </summary>
    public TableState AddScene(SceneSpec spec)
    {
        if (_scenes.Count >= MaxScenes) throw new FormatException($"La partida ya tiene {MaxScenes} lugares, que es el máximo.");
        if (_scenes.ContainsKey(spec.Id)) throw new FormatException($"El lugar «{spec.Id}» está repetido.");
        var table = new TableState(spec);
        foreach (var exit in table.Exits)
        {
            if (!_scenes.TryGetValue(exit.To, out var target))
                throw new FormatException($"«{table.SceneName}» tiene una salida hacia «{exit.To}», que no existe.");
            if (!target.InBounds(exit.ToX, exit.ToY))
                throw new FormatException($"La salida de «{table.SceneName}» llega fuera del mapa de «{target.SceneName}».");
        }
        if (table.Tokens.Any(t => t.Kind == TokenKind.Hero && _scenes.Values.Any(s => s.FindToken(t.Id) is not null)))
            throw new FormatException("Un héroe no puede estar en dos lugares.");
        _scenes.Add(table.SceneId, table);
        _sceneOrder.Add(table.SceneId);
        table.Changed += () => Changed?.Invoke();
        Changed?.Invoke();
        return table;
    }

    /// <summary>Abre una salida en un lugar hacia otro que ya existe.</summary>
    public void AddExit(string sceneId, ExitSpec exit)
    {
        var from = _scenes[sceneId];
        if (!_scenes.TryGetValue(exit.To, out var target)) throw new FormatException($"El lugar «{exit.To}» no existe.");
        if (!from.InBounds(exit.X, exit.Y) || !from.InBounds(exit.X + exit.W - 1, exit.Y + exit.H - 1))
            throw new FormatException($"La salida está fuera del mapa de «{from.SceneName}».");
        if (!target.InBounds(exit.ToX, exit.ToY)) throw new FormatException($"La salida llega fuera del mapa de «{target.SceneName}».");
        from.AddExit(exit);
    }

    /// <summary>
    /// Lleva a todos los héroes del lugar actual a otro, junto a la casilla indicada.
    /// Devuelve null si el lugar no existe, es el actual o el grupo no cabe.
    /// </summary>
    public TravelResult? MoveParty(string sceneId, int x, int y, string label = "", Token? first = null)
    {
        var from = Current;
        if (!_scenes.TryGetValue(sceneId, out var to) || to == from) return null;
        var party = from.Tokens.Where(t => t.Kind == TokenKind.Hero).ToList();
        if (first is not null && party.Remove(first))
        {
            // Primero quien cruza, para que se quede con la casilla de llegada.
            party.Insert(0, first);
        }

        var cells = to.FreeCellsNear(x, y, party.Count);
        if (cells.Count < party.Count) return null;

        // El grupo se va: el combate que hubiera en este lugar se da por terminado.
        from.EndCombat();
        for (var i = 0; i < party.Count; i++)
        {
            from.RemoveToken(party[i].Id);
            to.AddToken(party[i], cells[i].X, cells[i].Y);
        }
        Current = to;
        from.NotifyChanged();
        to.NotifyChanged();
        return new TravelResult(from.SceneName, to.SceneName, label, party.Select(t => t.Name).ToList());
    }

    /// <summary>
    /// Trae al lugar actual una ficha que se quedó en otro (un acompañante, un perseguidor).
    /// Devuelve null si no existe fuera de aquí, hay varias con ese nombre, es un héroe o no hay sitio.
    /// </summary>
    public Token? BringToken(string idOrName, int x, int y)
    {
        var found = _scenes.Values.Where(s => s != Current)
            .SelectMany(s => s.Tokens.Select(t => (Scene: s, Token: t)))
            .Where(f => f.Token.Kind != TokenKind.Hero
                && (string.Equals(f.Token.Id, idOrName, StringComparison.OrdinalIgnoreCase) || string.Equals(f.Token.Name, idOrName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (found.Count != 1 || Current.FindToken(found[0].Token.Id) is not null) return null;
        var cells = Current.FreeCellsNear(x, y, 1);
        if (cells.Count == 0) return null;
        var (scene, token) = found[0];
        scene.DeleteToken(token.Id);
        Current.AddToken(token, cells[0].X, cells[0].Y);
        Current.NotifyChanged();
        return token;
    }

    public string ToJson() => JsonSerializer.Serialize(
        new CampaignSave(SaveVersion, Name, Current.SceneId, Scenes.Select(s => s.ToSpec()).ToList(), _log.ToList(), Story.ToSave()),
        JsonOptions);

    public static Campaign FromJson(string json)
    {
        CampaignSave? save;
        try
        {
            save = JsonSerializer.Deserialize<CampaignSave>(json, JsonOptions);
        }
        catch (JsonException e)
        {
            throw new FormatException("El archivo de la partida está dañado.", e);
        }
        if (save is null || save.Scenes is null) throw new FormatException("El archivo de la partida está vacío.");
        if (save.Version > SaveVersion)
            throw new FormatException("La partida se guardó con una versión más nueva del juego.");
        return new Campaign(save.Name, save.Scenes, save.CurrentScene ?? "", save.Log, save.Story);
    }
}
