using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexPortableManager
{
    internal sealed class StorageMaintenanceResult
    {
        public int DeletedPackageFiles { get; internal set; }
        public int DeletedDownloadFiles { get; internal set; }
        public int DeletedInvalidFiles { get; internal set; }
        public int DeletedLogFiles { get; internal set; }
        public int DeletedWorkDirectories { get; internal set; }
        public int SkippedLockedFiles { get; internal set; }
        public long ReclaimedBytes { get; internal set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// 只维护名称完全符合管理器规则的缓存和日志，以及超过保留期的下载临时文件。
    /// 未知文件、未知目录以及没有所有权 marker 的 .cpm-* 工作目录始终不会被此类删除。
    /// </summary>
    internal static class StorageMaintenance
    {
        public static void RunBestEffort(Action<string> log)
        {
            Action<string> safeLog = log ?? delegate { };
            try
            {
                StorageMaintenanceResult result = Run(PortableStorage.CacheRoot, PortableStorage.LogsRoot);
                if (result.ReclaimedBytes > 0 ||
                    result.DeletedPackageFiles > 0 ||
                    result.DeletedDownloadFiles > 0 ||
                    result.DeletedInvalidFiles > 0 ||
                    result.DeletedLogFiles > 0)
                {
                    safeLog(string.Format(
                        CultureInfo.InvariantCulture,
                        "存储维护完成：清理 MSIX {0} 个、无效缓存 {1} 个、临时文件 {2} 个、日志 {3} 个，释放 {4:F1} MiB。",
                        result.DeletedPackageFiles,
                        result.DeletedInvalidFiles,
                        result.DeletedDownloadFiles,
                        result.DeletedLogFiles,
                        result.ReclaimedBytes / 1048576d));
                }
                foreach (string warning in result.Warnings)
                {
                    safeLog("存储维护警告：" + warning);
                }
            }
            catch (Exception exception)
            {
                safeLog("存储维护警告：" + exception.Message);
            }
        }

        public const int DefaultPackagesToKeep = 2;
        public const int DefaultInvalidFilesToKeep = 1;
        public const long DefaultLogSizeLimitBytes = 50L * 1024L * 1024L;
        public static readonly TimeSpan DefaultInvalidRetention = TimeSpan.FromDays(7);
        public static readonly TimeSpan DefaultLogRetention = TimeSpan.FromDays(30);
        public static readonly TimeSpan DefaultDownloadRetention = TimeSpan.FromDays(1);

        public const string WorkMarkerFileName = ".codex-portable-manager-work.json";
        private const string WorkMarkerOwner = "CodexPortableManager";

        private static readonly Regex PackageRegex = new Regex(
            @"^OpenAI\.Codex_(?<version>[0-9]+(?:\.[0-9]+){1,3})_(?<arch>x64|arm64)\.msix$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex InvalidPackageRegex = new Regex(
            @"^OpenAI\.Codex_(?<version>[0-9]+(?:\.[0-9]+){1,3})_(?<arch>x64|arm64)\.msix\.invalid-(?<id>[0-9a-f]{32})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex DownloadTempRegex = new Regex(
            @"^OpenAI\.Codex_[0-9]+(?:\.[0-9]+){1,3}_(?:x64|arm64)\.msix\.download-[0-9a-f]{32}\.msix$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex MaterializeTempRegex = new Regex(
            @"^(?:OpenAI\.Codex_[0-9]+(?:\.[0-9]+){1,3}_(?:x64|arm64)\.msix)?\.materialize-[0-9a-f]{32}\.msix$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex SessionLogRegex = new Regex(
            @"^session-[0-9]{8}-[0-9]{6}\.log$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex StorageErrorLogRegex = new Regex(
            @"^storage-load-error-[0-9]{8}-[0-9]{9}-[0-9a-f]{32}\.log$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex FatalErrorLogRegex = new Regex(
            @"^fatal-error-[0-9]{8}-[0-9]{9}-[0-9a-f]{32}\.log$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex WorkDirectoryRegex = new Regex(
            @"^\.cpm-(?<id>[0-9a-f]{32})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static StorageMaintenanceResult Run(string cacheRoot, string logsRoot)
        {
            return Run(
                cacheRoot,
                logsRoot,
                DateTime.UtcNow,
                DefaultPackagesToKeep,
                DefaultInvalidFilesToKeep,
                DefaultInvalidRetention,
                DefaultLogRetention,
                DefaultLogSizeLimitBytes);
        }

        internal static StorageMaintenanceResult Run(
            string cacheRoot,
            string logsRoot,
            DateTime utcNow,
            int packagesToKeep,
            int invalidFilesToKeep,
            TimeSpan invalidRetention,
            TimeSpan logRetention,
            long logSizeLimitBytes)
        {
            if (packagesToKeep < 0 || invalidFilesToKeep < 0 ||
                invalidRetention < TimeSpan.Zero || logRetention < TimeSpan.Zero ||
                logSizeLimitBytes < 0)
            {
                throw new ArgumentOutOfRangeException("缓存维护参数不能为负数。");
            }

            StorageMaintenanceResult result = new StorageMaintenanceResult();
            MaintainCache(
                cacheRoot,
                utcNow,
                packagesToKeep,
                invalidFilesToKeep,
                invalidRetention,
                result);
            MaintainLogs(logsRoot, utcNow, logRetention, logSizeLimitBytes, result);
            return result;
        }

        public static void WriteWorkMarker(string workRoot, string installRoot)
        {
            string fullWorkRoot = Path.GetFullPath(workRoot);
            Match match = WorkDirectoryRegex.Match(Path.GetFileName(fullWorkRoot));
            if (!match.Success)
            {
                throw new InvalidDataException("工作目录名称不符合 .cpm-<GUID> 规则：" + fullWorkRoot);
            }
            if (!Directory.Exists(fullWorkRoot))
            {
                throw new DirectoryNotFoundException("工作目录不存在：" + fullWorkRoot);
            }
            if ((File.GetAttributes(fullWorkRoot) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("工作目录不能是 junction、符号链接或其他重解析点：" + fullWorkRoot);
            }

            string markerPath = Path.Combine(fullWorkRoot, WorkMarkerFileName);
            string validationError;
            if (File.Exists(markerPath))
            {
                if (TryRecognizeOwnedWorkDirectory(fullWorkRoot, installRoot, out validationError))
                {
                    return;
                }
                throw new InvalidDataException("工作目录已有无效的所有权 marker：" + validationError);
            }

            WorkDirectoryMarker marker = new WorkDirectoryMarker
            {
                Owner = WorkMarkerOwner,
                WorkId = match.Groups["id"].Value.ToLowerInvariant(),
                InstallRootHash = CreateInstallRootHash(installRoot),
                DirectoryIdentity = InstallOwnership.GetManagedDirectoryIdentity(fullWorkRoot),
                CreatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };
            byte[] contents = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(marker));
            using (FileStream stream = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents, 0, contents.Length);
                stream.Flush(true);
            }
        }

        public static bool TryRecognizeOwnedWorkDirectory(
            string workRoot,
            string expectedInstallRoot,
            out string validationError)
        {
            DateTime createdUtc;
            string directoryIdentity;
            return TryReadOwnedWorkMarker(
                workRoot,
                expectedInstallRoot,
                out createdUtc,
                out directoryIdentity,
                out validationError);
        }

        /// <summary>
        /// 只枚举 parentRoot 顶层的 .cpm-&lt;GUID&gt;。目录必须具有与 installRoot 匹配的
        /// marker 且超过 maxAge，才会交给句柄式安全删除；其他目录全部保留并记录警告。
        /// </summary>
        public static StorageMaintenanceResult CleanupOwnedWorkDirectories(
            string parentRoot,
            string installRoot,
            TimeSpan maxAge)
        {
            return CleanupOwnedWorkDirectories(parentRoot, installRoot, maxAge, DateTime.UtcNow);
        }

        internal static StorageMaintenanceResult CleanupOwnedWorkDirectories(
            string parentRoot,
            string installRoot,
            TimeSpan maxAge,
            DateTime utcNow)
        {
            if (maxAge < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(maxAge), "工作目录保留时间不能为负数。");
            }

            StorageMaintenanceResult result = new StorageMaintenanceResult();
            string fullParent;
            try
            {
                fullParent = Path.GetFullPath(parentRoot);
                if (!Directory.Exists(fullParent))
                {
                    return result;
                }
                if ((File.GetAttributes(fullParent) & FileAttributes.ReparsePoint) != 0)
                {
                    result.Warnings.Add("工作目录父路径是重解析点，已拒绝扫描：" + fullParent);
                    return result;
                }
            }
            catch (Exception exception)
            {
                result.Warnings.Add("无法验证工作目录父路径，已跳过清理：" + exception.Message);
                return result;
            }

            string[] candidates;
            try
            {
                candidates = Directory.GetDirectories(fullParent, ".cpm-*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception)
            {
                result.Warnings.Add("无法枚举工作目录，已跳过清理：" + exception.Message);
                return result;
            }

            foreach (string candidate in candidates)
            {
                if (!WorkDirectoryRegex.IsMatch(Path.GetFileName(candidate)))
                {
                    // 通配符可能匹配额外名称；非精确名称不属于管理器清理范围。
                    continue;
                }

                DateTime createdUtc;
                string directoryIdentity;
                string validationError;
                if (!TryReadOwnedWorkMarker(
                    candidate,
                    installRoot,
                    out createdUtc,
                    out directoryIdentity,
                    out validationError))
                {
                    result.Warnings.Add("保留未知工作目录“" + candidate + "”：" + validationError);
                    continue;
                }

                if (utcNow - createdUtc < maxAge)
                {
                    result.Warnings.Add("保留尚未超过清理期限的工作目录：" + candidate);
                    continue;
                }

                try
                {
                    NativeFileSystem.DeleteDirectoryRecursively(
                        candidate,
                        directoryIdentity);
                    if (Directory.Exists(candidate))
                    {
                        result.Warnings.Add("工作目录删除后仍然存在，可能正被其他进程锁定：" + candidate);
                    }
                    else
                    {
                        result.DeletedWorkDirectories++;
                    }
                }
                catch (Exception exception)
                {
                    result.Warnings.Add(
                        "无法清理可能仍被进程锁定的工作目录“" + candidate + "”，已保留剩余内容：" +
                        exception.Message);
                }
            }

            return result;
        }

        private static bool TryReadOwnedWorkMarker(
            string workRoot,
            string expectedInstallRoot,
            out DateTime createdUtc,
            out string directoryIdentity,
            out string validationError)
        {
            createdUtc = default(DateTime);
            directoryIdentity = null;
            validationError = null;
            try
            {
                string fullWorkRoot = Path.GetFullPath(workRoot);
                Match match = WorkDirectoryRegex.Match(Path.GetFileName(fullWorkRoot));
                if (!match.Success)
                {
                    validationError = "目录名称不符合 .cpm-<GUID> 规则。";
                    return false;
                }
                if (!Directory.Exists(fullWorkRoot) ||
                    (File.GetAttributes(fullWorkRoot) & FileAttributes.ReparsePoint) != 0)
                {
                    validationError = "目录不存在或目录本身是重解析点。";
                    return false;
                }

                string markerPath = Path.Combine(fullWorkRoot, WorkMarkerFileName);
                FileInfo markerInfo = new FileInfo(markerPath);
                if (!markerInfo.Exists || markerInfo.Length <= 0 || markerInfo.Length > 4096 ||
                    (markerInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    validationError = "marker 不存在、大小异常或本身是重解析点。";
                    return false;
                }

                WorkDirectoryMarker marker = new JavaScriptSerializer().Deserialize<WorkDirectoryMarker>(
                    File.ReadAllText(markerPath, Encoding.UTF8));
                if (marker == null ||
                    !string.Equals(marker.Owner, WorkMarkerOwner, StringComparison.Ordinal) ||
                    !string.Equals(marker.WorkId, match.Groups["id"].Value, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(marker.InstallRootHash, CreateInstallRootHash(expectedInstallRoot), StringComparison.Ordinal) ||
                    !InstallOwnership.IsManagedDirectoryIdentity(marker.DirectoryIdentity) ||
                    !DateTime.TryParse(
                        marker.CreatedUtc,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out createdUtc) ||
                    createdUtc > DateTime.UtcNow.AddDays(1))
                {
                    validationError = "marker 内容、安装根哈希或创建时间无效。";
                    return false;
                }

                string actualIdentity = InstallOwnership.GetManagedDirectoryIdentity(fullWorkRoot);
                if (!string.Equals(
                    actualIdentity,
                    marker.DirectoryIdentity,
                    StringComparison.OrdinalIgnoreCase))
                {
                    validationError = "工作目录身份与 marker 不一致。";
                    return false;
                }

                directoryIdentity = marker.DirectoryIdentity;
                return true;
            }
            catch (Exception exception)
            {
                validationError = exception.Message;
                return false;
            }
        }

        private static void MaintainCache(
            string cacheRoot,
            DateTime utcNow,
            int packagesToKeep,
            int invalidFilesToKeep,
            TimeSpan invalidRetention,
            StorageMaintenanceResult result)
        {
            List<OrdinaryFileCandidate> files;
            if (!TryEnumerateOrdinaryRoot(cacheRoot, "缓存", result, out files))
            {
                return;
            }

            List<PackageCandidate> packages = new List<PackageCandidate>();
            List<OrdinaryFileCandidate> invalidFiles = new List<OrdinaryFileCandidate>();
            List<OrdinaryFileCandidate> downloadFiles = new List<OrdinaryFileCandidate>();
            foreach (OrdinaryFileCandidate file in files)
            {
                Match packageMatch = PackageRegex.Match(file.Name);
                Version version;
                if (packageMatch.Success && Version.TryParse(packageMatch.Groups["version"].Value, out version))
                {
                    packages.Add(new PackageCandidate
                    {
                        File = file,
                        Version = version,
                        Architecture = packageMatch.Groups["arch"].Value.ToLowerInvariant()
                    });
                    continue;
                }
                if (InvalidPackageRegex.IsMatch(file.Name))
                {
                    invalidFiles.Add(file);
                    continue;
                }
                if (DownloadTempRegex.IsMatch(file.Name) || MaterializeTempRegex.IsMatch(file.Name))
                {
                    downloadFiles.Add(file);
                }
            }

            foreach (PackageCandidate package in packages
                .GroupBy(value => value.Architecture, StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => group
                    .OrderByDescending(value => value.Version)
                    .ThenByDescending(value => value.File.LastWriteTimeUtc)
                    .Skip(packagesToKeep)))
            {
                if (TryDeleteCacheFile(cacheRoot, package.File, result))
                {
                    result.DeletedPackageFiles++;
                }
            }

            DateTime invalidCutoff = utcNow.Subtract(invalidRetention);
            List<OrdinaryFileCandidate> orderedInvalidFiles = invalidFiles
                .OrderByDescending(value => value.LastWriteTimeUtc)
                .ToList();
            for (int index = 0; index < orderedInvalidFiles.Count; index++)
            {
                OrdinaryFileCandidate file = orderedInvalidFiles[index];
                if (index >= invalidFilesToKeep || file.LastWriteTimeUtc < invalidCutoff)
                {
                    if (TryDeleteCacheFile(cacheRoot, file, result))
                    {
                        result.DeletedInvalidFiles++;
                    }
                }
            }

            DateTime downloadCutoff = utcNow.Subtract(DefaultDownloadRetention);
            foreach (OrdinaryFileCandidate file in downloadFiles.Where(
                value => value.LastWriteTimeUtc < downloadCutoff))
            {
                if (TryDeleteCacheFile(cacheRoot, file, result))
                {
                    result.DeletedDownloadFiles++;
                }
            }
        }

        private static void MaintainLogs(
            string logsRoot,
            DateTime utcNow,
            TimeSpan retention,
            long sizeLimitBytes,
            StorageMaintenanceResult result)
        {
            List<OrdinaryFileCandidate> files;
            if (!TryEnumerateOrdinaryRoot(logsRoot, "日志", result, out files))
            {
                return;
            }

            List<OrdinaryFileCandidate> managedLogs = files
                .Where(value =>
                    SessionLogRegex.IsMatch(value.Name) ||
                    StorageErrorLogRegex.IsMatch(value.Name) ||
                    FatalErrorLogRegex.IsMatch(value.Name))
                .ToList();
            DateTime cutoff = utcNow.Subtract(retention);
            foreach (OrdinaryFileCandidate file in managedLogs.Where(
                value => value.LastWriteTimeUtc < cutoff).ToList())
            {
                if (TryDeleteOrdinaryFile(logsRoot, file, result))
                {
                    result.DeletedLogFiles++;
                    managedLogs.Remove(file);
                }
            }

            long totalBytes = managedLogs
                .Where(value => value.Exists)
                .Sum(value => SafeGetLength(value.File));
            foreach (OrdinaryFileCandidate file in managedLogs.OrderBy(
                value => value.LastWriteTimeUtc))
            {
                if (totalBytes <= sizeLimitBytes)
                {
                    break;
                }

                long length = SafeGetLength(file.File);
                if (TryDeleteOrdinaryFile(logsRoot, file, result))
                {
                    result.DeletedLogFiles++;
                    totalBytes = Math.Max(0, totalBytes - length);
                }
            }
        }

        private static bool TryEnumerateOrdinaryRoot(
            string root,
            string description,
            StorageMaintenanceResult result,
            out List<OrdinaryFileCandidate> files)
        {
            files = new List<OrdinaryFileCandidate>();
            try
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    result.Warnings.Add(description + "目录为空，已跳过维护。");
                    return false;
                }

                string fullRoot = Path.GetFullPath(root);
                if (!Directory.Exists(fullRoot))
                {
                    return false;
                }
                if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
                {
                    result.Warnings.Add(description + "目录是重解析点，已拒绝维护：" + fullRoot);
                    return false;
                }

                foreach (string path in Directory.EnumerateFiles(
                    fullRoot,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        string identity = NativeFileSystem.GetPersistentFileIdentity(path);
                        files.Add(new OrdinaryFileCandidate(
                            new FileInfo(path),
                            identity));
                    }
                    catch (Exception exception)
                    {
                        result.Warnings.Add(
                            "无法绑定待维护文件身份，已保留“" + path + "”：" +
                            exception.Message);
                    }
                }
                return true;
            }
            catch (Exception exception)
            {
                result.Warnings.Add("无法枚举" + description + "目录：" + exception.Message);
                return false;
            }
        }

        private static bool TryDeleteCacheFile(
            string expectedRoot,
            OrdinaryFileCandidate file,
            StorageMaintenanceResult result)
        {
            try
            {
                using (CacheFileLock cacheLock = CacheFileLock.TryAcquire(file.FullName))
                {
                    if (cacheLock == null)
                    {
                        result.SkippedLockedFiles++;
                        return false;
                    }
                    return TryDeleteOrdinaryFile(expectedRoot, file, result);
                }
            }
            catch (Exception exception)
            {
                result.Warnings.Add("无法取得缓存维护锁，已保留文件“" + file.FullName + "”：" + exception.Message);
                return false;
            }
        }

        private static bool TryDeleteOrdinaryFile(
            string expectedRoot,
            OrdinaryFileCandidate file,
            StorageMaintenanceResult result)
        {
            try
            {
                string fullRoot = Path.GetFullPath(expectedRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullPath = Path.GetFullPath(file.FullName);
                if (!string.Equals(Path.GetDirectoryName(fullPath), fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    result.Warnings.Add("拒绝清理目标目录之外的文件：" + fullPath);
                    return false;
                }
                if (!File.Exists(fullPath))
                {
                    return true;
                }

                long length = SafeGetLength(new FileInfo(fullPath));
                NativeFileSystem.DeleteFile(fullPath, file.Identity);
                result.ReclaimedBytes += length;
                return true;
            }
            catch (Exception exception)
            {
                result.Warnings.Add("无法清理文件“" + file.FullName + "”：" + exception.Message);
                return false;
            }
        }

        private static long SafeGetLength(FileInfo file)
        {
            try
            {
                return file.Exists ? file.Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static string CreateInstallRootHash(string installRoot)
        {
            return CrossProcessFileLock.ComputeKeyHash(
                "install-root|" + CrossProcessFileLock.NormalizePathKey(installRoot));
        }

        private sealed class PackageCandidate
        {
            public OrdinaryFileCandidate File { get; set; }
            public Version Version { get; set; }
            public string Architecture { get; set; }
        }

        private sealed class OrdinaryFileCandidate
        {
            internal OrdinaryFileCandidate(FileInfo file, string identity)
            {
                File = file ?? throw new ArgumentNullException(nameof(file));
                Identity = identity;
            }

            internal FileInfo File { get; private set; }
            internal string Identity { get; private set; }
            internal string Name { get { return File.Name; } }
            internal string FullName { get { return File.FullName; } }
            internal bool Exists { get { return File.Exists; } }
            internal DateTime LastWriteTimeUtc { get { return File.LastWriteTimeUtc; } }
        }

        private sealed class WorkDirectoryMarker
        {
            public string Owner { get; set; }
            public string WorkId { get; set; }
            public string InstallRootHash { get; set; }
            public string DirectoryIdentity { get; set; }
            public string CreatedUtc { get; set; }
        }
    }
}
