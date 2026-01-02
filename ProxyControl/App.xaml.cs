using ProxyControl.Services;
using System;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
            SystemProxyHelper.RestoreSystemProxy();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            _mutex = new Mutex(true, MutexName, out createdNew);

            if (!createdNew)
            {
                ActivateExistingInstance();
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // --- Добавлено: Сброс DNS при запуске (исправление залипания) ---
            if (SystemProxyHelper.IsAdministrator())
            {
                SystemProxyHelper.RestoreSystemDns();
            }
            // ----------------------------------------------------------------

            SystemProxyHelper.EnableSafetyNet();

            this.DispatcherUnhandledException += (s, args) =>
            {
                SystemProxyHelper.RestoreSystemProxy();
                // Также пытаемся сбросить DNS при краше
                if (SystemProxyHelper.IsAdministrator()) SystemProxyHelper.RestoreSystemDns();
            };

            System.AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                SystemProxyHelper.RestoreSystemProxy();
                if (SystemProxyHelper.IsAdministrator()) SystemProxyHelper.RestoreSystemDns();
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
            SystemProxyHelper.DisableSafetyNet();
            SystemProxyHelper.RestoreSystemProxy();

            // --- Добавлено: Сброс DNS при выходе ---
            SystemProxyHelper.RestoreSystemDns();
            // ---------------------------------------

            base.OnExit(e);
        }
    }
}