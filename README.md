# ClawPilot — OpenClaw Desktop 控制工具

🦞 一个通用的 OpenClaw 桌面控制工具，将任务编排、执行守护、草案审批等逻辑全部内置，开箱即用。

## 特性

- **零依赖部署** — 单个 exe，开箱即用
- **自动驾驶编排（Autopilot）** — LLM 定时唤醒，根据目标+白板记忆+执行结果自主决策任务；支持自适应间隔、空周期回退、编排历史可视化
- **内嵌 SQLite** — 任务队列 + 编排数据库，本地存储
- **OpenClaw 进程管理** — 直接管理 `openclaw agent` 进程；超时可配置，失败任务自动指数退避重试
- **任务编排** — Persona / Prompt / DailyPlan / Draft 完整 CRUD
- **草案审批** — 自动确认倒计时 + 手动确认/打回
- **调试日志** — 支持 `--debug` / `--verbose` 启用 NDJSON 结构化日志，自动轮转与归档
- **深色主题** — 现代化暗色 UI

## 架构

```
ClawPilot (单进程 C# WPF)
├── ClawPilot.App (WPF UI 层)
│   ├── MainWindow.xaml — 主界面
│   ├── ViewModels/MainViewModel.cs — MVVM 视图模型
│   ├── Logging/FileLoggerProvider.cs — 文件日志
│   ├── Logging/NdJsonFileLoggerProvider.cs — NDJSON 结构化调试日志
│   ├── Services/ProfileService.cs — Profile 管理
│   └── App.xaml.cs — 服务初始化与 DI
│
└── ClawPilot.Core (核心业务库)
    ├── Models/ — 领域模型
    │   ├── TaskItem.cs
    │   ├── OrchestrationModels.cs (Persona/Prompt/Plan)
    │   ├── AutopilotModels.cs — 自动驾驶决策模型 / 会话 / 白板
    │   ├── DraftModels.cs
    │   ├── OperationResult.cs — 操作结果封装
    │   └── Enums.cs
    │
    └── Services/ — 核心服务
        ├── TaskQueueService.cs — 任务队列（含重试支持）
        ├── OrchestrationService.cs — 编排服务
        ├── AutopilotOrchestrator.cs — LLM 定时编排器
        ├── LlmDecisionEngine.cs — LLM 决策引擎（Prompt 构建 / JSON 解析）
        ├── OpenClawExecutor.cs — OpenClaw CLI 执行器
        ├── DaemonService.cs — 任务守护
        └── ProfileService.cs — Profile YAML 加载
```

## 开发

### 前置条件

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [OpenClaw CLI](https://github.com/openclaw/openclaw) (可选，用于实际执行任务)

### 构建

```bash
dotnet build
```

### 运行

```bash
dotnet run --project src/ClawPilot.App
```

### 调试模式（启用 NDJSON 日志）

```bash
dotnet run --project src/ClawPilot.App -- --debug
```

带上 `--debug` 或 `--verbose` 后，会在项目根目录生成 `_debug_logs/debug-{日期}.ndjson`，每行一个 JSON 对象，可直接用 `jq` 分析：

```bash
jq 'select(.level == "Error")' _debug_logs/debug-20260424.ndjson
```

### 发布单文件

```bash
dotnet publish src/ClawPilot.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## 数据存储

| 数据 | 路径 |
|------|------|
| 任务数据库 | `%APPDATA%\ClawPilot\tasks.db` |
| 编排数据库 | `%APPDATA%\ClawPilot\orchestrator.db` |
| 用户配置 | `%APPDATA%\ClawPilot\settings.json` — LLM Provider / 超时 / 并发数 / 编排间隔 |
| Profile 目录 | `%APPDATA%\ClawPilot\profiles\` |
| 日志文件 | `%APPDATA%\ClawPilot\logs\clawpilot.log` |
| 调试日志（需 `--debug`） | `_debug_logs/debug-{yyyyMMdd}.ndjson`（相对项目根目录） |

## 配置说明

`profiles/` 目录包含示例编排配置，实际使用时请复制到 `%APPDATA%\ClawPilot\profiles\` 并按需调整。

`example-schedule.yaml` 提供了一份每日任务编排的参考模板，展示如何定义 Persona、Prompt 和 DailyPlan。

## 项目结构

```
ClawPilot/
├── ClawPilot.sln
├── README.md
├── LICENSE
├── VERSION
├── .gitignore
├── example-schedule.yaml   # 编排配置示例
├── profiles/               # 示例 Profile 配置
│   ├── default.yaml
│   └── tech_news_collector.yaml
└── src/
    ├── ClawPilot.App/        # WPF 应用
    ├── ClawPilot.Core/       # 核心类库
    └── ClawPilot.Tests/      # 单元测试
```

## License

[MIT](LICENSE)
