namespace ClawPilot.Core.Models;

public class LlmProvider
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string DefaultModel { get; set; } = "";

    public static List<LlmProvider> BuiltInProviders =>
    [
        new() { Name = "custom", DisplayName = "Custom", BaseUrl = "", DefaultModel = "" },
        new() { Name = "deepseek", DisplayName = "DeepSeek", BaseUrl = "https://api.deepseek.com", DefaultModel = "deepseek-chat" },
        new() { Name = "openai", DisplayName = "OpenAI", BaseUrl = "https://api.openai.com/v1", DefaultModel = "gpt-4o" },
        new() { Name = "openrouter", DisplayName = "OpenRouter", BaseUrl = "https://openrouter.ai/api/v1", DefaultModel = "openai/gpt-4o" },
        new() { Name = "moonshot", DisplayName = "Moonshot (Kimi)", BaseUrl = "https://api.moonshot.cn/v1", DefaultModel = "moonshot-v1-128k" },
        new() { Name = "zhipu", DisplayName = "ZhipuAI (GLM)", BaseUrl = "https://open.bigmodel.cn/api/paas/v4", DefaultModel = "glm-4-plus" },
        new() { Name = "qwen", DisplayName = "Qwen (DashScope)", BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1", DefaultModel = "qwen-max" },
        new() { Name = "siliconflow", DisplayName = "SiliconFlow", BaseUrl = "https://api.siliconflow.cn/v1", DefaultModel = "deepseek-ai/DeepSeek-V3" },
        new() { Name = "ollama", DisplayName = "Ollama (Local)", BaseUrl = "http://localhost:11434/v1", DefaultModel = "llama3" },
        new() { Name = "vllm", DisplayName = "vLLM (Local)", BaseUrl = "http://localhost:8000/v1", DefaultModel = "" }
    ];
}
