using System.Text.Json;

namespace RoleDMFable.Core;

public sealed record LogEntry(string Who, string Text);

public sealed record CampaignSave(int Version, string Name, string CurrentScene, IReadOnlyList<SceneSpec> Scenes, IReadOnlyList<LogEntry>? Log = null);

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
    public IEnumerable<TableState> Scenes => _sceneOrder.Select(id => _scenes[id]);

    /// <summary>Algo cambió en la partida (una ficha, la niebla, el lugar o el registro).</summary>
    public event Action? Changed;

    public Campaign(string name, IEnumerable<SceneSpec> scenes, string startScene, IEnumerable<LogEntry>? log = null)
    {
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
        var from = Current;
        var token = from.FindToken(tokenId);
        if (token is null || token.Kind != TokenKind.Hero) return null;
        var exit = from.ExitAt(token.X, token.Y);
        if (exit is null) return null;

        var to = _scenes[exit.To];
        var party = from.Tokens.Where(t => t.Kind == TokenKind.Hero).ToList();
        // Primero quien cruza, para que se quede con la casilla de llegada.
        party.Remove(token);
        party.Insert(0, token);

        var cells = to.FreeCellsNear(exit.ToX, exit.ToY, party.Count);
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
        return new TravelResult(from.SceneName, to.SceneName, exit.Label, party.Select(t => t.Name).ToList());
    }

    public string ToJson() => JsonSerializer.Serialize(
        new CampaignSave(SaveVersion, Name, Current.SceneId, Scenes.Select(s => s.ToSpec()).ToList(), _log.ToList()),
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
        return new Campaign(save.Name, save.Scenes, save.CurrentScene ?? "", save.Log);
    }
}
