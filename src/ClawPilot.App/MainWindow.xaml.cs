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

            // 自动刷新定时器
            _refreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _refreshTimer.Tick += async (s, e) => await ViewModel.Refresh();
            _refreshTimer.Start();
        }

        #region 导航

        private void NavTasks_Click(object sender, RoutedEventArgs e) => ShowPage("PageTasks");
        private void NavOps_Click(object sender, RoutedEventArgs e) => ShowPage("PageOps");
        private void NavOrch_Click(object sender, RoutedEventArgs e) => ShowPage("PageOrchestrator");
        private void NavDrafts_Click(object sender, RoutedEventArgs e) => ShowPage("PageDrafts");
        private void NavAutopilot_Click(object sender, RoutedEventArgs e) => ShowPage("PageAutopilot");
        private void NavSettings_Click(object sender, RoutedEventArgs e) => ShowPage("PageSettings");

        private void ShowPage(string pageName)
        {
            PageTasks.Visibility = pageName == "PageTasks" ? Visibility.Visible : Visibility.Collapsed;
            PageOps.Visibility = pageName == "PageOps" ? Visibility.Visible : Visibility.Collapsed;
            PageOrchestrator.Visibility = pageName == "PageOrchestrator" ? Visibility.Visible : Visibility.Collapsed;
            PageDrafts.Visibility = pageName == "PageDrafts" ? Visibility.Visible : Visibility.Collapsed;
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