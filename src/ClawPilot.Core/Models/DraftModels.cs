using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClawPilot.Core.Models;

/// <summary>
/// 草案中的单条任务指令
/// </summary>
public class DraftItem
{
    public string PersonaName { get; set; } = "";
    public string Message { get; set; } = "";
    public string TaskType { get; set; } = "openclaw";
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
    public string SourcePlanName { get; set; } = "";
    public int SourceItemIndex { get; set; }
    public string Reason { get; set; } = "";
    public bool IsEdited { get; set; }
    public string? OriginalMessage { get; set; }
}

/// <summary>
/// 编排草案
/// </summary>
public class OrchestrationDraft : INotifyPropertyChanged
{
    private int? _id;
    private string _contextSummary = "";
    private DraftStatus _status = DraftStatus.Pending;

    public int? Id
    {
        get => _id;
        set { _id = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayId)); }
    }

    public List<DraftItem> Items { get; set; } = [];
    public string ContextSummary
    {
        get => _contextSummary;
        set { _contextSummary = value; OnPropertyChanged(); }
    }
    public string DecisionReason { get; set; } = "";
    public string TriggeredBy { get; set; } = "scheduler";
    public string ProfileName { get; set; } = "";
    public string DecisionModeUsed { get; set; } = "";

    public DraftStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusIcon)); }
    }

    public string? CreatedAt { get; set; }
    public string? ExpiresAt { get; set; }
    public string? ApprovedAt { get; set; }
    public string? ExecutedAt { get; set; }

    public bool AutoApprove { get; set; } = true;
    public int AutoApproveAfterSeconds { get; set; } = 300;

    public string ExecutionLog { get; set; } = "";
    public string? ExecutionResult { get; set; }
    public string Feedback { get; set; } = "";

    // UI 计算属性
    public string DisplayId => Id.HasValue ? $"draft#{Id}" : "draft#?";
    public string ItemsCountText => $"{Items.Count} 个任务";
    public string StatusIcon => Status switch
    {
        DraftStatus.Pending => "⏳",
        DraftStatus.Approved => "✓",
        DraftStatus.Executing => "▶",
        DraftStatus.Executed => "✓",
        DraftStatus.Edited => "✏️",
        DraftStatus.Rejected => "✘",
        DraftStatus.Expired => "⏰",
        DraftStatus.Cancelled => "🚫",
        _ => "?"
    };

    // 倒计时 UI 属性
    public double CountdownPercent { get; set; }
    public string CountdownText { get; set; } = "";
    public string CountdownForeground { get; set; } = "#FF9800";
    public string AutoApproveText => AutoApprove ? $"自动确认 ({AutoApproveAfterSeconds}s)" : "手动确认";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
