using RoleDMFable.Core;
using Xunit;

namespace RoleDMFable.Tests;

public class SetupTests
{
    private static readonly HeroSetup[] Party =
    {
        new("Ánika la Roja", "Bárbara", "sword", 14),
        new("Fen", "Hechicero", "hat", 8),
        new("Mulo", "Porteador", "", 6, HasPlayer: false),
    };

    [Fact]
    public void CreatesAPartyReadyToPlay()
    {
        var game = CampaignSetup.Create("  La torre hundida ", "Buscamos a un mago desaparecido", Party);

        Assert.Equal("La torre hundida", game.Name);
        Assert.Equal(CampaignSetup.StartSceneId, game.Current.SceneId);
        Assert.Equal("Buscamos a un mago desaparecido", game.Story.Premise);
        var anika = game.Current.FindToken("anika_la_roja")!;
        Assert.Equal((TokenKind.Hero, 14, 14, 1), (anika.Kind, anika.Hp, anika.MaxHp, anika.Seat));
        Assert.Equal(2, game.Current.FindToken("fen")!.Seat);
        Assert.Null(game.Current.FindToken("mulo")!.Seat);
        // Cada uno en su casilla y sobre suelo.
        Assert.Equal(3, game.Current.Tokens.Select(t => (t.X, t.Y)).Distinct().Count());
        Assert.All(game.Current.Tokens, t => Assert.True(TableState.IsWalkable(game.Current.TerrainAt(t.X, t.Y))));
    }

    [Fact]
    public void ThePremiseSurvivesSavingAndReachesTheMaster()
    {
        var game = Campaign.FromJson(CampaignSetup.Create("X", "Un dragón duerme bajo el pueblo", Party).ToJson());

        Assert.Equal("Un dragón duerme bajo el pueblo", game.Story.Premise);
        Assert.Contains("Un dragón duerme bajo el pueblo", MasterPrompt.BuildTurn(game, "Fen", "hola"));
        Assert.Contains("Un dragón duerme bajo el pueblo", CampaignSetup.OpeningRequest(game));
        Assert.Contains("crear_lugar", CampaignSetup.OpeningRequest(game));
    }

    private const string Market = """{"nombre":"Plaza del mercado","plano":["........","........","...oo...","........","........"],"llegada":{"x":1,"y":1}}""";

    [Fact]
    public void TheAdventureCannotStartUntilEveryHeroIsCreated()
    {
        var game = CampaignSetup.Create("X", "Un dragón duerme bajo el pueblo", Party);
        var tools = new MasterToolbox(game, new SystemRandomSource());

        Assert.Equal(3, CampaignSetup.PendingHeroes(game).Count);
        Assert.Contains("FASE: CREACIÓN DE PERSONAJES", MasterPrompt.BuildTurn(game, "Fen", "hola"));
        Assert.Contains("PERSONAJE SIN CREAR", MasterPrompt.DescribeTable(game.Current));
        Assert.Contains("primero hay que crear los personajes", CampaignSetup.StartRequest(game));

        var refused = tools.Execute("crear_lugar", MasterToolboxTests.Input(Market));
        Assert.True(refused.IsError);
        Assert.Contains("Fen", refused.Result);
        Assert.Equal(CampaignSetup.StartSceneId, game.Current.SceneId);

        // Una hoja vacía no se puede dar por terminada.
        Assert.True(tools.Execute("personaje_listo", MasterToolboxTests.Input("""{"ficha":"fen"}""")).IsError);

        foreach (var id in new[] { "anika_la_roja", "fen", "mulo" })
        {
            tools.Execute("anotar_ficha", MasterToolboxTests.Input($$"""{"ficha":"{{id}}","dato":"Fuerza","valor":"12"}"""));
            var done = tools.Execute("personaje_listo", MasterToolboxTests.Input($$"""{"ficha":"{{id}}"}"""));
            Assert.False(done.IsError, done.Result);
            if (id == "mulo") Assert.Contains("crear_lugar", done.Result);
            else Assert.Contains("Faltan por crear", done.Result);
        }

        Assert.Empty(CampaignSetup.PendingHeroes(game));
        Assert.DoesNotContain("FASE: CREACIÓN", MasterPrompt.BuildTurn(game, "Fen", "hola"));
        Assert.Contains("Abre la aventura", CampaignSetup.StartRequest(game));
        var outcome = tools.Execute("crear_lugar", MasterToolboxTests.Input(Market));
        Assert.False(outcome.IsError, outcome.Result);
        Assert.Equal(3, game.Current.Tokens.Count(t => t.Kind == TokenKind.Hero));
    }

