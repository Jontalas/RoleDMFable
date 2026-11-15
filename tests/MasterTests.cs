using System.Net;
using System.Text;
using System.Text.Json;
using RoleDMFable.Core;
using RoleDMFable.Master;
using Xunit;

namespace RoleDMFable.Tests;

public class MasterToolboxTests
{
    private sealed class FixedRandom(params int[] values) : IRandomSource
    {
        private int _next;
        public int Next(int sides) => values[_next++ % values.Length];
    }

    internal static SceneSpec Load(string id) =>
        SceneSpec.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenes", id + ".json")));

    internal static Campaign NewGame() =>
        new("Prueba", new[] { Load("sala_del_puente"), Load("galeria_de_las_setas") }, "sala_del_puente");

    internal static IReadOnlyDictionary<string, JsonElement> Input(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void EveryToolHasAValidObjectSchema()
    {
        foreach (var tool in MasterToolbox.Tools)
        {
            using var schema = JsonDocument.Parse(tool.SchemaJson);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Object, schema.RootElement.GetProperty("properties").ValueKind);
        }
        Assert.Equal(MasterToolbox.Tools.Count, MasterToolbox.Tools.Select(t => t.Name).Distinct().Count());
    }

    [Fact]
    public void RollUsesRealDiceAndAnnouncesIt()
    {
        var tools = new MasterToolbox(NewGame(), new FixedRandom(14));
        var seen = new List<(string Who, string Text)>();
        tools.Announced += (who, text) => seen.Add((who, text));

        var outcome = tools.Execute("tirar_dados", Input("""{"expresion":"1d20+3","motivo":"ataque","quien":"goblin_1"}"""));

        Assert.False(outcome.IsError);
        Assert.Contains("Total: 17", outcome.Result);
        var line = Assert.Single(seen);
        Assert.Contains("ataque", line.Text);
        Assert.Contains("17", line.Text);
    }

    [Fact]
    public void BadDiceAreAnErrorAndNothingIsAnnounced()
    {
        var tools = new MasterToolbox(NewGame(), new FixedRandom(1));
        var announced = 0;
        tools.Announced += (_, _) => announced++;
        Assert.True(tools.Execute("tirar_dados", Input("""{"expresion":"dos dados","motivo":"x"}""")).IsError);
        Assert.True(tools.Execute("tirar_dados", Input("""{"motivo":"x"}""")).IsError);
        Assert.Equal(0, announced);
    }

    [Fact]
    public void MoveChangesTheTableAndRefusesBadCells()
    {
        var game = NewGame();
        var tools = new MasterToolbox(game, new FixedRandom(1));

        Assert.False(tools.Execute("mover_ficha", Input("""{"ficha":"goblin_1","x":8,"y":5}""")).IsError);
        var goblin = game.Current.FindToken("goblin_1")!;
        Assert.Equal((8, 5), (goblin.X, goblin.Y));

        var kael = game.Current.FindToken("kael")!;
        var refused = tools.Execute("mover_ficha", Input($$"""{"ficha":"goblin_1","x":{{kael.X}},"y":{{kael.Y}}}"""));
        Assert.True(refused.IsError);
        Assert.Equal((8, 5), (goblin.X, goblin.Y));

        Assert.True(tools.Execute("mover_ficha", Input("""{"ficha":"dragon","x":1,"y":1}""")).IsError);
        Assert.True(tools.Execute("mover_ficha", Input("""{"ficha":"goblin_1","x":"a","y":1}""")).IsError);
    }

    [Fact]
    public void MovingAHeroOntoAnExitTakesThePartyAlong()
    {
        var game = NewGame();
        var tools = new MasterToolbox(game, new FixedRandom(1));
        TravelResult? travel = null;
        tools.Travelled += result => travel = result;

        tools.Execute("mover_ficha", Input("""{"ficha":"goblin_1","x":8,"y":5}"""));
        var outcome = tools.Execute("mover_ficha", Input("""{"ficha":"kael","x":12,"y":4}"""));

        Assert.False(outcome.IsError);
        Assert.NotNull(travel);
        Assert.Equal("galeria_de_las_setas", game.Current.SceneId);
        Assert.Contains(game.Current.SceneName, outcome.Result);
    }

