using ClawPilot.Core.Services;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Tests;

public class ProfileServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ProfileService _service;

    public ProfileServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"clawpilot_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _service = new ProfileService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    private void WriteYaml(string fileName, string content)
    {
        File.WriteAllText(Path.Combine(_tempDir, fileName), content);
    }

    [Fact]
    public void LoadProfile_ValidYaml_ReturnsProfile()
    {
        WriteYaml("test.yaml", @"
name: test
display_name: ""Test Profile""
description: ""A test profile""
version: ""1.0""
is_builtin: false

orchestrator_persona: ""You are a test orchestrator.""
orchestrator_rules:
  - ""Rule 1""
  - ""Rule 2""
focus_domains:
  - ""testing""

default_decision_mode: llm_only
schedule_cron: ""0 */1 * * *""
max_daily_tasks: 30

tags:
  - ""test""

persona_presets:
  - name: coder
    display_name: ""Coder""
    description: ""Writes code""
    system_prompt: ""You write code.""
    task_type: codebuddy
    max_concurrent: 2
    tags:
      - ""coding""
");

        var profile = _service.LoadProfile(Path.Combine(_tempDir, "test.yaml"));

        Assert.NotNull(profile);
        Assert.Equal("test", profile!.Name);
        Assert.Equal("Test Profile", profile.DisplayName);
        Assert.Equal("You are a test orchestrator.", profile.OrchestratorPersona);
        Assert.Equal(2, profile.OrchestratorRules.Count);
        Assert.Contains("testing", profile.FocusDomains);
        Assert.Equal("llm_only", profile.DefaultDecisionMode);
        Assert.Equal(30, profile.MaxDailyTasks);
        Assert.Single(profile.PersonaPresets);
        Assert.Equal("coder", profile.PersonaPresets[0].Name);
        Assert.Equal("codebuddy", profile.PersonaPresets[0].TaskType);
        Assert.Equal(2, profile.PersonaPresets[0].MaxConcurrent);
    }

    [Fact]
    public void LoadProfile_NonexistentFile_ReturnsNull()
    {
        var profile = _service.LoadProfile(Path.Combine(_tempDir, "nonexistent.yaml"));
        Assert.Null(profile);
    }

    [Fact]
    public void LoadProfile_InvalidYaml_ReturnsNull()
    {
        WriteYaml("invalid.yaml", "this: is: not: valid: yaml: {{{");

        var profile = _service.LoadProfile(Path.Combine(_tempDir, "invalid.yaml"));
        Assert.Null(profile);
    }

    [Fact]
    public void LoadProfile_MinimalYaml_UsesDefaults()
    {
        WriteYaml("minimal.yaml", @"
name: minimal
");

        var profile = _service.LoadProfile(Path.Combine(_tempDir, "minimal.yaml"));

        Assert.NotNull(profile);
        Assert.Equal("minimal", profile!.Name);
        Assert.Equal("", profile.DisplayName);
        Assert.Equal("1.0", profile.Version);
        Assert.False(profile.IsBuiltin);
        Assert.Empty(profile.OrchestratorRules);
        Assert.Empty(profile.FocusDomains);
        Assert.Equal(20, profile.MaxDailyTasks);
        Assert.Empty(profile.PersonaPresets);
    }

    [Fact]
    public void LoadProfile_WithPromptPresets_ParsesCorrectly()
    {
        WriteYaml("prompts.yaml", @"
name: with_prompts
prompt_presets:
  - name: summary
    description: ""Summary template""
    template: ""Summarize: {{content}}""
    variables:
      - ""content""
    default_agent: main
");

        var profile = _service.LoadProfile(Path.Combine(_tempDir, "prompts.yaml"));

        Assert.NotNull(profile);
        Assert.Single(profile!.PromptPresets);
        Assert.Equal("summary", profile.PromptPresets[0].Name);
        Assert.Equal("Summarize: {{content}}", profile.PromptPresets[0].Template);
        Assert.Contains("content", profile.PromptPresets[0].Variables);
        Assert.Equal("main", profile.PromptPresets[0].DefaultAgent);
    }

    [Fact]
    public void LoadAllProfiles_LoadsYamlAndYml()
    {
        WriteYaml("profile1.yaml", "name: profile1");
        WriteYaml("profile2.yml", "name: profile2");
        WriteYaml("readme.txt", "not a profile");

        var profiles = _service.LoadAllProfiles(_tempDir);

        Assert.Equal(2, profiles.Count);
        Assert.Contains(profiles, p => p.Name == "profile1");
        Assert.Contains(profiles, p => p.Name == "profile2");
    }

    [Fact]
    public void LoadAllProfiles_EmptyDirectory_ReturnsEmptyList()
    {
        var profiles = _service.LoadAllProfiles(_tempDir);
        Assert.Empty(profiles);
    }

    [Fact]
    public void LoadAllProfiles_NonexistentDirectory_ReturnsEmptyList()
    {
        var profiles = _service.LoadAllProfiles(Path.Combine(_tempDir, "nonexistent"));
        Assert.Empty(profiles);
    }

    [Fact]
    public void LoadAllProfiles_SkipsInvalidFiles()
    {
        WriteYaml("valid.yaml", "name: valid_profile");
        WriteYaml("invalid.yaml", "bad: {{{yaml");

        var profiles = _service.LoadAllProfiles(_tempDir);

        Assert.Single(profiles);
        Assert.Equal("valid_profile", profiles[0].Name);
    }
}