    [Fact]
    public void CreationProgressIsSavedAndOldGamesCountAsCreated()
    {
        var game = CampaignSetup.Create("X", "", Party);
        game.Current.SetSheetEntry("fen", "Fuerza", "12");
        game.Current.SetCreated("fen", true);

        var loaded = Campaign.FromJson(game.ToJson());
        Assert.Equal(new[] { "anika_la_roja", "mulo" }, CampaignSetup.PendingHeroes(loaded).Select(t => t.Id));

        // La partida de ejemplo y las guardadas antes de este cambio no tienen fase de creación.
        Assert.Empty(CampaignSetup.PendingHeroes(MasterToolboxTests.NewGame()));
    }

    [Fact]
    public void TheMasterSetsMaxHpWhenTheSheetIsFilledIn()
    {
        var game = CampaignSetup.Create("X", "", Party);
        var tools = new MasterToolbox(game, new SystemRandomSource());
        var fen = game.Current.FindToken("fen")!;

        Assert.False(tools.Execute("fijar_pv_maximos", MasterToolboxTests.Input("""{"ficha":"fen","maximo":22,"curar":true}""")).IsError);
        Assert.Equal((22, 22), (fen.Hp, fen.MaxHp));

        // Bajar el máximo recorta la vida; subirlo sin curar no la regala.
        tools.Execute("fijar_pv_maximos", MasterToolboxTests.Input("""{"ficha":"fen","maximo":15}"""));
        Assert.Equal((15, 15), (fen.Hp, fen.MaxHp));
        tools.Execute("fijar_pv_maximos", MasterToolboxTests.Input("""{"ficha":"fen","maximo":30}"""));
        Assert.Equal((15, 30), (fen.Hp, fen.MaxHp));

        Assert.True(tools.Execute("fijar_pv_maximos", MasterToolboxTests.Input("""{"ficha":"fen","maximo":0}""")).IsError);
        Assert.Equal(30, Campaign.FromJson(game.ToJson()).Current.FindToken("fen")!.MaxHp);
    }

    [Fact]
    public void BadPartiesAreRefusedWithAReason()
    {
        Assert.Contains("al menos un héroe", Assert.Throws<FormatException>(() => CampaignSetup.Create("X", "", Array.Empty<HeroSetup>())).Message);
        Assert.Contains("no tiene nombre", Assert.Throws<FormatException>(() => CampaignSetup.Create("X", "", new[] { new HeroSetup("  ") })).Message);
        Assert.Contains("dos héroes", Assert.Throws<FormatException>(() => CampaignSetup.Create("X", "", new[] { new HeroSetup("Fen"), new HeroSetup("fen") })).Message);
        Assert.Contains("PV", Assert.Throws<FormatException>(() => CampaignSetup.Create("X", "", new[] { new HeroSetup("Fen", MaxHp: 0) })).Message);
        Assert.Contains("jugador", Assert.Throws<FormatException>(() => CampaignSetup.Create("X", "", new[] { new HeroSetup("Fen", HasPlayer: false) })).Message);
        Assert.Contains("Como mucho", Assert.Throws<FormatException>(() => CampaignSetup.Create("X", "", Enumerable.Range(1, 7).Select(i => new HeroSetup($"H{i}")).ToArray())).Message);
    }
}
