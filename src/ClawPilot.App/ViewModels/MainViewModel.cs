using ClawPilot.Core.Models;
using ClawPilot.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;

using TaskStatus = ClawPilot.Core.Models.TaskStatus;

namespace ClawPilot.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly TaskQueueService _taskQueue;
    private readonly OrchestrationService _orchestrator;
    private readonly DaemonService _daemon;
    private readonly ProfileService _profileService;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty]
    private string _statusText = "准备就绪";

    [ObservableProperty]
    private string _taskCountText = "0 任务";

    [ObservableProperty]
    private string _queueStatusText = "Daemon: 未运行";

    [ObservableProperty]
    private bool _isDaemonRunning;

    [ObservableProperty]
    private bool _isOrchestratorRunning;

    [ObservableProperty]
    private string _dataDirPath = "";

    [ObservableProperty]
    private bool _isRefreshButtonEnabled = true;

    [ObservableProperty]
    private TaskItem? _selectedTask;

    [ObservableProperty]
    private string _agentName = "main";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private string _llmApiKey = "";

    [ObservableProperty]
    private string _llmBaseUrl = "";

    [ObservableProperty]
    private string _llmModel = "";

    // Note: _openClawTimeoutSeconds and _daemonMaxConcurrency are implemented manually
    // with validation logic, so they don't use [ObservableProperty] attribute
    private int _openClawTimeoutSeconds = 600;

    public int OpenClawTimeoutSeconds
    {
        get => _openClawTimeoutSeconds;
        set
        {
            if (value < 10) value = 10;
            if (value > 36000) value = 36000;
            SetProperty(ref _openClawTimeoutSeconds, value);
        }
    }

    private int _daemonMaxConcurrency = 1;

    public int DaemonMaxConcurrency
    {
        get => _daemonMaxConcurrency;
        set
        {
            if (value < 1) value = 1;
            if (value > 100) value = 100;
            SetProperty(ref _daemonMaxConcurrency, value);
        }
    }

    public ObservableCollection<TaskItem> TaskItems { get; } = new();
    public AutopilotViewModel AutopilotVm { get; }

    public MainViewModel(
        TaskQueueService taskQueue,
        OrchestrationService orchestrator,
        DaemonService daemon,
        ProfileService profileService,
        AutopilotViewModel autopilotVm,
        ILogger<MainViewModel> logger)
    {
        _taskQueue = taskQueue;
        _orchestrator = orchestrator;
        _daemon = daemon;
        _profileService = profileService;
        AutopilotVm = autopilotVm;
        _logger = logger;

        IsDaemonRunning = _daemon.IsRunning;
        IsOrchestratorRunning = _orchestrator.IsRunning;
        DataDirPath = App.DataDir;

        LoadLlmSettings();
        _ = LoadData();
    }

    private void LoadLlmSettings()
    {
        try
        {
            if (File.Exists(App.SettingsPath))
            {
                var json = File.ReadAllText(App.SettingsPath);
                var settings = System.Text.Json.JsonSerializer.Deserialize<LlmSettings>(json);
                if (settings != null)
                {
                    LlmApiKey = settings.ApiKey ?? "";
                    LlmBaseUrl = settings.BaseUrl ?? "";
                    LlmModel = settings.Model ?? "";
                    OpenClawTimeoutSeconds = settings.OpenClawTimeoutSeconds > 0 ? settings.OpenClawTimeoutSeconds : 600;
                    DaemonMaxConcurrency = settings.DaemonMaxConcurrency > 0 ? settings.DaemonMaxConcurrency : 1;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载 LLM 设置失败");
        }
    }

    [RelayCommand]
    public async Task Refresh()
    {
        IsRefreshButtonEnabled = false;
        try
        {
            await LoadData();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新任务列表失败");
            MessageBox.Show($"刷新任务列表失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsRefreshButtonEnabled = true;
        }
    }

    [RelayCommand]
    private async Task AddTask()
    {
        if (string.IsNullOrWhiteSpace(AgentName) || string.IsNullOrWhiteSpace(Message))
        {
            MessageBox.Show("请输入代理名称和任务信息", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var result = await _taskQueue.AddTaskAsync(Message, AgentName);
            if (result.Success)
            {
                MessageBox.Show($"任务添加成功 (ID: {result.TaskId})", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadData();
            }
            else
            {
                MessageBox.Show($"添加任务失败: {result.Error}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加任务失败");
            MessageBox.Show($"添加任务失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedTask()
    {
        if (SelectedTask == null)
        {
            MessageBox.Show("请选择要删除的任务", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show($"确定要删除任务 #{SelectedTask.Id} 吗?", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            try
            {
                var deleteResult = await _taskQueue.DeleteTaskAsync(SelectedTask.Id);
                if (deleteResult.Success)
                {
                    MessageBox.Show("任务删除成功", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    await LoadData();
                }
                else
                {
                    MessageBox.Show($"删除任务失败: {deleteResult.Error}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除任务失败");
                MessageBox.Show($"删除任务失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private async Task ClearCompletedTasks()
    {
        var result = MessageBox.Show("确定要清除所有已完成的任务吗?", "确认清除", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            try
            {
                var count = await _taskQueue.ClearCompletedTasksAsync();
                MessageBox.Show($"已清除 {count} 个已完成的任务", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadData();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清除已完成任务失败");
                MessageBox.Show($"清除已完成任务失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private async Task ToggleDaemon()
    {
        await Task.CompletedTask;
        try
        {
            if (IsDaemonRunning)
            {
                _daemon.Stop();
                StatusText = "Daemon 已停止";
                QueueStatusText = "Daemon: 未运行";
            }
            else
            {
                _daemon.Start();
                StatusText = "Daemon 正在运行";
                QueueStatusText = "Daemon: 运行中";
            }

            IsDaemonRunning = _daemon.IsRunning;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Daemon 操作失败");
            MessageBox.Show($"Daemon 操作失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ToggleOrchestrator()
    {
        await Task.CompletedTask;
        try
        {
            if (IsOrchestratorRunning)
            {
                _orchestrator.Stop();
                StatusText = "编排服务已停止";
            }
            else
            {
                await _orchestrator.StartAsync();
                StatusText = "编排服务正在运行";
            }

            IsOrchestratorRunning = _orchestrator.IsRunning;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "编排服务操作失败");
            MessageBox.Show($"编排服务操作失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task RunTaskOnce()
    {
        await Task.CompletedTask;
        try
        {
            var ran = await _daemon.RunOnceAsync();
            if (ran)
            {
                MessageBox.Show("任务已开始执行", "任务执行", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("没有待执行的任务", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            await LoadData();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "执行任务失败");
            MessageBox.Show($"执行任务失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task OpenProfilesDirectory()
    {
        await Task.CompletedTask;
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", App.ProfilesDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开配置文件目录失败");
            MessageBox.Show($"打开配置文件目录失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ViewProfile()
    {
        await Task.CompletedTask;
        try
        {
            // 这里应该打开一个配置文件查看器
            MessageBox.Show("配置文件查看功能将在后续版本中实现", "功能未实现", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查看配置文件失败");
            MessageBox.Show($"查看配置文件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task LoadConfigFromFile()
    {
        try
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "YAML 文件 (*.yaml;*.yml)|*.yaml;*.yml|所有文件 (*.*)|*.*",
                Title = "选择配置文件"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                var yamlContent = await File.ReadAllTextAsync(openFileDialog.FileName);
                var result = await _orchestrator.LoadAndParseYamlAsync(yamlContent);

                if (result.Success)
                {
                    MessageBox.Show($"配置文件加载成功，解析到 {result.GetData<int?>()} 个任务规范", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    StatusText = "配置文件已加载";
                }
                else
                {
                    MessageBox.Show($"配置文件解析失败: {result.Error}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载配置文件失败");
            MessageBox.Show($"加载配置文件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ShowQueueInfo()
    {
        try
        {
            var stats = await _taskQueue.GetStatisticsAsync();
            if (stats != null)
            {
                var statusCounts = string.Join(", ", stats.Status.Select(kv => $"{kv.Key}: {kv.Value}"));
                var typeCounts = string.Join(", ", stats.Type.Select(kv => $"{kv.Key}: {kv.Value}"));

                var message = $"任务统计:\n\n总数: {stats.Total}\n\n按状态:\n{statusCounts}\n\n按类型:\n{typeCounts}";
                MessageBox.Show(message, "队列信息", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("获取统计信息失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取队列信息失败");
            MessageBox.Show($"获取队列信息失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadData()
    {
        try
        {
            var selectedId = SelectedTask?.Id;
            var tasks = await _taskQueue.ListTasksAsync();
            TaskItems.Clear();
            foreach (var task in tasks)
            {
                TaskItems.Add(task);
            }

            // 恢复之前的选中状态
            if (selectedId.HasValue)
            {
                SelectedTask = TaskItems.FirstOrDefault(t => t.Id == selectedId.Value);
            }

            TaskCountText = $"{TaskItems.Count} 任务";

            // 更新状态文本
            var stats = await _taskQueue.GetStatisticsAsync();
            if (stats != null)
            {
                var pendingCount = stats.Status.TryGetValue(TaskStatus.Pending, out var count) ? count : 0;
                StatusText = $"就绪，{pendingCount} 个待处理任务";
            }

            if (_daemon.IsRunning)
            {
                QueueStatusText = $"Daemon: 运行中，并发数: {_daemon.ActiveTaskCount} 个";
            }
            else
            {
                QueueStatusText = "Daemon: 未运行";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载任务数据失败");
            StatusText = "加载任务数据失败";
        }
    }

    public async Task ShowTaskDetails(TaskItem task)
    {
        await Task.CompletedTask;
        try
        {
            SelectedTask = task;
            var details = $"ID: {task.Id}\n" +
                         $"代理: {task.AgentName}\n" +
                         $"消息: {task.Message}\n" +
                         $"状态: {task.StatusText}\n" +
                         $"类型: {task.TaskType}\n" +
                         $"来源: {task.Source}\n" +
                         $"创建时间: {task.CreatedAt:yyyy-MM-dd HH:mm:ss}\n" +
                         $"更新时间: {task.UpdatedAt:yyyy-MM-dd HH:mm:ss}\n\n" +
                         $"输出:\n{task.Output}";

            MessageBox.Show(details, "任务详情", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "显示任务详情失败");
            MessageBox.Show($"显示任务详情失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task ShowQueueInfoDialog()
    {
        await ShowQueueInfo();
    }

    [RelayCommand]
    private void SaveLlmSettings()
    {
        try
        {
            var settings = new LlmSettings
            {
                ApiKey = LlmApiKey,
                BaseUrl = LlmBaseUrl,
                Model = LlmModel,
                OpenClawTimeoutSeconds = OpenClawTimeoutSeconds,
                DaemonMaxConcurrency = DaemonMaxConcurrency
            };

            var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(App.SettingsPath, json);
            _daemon.ExecutorTimeoutSeconds = OpenClawTimeoutSeconds;
            _daemon.UpdateConcurrency(DaemonMaxConcurrency);
            MessageBox.Show("配置已保存。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存 LLM 设置失败");
            MessageBox.Show($"保存配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}