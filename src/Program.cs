using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace CodexPortableManager
{
    internal static class Program
    {
        private const string GuiMutexName = @"Local\OpenAI.CodexPortableManager.Gui.1E8A568B-9D9F-46F8-9F3F-1D94C5735DC7";
        private const int FatalExitCode = 1;
        private static int fatalExitStarted;

        [STAThread]
        private static void Main(string[] args)
        {
            EmbeddedAssemblyResolver.Initialize();
            AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
                ExitAfterUnhandledException(
                    "CLR 未处理异常",
                    eventArgs.ExceptionObject as Exception ??
                        new InvalidOperationException("CLR 抛出了非 Exception 类型的未处理对象。"));
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            int cleanupExitCode;
            if (UninstallCleanupWorker.TryRun(args, out cleanupExitCode))
            {
                Environment.ExitCode = cleanupExitCode;
                return;
            }
            bool createdNew;
            using (Mutex guiMutex = new Mutex(true, GuiMutexName, out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("Codex Portable Manager 已在运行，请使用现有窗口。", "程序已运行", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                Application application = new App { ShutdownMode = ShutdownMode.OnMainWindowClose };
                application.DispatcherUnhandledException += (sender, eventArgs) =>
                    ExitAfterUnhandledException("WPF Dispatcher 未处理异常", eventArgs.Exception);
                TaskScheduler.UnobservedTaskException += (sender, eventArgs) =>
                {
                    eventArgs.SetObserved();
                    ExitAfterUnhandledException("未观察的任务异常", eventArgs.Exception);
                };
                try { application.Run(new MainWindow(true)); }
                finally { guiMutex.ReleaseMutex(); }
            }
        }

        private static void ExitAfterUnhandledException(string source, Exception exception)
        {
            if (Interlocked.Exchange(ref fatalExitStarted, 1) != 0)
            {
                Environment.Exit(FatalExitCode);
                return;
            }

            try
            {
                // 致命异常期间不能等待模态 UI，否则嵌套消息循环可能让未知状态下的操作继续推进。
                PortableStorage.RecordFatalException(source, exception);
            }
            finally
            {
                Environment.Exit(FatalExitCode);
            }
        }
    }
}
