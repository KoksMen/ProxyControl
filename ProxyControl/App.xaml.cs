using ProxyControl.Services;
using System.IO;
using System;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ProxyControl.ViewModels;
using System.Windows;

namespace ProxyControl
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private const string MutexName = "Global\\ProxyManagerApp_Unique_Mutex_ID_v1";
        private Mutex _mutex;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_RESTORE = 9;

        public App()
        {
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            // Ensure the working directory is correct (fixes autostart issues where CWD is System32)
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

            _mutex = new Mutex(true, MutexName, out createdNew);

            if (!createdNew)
            {
                ActivateExistingInstance();
                Shutdown();
                return;
            }

            // Only the owning instance may recover settings left by a crashed run.
            // A second launch must never reset the active instance's proxy.
            SystemProxyHelper.RestoreSystemProxy();
            TunService.StopOrphanedManagedProcesses();
            base.OnStartup(e);

            // Reset DNS only if a previous ProxyControl-managed loopback DNS is still active.
            if (SystemProxyHelper.IsAdministrator())
            {
                SystemProxyHelper.RestoreSystemDnsIfManagedByProxyControl();
            }

            SystemProxyHelper.EnableSafetyNet();

            this.DispatcherUnhandledException += (s, args) =>
            {
                SystemProxyHelper.RestoreSystemProxy();
                // Также пытаемся сбросить DNS при краше
                if (SystemProxyHelper.IsAdministrator()) SystemProxyHelper.RestoreSystemDnsIfManagedByProxyControl();
            };

            System.AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                SystemProxyHelper.RestoreSystemProxy();
                if (SystemProxyHelper.IsAdministrator()) SystemProxyHelper.RestoreSystemDnsIfManagedByProxyControl();
            };

            var mainWindow = new MainWindow();
            bool isAutostart = e.Args.Contains("--autostart");

            if (!isAutostart)
            {
                mainWindow.Show();
            }
        }

        private void ActivateExistingInstance()
        {
            Process current = Process.GetCurrentProcess();
            foreach (Process process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id != current.Id)
                {
                    IntPtr hWnd = process.MainWindowHandle;
                    if (hWnd != IntPtr.Zero)
                    {
                        ShowWindow(hWnd, SW_RESTORE);
                        SetForegroundWindow(hWnd);
                    }
                    break;
                }
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _mutex.Dispose();
            }
            // --- TUN Cleanup ---
            if (MainWindow is MainWindow mw && mw.DataContext is MainViewModel vm)
            {
                vm.Cleanup();
            }
            // -------------------

            // Final, synchronous cleanup after every background service has been
            // stopped. This is intentionally last to prevent a worker from
            // re-applying settings during shutdown.
            SystemProxyHelper.DisableSafetyNet();
            SystemProxyHelper.RestoreSystemProxy();
            SystemProxyHelper.RestoreSystemDnsIfManagedByProxyControl();
            TunService.StopOrphanedManagedProcesses();

            base.OnExit(e);
        }
    }
}
