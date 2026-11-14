namespace RoleDMFable.Core;

/// <summary>Cómo de probable es un «sí» antes de preguntar al oráculo.</summary>
public enum Likelihood
{
    VeryUnlikely,
    Unlikely,
    Even,
    Likely,
    VeryLikely,
}

public enum OracleAnswer
{
    /// <summary>No, y además algo empeora.</summary>
    NoAnd,
    No,
    Yes,
    /// <summary>Sí, y además algo mejora.</summary>
    YesAnd,
}

/// <summary>Un giro que nadie había pedido: el azar mete algo nuevo en la historia.</summary>
public sealed record StoryEvent(bool Major, string Tone, string Subject, string Timing, int PointsGained)
{
    public string Describe()
    {
        var size = Major ? "Evento inesperado importante" : "Evento inesperado";
        return $"{size}: {Tone}. {Subject} {Timing}";
    }
}

public sealed record OracleResult(OracleAnswer Answer, string Degree, int D10, int D8, int D6, StoryEvent? Event)
{
    public string AnswerText => Answer switch
    {
        OracleAnswer.NoAnd => "No, y además algo lo empeora",
        OracleAnswer.No => "No",
        OracleAnswer.Yes => "Sí",
        _ => "Sí, y además algo lo mejora",
    };
}

public sealed record NpcReaction(string Attitude, string Personality, string Capability, StoryEvent? Event);

/// <summary>Una cuenta atrás a la vista de todos: cuando llega a cero, pasa lo que amenaza.</summary>
public sealed record Countdown(string Name, int Left, int Total);

/// <summary>Lo que se guarda de la historia junto con la partida.</summary>
public sealed record StorySave(
    int Scene = 1,
    string SceneTitle = "",
    int Points = 0,
    int TurningPoint = StoryState.MediumAdventure,
    bool EventUsedThisScene = false,
    string FinalGoal = "",
    IReadOnlyList<string>? Goals = null,
    IReadOnlyList<string>? Allies = null,
    IReadOnlyList<string>? Enemies = null,
    IReadOnlyList<string>? Notes = null,
    IReadOnlyList<Countdown>? Countdowns = null);

/// <summary>
/// El hilo de la historia: escenas numeradas, objetivos, personajes importantes, cuentas atrás
/// y el oráculo. El azar decide lo que nadie ha fijado, para que ni el máster sepa del todo
/// qué va a pasar; los puntos de historia marcan cuándo la aventura gira hacia su desenlace.
/// </summary>
public sealed class StoryState
{
    public const int ShortAdventure = 15;
    public const int MediumAdventure = 25;
    public const int LongAdventure = 35;
    public const int MaxListEntries = 12;
    public const int MaxTextLength = 160;
    public const int MaxCountdowns = 4;
    public const int MaxCountdownSteps = 12;

    private static readonly string[] Degrees = { "bajo", "medio", "alto" };

    private static readonly string[] Attitudes =
    {
        "violenta", "agresiva", "irritada", "distante", "indiferente", "indiferente", "educada", "agradable", "bondadosa", "afectuosa",
    };

    private static readonly string[] Personalities =
    {
        "reservada y prudente, habla poco",
        "sociable y entusiasta, le gusta la gente",
        "quejosa y descontenta, todo le parece mal",
        "dominante y manipuladora, quiere mandar",
        "apasionada y competitiva, se enciende pronto",
        "desconfiada y asustadiza, ve peligros en todo",
        "serena y de fiar, transmite calma",
        "apática y cansada, nada le importa mucho",
    };

    private static readonly string[] Capabilities =
    {
        "es más capaz que los héroes en lo que importe ahora",
        "está a la altura de los héroes",
        "es menos capaz que los héroes",
    };

    private static readonly string[] SubjectsBefore =
    {
        "Afecta a uno de los héroes.",
        "Afecta a un personaje no jugador presente.",
        "Un personaje no jugador presente hace algo que nadie esperaba.",
        "Entra en la historia un personaje nuevo.",
        "Aparece un objeto o una pista nuevos.",
        "Afecta al objetivo que el grupo persigue ahora.",
        "Afecta al objetivo final de la aventura.",
        "Ocurre algo del todo ajeno a lo que estaba pasando.",
    };

