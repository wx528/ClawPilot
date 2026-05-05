# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.0] - 2026-05-05

### Added

#### 通用 CLI 执行器架构
- **CliExecutorBase 抽象基类** — 提取所有 AI Code CLI 的公共逻辑（PATH 查找、进程启动、超时控制、输出收集、参数转义），新增执行器只需继承并实现 2 个成员
- **Windows PATH 智能查找** — 使用 `where` 命令自动在系统 PATH 中查找 CLI 可执行文件，不再要求用户必须配置绝对路径
- **Windows 中文编码修复** — 通过 `cmd /c chcp 65001` 设置代码页为 UTF-8，解决 Node.js CLI 工具（Kimi、CodeBuddy）在 Windows 中文环境下的输出乱码问题
- **超时输出保留** — 超时终止进程前先读取已缓冲的输出内容，不再直接丢弃

#### Kimi Code CLI 执行器（重构）
- **正确参数格式** — 修正为 `kimi --quiet -p "msg"`，原 `--no-color` 和 `KIMI_NO_INTERACTIVE` 环境变量不存在于 Kimi CLI
- **工作目录支持** — 新增 `--work-dir` 参数，可指定项目目录
- **AFK 无人值守模式** — 支持 `--afk` 参数，自动审批 + 自动 dismiss AskUserQuestion
- **步数控制** — 支持 `--max-steps-per-turn` 参数
- **默认超时延长** — 从 120s 调整为 300s，适应 AI 编程任务较重的场景
- **配置项** — `settings.json` 新增 `KimiCodeCommandPath`（默认 `kimi.exe`）、`KimiCodeWorkDir`、`KimiCodeMaxStepsPerTurn`

#### CodeBuddy Code CLI 执行器（新增）
- **CodeBuddyExecutor** — 腾讯云 AI 编程助手 CLI 执行器，安装方式 `npm install -g @tencent-ai/codebuddy-code`
- **非交互模式** — 使用 `-p` 参数传 prompt，`--output-format` 控制输出格式
- **权限跳过** — 无人值守场景自动启用 `--dangerously-skip-permissions`
- **工具白名单/黑名单** — 支持 `--tools` 和 `--disallowedTools` 参数精细控制
- **系统提示词追加** — 支持 `--append-system-prompt` 参数
- **配置项** — `settings.json` 新增 `CodeBuddyCommandPath`（默认 `codebuddy`）、`CodeBuddyWorkDir`、`CodeBuddySkipPermissions`、`CodeBuddyAllowedTools`

#### 枚举扩展
- `TaskType` 新增 `CodeBuddy`
- `ExecutorType` 新增 `CodeBuddy`

#### UI 改进
- **快捷操作页重构** — 执行器选择下拉框移至第一行，移除未实现的 `langgraph`，绑定 ViewModel 的 `SelectedTaskTypeIndex`
- **添加任务传正确类型** — `AddTask()` 根据下拉框索引映射正确的 `TaskType`，KimiCode/CodeBuddy 不再强制要求 Agent 名称
- **执行器状态卡片** — 快捷操作页新增执行器状态展示，一目了然当前支持的所有执行器
- **Daemon 控制增强** — 新增"执行一个任务"按钮
- **ComboBox 深色主题修复** — 重写 `DarkComboBoxStyle`：添加 ToggleButton 支持点击切换、使用 `RelativeSource TemplatedParent` 替代 `ElementName` 绑定宽度（修复 Popup 跨可视化树绑定失效）

### Changed
- **KimiCodeExecutor** 从独立实现改为继承 `CliExecutorBase`，代码量从 138 行降至 55 行
- **DaemonService.RunOnceAsync** 补全 KimiCode 和 CodeBuddy 执行分支
- **AutopilotOrchestrator** 的 `ExecutorType→TaskType` 映射从 `if-else` 改为 `switch` 表达式，覆盖所有执行器类型
- **RegisteredExecutors** 从硬编码 `["openclaw"]` 改为 `["openclaw", "hermes", "kimicode", "codebuddy"]`
- **执行器架构** — 从 3 种执行器扩展到 4 种，新增执行器只需 3 步（继承基类 + 加枚举 + 注册 DI）

## [0.2.2] - 2026-04-30

### Changed
- **数据存储路径** — 从 `%APPDATA%\ClawPilot` 改为 `%USERPROFILE%\.clawpilot`，更符合开发者工具惯例（类似 `.ssh`、`.codebuddy`）
- **自动数据迁移** — 首次启动自动将旧数据迁移到新目录，迁移失败时优雅回退

