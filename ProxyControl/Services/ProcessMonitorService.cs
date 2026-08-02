using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ProxyControl.Services
{
    public class ProcessMonitorService
    {
        public void Start()
        {
            // Process identity is resolved for the current PID on demand. Keeping a
            // PID keyed cache is unsafe because Windows reuses PIDs, particularly
            // with short-lived and packaged/Metro application processes.
        }

        public void Stop()
        {
        }

        public string GetProcessName(int pid)
        {
            if (pid <= 0) return "Unknown";

            try { return ResolveProcessName(pid); } catch { return "Unknown"; }
        }

        public (string Name, string Path) GetProcessIdentity(int pid)
        {
            if (pid <= 0) return ("Unknown", "");

            string path = GetProcessPathNative(pid);
            if (!string.IsNullOrWhiteSpace(path))
                return (Path.GetFileName(path), path);

            try
            {
                using var process = Process.GetProcessById(pid);
                path = process.MainModule?.FileName ?? "";
                return (process.ProcessName + ".exe", path);
            }
            catch
            {
                return (GetProcessName(pid), "");
            }
        }

        private string ResolveProcessName(int pid)
        {
            // 1. Try Native method (fast, low overhead)
            string name = GetProcessNameNative(pid);
            if (!string.IsNullOrEmpty(name) && name != "Unknown") return name;

            // 2. Fallback to .NET Process API (reliable but heavier)
            try
            {
                var p = Process.GetProcessById(pid);
                return p.ProcessName + ".exe";
            }
            catch
            {
                return "Unknown";
            }
        }

        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);
        [DllImport("kernel32.dll")] private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr hHandle);

        private string GetProcessNameNative(int pid)
        {
            string path = GetProcessPathNative(pid);
            return string.IsNullOrWhiteSpace(path) ? "Unknown" : Path.GetFileName(path);
        }

        private string GetProcessPathNative(int pid)
        {
            // PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
            IntPtr hProcess = OpenProcess(0x1000, false, pid);
            if (hProcess == IntPtr.Zero) return "";
            try
            {
                int cap = 1024;
                StringBuilder sb = new StringBuilder(cap);
                int size = cap;
                if (QueryFullProcessImageName(hProcess, 0, sb, ref size)) return sb.ToString();
            }
            finally { CloseHandle(hProcess); }
            return "";
        }
    }
}