    private static readonly string[] SubjectsAfter =
    {
        "Afecta a uno de los héroes.",
        "Afecta a un personaje no jugador presente.",
        "Un personaje no jugador presente hace algo que nadie esperaba.",
        "Reaparece un personaje que ya había salido.",
        "Aparece un objeto nuevo.",
        "Afecta al objetivo que el grupo persigue ahora.",
        "Afecta al objetivo final: cambia el tiempo o lo que hay en juego.",
        "Giro de guion: algo que se daba por cierto resulta no serlo.",
    };

    private static readonly string[] Verbs =
    {
        "abandonar", "abrir", "acechar", "advertir", "ayudar", "buscar", "cambiar", "capturar", "cerrar", "confundir",
        "construir", "curar", "defender", "delatar", "desafiar", "descubrir", "destruir", "dividir", "engañar", "esconder",
        "escapar", "esperar", "guiar", "heredar", "huir", "imitar", "intercambiar", "invocar", "jurar", "liberar",
        "llamar", "mentir", "negociar", "olvidar", "pagar", "perder", "perseguir", "proteger", "quemar", "recordar",
        "rescatar", "retrasar", "robar", "romper", "seguir", "sellar", "tentar", "traicionar", "unir", "vigilar",
    };

    private static readonly string[] Nouns =
    {
        "aliado", "amenaza", "animal", "arma", "camino", "carta", "castigo", "deuda", "disfraz", "enfermedad",
        "familia", "fiesta", "frontera", "fuego", "herencia", "hogar", "juramento", "ladrón", "llave", "lluvia",
        "mapa", "máscara", "mensaje", "mercado", "miedo", "moneda", "niño", "noche", "nombre", "oficio",
        "orgullo", "pasadizo", "poder", "promesa", "puerta", "recuerdo", "rival", "ruina", "rumor", "sangre",
        "secreto", "señal", "sombra", "sueño", "templo", "tesoro", "testigo", "tormenta", "trampa", "venganza",
    };

    private readonly List<string> _goals = new();
    private readonly List<string> _allies = new();
    private readonly List<string> _enemies = new();
    private readonly List<string> _notes = new();
    private readonly List<Countdown> _countdowns = new();

    public StoryState(StorySave? save = null)
    {
        save ??= new StorySave();
        Scene = Math.Max(1, save.Scene);
        SceneTitle = Clean(save.SceneTitle);
        Points = Math.Max(0, save.Points);
        TurningPoint = Math.Clamp(save.TurningPoint, 1, 200);
        EventUsedThisScene = save.EventUsedThisScene;
        FinalGoal = Clean(save.FinalGoal);
        Fill(_goals, save.Goals);
        Fill(_allies, save.Allies);
        Fill(_enemies, save.Enemies);
        Fill(_notes, save.Notes);
        foreach (var countdown in (save.Countdowns ?? Array.Empty<Countdown>()).Take(MaxCountdowns))
        {
            var total = Math.Clamp(countdown.Total, 1, MaxCountdownSteps);
            if (Clean(countdown.Name) is { Length: > 0 } name) _countdowns.Add(new Countdown(name, Math.Clamp(countdown.Left, 0, total), total));
        }
    }

    public event Action? Changed;

    public int Scene { get; private set; }
    public string SceneTitle { get; private set; }
    /// <summary>Puntos de historia: suben con cada evento inesperado hasta el punto de giro.</summary>
    public int Points { get; private set; }
    public int TurningPoint { get; private set; }
    public bool PastTurningPoint => Points >= TurningPoint;
    public bool EventUsedThisScene { get; private set; }
    public string FinalGoal { get; private set; }
    public IReadOnlyList<string> Goals => _goals;
    public IReadOnlyList<string> Allies => _allies;
    public IReadOnlyList<string> Enemies => _enemies;
    public IReadOnlyList<string> Notes => _notes;
    public IReadOnlyList<Countdown> Countdowns => _countdowns;

    public StorySave ToSave() => new(Scene, SceneTitle, Points, TurningPoint, EventUsedThisScene, FinalGoal,
        _goals.ToList(), _allies.ToList(), _enemies.ToList(), _notes.ToList(), _countdowns.ToList());

