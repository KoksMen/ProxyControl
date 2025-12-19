using ProxyControl.Services;
using System.Configuration;
using System.Data;
using System.Windows;

namespace ProxyControl
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            SystemProxyHelper.RestoreSystemProxy();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            SystemProxyHelper.EnableSafetyNet();

            this.DispatcherUnhandledException += (s, args) => SystemProxyHelper.RestoreSystemProxy();
            System.AppDomain.CurrentDomain.UnhandledException += (s, args) => SystemProxyHelper.RestoreSystemProxy();

            var mainWindow = new MainWindow();

            bool isAutostart = e.Args.Contains("--autostart");

            if (isAutostart)
            {
            }
            else
            {
                mainWindow.Show();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            SystemProxyHelper.DisableSafetyNet();
            SystemProxyHelper.RestoreSystemProxy();
            base.OnExit(e);
        }
    }

}
