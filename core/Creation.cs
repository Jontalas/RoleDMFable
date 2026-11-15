using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoleDMFable.Core;

/// <summary>Qué se le pide al jugador en un paso de la creación.</summary>
public enum StepKind
{
    /// <summary>Elegir una de varias opciones.</summary>
    Choice,
    /// <summary>Tirar dados: una tirada por cada dato de <see cref="CreationStep.Targets"/>.</summary>
    Roll,
    /// <summary>Escribir un texto libre.</summary>
    Text,
    /// <summary>Poner un número entre un mínimo y un máximo.</summary>
    Number,
}

public sealed record CreationOption(string Label, string Detail = "");

/// <summary>
/// Un paso de la creación guiada. Su respuesta se apunta en la ficha como una o varias líneas nombre/valor.
/// </summary>
/// <param name="Entry">Nombre de la línea de ficha; si se deja vacío se usa el título.</param>
/// <param name="Targets">Solo en tiradas: los datos que reciben una tirada cada uno (Fuerza, Destreza…). Vacío: uno solo, <paramref name="Entry"/>.</param>
/// <param name="Hp">El resultado son los puntos de vida máximos del personaje.</param>
/// <param name="When">Si no está vacío, el paso solo aparece cuando el dato con ese nombre (de una elección anterior) vale alguno de <paramref name="WhenValues"/>.</param>
public sealed record CreationStep(
    string Title,
    StepKind Kind,
    string Help = "",
    string Entry = "",
    IReadOnlyList<CreationOption>? Options = null,
    string Dice = "",
    IReadOnlyList<string>? Targets = null,
    int Min = 1,
    int Max = 999,
    bool Optional = false,
    bool Hp = false,
    string When = "",
    IReadOnlyList<string>? WhenValues = null)
{
    [JsonIgnore] public string EntryName => Entry.Trim().Length > 0 ? Entry.Trim() : Title.Trim();

    /// <summary>Los datos de la ficha que rellena este paso.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EntryNames =>
        Kind == StepKind.Roll && Targets is { Count: > 0 } ? Targets : new[] { EntryName };
}

/// <summary>
/// Los pasos para crear un personaje en un juego concreto. Son datos: los puede traer el juego
/// o prepararse a partir de un reglamento, y el asistente los recorre igual.
/// </summary>
public sealed record CreationGuide(string Title, IReadOnlyList<CreationStep> Steps, string Source = "")
{
    public const int MaxSteps = 40;
    public const int MaxOptions = 40;
    public const int MaxTargets = 12;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>La creación breve que se usa cuando no hay una guía del reglamento.</summary>
    public static CreationGuide Basic { get; } = new("Creación breve", new[]
    {
        new CreationStep("Concepto", StepKind.Text, "Una frase que lo resuma: de dónde viene y qué busca."),
        new CreationStep("Puntos fuertes", StepKind.Text, "Tres cosas que se le dan bien, separadas por comas."),
        new CreationStep("Punto débil", StepKind.Text, "Algo que le cuesta o le mete en líos."),
        new CreationStep("Equipo", StepKind.Text, "Lo que lleva encima: armas, herramientas, algo especial.", Optional: true),
        new CreationStep("Puntos de vida", StepKind.Number, "Cuánto aguanta. Si no sabes qué poner, deja 10.", Min: 1, Max: CampaignSetup.MaxHeroHp, Hp: true),
    });

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Lee una guía y la revisa. Lanza <see cref="FormatException"/> si no se puede seguir.</summary>
    public static CreationGuide FromJson(string json)
    {
        CreationGuide? guide;
        try
        {
            guide = JsonSerializer.Deserialize<CreationGuide>(json, Json);
        }
        catch (JsonException e)
        {
            throw new FormatException("La guía de creación está dañada.", e);
        }
        if (guide is null || guide.Steps is null) throw new FormatException("La guía de creación está vacía.");
        return guide.Checked();
    }

