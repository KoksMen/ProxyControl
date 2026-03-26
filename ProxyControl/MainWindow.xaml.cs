using ProxyControl.ViewModels;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace ProxyControl
{
    public partial class MainWindow : Window
    {
        // Статическое поле для управления реальным закрытием
        public static bool AllowClose { get; set; } = false;
        private const double LogAutoFollowThreshold = 0.5;
        private bool _preserveLogScrollOnInsert = false;
        private bool _isAdjustingLogScroll = false;

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
                        toast.DataContext = vm; // ensure ViewModel context for commands
                        TrayIcon.ShowCustomBalloon(toast, System.Windows.Controls.Primitives.PopupAnimation.Slide, 8000);
                    });
                };
            }
        }

        // Перехватываем событие закрытия окна
        protected override void OnClosing(CancelEventArgs e)
        {
            // Если флаг не установлен (пользователь нажал крестик), отменяем закрытие и скрываем окно
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
            // Если окно свернули, скрываем его, чтобы оно ушло в трей (если используется TaskbarIcon)
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

            // New logs are inserted at the top. Keep user's reading position stable
            // unless they are currently at the top (live mode).
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
    }
}
