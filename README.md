[中文文档](README.zh-CN.md) | English

# ClawPilot — Unified Multi-Agent Orchestration Platform

🦞 A universal AI Agent orchestration and scheduling platform that supports multiple execution backends including OpenClaw, Hermes, Kimi Code CLI, CodeBuddy Code, Aider, Codex, and Qwen Code. Task orchestration, execution daemon, draft approval — all built-in, ready to use out of the box.

## Features

- **Multi-Executor Support** — Unified scheduling for multiple AI Code CLIs with mixed orchestration
  - ✅ OpenClaw (Remote API)
  - ✅ Hermes (Local PowerShell scripts)
  - ✅ Kimi Code CLI (Moonshot local assistant)
  - ✅ CodeBuddy Code (Tencent AI coding assistant CLI)
  - ✅ Aider CLI (Open-source AI pair programming)
  - ✅ Codex CLI (OpenAI terminal coding assistant)
  - ✅ Qwen Code (Alibaba Tongyi local assistant)

- **Generic CLI Executor Architecture** — `CliExecutorBase` abstract class encapsulates PATH lookup, process management, timeout control, encoding fixes, and more. Add a new executor by inheriting and implementing just 2 members.

- **Dual-Mode Autopilot Orchestration**
  - **Plan and Execute (Scheduled Mode)** — LLM wakes on schedule, autonomously decides tasks based on goals + whiteboard memory + execution results; supports adaptive intervals, idle cycle backoff, orchestration history visualization
  - **ReAct (Event-Driven Mode)** — Task completion automatically triggers the next perceive-act cycle, achieving a true task-perceive-act closed loop

- **Executor Health Checks** — Real-time availability detection for all executors, batch health checks, automatic error reporting

- **Configurable Retry Policies** — Three retry strategies: exponential backoff, fixed interval, and linear backoff. Configurable max retries and delay caps.

- **Task Log Persistence** — Execution logs automatically written to SQLite with per-task queries, recent log queries, and TTL-based cleanup

- **Zero-Dependency Deployment** — Single exe, ready to run
- **Embedded SQLite** — Task queue + orchestration database, stored locally
- **Process Management** — Direct management of executor processes; configurable timeouts, automatic retry on failure
- **Task Orchestration** — Full CRUD for Persona / Prompt / DailyPlan
- **Draft Approval** — Auto-confirm countdown + manual approve/reject
- **Debug Logging** — `--debug` / `--verbose` flags enable NDJSON structured logging with automatic rotation and archival
- **Dark Theme** — Modern dark UI

## Executor Matrix

| Executor | Implementation | Status | Installation | Use Case |
|----------|---------------|--------|-------------|----------|
| **OpenClaw** | Remote API | ✅ Stable | — | Distributed agent task execution |
| **Hermes** | Local PowerShell script | ✅ Stable | Built-in | Local automation script execution |
| **Kimi Code CLI** | Local executable | ✅ Supported | Official website | Kimi local coding assistant |
| **CodeBuddy Code** | Local executable | ✅ Supported | `npm i -g @tencent-ai/codebuddy-code` | Tencent AI coding assistant |
| **Aider CLI** | Local executable | ✅ Supported | `pip install aider-chat` | Open-source AI pair programming |
| **Codex CLI** | Local executable | ✅ Supported | `npm i -g @openai/codex` | OpenAI terminal coding assistant |
| **Qwen Code** | Local executable | ✅ Supported | `pip install qwen-cli` | Alibaba Tongyi local assistant |

## Architecture

```
ClawPilot (Single-process C# WPF)
├── ClawPilot.App (WPF UI Layer)
│   ├── MainWindow.xaml — Main window
│   ├── ViewModels/MainViewModel.cs — MVVM view model
│   ├── ViewModels/AutopilotViewModel.cs — Autopilot configuration
│   ├── Logging/FileLoggerProvider.cs — File logging
│   ├── Logging/NdJsonFileLoggerProvider.cs — NDJSON structured debug logging
│   ├── Services/ProfileService.cs — Profile management
│   └── App.xaml.cs — Service initialization & DI
│
└── ClawPilot.Core (Core Business Library)
    ├── Models/ — Domain models
    │   ├── TaskItem.cs
    │   ├── OrchestrationModels.cs (Persona/Prompt/Plan)
    │   ├── AutopilotModels.cs — Autopilot decision models / sessions / whiteboard
    │   ├── DraftModels.cs
    │   ├── OperationResult.cs — Operation result wrapper
    │   └── Enums.cs (ExecutorType / TaskType / AutopilotMode)
    │
    └── Services/ — Core services
        ├── IExecutor.cs — Executor interface + health check
        ├── ExecutorRegistry.cs — Executor registry
        ├── CliExecutorBase.cs — Generic CLI executor base class ★
        ├── OpenClawExecutor.cs — OpenClaw CLI executor
        ├── HermesExecutor.cs — Hermes local script executor
        ├── KimiCodeExecutor.cs — Kimi Code CLI executor (extends CliExecutorBase)
        ├── CodeBuddyExecutor.cs — CodeBuddy Code CLI executor (extends CliExecutorBase)
        ├── AiderExecutor.cs — Aider CLI executor (extends CliExecutorBase)
        ├── CodexExecutor.cs — Codex CLI executor (extends CliExecutorBase)
        ├── QwenCodeExecutor.cs — Qwen Code CLI executor (extends CliExecutorBase)
        ├── DaemonService.cs — Task daemon (dynamic executor selection + retry policy + log persistence)
        ├── AutopilotOrchestrator.cs — LLM orchestrator (scheduled / event-driven)
        ├── LlmDecisionEngine.cs — LLM decision engine (prompt construction / JSON parsing)
        ├── TaskQueueService.cs — Task queue (with retry support + task logging)
        ├── OrchestrationService.cs — Orchestration service
        └── ProfileService.cs — Profile YAML loader
```

