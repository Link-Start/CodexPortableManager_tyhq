using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPortableManager
{
    internal static class UninstallCleanupWorker
    {
        internal const string Command = "--complete-uninstall-cleanup";
        internal const int CleanupPendingExitCode = 2;

        internal static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0 ||
                !string.Equals(args[0], Command, StringComparison.Ordinal))
            {
                return false;
            }
            if (args.Length != 2)
            {
                exitCode = 64;
                return true;
            }

            exitCode = Run(args[1]);
            return true;
        }

        internal static Task<int> StartAsync(
            string installRoot,
            Action<string> logAction)
        {
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                throw new ArgumentException("后台清理目标目录不能为空。", nameof(installRoot));
            }
            Action<string> log = logAction ?? delegate { };
            string executablePath = typeof(Program).Assembly.Location;
            Process process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = Command + " " + QuoteArgument(installRoot),
                    WorkingDirectory = Path.GetDirectoryName(executablePath),
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };
            TaskCompletionSource<int> completion = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int completionStarted = 0;
            Action complete = () =>
            {
                if (Interlocked.Exchange(ref completionStarted, 1) != 0)
                {
                    return;
                }
                try
                {
                    int code = process.ExitCode;
                    if (code == 0)
                    {
                        log("卸载后台清理已经完成。");
                    }
                    else if (code == CleanupPendingExitCode)
                    {
                        log("卸载后台清理尚未完成，管理器将在后续启动或检查时继续恢复。");
                    }
                    else
                    {
                        log("卸载后台清理进程异常结束，退出代码：" + code + "。");
                    }
                    completion.TrySetResult(code);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    process.Dispose();
                }
            };
            process.Exited += (sender, eventArgs) => complete();

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("Windows 没有启动卸载后台清理进程。");
                }
                log("已启动卸载后台清理进程，PID=" + process.Id + "。");
                if (process.HasExited)
                {
                    complete();
                }
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref completionStarted, 1) == 0)
                {
                    process.Dispose();
                    completion.TrySetException(exception);
                }
            }
            return completion.Task;
        }

        private static int Run(string installRoot)
        {
            string logPath = null;
            Action<string> log = message =>
            {
                try
                {
                    if (logPath == null)
                    {
                        Directory.CreateDirectory(PortableStorage.LogsRoot);
                        logPath = Path.Combine(
                            PortableStorage.LogsRoot,
                            "uninstall-cleanup-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") +
                            "-" + Guid.NewGuid().ToString("N") + ".log");
                    }
                    File.AppendAllText(
                        logPath,
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " +
                        message + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch
                {
                    // 后台诊断日志不可写时仍继续依赖 journal 完成安全清理。
                }
            };

            try
            {
                log("开始清理已经逻辑卸载的 Codex 程序目录：" + installRoot);
                using (CodexPortableService service = new CodexPortableService(log))
                {
                    bool complete = service.CompletePendingUninstallCleanup(installRoot);
                    log(complete
                        ? "卸载程序目录及事务元数据已经清理完成。"
                        : "仍有卸载清理待办，后续操作将继续恢复。");
                    return complete ? 0 : CleanupPendingExitCode;
                }
            }
            catch (Exception exception)
            {
                log("卸载后台清理失败：" + exception);
                return 1;
            }
        }

        private static string QuoteArgument(string value)
        {
            if (value.IndexOf('"') >= 0)
            {
                throw new ArgumentException("后台清理目录包含不受支持的引号。", nameof(value));
            }
            return "\"" + value + "\"";
        }
    }
}