    [Fact]
    public void HpChangesAreClampedAndFoundByName()
    {
        var game = NewGame();
        var tools = new MasterToolbox(game, new FixedRandom(1));
        var kael = game.Current.FindToken("kael")!;
        var max = kael.MaxHp;

        tools.Execute("cambiar_pv", Input("""{"ficha":"Kael","cantidad":-5,"motivo":"flecha"}"""));
        Assert.Equal(Math.Max(0, Math.Min(max, kael.Hp)), kael.Hp);
        var afterHit = kael.Hp;
        tools.Execute("cambiar_pv", Input("""{"ficha":"kael","cantidad":999,"motivo":"poción"}"""));
        Assert.Equal(max, kael.Hp);
        var down = tools.Execute("cambiar_pv", Input("""{"ficha":"kael","cantidad":-999,"motivo":"derrumbe"}"""));
        Assert.Equal(0, kael.Hp);
        Assert.Contains("0", down.Result);
        Assert.True(afterHit < max || max == 0);
        Assert.True(tools.Execute("cambiar_pv", Input("""{"ficha":"kael","cantidad":0,"motivo":"nada"}""")).IsError);
    }

    [Fact]
    public void MeasuresDistanceAndRejectsUnknownTools()
    {
        var game = NewGame();
        var tools = new MasterToolbox(game, new FixedRandom(1));
        var a = game.Current.FindToken("kael")!;
        var b = game.Current.FindToken("goblin_1")!;
        var feet = GridDistance.Feet(a.X, a.Y, b.X, b.Y);
        Assert.Contains($"{feet} pies", tools.Execute("medir_distancia", Input("""{"desde":"kael","hasta":"goblin_1"}""")).Result);
        Assert.True(tools.Execute("lanzar_meteorito", Input("{}")).IsError);
    }

    [Fact]
    public void TurnTextCarriesTableLogAndWhatThePlayerSays()
    {
        var game = NewGame();
        game.AddLog("Mira", "enciende una antorcha.");
        var text = MasterPrompt.BuildTurn(game, "Kael", "Miro si hay huellas.");

        Assert.Contains(game.Current.SceneName, text);
        Assert.Contains("- kael: Kael", text);
        Assert.Contains("Mira: enciende una antorcha.", text);
        Assert.EndsWith("Kael dice: Miro si hay huellas.", text);

        var plan = MasterPrompt.DescribeTable(game.Current).Split('\n').Skip(2).Take(game.Current.Height).ToList();
        Assert.All(plan, row => Assert.Equal(game.Current.Width, row.TrimEnd('\r').Length));
        Assert.Contains("sin explorar", text);
    }
}

/// <summary>
/// Prueba el bucle completo contra un servidor local que imita la API de Claude:
/// así se comprueba el formato de las peticiones sin gastar ni necesitar clave.
/// </summary>
public class ClaudeMasterTests
{
    private sealed class FakeApi : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Queue<(int Status, string Body)> _replies = new();
        public List<JsonDocument> Requests { get; } = new();
        public List<string?> Keys { get; } = new();
        public string Url { get; }

