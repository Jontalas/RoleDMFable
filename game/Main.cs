using Godot;
using System.Text.Json;
using RoleDMFable.Core;
using RoleDMFable.Master;
using RoleDMFable.Rules;

namespace RoleDMFable;

/// <summary>
/// Pantalla de la mesa. El mapa ocupa siempre el centro; a la izquierda los asientos
/// de este PC, debajo los dados y a la derecha el registro (donde hablará el máster).
/// </summary>
public partial class Main : Control
{
    private const string ScenesFolder = "res://data/scenes";
    private const string StartScene = "sala_del_puente";
    // La partida única de las versiones anteriores: se pasa a la carpeta de partidas al arrancar.
    private const string LegacySavePath = "user://partida.json";
    private const string GamesFolder = "user://partidas";
    private const double SaveDelaySeconds = 0.5;
    private const string SettingsPath = "user://ajustes.json";
    private const string RulesFolder = "user://reglamentos";
    private const string HeroesFolder = "user://personajes";

    private Campaign _game = null!;
    private TableState _table => _game.Current;
    private Label _sceneLabel = null!;
    private Label _gameLabel = null!;
    private bool _ephemeral;
    private bool _refreshQueued;
    private double _saveCountdown = -1;
    private MapView _map = null!;
    private DiceTray _dice = null!;
    private VBoxContainer _seats = null!;
    private RichTextLabel _log = null!;
    private LineEdit _chat = null!;
    private int _activeSeat = 1;

    // El máster IA: la clave y el modelo se guardan solo en este PC, fuera de la partida.
    private string _apiKey = "";
    private string _modelId = ClaudeMaster.DefaultModel;
    private string? _apiUrl;
    private MasterToolbox _tools = null!;
    private CancellationTokenSource? _masterTurn;
    private bool _masterBusy;
    private bool _noKeyHintShown;
    private double _sessionCost;
    private Label _masterStatus = null!;
    private Label _masterTitle = null!;
    private Button _send = null!;
    private PanelContainer _turnBar = null!;
    private HBoxContainer _turnRow = null!;
    private string? _lastTurnId;

    // Reglamentos: se leen una vez y se guardan troceados en este PC.
    // Personajes guardados: viven fuera de la partida para poder entrar en varias aventuras.
    private HeroStore _heroes = null!;
    private CampaignStore _games = null!;
    private string _gameId = "";

    private readonly RulesLibrary _rules = new();
    private RulesStore? _rulesStore;
    private VBoxContainer? _rulesList;
    private Label? _rulesProgress;
    private string? _importing;
    private volatile int _importPercent;

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        var capture = args.FirstOrDefault(a => a.StartsWith("--captura="));
        // Las capturas de revisión no leen ni pisan la partida guardada del jugador.
        _ephemeral = capture is not null;

        if (_ephemeral)
        {
            // Solo en capturas de revisión: permite apuntar a un servidor de imitación.
            _apiUrl = args.FirstOrDefault(a => a.StartsWith("--api="))?["--api=".Length..];
            if (_apiUrl is not null) _apiKey = "clave-de-prueba";
        }
        else
        {
            LoadSettings();
        }

