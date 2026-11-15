using Godot;
using RoleDMFable.Core;
using RoleDMFable.Master;

namespace RoleDMFable;

/// <summary>El menú principal, la pantalla de personajes y el asistente de creación por pasos.</summary>
public partial class Main
{
    private const string GuidePath = "user://guia_creacion.json";

    private Control _menu = null!;
    private VBoxContainer _menuButtons = null!;
    private Label _menuStatus = null!;
    private CreationGuide? _guide;
    private bool _guideBusy;
    private AcceptDialog? _heroesDialog;

    // ---------- Menú principal ----------

    private void BuildMenu()
    {
        _menu = new Control { Visible = false };
        _menu.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var back = new ColorRect { Color = Ui.WoodDark };
        back.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _menu.AddChild(back);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _menu.AddChild(center);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        center.AddChild(column);

        var title = Ui.Text("RoleDMFable", 68, Ui.Gold, title: true);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(title);
        var subtitle = Ui.Text("Tu mesa de rol, con máster incluido", 18, Ui.Parchment);
        subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(subtitle);
        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 18) });

        var panel = Ui.Panel(Ui.Parchment, 18, 22, 5);
        panel.CustomMinimumSize = new Vector2(420, 0);
        column.AddChild(panel);
        _menuButtons = new VBoxContainer();
        _menuButtons.AddThemeConstantOverride("separation", 10);
        panel.AddChild(_menuButtons);

        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });
        _menuStatus = Ui.Text("", 14, Ui.Parchment, weight: 600);
        _menuStatus.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(_menuStatus);
        AddChild(_menu);
    }

    private void ShowMenu()
    {
        if (_saveCountdown >= 0) SaveNow();
        RefreshMenu();
        _menu.Visible = true;
    }

    private void HideMenu() => _menu.Visible = false;

    private void RefreshMenu()
    {
        foreach (var child in _menuButtons.GetChildren())
        {
            _menuButtons.RemoveChild(child);
            child.QueueFree();
        }

        void Add(string text, Action pressed, Color? color = null, string tooltip = "")
        {
            var button = Ui.Button(text, color ?? Ui.Cream, 18);
            button.CustomMinimumSize = new Vector2(0, 56);
            button.TooltipText = tooltip;
            button.Pressed += pressed;
            _menuButtons.AddChild(button);
        }

        var heroes = _heroes.LoadAll().Count;
        int games;
        try { games = _games.List().Count; }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { games = 0; }

        if (_gameId.Length > 0) Add($"Continuar «{_game.Name}»", HideMenu, Ui.Gold);
        Add("Nueva partida", () => AskNewGame(), _gameId.Length > 0 ? null : Ui.Gold);
        if (games > (_gameId.Length > 0 ? 1 : 0)) Add("Cargar partida", OpenGames);
        Add("Personajes", OpenHeroes, tooltip: "Crear personajes y ver los que ya tienes");
        Add("Reglamentos", OpenRules, tooltip: "Cargar las reglas de tu juego desde PDF, Word o TXT");
        Add("Máster IA", OpenSettings, tooltip: "Clave y modelo del máster");
        Add("Salir", () =>
        {
            if (_saveCountdown >= 0) SaveNow();
            GetTree().Quit();
        });

        var rules = _rules.BooksOf(BookKind.Rules).Count();
        _menuStatus.Text = string.Join(" · ", new[]
        {
            heroes == 1 ? "1 personaje" : $"{heroes} personajes",
            games == 1 ? "1 partida" : $"{games} partidas",
            rules == 0 ? "sin reglamento" : rules == 1 ? "1 reglamento" : $"{rules} reglamentos",
            HasMaster ? $"máster: {CurrentModel.Name}" : "máster sin conectar",
        });
    }

    // ---------- Guía de creación ----------

    private void LoadGuide()
    {
        _guide = null;
        if (_ephemeral || !Godot.FileAccess.FileExists(GuidePath)) return;
        try
        {
            _guide = CreationGuide.FromJson(Godot.FileAccess.GetFileAsString(GuidePath));
        }
        catch (FormatException e)
        {
            GD.PushWarning($"La guía de creación guardada no vale: {e.Message}");
        }
    }

    private async Task PrepareGuide()
    {
        if (_guideBusy || !HasMaster) return;
        _guideBusy = true;
        ReopenHeroes();
        string? problem = null;
        try
        {
            var master = new ClaudeMaster(_apiKey, CurrentModel.Id, _apiUrl);
            var (guide, usage) = await master.BuildCreationGuideAsync(_rules);
            _sessionCost += CurrentModel.Cost(usage);
            RefreshMasterStatus();
            _guide = guide;
            if (!_ephemeral)
            {
                using var file = Godot.FileAccess.Open(GuidePath, Godot.FileAccess.ModeFlags.Write);
                if (file is null) problem = $"La guía está lista pero no se pudo guardar: {Godot.FileAccess.GetOpenError()}";
                else file.StoreString(guide.ToJson());
            }
        }
        catch (MasterException e)
        {
            problem = e.Message;
        }
        catch (Exception e)
        {
            GD.PushError(e.ToString());
            problem = $"No se pudo preparar la guía: {e.Message}";
        }
        _guideBusy = false;
        ReopenHeroes(problem);
    }

    private void ReopenHeroes(string? problem = null)
    {
        if (_heroesDialog is null || !IsInstanceValid(_heroesDialog) || !_heroesDialog.Visible) return;
        _heroesDialog.Hide();
        OpenHeroes(problem);
    }

    // ---------- Personajes ----------

    private void OpenHeroes() => OpenHeroes(null);

    private void OpenHeroes(string? notice)
    {
        var dialog = new AcceptDialog { Title = "Personajes", OkButtonText = "Cerrar", MinSize = new Vector2I(820, 0) };
        dialog.AddButton("Crear personaje", true, "crear");
        _heroesDialog = dialog;

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        dialog.AddChild(column);

        // De dónde salen los pasos de la creación.
        var source = CreationGuidePrompt.SourceOf(_rules);
        var guideCard = Ui.Panel(Ui.PaleGold, radius: 12, padding: 10, lift: 2);
        var guideRow = new HBoxContainer();
        guideRow.AddThemeConstantOverride("separation", 10);
        guideCard.AddChild(guideRow);
        var guideText = DialogText("", 700);
        guideText.CustomMinimumSize = new Vector2(420, 0);
        guideText.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        guideRow.AddChild(guideText);
        var prepare = Ui.Button(_guide is null ? "Preparar guía" : "Volver a prepararla", Ui.Gold, 14);
        prepare.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        prepare.Pressed += () => _ = PrepareGuide();
        if (_guideBusy)
        {
            guideText.Text = "El máster está leyendo el reglamento para preparar la guía de creación. Puede tardar un minuto.";
        }
        else if (_guide is not null)
        {
            var steps = _guide.Steps.Count == 1 ? "1 paso" : $"{_guide.Steps.Count} pasos";
            guideText.Text = $"Los personajes se crean siguiendo «{_guide.Title}» ({steps})."
                + (source.Length > 0 && source != _guide.Source ? " Los reglamentos han cambiado desde que se preparó: conviene volver a prepararla." : "");
            guideRow.AddChild(prepare);
        }
        else if (source.Length == 0)
        {
            guideText.Text = "No hay reglamento cargado, así que los personajes se crean con una creación breve. Carga tu manual en «Reglamentos» para crearlos según sus reglas.";
        }
        else
        {
            guideText.Text = "Hay reglamento cargado. El máster lo lee una sola vez para preparar los pasos de creación (gasta una consulta) y desde entonces los personajes se crean por pasos, sin conversación."
                + (HasMaster ? "" : " Antes conecta el máster en «Máster IA».");
            prepare.Disabled = !HasMaster;
            guideRow.AddChild(prepare);
        }
        column.AddChild(guideCard);

        var problem = DialogText(notice ?? "", 800);
        problem.AddThemeColorOverride("font_color", Ui.Red);

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 340), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(list);
        column.AddChild(scroll);
        column.AddChild(problem);

        var saved = _heroes.LoadAll();
        if (saved.Count == 0) list.AddChild(DialogText("Todavía no tienes personajes. Pulsa «Crear personaje» para hacer el primero.", 700));
        foreach (var hero in saved)
        {
            var card = Ui.Panel(Ui.Cream, radius: 12, padding: 10, lift: 2);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            card.AddChild(row);
            row.AddChild(Dot(new Color(hero.Color), 30));
            var texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            texts.AddThemeConstantOverride("separation", 0);
            row.AddChild(texts);
            var name = Ui.Text(hero.Name, 19, Ui.Ink, title: true);
            name.ClipText = true;
            texts.AddChild(name);
            var parts = new List<string>();
            if (hero.Role.Length > 0) parts.Add(hero.Role);
            parts.Add($"PV {hero.MaxHp}");
            parts.Add(hero.LastAdventure.Length > 0 ? $"última aventura: {hero.LastAdventure}" : "sin estrenar");
            var details = Ui.Text(string.Join(" · ", parts), 13, Ui.Ink, weight: 600);
            details.ClipText = true;
            texts.AddChild(details);

            var sheet = Ui.Button("Ficha", Ui.Parchment, 14);
            sheet.Pressed += () =>
            {
                dialog.Hide();
                OpenHeroSheet(hero);
            };
            row.AddChild(sheet);
            var forget = Ui.Button("Borrar", Ui.Parchment, 13);
            forget.TooltipText = "Borra este personaje para siempre";
            forget.Pressed += () =>
            {
                // Dos pulsaciones: borrar un personaje no tiene vuelta atrás.
                if (forget.Text != "¿Seguro?")
                {
                    forget.Text = "¿Seguro?";
                    return;
                }
                _heroes.Delete(hero.Id);
                list.RemoveChild(card);
                card.QueueFree();
                RefreshMenu();
            };
            row.AddChild(forget);
            list.AddChild(card);
        }

        dialog.CustomAction += action =>
        {
            if (action != "crear") return;
            dialog.Hide();
            OpenCreation(OpenHeroes);
        };
        dialog.VisibilityChanged += () =>
        {
            if (dialog.Visible) return;
            if (_heroesDialog == dialog) _heroesDialog = null;
            dialog.QueueFree();
        };
        AddChild(dialog);
        dialog.PopupCentered();
    }

    /// <summary>La ficha de un personaje guardado, para verla y retocarla a mano.</summary>
    private void OpenHeroSheet(SavedHero hero)
    {
        var dialog = new AcceptDialog { Title = $"Ficha de {hero.Name}", OkButtonText = "Guardar", MinSize = new Vector2I(560, 0) };
        dialog.AddCancelButton("Cancelar");
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        dialog.AddChild(column);
        if (hero.Role.Length > 0) column.AddChild(DialogText(hero.Role, 800));

        var hpRow = new HBoxContainer();
        hpRow.AddThemeConstantOverride("separation", 10);
        hpRow.AddChild(Ui.Text("Puntos de vida máximos", 15, Ui.Ink, weight: 800));
        var hp = new SpinBox { MinValue = 1, MaxValue = TableState.MaxHpLimit, Value = Math.Clamp(hero.MaxHp, 1, TableState.MaxHpLimit), CustomMinimumSize = new Vector2(120, 44) };
        hpRow.AddChild(hp);
        column.AddChild(hpRow);

        column.AddChild(DialogText("Un dato por línea, con el nombre, dos puntos y el valor. Por ejemplo «Fuerza: 16».", 600));
        var editor = new TextEdit
        {
            Text = string.Join("\n", (hero.Sheet ?? Array.Empty<SheetEntry>()).Select(e => $"{e.Name}: {e.Value}")),
            CustomMinimumSize = new Vector2(520, 320),
        };
        editor.AddThemeFontOverride("font", Ui.Body(600));
        editor.AddThemeFontSizeOverride("font_size", 15);
        column.AddChild(editor);

        dialog.Confirmed += () =>
        {
            var entries = editor.Text.Split('\n')
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2 && parts[0].Trim().Length > 0 && parts[1].Trim().Length > 0)
                .Select(parts => new SheetEntry(parts[0].Trim(), parts[1].Trim()))
                .Take(TableState.MaxSheetEntries).ToList();
            try
            {
                _heroes.Save(hero with { Sheet = entries, MaxHp = (int)hp.Value });
                // Si está en la partida abierta, su figurita se pone al día.
                _heroes.ApplyTo(_game);
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            {
                GD.PushError($"No se pudo guardar el personaje: {e.Message}");
            }
        };
        dialog.VisibilityChanged += () =>
        {
            if (dialog.Visible) return;
            dialog.QueueFree();
            Callable.From(OpenHeroes).CallDeferred();
        };
        AddChild(dialog);
        dialog.PopupCentered();
    }

    // ---------- Asistente de creación ----------

    /// <summary>
    /// Crea un personaje paso a paso: quién es, y luego los pasos de la guía del reglamento
    /// (o la creación breve si no hay guía). Al terminar queda guardado y se llama a <paramref name="done"/>.
    /// </summary>
    private void OpenCreation(Action? done, int startPage = 0)
    {
        var run = new CreationRun(_guide ?? CreationGuide.Basic, new SystemRandomSource());
        var steps = run.Guide.Steps;
        var dialog = new AcceptDialog { Title = "Crear personaje", OkButtonText = "Siguiente", MinSize = new Vector2I(760, 0), DialogHideOnOk = false };
        dialog.AddCancelButton("Cancelar");
        var back = dialog.AddButton("Atrás", false, "atras");

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        dialog.AddChild(column);
        var progress = Ui.Text("", 13, Ui.Wood, weight: 800);
        column.AddChild(progress);
        var heading = Ui.Text("", 28, Ui.Ink, title: true);
        column.AddChild(heading);
        var help = DialogText("", 600);
        column.AddChild(help);
        var body = new VBoxContainer { CustomMinimumSize = new Vector2(700, 330) };
        body.AddThemeConstantOverride("separation", 8);
        column.AddChild(body);
        var problem = DialogText("", 800);
        problem.AddThemeColorOverride("font_color", Ui.Red);
        column.AddChild(problem);

        // Página -1: quién es. De 0 a n-1: los pasos de la guía. n: el repaso final.
        var page = -1;
        Func<bool> commit = () => true;

        int Next(int from, int direction)
        {
            var next = from + direction;
            while (next >= 0 && next < steps.Count && !run.IsActive(next)) next += direction;
            return Math.Clamp(next, -1, steps.Count);
        }

        void Render()
        {
            foreach (var child in body.GetChildren())
            {
                body.RemoveChild(child);
                child.QueueFree();
            }
            problem.Text = "";
            var active = Enumerable.Range(0, steps.Count).Where(run.IsActive).ToList();
            var position = page < 0 ? 1 : page >= steps.Count ? active.Count + 2 : active.IndexOf(page) + 2;
            progress.Text = $"PASO {position} DE {active.Count + 2} · {run.Guide.Title.ToUpperInvariant()}";
            back.Disabled = page < 0;
            dialog.GetOkButton().Text = page >= steps.Count ? "Crear personaje" : "Siguiente";

            if (page < 0)
            {
                heading.Text = "¿Quién es?";
                help.Text = "Su nombre, qué es en pocas palabras y cómo se verá su figurita en el mapa.";
                body.AddChild(DialogText("Nombre", 800));
                var name = Ui.Input("Ánika la Roja");
                name.MaxLength = CampaignSetup.MaxNameLength;
                name.Text = run.Name;
                body.AddChild(name);
                body.AddChild(DialogText("Qué es", 800));
                var role = Ui.Input("Bárbara del norte, elfa exploradora, mago curioso…");
                role.MaxLength = 40;
                role.Text = run.Role;
                body.AddChild(role);
                body.AddChild(DialogText("Aspecto de la figurita", 800));
                var look = new OptionButton { CustomMinimumSize = new Vector2(220, 48) };
                look.AddThemeFontOverride("font", Ui.Body(700));
                foreach (var (label, icon) in CampaignSetup.Looks)
                {
                    look.AddItem(label);
                    if (run.HasIdentity && run.Icon == icon) look.Selected = look.ItemCount - 1;
                }
                body.AddChild(look);
                commit = () =>
                {
                    run.SetIdentity(name.Text, role.Text, CampaignSetup.Looks[Math.Max(0, look.Selected)].Icon, _heroes.Exists);
                    return true;
                };
                Callable.From(() => { if (IsInstanceValid(name) && name.IsInsideTree()) name.GrabFocus(); }).CallDeferred();
                return;
            }

            if (page >= steps.Count)
            {
                heading.Text = run.Name;
                help.Text = (run.Role.Length > 0 ? run.Role + ". " : "") + "Repasa la ficha. Si algo no te convence, vuelve atrás; si está bien, crea el personaje.";
                var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 320), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
                var lines = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                lines.AddThemeConstantOverride("separation", 4);
                scroll.AddChild(lines);
                lines.AddChild(DialogText($"Puntos de vida máximos: {run.MaxHp}", 800));
                foreach (var entry in run.Sheet) lines.AddChild(DialogText($"{entry.Name}: {entry.Value}", 600));
                body.AddChild(scroll);
                commit = () => true;
                return;
            }

            var index = page;
            var step = steps[index];
            heading.Text = step.Title;
            help.Text = step.Help + (step.Optional ? (step.Help.Length > 0 ? " " : "") + "Este paso es opcional." : "");
            switch (step.Kind)
            {
                case StepKind.Choice:
                {
                    var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 320), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
                    var options = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    options.AddThemeConstantOverride("separation", 6);
                    scroll.AddChild(options);
                    var group = new ButtonGroup();
                    var chosen = run.Chosen(index);
                    for (var i = 0; i < step.Options!.Count; i++)
                    {
                        var option = step.Options[i];
                        var which = i;
                        var pick = new CheckBox { Text = option.Label, ButtonGroup = group, ButtonPressed = i == chosen };
                        pick.AddThemeFontOverride("font", Ui.Body(800));
                        pick.AddThemeFontSizeOverride("font_size", 17);
                        pick.Pressed += () => run.Choose(index, which);
                        options.AddChild(pick);
                        if (option.Detail.Length > 0)
                        {
                            var detail = DialogText(option.Detail, 600);
                            var indent = new MarginContainer();
                            indent.AddThemeConstantOverride("margin_left", 34);
                            indent.AddChild(detail);
                            options.AddChild(indent);
                        }
                    }
                    body.AddChild(scroll);
                    commit = () => run.IsDone(index) ? true : throw new FormatException("Elige una opción para seguir.");
                    break;
                }
                case StepKind.Roll:
                {
                    var roll = Ui.Button($"Tirar {step.Dice}", Ui.Gold, 17);
                    roll.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
                    body.AddChild(roll);
                    var results = new VBoxContainer();
                    results.AddThemeConstantOverride("separation", 4);
                    body.AddChild(results);
                    void ShowRolls()
                    {
                        var rolls = run.Rolls(index);
                        if (rolls.Count == 0) return;
                        roll.Disabled = true;
                        roll.Text = "Dados tirados";
                        foreach (var child in results.GetChildren()) child.QueueFree();
                        for (var i = 0; i < rolls.Count; i++)
                        {
                            var line = new HBoxContainer();
                            line.AddThemeConstantOverride("separation", 12);
                            var name = Ui.Text(step.EntryNames[i], 18, Ui.Ink, weight: 800);
                            name.CustomMinimumSize = new Vector2(220, 0);
                            line.AddChild(name);
                            line.AddChild(Ui.Text(rolls[i].Total.ToString(), 26, Ui.Red, title: true));
                            line.AddChild(Ui.Text(rolls[i].Describe(), 13, Ui.Ink, weight: 600));
                            results.AddChild(line);
                        }
                    }
                    roll.Pressed += () =>
                    {
                        run.Roll(index);
                        ShowRolls();
                    };
                    ShowRolls();
                    commit = () => run.IsDone(index) ? true : throw new FormatException("Tira los dados para seguir.");
                    break;
                }
                case StepKind.Number:
                {
                    var current = run.Answer(index) is [var entry] && int.TryParse(entry.Value, out var before) ? before : Math.Clamp(10, step.Min, step.Max);
                    var number = new SpinBox { MinValue = step.Min, MaxValue = step.Max, Value = current, CustomMinimumSize = new Vector2(160, 48) };
                    number.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
                    body.AddChild(number);
                    body.AddChild(DialogText($"Entre {step.Min} y {step.Max}.", 600));
                    commit = () =>
                    {
                        number.Apply();
                        run.SetNumber(index, (int)number.Value);
                        return true;
                    };
                    break;
                }
                default:
                {
                    var text = new TextEdit
                    {
                        Text = run.Answer(index) is [var entry] ? entry.Value : "",
                        CustomMinimumSize = new Vector2(0, 140),
                        WrapMode = TextEdit.LineWrappingMode.Boundary,
                    };
                    text.AddThemeFontOverride("font", Ui.Body(600));
                    text.AddThemeFontSizeOverride("font_size", 16);
                    body.AddChild(text);
                    commit = () =>
                    {
                        run.Write(index, text.Text);
                        return run.IsDone(index) ? true : throw new FormatException("Escribe algo para seguir.");
                    };
                    Callable.From(() => { if (IsInstanceValid(text) && text.IsInsideTree()) text.GrabFocus(); }).CallDeferred();
                    break;
                }
            }
        }

        dialog.Confirmed += () =>
        {
            try
            {
                commit();
                if (page < steps.Count)
                {
                    page = Next(page, +1);
                    Render();
                    return;
                }
                var hero = run.Build(CampaignSetup.HeroColor(_heroes.LoadAll().Count));
                _heroes.Save(hero);
                dialog.Hide();
            }
            catch (Exception e) when (e is FormatException or ArgumentException or System.IO.IOException or UnauthorizedAccessException)
            {
                problem.Text = e.Message;
            }
        };
        dialog.CustomAction += action =>
        {
            if (action != "atras" || page < 0) return;
            // Lo escrito en esta página se conserva si vale; si no, se vuelve igualmente.
            try { commit(); }
            catch (Exception e) when (e is FormatException or ArgumentException) { }
            page = Next(page, -1);
            Render();
        };
        dialog.VisibilityChanged += () =>
        {
            if (dialog.Visible) return;
            dialog.QueueFree();
            RefreshMenu();
            if (done is not null) Callable.From(done).CallDeferred();
        };
        AddChild(dialog);
        Render();
        // Solo en capturas de revisión: salta a un paso concreto con respuestas de muestra.
        for (var i = 0; i < startPage && page < steps.Count; i++)
        {
            if (page < 0) run.SetIdentity("Ánika la Roja", "Bárbara del norte", "sword");
            else if (steps[page].Kind == StepKind.Choice) run.Choose(page, 0);
            else if (steps[page].Kind == StepKind.Roll) run.Roll(page);
            else if (steps[page].Kind == StepKind.Number) run.SetNumber(page, steps[page].Min);
            else run.Write(page, "De muestra");
            page = Next(page, +1);
            Render();
        }
        dialog.PopupCentered();
    }
}
