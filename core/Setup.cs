namespace RoleDMFable.Core;

/// <summary>Un héroe tal como lo define su jugador al empezar una partida.</summary>
/// <param name="MaxHp">Provisional: el máster lo fija según el reglamento al completar la hoja.</param>
/// <param name="Saved">Si viene de la lista de personajes guardados: entra ya creado, con su hoja.</param>
public sealed record HeroSetup(string Name, string Role = "", string Icon = "", int MaxHp = 10, bool HasPlayer = true, SavedHero? Saved = null);

/// <summary>Monta una partida propia: nombre, idea de la aventura y héroes del grupo.</summary>
public static class CampaignSetup
{
    public const string StartSceneId = "punto_de_partida";
    public const int MaxHeroes = 6;
    public const int MaxHeroHp = 999;
    public const int MaxNameLength = 30;

    /// <summary>Aspectos que sabe dibujar el mapa, con el nombre que ve el jugador.</summary>
    public static readonly IReadOnlyList<(string Label, string Icon)> Looks = new[]
    {
        ("Guerrero", "sword"), ("Mago", "hat"), ("Clérigo", "sun"), ("Druida", "mushroom"), ("Aventurero", ""),
    };

    private static readonly string[] Colors = { "#3f6fbf", "#7c4fb3", "#1f8a7a", "#c98a2b", "#b8472f", "#5b8f3a" };

    /// <summary>
    /// Crea la partida con el grupo reunido en un claro vacío, a la espera de que el máster abra la aventura.
    /// Lanza <see cref="FormatException"/> con un mensaje para el jugador si algo no vale.
    /// </summary>
    /// <param name="taken">Dice si ya hay un personaje guardado con ese id, para no pisarlo con uno nuevo.</param>
    public static Campaign Create(string name, string premise, IReadOnlyList<HeroSetup> heroes, Func<string, bool>? taken = null)
    {
        if (heroes.Count == 0) throw new FormatException("Hace falta al menos un héroe.");
        if (heroes.Count > MaxHeroes) throw new FormatException($"Como mucho {MaxHeroes} héroes.");

        var tokens = new List<TokenSpec>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var seat = 0;
        for (var i = 0; i < heroes.Count; i++)
        {
            var hero = heroes[i];
            var (x, y) = (4 + i % 3, 3 + i / 3);
            if (hero.Saved is { } saved)
            {
                // Un personaje guardado llega tal como quedó en su última aventura, descansado.
                if (!ids.Add(saved.Id)) throw new FormatException($"«{saved.Name}» está dos veces en el grupo.");
                var savedHp = Math.Clamp(saved.MaxHp, 1, TableState.MaxHpLimit);
                tokens.Add(new TokenSpec(saved.Id, saved.Name, TokenKind.Hero, x, y, savedHp, savedHp,
                    saved.Color, Looks.Any(l => l.Icon == saved.Icon) ? saved.Icon : "", saved.Role, hero.HasPlayer ? ++seat : null, saved.Sheet, Created: true));
                continue;
            }

            var heroName = hero.Name.Trim();
            if (heroName.Length == 0) throw new FormatException($"El héroe {i + 1} no tiene nombre.");
            if (heroName.Length > MaxNameLength) heroName = heroName[..MaxNameLength];
            if (hero.MaxHp < 1 || hero.MaxHp > MaxHeroHp) throw new FormatException($"Los PV de {heroName} tienen que estar entre 1 y {MaxHeroHp}.");
            if (Looks.All(l => l.Icon != hero.Icon)) throw new FormatException($"El aspecto de {heroName} no vale.");

            var id = HeroId(heroName, i);
            if (!ids.Add(id)) throw new FormatException($"Hay dos héroes llamados «{heroName}».");
            if (taken is not null && taken(id))
                throw new FormatException($"Ya tienes un personaje guardado llamado «{heroName}». Elígelo de la lista de guardados o ponle otro nombre al nuevo.");

            var role = hero.Role.Trim();
            if (role.Length > 40) role = role[..40];
            // El grupo empieza junto, en el centro del claro.
            tokens.Add(new TokenSpec(id, heroName, TokenKind.Hero, x, y, hero.MaxHp, hero.MaxHp,
                Colors[i % Colors.Length], hero.Icon, role, hero.HasPlayer ? ++seat : null, Created: false));
        }
        if (seat == 0) throw new FormatException("Al menos un héroe tiene que llevarlo un jugador.");

        var start = new SceneSpec(StartSceneId, "Punto de partida", 10, 8, Features: new[]
        {
            new FeatureSpec(Terrain.Rock, 1, 1), new FeatureSpec(Terrain.Rock, 8, 2), new FeatureSpec(Terrain.Rock, 2, 6), new FeatureSpec(Terrain.Rock, 7, 6),
        }, Tokens: tokens);

        var title = name.Trim();
        if (title.Length > 40) title = title[..40];
        var idea = premise.Trim();
        if (idea.Length > StoryState.MaxPremiseLength) idea = idea[..StoryState.MaxPremiseLength];
        return new Campaign(title.Length > 0 ? title : "Aventura nueva", new[] { start }, StartSceneId, story: new StorySave(Premise: idea));
    }

