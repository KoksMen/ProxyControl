using Hardcodet.Wpf.TaskbarNotification;
using System.Windows;
using System.Windows.Controls;

namespace ProxyControl.Views
{
    public partial class UpdateToast : UserControl
    {
        public UpdateToast()
        {
            InitializeComponent();
        }

        private void OnDismissClick(object sender, RoutedEventArgs e)
        {
            var taskbarIcon = TaskbarIcon.GetParentTaskbarIcon(this);
            taskbarIcon?.CloseBalloon();
        }

        private void OnUpdateClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.MainViewModel vm)
            {
                if (vm.OpenUpdateModalCommand.CanExecute(null))
                {
                    vm.OpenUpdateModalCommand.Execute(null);
                }
            }

            var taskbarIcon = TaskbarIcon.GetParentTaskbarIcon(this);
            taskbarIcon?.CloseBalloon();
        }
    }
}
