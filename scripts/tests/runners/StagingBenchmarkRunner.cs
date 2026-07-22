using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace CodexPortableManager
{
    internal static class StagingBenchmarkRunner
    {
        internal static int Run(string reportPath, string packagePath, string stagingRoot)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
                {
                    throw new FileNotFoundException("没有找到 staging 基准使用的 MSIX。", packagePath);
                }
                if (string.IsNullOrWhiteSpace(stagingRoot))
                {
                    throw new ArgumentException("staging 基准目录不能为空。", nameof(stagingRoot));
                }

                using (StagingBuildResult result = StagingBuilder.ExtractAndValidate(
                    Path.GetFullPath(packagePath),
                    Path.GetFullPath(stagingRoot),
                    CancellationToken.None))
                {
                    stopwatch.Stop();
                    File.WriteAllText(
                        reportPath,
                        "RESULT=PASS" + Environment.NewLine +
                        "SECONDS=" + stopwatch.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) + Environment.NewLine +
                        "FILES=" + result.ExtractedFileCount.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                        "DIRECTORIES=" + result.ValidatedDirectoryCount.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                        "SKIPPED_DIRECTORY_PROBES=" + result.SkippedDirectoryProbeCount.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                        "WORKERS=" + result.WorkerCount.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                        "BYTES=" + result.ExtractedBytes.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                        "BLOCKS=" + result.VerifiedBlockCount.ToString(CultureInfo.InvariantCulture),
                        new UTF8Encoding(false));
                }
                return 0;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                File.WriteAllText(
                    reportPath,
                    "RESULT=FAIL" + Environment.NewLine +
                    "SECONDS=" + stopwatch.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) + Environment.NewLine +
                    exception,
                    new UTF8Encoding(false));
                return 1;
            }
        }
    }
}
