using Godot;

namespace RoleDMFable;

/// <summary>Colores, tipografías y cajas del estilo de la maqueta: trazo grueso y colores planos.</summary>
public static class Ui
{
    public static readonly Color Ink = new("2b2118");
    public static readonly Color Wood = new("8a5a3c");
    public static readonly Color WoodDark = new("6f4630");
    public static readonly Color Parchment = new("f6ead2");
    public static readonly Color Cream = new("fff8e7");
    public static readonly Color Gold = new("f2b632");
    public static readonly Color PaleGold = new("fff3c4");
    public static readonly Color Red = new("b8371f");
    public static readonly Color HpRed = new("c0392b");
    public static readonly Color Light = new("fff3d6");

    private static FontFile? _baloo;
    private static FontFile? _nunito;

    /// <summary>Baloo 2, para títulos y números.</summary>
    public static Font Title(int weight = 800) =>
        Weighted(_baloo ??= GD.Load<FontFile>("res://assets/fonts/Baloo2.ttf"), weight);

    /// <summary>Nunito, para el texto corriente.</summary>
    public static Font Body(int weight = 700) =>
        Weighted(_nunito ??= GD.Load<FontFile>("res://assets/fonts/Nunito.ttf"), weight);

    private static Font Weighted(FontFile file, int weight)
    {
        var tag = TextServerManager.GetPrimaryInterface().NameToTag("wght");
        return new FontVariation
        {
            BaseFont = file,
            VariationOpentype = new Godot.Collections.Dictionary { { tag, weight } },
        };
    }

    /// <summary>Caja con borde grueso y la base más gruesa, como una pegatina.</summary>
    public static StyleBoxFlat Box(Color background, int radius = 14, int border = 3, int lift = 3, int padding = 12)
    {
        var box = new StyleBoxFlat { BgColor = background, BorderColor = Ink, AntiAliasing = true };
        box.SetBorderWidthAll(border);
        box.BorderWidthBottom = border + lift;
        box.SetCornerRadiusAll(radius);
        box.SetContentMarginAll(padding);
        box.ContentMarginBottom = padding + lift;
        return box;
    }

    public static Label Text(string text, int size = 14, Color? color = null, bool title = false, int weight = 700)
    {
        var label = new Label { Text = text };
        label.AddThemeFontOverride("font", title ? Title(weight) : Body(weight));
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color ?? Ink);
        return label;
    }

    public static Button Button(string text, Color? background = null, int size = 15, Color? textColor = null)
    {
        var bg = background ?? Cream;
        var fg = textColor ?? Ink;
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(48, 48),
            FocusMode = Control.FocusModeEnum.All,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        button.AddThemeFontOverride("font", Body(800));
        button.AddThemeFontSizeOverride("font_size", size);
        foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color", "font_hover_pressed_color" })
            button.AddThemeColorOverride(state, fg);
        button.AddThemeStyleboxOverride("normal", Box(bg, 12, 3, 3, 8));
        button.AddThemeStyleboxOverride("hover", Box(bg.Lightened(0.12f), 12, 3, 3, 8));
        var pressed = Box(bg.Darkened(0.08f), 12, 3, 0, 8);
        pressed.ContentMarginTop = 11;
        pressed.ExpandMarginTop = -3;
        button.AddThemeStyleboxOverride("pressed", pressed);
        var focus = new StyleBoxFlat { DrawCenter = false, BorderColor = Gold.Darkened(0.2f) };
        focus.SetBorderWidthAll(3);
        focus.SetCornerRadiusAll(14);
        focus.SetExpandMarginAll(3);
        button.AddThemeStyleboxOverride("focus", focus);
        return button;
    }

    public static PanelContainer Panel(Color background, int radius = 14, int padding = 12, int lift = 3)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", Box(background, radius, 3, lift, padding));
        return panel;
    }

    public static LineEdit Input(string placeholder)
    {
        var input = new LineEdit { PlaceholderText = placeholder, CustomMinimumSize = new Vector2(0, 48) };
        input.AddThemeFontOverride("font", Body(600));
        input.AddThemeFontSizeOverride("font_size", 15);
        input.AddThemeColorOverride("font_color", Ink);
        input.AddThemeColorOverride("font_placeholder_color", new Color(Ink, 0.6f));
        input.AddThemeColorOverride("caret_color", Ink);
        input.AddThemeStyleboxOverride("normal", Box(Colors.White, 12, 3, 0, 10));
        var focus = Box(Colors.White, 12, 3, 0, 10);
        focus.BorderColor = Gold.Darkened(0.25f);
        input.AddThemeStyleboxOverride("focus", focus);
        return input;
    }
}
