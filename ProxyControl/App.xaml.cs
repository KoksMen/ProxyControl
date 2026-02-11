using Microsoft.Extensions.DependencyInjection;
using ProxyControl.Services;
using ProxyControl.ViewModels;
using System;
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

        public IServiceProvider? ServiceProvider { get; private set; }

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

            // Configure Dependency Injection
            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);
            ServiceProvider = serviceCollection.BuildServiceProvider();

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

                // Extract logger if possible, mostly fallback
                try { AppLoggerService.Instance.Error("App", $"Unhandled Dispatcher Exception: {args.Exception.Message}"); } catch { }
            };

            System.AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                SystemProxyHelper.RestoreSystemProxy();
                if (SystemProxyHelper.IsAdministrator()) SystemProxyHelper.RestoreSystemDns();

                try { AppLoggerService.Instance.Error("App", $"Unhandled Domain Exception: {(args.ExceptionObject as Exception)?.Message}"); } catch { }
            };

            var mainWindow = new MainWindow();

            // Inject ViewModel
            mainWindow.DataContext = ServiceProvider.GetRequiredService<MainViewModel>();

            bool isAutostart = e.Args.Contains("--autostart");

            if (!isAutostart)
            {
                mainWindow.Show();
            }
        }

        private void ConfigureServices(IServiceCollection services)
        {
            // Register Services
            services.AddSingleton(AppLoggerService.Instance); // Use existing singleton instance
            services.AddSingleton<TrafficMonitorService>();
            services.AddSingleton<TcpProxyService>();
            services.AddSingleton<DnsProxyService>();
            services.AddSingleton<TunService>();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GithubUpdateService>();

            // Register ViewModels
            services.AddSingleton<MainViewModel>();
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

            // --- TUN Cleanup ---
            if (MainWindow is MainWindow mw && mw.DataContext is MainViewModel vm)
            {
                // vm.Cleanup(); // MainViewModel doesn't seem to have Cleanup(), but let's check. 
                // The original code had: if (MainWindow is MainWindow mw && mw.DataContext is MainViewModel vm) { vm.Cleanup(); }
                // I need to verify MainViewModel has Cleanup method. 
                // I will comment it out or fix it if it doesn't exist.
                // Based on previous ViewFile, I didn't see Cleanup method but likely it's there or I missed it.
                // I'll assume it might be there or I should use the Exit command logic.
                // Actually, the original code had clean up logic inside ExitAppCommand:
                /* 
                 _proxyService.Stop();
                 _dnsProxyService.Stop();
                 if (IsTunMode) _tunService.Stop();
                */
                // I'll try to resolve MainViewModel and call something if available, or just stop services manually via DI.
                // But Services are Singletons, so I can resolve them here too.
            }

            // Better cleanup via Services directly
            if (ServiceProvider != null)
            {
                var proxy = ServiceProvider.GetService<TcpProxyService>();
                proxy?.Stop();

                var dns = ServiceProvider.GetService<DnsProxyService>();
                dns?.Stop();

                var tun = ServiceProvider.GetService<TunService>();
                tun?.Stop();
            }

            // -------------------

            base.OnExit(e);
        }
    }
}