using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClawPilot.Core.Models;

/// <summary>
/// 任务项 — 对应 tasks 表的一条记录
/// </summary>
public class TaskItem : INotifyPropertyChanged
{
    private int _id;
    private string _agentName = "";
    private string _message = "";
    private TaskStatus _status = TaskStatus.Pending;
    private TaskType _taskType = TaskType.OpenClaw;
    private TaskSource _source = TaskSource.User;
    private string _output = "";
    private DateTime _createdAt = DateTime.Now;
    private DateTime _updatedAt = DateTime.Now;

    public int Id
    {
        get => _id;
        set { _id = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayText)); }
    }

    public string AgentName
    {
        get => _agentName;
        set { _agentName = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayText)); }
    }

    public string Message
    {
        get => _message;
        set { _message = value; OnPropertyChanged(); OnPropertyChanged(nameof(MessagePreview)); }
    }

    public TaskStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    public TaskType TaskType
    {
        get => _taskType;
        set { _taskType = value; OnPropertyChanged(); }
    }

    public TaskSource Source
    {
        get => _source;
        set { _source = value; OnPropertyChanged(); }
    }

    public string Output
    {
        get => _output;
        set { _output = value ?? ""; OnPropertyChanged(); }
    }

    public DateTime CreatedAt
    {
        get => _createdAt;
        set { _createdAt = value; OnPropertyChanged(); }
    }

    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set { _updatedAt = value; OnPropertyChanged(); }
    }

    // 计算属性
    public string DisplayText => $"#{Id} {AgentName}";
    public string StatusText => Status switch
    {
        TaskStatus.Pending => "⏳ 等待中",
        TaskStatus.Running => "▶ 执行中",
        TaskStatus.Success => "✓ 已完成",
        TaskStatus.Failed => "✗ 已失败",
        _ => Status.ToString()
    };
    public string MessagePreview => Message.Length > 30 ? Message[..30] + "..." : Message;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}