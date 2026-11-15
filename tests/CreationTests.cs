using RoleDMFable.Core;
using Xunit;

namespace RoleDMFable.Tests;

public class CreationTests
{
    private static CreationGuide Guide() => new("Juego de prueba", new[]
    {
        new CreationStep("Raza", StepKind.Choice, "Elige tu pueblo.", Options: new[] { new CreationOption("Humano", "Versátil"), new CreationOption("Enano", "Duro") }),
        new CreationStep("Características", StepKind.Roll, "3d6 en orden.", Dice: "3d6", Targets: new[] { "Fuerza", "Destreza" }),
        new CreationStep("Puntos de vida", StepKind.Roll, Dice: "1d8+2", Hp: true),
        new CreationStep("Trasfondo", StepKind.Text, Optional: true),
        new CreationStep("Edad", StepKind.Number, Min: 16, Max: 80),
    }, "manual.pdf");

    [Fact]
    public void AGuidedCreationBuildsAFinishedHero()
    {
        var run = new CreationRun(Guide(), new FixedRandom(3, 4, 5, 6, 6, 6, 7));
        Assert.Throws<FormatException>(() => run.Build());

        run.SetIdentity("  Ánika la Roja ", "Bárbara del norte", "sword");
        Assert.Equal(0, run.FirstPending);
        run.Choose(0, 1);
        Assert.Equal(1, run.Chosen(0));
        Assert.Equal(new[] { 12, 18 }, run.Roll(1).Select(r => r.Total));
        // Los dados se tiran una sola vez: volver al paso no cambia el resultado.
        Assert.Equal(new[] { 12, 18 }, run.Roll(1).Select(r => r.Total));
        run.Roll(2);
        Assert.Equal(4, run.FirstPending);
        Assert.Contains("Edad", Assert.Throws<FormatException>(() => run.Build()).Message);
        Assert.Throws<ArgumentException>(() => run.SetNumber(4, 12));
        run.SetNumber(4, 23);
        run.Write(3, "  Creció   entre lobos ");

        var hero = run.Build("#b8472f");
        Assert.Equal(("anika_la_roja", "Ánika la Roja", "Bárbara del norte", "sword", "#b8472f", 9), (hero.Id, hero.Name, hero.Role, hero.Icon, hero.Color, hero.MaxHp));
        Assert.Equal(new[]
        {
            new SheetEntry("Raza", "Enano"), new SheetEntry("Fuerza", "12"), new SheetEntry("Destreza", "18"),
            new SheetEntry("Puntos de vida", "9"), new SheetEntry("Trasfondo", "Creció entre lobos"), new SheetEntry("Edad", "23"),
        }, hero.Sheet);

        // Entra en una aventura ya creado, con su ficha.
        var game = CampaignSetup.Create("La torre", "", new[] { new HeroSetup("", Saved: hero) });
        var token = game.Current.FindToken("anika_la_roja")!;
        Assert.True(token.Created);
        Assert.Equal(9, token.MaxHp);
    }

    [Fact]
    public void IdentityAndAnswersAreChecked()
    {
        var run = new CreationRun(Guide(), new FixedRandom(1));
        Assert.Throws<FormatException>(() => run.SetIdentity(" ", "", ""));
        Assert.Throws<FormatException>(() => run.SetIdentity("Fen", "", "dragon"));
        Assert.Contains("Ya tienes", Assert.Throws<FormatException>(() => run.SetIdentity("Fen", "", "hat", id => id == "fen")).Message);
        Assert.False(run.HasIdentity);

        Assert.Throws<ArgumentException>(() => run.Choose(0, 2));
        Assert.Throws<ArgumentException>(() => run.Choose(1, 0));
        Assert.Throws<ArgumentException>(() => run.Roll(0));
        Assert.Throws<ArgumentException>(() => run.Write(9, "x"));

        run.Write(3, "algo");
        run.Write(3, "   ");
        Assert.Empty(run.Answer(3));
        Assert.Equal(10, run.MaxHp);
    }

    [Fact]
    public void TheBasicGuideWorksWithoutARulebook()
    {
        var run = new CreationRun(CreationGuide.Basic, new FixedRandom(1));
        run.SetIdentity("Lira", "Exploradora", "");
        run.Write(0, "Busca a su hermano");
        run.Write(1, "Sigilo, arco, rastrear");
        run.Write(2, "Desconfiada");
        run.SetNumber(4, 14);

        var hero = run.Build();
        Assert.Equal(14, hero.MaxHp);
        Assert.Equal(4, hero.Sheet!.Count);
    }

