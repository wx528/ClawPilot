using ClawPilot.Core.Models;
using ClawPilot.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace ClawPilot.App.ViewModels;

public partial class AutopilotViewModel : ObservableObject
{
    private readonly AutopilotOrchestrator _autopilot;
    private readonly OrchestratorStorageService _storage;
    private readonly ILogger? _logger;
    private System.Windows.Threading.DispatcherTimer? _statusTimer;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _elapsedText = "--";

    [ObservableProperty]
    private string _lastRunText = "--";

    [ObservableProperty]
    private string _nextRunText = "--";

    [ObservableProperty]
    private string _goalTitle = "";

    [ObservableProperty]
    private string _goalDescription = "";

    [ObservableProperty]
    private string _whiteboardContent = "";

    [ObservableProperty]
    private string _statusMessage = "自动驾驶未启动";

    [ObservableProperty]
    private string _lastError = "";

    [ObservableProperty]
    private int _totalSessions;

    [ObservableProperty]
    private int _totalTasksScheduled;

    [ObservableProperty]
    private bool _isGoalEditing;

    [ObservableProperty]
    private int _intervalMinutes = 60;

    [ObservableProperty]
    private bool _adaptiveIntervalEnabled = false;

    [ObservableProperty]
    private string _agentName = "main";

    [ObservableProperty]
    private int _selectedExecutorType;

    [ObservableProperty]
    private int _selectedMode;

    [ObservableProperty]
    private bool _isConfigDirty;

    [ObservableProperty]
    private bool _isExecutorAuto;

    /// <summary>
    /// 记住用户手动选择的执行器类型，切换 Auto 时可恢复
    /// </summary>
    private int _lastManualExecutorType;

    // ==================== 多预设 Tab 支持 ====================

    /// <summary>
    /// 所有编排者预设
    /// </summary>
    public ObservableCollection<OrchestratorPreset> Presets { get; } = new();

    [ObservableProperty]
    private int _selectedPresetIndex;

    [ObservableProperty]
    private OrchestratorPreset? _selectedPreset;

    [ObservableProperty]
    private string _presetPersonaPrompt = "";

    public bool IsIntervalEditable => !AdaptiveIntervalEnabled;
    public bool IsExecutorTypeEditable => !IsExecutorAuto;

    partial void OnAdaptiveIntervalEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIntervalEditable));
        IsConfigDirty = true;
        SyncCurrentConfigToPreset();
    }

    partial void OnIntervalMinutesChanged(int value) { IsConfigDirty = true; SyncCurrentConfigToPreset(); }
    partial void OnAgentNameChanged(string value) { IsConfigDirty = true; SyncCurrentConfigToPreset(); }
    partial void OnSelectedExecutorTypeChanged(int value) { IsConfigDirty = true; SyncCurrentConfigToPreset(); }
    partial void OnSelectedModeChanged(int value) { IsConfigDirty = true; SyncCurrentConfigToPreset(); }
    partial void OnIsExecutorAutoChanged(bool value)
    {
        IsConfigDirty = true;
        OnPropertyChanged(nameof(IsExecutorTypeEditable));
        if (value)
        {
            _lastManualExecutorType = SelectedExecutorType;
        }
        else
        {
            SelectedExecutorType = _lastManualExecutorType;
        }
        SyncCurrentConfigToPreset();
    }

    partial void OnSelectedPresetIndexChanged(int value)
    {
        if (value >= 0 && value < Presets.Count)
        {
            SwitchToPreset(Presets[value]);
        }
    }

    partial void OnPresetPersonaPromptChanged(string value)
    {
        if (SelectedPreset != null)
        {
            SelectedPreset.PersonaPrompt = value;
            IsConfigDirty = true;
        }
    }

    public ObservableCollection<OrchestrationSession> Sessions { get; } = new();

    public AutopilotViewModel(
        AutopilotOrchestrator autopilot,
        OrchestratorStorageService storage,
        ILogger<AutopilotViewModel>? logger = null)
    {
        _autopilot = autopilot;
        _storage = storage;
        _logger = logger;

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            LoadPresets();
            await LoadGoalAsync();
            await LoadWhiteboardAsync();
            await RefreshSessionsAsync();
            await RefreshStatusAsync();
            await LoadIntervalAsync();

            // 启动 UI 刷新定时器
            _statusTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _statusTimer.Tick += async (s, e) => await RefreshStatusAsync();
            _statusTimer.Start();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AutopilotViewModel 初始化失败");
        }
    }

    // ==================== 预设管理 ====================

    /// <summary>
    /// 从 settings.json 加载预设，如果不存在则使用内置预设
    /// </summary>
    private void LoadPresets()
    {
        try
        {
            if (File.Exists(App.SettingsPath))
            {
                var json = File.ReadAllText(App.SettingsPath);
                var settings = System.Text.Json.JsonSerializer.Deserialize<LlmSettings>(json);
                if (settings?.OrchestratorPresets?.Count > 0)
                {
                    foreach (var p in settings.OrchestratorPresets)
                        Presets.Add(p);

                    // 恢复上次激活的预设
                    var activeId = settings.ActivePresetId;
                    var idx = Presets.ToList().FindIndex(p => p.Id == activeId);
                    SelectedPresetIndex = idx >= 0 ? idx : 0;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "加载预设失败，使用内置预设");
        }

        // 使用内置预设
        foreach (var p in OrchestratorPreset.CreateBuiltInPresets())
            Presets.Add(p);

        SelectedPresetIndex = 0;
    }

    /// <summary>
    /// 切换到指定预设 — 保存当前配置到旧预设，加载新预设的配置到 UI
    /// </summary>
    private void SwitchToPreset(OrchestratorPreset preset)
    {
        SelectedPreset = preset;

        // 从预设加载配置到 UI
        GoalTitle = preset.GoalTitle;
        GoalDescription = preset.GoalDescription;
        IntervalMinutes = preset.IntervalMinutes;
        AdaptiveIntervalEnabled = preset.AdaptiveIntervalEnabled;
        SelectedMode = preset.SelectedMode;
        SelectedExecutorType = preset.SelectedExecutorType;
        IsExecutorAuto = preset.IsExecutorAuto;
        AgentName = preset.AgentName;
        PresetPersonaPrompt = preset.PersonaPrompt;

        IsConfigDirty = false;

        // 同步到 Orchestrator
        ApplyPresetToOrchestrator(preset);

        // 将预设的目标写入数据库，避免 RefreshStatusAsync 覆盖 UI
        _ = SyncPresetGoalToDatabaseAsync(preset);

        _logger?.LogInformation("切换到编排者预设: {Name}", preset.DisplayName);
    }

    /// <summary>
    /// 将预设的 Goal 同步到数据库（使 Orchestrator 和 RefreshStatusAsync 读到正确的目标）
    /// </summary>
    private async Task SyncPresetGoalToDatabaseAsync(OrchestratorPreset preset)
    {
        try
        {
            var existing = await _storage.GetActiveGoalAsync();
            if (existing != null)
            {
                await _storage.UpdateGoalAsync(existing.Id, preset.GoalTitle, preset.GoalDescription);
            }
            else
            {
                await _storage.CreateGoalAsync(preset.GoalTitle, preset.GoalDescription);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "同步预设目标到数据库失败");
        }
    }

    /// <summary>
    /// 将当前 UI 配置同步回 SelectedPreset
    /// </summary>
    private void SyncCurrentConfigToPreset()
    {
        if (SelectedPreset == null) return;

        SelectedPreset.GoalTitle = GoalTitle;
        SelectedPreset.GoalDescription = GoalDescription;
        SelectedPreset.IntervalMinutes = IntervalMinutes;
        SelectedPreset.AdaptiveIntervalEnabled = AdaptiveIntervalEnabled;
        SelectedPreset.SelectedMode = SelectedMode;
        SelectedPreset.SelectedExecutorType = SelectedExecutorType;
        SelectedPreset.IsExecutorAuto = IsExecutorAuto;
        SelectedPreset.AgentName = AgentName;
        SelectedPreset.PersonaPrompt = PresetPersonaPrompt;
    }

    /// <summary>
    /// 将预设配置应用到 Orchestrator 运行时
    /// </summary>
    private void ApplyPresetToOrchestrator(OrchestratorPreset preset)
    {
        _autopilot.Interval = TimeSpan.FromMinutes(preset.IntervalMinutes);
        _autopilot.AdaptiveIntervalEnabled = preset.AdaptiveIntervalEnabled;
        _autopilot.Mode = (Core.Models.AutopilotMode)preset.SelectedMode;
        _autopilot.ExecutorType = preset.IsExecutorAuto
            ? Core.Models.ExecutorType.Auto
            : (Core.Models.ExecutorType)preset.SelectedExecutorType;
        _autopilot.AgentName = preset.AgentName;
        _autopilot.PersonaPrompt = preset.PersonaPrompt;
    }

    /// <summary>
    /// 保存预设到 settings.json
    /// </summary>
    private async Task SavePresetsToSettingsAsync()
    {
        SyncCurrentConfigToPreset();
        try
        {
            LlmSettings settings;
            if (File.Exists(App.SettingsPath))
            {
                var json = await File.ReadAllTextAsync(App.SettingsPath);
                settings = System.Text.Json.JsonSerializer.Deserialize<LlmSettings>(json) ?? new LlmSettings();
            }
            else
            {
                settings = new LlmSettings();
            }

            settings.OrchestratorPresets = Presets.ToList();
            settings.ActivePresetId = SelectedPreset?.Id;

            var newJson = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(App.SettingsPath, newJson);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "保存预设到 settings 失败");
        }
    }

    [RelayCommand]
    private void AddPreset()
    {
        var newPreset = new OrchestratorPreset
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            DisplayName = "新编排者",
            Description = "自定义编排者",
            Icon = "✨",
            GoalTitle = GoalTitle,
            GoalDescription = GoalDescription,
            IntervalMinutes = IntervalMinutes,
            AdaptiveIntervalEnabled = AdaptiveIntervalEnabled,
            SelectedMode = SelectedMode,
            SelectedExecutorType = SelectedExecutorType,
            IsExecutorAuto = IsExecutorAuto,
            AgentName = AgentName,
            PersonaPrompt = PresetPersonaPrompt
        };

        Presets.Add(newPreset);
        SelectedPresetIndex = Presets.Count - 1;
        IsConfigDirty = true;
    }

    [RelayCommand]
    private void RemovePreset()
    {
        if (Presets.Count <= 1)
        {
            MessageBox.Show("至少保留一个编排者预设", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (SelectedPreset == null) return;

        var currentIdx = SelectedPresetIndex;
        Presets.RemoveAt(currentIdx);

        // 选中相邻的
        SelectedPresetIndex = Math.Min(currentIdx, Presets.Count - 1);
        IsConfigDirty = true;
    }

    // ==================== 命令 ====================

    [RelayCommand]
    private async Task ToggleAutopilot()
    {
        try
        {
            if (_autopilot.IsRunning)
            {
                _autopilot.Stop();
                IsRunning = false;
                StatusMessage = "自动驾驶已停止";
            }
            else
            {
                // 确保有目标
                var goal = await _storage.GetActiveGoalAsync();
                if (goal == null && string.IsNullOrWhiteSpace(GoalTitle))
                {
                    MessageBox.Show("请先设置任务目标", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (goal == null)
                {
                    await SaveGoalInternalAsync();
                }

                // 启动前确保当前预设配置已同步
                SyncCurrentConfigToPreset();
                ApplyPresetToOrchestrator(SelectedPreset!);

                await _autopilot.StartAsync();
                IsRunning = true;
                StatusMessage = "自动驾驶运行中";
            }

            await RefreshStatusAsync();
            await SavePresetsToSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "切换自动驾驶状态失败");
            MessageBox.Show($"操作失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task TriggerNow()
    {
        try
        {
            if (!_autopilot.IsRunning)
            {
                MessageBox.Show("请先启动自动驾驶模式", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StatusMessage = "正在手动触发编排周期...";
            await _autopilot.TriggerNowAsync();
            StatusMessage = "手动触发完成";
            await RefreshStatusAsync();
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "手动触发编排周期失败");
            MessageBox.Show($"触发失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task SaveGoal()
    {
        try
        {
            await SaveGoalInternalAsync();
            SyncCurrentConfigToPreset();
            await SavePresetsToSettingsAsync();
            IsGoalEditing = false;
            MessageBox.Show("目标已保存", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "保存目标失败");
            MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task SaveWhiteboard()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(WhiteboardContent))
            {
                await _storage.UpdateWhiteboardAsync(WhiteboardContent);
                MessageBox.Show("白板已保存", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "保存白板失败");
            MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task UpdateInterval()
    {
        try
        {
            SyncCurrentConfigToPreset();

            LlmSettings settings;
            if (File.Exists(App.SettingsPath))
            {
                var json = await File.ReadAllTextAsync(App.SettingsPath);
                settings = System.Text.Json.JsonSerializer.Deserialize<LlmSettings>(json) ?? new LlmSettings();
            }
            else
            {
                settings = new LlmSettings();
            }
            settings.AutopilotIntervalMinutes = IntervalMinutes;
            settings.AdaptiveIntervalEnabled = AdaptiveIntervalEnabled;
            settings.AutopilotAgentName = AgentName;
            settings.ExecutorType = IsExecutorAuto ? ExecutorType.Auto : (ExecutorType)SelectedExecutorType;
            settings.AutopilotMode = (AutopilotMode)SelectedMode;
            settings.OrchestratorPresets = Presets.ToList();
            settings.ActivePresetId = SelectedPreset?.Id;
            var newJson = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(App.SettingsPath, newJson);

            ApplyPresetToOrchestrator(SelectedPreset!);
            var newInterval = TimeSpan.FromMinutes(IntervalMinutes);
            await _autopilot.RestartAsync(newInterval);

            IsConfigDirty = false;
            await RefreshStatusAsync();
            var modeText = (AutopilotMode)SelectedMode == AutopilotMode.ReAct ? "ReAct 模式" : "Plan-and-Execute 模式";
            var presetName = SelectedPreset?.DisplayName ?? "未知";
            MessageBox.Show($"编排者「{presetName}」配置已更新（{modeText}），已立即生效。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "更新编排配置失败");
            MessageBox.Show($"更新失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task RefreshStatus()
    {
        await RefreshStatusAsync();
        await RefreshSessionsAsync();
    }

    [RelayCommand]
    private void EditGoal()
    {
        IsGoalEditing = true;
    }

    [RelayCommand]
    private void CancelEditGoal()
    {
        IsGoalEditing = false;
        _ = LoadGoalAsync();
    }

    // ==================== 内部方法 ====================

    private async Task RefreshStatusAsync()
    {
        try
        {
            IsRunning = _autopilot.IsRunning;
            var status = await _autopilot.GetStatusAsync();

            ElapsedText = FormatElapsed(status.ElapsedSinceStart);
            LastRunText = status.LastRunAt?.ToString("HH:mm:ss") ?? "--";
            NextRunText = status.NextRunAt?.ToString("HH:mm:ss") ?? "--";
            // 不再用数据库目标覆盖 UI — 目标由预设管理
            TotalSessions = status.TotalSessions;
            TotalTasksScheduled = status.TotalTasksScheduled;
            LastError = status.LastError ?? "";

            // 自适应模式下，同步 orchestrator 的实际间隔值到 UI
            if (_autopilot.AdaptiveIntervalEnabled)
            {
                IntervalMinutes = (int)_autopilot.Interval.TotalMinutes;
            }

            if (!string.IsNullOrEmpty(LastError))
            {
                StatusMessage = $"运行中 - 上次错误: {LastError}";
            }
            else if (IsRunning)
            {
                var presetName = SelectedPreset?.DisplayName ?? "自动驾驶";
                StatusMessage = $"{presetName} 运行中 - 下次唤醒 {NextRunText}";
            }
            else
            {
                StatusMessage = "自动驾驶未启动";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "刷新状态失败");
        }
    }

    private async Task RefreshSessionsAsync()
    {
        try
        {
            var sessions = await _storage.ListSessionsAsync(20);

            // UTC → 本地时区转换（SQLite CURRENT_TIMESTAMP 返回 UTC）
            foreach (var session in sessions)
            {
                if (DateTime.TryParse(session.TriggeredAt, out var triggeredUtc))
                {
                    session.TriggeredAt = triggeredUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                }
                if (!string.IsNullOrEmpty(session.CompletedAt) &&
                    DateTime.TryParse(session.CompletedAt, out var completedUtc))
                {
                    session.CompletedAt = completedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                }
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Sessions.Clear();
                foreach (var session in sessions)
                {
                    Sessions.Add(session);
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "刷新会话列表失败");
        }
    }

    private async Task LoadIntervalAsync()
    {
        try
        {
            if (File.Exists(App.SettingsPath))
            {
                var json = await File.ReadAllTextAsync(App.SettingsPath);
                var settings = System.Text.Json.JsonSerializer.Deserialize<LlmSettings>(json);
                if (settings != null)
                {
                    if (settings.AutopilotIntervalMinutes > 0)
                    {
                        // 预设加载已覆盖 IntervalMinutes，这里只同步非预设字段
                        _autopilot.Interval = TimeSpan.FromMinutes(IntervalMinutes);
                    }
                    _autopilot.AdaptiveIntervalEnabled = AdaptiveIntervalEnabled;
                    _autopilot.AgentName = AgentName;
                    _autopilot.ExecutorType = IsExecutorAuto ? Core.Models.ExecutorType.Auto : (Core.Models.ExecutorType)SelectedExecutorType;
                    _autopilot.Mode = (Core.Models.AutopilotMode)SelectedMode;
                    _autopilot.PersonaPrompt = PresetPersonaPrompt;
                    IsConfigDirty = false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "加载编排配置失败");
        }
    }

    private async Task LoadGoalAsync()
    {
        // 如果预设已经有目标，使用预设的（优先级更高）
        if (SelectedPreset != null && !string.IsNullOrWhiteSpace(SelectedPreset.GoalTitle))
        {
            await SyncPresetGoalToDatabaseAsync(SelectedPreset);
            return;
        }

        // 否则从数据库加载
        var goal = await _storage.GetActiveGoalAsync();
        if (goal != null)
        {
            GoalTitle = goal.Title;
            GoalDescription = goal.Description;
        }
    }

    private async Task LoadWhiteboardAsync()
    {
        var whiteboard = await _storage.GetLatestWhiteboardAsync();
        WhiteboardContent = whiteboard.Content;
    }

    private async Task SaveGoalInternalAsync()
    {
        var existing = await _storage.GetActiveGoalAsync();
        if (existing != null)
        {
            await _storage.UpdateGoalAsync(existing.Id, GoalTitle, GoalDescription);
        }
        else
        {
            await _storage.CreateGoalAsync(GoalTitle, GoalDescription);
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalDays >= 1)
            return $"{elapsed.Days}天 {elapsed.Hours}小时";
        if (elapsed.TotalHours >= 1)
            return $"{elapsed.Hours}小时 {elapsed.Minutes}分钟";
        return $"{elapsed.Minutes}分钟";
    }
}
