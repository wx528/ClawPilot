# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
