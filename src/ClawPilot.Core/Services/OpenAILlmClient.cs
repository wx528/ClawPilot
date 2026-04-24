using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Core.Services;

/// <summary>
/// OpenAI API 兼容客户端
/// 支持：OpenAI、Azure OpenAI、阿里百炼、DeepSeek、Kimi、本地 vLLM/Ollama 等
/// </summary>
public class OpenAILlmClient : ILlmClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _defaultModel;
    private readonly ILogger? _logger;

    public OpenAILlmClient(
        string apiKey,
        string baseUrl,
        string defaultModel,
        ILogger? logger = null,
        HttpClient? httpClient = null)
    {
        _apiKey = apiKey;
        _baseUrl = baseUrl.TrimEnd('/');
        _defaultModel = defaultModel;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
    }

    public async Task<string> ChatCompletionAsync(
        string systemPrompt,
        string userPrompt,
        string? model = null,
        double temperature = 0.3,
        CancellationToken ct = default)
    {
        var requestBody = new
        {
            model = model ?? _defaultModel,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature
        };

        try
        {
            var url = $"{_baseUrl}/chat/completions";
            _logger?.LogDebug("LLM 请求: {Url}, Model: {Model}", url, requestBody.model);

            using var response = await _httpClient.PostAsJsonAsync(url, requestBody, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(_jsonOptions, ct);
            var content = result?.Choices?.FirstOrDefault()?.Message?.Content ?? "";

            _logger?.LogDebug("LLM 响应长度: {Length}", content.Length);
            return content;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "LLM 请求失败");
            throw;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] List<ChatChoice> Choices);

    private record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage Message);

    private record ChatMessage(
        [property: JsonPropertyName("content")] string Content);
}
