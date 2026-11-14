using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using RoleDMFable.Core;

namespace RoleDMFable.Master;

/// <summary>Un modelo de Claude que se puede elegir como máster.</summary>
/// <summary>Un modelo de Claude que se puede elegir como máster. Precios en dólares por millón de tokens.</summary>
public sealed record MasterModel(string Id, string Name, string Note, double InputPrice, double OutputPrice)
{
    /// <summary>Coste aproximado en dólares. Lo leído de caché se cobra a una décima parte.</summary>
    public double Cost(MasterUsage usage) =>
        (usage.InputTokens * InputPrice + usage.CachedTokens * InputPrice * 0.1 + usage.OutputTokens * OutputPrice) / 1_000_000;
}

/// <summary>
/// El máster llevado por Claude. Cada turno es una conversación nueva: instrucciones fijas,
/// estado de la mesa y lo que dice el jugador. Claude pide herramientas, <see cref="MasterToolbox"/>
/// las ejecuta y se le devuelve el resultado hasta que termina de narrar.
/// </summary>
public sealed class ClaudeMaster : IMaster
{
    public const string DefaultModel = "claude-opus-5-5";

    public static IReadOnlyList<MasterModel> Models { get; } = new[]
    {
        new MasterModel("claude-opus-5-5", "Claude Opus 5.5", "el mejor narrador, el más caro", 4, 20),
        new MasterModel("claude-sonnet-5-5", "Claude Sonnet 5.5", "equilibrado, la mitad de precio", 2, 10),
        new MasterModel("claude-haiku-5-5", "Claude Haiku 5.5", "rápido y muy barato", 0.10, 0.50),
    };

    /// <summary>Tope de idas y vueltas con herramientas en un turno, para que un error no gaste sin fin.</summary>
    public const int MaxRounds = 12;

    private const int MaxTokens = 8000;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private readonly AnthropicClient _client;
    private readonly string _model;

    public ClaudeMaster(string apiKey, string model, string? baseUrl = null)
    {
        _model = model;
        _client = baseUrl is null
            ? new AnthropicClient { ApiKey = apiKey, Timeout = Timeout }
            : new AnthropicClient { ApiKey = apiKey, Timeout = Timeout, BaseUrl = baseUrl };
    }

    public async Task<MasterUsage> PlayTurnAsync(Campaign game, MasterToolbox tools, string speaker, string text, Action<string> narrate, CancellationToken cancel = default)
    {
        var messages = new List<MessageParam>
        {
            new UserMessageParam { Content = MasterPrompt.BuildTurn(game, speaker, text, tools.Rules) },
        };
        long input = 0, output = 0, cached = 0;

        for (var round = 0; round < MaxRounds; round++)
        {
            Message response;
            try
            {
                response = await _client.Messages.Create(new MessageCreateParams
                {
                    Model = _model,
                    MaxTokens = MaxTokens,
                    System = new List<TextBlockParam>
                    {
                        new() { Text = MasterPrompt.Instructions, CacheControl = new CacheControlEphemeral() },
                    },
                    Tools = ToolDefinitions,
                    Messages = messages,
                }, cancellationToken: cancel);
            }
            catch (AnthropicApiException e)
            {
                throw new MasterException(Explain(e), e);
            }
            catch (AnthropicIOException e)
            {
                throw new MasterException("No se pudo conectar con Claude. Comprueba tu conexión a internet y vuelve a intentarlo.", e);
            }
            catch (HttpRequestException e)
            {
                throw new MasterException("No se pudo conectar con Claude. Comprueba tu conexión a internet y vuelve a intentarlo.", e);
            }

            input += response.Usage.InputTokens;
            output += response.Usage.OutputTokens;
            cached += response.Usage.CacheReadInputTokens ?? 0;

            // La respuesta se devuelve entera en la siguiente petición, pensamiento incluido.
            var echo = new List<ContentBlockParam>();
            var results = new List<ContentBlockParam>();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var said))
                {
                    echo.Add(new TextBlockParam { Text = said.Text });
                    if (!string.IsNullOrWhiteSpace(said.Text)) narrate(said.Text.Trim());
                }
                else if (block.TryPickThinking(out var thinking))
                {
                    echo.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                }
                else if (block.TryPickRedactedThinking(out var redacted))
                {
                    echo.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickToolUse(out var call))
                {
                    echo.Add(new ToolUseBlockParam { ID = call.ID, Name = call.Name, Input = call.Input });
                    var outcome = tools.Execute(call.Name, call.Input);
                    results.Add(new ToolResultBlockParam { ToolUseID = call.ID, Content = outcome.Result, IsError = outcome.IsError });
                }
            }

            var stop = response.StopReason?.Raw();
            if (stop == "refusal")
                throw new MasterException("El máster no ha querido responder a eso. Prueba a decirlo de otra forma.");
            if (stop == "max_tokens" && results.Count == 0)
                throw new MasterException("La respuesta del máster se ha cortado por ser demasiado larga. Vuelve a intentarlo.");
            if (results.Count == 0) return new MasterUsage(input, output, cached);

            messages.Add(new AssistantMessageParam { Content = echo });
            messages.Add(new UserMessageParam { Content = results });
        }

        throw new MasterException("El máster ha encadenado demasiadas acciones seguidas y se ha parado el turno. Lo hecho hasta ahora queda en la mesa.");
    }

    private static readonly IReadOnlyList<ToolUnion> ToolDefinitions = MasterToolbox.Tools.Select(ToTool).ToList();

    private static ToolUnion ToTool(MasterTool tool)
    {
        using var schema = JsonDocument.Parse(tool.SchemaJson);
        var properties = schema.RootElement.GetProperty("properties").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        var required = schema.RootElement.TryGetProperty("required", out var list)
            ? list.EnumerateArray().Select(item => item.GetString()!).ToList()
            : new List<string>();
        return new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new InputSchema { Properties = properties, Required = required },
        };
    }

    private static string Explain(AnthropicApiException e) => e switch
    {
        AnthropicUnauthorizedException => "Claude no acepta la clave. Revisa que la hayas copiado entera en «Máster IA».",
        AnthropicForbiddenException => "Esa clave no tiene permiso para usar este modelo. Prueba con otro modelo en «Máster IA».",
        AnthropicNotFoundException => "Claude no reconoce el modelo elegido. Elige otro en «Máster IA».",
        AnthropicRateLimitException => "Claude está recibiendo demasiadas peticiones con tu clave. Espera un minuto y vuelve a intentarlo.",
        Anthropic5xxException => "Claude está saturado o con problemas en este momento. Vuelve a intentarlo en un rato.",
        _ when (int)e.StatusCode == 402 || e.Message.Contains("credit balance", StringComparison.OrdinalIgnoreCase)
            => "Tu cuenta de Claude no tiene saldo. Añade crédito en la consola de Claude (Facturación) y vuelve a intentarlo.",
        _ => $"Claude ha rechazado la petición ({(int)e.StatusCode}). {e.Message}",
    };
}
