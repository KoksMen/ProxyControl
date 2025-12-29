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