    // ---------- Oráculo ----------

    /// <summary>
    /// Pregunta de sí o no. Tira d10, d8 y d6 a la vez: el d10 responde, el d8 gradúa el detalle
    /// y el d6 inclina la balanza según la probabilidad. Dos o tres dados iguales traen un evento inesperado.
    /// </summary>
    public OracleResult Ask(Likelihood likelihood, IRandomSource random)
    {
        var d10 = random.Next(10);
        var d8 = random.Next(8);
        var d6 = random.Next(6);
        var total = Shift(d10, d6, likelihood);
        var answer = total switch
        {
            <= 1 => OracleAnswer.NoAnd,
            <= 5 => OracleAnswer.No,
            <= 9 => OracleAnswer.Yes,
            _ => OracleAnswer.YesAnd,
        };
        var degree = Degrees[d8 <= 2 ? 0 : d8 <= 6 ? 1 : 2];
        return new OracleResult(answer, degree, d10, d8, d6, EventFromMatches(d10, d8, d6, random));
    }

    /// <summary>Primera impresión de un personaje no jugador ante el grupo.</summary>
    public NpcReaction React(Likelihood goodwill, IRandomSource random)
    {
        var d10 = random.Next(10);
        var d8 = random.Next(8);
        var d6 = random.Next(6);
        var attitude = Attitudes[Shift(d10, d6, goodwill) - 1];
        var capability = Capabilities[random.Next(3) - 1];
        return new NpcReaction(attitude, Personalities[d8 - 1], capability, EventFromMatches(d10, d8, d6, random));
    }

    /// <summary>
    /// El máster mete un giro por decisión propia, como mucho uno por escena. Da tantos puntos
    /// como el número de la escena (el doble si es importante), así que pesa más cuanto más avanzada va la aventura.
    /// </summary>
    public StoryEvent? VoluntaryEvent(bool major, IRandomSource random)
    {
        if (EventUsedThisScene) return null;
        EventUsedThisScene = true;
        var storyEvent = RollEvent(major, major ? Scene * 2 : Scene, random);
        Changed?.Invoke();
        return storyEvent;
    }

    /// <summary>Un verbo y un nombre al azar, para desatascar la imaginación.</summary>
    public static (string Verb, string Noun) Inspire(IRandomSource random) =>
        (Verbs[random.Next(Verbs.Length) - 1], Nouns[random.Next(Nouns.Length) - 1]);

    private static int Shift(int d10, int d6, Likelihood likelihood) => Math.Clamp(likelihood switch
    {
        Likelihood.VeryUnlikely => d10 - d6,
        Likelihood.Unlikely => d10 - (d6 + 1) / 2,
        Likelihood.Likely => d10 + (d6 + 1) / 2,
        Likelihood.VeryLikely => d10 + d6,
        _ => d10,
    }, 1, 10);

    private StoryEvent? EventFromMatches(int d10, int d8, int d6, IRandomSource random)
    {
        var triple = d10 == d8 && d8 == d6;
        var pair = d10 == d8 || d8 == d6 || d10 == d6;
        if (!pair) return null;
        var storyEvent = RollEvent(triple, triple ? 3 : 2, random);
        Changed?.Invoke();
        return storyEvent;
    }

    private StoryEvent RollEvent(bool major, int points, IRandomSource random)
    {
        var d10 = random.Next(10);
        var d8 = random.Next(8);
        var d6 = random.Next(6);
        var tone = major
            ? d10 <= 5 ? "muy negativo para el grupo" : "muy positivo para el grupo"
            : d10 switch
            {
                1 => "muy negativo para el grupo",
                <= 5 => "negativo para el grupo",
                <= 9 => "positivo para el grupo",
                _ => "muy positivo para el grupo",
            };
        var subject = (PastTurningPoint ? SubjectsAfter : SubjectsBefore)[d8 - 1];
        var timing = d6 switch
        {
            <= 2 => "Ocurre ya, interrumpiendo lo que estuviera pasando.",
            <= 4 => "Ocurre en cuanto termine la acción en curso.",
            _ => "Ocurre algo más tarde, cuando mejor encaje.",
        };

        // Tras el punto de giro la historia ya no acumula: va hacia su desenlace.
        var gained = PastTurningPoint ? 0 : points;
        Points += gained;
        return new StoryEvent(major, tone, subject, timing, gained);
    }

