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
            // Find the parent TaskbarIcon to close the balloon
            var taskbarIcon = TaskbarIcon.GetParentTaskbarIcon(this);
            taskbarIcon?.CloseBalloon();
        }
    }
}
