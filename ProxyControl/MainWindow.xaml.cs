using ProxyControl.ViewModels;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

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
                        toast.DataContext = vm; /
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
            base.OnStateChanged(e);
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
    }
}