    [Fact]
    public void GuidesSurviveJsonAndBadOnesAreRejected()
    {
        var copy = CreationGuide.FromJson(Guide().ToJson());
        Assert.Equal(("Juego de prueba", "manual.pdf", 5), (copy.Title, copy.Source, copy.Steps.Count));
        Assert.Equal(StepKind.Roll, copy.Steps[1].Kind);
        Assert.Equal(new[] { "Fuerza", "Destreza" }, copy.Steps[1].Targets);
        Assert.Equal("Enano", copy.Steps[0].Options![1].Label);

        // Tal como la escribiría el máster.
        var written = CreationGuide.FromJson("""
            {"title":"Mi juego","steps":[
              {"title":"Clase","kind":"choice","options":[{"label":"Guerrero"},{"label":"Mago","detail":"Lanza conjuros"},{"label":" "}]},
              {"title":"Golpes","kind":"roll","dice":"1d6","hp":true}]}
            """);
        Assert.Equal(2, written.Steps[0].Options!.Count);
        Assert.True(written.Steps[1].Hp);

        Assert.Throws<FormatException>(() => CreationGuide.FromJson("{ roto"));
        Assert.Throws<FormatException>(() => CreationGuide.FromJson("""{"title":"x","steps":[]}"""));
        Assert.Throws<FormatException>(() => CreationGuide.FromJson("""{"title":"x","steps":[{"title":"Clase","kind":"choice","options":[{"label":"Solo una"}]}]}"""));
        Assert.Throws<FormatException>(() => CreationGuide.FromJson("""{"title":"x","steps":[{"title":"PV","kind":"roll","dice":"muchos"}]}"""));
        Assert.Throws<FormatException>(() => CreationGuide.FromJson("""{"title":"x","steps":[{"title":"A","kind":"text"},{"title":"a","kind":"text"}]}"""));
        Assert.Throws<FormatException>(() => CreationGuide.FromJson("""{"title":"x","steps":[{"title":"A","kind":"text","hp":true}]}"""));
        Assert.Throws<FormatException>(() => CreationGuide.FromJson("""{"title":"x","steps":[{"title":"A","kind":"volar"}]}"""));
    }

    [Fact]
    public void StepsCanDependOnAnEarlierChoice()
    {
        var guide = new CreationGuide("Clases", new[]
        {
            new CreationStep("Clase", StepKind.Choice, Options: new[] { new CreationOption("Guerrero"), new CreationOption("Mago"), new CreationOption("Pícaro") }),
            new CreationStep("Golpes de guerrero", StepKind.Roll, Entry: "Puntos de vida", Dice: "1d10", Hp: true, When: "Clase", WhenValues: new[] { "guerrero" }),
            new CreationStep("Golpes de mago o pícaro", StepKind.Roll, Entry: "Puntos de vida", Dice: "1d4", Hp: true, When: "clase", WhenValues: new[] { "Mago", "Pícaro" }),
            new CreationStep("Conjuro inicial", StepKind.Text, When: "Clase", WhenValues: new[] { "Mago" }),
        }).Checked();

        var run = new CreationRun(guide, new FixedRandom(8, 3));
        run.SetIdentity("Fen", "", "hat");
        Assert.False(run.IsActive(1));
        run.Choose(0, 0);
        Assert.Equal((true, false, false), (run.IsActive(1), run.IsActive(2), run.IsActive(3)));
        run.Roll(1);
        Assert.Equal(8, run.Build().MaxHp);

        // Cambia de idea: lo del guerrero deja de contar y aparecen los pasos del mago.
        run.Choose(0, 1);
        Assert.Equal(2, run.FirstPending);
        run.Roll(2);
        Assert.Contains("Conjuro", Assert.Throws<FormatException>(() => run.Build()).Message);
        run.Write(3, "Luz");
        var mage = run.Build();
        Assert.Equal(3, mage.MaxHp);
        Assert.Equal(new[] { "Clase", "Puntos de vida", "Conjuro inicial" }, mage.Sheet!.Select(e => e.Name));

        Assert.Throws<FormatException>(() => new CreationGuide("x", new[] { new CreationStep("A", StepKind.Text, When: "Clase", WhenValues: new[] { "Mago" }) }).Checked());
        Assert.Throws<FormatException>(() => new CreationGuide("x", new[]
        {
            new CreationStep("Clase", StepKind.Choice, Options: new[] { new CreationOption("Guerrero"), new CreationOption("Mago") }),
            new CreationStep("A", StepKind.Text, When: "Clase", WhenValues: new[] { "Bardo" }),
        }).Checked());
    }
}