    // ---------- Escenas, objetivos y apuntes ----------

    public void NextScene(string title)
    {
        Scene++;
        SceneTitle = Clean(title);
        EventUsedThisScene = false;
        Changed?.Invoke();
    }

    public void SetLength(int turningPoint)
    {
        TurningPoint = Math.Clamp(turningPoint, 1, 200);
        Changed?.Invoke();
    }

    public void SetFinalGoal(string text)
    {
        FinalGoal = Clean(text);
        Changed?.Invoke();
    }

    public bool AddGoal(string text) => Add(_goals, text);
    public bool RemoveGoal(string text) => Remove(_goals, text);
    public bool AddAlly(string text) => Add(_allies, text);
    public bool RemoveAlly(string text) => Remove(_allies, text);
    public bool AddEnemy(string text) => Add(_enemies, text);
    public bool RemoveEnemy(string text) => Remove(_enemies, text);
    public bool AddNote(string text) => Add(_notes, text);
    public bool RemoveNote(string text) => Remove(_notes, text);

    // ---------- Cuentas atrás ----------

    /// <summary>Crea una cuenta atrás, o la reinicia si ya existía una con ese nombre.</summary>
    public Countdown? StartCountdown(string name, int steps)
    {
        var clean = Clean(name);
        if (clean.Length == 0) return null;
        var total = Math.Clamp(steps, 1, MaxCountdownSteps);
        var index = FindCountdown(clean);
        if (index < 0 && _countdowns.Count >= MaxCountdowns) return null;
        var countdown = new Countdown(index >= 0 ? _countdowns[index].Name : clean, total, total);
        if (index >= 0) _countdowns[index] = countdown;
        else _countdowns.Add(countdown);
        Changed?.Invoke();
        return countdown;
    }

    /// <summary>Avanza una cuenta atrás hacia cero. Devuelve null si no existe.</summary>
    public Countdown? AdvanceCountdown(string name, int steps = 1)
    {
        var index = FindCountdown(Clean(name));
        if (index < 0) return null;
        var current = _countdowns[index];
        var next = current with { Left = Math.Clamp(current.Left - Math.Max(1, steps), 0, current.Total) };
        _countdowns[index] = next;
        Changed?.Invoke();
        return next;
    }

    public bool RemoveCountdown(string name)
    {
        var index = FindCountdown(Clean(name));
        if (index < 0) return false;
        _countdowns.RemoveAt(index);
        Changed?.Invoke();
        return true;
    }

    private int FindCountdown(string name) =>
        _countdowns.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool Add(List<string> list, string text)
    {
        var clean = Clean(text);
        if (clean.Length == 0) return false;
        if (list.Any(item => string.Equals(item, clean, StringComparison.OrdinalIgnoreCase))) return true;
        // Las listas son cortas a propósito: lo viejo deja sitio a lo nuevo.
        if (list.Count >= MaxListEntries) list.RemoveAt(0);
        list.Add(clean);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Quita la entrada que coincide o, si no hay, la única que contiene el texto.</summary>
    private bool Remove(List<string> list, string text)
    {
        var clean = Clean(text);
        if (clean.Length == 0) return false;
        var index = list.FindIndex(item => string.Equals(item, clean, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            var matches = list.Where(item => item.Contains(clean, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1) return false;
            index = list.IndexOf(matches[0]);
        }
        list.RemoveAt(index);
        Changed?.Invoke();
        return true;
    }

    private static void Fill(List<string> list, IReadOnlyList<string>? items)
    {
        foreach (var item in (items ?? Array.Empty<string>()).TakeLast(MaxListEntries))
            if (Clean(item) is { Length: > 0 } clean) list.Add(clean);
    }

    private static string Clean(string? text)
    {
        var clean = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > MaxTextLength ? clean[..MaxTextLength] : clean;
    }
}