        public FakeApi(params (int Status, string Body)[] replies)
        {
            foreach (var reply in replies) _replies.Enqueue(reply);
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Serve();
        }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                Requests.Add(JsonDocument.Parse(await reader.ReadToEndAsync()));
                Keys.Add(context.Request.Headers["Authorization"]);
                var (status, body) = _replies.Count > 0 ? _replies.Dequeue() : (500, """{"type":"error","error":{"type":"api_error","message":"sin respuesta preparada"}}""");
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }

    private static string Reply(string stop, string content) =>
        """{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[CONTENT],"stop_reason":"STOP","stop_sequence":null,"usage":{"input_tokens":100,"output_tokens":20,"cache_read_input_tokens":40,"cache_creation_input_tokens":0}}"""
            .Replace("CONTENT", content).Replace("STOP", stop);

    private sealed class Always(int value) : IRandomSource
    {
        public int Next(int sides) => value;
    }

    [Fact]
    public async Task RunsToolsAndSendsResultsBackUntilTheMasterFinishes()
    {
        using var api = new FakeApi(
            (200, Reply("tool_use", """
                {"type":"thinking","thinking":"","signature":"firma"},
                {"type":"text","text":"El goblin enseña los dientes."},
                {"type":"tool_use","id":"toolu_1","name":"tirar_dados","input":{"expresion":"1d20+2","motivo":"ataque del goblin","quien":"goblin_1"}},
                {"type":"tool_use","id":"toolu_2","name":"mover_ficha","input":{"ficha":"dragon","x":1,"y":1}}
                """)),
            (200, Reply("end_turn", """{"type":"text","text":"Falla por poco."}""")));
        var game = MasterToolboxTests.NewGame();
        var tools = new MasterToolbox(game, new Always(9));
        var announced = new List<string>();
        tools.Announced += (who, text) => announced.Add($"{who} {text}");
        var narration = new List<string>();

        var master = new ClaudeMaster("clave-de-prueba", "claude-opus-5-5", api.Url);
        var usage = await master.PlayTurnAsync(game, tools, "Kael", "Me planto en el puente.", narration.Add);

        Assert.Equal(new[] { "El goblin enseña los dientes.", "Falla por poco." }, narration);
        Assert.Single(announced);
        Assert.Equal(new MasterUsage(200, 40, 80), usage);
        Assert.All(api.Keys, key => Assert.Equal("Bearer clave-de-prueba", key));

        Assert.Equal(2, api.Requests.Count);
        var first = api.Requests[0].RootElement;
        Assert.Equal("claude-opus-5-5", first.GetProperty("model").GetString());
        Assert.Equal(MasterToolbox.Tools.Count, first.GetProperty("tools").GetArrayLength());
        Assert.Equal("object", first.GetProperty("tools")[1].GetProperty("input_schema").GetProperty("type").GetString());
        Assert.Equal("ephemeral", first.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Contains("Kael dice: Me planto en el puente.", first.GetProperty("messages")[0].GetProperty("content").GetString());

        var messages = api.Requests[1].RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        var echoed = messages[1].GetProperty("content");
        Assert.Equal(new[] { "thinking", "text", "tool_use", "tool_use" }, echoed.EnumerateArray().Select(b => b.GetProperty("type").GetString()));
        Assert.Equal("firma", echoed[0].GetProperty("signature").GetString());
        var results = messages[2].GetProperty("content");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("toolu_1", results[0].GetProperty("tool_use_id").GetString());
        Assert.Contains("Total: 11", results[0].GetProperty("content").GetString());
        Assert.True(results[1].GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public async Task ABadKeyIsExplainedInPlainSpanish()
    {
        using var api = new FakeApi((401, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}"""));
        var game = MasterToolboxTests.NewGame();
        var master = new ClaudeMaster("mala", "claude-opus-5-5", api.Url);

        var error = await Assert.ThrowsAsync<MasterException>(() =>
            master.PlayTurnAsync(game, new MasterToolbox(game, new Always(1)), "Kael", "Hola", _ => { }));
        Assert.Contains("clave", error.Message);
    }

    [Fact]
    public async Task ARefusalDoesNotPretendToBeNarration()
    {
        using var api = new FakeApi((200, Reply("refusal", "")));
        var game = MasterToolboxTests.NewGame();
        var master = new ClaudeMaster("clave", "claude-opus-5-5", api.Url);
        await Assert.ThrowsAsync<MasterException>(() =>
            master.PlayTurnAsync(game, new MasterToolbox(game, new Always(1)), "Kael", "Hola", _ => { }));
    }

    [Fact]
    public async Task StopsIfTheMasterNeverStopsAskingForTools()
    {
        var loop = (200, Reply("tool_use", """{"type":"tool_use","id":"toolu_1","name":"consultar_mesa","input":{}}"""));
        using var api = new FakeApi(Enumerable.Repeat(loop, ClaudeMaster.MaxRounds + 3).ToArray());
        var game = MasterToolboxTests.NewGame();
        var master = new ClaudeMaster("clave", "claude-opus-5-5", api.Url);
        await Assert.ThrowsAsync<MasterException>(() =>
            master.PlayTurnAsync(game, new MasterToolbox(game, new Always(1)), "Kael", "Hola", _ => { }));
        Assert.Equal(ClaudeMaster.MaxRounds, api.Requests.Count);
    }

    [Fact]
    public async Task ReadsTheRulebookOnceAndReturnsACreationGuide()
    {
        var rules = new RulesLibrary();
        rules.Add(Rulebook.FromPages("Reglas de la casa", "casa.txt", new[]
        {
            new RulePage(1, "Creación de personaje. Elige una clase: guerrero o mago. Tira 3d6 para Fuerza y Destreza."),
            new RulePage(2, "Puntos de vida iniciales: el guerrero tira 1d10 y el mago 1d4."),
        }));
        using var api = new FakeApi(
            (200, Reply("tool_use", """{"type":"tool_use","id":"toolu_1","name":"buscar_reglas","input":{"consulta":"puntos de vida iniciales"}}""")),
            (200, Reply("tool_use", """{"type":"tool_use","id":"toolu_2","name":"guardar_guia","input":{"title":"Reglas de la casa","steps":[{"title":"Clase","kind":"choice","options":[{"label":"Guerrero"}]}]}}""")),
            (200, Reply("tool_use", """
                {"type":"tool_use","id":"toolu_3","name":"guardar_guia","input":{"title":"Reglas de la casa","steps":[
                  {"title":"Clase","kind":"choice","options":[{"label":"Guerrero","detail":"Aguanta"},{"label":"Mago","detail":"Conjura"}]},
                  {"title":"Características","kind":"roll","dice":"3d6","targets":["Fuerza","Destreza"]},
                  {"title":"Golpes","kind":"roll","dice":"1d10","entry":"Puntos de vida","hp":true,"when":"Clase","whenValues":["Guerrero"]},
                  {"title":"Golpes de mago","kind":"roll","dice":"1d4","entry":"Puntos de vida","hp":true,"when":"Clase","whenValues":["Mago"]}]}}
                """)));

        var master = new ClaudeMaster("clave", "claude-haiku-5-5", api.Url);
        var (guide, usage) = await master.BuildCreationGuideAsync(rules);

        Assert.Equal(("Reglas de la casa", "Reglas de la casa", 4), (guide.Title, guide.Source, guide.Steps.Count));
        Assert.Equal(300, usage.InputTokens);
        var first = api.Requests[0].RootElement;
        Assert.Equal(2, first.GetProperty("tools").GetArrayLength());
        Assert.Contains("Elige una clase", first.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Contains("1d10", api.Requests[1].RootElement.GetProperty("messages")[2].GetProperty("content")[0].GetProperty("content").GetString());
        // La primera guía no valía (una sola opción): se le dice por qué y la corrige.
        var rejected = api.Requests[2].RootElement.GetProperty("messages")[4].GetProperty("content")[0];
        Assert.True(rejected.GetProperty("is_error").GetBoolean());
        Assert.Contains("dos opciones", rejected.GetProperty("content").GetString());

        await Assert.ThrowsAsync<MasterException>(() => master.BuildCreationGuideAsync(new RulesLibrary()));
    }
}
