# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] - TBD

基于 Debug 日志分析，下一版本聚焦**可观测性降噪**、**任务执行可靠性**与**编排策略优化**。

### Planned

#### 1. 日志降噪与分级
- [ ] 将 Daemon 高频轮询日志（"没有待处理任务"等）从 `Debug` 降级为 `Trace`。
- [ ] 并发限制器满时，Daemon 轮询间隔从 5s 延长至 30s，减少无效调度噪音。
- [ ] 增加日志自动轮转 / gzip 归档历史文件机制（单天 47MB+ 不可持续）。

#### 2. 任务执行可靠性
- [ ] 增加 OpenClaw 调用超时配置项（当前超时过短导致高频超时失败）。
- [ ] 结构化捕获并记录 OpenClaw `stderr`，定位 `ExitCode: 1` 根因。
- [ ] 失败任务支持指数退避重试（当前直接标记 `Failed`，丢失恢复机会）。

#### 3. 编排策略优化
- [ ] 防止 LLM "做完就停" 的保守决策：目标增加持续性指令或空编排提醒机制。
- [ ] 连续 N 个周期安排 0 任务时，触发系统通知或回退默认行为。

#### 4. 配置管理基础
- [ ] LLM Provider 配置 UI（API Key / Base URL / Model），区分开发配置与分发配置。
- [ ] 明确 `VERSION` 文件与 `.csproj` 的同步策略。

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
