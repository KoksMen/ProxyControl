using ProxyControl.ViewModels;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ProxyControl
{
    public partial class MainWindow : Window
    {
        public static bool AllowClose { get; set; } = false;
        private const double LogAutoFollowThreshold = 0.5;
        private bool _preserveLogScrollOnInsert = false;
        private bool _isAdjustingLogScroll = false;
        private bool _preserveMonitorScrollOnInsert = false;
        private bool _isAdjustingMonitorScroll = false;

        public MainWindow()
        {
            InitializeComponent();

            if (DataContext is MainViewModel vm)
            {
                vm.RequestShowNotification += (tag, url, size) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        vm.LatestVersion = tag;
                        var toast = new Views.UpdateToast();
                        toast.DataContext = vm;
                        TrayIcon.ShowCustomBalloon(toast, System.Windows.Controls.Primitives.PopupAnimation.Slide, 8000);
                    });
                };
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!AllowClose)
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                base.OnClosing(e);
            }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
            }
            else if (WindowState == WindowState.Maximized)
            {
                if (WindowRootBorder != null)
                {
                    WindowRootBorder.Padding = new Thickness(7);
                    WindowRootBorder.BorderThickness = new Thickness(0);
                }
                if (MaximizeGlyph != null) MaximizeGlyph.Text = "🗗";
                if (MaximizeButton != null) MaximizeButton.ToolTip = "Restore Down";
            }
            else
            {
                if (WindowRootBorder != null)
                {
                    WindowRootBorder.Padding = new Thickness(0);
                    WindowRootBorder.BorderThickness = new Thickness(1);
                }
                if (MaximizeGlyph != null) MaximizeGlyph.Text = "🗖";
                if (MaximizeButton != null) MaximizeButton.ToolTip = "Maximize";
            }

            base.OnStateChanged(e);
        }

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
            if (sender is Button btn)
            {
                btn.ToolTip = Topmost ? "Window is pinned on top (Click to unpin)" : "Keep window always on top";
                btn.Opacity = Topmost ? 1.0 : 0.7;
                btn.BorderBrush = Topmost ? (System.Windows.Media.Brush)FindResource("PrimaryAccentBrush") : System.Windows.Media.Brushes.Transparent;
            }
        }

        private void TrayButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ConnectionLogsScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isAdjustingLogScroll) return;
            if (sender is not ScrollViewer scrollViewer) return;

            bool isAtTop = scrollViewer.VerticalOffset <= LogAutoFollowThreshold;

            if (e.ExtentHeightChange > 0)
            {
                _isAdjustingLogScroll = true;
                try
                {
                    if (_preserveLogScrollOnInsert)
                    {
                        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + e.ExtentHeightChange);
                    }
                    else if (isAtTop)
                    {
                        scrollViewer.ScrollToTop();
                    }
                }
                finally
                {
                    _isAdjustingLogScroll = false;
                }
            }

            _preserveLogScrollOnInsert = scrollViewer.VerticalOffset > LogAutoFollowThreshold;
        }

        private void MonitorConnectionsScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isAdjustingMonitorScroll) return;
            if (sender is not ScrollViewer scrollViewer) return;

            bool isAtTop = scrollViewer.VerticalOffset <= LogAutoFollowThreshold;

            if (e.ExtentHeightChange > 0)
            {
                _isAdjustingMonitorScroll = true;
                try
                {
                    if (_preserveMonitorScrollOnInsert)
                    {
                        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + e.ExtentHeightChange);
                    }
                    else if (isAtTop)
                    {
                        scrollViewer.ScrollToTop();
                    }
                }
                finally
                {
                    _isAdjustingMonitorScroll = false;
                }
            }

            _preserveMonitorScrollOnInsert = scrollViewer.VerticalOffset > LogAutoFollowThreshold;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (DataContext is not MainViewModel vm) return;

            bool isTypingInTextBox = Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is TextBoxBase;

            // 1. Esc: Cancel modal / dismiss drill-down / clear search / blur text input
            if (e.Key == Key.Escape)
            {
                if (vm.IsUpdateFoundModalVisible)
                {
                    vm.DismissUpdateFoundModalCommand?.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (vm.IsWhatsNewModalVisible)
                {
                    vm.CloseWhatsNewModalCommand?.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (vm.IsConfirmModalVisible)
                {
                    vm.CloseConfirmModalCommand?.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (vm.IsModalVisible)
                {
                    vm.CloseModalCommand?.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (vm.IsProxyModalVisible)
                {
                    vm.CloseProxyModalCommand?.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (vm.IsDeleteModalVisible)
                {
                    vm.CloseDeleteModalCommand?.Execute(null);
                    e.Handled = true;
                    return;
                }

                // If drill-down inside an app is active
                if (!string.IsNullOrEmpty(vm.SelectedAppName))
                {
                    vm.SelectedAppName = null;
                    e.Handled = true;
                    return;
                }

                // If monitor drill-down is active
                if (vm.SelectedMonitorProcess != null)
                {
                    vm.SelectedMonitorProcess = null;
                    e.Handled = true;
                    return;
                }

                // If search query is active
                if (!string.IsNullOrEmpty(vm.SearchText))
                {
                    vm.SearchText = string.Empty;
                    e.Handled = true;
                    return;
                }

                // If typing inside a TextBox, unfocus it
                if (isTypingInTextBox)
                {
                    Keyboard.ClearFocus();
                    e.Handled = true;
                    return;
                }
            }

            // 2. Enter: Confirm or save modal
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (vm.IsUpdateFoundModalVisible)
                {
                    vm.ConfirmUpdateCommand?.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (vm.IsWhatsNewModalVisible)
                {
                    vm.CloseWhatsNewModalCommand?.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (vm.IsConfirmModalVisible)
                {
                    vm.ConfirmActionCommand?.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (vm.IsModalVisible)
                {
                    vm.SaveModalRuleCommand?.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (vm.IsProxyModalVisible)
                {
                    vm.SaveProxyModalCommand?.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (vm.IsDeleteModalVisible)
                {
                    if (vm.IsDeleteAppEnabled)
                    {
                        vm.DeleteAppRulesCommand?.Execute(null);
                        e.Handled = true;
                        return;
                    }
                    if (vm.IsDeleteGroupEnabled)
                    {
                        vm.DeleteGroupRulesCommand?.Execute(null);
                        e.Handled = true;
                        return;
                    }
                }
            }

            // 3. Ctrl shortcuts
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                // Tab switching with Ctrl + 1..4
                if (e.Key == Key.D1 || e.Key == Key.NumPad1)
                {
                    vm.NavigateCommand?.Execute("Rules");
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.D2 || e.Key == Key.NumPad2)
                {
                    vm.NavigateCommand?.Execute("Monitor");
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.D3 || e.Key == Key.NumPad3)
                {
                    vm.NavigateCommand?.Execute("Logs");
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.D4 || e.Key == Key.NumPad4)
                {
                    vm.NavigateCommand?.Execute("Settings");
                    e.Handled = true;
                    return;
                }

                // Tab cycling with Ctrl+Tab / Ctrl+Shift+Tab
                if (e.Key == Key.Tab)
                {
                    string[] tabs = { "Rules", "Monitor", "Logs", "Settings" };
                    int currentIndex = Array.IndexOf(tabs, vm.CurrentView);
                    if (currentIndex < 0) currentIndex = 0;

                    int nextIndex;
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    {
                        nextIndex = (currentIndex - 1 + tabs.Length) % tabs.Length;
                    }
                    else
                    {
                        nextIndex = (currentIndex + 1) % tabs.Length;
                    }

                    vm.NavigateCommand?.Execute(tabs[nextIndex]);
                    e.Handled = true;
                    return;
                }

                // Ctrl + N: New rule
                if (e.Key == Key.N)
                {
                    vm.OpenRuleModalCommand?.Execute(null);
                    e.Handled = true;
                    return;
                }

                // Ctrl + R: Refresh
                if (e.Key == Key.R)
                {
                    vm.RefreshActiveProcessesCommand?.Execute(null);
                    e.Handled = true;
                    return;
                }
            }

            // 4. F5: Refresh
            if (e.Key == Key.F5)
            {
                vm.RefreshActiveProcessesCommand?.Execute(null);
                e.Handled = true;
                return;
            }

            // 5. Delete: Delete selected rule or app if not in text input
            if (e.Key == Key.Delete && !isTypingInTextBox)
            {
                if (vm.SelectedRule != null)
                {
                    vm.RemoveRuleCommand?.Execute(vm.SelectedRule);
                    e.Handled = true;
                    return;
                }
                if (!string.IsNullOrEmpty(vm.SelectedAppName))
                {
                    vm.RemoveAppCommand?.Execute(vm.SelectedAppName);
                    e.Handled = true;
                    return;
                }
            }
        }
    }
}