    /// <summary>Devuelve la guía limpia, o lanza <see cref="FormatException"/> diciendo qué paso no vale.</summary>
    public CreationGuide Checked()
    {
        if (Steps.Count == 0) throw new FormatException("La guía de creación no tiene pasos.");
        if (Steps.Count > MaxSteps) throw new FormatException($"La guía de creación tiene más de {MaxSteps} pasos.");
        var steps = new List<CreationStep>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conditional = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var choices = new Dictionary<string, IReadOnlyList<CreationOption>>(StringComparer.OrdinalIgnoreCase);
        var hp = false;
        foreach (var raw in Steps)
        {
            var title = Clean(raw.Title, TableState.MaxSheetNameLength);
            if (title.Length == 0) throw new FormatException($"El paso {steps.Count + 1} de la guía no tiene título.");
            var step = raw with
            {
                Title = title,
                Help = Clean(raw.Help, 600),
                Entry = Clean(raw.Entry, TableState.MaxSheetNameLength),
                Dice = (raw.Dice ?? "").Trim(),
                Options = null,
                Targets = null,
                When = Clean(raw.When, TableState.MaxSheetNameLength),
                WhenValues = null,
            };
            if (step.When.Length > 0)
            {
                if (!choices.TryGetValue(step.When, out var source))
                    throw new FormatException($"El paso «{title}» depende de «{step.When}», que no es una elección anterior.");
                var values = (raw.WhenValues ?? Array.Empty<string>()).Select(v => Clean(v, 60))
                    .Select(v => source.FirstOrDefault(o => string.Equals(o.Label, v, StringComparison.OrdinalIgnoreCase))?.Label)
                    .Where(v => v is not null).Select(v => v!).Distinct().ToList();
                if (values.Count == 0) throw new FormatException($"El paso «{title}» depende de «{step.When}» pero no dice con qué opciones aparece.");
                step = step with { WhenValues = values };
            }
            switch (step.Kind)
            {
                case StepKind.Choice:
                    var options = (raw.Options ?? Array.Empty<CreationOption>())
                        .Select(o => new CreationOption(Clean(o.Label, 60), Clean(o.Detail, 300)))
                        .Where(o => o.Label.Length > 0).DistinctBy(o => o.Label, StringComparer.OrdinalIgnoreCase).ToList();
                    if (options.Count < 2) throw new FormatException($"El paso «{title}» necesita al menos dos opciones.");
                    if (options.Count > MaxOptions) throw new FormatException($"El paso «{title}» tiene más de {MaxOptions} opciones.");
                    step = step with { Options = options };
                    break;
                case StepKind.Roll:
                    if (!DiceExpression.TryParse(step.Dice, out _, out var error))
                        throw new FormatException($"El paso «{title}» no dice bien qué dados tirar: {error}");
                    var targets = (raw.Targets ?? Array.Empty<string>()).Select(t => Clean(t, TableState.MaxSheetNameLength)).Where(t => t.Length > 0).ToList();
                    if (targets.Count > MaxTargets) throw new FormatException($"El paso «{title}» tira para más de {MaxTargets} datos.");
                    if (targets.Count > 0) step = step with { Targets = targets };
                    break;
                case StepKind.Number:
                    if (step.Min > step.Max) throw new FormatException($"El paso «{title}» tiene el mínimo por encima del máximo.");
                    break;
                case StepKind.Text:
                    break;
                default:
                    throw new FormatException($"El paso «{title}» es de un tipo que no existe.");
            }
            if (step.Hp)
            {
                if (hp && step.When.Length == 0) throw new FormatException("Solo un paso puede fijar los puntos de vida, salvo que dependan de una elección.");
                if (step.Kind is not (StepKind.Roll or StepKind.Number) || step.EntryNames.Count != 1)
                    throw new FormatException($"El paso «{title}» no puede fijar los puntos de vida: tiene que dar un solo número.");
                hp = hp || step.When.Length == 0;
            }
            // Dos pasos pueden apuntar el mismo dato solo si ambos dependen de una elección (uno por opción).
            foreach (var name in step.EntryNames)
            {
                var repeated = step.When.Length > 0 ? names.Contains(name) && !conditional.Contains(name) : names.Contains(name);
                if (repeated) throw new FormatException($"Dos pasos de la guía apuntan el mismo dato: «{name}».");
                names.Add(name);
                if (step.When.Length > 0) conditional.Add(name);
            }
            if (step.Kind == StepKind.Choice && step.When.Length == 0) choices[step.EntryName] = step.Options!;
            steps.Add(step);
        }
        return new CreationGuide(Clean(Title, 60) is { Length: > 0 } t ? t : "Creación de personaje", steps, Clean(Source, 120));
    }