    /// <summary>El id de un héroe, sacado de su nombre: «Ánika la Roja» → anika_la_roja.</summary>
    public static string HeroId(string name, int index = 0)
    {
        var id = new string(RulesText.Fold(name.Trim()).Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        while (id.Contains("__")) id = id.Replace("__", "_");
        return id.Length > 0 ? id : $"heroe_{index + 1}";
    }

    /// <summary>Una partida creada por los jugadores (no la de ejemplo): sus héroes se guardan como personajes.</summary>
    public static bool IsOwnAdventure(Campaign game) => game.Scenes.Any(s => s.SceneId == StartSceneId);

    /// <summary>Los héroes que todavía no están creados, en cualquier lugar de la partida.</summary>
    public static IReadOnlyList<Token> PendingHeroes(Campaign game) =>
        game.Scenes.SelectMany(s => s.Tokens).Where(t => t.Kind == TokenKind.Hero && !t.Created).ToList();

    /// <summary>Lo que toca hacer en el punto de partida: crear personajes o, si ya están, abrir la aventura.</summary>
    public static string StartRequest(Campaign game) => PendingHeroes(game).Count > 0 ? CreationRequest(game) : OpeningRequest(game);

    /// <summary>Lo que se le pide al máster mientras queden personajes por crear.</summary>
    public static string CreationRequest(Campaign game)
    {
        var pending = PendingHeroes(game);
        var names = string.Join(", ", pending.Select(t => t.Seat is null ? $"{t.Name} (sin jugador: créalo tú deprisa, preguntando al grupo solo lo esencial)" : t.Name));
        return "La aventura todavía no ha empezado: primero hay que crear los personajes. "
            + $"Faltan por crear: {names}. "
            + "No narres historia ni crees lugares. Crea un personaje cada vez, empezando por el de quien te habla o por el primero de la lista: "
            + "busca el proceso de creación en el reglamento con consultar_reglas y síguelo en su orden; en cada mensaje pide una sola decisión al jugador, "
            + "con las opciones que da el manual explicadas en una línea cada una; tira con tirar_dados lo que el manual mande tirar; apunta cada resultado con anotar_ficha "
            + "y fija los puntos de vida con fijar_pv_maximos (con curar). Si no hay reglamento cargado, usa una creación breve: concepto, tres puntos fuertes, un punto débil, "
            + "equipo y puntos de vida. Cuando un personaje esté completo, repásalo en dos líneas, pide el visto bueno de su jugador y márcalo con personaje_listo. "
            + "Después pasa al siguiente. Cuando no quede ninguno, la aventura podrá empezar.";
    }

    /// <summary>Lo que se le pide al máster para que abra una partida recién creada.</summary>
    public static string OpeningRequest(Campaign game)
    {
        var idea = game.Story.Premise.Length > 0
            ? $"La idea que han pedido los jugadores: {game.Story.Premise}"
            : "Los jugadores no han pedido nada concreto: propón tú un arranque con gancho.";
        return "Empieza una aventura nueva. El grupo está en «Punto de partida», que es solo una sala de espera sin historia. "
            + idea
            + " Abre la aventura: crea el primer lugar con crear_lugar (sin puerta: no se vuelve al punto de partida), con los personajes que haya; "
            + "abre la primera escena con nueva_escena; apunta el objetivo final si la idea lo deja claro; y presenta la situación en pocas frases, "
            + "terminando con algo que invite a los héroes a actuar. Los personajes ya están creados: usa lo que digan sus hojas.";
    }
}
