using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace ClawPilot.Tests.E2E;

public class MockLlmServer : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly List<ReceivedRequest> _receivedRequests = new();
    private Func<ChatCompletionRequest, string>? _responseFactory;

    public string BaseUrl { get; }
    public IReadOnlyList<ReceivedRequest> ReceivedRequests => _receivedRequests;

    public MockLlmServer(int? port = null)
    {
        port ??= GetAvailablePort();
        BaseUrl = $"http://localhost:{port}";
    }

    public void SetResponseFactory(Func<ChatCompletionRequest, string> factory)
    {
        _responseFactory = factory;
    }

    public void SetFixedResponse(string content)
    {
        _responseFactory = _ => content;
    }

    public void SetDecisionResponse(AutopilotDecisionBuilder decision)
    {
        _responseFactory = _ => decision.BuildJson();
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(BaseUrl);

        _app = builder.Build();

        _app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();

            var request = JsonSerializer.Deserialize<ChatCompletionRequest>(body, _jsonOptions);
            if (request == null)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("Invalid request body");
                return;
            }

            lock (_receivedRequests)
            {
                _receivedRequests.Add(new ReceivedRequest
                {
                    Model = request.Model,
                    SystemPrompt = request.Messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "",
                    UserPrompt = request.Messages.FirstOrDefault(m => m.Role == "user")?.Content ?? "",
                    ReceivedAt = DateTime.UtcNow
                });
            }

            var responseContent = _responseFactory != null
                ? _responseFactory(request)
                : BuildDefaultResponse();

            var response = new
            {
                id = $"chatcmpl-{Guid.NewGuid():N}",
                @object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = request.Model ?? "mock-model",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = responseContent },
                        finish_reason = "stop"
                    }
                },
                usage = new { prompt_tokens = 100, completion_tokens = 200, total_tokens = 300 }
            };

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(response, _jsonOptions));
        });

        _app.MapGet("/v1/models", () =>
        {
            var models = new
            {
                data = new[]
                {
                    new { id = "mock-model", @object = "model", owned_by = "test" }
                }
            };
            return Results.Json(models);
        });

        await _app.StartAsync();
    }

    public async Task StopAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.DisposeAsync();
        }
    }

    private static string BuildDefaultResponse()
    {
        var decision = new AutopilotDecisionBuilder();
        decision.AddTask("mock task", "main", "openclaw");
        decision.SetWhiteboardUpdate("Mock LLM response - no tasks configured");
        return decision.BuildJson();
    }

    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public class ReceivedRequest
{
    public string Model { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public string UserPrompt { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
}

public class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }
    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();
    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }
}

public class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

public class AutopilotDecisionBuilder
{
    private string _decisionType = "add_tasks";
    private string _reasoning = "E2E test decision";
    private readonly List<object> _tasks = new();
    private string _whiteboardUpdate = "";

    public AutopilotDecisionBuilder SetDecisionType(string type) { _decisionType = type; return this; }
    public AutopilotDecisionBuilder SetReasoning(string reasoning) { _reasoning = reasoning; return this; }
    public AutopilotDecisionBuilder SetWhiteboardUpdate(string update) { _whiteboardUpdate = update; return this; }

    public AutopilotDecisionBuilder AddTask(string message, string personaName = "main",
        string taskType = "openclaw", string priority = "normal", string reason = "",
        int? dependsOnTaskId = null, string? chainId = null, int chainRound = 1)
    {
        _tasks.Add(new
        {
            persona_name = personaName,
            message,
            task_type = taskType,
            priority,
            reason,
            depends_on_task_id = dependsOnTaskId,
            chain_id = chainId,
            chain_round = chainRound
        });
        return this;
    }

    public string BuildJson()
    {
        var decision = new
        {
            decision_type = _decisionType,
            reasoning = _reasoning,
            tasks_to_add = _tasks,
            whiteboard_update = _whiteboardUpdate
        };
        return JsonSerializer.Serialize(decision);
    }
}