        // Las capturas de revisión usan una carpeta aparte para no tocar los personajes del jugador.
        _heroes = new HeroStore(_ephemeral
            ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "roledmfable-captura-personajes")
            : ProjectSettings.GlobalizePath(HeroesFolder));
        var captureGames = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "roledmfable-captura-partidas");
        if (_ephemeral && System.IO.Directory.Exists(captureGames)) System.IO.Directory.Delete(captureGames, recursive: true);
        _games = new CampaignStore(_ephemeral ? captureGames : ProjectSettings.GlobalizePath(GamesFolder));

        var rulesProblems = new List<string>();
        if (!_ephemeral)
        {
            _rulesStore = new RulesStore(ProjectSettings.GlobalizePath(RulesFolder));
            rulesProblems.AddRange(_rulesStore.LoadInto(_rules));
        }
        _rules.Changed += RefreshRulesList;
        GetWindow().FilesDropped += files => _ = ImportRules(files);

        string? notice = null;
        IReadOnlyList<string> grown = Array.Empty<string>();
        var (loaded, loadedId) = _ephemeral ? (null, null) : TryLoadSave(out notice, out grown);
        BuildLayout();
        StartGame(loaded ?? NewCampaign(), isNew: loaded is null, loadedId);
        SayGrown(grown);
        if (notice is not null) Say("Mesa", notice);
        foreach (var problem in rulesProblems) Say("Mesa", problem);
        RefreshMasterStatus();

        var sampleRules = args.FirstOrDefault(a => a.StartsWith("--reglas="))?["--reglas=".Length..];
        if (capture is not null)
            _ = CaptureAndQuit(sampleRules, capture["--captura=".Length..], args.Contains("--arrastrar"), args.Contains("--viajar"),
                args.Contains("--ajustes"), args.FirstOrDefault(a => a.StartsWith("--decir="))?["--decir=".Length..]);
    }

    public override void _Process(double delta)
    {
        if (_importing is not null && _rulesProgress is not null && IsInstanceValid(_rulesProgress))
            _rulesProgress.Text = $"Leyendo «{_importing}»… {_importPercent} %";
        if (_saveCountdown < 0) return;
        _saveCountdown -= delta;
        if (_saveCountdown < 0) SaveNow();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest && _saveCountdown >= 0) SaveNow();
    }

    // ---------- Partida: nueva, cargar y guardar ----------

    private static Campaign NewCampaign()
    {
        var scenes = DirAccess.GetFilesAt(ScenesFolder)
            .Where(file => file.EndsWith(".json"))
            .OrderBy(file => file)
            .Select(file => SceneSpec.FromJson(Godot.FileAccess.GetFileAsString($"{ScenesFolder}/{file}")));
        return new Campaign("Partida de prueba", scenes, StartScene);
    }

    /// <summary>Abre la última partida jugada. Si está dañada prueba con la anterior.</summary>
    private (Campaign?, string?) TryLoadSave(out string? notice, out IReadOnlyList<string> grown)
    {
        notice = null;
        grown = Array.Empty<string>();
        try
        {
            _games.ImportLegacy(ProjectSettings.GlobalizePath(LegacySavePath));
            foreach (var info in _games.List())
            {
                try
                {
                    var game = _games.Load(info.Id);
                    grown = _heroes.ApplyTo(game);
                    return (game, info.Id);
                }
                catch (FormatException e)
                {
                    // No se borra: el almacén la aparta por si se puede recuperar a mano.
                    notice = $"No se pudo cargar la partida «{info.Name}» ({e.Message}). Queda apartada en la carpeta de partidas con el final «.roto».";
                }
            }
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            notice = $"No se pudieron leer las partidas guardadas: {e.Message}";
        }
        return (null, null);
    }

    private void SayGrown(IReadOnlyList<string> grown)
    {
        if (grown.Count > 0)
            Say("Mesa", $"{string.Join(", ", grown)}: su ficha se ha puesto al día con lo que ganó en otra aventura.");
    }

    /// <summary>Deja la partida actual guardada y abre otra de las guardadas.</summary>
    private bool OpenSavedGame(string id, out string problem)
    {
        problem = "";
        try
        {
            if (_saveCountdown >= 0) SaveNow();
            var game = _games.Load(id);
            var grown = _heroes.ApplyTo(game);
            StartGame(game, isNew: false, id);
            SayGrown(grown);
            return true;
        }
        catch (Exception e) when (e is FormatException or System.IO.IOException or UnauthorizedAccessException)
        {
            problem = $"No se pudo abrir: {e.Message}";
            return false;
        }
    }

    private void StartGame(Campaign game, bool isNew, string? id = null)
    {
        if (_game is not null)
        {
            // La partida que se deja queda guardada: se puede volver a ella desde «Partidas».
            if (_saveCountdown >= 0) SaveNow();
            _game.Changed -= OnGameChanged;
        }
        _masterTurn?.Cancel();
        _gameId = id ?? _games.NewId(game.Name);
        _game = game;
        _game.Changed += OnGameChanged;
        _tools = new MasterToolbox(_game, new SystemRandomSource(), _rules);
        _tools.Announced += Say;
        _tools.Travelled += OnTravelled;
        _activeSeat = _game.Scenes.SelectMany(s => s.Tokens).Where(t => t.Seat is not null).Select(t => t.Seat!.Value).DefaultIfEmpty(1).Min();

        _log.Clear();
        foreach (var entry in _game.Log) AppendLog(entry.Who, entry.Text);
        ShowCurrentScene();

        if (isNew && _table.SceneId == CampaignSetup.StartSceneId)
        {
            // Partida propia: el máster abre la aventura creando el primer lugar.
            Say("Mesa", $"Empieza «{_game.Name}». La partida se guarda sola.");
            Say("Mesa", CampaignSetup.PendingHeroes(_game).Count > 0
                ? "Antes de la aventura se crean los personajes nuevos, uno a uno, con el máster y según el reglamento cargado. Cuando estén todos, el máster abrirá la historia."
                : "Todos los personajes vienen ya creados, así que el máster abre la historia.");
            if (HasMaster)
                _ = AskMaster("Mesa", CampaignSetup.StartRequest(_game));
            else
                Say("Mesa", "Para empezar, pulsa «Máster IA» arriba, pon tu clave y escribe «empezamos».");
        }
        else if (isNew)
        {
            Say("Mesa", $"Empieza la partida en {_table.SceneName}. Arrastra una figurita para moverla; si un héroe pisa una salida dorada, el grupo pasa al siguiente lugar.");
            Say("Mesa", HasMaster
                ? "La partida se guarda sola. Escribe abajo lo que hace tu personaje y el máster te responderá."
                : "La partida se guarda sola. Para que el máster responda, pulsa «Máster IA» arriba y pon tu clave.");
        }
        else
        {
            Say("Mesa", $"Partida retomada en {_table.SceneName}.");
        }
    }

    private void ShowCurrentScene()
    {
        _map.SetTable(_table);
        _sceneLabel.Text = _table.SceneName;
        _gameLabel.Text = _game.Name;
        RefreshSeats();
    }

    private void OnGameChanged()
    {
        if (!_ephemeral) _saveCountdown = SaveDelaySeconds;
        if (_refreshQueued) return;
        _refreshQueued = true;
        Callable.From(() =>
        {
            _refreshQueued = false;
            RefreshSeats();
        }).CallDeferred();
    }

    private void SaveNow()
    {
        _saveCountdown = -1;
        try
        {
            _games.Save(_gameId, _game);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            GD.PushError($"No se pudo guardar la partida: {e.Message}");
            return;
        }
        SaveHeroes();
    }

    /// <summary>Lo que los héroes han ganado en esta partida pasa a sus personajes guardados.</summary>
    private void SaveHeroes()
    {
        try
        {
            _heroes.SaveFrom(_game);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            GD.PushError($"No se pudieron guardar los personajes: {e.Message}");
        }
    }

    private void OnTokenMoved(string id, int feet)
    {
        Say(_table.FindToken(id)!.Name, $"se mueve {feet} pies.");
        var travel = _game.TryTravel(id);
        if (travel is not null)
        {
            OnTravelled(travel);
        }
        else if (_table.FindToken(id) is { Kind: TokenKind.Hero } hero && _table.ExitAt(hero.X, hero.Y) is not null)
        {
            Say("Mesa", "El grupo no cabe al otro lado de esa salida.");
        }
    }

    private void OnTravelled(TravelResult travel)
    {
        ShowCurrentScene();
        var way = travel.Label.Length > 0 ? $" por «{travel.Label}»" : "";
        Say("Mesa", $"El grupo sale de {travel.FromScene}{way} y llega a {travel.ToScene}.");
    }

    private static string WhenPlayed(DateTime when)
    {
        var days = (DateTime.Today - when.Date).Days;
        return days switch
        {
            <= 0 => $"hoy a las {when:HH:mm}",
            1 => $"ayer a las {when:HH:mm}",
            < 7 => $"hace {days} días",
            _ => when.ToString("d MMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("es-ES")),
        };
    }

    /// <summary>Las partidas guardadas: continuar otra, borrar las que sobran o empezar una nueva.</summary>
    private void OpenGames()
    {
        if (_saveCountdown >= 0) SaveNow();
        var dialog = new AcceptDialog
        {
            Title = "Partidas",
            OkButtonText = "Seguir jugando",
            MinSize = new Vector2I(820, 0),
        };
        dialog.AddButton("Nueva partida", true, "nueva");

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        dialog.AddChild(column);
        column.AddChild(DialogText("Cada partida se guarda sola. Al continuar otra, la actual queda tal como está y los personajes conservan lo que hayan ganado.", 600));
        var problem = DialogText("", 800);
        problem.AddThemeColorOverride("font_color", Ui.Red);

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 360), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(list);
        column.AddChild(scroll);
        column.AddChild(problem);

        IReadOnlyList<SavedGameInfo> games;
        try
        {
            games = _games.List();
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            games = Array.Empty<SavedGameInfo>();
            problem.Text = $"No se pudieron leer las partidas guardadas: {e.Message}";
        }
        if (games.Count == 0 && problem.Text.Length == 0) list.AddChild(DialogText("Todavía no hay ninguna partida guardada.", 700));

        foreach (var info in games)
        {
            var current = info.Id == _gameId;
            var card = Ui.Panel(current ? Ui.PaleGold : Ui.Cream, radius: 12, padding: 10, lift: 2);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            card.AddChild(row);
            var texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            texts.AddThemeConstantOverride("separation", 0);
            row.AddChild(texts);
            var title = Ui.Text(info.Name, 19, Ui.Ink, title: true);
            title.ClipText = true;
            texts.AddChild(title);
            var party = info.Heroes.Count > 0 ? string.Join(", ", info.Heroes) : "sin héroes";
            var details = Ui.Text($"{party} · en {info.Place} · {WhenPlayed(info.LastPlayed)}", 13, Ui.Ink, weight: 600);
            details.ClipText = true;
            texts.AddChild(details);

            if (current)
            {
                row.AddChild(Ui.Text("Jugando ahora", 14, Ui.Ink, weight: 800));
            }
            else
            {
                var open = Ui.Button("Continuar", Ui.Gold, 14);
                open.Pressed += () =>
                {
                    if (OpenSavedGame(info.Id, out var why)) dialog.Hide();
                    else problem.Text = why;
                };
                row.AddChild(open);
                var forget = Ui.Button("Borrar", Ui.Parchment, 13);
                forget.TooltipText = "Borra esta partida para siempre. Los personajes guardados no se borran.";
                forget.Pressed += () =>
                {
                    // Dos pulsaciones: borrar una partida no tiene vuelta atrás.
                    if (forget.Text != "¿Seguro?")
                    {
                        forget.Text = "¿Seguro?";
                        return;
                    }
                    try
                    {
                        _games.Delete(info.Id);
                        list.RemoveChild(card);
                        card.QueueFree();
                    }
                    catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
                    {
                        problem.Text = $"No se pudo borrar: {e.Message}";
                    }
                };
                row.AddChild(forget);
            }
            list.AddChild(card);
        }

        dialog.CustomAction += action =>
        {
            if (action != "nueva") return;
            dialog.Hide();
            AskNewGame();
        };
        dialog.VisibilityChanged += () =>
        {
            if (!dialog.Visible) dialog.QueueFree();
        };
        AddChild(dialog);
        dialog.PopupCentered();
    }

    private void AskNewGame(IReadOnlyList<HeroSetup>? prefill = null)
    {
        var dialog = new AcceptDialog
        {
            Title = "Nueva partida",
            OkButtonText = "Empezar la aventura",
            MinSize = new Vector2I(820, 0),
            DialogHideOnOk = false,
        };
        dialog.AddCancelButton("Seguir jugando");
        dialog.AddButton("Partida de ejemplo", true, "ejemplo");

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        dialog.AddChild(column);

        column.AddChild(DialogText("La partida actual queda guardada: puedes volver a ella cuando quieras desde «Partidas».", 600));
        column.AddChild(DialogText("Nombre de la aventura", 800));
        var name = Ui.Input("La torre hundida");
        column.AddChild(name);
        column.AddChild(DialogText("¿De qué queréis que vaya? (opcional)", 800));
        var premise = Ui.Input("Una frase basta: «buscamos a un mago desaparecido en una ciudad de canales»");
        column.AddChild(premise);

        // Personajes de aventuras anteriores: entran ya creados, con su ficha.
        var saved = _heroes.LoadAll();
        var picks = new List<(SavedHero Hero, CheckBox Joins, CheckBox Player)>();
        if (saved.Count > 0)
        {
            column.AddChild(DialogText("Personajes guardados", 800));
            column.AddChild(DialogText("Marca los que vuelven en esta aventura. Entran con su ficha tal como quedó.", 600));
            foreach (var hero in saved)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 10);
                var from = hero.LastAdventure.Length > 0 ? $" · viene de «{hero.LastAdventure}»" : "";
                var role = hero.Role.Length > 0 ? $" · {hero.Role}" : "";
                var joins = new CheckBox { Text = $"{hero.Name}{role} · PV {hero.MaxHp}{from}", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
                joins.AddThemeFontOverride("font", Ui.Body(800));
                var asPlayer = new CheckBox { Text = "Lo lleva un jugador", ButtonPressed = true };
                asPlayer.AddThemeFontOverride("font", Ui.Body(700));
                var forget = Ui.Button("Borrar", Ui.Parchment, 13);
                forget.TooltipText = "Borra este personaje guardado para siempre";
                forget.Pressed += () =>
                {
                    // Dos pulsaciones: borrar un personaje no tiene vuelta atrás.
                    if (forget.Text != "¿Seguro?")
                    {
                        forget.Text = "¿Seguro?";
                        return;
                    }
                    _heroes.Delete(hero.Id);
                    picks.RemoveAll(p => p.Hero.Id == hero.Id);
                    column.RemoveChild(row);
                    row.QueueFree();
                    dialog.ResetSize();
                };
                row.AddChild(joins);
                row.AddChild(asPlayer);
                row.AddChild(forget);
                column.AddChild(row);
                picks.Add((hero, joins, asPlayer));
            }
        }

        column.AddChild(DialogText(saved.Count > 0 ? "Héroes nuevos" : "Héroes", 800));
        column.AddChild(DialogText(
            "Aquí solo va lo básico. Antes de empezar, el máster crea cada personaje contigo según el reglamento cargado, y queda guardado para otras aventuras.", 600));
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 6);
        column.AddChild(rows);
        var add = Ui.Button("Añadir héroe", Ui.Parchment, 14);
        column.AddChild(add);
        var problem = DialogText("", 800);
        problem.AddThemeColorOverride("font_color", Ui.Red);
        column.AddChild(problem);

        var heroes = new List<(HBoxContainer Row, LineEdit Name, LineEdit Role, OptionButton Look, CheckBox Player)>();

        void AddRow(HeroSetup? hero)
        {
            if (heroes.Count >= CampaignSetup.MaxHeroes) return;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            var heroName = Ui.Input("Nombre");
            heroName.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            heroName.MaxLength = CampaignSetup.MaxNameLength;
            heroName.Text = hero?.Name ?? "";
            var role = Ui.Input("Qué es: elfa exploradora");
            role.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            role.MaxLength = 40;
            role.Text = hero?.Role ?? "";
            var look = new OptionButton { CustomMinimumSize = new Vector2(130, 48), TooltipText = "Aspecto de la figurita en el mapa" };
            look.AddThemeFontOverride("font", Ui.Body(700));
            foreach (var (label, icon) in CampaignSetup.Looks)
            {
                look.AddItem(label);
                if (hero is not null && hero.Icon == icon) look.Selected = look.ItemCount - 1;
            }
            var player = new CheckBox { Text = "Lo lleva un jugador", ButtonPressed = hero?.HasPlayer ?? true, TooltipText = "Sin marcar, acompaña al grupo y lo lleváis entre todos" };
            player.AddThemeFontOverride("font", Ui.Body(700));
            var remove = Ui.Button("Quitar", Ui.Parchment, 13);
            row.AddChild(heroName);
            row.AddChild(role);
            row.AddChild(look);
            row.AddChild(player);
            row.AddChild(remove);
            rows.AddChild(row);
            var entry = (row, heroName, role, look, player);
            heroes.Add(entry);
            remove.Pressed += () =>
            {
                heroes.Remove(entry);
                rows.RemoveChild(row);
                row.QueueFree();
                add.Disabled = false;
                dialog.ResetSize();
            };
            add.Disabled = heroes.Count >= CampaignSetup.MaxHeroes;
            dialog.ResetSize();
        }

        foreach (var hero in prefill ?? (saved.Count > 0 ? new HeroSetup?[] { null } : new HeroSetup?[] { null, null })) AddRow(hero);
        add.Pressed += () => AddRow(null);

        dialog.Confirmed += () =>
        {
            try
            {
                var party = picks
                    .Where(p => p.Joins.ButtonPressed)
                    .Select(p => new HeroSetup(p.Hero.Name, HasPlayer: p.Player.ButtonPressed, Saved: p.Hero))
                    .Concat(heroes
                        .Where(h => h.Name.Text.Trim().Length > 0 || h.Role.Text.Trim().Length > 0)
                        .Select(h => new HeroSetup(h.Name.Text, h.Role.Text, CampaignSetup.Looks[Math.Max(0, h.Look.Selected)].Icon, HasPlayer: h.Player.ButtonPressed)))
                    .ToList();
                var game = CampaignSetup.Create(name.Text, premise.Text, party, _heroes.Exists);
                dialog.Hide();
                StartGame(game, isNew: true);
            }
            catch (FormatException e)
            {
                problem.Text = e.Message;
            }
        };
        dialog.CustomAction += action =>
        {
            if (action != "ejemplo") return;
            dialog.Hide();
            StartGame(NewCampaign(), isNew: true);
        };
        dialog.VisibilityChanged += () =>
        {
            if (!dialog.Visible) dialog.QueueFree();
        };
        AddChild(dialog);
        dialog.PopupCentered();
        if (prefill is not null)
        {
            name.Text = "La torre hundida";
            premise.Text = "Buscamos a un mago desaparecido en una ciudad de canales";
        }
    }

    // ---------- Construcción de la pantalla ----------

    private void BuildLayout()
    {
        var background = new ColorRect { Color = Ui.Wood, MouseFilter = MouseFilterEnum.Ignore };
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(background);

        var root = new VBoxContainer();
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        root.AddChild(BuildTopBar());

        var margin = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 16);
        root.AddChild(margin);

        var body = new HBoxContainer();
        body.AddThemeConstantOverride("separation", 16);
        margin.AddChild(body);

        var leftScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(232, 0),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _seats = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _seats.AddThemeConstantOverride("separation", 10);
        leftScroll.AddChild(_seats);
        body.AddChild(leftScroll);

        var center = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        center.AddThemeConstantOverride("separation", 12);
        body.AddChild(center);

        _turnBar = Ui.Panel(Ui.WoodDark, 12, 8, 3);
        _turnBar.Visible = false;
        var turnScroll = new ScrollContainer { VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 50) };
        _turnRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _turnRow.AddThemeConstantOverride("separation", 8);
        turnScroll.AddChild(_turnRow);
        _turnBar.AddChild(turnScroll);
        center.AddChild(_turnBar);

        _map = new MapView { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(320, 240) };
        _map.TokenMoved += OnTokenMoved;
        _map.MoveRefused += (id, reason) => Say("Mesa", $"{_table.FindToken(id)?.Name ?? "La ficha"} no se mueve. {reason}");
        _map.SelectionChanged += id =>
        {
            if (id is not null && _table.FindToken(id)?.Seat is { } seat) SetActiveSeat(seat);
        };
        center.AddChild(_map);

        _dice = new DiceTray();
        _dice.Rolled += result => Say(ActiveName(), $"tira {result.Describe()}");
        center.AddChild(_dice);

        body.AddChild(BuildLogPanel());
    }

    private Control BuildTopBar()
    {
        var bar = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = Ui.Ink };
        style.SetContentMarginAll(10);
        style.ContentMarginLeft = 18;
        style.ContentMarginRight = 18;
        bar.AddThemeStyleboxOverride("panel", style);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        bar.AddChild(row);
        _gameLabel = Ui.Text("", 24, Ui.Gold, title: true);
        row.AddChild(_gameLabel);
        _sceneLabel = Ui.Text("", 15, Ui.Parchment);
        row.AddChild(_sceneLabel);
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _masterStatus = Ui.Text("", 14, Ui.Parchment);
        row.AddChild(_masterStatus);
        var rules = Ui.Button("Reglamentos", Ui.Parchment, 13);
        rules.CustomMinimumSize = new Vector2(0, 44);
        rules.TooltipText = "Cargar reglas desde PDF, Word o TXT";
        rules.Pressed += OpenRules;
        row.AddChild(rules);
        var master = Ui.Button("Máster IA", Ui.Gold, 13);
        master.CustomMinimumSize = new Vector2(0, 44);
        master.TooltipText = "Clave y modelo del máster";
        master.Pressed += OpenSettings;
        row.AddChild(master);
        RefreshMasterStatus();
        var games = Ui.Button("Partidas", Ui.Parchment, 13);
        games.CustomMinimumSize = new Vector2(0, 44);
        games.TooltipText = "Continuar otra partida guardada o empezar una nueva";
        games.Pressed += OpenGames;
        row.AddChild(games);
        return bar;
    }

    private Control BuildLogPanel()
    {
        var panel = Ui.Panel(Ui.Parchment, padding: 0);
        panel.CustomMinimumSize = new Vector2(360, 0);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 0);
        panel.AddChild(column);

        var header = new PanelContainer();
        var headerStyle = new StyleBoxFlat { BgColor = Ui.Ink };
        headerStyle.SetContentMarginAll(10);
        headerStyle.ContentMarginLeft = 14;
        headerStyle.CornerRadiusTopLeft = 10;
        headerStyle.CornerRadiusTopRight = 10;
        header.AddThemeStyleboxOverride("panel", headerStyle);
        _masterTitle = Ui.Text("El máster", 20, Ui.Parchment, title: true);
        header.AddChild(_masterTitle);
        column.AddChild(header);

        var logMargin = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            logMargin.AddThemeConstantOverride(side, 14);
        column.AddChild(logMargin);

        _log = new RichTextLabel { BbcodeEnabled = true, ScrollFollowing = true, SelectionEnabled = true, FocusMode = FocusModeEnum.None };
        _log.AddThemeFontOverride("normal_font", Ui.Body(600));
        _log.AddThemeFontOverride("bold_font", Ui.Body(800));
        _log.AddThemeFontSizeOverride("normal_font_size", 15);
        _log.AddThemeFontSizeOverride("bold_font_size", 15);
        _log.AddThemeColorOverride("default_color", Ui.Ink);
        _log.AddThemeConstantOverride("line_separation", 4);
        logMargin.AddChild(_log);

        var footer = new PanelContainer();
        var footerStyle = new StyleBoxFlat { BgColor = new Color("eadbbb"), BorderColor = Ui.Ink, BorderWidthTop = 3 };
        footerStyle.SetContentMarginAll(12);
        footerStyle.ContentMarginBottom = 15;
        footerStyle.CornerRadiusBottomLeft = 10;
        footerStyle.CornerRadiusBottomRight = 10;
        footer.AddThemeStyleboxOverride("panel", footerStyle);
        column.AddChild(footer);

        var inputRow = new HBoxContainer();
        inputRow.AddThemeConstantOverride("separation", 8);
        footer.AddChild(inputRow);
        _chat = Ui.Input("");
        _chat.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _chat.TextSubmitted += _ => SendChat();
        inputRow.AddChild(_chat);
        _send = Ui.Button("Enviar", Ui.Gold);
        _send.Pressed += SendChat;
        inputRow.AddChild(_send);
        return panel;
    }

    // ---------- Asientos ----------

    private Token? ActiveToken() => _table.Tokens.FirstOrDefault(t => t.Seat == _activeSeat);

    private string ActiveName() => ActiveToken()?.Name ?? $"Asiento {_activeSeat}";

    private void SetActiveSeat(int seat)
    {
        if (_activeSeat == seat) return;
        _activeSeat = seat;
        RefreshSeats();
    }

    private void RefreshSeats()
    {
        foreach (var child in _seats.GetChildren())
        {
            _seats.RemoveChild(child);
            child.QueueFree();
        }

        _seats.AddChild(SectionTitle("ASIENTOS EN ESTE PC"));
        foreach (var token in _table.Tokens.Where(t => t.Seat is not null).OrderBy(t => t.Seat))
            _seats.AddChild(SeatCard(token));

        var others = _table.Tokens.Where(t => t.Seat is null && !_table.IsUnexplored(t.X, t.Y)).ToList();
        if (others.Count > 0)
        {
            _seats.AddChild(SectionTitle("TAMBIÉN EN LA MESA"));
            foreach (var token in others) _seats.AddChild(OtherRow(token));
        }

        AddStoryPanel();

        if (ActiveToken() is { } sheetOwner)
        {
            var sheet = Ui.Button(sheetOwner.Created ? $"Ficha de {sheetOwner.Name}" : $"Crear a {sheetOwner.Name}", sheetOwner.Created ? Ui.Parchment : Ui.Gold, 13);
            sheet.CustomMinimumSize = new Vector2(0, 44);
            sheet.TooltipText = "Ver y editar la hoja de personaje";
            sheet.Pressed += () => OpenSheet(sheetOwner.Id);
            _seats.AddChild(sheet);
        }

        if (_chat is not null) _chat.PlaceholderText = $"Escribe lo que hace {ActiveName()}…";
        RefreshTurnBar();
    }

    // ---------- Historia ----------

    private void AddStoryPanel()
    {
        var story = _game.Story;
        _seats.AddChild(SectionTitle("LA HISTORIA"));
        var panel = Ui.Panel(Ui.Parchment, 12, 10, 2);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 4);
        panel.AddChild(column);

        var scene = story.SceneTitle.Length > 0 ? $"Escena {story.Scene} · {story.SceneTitle}" : $"Escena {story.Scene}";
        column.AddChild(Wrapped(scene, 14, 800));
        if (story.FinalGoal.Length > 0) column.AddChild(Wrapped($"Objetivo: {story.FinalGoal}", 13, 700));
        foreach (var goal in story.Goals) column.AddChild(Wrapped($"· {goal}", 13, 600));
        if (story.PastTurningPoint) column.AddChild(Wrapped("La aventura se acerca a su desenlace.", 13, 800));
        _seats.AddChild(panel);

        foreach (var countdown in story.Countdowns)
        {
            var card = Ui.Panel(countdown.Left == 0 ? Ui.Red : Ui.PaleGold, 12, 10, 2);
            var rows = new VBoxContainer();
            rows.AddThemeConstantOverride("separation", 6);
            card.AddChild(rows);
            rows.AddChild(Wrapped(countdown.Name, 14, 800, countdown.Left == 0 ? Ui.Cream : Ui.Ink));
            var steps = new HBoxContainer();
            steps.AddThemeConstantOverride("separation", 4);
            for (var i = 0; i < countdown.Total; i++)
            {
                var step = new Panel { CustomMinimumSize = new Vector2(0, 14), SizeFlagsHorizontal = SizeFlags.ExpandFill };
                var style = new StyleBoxFlat { BgColor = i < countdown.Left ? Ui.HpRed : Ui.Cream, BorderColor = Ui.Ink };
                style.SetBorderWidthAll(2);
                style.SetCornerRadiusAll(4);
                step.AddThemeStyleboxOverride("panel", style);
                steps.AddChild(step);
            }
            rows.AddChild(steps);
            card.TooltipText = countdown.Left == 0 ? "Ha llegado a cero" : $"Quedan {countdown.Left} de {countdown.Total}";
            _seats.AddChild(card);
        }
    }

    private static Label Wrapped(string text, int size, int weight, Color? color = null)
    {
        var label = Ui.Text(text, size, color ?? Ui.Ink, weight: weight);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(180, 0);
        return label;
    }

    // ---------- Combate ----------

    private void RefreshTurnBar()
    {
        foreach (var child in _turnRow.GetChildren())
        {
            _turnRow.RemoveChild(child);
            child.QueueFree();
        }
        var combat = _table.Combat;
        _turnBar.Visible = combat is not null;
        if (combat is null)
        {
            _lastTurnId = null;
            return;
        }

        var round = Ui.Text($"Ronda {combat.Round}", 18, Ui.Gold, title: true);
        round.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _turnRow.AddChild(round);
        foreach (var combatant in combat.Order)
        {
            var token = _table.FindToken(combatant.Token);
            if (token is null) continue;
            var current = combatant.Token == combat.CurrentTokenId;
            var chip = Ui.Panel(current ? Ui.Gold : Ui.Parchment, 10, 6, 2);
            chip.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            chip.Modulate = token.Hp > 0 ? Colors.White : new Color(1, 1, 1, 0.45f);
            chip.TooltipText = token.Hp > 0 ? $"Iniciativa {combatant.Initiative}" : "Fuera de combate";
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 6);
            line.AddChild(Dot(new Color(token.Color), 20));
            line.AddChild(Ui.Text(token.Name, 14, weight: 800));
            line.AddChild(Ui.Text(combatant.Initiative.ToString(), 13));
            chip.AddChild(line);
            _turnRow.AddChild(chip);
        }
        _turnRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var pass = Ui.Button("Pasar turno", Ui.Parchment, 13);
        pass.CustomMinimumSize = new Vector2(0, 40);
        pass.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        pass.Disabled = _masterBusy;
        pass.TooltipText = "Termina el turno actual";
        pass.Pressed += PassTurn;
        _turnRow.AddChild(pass);

        // Cuando le toca a un héroe de este PC, su asiento pasa a ser el activo.
        if (_lastTurnId != combat.CurrentTokenId)
        {
            _lastTurnId = combat.CurrentTokenId;
            if (_table.TurnToken?.Seat is { } seat && seat != _activeSeat) Callable.From(() => SetActiveSeat(seat)).CallDeferred();
        }
    }

    private void PassTurn()
    {
        if (_masterBusy || _table.Combat is null) return;
        var round = _table.Combat.Round;
        var next = _table.NextTurn();
        if (next is null)
        {
            Say("Mesa", "No queda nadie en pie para actuar.");
            return;
        }
        var newRound = _table.Combat.Round != round ? $"Empieza la ronda {_table.Combat.Round}. " : "";
        Say("Mesa", $"{newRound}Le toca a {next.Name}.");
        // Si le toca a una criatura del máster, se le avisa para que juegue su turno.
        if (next.Kind != TokenKind.Hero && HasMaster)
            _ = AskMaster("Mesa", $"Los jugadores han pasado el turno. Le toca a {next.Name}: resuelve los turnos de tus criaturas hasta que le toque a un héroe.");
    }

    // ---------- Hoja de personaje ----------

    private void OpenSheet(string tokenId)
    {
        var token = _table.FindToken(tokenId);
        if (token is null) return;
        var dialog = new AcceptDialog { Title = $"Ficha de {token.Name}", OkButtonText = "Guardar", MinSize = new Vector2I(560, 0) };
        dialog.AddCancelButton("Cancelar");
        // El máster crea o completa el personaje preguntando paso a paso según el reglamento cargado.
        var guided = dialog.AddButton(!token.Created ? "Crear con el máster" : "Repasar con el máster", true, "master");
        guided.TooltipText = "El máster te guía paso a paso según el reglamento cargado y apunta aquí el resultado";
        guided.Disabled = !HasMaster || _masterBusy;
        dialog.CustomAction += action =>
        {
            if (action != "master") return;
            dialog.Hide();
            if (token.Seat is { } seat) SetActiveSeat(seat);
            _chat.Text = !token.Created
                ? $"Vamos a crear a {token.Name} paso a paso según el reglamento."
                : $"Quiero repasar la ficha de {token.Name} según el reglamento.";
            SendChat();
        };
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        dialog.AddChild(column);
        CheckBox? finished = null;
        if (!token.Created)
        {
            column.AddChild(DialogText(HasMaster
                ? "Este personaje está sin crear. Pulsa «Crear con el máster» y te irá preguntando según el reglamento cargado. La aventura empieza cuando estén todos."
                : "Este personaje está sin crear. Conecta el máster en «Máster IA» para que te guíe, o escribe tú la ficha aquí.", 800));
            finished = new CheckBox { Text = "Ya lo he creado yo: dar el personaje por terminado" };
            finished.AddThemeFontOverride("font", Ui.Body(700));
        }
        column.AddChild(DialogText(
            "Un dato por línea, con el nombre, dos puntos y el valor. Por ejemplo «Fuerza: 16» o «Espada larga: +6, daño 1d8+3». "
            + "Usa los nombres de tu reglamento. El máster lee esta ficha y también puede apuntar datos en ella.", 600));
        var editor = new TextEdit
        {
            Text = string.Join("\n", token.Sheet.Select(e => $"{e.Name}: {e.Value}")),
            PlaceholderText = "Fuerza: 16\nCA: 18\nEspada larga: +6, daño 1d8+3",
            CustomMinimumSize = new Vector2(520, 320),
        };
        editor.AddThemeFontOverride("font", Ui.Body(600));
        editor.AddThemeFontSizeOverride("font_size", 15);
        column.AddChild(editor);
        if (finished is not null) column.AddChild(finished);

        dialog.Confirmed += () =>
        {
            if (_table.FindToken(tokenId) is null) return;
            var entries = editor.Text.Split('\n')
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2)
                .Select(parts => new SheetEntry(parts[0], parts[1]));
            _table.ReplaceSheet(tokenId, entries);
            if (finished is { ButtonPressed: true } && _table.FindToken(tokenId) is { Sheet.Count: > 0 } done)
            {
                _table.SetCreated(tokenId, true);
                Say("Mesa", $"{done.Name} ya está listo para la aventura.");
                // Si era el último, el máster abre la historia.
                if (CampaignSetup.PendingHeroes(_game).Count == 0 && _table.SceneId == CampaignSetup.StartSceneId && HasMaster && !_masterBusy)
                    _ = AskMaster("Mesa", CampaignSetup.OpeningRequest(_game));
            }
        };
        dialog.VisibilityChanged += () =>
        {
            if (!dialog.Visible) dialog.QueueFree();
        };
        AddChild(dialog);
        dialog.PopupCentered();
    }

    private static Label SectionTitle(string text) => Ui.Text(text, 13, Ui.Light, weight: 800);

    private Control SeatCard(Token token)
    {
        var active = token.Seat == _activeSeat;
        var card = Ui.Button("", active ? Ui.PaleGold : Ui.Parchment);
        card.ToggleMode = false;
        card.TooltipText = $"Jugar como {token.Name}";
        card.CustomMinimumSize = new Vector2(0, 112);
        card.Pressed += () =>
        {
            SetActiveSeat(token.Seat!.Value);
            _map.Select(token.Id);
        };

        var content = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        content.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.Minsize, 12);
        content.OffsetBottom = -15;
        content.AddThemeConstantOverride("separation", 2);
        card.AddChild(content);

        var top = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        top.AddThemeConstantOverride("separation", 10);
        top.AddChild(Dot(new Color(token.Color), 34));
        var names = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        names.AddThemeConstantOverride("separation", -6);
        names.AddChild(Ui.Text(token.Name, 20, title: true));
        names.AddChild(Ui.Text(token.Created ? $"{token.Role} · Asiento {token.Seat}" : $"Sin crear · Asiento {token.Seat}", 12, token.Created ? null : Ui.Red, weight: token.Created ? 700 : 800));
        top.AddChild(names);
        content.AddChild(top);

        content.AddChild(HpBar(token));
        var stats = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        stats.AddChild(Ui.Text($"PV {token.Hp} / {token.MaxHp}", 13, weight: 800));
        stats.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        if (active) stats.AddChild(Ui.Text("Activo", 13, weight: 800));
        content.AddChild(stats);
        return card;
    }

    private static Control OtherRow(Token token)
    {
        var panel = Ui.Panel(Ui.Parchment, 12, 8, 2);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);
        row.AddChild(Dot(new Color(token.Color), 24));
        var name = Ui.Text(token.Name, 14, weight: 800);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        row.AddChild(name);
        row.AddChild(Ui.Text($"{token.Hp}/{token.MaxHp}", 13, weight: 800));
        return panel;
    }

    private static Control Dot(Color color, int size)
    {
        var dot = new Panel { CustomMinimumSize = new Vector2(size, size), MouseFilter = MouseFilterEnum.Ignore };
        dot.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        var style = new StyleBoxFlat { BgColor = color, BorderColor = Ui.Ink, AntiAliasing = true };
        style.SetBorderWidthAll(3);
        style.SetCornerRadiusAll(size / 2);
        dot.AddThemeStyleboxOverride("panel", style);
        return dot;
    }

    private static Control HpBar(Token token)
    {
        var bar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = Math.Max(1, token.MaxHp),
            Value = token.Hp,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 16),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        var back = new StyleBoxFlat { BgColor = Ui.Cream, BorderColor = Ui.Ink };
        back.SetBorderWidthAll(3);
        back.SetCornerRadiusAll(8);
        var fill = new StyleBoxFlat { BgColor = Ui.HpRed, BorderColor = Ui.Ink };
        fill.SetBorderWidthAll(3);
        fill.SetCornerRadiusAll(8);
        bar.AddThemeStyleboxOverride("background", back);
        bar.AddThemeStyleboxOverride("fill", fill);
        return bar;
    }

    // ---------- Registro ----------

    private void SendChat()
    {
        var text = _chat.Text.Trim();
        if (text.Length == 0 || _masterBusy) return;
        _chat.Clear();
        var speaker = ActiveName();
        Say(speaker, text);

        if (!HasMaster)
        {
            if (!_noKeyHintShown) Say("Mesa", "El máster no está conectado, así que nadie responde. Pulsa «Máster IA» arriba para poner tu clave.");
            _noKeyHintShown = true;
            return;
        }
        // En el punto de partida todavía no hay aventura: lo primero es que el máster la abra.
        _ = AskMaster(speaker, _table.SceneId == CampaignSetup.StartSceneId ? $"{text}\n\n({CampaignSetup.StartRequest(_game)})" : text);
    }

    // ---------- Máster IA ----------

    private bool HasMaster => _apiKey.Length > 0;

    private MasterModel CurrentModel => ClaudeMaster.Models.FirstOrDefault(m => m.Id == _modelId) ?? ClaudeMaster.Models[0];

    private async Task AskMaster(string speaker, string text)
    {
        var game = _game;
        var turn = new CancellationTokenSource();
        _masterTurn = turn;
        SetMasterBusy(true);
        try
        {
            var master = new ClaudeMaster(_apiKey, CurrentModel.Id, _apiUrl);
            // Tras cada espera se vuelve al hilo principal de Godot, así que las herramientas
            // cambian la mesa en el mismo hilo que la dibuja.
            var usage = await master.PlayTurnAsync(game, _tools, speaker, text,
                narration => { if (game == _game) Say(MasterToolbox.MasterName, narration); }, turn.Token);
            _sessionCost += CurrentModel.Cost(usage);
        }
        catch (MasterException e)
        {
            if (game == _game) Say("Mesa", e.Message);
        }
        catch (OperationCanceledException)
        {
            // Se empezó otra partida mientras el máster pensaba: su respuesta ya no hace falta.
        }
        catch (Exception e)
        {
            GD.PushError(e.ToString());
            if (game == _game) Say("Mesa", $"El máster ha tenido un problema inesperado: {e.Message}");
        }
        finally
        {
            if (_masterTurn == turn)
            {
                _masterTurn = null;
                SetMasterBusy(false);
            }
            turn.Dispose();
        }
    }

    private void SetMasterBusy(bool busy)
    {
        _masterBusy = busy;
        _masterTitle.Text = busy ? "El máster está pensando…" : "El máster";
        _chat.Editable = !busy;
        _send.Disabled = busy;
        RefreshMasterStatus();
        RefreshTurnBar();
        if (!busy && !_ephemeral) _chat.GrabFocus();
    }

    private void RefreshMasterStatus()
    {
        var ruleCount = _rules.BooksOf(BookKind.Rules).Count();
        var guideCount = _rules.BooksOf(BookKind.Guide).Count();
        var books = ruleCount switch
        {
            0 => "sin reglamentos",
            1 => "1 reglamento",
            _ => $"{ruleCount} reglamentos",
        };
        if (guideCount > 0) books += guideCount == 1 ? " · 1 guía" : $" · {guideCount} guías";
        if (!HasMaster)
        {
            _masterStatus.Text = $"Máster sin conectar · {books}";
            return;
        }
        var cost = _sessionCost > 0 ? $" · gasto aprox. {_sessionCost:0.00} $" : "";
        _masterStatus.Text = $"Máster: {CurrentModel.Name}{cost} · {books}";
    }

    private void LoadSettings()
    {
        if (!Godot.FileAccess.FileExists(SettingsPath)) return;
        try
        {
            using var settings = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(SettingsPath));
            if (settings.RootElement.TryGetProperty("claveApi", out var key)) _apiKey = key.GetString() ?? "";
            if (settings.RootElement.TryGetProperty("modelo", out var model) && ClaudeMaster.Models.Any(m => m.Id == model.GetString()))
                _modelId = model.GetString()!;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            GD.PushWarning($"No se pudieron leer los ajustes: {e.Message}");
        }
    }

    private void SaveSettings()
    {
        using var file = Godot.FileAccess.Open(SettingsPath, Godot.FileAccess.ModeFlags.Write);
        if (file is null)
        {
            Say("Mesa", $"No se pudieron guardar los ajustes: {Godot.FileAccess.GetOpenError()}");
            return;
        }
        file.StoreString(JsonSerializer.Serialize(new Dictionary<string, string> { ["claveApi"] = _apiKey, ["modelo"] = _modelId }));
    }

    private void OpenSettings()
    {
        var dialog = new AcceptDialog { Title = "Máster IA", OkButtonText = "Guardar", MinSize = new Vector2I(560, 0) };
        dialog.AddCancelButton("Cancelar");

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        dialog.AddChild(column);

        column.AddChild(DialogText("Clave de API de Claude", 800));
        var key = Ui.Input("sk-ant-…");
        key.Secret = true;
        key.Text = _apiKey;
        column.AddChild(key);
        column.AddChild(DialogText(
            "Se crea en platform.claude.com, en «Claves de API». Se guarda solo en este PC y solo se envía a Claude. "
            + "Cada respuesta del máster gasta un poco del saldo de esa cuenta.", 600));

        column.AddChild(DialogText("Modelo", 800));
        var model = new OptionButton { CustomMinimumSize = new Vector2(0, 44) };
        model.AddThemeFontOverride("font", Ui.Body(700));
        foreach (var option in ClaudeMaster.Models)
        {
            model.AddItem($"{option.Name}: {option.Note}");
            if (option.Id == _modelId) model.Selected = model.ItemCount - 1;
        }
        column.AddChild(model);

        dialog.Confirmed += () =>
        {
            _apiKey = key.Text.Trim();
            _modelId = ClaudeMaster.Models[Math.Max(0, model.Selected)].Id;
            _noKeyHintShown = false;
            if (!_ephemeral) SaveSettings();
            RefreshMasterStatus();
            Say("Mesa", HasMaster
                ? $"Máster conectado con {CurrentModel.Name}. Escribe lo que hace tu personaje."
                : "Máster desconectado: no hay clave.");
        };
        dialog.VisibilityChanged += () =>
        {
            if (!dialog.Visible) dialog.QueueFree();
        };
        AddChild(dialog);
        dialog.PopupCentered();
    }

    // ---------- Reglamentos ----------

    private void OpenRules()
    {
        var dialog = new AcceptDialog { Title = "Reglamentos", OkButtonText = "Cerrar", MinSize = new Vector2I(600, 0) };
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        dialog.AddChild(column);

        column.AddChild(DialogText(
            "Añade aquí los manuales de tu juego en PDF, Word (.docx) o TXT. El juego los lee una vez, los guarda en este PC "
            + "y el máster los consulta cuando necesita una regla. Si arrastras archivos a la ventana se añaden como reglamentos.\n\n"
            + "Las guías de dirección son libros de consejos para llevar partidas: no son reglas, pero el máster las consulta "
            + "para decidir cómo llevar una escena.", 600));

        _rulesList = new VBoxContainer();
        _rulesList.AddThemeConstantOverride("separation", 6);
        column.AddChild(_rulesList);
        RefreshRulesList();

        _rulesProgress = DialogText("", 800);
        column.AddChild(_rulesProgress);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 10);
        foreach (var (text, kind) in new[] { ("Añadir reglamento…", BookKind.Rules), ("Añadir guía de dirección…", BookKind.Guide) })
        {
            var add = new Button { Text = text, CustomMinimumSize = new Vector2(0, 44), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            add.AddThemeFontOverride("font", Ui.Body(800));
            add.Pressed += () => PickRuleFiles(kind);
            buttons.AddChild(add);
        }
        column.AddChild(buttons);

        dialog.VisibilityChanged += () =>
        {
            if (dialog.Visible) return;
            _rulesList = null;
            _rulesProgress = null;
            dialog.QueueFree();
        };
        AddChild(dialog);
        dialog.PopupCentered();
    }

    private void RefreshRulesList()
    {
        RefreshMasterStatus();
        if (_rulesList is null || !IsInstanceValid(_rulesList)) return;
        foreach (var child in _rulesList.GetChildren())
        {
            _rulesList.RemoveChild(child);
            child.QueueFree();
        }
        if (_rules.Books.Count == 0)
        {
            _rulesList.AddChild(DialogText("Todavía no hay ningún reglamento. El máster usa una regla sencilla de 1d20 contra dificultad.", 700));
            return;
        }
        foreach (var book in _rules.Books.OrderBy(b => b.Kind))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var pages = book.HasPages ? "con números de página" : "sin números de página";
            var kind = book.Kind == BookKind.Guide ? "Guía de dirección" : "Reglamento";
            var label = DialogText($"{book.Title}\n{kind} · {book.FileName} · {book.Passages.Count} pasajes · {pages}", 700);
            label.CustomMinimumSize = new Vector2(400, 0);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(label);
            var remove = new Button { Text = "Quitar", SizeFlagsVertical = SizeFlags.ShrinkCenter, CustomMinimumSize = new Vector2(0, 40) };
            remove.AddThemeFontOverride("font", Ui.Body(700));
            var id = book.Id;
            var title = book.Title;
            remove.Pressed += () =>
            {
                _rulesStore?.Delete(id);
                _rules.Remove(id);
                Say("Mesa", $"«{title}» quitado.");
            };
            row.AddChild(remove);
            _rulesList.AddChild(row);
        }
    }

    private void PickRuleFiles(BookKind kind)
    {
        var picker = new FileDialog
        {
            Title = kind == BookKind.Guide ? "Elige una o varias guías de dirección" : "Elige uno o varios reglamentos",
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFiles,
            UseNativeDialog = true,
            Filters = new[] { "*.pdf, *.docx, *.txt, *.md ; PDF, Word o texto" },
        };
        picker.FilesSelected += files => _ = ImportRules(files, kind);
        picker.VisibilityChanged += () =>
        {
            if (!picker.Visible) picker.QueueFree();
        };
        AddChild(picker);
        picker.PopupCentered(new Vector2I(900, 600));
    }

    private async Task ImportRules(string[] files, BookKind kind = BookKind.Rules)
    {
        if (_importing is not null)
        {
            Say("Mesa", "Espera a que termine de leerse el reglamento anterior.");
            return;
        }
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            _importPercent = 0;
            _importing = name;
            try
            {
                // Leer un manual grande tarda unos segundos: se hace aparte para no congelar la mesa.
                var book = await Task.Run(() => RuleFiles.Import(file, progress => _importPercent = (int)(progress * 100), kind: kind));
                _rulesStore?.Save(book);
                _rules.Add(book);
                var pages = book.HasPages || kind == BookKind.Guide ? "" : " Este formato no tiene números de página, así que el máster citará solo el libro.";
                var what = kind == BookKind.Guide ? "Guía de dirección" : "Reglamento";
                Say("Mesa", $"{what} «{book.Title}» cargado: {book.Passages.Count} pasajes.{pages}");
            }
            catch (FormatException e)
            {
                Say("Mesa", $"«{name}» no se ha cargado. {e.Message}");
            }
            catch (IOException e)
            {
                Say("Mesa", $"«{name}» no se ha podido guardar: {e.Message}");
            }
            finally
            {
                _importing = null;
            }
        }
        if (_rulesProgress is not null && IsInstanceValid(_rulesProgress)) _rulesProgress.Text = "";
    }

    private static Label DialogText(string text, int weight)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(520, 0) };
        label.AddThemeFontOverride("font", Ui.Body(weight));
        label.AddThemeFontSizeOverride("font_size", 15);
        return label;
    }

    private void Say(string who, string text)
    {
        AppendLog(who, text);
        _game.AddLog(who, text);
    }

    private void AppendLog(string who, string text)
    {
        if (_log.GetParsedText().Length > 0) _log.AppendText("\n\n");
        _log.AppendText($"[b]{Escape(who)}[/b]  {Escape(text)}");
    }

    /// <summary>Lo que escribe un jugador se muestra tal cual, nunca como formato.</summary>
    private static string Escape(string text) => text.Replace("[", "[lb]");

    // ---------- Captura para revisar el aspecto sin abrir el editor ----------

    private async Task CaptureAndQuit(string? rules, string path, bool drag, bool travel, bool settings, string? say)
    {
        if (rules is not null)
        {
            await ImportRules(rules.Split(';'));
            OpenRules();
        }
        if (say is not null)
        {
            _chat.Text = say;
            SendChat();
            while (_masterBusy) await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
        }
        if (settings) OpenSettings();
        if (OS.GetCmdlineUserArgs().Contains("--historia"))
        {
            _game.Story.NextScene("El peaje del puente");
            _game.Story.SetFinalGoal("Recuperar la campana de la mina");
            _game.Story.AddGoal("Cruzar el puente");
            _game.Story.StartCountdown("Llegan refuerzos goblin", 5);
            _game.Story.AdvanceCountdown("Llegan refuerzos goblin", 2);
        }
        if (OS.GetCmdlineUserArgs().Contains("--partidas"))
        {
            _games.Save("la_torre_hundida", CampaignSetup.Create("La torre hundida", "", new[] { new HeroSetup("Ánika la Roja", "Bárbara"), new HeroSetup("Fen", "Hechicero") }));
            _games.Save("el_pantano_de_los_susurros", CampaignSetup.Create("El pantano de los susurros", "", new[] { new HeroSetup("Lira", "Exploradora"), new HeroSetup("Brom", "Herrero") }));
            _games.Save(_gameId, _game);
            OpenGames();
        }
        if (OS.GetCmdlineUserArgs().Contains("--nueva"))
        {
            _heroes.Save(new SavedHero("lira", "Lira", "Exploradora elfa", "", "#1f8a7a", 23, new[] { new SheetEntry("Nivel", "3") }, "El pantano de los susurros"));
            _heroes.Save(new SavedHero("brom", "Brom", "Enano herrero", "sword", "#c98a2b", 31, new[] { new SheetEntry("Nivel", "3") }, "El pantano de los susurros"));
            AskNewGame(new[] { new HeroSetup("Ánika la Roja", "Bárbara del norte", "sword"), new HeroSetup("Fen", "Hechicero curioso", "hat"), new HeroSetup("Mulo", "Porteador", "", HasPlayer: false) });
        }
        if (OS.GetCmdlineUserArgs().Contains("--propia"))
        {
            StartGame(CampaignSetup.Create("La torre hundida", "Buscamos a un mago desaparecido",
                new[] { new HeroSetup("Ánika la Roja", "Bárbara del norte", "sword"), new HeroSetup("Fen", "Hechicero curioso", "hat"), new HeroSetup("Mulo", "Porteador", "", HasPlayer: false) }), isNew: true);
            while (_masterBusy) await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            if (OS.GetCmdlineUserArgs().Contains("--ficha") && ActiveToken() is { } own) OpenSheet(own.Id);
        }
        if (OS.GetCmdlineUserArgs().Contains("--lugar"))
        {
            // Lo mismo que pediría el máster al llevar al grupo a un sitio nuevo.
            var place = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>("""
                {"nombre":"Posada del Jabalí Tuerto",
                 "plano":["##############","#......#.....#","#.oo...#.....#","#......#.....#","#............#","#......###..##","#............#","#..~~........#","#..~~....oo..#","######..######"],
                 "llegada":{"x":7,"y":8},
                 "fichas":[{"nombre":"Posadera","tipo":"pnj","x":3,"y":3,"pv":8,"papel":"dueña de la posada","color":"#c98a2b"},
                           {"nombre":"Viajero encapuchado","tipo":"pnj","x":10,"y":2,"pv":12,"icono":"mago","papel":"forastero callado","color":"#5b5b6b"},
                           {"nombre":"Matón","tipo":"enemigo","x":10,"y":7,"pv":9,"icono":"guerrero","papel":"busca pelea"}]}
                """)!;
            var outcome = _tools.Execute("crear_lugar", place);
            if (outcome.IsError) GD.PushError(outcome.Result);
        }
        if (OS.GetCmdlineUserArgs().Contains("--combate"))
        {
            _table.SetSheetEntry("kael", "Fuerza", "16");
            _table.SetSheetEntry("kael", "Espada larga", "+6, daño 1d8+3");
            _table.StartCombat(new[] { new CombatantSpec("goblin_1", 17), new CombatantSpec("kael", 14), new CombatantSpec("mira", 9), new CombatantSpec("goblin_jefe", 6) });
            _table.NextTurn();
            if (OS.GetCmdlineUserArgs().Contains("--ficha")) OpenSheet("kael");
        }
        if (travel)
        {
            // Lleva a Kael hasta la salida del este para ver el segundo lugar.
            _table.MoveToken("goblin_1", 8, 5);
            _table.MoveToken("kael", 12, 4);
            OnTokenMoved("kael", 40);
        }
        _map.Select(ActiveToken()?.Id);
        _dice.Roll("2d6+3");
        await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
        if (drag && ActiveToken() is { } token)
        {
            // Simula coger la ficha activa y llevarla dos casillas a la derecha, sin soltarla.
            var from = _map.GlobalPosition + _map.CellCenter(token.X, token.Y);
            var to = _map.GlobalPosition + _map.CellCenter(token.X + 2, token.Y - 1);
            GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = from, GlobalPosition = from });
            GetViewport().PushInput(new InputEventMouseMotion { Position = to, GlobalPosition = to, ButtonMask = MouseButtonMask.Left });
            await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        }
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var error = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print(error == Error.Ok ? $"Captura guardada en {path}" : $"No se pudo guardar la captura: {error}");
        GetTree().Quit(error == Error.Ok ? 0 : 1);
    }
}
