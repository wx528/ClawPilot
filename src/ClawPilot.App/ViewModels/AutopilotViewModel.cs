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

    public bool IsIntervalEditable => !AdaptiveIntervalEnabled;
    public bool IsExecutorTypeEditable => !IsExecutorAuto;

    partial void OnAdaptiveIntervalEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIntervalEditable));
        IsConfigDirty = true;
    }

    partial void OnIntervalMinutesChanged(int value) => IsConfigDirty = true;
    partial void OnAgentNameChanged(string value) => IsConfigDirty = true;
    partial void OnSelectedExecutorTypeChanged(int value) => IsConfigDirty = true;
    partial void OnSelectedModeChanged(int value) => IsConfigDirty = true;
    partial void OnIsExecutorAutoChanged(bool value)
    {
        IsConfigDirty = true;
        OnPropertyChanged(nameof(IsExecutorTypeEditable));
        if (value)
        {
            // 切换到 Auto 前，记住当前手动选择
            _lastManualExecutorType = SelectedExecutorType;
        }
        else
        {
            // 取消 Auto，恢复之前的手动选择
            SelectedExecutorType = _lastManualExecutorType;
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

                await _autopilot.StartAsync();
                IsRunning = true;
                StatusMessage = "自动驾驶运行中";
            }

            await RefreshStatusAsync();
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
            var newJson = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(App.SettingsPath, newJson);

            _autopilot.AdaptiveIntervalEnabled = AdaptiveIntervalEnabled;
            _autopilot.AgentName = AgentName;
            _autopilot.ExecutorType = IsExecutorAuto ? ClawPilot.Core.Models.ExecutorType.Auto : (ClawPilot.Core.Models.ExecutorType)SelectedExecutorType;
            _autopilot.Mode = (ClawPilot.Core.Models.AutopilotMode)SelectedMode;
            var newInterval = TimeSpan.FromMinutes(IntervalMinutes);
            await _autopilot.RestartAsync(newInterval);

            IsConfigDirty = false;
            await RefreshStatusAsync();
            var modeText = (AutopilotMode)SelectedMode == AutopilotMode.ReAct ? "ReAct 模式" : "Plan-and-Execute 模式";
            MessageBox.Show($"编排配置已更新（{modeText}），已立即生效。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
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
            if (!IsGoalEditing)
            {
                GoalTitle = status.CurrentGoal;
            }
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
                StatusMessage = $"自动驾驶运行中 - 下次唤醒 {NextRunText}";
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
                        IntervalMinutes = settings.AutopilotIntervalMinutes;
                        _autopilot.Interval = TimeSpan.FromMinutes(IntervalMinutes);
                    }
                    AdaptiveIntervalEnabled = settings.AdaptiveIntervalEnabled;
                    _autopilot.AdaptiveIntervalEnabled = AdaptiveIntervalEnabled;
                    AgentName = settings.AutopilotAgentName ?? "main";
                    _autopilot.AgentName = AgentName;
                    IsExecutorAuto = settings.ExecutorType == ExecutorType.Auto;
                    SelectedExecutorType = IsExecutorAuto ? 0 : (int)settings.ExecutorType;
                    _lastManualExecutorType = (int)settings.ExecutorType;
                    _autopilot.ExecutorType = (ClawPilot.Core.Models.ExecutorType)settings.ExecutorType;
                    SelectedMode = (int)settings.AutopilotMode;
                    _autopilot.Mode = (ClawPilot.Core.Models.AutopilotMode)settings.AutopilotMode;
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
