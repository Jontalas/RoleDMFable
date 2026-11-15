using RoleDMFable.Core;
using Xunit;

namespace RoleDMFable.Tests;

public class CampaignStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roledm-partidas-" + Guid.NewGuid().ToString("N"));
    private string Games => Path.Combine(_root, "partidas");
    private string Heroes => Path.Combine(_root, "personajes");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static Campaign Adventure(string name, params HeroSetup[] heroes) =>
        CampaignSetup.Create(name, "", heroes.Length > 0 ? heroes : new[] { new HeroSetup("Ánika la Roja", "Bárbara", "sword") });

    [Fact]
    public void SeveralGamesLiveSideBySide()
    {
        var store = new CampaignStore(Games);
        Assert.Empty(store.List());

        var tower = Adventure("La torre hundida");
        var towerId = store.NewId(tower.Name);
        store.Save(towerId, tower);
        var swamp = Adventure("El pantano", new HeroSetup("Lira", "Exploradora"), new HeroSetup("Brom", "Herrero"));
        var swampId = store.NewId(swamp.Name);
        store.Save(swampId, swamp);
        File.SetLastWriteTime(Path.Combine(Games, towerId + ".json"), DateTime.Now.AddHours(-2));

        Assert.Equal(("la_torre_hundida", "el_pantano"), (towerId, swampId));
        var list = store.List();
        Assert.Equal(new[] { "el_pantano", "la_torre_hundida" }, list.Select(g => g.Id));
        Assert.Equal(("El pantano", "Punto de partida", true), (list[0].Name, list[0].Place, list[0].Own));
        Assert.Equal(new[] { "Lira", "Brom" }, list[0].Heroes);

        tower.AddLog("Mesa", "Seguimos.");
        store.Save(towerId, tower);
        Assert.Equal("la_torre_hundida", store.List()[0].Id);
        Assert.Equal("Seguimos.", store.Load(towerId).Log[^1].Text);

        store.Delete(swampId);
        Assert.False(store.Exists(swampId));
        Assert.Single(store.List());
    }

    [Fact]
    public void TwoGamesWithTheSameNameGetDifferentIds()
    {
        var store = new CampaignStore(Games);
        store.Save(store.NewId("La torre hundida"), Adventure("La torre hundida"));

        Assert.Equal("la_torre_hundida_2", store.NewId("La Torre Hundida"));
        Assert.Equal("partida", store.NewId("¡¿?!"));
    }

    [Fact]
    public void DamagedGamesAreSetAsideAndStrangeIdsRejected()
    {
        var store = new CampaignStore(Games);
        store.Save("buena", Adventure("Buena"));
        File.WriteAllText(Path.Combine(Games, "rota.json"), "{ esto no es json");

        Assert.Equal(new[] { "buena" }, store.List().Select(g => g.Id));
        Assert.Throws<FormatException>(() => store.Load("rota"));
        Assert.True(File.Exists(Path.Combine(Games, "rota.json.roto")));
        Assert.False(store.Exists("rota"));

        Assert.Throws<FormatException>(() => store.Load("../ajustes"));
        Assert.Throws<ArgumentException>(() => store.Save("../fuera", Adventure("X")));
        Assert.False(store.Exists("no_esta"));
    }

    [Fact]
    public void TheSingleSaveOfOlderVersionsIsBroughtIn()
    {
        Directory.CreateDirectory(_root);
        var legacy = Path.Combine(_root, "partida.json");
        File.WriteAllText(legacy, Adventure("La torre hundida").ToJson());
        var store = new CampaignStore(Games);

        Assert.Equal("la_torre_hundida", store.ImportLegacy(legacy));
        Assert.False(File.Exists(legacy));
        Assert.Equal("La torre hundida", store.Load("la_torre_hundida").Name);
        Assert.Null(store.ImportLegacy(legacy));

        File.WriteAllText(legacy, "roto");
        Assert.Null(store.ImportLegacy(legacy));
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public void AHeroWhoGrewElsewhereComesBackUpToDate()
    {
        var games = new CampaignStore(Games);
        var heroes = new HeroStore(Heroes);

        var tower = Adventure("La torre hundida");
        tower.Current.SetMaxHp("anika_la_roja", 14, heal: true);
        tower.Current.SetSheetEntry("anika_la_roja", "Nivel", "1");
        tower.Current.SetCreated("anika_la_roja", true);
        tower.Current.SetHp("anika_la_roja", 9);
        heroes.SaveFrom(tower);
        games.Save("la_torre_hundida", tower);

        // La misma heroína juega otra aventura y sube de nivel.
        var swamp = CampaignSetup.Create("El pantano", "", new[] { new HeroSetup("", Saved: heroes.LoadAll().Single()) }, heroes.Exists);
        swamp.Current.SetSheetEntry("anika_la_roja", "Nivel", "2");
        swamp.Current.SetMaxHp("anika_la_roja", 23, heal: true);
        heroes.SaveFrom(swamp);

        var back = games.Load("la_torre_hundida");
        Assert.Equal(new[] { "Ánika la Roja" }, heroes.ApplyTo(back));
        var token = back.Current.FindToken("anika_la_roja")!;
        Assert.Equal((9, 23), (token.Hp, token.MaxHp));
        Assert.Contains(new SheetEntry("Nivel", "2"), token.Sheet);
        Assert.Empty(heroes.ApplyTo(back));

        // Las partidas de ejemplo y los héroes sin crear no se tocan.
        Assert.Empty(heroes.ApplyTo(MasterToolboxTests.NewGame()));
    }
}