### Adding a New Executor

With `CliExecutorBase`, adding a new executor takes just **3 steps**:

1. **Create an Executor class** — Inherit from `CliExecutorBase`, implement `CommandName` and `BuildArguments()`

```csharp
public class AiderExecutor : CliExecutorBase
{
    protected override string CommandName => "aider";

    protected override string BuildArguments(string message)
        => $"--message {EscapeArgument(message)} --yes";
}
```

2. **Add enum values** — Add entries to `TaskType` and `ExecutorType`

3. **Register in DI** — Register in `App.xaml.cs` and `ExecutorRegistry`

The base class automatically provides: PATH lookup (`where`/`which`), Windows UTF-8 encoding fix (`chcp 65001`), timeout control, output collection, argument escaping, and health checks.

## Development

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- At least one execution backend (pick one):
  - [OpenClaw CLI](https://github.com/openclaw/openclaw)
  - [Kimi Code CLI](https://kimi.moonshot.cn)
  - [CodeBuddy Code](https://www.codebuddy.cn/docs/cli/headless) (`npm i -g @tencent-ai/codebuddy-code`)
  - Hermes local scripts

### Build

```bash
dotnet build
```

### Run

```bash
dotnet run --project src/ClawPilot.App
```

### Debug Mode (NDJSON Logging)

```bash
dotnet run --project src/ClawPilot.App -- --debug
```

With `--debug` or `--verbose`, NDJSON structured logs are written to `_debug_logs/debug-{date}.ndjson` in the project root. Each line is a JSON object that can be analyzed with `jq`:

```bash
jq 'select(.level == "Error")' _debug_logs/debug-20260424.ndjson
```

### Test

```bash
dotnet test src/ClawPilot.Tests
```

### Publish Single File

```bash
dotnet publish src/ClawPilot.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Data Storage

| Data | Path |
|------|------|
| Task database | `%USERPROFILE%\.clawpilot\tasks.db` |
| Orchestration database | `%USERPROFILE%\.clawpilot\orchestrator.db` |
| User settings | `%USERPROFILE%\.clawpilot\settings.json` — LLM Provider / timeouts / concurrency / orchestration intervals / executor paths |
| Profile directory | `%USERPROFILE%\.clawpilot\profiles\` |
| Log files | `%USERPROFILE%\.clawpilot\logs\clawpilot.log` |
| Debug logs (requires `--debug`) | `_debug_logs/debug-{yyyyMMdd}.ndjson` (relative to project root) |

## Configuration

The `profiles/` directory contains sample orchestration configs. Copy them to `%USERPROFILE%\.clawpilot\profiles\` and adjust as needed.

`example-schedule.yaml` provides a reference template for daily task orchestration, showing how to define Personas, Prompts, and DailyPlans.

Executor command paths can be configured in the Settings page:
- OpenClaw API endpoint
- Hermes script path (e.g., `D:\agents\hermes-agent\hermes.ps1`)
- Kimi Code CLI executable path (default: `kimi.exe`, supports PATH auto-discovery)
- CodeBuddy Code CLI executable path (default: `codebuddy`, supports PATH auto-discovery)

## Project Structure

```
ClawPilot/
├── ClawPilot.sln
├── README.md
├── README.zh-CN.md
├── CHANGELOG.md
├── LICENSE
├── VERSION
├── .gitignore
├── example-schedule.yaml   # Orchestration config sample
├── profiles/               # Sample profile configs
│   ├── default.yaml
│   └── tech_news_collector.yaml
└── src/
    ├── ClawPilot.App/        # WPF application
    ├── ClawPilot.Core/       # Core library
    └── ClawPilot.Tests/      # Unit tests
```

## License

[MIT](LICENSE)
