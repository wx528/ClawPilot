using ClawPilot.App.ViewModels;
using ClawPilot.Core.Models;
using System.Windows;
using System.Windows.Input;

namespace ClawPilot.App
{
    public partial class MainWindow : Window
    {
        private System.Windows.Threading.DispatcherTimer? _refreshTimer;

        public MainViewModel ViewModel { get; }

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = ViewModel;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            Loaded += MainWindow_Loaded;
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedTask))
            {
                Dispatcher.Invoke(UpdateTaskDetailVisibility);
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateTaskDetailVisibility();

            // 注册执行器类型选择事件
            ExecutorTypeCombo.SelectionChanged += ExecutorTypeCombo_SelectionChanged;

            // 自动刷新定时器
            _refreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _refreshTimer.Tick += async (s, e) => await ViewModel.Refresh();
            _refreshTimer.Start();
        }

        private void ExecutorTypeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateExecutorTypeUI();
        }

        private void ExecutorAutoCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            UpdateExecutorTypeUI();
        }

        private void UpdateExecutorTypeUI()
        {
            var vm = ViewModel?.AutopilotVm;
            if (vm?.IsExecutorAuto == true)
            {
                // Auto 模式：隐藏 Agent 名称和所有提示
                AgentNamePanel.Visibility = Visibility.Collapsed;
                HermesAgentHint.Visibility = Visibility.Collapsed;
                OpenClawAgentHint.Visibility = Visibility.Collapsed;
            }
            else
            {
                // 非 Auto 模式：根据选中的执行器类型决定 UI
                var idx = ExecutorTypeCombo.SelectedIndex;
                if (idx == 1) // Hermes
                {
                    AgentNamePanel.Visibility = Visibility.Collapsed;
                    HermesAgentHint.Visibility = Visibility.Visible;
                    OpenClawAgentHint.Visibility = Visibility.Collapsed;
                }
                else if (idx == 2 || idx == 3) // KimiCode / CodeBuddy
                {
                    AgentNamePanel.Visibility = Visibility.Collapsed;
                    HermesAgentHint.Visibility = Visibility.Collapsed;
                    OpenClawAgentHint.Visibility = Visibility.Collapsed;
                }
                else // OpenClaw (idx == 0)
                {
                    AgentNamePanel.Visibility = Visibility.Visible;
                    HermesAgentHint.Visibility = Visibility.Collapsed;
                    OpenClawAgentHint.Visibility = Visibility.Visible;
                }
            }
        }

        /// <summary>
        /// 编排者 Tab 切换事件
        /// </summary>
        private void PresetTabList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var vm = ViewModel?.AutopilotVm;
            if (vm == null) return;

            // 如果正在运行，确认是否切换（仅当用户主动切换时）
            if (vm.IsRunning && e.RemovedItems.Count > 0)
            {
                var result = MessageBox.Show(
                    "切换编排者将停止当前运行并重新启动，是否继续？",
                    "确认切换",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    // 恢复原来的选中项
                    var list = (System.Windows.Controls.ListBox)sender;
                    list.SelectionChanged -= PresetTabList_SelectionChanged;
                    list.SelectedItem = e.RemovedItems[0];
                    list.SelectionChanged += PresetTabList_SelectionChanged;
                    return;
                }
            }

            UpdateExecutorTypeUI();
        }

        #region 导航

        private void NavTasks_Click(object sender, RoutedEventArgs e) => ShowPage("PageTasks");
        private void NavOps_Click(object sender, RoutedEventArgs e) => ShowPage("PageOps");
        private void NavAutopilot_Click(object sender, RoutedEventArgs e) => ShowPage("PageAutopilot");
        private void NavSettings_Click(object sender, RoutedEventArgs e) => ShowPage("PageSettings");

        private void ShowPage(string pageName)
        {
            PageTasks.Visibility = pageName == "PageTasks" ? Visibility.Visible : Visibility.Collapsed;
            PageOps.Visibility = pageName == "PageOps" ? Visibility.Visible : Visibility.Collapsed;
            PageAutopilot.Visibility = pageName == "PageAutopilot" ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility = pageName == "PageSettings" ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        #region 任务详情

        private void UpdateTaskDetailVisibility()
        {
            TaskDetailPanel.Visibility = ViewModel.SelectedTask != null ? Visibility.Visible : Visibility.Collapsed;
            TaskEmptyHint.Visibility = ViewModel.SelectedTask == null ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void ShowTaskDetails_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedTask != null)
            {
                await ViewModel.ShowTaskDetails(ViewModel.SelectedTask);
            }
        }

        #endregion

        #region 窗口控制

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && Mouse.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        /// <summary>
        /// 双击标题栏切换最大化/还原
        /// </summary>
        private void TitleBar_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ToggleMaximize();
        }

        /// <summary>
        /// 窗口状态改变时调整圆角
        /// </summary>
        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                // 最大化时移除圆角，避免边缘显示问题
                WindowBorder.CornerRadius = new CornerRadius(0);
                TitleBarBorder.CornerRadius = new CornerRadius(0);
                MaximizeBtn.Content = "❐";
            }
            else
            {
                // 还原时恢复圆角
                WindowBorder.CornerRadius = new CornerRadius(8);
                TitleBarBorder.CornerRadius = new CornerRadius(8, 8, 0, 0);
                MaximizeBtn.Content = "□";
            }
        }

        /// <summary>
        /// 最小化按钮
        /// </summary>
        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        /// <summary>
        /// 最大化/还原按钮
        /// </summary>
        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        /// <summary>
        /// 切换最大化/还原状态
        /// </summary>
        private void ToggleMaximize()
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }
            else
            {
                WindowState = WindowState.Maximized;
            }
        }

        /// <summary>
        /// 关闭按钮 → 最小化到系统托盘（Daemon 继续运行）
        /// </summary>
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).HideMainWindow();
        }

        /// <summary>
        /// 拦截 Alt+F4 等关闭行为，改为最小化到托盘
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            ((App)Application.Current).HideMainWindow();
            base.OnClosing(e);
        }

        #endregion
    }
}