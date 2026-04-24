namespace ClawPilot.Core.Services;

/// <summary>
/// LLM 客户端抽象接口
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// 发送对话请求，返回模型生成的文本内容
    /// </summary>
    Task<string> ChatCompletionAsync(
        string systemPrompt,
        string userPrompt,
        string? model = null,
        double temperature = 0.3,
        CancellationToken ct = default);
}
