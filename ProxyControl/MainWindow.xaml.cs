using ProxyControl.ViewModels;
using System;
using System.ComponentModel;
using System.Windows;

namespace ProxyControl
{
    public partial class MainWindow : Window
    {
        // Статическое поле для управления реальным закрытием
        public static bool AllowClose { get; set; } = false;

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
                this.Hide();
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
                this.Hide();
            }
            base.OnStateChanged(e);
        }
    }
}