using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CodexPortableManager
{
    internal static class ProcessesUnderPath
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;

        internal static void Stop(string root)
        {
            string normalizedRoot = NormalizeDirectoryPrefix(root);
            int currentProcessId = Process.GetCurrentProcess().Id;
            List<Process> matchingProcesses = new List<Process>();
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == currentProcessId) continue;
                    string processPath;
                    if (!TryGetProcessImagePath(process.Id, out processPath)) continue;
                    if (!string.IsNullOrWhiteSpace(processPath) && IsPathUnderPrefix(processPath, normalizedRoot))
                    {
                        matchingProcesses.Add(process);
                        process.CloseMainWindow();
                        continue;
                    }
                }
                catch
                {
                    // 忽略无法访问或已经退出的进程。
                }
                finally
                {
                    if (!matchingProcesses.Contains(process)) process.Dispose();
                }
            }

            Stopwatch gracefulWait = Stopwatch.StartNew();
            foreach (Process process in matchingProcesses)
            {
                try
                {
                    int remainingMilliseconds = Math.Max(0, 2000 - (int)gracefulWait.ElapsedMilliseconds);
                    if (remainingMilliseconds > 0) process.WaitForExit(remainingMilliseconds);
                    if (!process.HasExited) process.Kill();
                }
                catch
                {
                    // 进程可能已正常退出，或当前用户无法向它发送关闭请求。
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        internal static void WaitForExit(string root, TimeSpan timeout)
        {
            if (!Directory.Exists(root)) return;

            string normalizedRoot = NormalizeDirectoryPrefix(root);
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<ProcessMatch> remaining;
            while ((remaining = FindProcessesUnderPrefix(normalizedRoot)).Count > 0)
            {
                if (stopwatch.Elapsed >= timeout)
                {
                    throw new IOException(
                        "等待目录内进程退出超时：" + root + "。仍在运行：" +
                        string.Join("；", remaining.ConvertAll(match => match.ToString()).ToArray()));
                }
                Thread.Sleep(100);
            }
        }

        internal static int[] FindProcessIds(string root)
        {
            string normalizedRoot = NormalizeDirectoryPrefix(root);
            return FindProcessesUnderPrefix(normalizedRoot)
                .ConvertAll(match => match.Id)
                .ToArray();
        }

        private static List<ProcessMatch> FindProcessesUnderPrefix(string normalizedRoot)
        {
            int currentProcessId = Process.GetCurrentProcess().Id;
            List<ProcessMatch> matches = new List<ProcessMatch>();
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == currentProcessId) continue;
                    string processPath;
                    if (!TryGetProcessImagePath(process.Id, out processPath)) continue;
                    if (!string.IsNullOrWhiteSpace(processPath) && IsPathUnderPrefix(processPath, normalizedRoot))
                    {
                        matches.Add(new ProcessMatch(process.Id, process.ProcessName, processPath));
                    }
                }
                catch
                {
                    // 忽略无法访问或已经退出的进程。
                }
                finally
                {
                    process.Dispose();
                }
            }
            return matches;
        }

        private static bool TryGetProcessImagePath(int processId, out string path)
        {
            path = null;
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) return false;
            try
            {
                int capacity = 32768;
                StringBuilder buffer = new StringBuilder(capacity);
                if (!QueryFullProcessImageName(handle, 0, buffer, ref capacity) || capacity <= 0)
                {
                    return false;
                }
                path = buffer.ToString(0, capacity);
                return !string.IsNullOrWhiteSpace(path);
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static string NormalizeDirectoryPrefix(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                fullPath = NativeFileSystem.GetStablePathForExistingPath(fullPath);
            }
            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
        }

        private static bool IsPathUnderPrefix(string filePath, string normalizedRoot)
        {
            string fullPath = Path.GetFullPath(filePath);
            try
            {
                if (File.Exists(fullPath))
                {
                    // QueryFullProcessImageName 可能返回 8.3 路径，而安装根已经解析为长物理路径。
                    // 两侧统一按稳定句柄路径比较，避免漏掉实际位于安装目录内的进程。
                    fullPath = NativeFileSystem.GetStablePathForExistingPath(fullPath);
                }
            }
            catch
            {
                // 进程可能刚退出；保留原路径继续做保守比较。
            }
            return fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(
            IntPtr process,
            int flags,
            StringBuilder executablePath,
            ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private sealed class ProcessMatch
        {
            internal ProcessMatch(int id, string name, string path)
            {
                Id = id;
                Name = name;
                Path = path;
            }

            internal int Id { get; private set; }
            internal string Name { get; private set; }
            internal string Path { get; private set; }

            public override string ToString()
            {
                return Name + " (PID " + Id + ") " + Path;
            }
        }
    }
}
