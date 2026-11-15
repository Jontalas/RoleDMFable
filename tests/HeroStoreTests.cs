using RoleDMFable.Core;
using Xunit;

namespace RoleDMFable.Tests;

public class HeroStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "roledm-heroes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static Campaign PlayedAdventure(string name = "La torre hundida")
    {
        var game = CampaignSetup.Create(name, "", new[] { new HeroSetup("Ánika la Roja", "Bárbara", "sword"), new HeroSetup("Fen", "Hechicero", "hat") });
        var table = game.Current;
        table.SetSheetEntry("anika_la_roja", "Fuerza", "17");
        table.SetSheetEntry("anika_la_roja", "Nivel", "1");
        table.SetMaxHp("anika_la_roja", 14, heal: true);
        table.SetCreated("anika_la_roja", true);
        return game;
    }

    [Fact]
    public void OnlyFinishedHeroesOfOwnAdventuresAreSaved()
    {
        var store = new HeroStore(_folder);

        Assert.Equal(1, store.SaveFrom(PlayedAdventure()));
        Assert.Equal(0, store.SaveFrom(MasterToolboxTests.NewGame()));

        var saved = Assert.Single(new HeroStore(_folder).LoadAll());
        Assert.Equal(("anika_la_roja", "Ánika la Roja", "Bárbara", "sword", 14), (saved.Id, saved.Name, saved.Role, saved.Icon, saved.MaxHp));
        Assert.Equal("La torre hundida", saved.LastAdventure);
        Assert.Contains(new SheetEntry("Fuerza", "17"), saved.Sheet!);
    }

    [Fact]
    public void ASavedHeroJoinsANewAdventureAlreadyCreatedAndKeepsGrowing()
    {
        var store = new HeroStore(_folder);
        store.SaveFrom(PlayedAdventure());
        var anika = store.LoadAll().Single();

        var second = CampaignSetup.Create("El pantano", "", new[] { new HeroSetup("", Saved: anika), new HeroSetup("Lira", "Exploradora") }, store.Exists);

        var token = second.Current.FindToken("anika_la_roja")!;
        Assert.True(token.Created);
        Assert.Equal((14, 14, 1), (token.Hp, token.MaxHp, token.Seat));
        Assert.Contains(new SheetEntry("Fuerza", "17"), token.Sheet);
        Assert.Equal(new[] { "lira" }, CampaignSetup.PendingHeroes(second).Select(t => t.Id));

        // Sube de nivel en la segunda aventura y eso queda en el personaje.
        second.Current.SetSheetEntry("anika_la_roja", "Nivel", "2");
        second.Current.SetMaxHp("anika_la_roja", 23, heal: false);
        Assert.Equal(1, store.SaveFrom(second));
        Assert.Equal(0, store.SaveFrom(second));

        var grown = new HeroStore(_folder).LoadAll().Single();
        Assert.Equal(23, grown.MaxHp);
        Assert.Contains(new SheetEntry("Nivel", "2"), grown.Sheet!);
        Assert.Equal("El pantano", grown.LastAdventure);
    }

    [Fact]
    public void ANewHeroCannotOverwriteASavedOne()
    {
        var store = new HeroStore(_folder);
        store.SaveFrom(PlayedAdventure());
        var anika = store.LoadAll().Single();

        var clash = Assert.Throws<FormatException>(() => CampaignSetup.Create("X", "", new[] { new HeroSetup("Anika la roja") }, store.Exists));
        Assert.Contains("personaje guardado", clash.Message);
        Assert.Throws<FormatException>(() => CampaignSetup.Create("X", "", new[] { new HeroSetup("", Saved: anika), new HeroSetup("", Saved: anika) }));
    }

    [Fact]
    public void DeletingAndDamagedFiles()
    {
        var store = new HeroStore(_folder);
        store.SaveFrom(PlayedAdventure());
        File.WriteAllText(Path.Combine(_folder, "roto.json"), "{ esto no es json");

        Assert.Single(store.LoadAll());
        store.Delete("anika_la_roja");
        Assert.Empty(store.LoadAll());
        Assert.False(store.Exists("anika_la_roja"));
    }
}
