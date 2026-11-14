using Godot;
using RoleDMFable.Core;

namespace RoleDMFable;

/// <summary>
/// Pantalla de la mesa. El mapa ocupa siempre el centro; a la izquierda los asientos
/// de este PC, debajo los dados y a la derecha el registro (donde hablará el máster).
/// </summary>
public partial class Main : Control
{
    private const string ScenesFolder = "res://data/scenes";
    private const string StartScene = "sala_del_puente";
    private const string SavePath = "user://partida.json";
    private const double SaveDelaySeconds = 0.5;

    private Campaign _game = null!;
    private TableState _table => _game.Current;
    private Label _sceneLabel = null!;
    private bool _ephemeral;
    private bool _refreshQueued;
    private double _saveCountdown = -1;
    private MapView _map = null!;
    private DiceTray _dice = null!;
    private VBoxContainer _seats = null!;
    private RichTextLabel _log = null!;
    private LineEdit _chat = null!;
    private int _activeSeat = 1;

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        var capture = args.FirstOrDefault(a => a.StartsWith("--captura="));
        // Las capturas de revisión no leen ni pisan la partida guardada del jugador.
        _ephemeral = capture is not null;

        string? notice = null;
        var loaded = _ephemeral ? null : TryLoadSave(out notice);
        BuildLayout();
        StartGame(loaded ?? NewCampaign(), isNew: loaded is null);
        if (notice is not null) Say("Mesa", notice);

        if (capture is not null)
            _ = CaptureAndQuit(capture["--captura=".Length..], args.Contains("--arrastrar"), args.Contains("--viajar"));
    }

    public override void _Process(double delta)
    {
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

    private static Campaign? TryLoadSave(out string? notice)
    {
        notice = null;
        if (!Godot.FileAccess.FileExists(SavePath)) return null;
        try
        {
            return Campaign.FromJson(Godot.FileAccess.GetFileAsString(SavePath));
        }
        catch (FormatException e)
        {
            // No se borra: se aparta por si se puede recuperar a mano.
            DirAccess.RenameAbsolute(SavePath, SavePath + ".roto");
            notice = $"No se pudo cargar la partida guardada ({e.Message}) y se ha empezado una nueva. La anterior queda apartada como partida.json.roto.";
            return null;
        }
    }

    private void StartGame(Campaign game, bool isNew)
    {
        if (_game is not null) _game.Changed -= OnGameChanged;
        _game = game;
        _game.Changed += OnGameChanged;
        _activeSeat = _game.Scenes.SelectMany(s => s.Tokens).Where(t => t.Seat is not null).Select(t => t.Seat!.Value).DefaultIfEmpty(1).Min();

        _log.Clear();
        foreach (var entry in _game.Log) AppendLog(entry.Who, entry.Text);
        ShowCurrentScene();

        if (isNew)
        {
            Say("Mesa", $"Empieza la partida en {_table.SceneName}. Arrastra una figurita para moverla; si un héroe pisa una salida dorada, el grupo pasa al siguiente lugar.");
            Say("Mesa", "La partida se guarda sola. El máster todavía no está conectado: llegará en la fase 1.");
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
        // Se escribe en un archivo aparte y luego se cambia por el bueno, para no dejarlo a medias.
        var temp = SavePath + ".tmp";
        using (var file = Godot.FileAccess.Open(temp, Godot.FileAccess.ModeFlags.Write))
        {
            if (file is null)
            {
                GD.PushError($"No se pudo guardar la partida: {Godot.FileAccess.GetOpenError()}");
                return;
            }
            file.StoreString(_game.ToJson());
        }
        var error = DirAccess.RenameAbsolute(temp, SavePath);
        if (error != Error.Ok) GD.PushError($"No se pudo guardar la partida: {error}");
    }

    private void OnTokenMoved(string id, int feet)
    {
        Say(_table.FindToken(id)!.Name, $"se mueve {feet} pies.");
        var travel = _game.TryTravel(id);
        if (travel is not null)
        {
            ShowCurrentScene();
            var way = travel.Label.Length > 0 ? $" por «{travel.Label}»" : "";
            Say("Mesa", $"El grupo sale de {travel.FromScene}{way} y llega a {travel.ToScene}.");
        }
        else if (_table.FindToken(id) is { Kind: TokenKind.Hero } hero && _table.ExitAt(hero.X, hero.Y) is not null)
        {
            Say("Mesa", "El grupo no cabe al otro lado de esa salida.");
        }
    }

    private void AskNewGame()
    {
        var dialog = new ConfirmationDialog
        {
            Title = "Nueva partida",
            DialogText = "Se borrará la partida guardada y empezarás de cero.",
            OkButtonText = "Empezar de cero",
            CancelButtonText = "Seguir jugando",
        };
        dialog.Confirmed += () => StartGame(NewCampaign(), isNew: true);
        dialog.VisibilityChanged += () =>
        {
            if (!dialog.Visible) dialog.QueueFree();
        };
        AddChild(dialog);
        dialog.PopupCentered();
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
        row.AddChild(Ui.Text("Partida de prueba", 24, Ui.Gold, title: true));
        _sceneLabel = Ui.Text("", 15, Ui.Parchment);
        row.AddChild(_sceneLabel);
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        row.AddChild(Ui.Text("Se guarda sola · un solo PC, sin máster todavía", 14, Ui.Parchment));
        var fresh = Ui.Button("Nueva partida", Ui.Parchment, 13);
        fresh.CustomMinimumSize = new Vector2(0, 44);
        fresh.Pressed += AskNewGame;
        row.AddChild(fresh);
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
        header.AddChild(Ui.Text("El máster", 20, Ui.Parchment, title: true));
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
        var send = Ui.Button("Enviar", Ui.Gold);
        send.Pressed += SendChat;
        inputRow.AddChild(send);
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

        if (_chat is not null) _chat.PlaceholderText = $"Escribe lo que hace {ActiveName()}…";
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
        names.AddChild(Ui.Text($"{token.Role} · Asiento {token.Seat}", 12));
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
        if (text.Length == 0) return;
        _chat.Clear();
        Say(ActiveName(), text);
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

    private async Task CaptureAndQuit(string path, bool drag, bool travel)
    {
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