    private static string Clean(string? text, int max)
    {
        var clean = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > max ? clean[..max].TrimEnd() : clean;
    }
}

/// <summary>
/// Una creación de personaje en marcha: guarda lo que el jugador va respondiendo en cada paso
/// y al final arma el personaje. Los dados los tira <c>core</c>, una sola vez por paso.
/// </summary>
public sealed class CreationRun
{
    private readonly IRandomSource _random;
    private readonly Dictionary<int, IReadOnlyList<SheetEntry>> _answers = new();
    private readonly Dictionary<int, IReadOnlyList<DiceResult>> _rolls = new();

    public CreationGuide Guide { get; }
    public string Name { get; private set; } = "";
    public string Role { get; private set; } = "";
    public string Icon { get; private set; } = "";

    public CreationRun(CreationGuide guide, IRandomSource random)
    {
        Guide = guide.Checked();
        _random = random;
    }

    /// <summary>Quién es: nombre, qué es y aspecto de la figurita. Lanza <see cref="FormatException"/> si no vale.</summary>
    public void SetIdentity(string name, string role, string icon, Func<string, bool>? taken = null)
    {
        var clean = name.Trim();
        if (clean.Length == 0) throw new FormatException("El personaje necesita un nombre.");
        if (clean.Length > CampaignSetup.MaxNameLength) clean = clean[..CampaignSetup.MaxNameLength].TrimEnd();
        if (CampaignSetup.Looks.All(l => l.Icon != icon)) throw new FormatException("Ese aspecto no vale.");
        if (taken is not null && taken(CampaignSetup.HeroId(clean)))
            throw new FormatException($"Ya tienes un personaje llamado «{clean}». Ponle otro nombre.");
        var what = role.Trim();
        (Name, Role, Icon) = (clean, what.Length > 40 ? what[..40].TrimEnd() : what, icon);
    }

    public bool HasIdentity => Name.Length > 0;

    /// <summary>Lo apuntado en un paso, o vacío si aún no se ha respondido.</summary>
    public IReadOnlyList<SheetEntry> Answer(int step) => _answers.TryGetValue(step, out var entries) ? entries : Array.Empty<SheetEntry>();

    /// <summary>Las tiradas de un paso, en el orden de sus datos. Vacío si aún no se ha tirado.</summary>
    public IReadOnlyList<DiceResult> Rolls(int step) => _rolls.TryGetValue(step, out var rolls) ? rolls : Array.Empty<DiceResult>();

    /// <summary>Si el paso toca con lo elegido hasta ahora. Los que dependen de otra elección pueden no aparecer.</summary>
    public bool IsActive(int step)
    {
        var s = Step(step);
        if (s.When.Length == 0) return true;
        for (var i = 0; i < step; i++)
            if (Guide.Steps[i] is { Kind: StepKind.Choice, When: "" } source && string.Equals(source.EntryName, s.When, StringComparison.OrdinalIgnoreCase))
                return Answer(i) is [var chosen] && s.WhenValues!.Contains(chosen.Value);
        return false;
    }

    public bool IsDone(int step) => !IsActive(step) || _answers.ContainsKey(step) || Step(step).Optional;

