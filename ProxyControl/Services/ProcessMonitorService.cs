using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ProxyControl.Services
{
    public class ProcessMonitorService
    {
        private readonly ConcurrentDictionary<int, Lazy<Task<string>>> _processCache
            = new ConcurrentDictionary<int, Lazy<Task<string>>>();

        private ManagementEventWatcher _startWatcher;
        private ManagementEventWatcher _stopWatcher;

        public void Start()
        {
            Task.Run(() => LoadInitialProcesses());

            try
            {
                _startWatcher = new ManagementEventWatcher("SELECT * FROM Win32_ProcessStartTrace");
                _startWatcher.EventArrived += OnProcessStarted;
                _startWatcher.Start();

                _stopWatcher = new ManagementEventWatcher("SELECT * FROM Win32_ProcessStopTrace");
                _stopWatcher.EventArrived += OnProcessStopped;
                _stopWatcher.Start();
            }
            catch { }
        }

        public void Stop()
        {
            try { _startWatcher?.Stop(); _stopWatcher?.Stop(); } catch { }
        }

        public string GetProcessName(int pid)
        {
            if (pid <= 0) return "Unknown";

            var lazyTask = _processCache.GetOrAdd(pid, _ => new Lazy<Task<string>>(() => Task.Run(() => GetProcessNameNative(pid))));

            try { return lazyTask.Value.Result; } catch { return "Unknown"; }
        }

        private void LoadInitialProcesses()
        {
            foreach (var p in Process.GetProcesses())
            {
                int id = p.Id;
                try
                {
                    string name = GetProcessNameNative(id);
                    _processCache.TryAdd(id, new Lazy<Task<string>>(() => Task.FromResult(name)));
                }
                catch { }
            }
        }

        private void OnProcessStarted(object sender, EventArrivedEventArgs e)
        {
            try
            {
                int pid = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
                string name = e.NewEvent.Properties["ProcessName"].Value.ToString();
                _processCache.TryAdd(pid, new Lazy<Task<string>>(() => Task.FromResult(name)));
            }
            catch { }
        }

        private void OnProcessStopped(object sender, EventArrivedEventArgs e)
        {
            try
            {
                int pid = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
                _processCache.TryRemove(pid, out _);
            }
            catch { }
        }

        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);
        [DllImport("kernel32.dll")] private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr hHandle);

        private string GetProcessNameNative(int pid)
        {
            IntPtr hProcess = OpenProcess(0x1000, false, pid); 
            if (hProcess == IntPtr.Zero) return "Unknown";
            try
            {
                int cap = 1024;
                StringBuilder sb = new StringBuilder(cap);
                if (QueryFullProcessImageName(hProcess, 0, sb, ref cap)) return Path.GetFileName(sb.ToString());
            }
            finally { CloseHandle(hProcess); }
            return "Unknown";
        }
    }
}
