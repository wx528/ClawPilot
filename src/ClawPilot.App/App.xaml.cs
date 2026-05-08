using ClawPilot.App.Logging;
using ClawPilot.App.ViewModels;
using ClawPilot.Core.Models;
using ClawPilot.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;

namespace ClawPilot.App
{
    public partial class App : System.Windows.Application
    {
        // 数据目录 — 混合模式：检测便携标记或本地 Data 目录，无权限则回退到 AppData
        public static string DataDir { get; }

        public static string TasksDbPath { get; }
        public static string OrchestratorDbPath { get; }
        public static string ProfilesDir { get; }
        public static string SettingsPath { get; }

        static App()
        {
            DataDir = ResolveDataDir();
            TasksDbPath = Path.Combine(DataDir, "tasks.db");
            OrchestratorDbPath = Path.Combine(DataDir, "orchestrator.db");
            ProfilesDir = Path.Combine(DataDir, "profiles");
            SettingsPath = Path.Combine(DataDir, "settings.json");
        }

        private static string ResolveDataDir()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    var exeDir = Path.GetDirectoryName(exePath)!;
                    var portableMarker = Path.Combine(exeDir, "portable");
                    var localDataDir = Path.Combine(exeDir, "Data");

                    // 存在 portable 标记，或已有本地 Data 目录，则尝试本地模式
                    if (File.Exists(portableMarker) || Directory.Exists(portableMarker) || Directory.Exists(localDataDir))
                    {
                        try
                        {
                            Directory.CreateDirectory(localDataDir);
                            // 测试写入权限
                            var testFile = Path.Combine(localDataDir, ".write_test");
                            File.WriteAllText(testFile, "");
                            File.Delete(testFile);
                            return localDataDir;
                        }
                        catch
                        {
                            // 本地目录无写入权限，回退到 AppData
                        }
                    }
                }
            }
            catch
            {
                // 任何异常都回退到 AppData
            }

            // 优先使用用户目录下的 .clawpilot（开发者工具惯例）
            var userProfileDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".clawpilot");

            // 自动迁移：如果旧路径存在且新路径不存在
            var legacyAppDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClawPilot");

            if (Directory.Exists(legacyAppDataDir) && !Directory.Exists(userProfileDir))
            {
                try
                {
                    Directory.Move(legacyAppDataDir, userProfileDir);
                }
                catch
                {
                    // 迁移失败，继续使用旧路径
                    return legacyAppDataDir;
                }
            }

            return userProfileDir;
        }

        // 依赖注入服务容器
        public static ServiceProvider ServiceProvider { get; private set; } = null!;

        // 系统托盘
        private TaskbarIcon? _notifyIcon;
        private MainWindow? _mainWindow;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 解析命令行参数
            var enableDebugLogs = e.Args.Contains("--verbose") || e.Args.Contains("--debug");

            // 全局异常捕获
            DispatcherUnhandledException += (s, args) =>
            {
                File.WriteAllText(Path.Combine(DataDir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] DispatcherUnhandledException:\n{args.Exception}\n\n");
                MessageBox.Show($"未处理的异常:\n{args.Exception.Message}\n\n详情见 crash.log", "ClawPilot 崩溃", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                File.WriteAllText(Path.Combine(DataDir, "crash_domain.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UnhandledException:\n{ex}\n\n");
            };
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                File.WriteAllText(Path.Combine(DataDir, "crash_task.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UnobservedTaskException:\n{args.Exception}\n\n");
            };

            try
            {
                // 确保数据目录存在
                Directory.CreateDirectory(DataDir);
                Directory.CreateDirectory(ProfilesDir);

                // 配置依赖注入
                ConfigureServices(enableDebugLogs);

                // 初始化系统托盘
                InitTrayIcon();

                // 显示主窗口
                _mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
                _mainWindow.Show();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(DataDir, "crash_startup.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Startup exception:\n{ex}\n\n");
                MessageBox.Show($"启动失败:\n{ex.Message}\n\n{ex.InnerException?.Message}", "ClawPilot 启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private void ConfigureServices(bool enableDebugLogs)
        {
            var services = new ServiceCollection();

            // 日志记录
            var logDir = Path.Combine(DataDir, "logs");
            var logFilePath = Path.Combine(logDir, "clawpilot.log");
            services.AddLogging(configure =>
            {
                configure.AddConsole();
                configure.AddProvider(new FileLoggerProvider(logFilePath, LogLevel.Debug));
                configure.SetMinimumLevel(LogLevel.Debug);

                if (enableDebugLogs)
                {
                    var debugLogDir = Path.Combine(Directory.GetCurrentDirectory(), "_debug_logs");
                    configure.AddProvider(new NdJsonFileLoggerProvider(debugLogDir, LogLevel.Debug));
                }
            });

            // 核心服务
            services.AddSingleton(sp => new TaskQueueService(TasksDbPath, sp.GetService<ILogger<TaskQueueService>>()));
            services.AddSingleton(sp => new OpenClawExecutor("openclaw", sp.GetService<ILogger<OpenClawExecutor>>()));
            services.AddSingleton(sp =>
            {
                var settings = LoadLlmSettings();
                var hermesExecutor = new HermesExecutor(
                    sp.GetService<ILogger<HermesExecutor>>(),
                    settings.HermesCommandPath);
                var kimiCodeExecutor = new KimiCodeExecutor(
                    sp.GetService<ILogger<KimiCodeExecutor>>(),
                    settings.KimiCodeCommandPath)
                {
                    WorkingDirectory = settings.KimiCodeWorkDir,
                    MaxStepsPerTurn = settings.KimiCodeMaxStepsPerTurn
                };
                var codeBuddyExecutor = new CodeBuddyExecutor(
                    sp.GetService<ILogger<CodeBuddyExecutor>>(),
                    settings.CodeBuddyCommandPath)
                {
                    WorkingDirectory = settings.CodeBuddyWorkDir,
                    SkipPermissions = settings.CodeBuddySkipPermissions,
                    AllowedTools = settings.CodeBuddyAllowedTools
                };
                var aiderExecutor = new AiderExecutor(
                    sp.GetService<ILogger<AiderExecutor>>(),
                    settings.AiderCommandPath)
                {
                    WorkingDirectory = settings.AiderWorkDir,
                    YesAlways = settings.AiderYesAlways,
                    NoAutoCommits = settings.AiderNoAutoCommits,
                    Model = settings.AiderModel
                };
                var codexExecutor = new CodexExecutor(
                    sp.GetService<ILogger<CodexExecutor>>(),
                    settings.CodexCommandPath)
                {
                    WorkingDirectory = settings.CodexWorkDir,
                    ApprovalMode = settings.CodexApprovalMode,
                    Model = settings.CodexModel
                };
                var qwenCodeExecutor = new QwenCodeExecutor(
                    sp.GetService<ILogger<QwenCodeExecutor>>(),
                    settings.QwenCodeCommandPath)
                {
                    WorkingDirectory = settings.QwenCodeWorkDir,
                    YesAlways = settings.QwenCodeYesAlways,
                    Model = settings.QwenCodeModel
                };
                var daemon = new DaemonService(
                    sp.GetRequiredService<TaskQueueService>(),
                    sp.GetRequiredService<OpenClawExecutor>(),
                    hermesExecutor,
                    kimiCodeExecutor,
                    codeBuddyExecutor,
                    aiderExecutor,
                    codexExecutor,
                    qwenCodeExecutor,
                    sp.GetService<ILogger<DaemonService>>());
                daemon.ExecutorTimeoutSeconds = settings.OpenClawTimeoutSeconds;
                daemon.MaxConcurrency = settings.DaemonMaxConcurrency;
                return daemon;
            });
            services.AddSingleton<ProfileService>();

            // 自动驾驶服务
            services.AddSingleton(sp => new OrchestratorStorageService(OrchestratorDbPath, sp.GetService<ILogger<OrchestratorStorageService>>()));
            services.AddSingleton<ILlmClient>(sp =>
            {
                var settings = LoadLlmSettings();
                var apiKey = settings.ApiKey;
                var baseUrl = settings.BaseUrl;
                var model = settings.Model;
                var logger = sp.GetService<ILogger<OpenAILlmClient>>();
                logger?.LogInformation("LLM 配置: BaseUrl={BaseUrl}, Model={Model}, KeyLength={KeyLen}", baseUrl, model, apiKey.Length);
                return new OpenAILlmClient(apiKey, baseUrl, model, logger);
            });
            services.AddSingleton(sp => new LlmDecisionEngine(
                sp.GetRequiredService<ILlmClient>(),
                sp.GetService<ILogger<LlmDecisionEngine>>()));
            services.AddSingleton(sp =>
            {
                var settings = LoadLlmSettings();
                var autopilot = new AutopilotOrchestrator(
                    sp.GetRequiredService<TaskQueueService>(),
                    sp.GetRequiredService<OrchestratorStorageService>(),
                    sp.GetRequiredService<LlmDecisionEngine>(),
                    sp.GetService<ILogger<AutopilotOrchestrator>>());
                autopilot.ExecutorType = (ClawPilot.Core.Models.ExecutorType)settings.ExecutorType;
                autopilot.Mode = (ClawPilot.Core.Models.AutopilotMode)settings.AutopilotMode;

                // 连接到 DaemonService 以支持 ReAct 事件
                var daemon = sp.GetRequiredService<DaemonService>();
                autopilot.SetDaemonService(daemon);
                return autopilot;
            });
            services.AddTransient<AutopilotViewModel>();

            // UI 组件和视图模型
            services.AddTransient<MainViewModel>();
            services.AddTransient<MainWindow>();

            // 初始化服务
            ServiceProvider = services.BuildServiceProvider();

            // 确保数据库表存在
            ServiceProvider.GetRequiredService<TaskQueueService>().EnsureTableExistsAsync().Wait();
            ServiceProvider.GetRequiredService<OrchestratorStorageService>().EnsureTablesExistAsync().Wait();
        }

        private void InitTrayIcon()
        {
            _notifyIcon = new TaskbarIcon();
            _notifyIcon.Icon = GenerateIcon();
            _notifyIcon.ToolTipText = "ClawPilot — OpenClaw Desktop";

            // 右键菜单
            var menu = new System.Windows.Controls.ContextMenu();

            var showItem = new System.Windows.Controls.MenuItem { Header = "显示主窗口" };
            showItem.Click += (s, e) => ShowMainWindow();
            menu.Items.Add(showItem);

            var daemonToggleItem = new System.Windows.Controls.MenuItem { Header = "启动 Daemon" };
            daemonToggleItem.Click += (s, e) =>
            {
                var daemon = ServiceProvider.GetRequiredService<DaemonService>();
                if (daemon.IsRunning)
                {
                    daemon.Stop();
                    daemonToggleItem.Header = "启动 Daemon";
                    _notifyIcon.ToolTipText = "ClawPilot — Daemon 已停止";
                }
                else
                {
                    daemon.Start();
                    daemonToggleItem.Header = "停止 Daemon";
                    _notifyIcon.ToolTipText = "ClawPilot — Daemon 运行中";
                }
            };
            menu.Items.Add(daemonToggleItem);

            var autopilotToggleItem = new System.Windows.Controls.MenuItem { Header = "启动自动驾驶" };
            autopilotToggleItem.Click += (s, e) =>
            {
                var autopilot = ServiceProvider.GetRequiredService<AutopilotOrchestrator>();
                if (autopilot.IsRunning)
                {
                    autopilot.Stop();
                    autopilotToggleItem.Header = "启动自动驾驶";
                }
                else
                {
                    autopilot.StartAsync();
                    autopilotToggleItem.Header = "停止自动驾驶";
                }
            };
            menu.Items.Add(autopilotToggleItem);

            menu.Items.Add(new System.Windows.Controls.Separator());

            var exitItem = new System.Windows.Controls.MenuItem { Header = "退出" };
            exitItem.Click += (s, e) => ShutdownApp();
            menu.Items.Add(exitItem);

            _notifyIcon.ContextMenu = menu;

            // 双击托盘图标显示主窗口
            _notifyIcon.TrayMouseDoubleClick += (s, e) => ShowMainWindow();
        }

        /// <summary>
        /// 用代码生成一个简单的 16x16 图标（蓝色圆形 + 白色 C）
        /// </summary>
        private static System.Drawing.Icon GenerateIcon()
        {
            using var bmp = new System.Drawing.Bitmap(16, 16);
            using var g = System.Drawing.Graphics.FromImage(bmp);

            // 蓝色圆形背景
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(102, 126, 234)); // #667EEA
            g.FillEllipse(brush, 0, 0, 15, 15);

            // 白色 C 字
            using var font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Bold);
            using var textBrush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            var sf = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center
            };
            g.DrawString("C", font, textBrush, new System.Drawing.RectangleF(0, 0, 16, 16), sf);

            // Bitmap → Icon
            var hIcon = bmp.GetHicon();
            return System.Drawing.Icon.FromHandle(hIcon);
        }

        private static LlmSettings LoadLlmSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var settings = System.Text.Json.JsonSerializer.Deserialize<LlmSettings>(json);
                    if (settings != null && !string.IsNullOrWhiteSpace(settings.ApiKey))
                        return settings;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取 settings.json 失败: {ex.Message}");
            }

            // 回退到环境变量
            return new LlmSettings
            {
                ApiKey = Environment.GetEnvironmentVariable("CLAWPILOT_LLM_API_KEY") ?? "",
                BaseUrl = Environment.GetEnvironmentVariable("CLAWPILOT_LLM_BASE_URL") ?? "https://api.deepseek.com",
                Model = Environment.GetEnvironmentVariable("CLAWPILOT_LLM_MODEL") ?? "deepseek-chat",
                DaemonMaxConcurrency = 1
            };
        }

        public void ShowMainWindow()
        {
            if (_mainWindow == null) return;

            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }

        public void HideMainWindow()
        {
            _mainWindow?.Hide();
        }

        public void ShutdownApp()
        {
            ServiceProvider.GetRequiredService<DaemonService>().Stop();
            ServiceProvider.GetRequiredService<AutopilotOrchestrator>().Stop();
            _notifyIcon?.Dispose();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ServiceProvider.GetRequiredService<DaemonService>().Stop();
            ServiceProvider.GetRequiredService<AutopilotOrchestrator>().Stop();
            _notifyIcon?.Dispose();
            ServiceProvider.Dispose();
            base.OnExit(e);
        }
    }

    public enum ExecutorType
    {
        OpenClaw,
        Hermes,
        KimiCode,
        CodeBuddy,
        Auto
    }

    public enum AutopilotMode
    {
        PlanAndExecute,
        ReAct
    }

    public class LlmSettings
    {
        public string ApiKey { get; set; } = "";
        public string BaseUrl { get; set; } = "https://api.deepseek.com";
        public string Model { get; set; } = "deepseek-chat";
        public int OpenClawTimeoutSeconds { get; set; } = 600;
        public int AutopilotIntervalMinutes { get; set; } = 60;
        public bool AdaptiveIntervalEnabled { get; set; } = false;
        public string AutopilotAgentName { get; set; } = "main";
        public int DaemonMaxConcurrency { get; set; } = 1;
        public ExecutorType ExecutorType { get; set; } = ExecutorType.OpenClaw;
        public AutopilotMode AutopilotMode { get; set; } = AutopilotMode.PlanAndExecute;
        public string HermesCommandPath { get; set; } = @"D:\agents\hermes-agent\hermes.ps1";
        public string KimiCodeCommandPath { get; set; } = "kimi.exe";
        public string? KimiCodeWorkDir { get; set; }
        public int KimiCodeMaxStepsPerTurn { get; set; } = 100;
        public string CodeBuddyCommandPath { get; set; } = "codebuddy";
        public string? CodeBuddyWorkDir { get; set; }
        public bool CodeBuddySkipPermissions { get; set; } = true;
        public string? CodeBuddyAllowedTools { get; set; }
        public string AiderCommandPath { get; set; } = "aider";
        public string? AiderWorkDir { get; set; }
        public bool AiderYesAlways { get; set; } = true;
        public bool AiderNoAutoCommits { get; set; } = true;
        public string? AiderModel { get; set; }
        public string CodexCommandPath { get; set; } = "codex";
        public string? CodexWorkDir { get; set; }
        public string CodexApprovalMode { get; set; } = "full-auto";
        public string? CodexModel { get; set; }
        public string QwenCodeCommandPath { get; set; } = "qwen-code";
        public string? QwenCodeWorkDir { get; set; }
        public bool QwenCodeYesAlways { get; set; } = true;
        public string? QwenCodeModel { get; set; }
        public List<OrchestratorPreset>? OrchestratorPresets { get; set; }
        public string? ActivePresetId { get; set; } = "general";
    }
}
