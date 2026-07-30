using System;
using System.Globalization;
using System.Runtime;

namespace CodexPortableManager
{
    internal static class CompatibilityAnalysisMemory
    {
        internal const long MinimumGrowthBytes = 64L * 1024 * 1024;
        internal const long LargeHeapBytes = 256L * 1024 * 1024;

        internal static T Run<T>(Func<T> operation, Action<string> log)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            long before = GC.GetTotalMemory(false);
            try
            {
                return operation();
            }
            finally
            {
                TryReclaim(before, log);
            }
        }

        internal static bool ShouldReclaim(long before, long after)
        {
            if (before < 0 || after < 0) return false;
            long growth = after > before ? after - before : 0;
            return growth >= MinimumGrowthBytes || after >= LargeHeapBytes;
        }

        private static void TryReclaim(long before, Action<string> log)
        {
            long afterAnalysis = GC.GetTotalMemory(false);
            if (!ShouldReclaim(before, afterAnalysis)) return;

            try
            {
                GCSettings.LargeObjectHeapCompactionMode =
                    GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(
                    GC.MaxGeneration,
                    GCCollectionMode.Forced,
                    true,
                    true);
                long afterCollection = GC.GetTotalMemory(false);
                SafeLog(
                    log,
                    "兼容分析临时内存已回收：托管堆 " +
                    FormatMiB(afterAnalysis) + " MiB -> " +
                    FormatMiB(afterCollection) + " MiB。");
            }
            catch (Exception exception)
            {
                SafeLog(log, "兼容分析临时内存回收已降级跳过：" + exception.Message);
            }
        }

        private static string FormatMiB(long bytes)
        {
            return (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static void SafeLog(Action<string> log, string message)
        {
            if (log == null) return;
            try { log(message); }
            catch { }
        }
    }
}