### Fixed
- **.gitignore** — 添加 `publish/` 目录，避免发布产物误提交

## [0.2.1] - 2026-04-30

### Added

#### Daemon 并发控制
- **并发数配置支持** — 设置页新增"并发数"输入框，可配置 Daemon 同时执行的任务数量，默认值为 1（串行执行）。
- **运行时动态更新** — 保存并发配置后立即生效，无需重启应用。
- **`settings.json` 持久化** — 新增 `DaemonMaxConcurrency` 字段，程序启动时自动加载。
- **状态栏实时显示** — Daemon 运行时状态栏显示当前活跃任务数量。

### Changed
- Daemon 默认并发数从 3 改为 1，确保任务串行执行，避免资源竞争。

### Fixed
- **并发数动态更新** — 修复 `DaemonService.UpdateConcurrency` 方法，Daemon 运行时修改并发数立即生效（之前只更新属性值，未更新 SemaphoreSlim）。
- **默认值缺失** — 修复 `App.xaml.cs` 中 `LoadLlmSettings` 默认设置缺少 `DaemonMaxConcurrency` 字段导致的并发数为 0 问题。
- **输入验证** — 在 `MainViewModel` 中添加并发数和超时值的范围验证（并发数 1-100，超时 10-36000 秒）。

## [0.2.0] - 2026-04-27

### Added

#### 自动驾驶编排（Autopilot）
- **LLM 驱动的定时编排** — 每小时唤醒 LLM，根据 mission goal + 白板记忆 + 执行结果自主决策下一周期任务。
- **自适应编排间隔** — LLM 可根据任务完成速度建议下次唤醒间隔（5–1440 分钟）；UI 支持一键启用/禁用，启用后文本框自动变为只读并实时同步当前间隔。
- **白板记忆（Whiteboard）** — LLM 跨周期的持久化记忆，每次编排后自动更新，避免"失忆"。
- **空周期回退机制** — 连续 3 个周期安排 0 任务时，自动插入 fallback checkpoint 任务，防止 mission stall。
- **编排历史面板** — 可视化展示每次编排周期的决策摘要、任务数量与状态，支持滚动浏览与悬停查看完整内容。

#### 任务执行可靠性
- **OpenClaw 超时配置** — `settings.json` 支持 `OpenClawTimeoutSeconds`（默认 600s），避免高频超时失败。
- **结构化 stderr 捕获** — 完整记录 OpenClaw 的 `stderr` 与 `ExitCode`，便于定位根因。
- **指数退避重试** — 失败任务自动进入重试队列，最多 3 次，退避间隔随重试次数增长。

#### 日志与可观测性
- **日志分级降噪** — Daemon 高频轮询日志从 `Debug` 降级为 `Trace`。
- **轮询间隔动态退避** — 并发限制器满时，轮询间隔从 5s → 15s → 30s 阶梯延长。
- **NDJSON 日志自动轮转** — 单文件超过 10MB 自动分片，7 天后历史文件 gzip 归档。

#### 配置管理
- **LLM Provider UI** — 设置页支持配置 API Key / Base URL / Model，数据持久化到 `%APPDATA%\ClawPilot\settings.json`。
- **统一版本管理** — `Directory.Build.props` 自动从根目录 `VERSION` 文件读取版本号，避免多项目版本不一致。

### Fixed
- `GoalTitle` 编辑时不再被 `RefreshStatusAsync` 定时器覆盖。
- `TaskStatus` 枚举引用歧义导致编译失败。

## [0.1.0] - 2026-04-25

### Added
- 初始版本发布。
- WPF 桌面应用主界面，基于 MVVM 架构 (`CommunityToolkit.Mvvm`)。
- SQLite 本地数据持久化与任务历史记录。
- 支持 Cron 表达式与 Loop 间隔两种任务调度模式 (`NCrontab`)。
- Daemon 守护进程机制，确保调度服务后台稳定运行。
- 草案审批工作流：任务执行前可预览并确认/驳回。
- 系统托盘集成（最小化到托盘、托盘菜单操作）。
- NDJSON 结构化日志输出，便于后续分析与审计。
- 单文件发布配置，方便分发与部署。
- 基于配置文件 (`profiles/*.yaml`) 的多 Agent 支持。

### Fixed
- 修复 `OpenClawExecutor` 指令截断问题：将消息中的换行符替换为空格，避免 `cmd.exe` 解析异常。