    /// <summary>El primer paso obligatorio sin responder, o -1 si están todos.</summary>
    public int FirstPending => Enumerable.Range(0, Guide.Steps.Count).Where(i => !IsDone(i)).DefaultIfEmpty(-1).First();

    public void Choose(int step, int option)
    {
        var s = Step(step, StepKind.Choice);
        if (option < 0 || option >= s.Options!.Count) throw new ArgumentException("Esa opción no existe.");
        _answers[step] = new[] { new SheetEntry(s.EntryName, s.Options[option].Label) };
    }

    /// <summary>La opción elegida en un paso de elección, o -1.</summary>
    public int Chosen(int step) =>
        Answer(step) is [var entry] && Step(step).Options is { } options ? options.ToList().FindIndex(o => o.Label == entry.Value) : -1;

    /// <summary>Tira los dados del paso. Solo vale la primera vez: repetir devuelve la misma tirada.</summary>
    public IReadOnlyList<DiceResult> Roll(int step)
    {
        var s = Step(step, StepKind.Roll);
        if (_rolls.TryGetValue(step, out var before)) return before;
        var dice = DiceExpression.Parse(s.Dice);
        var rolls = s.EntryNames.Select(_ => dice.Roll(_random)).ToList();
        _rolls[step] = rolls;
        _answers[step] = s.EntryNames.Zip(rolls, (name, roll) => new SheetEntry(name, roll.Total.ToString())).ToList();
        return rolls;
    }

    /// <summary>Apunta un texto. Vacío borra la respuesta.</summary>
    public void Write(int step, string text)
    {
        var s = Step(step, StepKind.Text);
        var clean = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > TableState.MaxSheetValueLength) clean = clean[..TableState.MaxSheetValueLength].TrimEnd();
        if (clean.Length == 0) _answers.Remove(step);
        else _answers[step] = new[] { new SheetEntry(s.EntryName, clean) };
    }

    public void SetNumber(int step, int value)
    {
        var s = Step(step, StepKind.Number);
        if (value < s.Min || value > s.Max) throw new ArgumentException($"«{s.Title}» tiene que estar entre {s.Min} y {s.Max}.");
        _answers[step] = new[] { new SheetEntry(s.EntryName, value.ToString()) };
    }

    /// <summary>La ficha tal como va, en el orden de los pasos.</summary>
    public IReadOnlyList<SheetEntry> Sheet =>
        Enumerable.Range(0, Guide.Steps.Count).Where(IsActive).SelectMany(Answer).Take(TableState.MaxSheetEntries).ToList();

    /// <summary>Los puntos de vida máximos: los del paso que los fija, o 10 si la guía no tiene ninguno.</summary>
    public int MaxHp
    {
        get
        {
            for (var i = 0; i < Guide.Steps.Count; i++)
                if (Guide.Steps[i].Hp && IsActive(i) && Answer(i) is [var entry] && int.TryParse(entry.Value, out var hp))
                    return Math.Clamp(hp, 1, TableState.MaxHpLimit);
            return 10;
        }
    }

    /// <summary>
    /// Arma el personaje terminado. Lanza <see cref="FormatException"/> si falta el nombre o algún paso obligatorio.
    /// </summary>
    public SavedHero Build(string color = "#3f6fbf")
    {
        if (!HasIdentity) throw new FormatException("El personaje necesita un nombre.");
        if (FirstPending is >= 0 and var pending) throw new FormatException($"Falta el paso «{Guide.Steps[pending].Title}».");
        return new SavedHero(CampaignSetup.HeroId(Name), Name, Role, Icon, color, MaxHp, Sheet, "");
    }

    private CreationStep Step(int step, StepKind? kind = null)
    {
        if (step < 0 || step >= Guide.Steps.Count) throw new ArgumentException("Ese paso no existe.");
        var s = Guide.Steps[step];
        if (kind is not null && s.Kind != kind) throw new ArgumentException($"El paso «{s.Title}» no se responde así.");
        return s;
    }
}
