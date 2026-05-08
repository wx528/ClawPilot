# ClawPilot — 多 Agent 统一调度平台

🦞 一个通用的 AI Agent 统一调度与编排平台，支持 OpenClaw、Hermes、Kimi Code CLI、CodeBuddy Code、Aider、Codex、Qwen Code 等多种执行后端，将任务编排、执行守护、草案审批等逻辑全部内置，开箱即用。

## 特性

- **多执行器支持** — 统一调度多种 AI Code CLI，支持混合编排
  - ✅ OpenClaw（远程 API 调用）
  - ✅ Hermes（本地 PowerShell 脚本）
  - ✅ Kimi Code CLI（月之暗面本地助手）
  - ✅ CodeBuddy Code（腾讯 AI 编程助手 CLI）
  - ✅ Aider CLI（开源代码辅助编程）
  - ✅ Codex CLI（OpenAI 终端编程助手）
  - ✅ Qwen Code（阿里通义本地助手）

- **通用 CLI 执行器架构** — `CliExecutorBase` 抽象基类封装 PATH 查找、进程管理、超时控制、编码修复等公共逻辑，新增执行器只需继承并实现 2 个成员

- **双模式自动驾驶编排（Autopilot）**
  - **Plan and Execute（定时模式）** — LLM 定时唤醒，根据目标+白板记忆+执行结果自主决策任务；支持自适应间隔、空周期回退、编排历史可视化
  - **ReAct（事件驱动模式）** — 任务完成自动触发下一轮感知-行动循环，实现真正的任务-感知-行动闭环

- **零依赖部署** — 单个 exe，开箱即用
- **内嵌 SQLite** — 任务队列 + 编排数据库，本地存储
- **进程管理** — 直接管理各执行器进程；超时可配置，失败任务自动指数退避重试
- **任务编排** — Persona / Prompt / DailyPlan / Draft 完整 CRUD
- **草案审批** — 自动确认倒计时 + 手动确认/打回
- **调试日志** — 支持 `--debug` / `--verbose` 启用 NDJSON 结构化日志，自动轮转与归档
- **深色主题** — 现代化暗色 UI

## 执行器矩阵

| 执行器类型 | 实现方式 | 状态 | 安装方式 | 典型场景 |
|-----------|---------|------|---------|---------|
| **OpenClaw** | 远程 API 调用 | ✅ 稳定 | — | 分布式 Agent 任务执行 |
| **Hermes** | 本地 PowerShell 脚本 | ✅ 稳定 | 内置 | 本地自动化脚本执行 |
| **Kimi Code CLI** | 本地可执行文件 | ✅ 支持 | 官网下载 | Kimi 本地助手代码生成 |
| **CodeBuddy Code** | 本地可执行文件 | ✅ 支持 | `npm i -g @tencent-ai/codebuddy-code` | 腾讯 AI 编程助手 |
| **Aider CLI** | 本地可执行文件 | ✅ 支持 | `pip install aider-chat` | 开源代码辅助编程 |
| **Codex CLI** | 本地可执行文件 | ✅ 支持 | `npm i -g @openai/codex` | OpenAI 终端编程助手 |
| **Qwen Code** | 本地可执行文件 | ✅ 支持 | `pip install qwen-cli` | 阿里通义本地助手 |

## 架构

```
ClawPilot (单进程 C# WPF)
├── ClawPilot.App (WPF UI 层)
│   ├── MainWindow.xaml — 主界面
│   ├── ViewModels/MainViewModel.cs — MVVM 视图模型
│   ├── ViewModels/AutopilotViewModel.cs — 自动驾驶配置
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
    │   └── Enums.cs (ExecutorType / TaskType / AutopilotMode)
    │
    └── Services/ — 核心服务
        ├── CliExecutorBase.cs — 通用 CLI 执行器基类 ★
        ├── OpenClawExecutor.cs — OpenClaw CLI 执行器
        ├── HermesExecutor.cs — Hermes 本地脚本执行器
        ├── KimiCodeExecutor.cs — Kimi Code CLI 执行器 (extends CliExecutorBase)
        ├── CodeBuddyExecutor.cs — CodeBuddy Code CLI 执行器 (extends CliExecutorBase)
        ├── AiderExecutor.cs — Aider CLI 执行器 (extends CliExecutorBase)
        ├── CodexExecutor.cs — Codex CLI 执行器 (extends CliExecutorBase)
        ├── QwenCodeExecutor.cs — Qwen Code CLI 执行器 (extends CliExecutorBase)
        ├── DaemonService.cs — 任务守护（动态选择执行器）
        ├── AutopilotOrchestrator.cs — LLM 编排器（定时 / 事件驱动）
        ├── LlmDecisionEngine.cs — LLM 决策引擎（Prompt 构建 / JSON 解析）
        ├── TaskQueueService.cs — 任务队列（含重试支持）
        ├── OrchestrationService.cs — 编排服务
        └── ProfileService.cs — Profile YAML 加载
```

### 扩展新执行器

基于 `CliExecutorBase`，添加新执行器只需 **3 步**：

1. **新建 Executor 类** — 继承 `CliExecutorBase`，实现 `CommandName` 和 `BuildArguments()`

```csharp
public class AiderExecutor : CliExecutorBase
{
    protected override string CommandName => "aider";
    
    protected override string BuildArguments(string message)
        => $"--message {EscapeArgument(message)} --yes";
}
```

2. **加枚举** — `TaskType` 和 `ExecutorType` 各加一个值

3. **注册 DI** — `App.xaml.cs` 注册、`DaemonService` / `AutopilotOrchestrator` 加 switch 分支

基类自动提供：PATH 查找（`where`/`which`）、Windows UTF-8 编码修复（`chcp 65001`）、超时控制、输出收集、参数转义。

## 开发

### 前置条件

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 至少一个执行后端（任选其一）：
  - [OpenClaw CLI](https://github.com/openclaw/openclaw)
  - [Kimi Code CLI](https://kimi.moonshot.cn)
  - [CodeBuddy Code](https://www.codebuddy.cn/docs/cli/headless)（`npm i -g @tencent-ai/codebuddy-code`）
  - Hermes 本地脚本

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
| 任务数据库 | `%USERPROFILE%\.clawpilot\tasks.db` |
| 编排数据库 | `%USERPROFILE%\.clawpilot\orchestrator.db` |
| 用户配置 | `%USERPROFILE%\.clawpilot\settings.json` — LLM Provider / 超时 / 并发数 / 编排间隔 / 各执行器路径 |
| Profile 目录 | `%USERPROFILE%\.clawpilot\profiles\` |
| 日志文件 | `%USERPROFILE%\.clawpilot\logs\clawpilot.log` |
| 调试日志（需 `--debug`） | `_debug_logs/debug-{yyyyMMdd}.ndjson`（相对项目根目录） |

## 配置说明

`profiles/` 目录包含示例编排配置，实际使用时请复制到 `%USERPROFILE%\.clawpilot\profiles\` 并按需调整。

`example-schedule.yaml` 提供了一份每日任务编排的参考模板，展示如何定义 Persona、Prompt 和 DailyPlan。

在设置页中可以配置各执行器的命令路径：
- OpenClaw API 地址
- Hermes 脚本路径（如：`D:\agents\hermes-agent\hermes.ps1`）
- Kimi Code CLI 可执行文件路径（默认 `kimi.exe`，支持 PATH 自动查找）
- CodeBuddy Code CLI 可执行文件路径（默认 `codebuddy`，支持 PATH 自动查找）

## 项目结构

```
ClawPilot/
├── ClawPilot.sln
├── README.md
├── CHANGELOG.md
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