using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using ClawPilot.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Core.Services;

/// <summary>
/// Profile 加载服务 — 从 YAML 文件加载编排器人设配置
/// </summary>
public class ProfileService
{
    private readonly ILogger? _logger;
    private readonly IDeserializer _deserializer;

    public ProfileService(ILogger? logger = null)
    {
        _logger = logger;
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <summary>
    /// 从 YAML 文件加载 Profile
    /// </summary>
    public OrchestratorProfile? LoadProfile(string yamlPath)
    {
        try
        {
            var yaml = File.ReadAllText(yamlPath);
            return _deserializer.Deserialize<OrchestratorProfile>(yaml);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "加载 Profile 失败: {Path}", yamlPath);
            return null;
        }
    }

    /// <summary>
    /// 从目录加载所有 Profile
    /// </summary>
    public List<OrchestratorProfile> LoadAllProfiles(string profilesDir)
    {
        var profiles = new List<OrchestratorProfile>();
        if (!Directory.Exists(profilesDir)) return profiles;

        foreach (var file in Directory.GetFiles(profilesDir, "*.yaml"))
        {
            var profile = LoadProfile(file);
            if (profile != null) profiles.Add(profile);
        }

        foreach (var file in Directory.GetFiles(profilesDir, "*.yml"))
        {
            var profile = LoadProfile(file);
            if (profile != null) profiles.Add(profile);
        }

        return profiles;
    }
}

/// <summary>
/// 编排器人设配置 — 对应 YAML Profile 文件
/// </summary>
public class OrchestratorProfile
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "1.0";
    public bool IsBuiltin { get; set; }

    // 编排器人设
    public string OrchestratorPersona { get; set; } = "";
    public List<string> OrchestratorRules { get; set; } = [];
    public List<string> FocusDomains { get; set; } = [];

    // 行为配置
    public string DefaultDecisionMode { get; set; } = "fallback";
    public string ScheduleCron { get; set; } = "0 8 * * *";
    public int MaxDailyTasks { get; set; } = 20;

    // 草案配置
    public bool DraftAutoApprove { get; set; } = true;
    public int DraftAutoApproveAfterSeconds { get; set; } = 300;

    // 元数据
    public List<string> Tags { get; set; } = [];

    // 预设资源
    public List<PersonaPreset> PersonaPresets { get; set; } = [];
    public List<PlanPreset> PlanPresets { get; set; } = [];
    public List<PromptPreset> PromptPresets { get; set; } = [];
}

public class PersonaPreset
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public string TaskType { get; set; } = "openclaw";
    public int MaxConcurrent { get; set; } = 1;
    public List<string> Tags { get; set; } = [];
}

public class PlanPreset
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ScheduleCron { get; set; } = "0 8 * * *";
    public List<PlanItemPreset> Items { get; set; } = [];
}

public class PlanItemPreset
{
    public string PersonaName { get; set; } = "";
    public string Message { get; set; } = "";
    public string TaskType { get; set; } = "openclaw";
    public string Priority { get; set; } = "normal";
    public string? ScheduledTime { get; set; }
    public List<int> DependsOn { get; set; } = [];
}

public class PromptPreset
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Template { get; set; } = "";
    public List<string> Variables { get; set; } = [];
    public string DefaultAgent { get; set; } = "main";
}
