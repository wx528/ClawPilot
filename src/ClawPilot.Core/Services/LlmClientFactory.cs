using Microsoft.Extensions.Logging;

namespace ClawPilot.Core.Services;

public class LlmClientFactory
{
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Dictionary<string, ILlmClient> _cache = new();

    public LlmClientFactory(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
    }

    public ILlmClient GetOrCreate(string apiKey, string baseUrl, string model)
    {
        var cacheKey = $"{apiKey}:{baseUrl}:{model}";

        if (_cache.TryGetValue(cacheKey, out var existing))
            return existing;

        var logger = _loggerFactory?.CreateLogger<OpenAILlmClient>();
        var client = new OpenAILlmClient(apiKey, baseUrl, model, logger);
        _cache[cacheKey] = client;
        return client;
    }

    public void ClearCache()
    {
        foreach (var client in _cache.Values)
        {
            if (client is IDisposable disposable)
                disposable.Dispose();
        }
        _cache.Clear();
    }
}
