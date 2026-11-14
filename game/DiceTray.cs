using Godot;
using RoleDMFable.Core;

namespace RoleDMFable;

/// <summary>Bandeja de dados: botones rápidos, notación libre y el último resultado en grande.</summary>
public partial class DiceTray : PanelContainer
{
    private static readonly int[] Sides = { 4, 6, 8, 10, 12, 20 };

    private readonly IRandomSource _random = new SystemRandomSource();
    private readonly RandomNumberGenerator _shuffle = new();
    private LineEdit _input = null!;
    private Label _total = null!;
    private Label _detail = null!;
    private Tween? _tween;

    /// <summary>Tirada hecha. El resultado ya es definitivo cuando empieza la animación.</summary>
    public event Action<DiceResult>? Rolled;

    public override void _Ready()
    {
        AddThemeStyleboxOverride("panel", Ui.Box(Ui.WoodDark, 14, 3, 3, 12));
        CustomMinimumSize = new Vector2(0, 104);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        AddChild(row);

        foreach (var sides in Sides)
        {
            var button = Ui.Button($"d{sides}", size: 17);
            button.CustomMinimumSize = new Vector2(52, 56);
            button.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            button.TooltipText = $"Tirar 1d{sides}";
            button.AddThemeFontOverride("font", Ui.Title());
            button.Pressed += () => Roll($"1d{sides}");
            row.AddChild(button);
        }

        _input = Ui.Input("2d6+3");
        _input.CustomMinimumSize = new Vector2(116, 48);
        _input.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _input.TooltipText = "Notación libre: 2d6+3, 4d6kh3, 1d20+5-1d4";
        _input.TextSubmitted += text => Roll(text);
        row.AddChild(_input);

        var go = Ui.Button("Tirar", Ui.Gold);
        go.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        go.CustomMinimumSize = new Vector2(76, 52);
        go.Pressed += () => Roll(_input.Text);
        row.AddChild(go);

        var result = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
        result.AddThemeConstantOverride("separation", -6);
        _total = Ui.Text("–", 40, Ui.Light, title: true);
        _total.HorizontalAlignment = HorizontalAlignment.Right;
        _detail = Ui.Text("Pulsa un dado o escribe una tirada", 13, Ui.Light);
        _detail.HorizontalAlignment = HorizontalAlignment.Right;
        _detail.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        result.AddChild(_total);
        result.AddChild(_detail);
        row.AddChild(result);
    }

    public void Roll(string text)
    {
        if (!DiceExpression.TryParse(text, out var expression, out var error))
        {
            _tween?.Kill();
            _total.Text = "?";
            _detail.Text = error;
            return;
        }

        var result = expression!.Roll(_random);
        Rolled?.Invoke(result);

        // La animación solo baraja números a la vista; el resultado no cambia.
        _tween?.Kill();
        _detail.Text = $"Tirando {result.Expression}…";
        var max = Math.Max(2, expression.Terms.Where(t => !t.IsConstant).Sum(t => t.Keep * t.Sides));
        _tween = CreateTween();
        for (var i = 0; i < 8; i++)
        {
            _tween.TweenCallback(Callable.From(() => _total.Text = _shuffle.RandiRange(1, max).ToString()));
            _tween.TweenInterval(0.05);
        }
        _tween.TweenCallback(Callable.From(() =>
        {
            _total.Text = result.Total.ToString();
            _detail.Text = result.Describe();
            _total.PivotOffset = _total.Size / 2f;
            _total.Scale = new Vector2(1.25f, 1.25f);
        }));
        _tween.TweenProperty(_total, "scale", Vector2.One, 0.18).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }
}
