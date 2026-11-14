using Godot;
using RoleDMFable.Core;

namespace RoleDMFable;

/// <summary>
/// Pantalla de la mesa. El mapa ocupa siempre el centro; a la izquierda los asientos
/// de este PC, debajo los dados y a la derecha el registro (donde hablará el máster).
/// </summary>
public partial class Main : Control
{
    private const string SampleScene = "res://data/scenes/sala_del_puente.json";

    private TableState _table = null!;
    private MapView _map = null!;
    private DiceTray _dice = null!;
    private VBoxContainer _seats = null!;
    private RichTextLabel _log = null!;
    private LineEdit _chat = null!;
    private int _activeSeat = 1;

    public override void _Ready()
    {
        _table = new TableState(SceneSpec.FromJson(Godot.FileAccess.GetFileAsString(SampleScene)));
        _activeSeat = _table.Tokens.Where(t => t.Seat is not null).Select(t => t.Seat!.Value).DefaultIfEmpty(1).Min();

        BuildLayout();

        _map.SetTable(_table);
        _table.Changed += RefreshSeats;
        RefreshSeats();

        Say("Mesa", $"Escena cargada: {_table.SceneName}. Arrastra una figurita para moverla y usa la bandeja para tirar dados.");
        Say("Mesa", "El máster todavía no está conectado: llegará en la fase 1.");

        var capture = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--captura="));
        if (capture is not null)
            _ = CaptureAndQuit(capture["--captura=".Length..], OS.GetCmdlineUserArgs().Contains("--arrastrar"));
    }

    public override void _ExitTree() => _table.Changed -= RefreshSeats;

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
        _map.TokenMoved += (id, feet) => Say(_table.FindToken(id)!.Name, $"se mueve {feet} pies.");
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
        row.AddChild(Ui.Text(_table.SceneName, 15, Ui.Parchment));
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        row.AddChild(Ui.Text("Fase 0 · un solo PC, sin máster todavía", 14, Ui.Parchment));
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
        if (_log.GetParsedText().Length > 0) _log.AppendText("\n\n");
        _log.AppendText($"[b]{Escape(who)}[/b]  {Escape(text)}");
    }

    /// <summary>Lo que escribe un jugador se muestra tal cual, nunca como formato.</summary>
    private static string Escape(string text) => text.Replace("[", "[lb]");

    // ---------- Captura para revisar el aspecto sin abrir el editor ----------

    private async Task CaptureAndQuit(string path, bool drag)
    {
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
