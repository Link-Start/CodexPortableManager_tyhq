using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexPortableManager
{
    internal static class PortableStorage
    {
        public static string DataRoot
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"); }
        }

        public static string CacheRoot
        {
            get { return Path.Combine(DataRoot, "cache"); }
        }

        public static string UserDataRoot
        {
            get { return GetUserDataRoot(DataRoot, GetCurrentUserStorageKey()); }
        }

        internal static string CurrentUserStorageKey
        {
            get { return GetCurrentUserStorageKey(); }
        }

        internal static string SharedLocksRoot
        {
            get { return Path.Combine(DataRoot, "locks"); }
        }

        public static string LogsRoot
        {
            get { return Path.Combine(UserDataRoot, "logs"); }
        }

        public static string RecordFatalException(string source, Exception exception)
        {
            return RecordFatalException(source, exception, LogsRoot);
        }

        internal static string RecordFatalException(string source, Exception exception, string logsRoot)
        {
            try
            {
                Directory.CreateDirectory(logsRoot);
                string logPath = Path.Combine(
                    logsRoot,
                    "fatal-error-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".log");
                string message = string.Format(
                    "时间（UTC）：{0:O}{1}来源：{2}{1}进程：{3}（PID {4}）{1}程序版本：{5}{1}操作系统：{6}{1}异常：{7}{1}",
                    DateTime.UtcNow,
                    Environment.NewLine,
                    string.IsNullOrWhiteSpace(source) ? "未知" : source,
                    AppDomain.CurrentDomain.FriendlyName,
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    System.Reflection.Assembly.GetExecutingAssembly().GetName().Version,
                    Environment.OSVersion,
                    exception == null ? "未提供异常对象。" : exception.ToString());
                File.WriteAllText(logPath, message, new UTF8Encoding(false));
                return logPath;
            }
            catch
            {
                return null;
            }
        }

        private static string ConfigPath
        {
            get { return Path.Combine(UserDataRoot, "config.json"); }
        }

        internal static string IntegrationStateFilePath
        {
            get { return Path.Combine(UserDataRoot, "integration.json"); }
        }

        public static void SaveIntegrationState(IntegrationState state)
        {
            string json = new JavaScriptSerializer().Serialize(state);
            WriteAllTextAtomically(IntegrationStateFilePath, json);
        }

        public static IntegrationState LoadIntegrationState()
        {
            try
            {
                return File.Exists(IntegrationStateFilePath)
                    ? new JavaScriptSerializer().Deserialize<IntegrationState>(File.ReadAllText(IntegrationStateFilePath, Encoding.UTF8))
                    : null;
            }
            catch (Exception ex)
            {
                RecordLoadFailure(IntegrationStateFilePath, ex);
                return null;
            }
        }

        public static void DeleteIntegrationState()
        {
            ExecuteWithStorageLock(IntegrationStateFilePath, delegate
            {
                if (File.Exists(IntegrationStateFilePath)) NativeFileSystem.DeleteFile(IntegrationStateFilePath);
            });
        }

        internal static void DeleteIntegrationStateIfSha256Matches(string expectedSha256)
        {
            ExecuteWithStorageLock(IntegrationStateFilePath, delegate
            {
                NativeFileSystem.DeleteFileIfSha256Matches(
                    IntegrationStateFilePath,
                    expectedSha256);
            });
        }

        internal static bool IntegrationStateFileExists()
        {
            return File.Exists(IntegrationStateFilePath);
        }

        public static ManagerSettings LoadSettings()
        {
            ManagerConfig config = LoadConfigCore();
            if (config == null)
            {
                return new ManagerSettings();
            }
            return new ManagerSettings
            {
                InstallRoot = config.InstallRoot
            };
        }

        public static void SaveRecordedInstallRoot(string installRoot)
        {
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                throw new ArgumentException("安装目录记录不能为空。", nameof(installRoot));
            }
            string normalizedRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(installRoot.Trim()))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            UpdateConfig(delegate(ManagerConfig config)
            {
                if (PathsEqual(config.InstallRoot, normalizedRoot))
                {
                    return false;
                }

                config.InstallRoot = normalizedRoot;
                return true;
            });
        }

        public static void ClearRecordedInstallRoot()
        {
            UpdateConfig(delegate(ManagerConfig config)
            {
                if (string.IsNullOrWhiteSpace(config.InstallRoot))
                {
                    return false;
                }

                config.InstallRoot = null;
                return true;
            });
        }

        public static bool ClearRecordedInstallRootIfMatches(string installRoot)
        {
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                throw new ArgumentException("待清除的安装目录不能为空。", nameof(installRoot));
            }

            string normalizedRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(installRoot.Trim()))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            bool cleared = false;
            UpdateConfig(delegate(ManagerConfig config)
            {
                if (!PathsEqual(config.InstallRoot, normalizedRoot))
                {
                    return false;
                }

                config.InstallRoot = null;
                cleared = true;
                return true;
            });
            return cleared;
        }

        private static void UpdateConfig(Func<ManagerConfig, bool> update)
        {
            if (update == null) throw new ArgumentNullException(nameof(update));
            ExecuteWithStorageLock(ConfigPath, delegate
            {
                ManagerConfig config = LoadConfigCore() ?? new ManagerConfig();
                if (!update(config))
                {
                    return;
                }

                string json = new JavaScriptSerializer().Serialize(config);
                WriteAllTextAtomicallyUnderLock(ConfigPath, json);
            });
        }

        public static async Task MigrateLegacyCacheAsync(IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        {
            string legacyRoot = Path.Combine(
                GetLocalApplicationDataRoot(),
                "CodexPortableManager");
            string legacyCache = Path.Combine(legacyRoot, "cache");
            if (!Directory.Exists(legacyCache))
            {
                return;
            }

            ValidateMigrationPaths(legacyRoot, legacyCache, CacheRoot, AppDomain.CurrentDomain.BaseDirectory);
            string[] files = Directory.GetFiles(legacyCache, "*", SearchOption.TopDirectoryOnly);
            Directory.CreateDirectory(CacheRoot);
            long totalBytes = files.Sum(path => new FileInfo(path).Length);
            long copiedBytes = 0;
            int lastReportedPercent = -1;

            foreach (string sourcePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sourceIdentity = NativeFileSystem.GetPersistentFileIdentity(sourcePath);
                string destinationPath = Path.Combine(CacheRoot, Path.GetFileName(sourcePath));
                long sourceLength = new FileInfo(sourcePath).Length;
                if (File.Exists(destinationPath))
                {
                    if (!await FilesAreIdenticalAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false))
                    {
                        throw new IOException(string.Format(
                            "旧缓存迁移冲突：源文件“{0}”与目标文件“{1}”同名但大小或 SHA-256 不一致。为避免数据丢失，两个文件均已保留，请手动确认后重试。",
                            sourcePath,
                            destinationPath));
                    }

                    // 目标文件与源文件完全一致后，才删除旧缓存中的源文件。
                    NativeFileSystem.DeleteFile(sourcePath, sourceIdentity);
                    copiedBytes += sourceLength;
                    ReportMigrationProgress(progress, copiedBytes, totalBytes, ref lastReportedPercent);
                    continue;
                }

                string temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".migrating";
                try
                {
                    if (progress != null)
                    {
                        progress.Report(new OperationProgress("正在迁移旧版管理器缓存。", totalBytes == 0 ? 0 : (int)(copiedBytes * 100L / totalBytes)));
                    }

                    byte[] sourceHash;
                    using (FileStream input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
                    using (FileStream output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                    using (SHA256 sha256 = SHA256.Create())
                    {
                        byte[] buffer = new byte[1024 * 1024];
                        int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                            sha256.TransformBlock(buffer, 0, read, buffer, 0);
                            copiedBytes += read;
                            ReportMigrationProgress(progress, copiedBytes, totalBytes, ref lastReportedPercent);
                        }

                        sha256.TransformFinalBlock(new byte[0], 0, 0);
                        sourceHash = sha256.Hash;
                        output.Flush(true);
                    }

                    long temporaryLength = new FileInfo(temporaryPath).Length;
                    byte[] temporaryHash = await ComputeSha256Async(temporaryPath, cancellationToken).ConfigureAwait(false);
                    if (temporaryLength != sourceLength || !HashesEqual(sourceHash, temporaryHash))
                    {
                        throw new IOException(string.Format(
                            "旧缓存迁移校验失败：源文件“{0}”与复制结果“{1}”的大小或 SHA-256 不一致，源文件已保留。",
                            sourcePath,
                            temporaryPath));
                    }

                    try
                    {
                        File.Move(temporaryPath, destinationPath);
                    }
                    catch (IOException)
                    {
                        // 另一进程可能刚刚发布了同名文件；此时重新校验，而不是覆盖它。
                        if (!File.Exists(destinationPath))
                        {
                            throw;
                        }

                        if (!await FilesAreIdenticalAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false))
                        {
                            throw new IOException(string.Format(
                                "旧缓存迁移冲突：目标文件“{0}”在迁移期间被创建，且与源文件“{1}”的大小或 SHA-256 不一致。两个文件均已保留。",
                                destinationPath,
                                sourcePath));
                        }
                    }

                    if (!await FilesAreIdenticalAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false))
                    {
                        throw new IOException(string.Format(
                            "旧缓存迁移校验失败：已发布的目标文件“{0}”与源文件“{1}”不一致，源文件已保留。",
                            destinationPath,
                            sourcePath));
                    }

                    NativeFileSystem.DeleteFile(sourcePath, sourceIdentity);
                }
                finally
                {
                    // GUID 临时文件只属于当前迁移；失败或取消时可以安全清理。
                    if (File.Exists(temporaryPath))
                    {
                        NativeFileSystem.DeleteFile(temporaryPath);
                    }
                }
            }

            DeleteDirectoryIfEmpty(legacyCache);
            DeleteDirectoryIfEmpty(legacyRoot);
            if (progress != null)
            {
                progress.Report(new OperationProgress("旧版管理器缓存已迁移到软件目录。", 100));
            }
        }

        private static ManagerConfig LoadConfigCore()
        {
            if (!File.Exists(ConfigPath))
            {
                return null;
            }

            try
            {
                string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                return new JavaScriptSerializer().Deserialize<ManagerConfig>(json);
            }
            catch (Exception ex)
            {
                RecordLoadFailure(ConfigPath, ex);
                return null;
            }
        }

        private static void WriteAllTextAtomically(string destinationPath, string contents)
        {
            ExecuteWithStorageLock(destinationPath, delegate
            {
                WriteAllTextAtomicallyUnderLock(destinationPath, contents);
            });
        }

        private static void ExecuteWithStorageLock(string destinationPath, Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            string directoryPath = Path.GetDirectoryName(destinationPath);
            Directory.CreateDirectory(directoryPath);
            string key = "storage-path|" + CrossProcessFileLock.NormalizePathKey(destinationPath);
            using (FileStream storageLock = CrossProcessFileLock.Acquire(
                "storage",
                key,
                TimeSpan.FromSeconds(30),
                value => value,
                "等待用户状态写入锁超时：" + destinationPath))
            {
                action();
            }
        }

        private static void WriteAllTextAtomicallyUnderLock(string destinationPath, string contents)
        {
            string temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(contents);
                    writer.Flush();
                    stream.Flush(true);
                }

                ReplaceFileAtomically(temporaryPath, destinationPath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    NativeFileSystem.DeleteFile(temporaryPath);
                }
            }
        }

        private static void ReplaceFileAtomically(string temporaryPath, string destinationPath)
        {
            IOException lastError = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (File.Exists(destinationPath))
                    {
                        File.Replace(temporaryPath, destinationPath, null, true);
                    }
                    else
                    {
                        File.Move(temporaryPath, destinationPath);
                    }
                    return;
                }
                catch (IOException ex)
                {
                    lastError = ex;
                    if (!File.Exists(temporaryPath))
                    {
                        throw new IOException(
                            "原子替换返回错误且临时文件已消失，保存结果无法确认：" + destinationPath,
                            ex);
                    }
                    Thread.Sleep(10 * (attempt + 1));
                }
            }

            throw new IOException("无法以原子方式保存配置文件，另一个进程可能正在同时写入：" + destinationPath, lastError);
        }

        private static void ValidateMigrationPaths(string legacyRoot, string legacyCache, string destinationCache, string managerRoot)
        {
            string normalizedLegacyRoot = NormalizeDirectoryPath(legacyRoot);
            string normalizedLegacyCache = NormalizeDirectoryPath(legacyCache);
            string normalizedDestinationCache = NormalizeDirectoryPath(destinationCache);
            string normalizedManagerRoot = NormalizeDirectoryPath(managerRoot);

            if (PathsOverlap(normalizedLegacyRoot, normalizedManagerRoot))
            {
                throw new InvalidOperationException(string.Format(
                    "旧缓存目录与管理器目录重叠，已停止迁移以避免误删文件。旧目录：{0}；管理器目录：{1}",
                    normalizedLegacyRoot,
                    normalizedManagerRoot));
            }

            if (PathsOverlap(normalizedLegacyCache, normalizedDestinationCache))
            {
                throw new InvalidOperationException(string.Format(
                    "旧缓存源目录与目标目录重叠，已停止迁移。源目录：{0}；目标目录：{1}",
                    normalizedLegacyCache,
                    normalizedDestinationCache));
            }

            if (!IsSameOrChildPath(normalizedDestinationCache, normalizedManagerRoot) ||
                string.Equals(normalizedDestinationCache, normalizedManagerRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(string.Format(
                    "缓存目标目录必须位于管理器目录内部。目标目录：{0}；管理器目录：{1}",
                    normalizedDestinationCache,
                    normalizedManagerRoot));
            }

            EnsureDirectoryIsNotReparsePoint(normalizedLegacyRoot, "旧缓存根目录");
            EnsureDirectoryIsNotReparsePoint(normalizedLegacyCache, "旧缓存目录");
            EnsureDirectoryIsNotReparsePoint(normalizedManagerRoot, "管理器目录");
            EnsureDirectoryIsNotReparsePoint(Path.GetDirectoryName(normalizedDestinationCache), "管理器数据目录");
            EnsureDirectoryIsNotReparsePoint(normalizedDestinationCache, "缓存目标目录");
        }

        private static void EnsureDirectoryIsNotReparsePoint(string path, string description)
        {
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(string.Format(
                    "{0}不能是 junction、符号链接或其他重解析点，已停止迁移以避免越界读写或删除。路径：{1}",
                    description,
                    path));
            }
        }

        private static string NormalizeDirectoryPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool PathsOverlap(string firstPath, string secondPath)
        {
            return IsSameOrChildPath(firstPath, secondPath) || IsSameOrChildPath(secondPath, firstPath);
        }

        private static bool IsSameOrChildPath(string candidatePath, string parentPath)
        {
            if (string.Equals(candidatePath, parentPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string parentWithSeparator = parentPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? parentPath
                : parentPath + Path.DirectorySeparatorChar;
            return candidatePath.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<bool> FilesAreIdenticalAsync(string firstPath, string secondPath, CancellationToken cancellationToken)
        {
            FileInfo first = new FileInfo(firstPath);
            FileInfo second = new FileInfo(secondPath);
            if (first.Length != second.Length)
            {
                return false;
            }

            byte[] firstHash = await ComputeSha256Async(firstPath, cancellationToken).ConfigureAwait(false);
            byte[] secondHash = await ComputeSha256Async(secondPath, cancellationToken).ConfigureAwait(false);
            return HashesEqual(firstHash, secondHash);
        }

        private static async Task<byte[]> ComputeSha256Async(string path, CancellationToken cancellationToken)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] buffer = new byte[1024 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    sha256.TransformBlock(buffer, 0, read, buffer, 0);
                }
                sha256.TransformFinalBlock(new byte[0], 0, 0);
                return sha256.Hash;
            }
        }

        private static bool HashesEqual(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length)
            {
                return false;
            }

            int difference = 0;
            for (int index = 0; index < first.Length; index++)
            {
                difference |= first[index] ^ second[index];
            }
            return difference == 0;
        }

        private static void ReportMigrationProgress(IProgress<OperationProgress> progress, long completedBytes, long totalBytes, ref int lastReportedPercent)
        {
            int percent = totalBytes == 0 ? 100 : (int)Math.Min(100, completedBytes * 100L / totalBytes);
            if (progress != null && percent != lastReportedPercent)
            {
                lastReportedPercent = percent;
                progress.Report(new OperationProgress("正在迁移旧版管理器缓存。", percent));
            }
        }

        private static void DeleteDirectoryIfEmpty(string path)
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                NativeFileSystem.DeleteEmptyDirectory(path);
            }
        }

        private static void RecordLoadFailure(string sourcePath, Exception exception)
        {
            try
            {
                Directory.CreateDirectory(LogsRoot);
                string logPath = Path.Combine(
                    LogsRoot,
                    "storage-load-error-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".log");
                string message = string.Format(
                    "时间（UTC）：{0:O}{1}读取文件：{2}{1}异常：{3}{1}",
                    DateTime.UtcNow,
                    Environment.NewLine,
                    sourcePath,
                    exception);
                File.WriteAllText(logPath, message, new UTF8Encoding(false));
            }
            catch
            {
                // 诊断日志写入失败不能影响启动；原配置文件始终保持不变。
            }
        }

        private static bool PathsEqual(string first, string second)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(first) &&
                    !string.IsNullOrWhiteSpace(second) &&
                    string.Equals(
                        Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private sealed class ManagerConfig
        {
            public string InstallRoot { get; set; }
        }

        internal static string GetLocalApplicationDataRoot()
        {
            // 直接读取 LOCALAPPDATA，使独立测试进程、企业重定向和显式环境覆盖按
            // Windows 通常语义生效；未设置时再回退到系统特殊目录 API。
            string localData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(localData))
            {
                localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            if (string.IsNullOrWhiteSpace(localData))
            {
                throw new InvalidOperationException("无法确定当前用户的 LocalAppData 目录。");
            }
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(localData));
        }

        internal static string GetUserDataRoot(string dataRoot, string userStorageKey)
        {
            if (string.IsNullOrWhiteSpace(dataRoot))
            {
                throw new ArgumentException("共享数据目录不能为空。", nameof(dataRoot));
            }
            if (string.IsNullOrWhiteSpace(userStorageKey) ||
                userStorageKey.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                userStorageKey.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                userStorageKey.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                throw new ArgumentException("用户存储键不能用于目录名称。", nameof(userStorageKey));
            }

            return Path.Combine(Path.GetFullPath(dataRoot), "users", userStorageKey);
        }

        private static string GetCurrentUserStorageKey()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query))
                {
                    if (identity != null && identity.User != null && !string.IsNullOrWhiteSpace(identity.User.Value))
                    {
                        return identity.User.Value;
                    }
                }
            }
            catch
            {
                // 极少数受限 token 无法读取 SID 时，使用域和用户名的稳定摘要隔离状态。
            }

            string account = (Environment.UserDomainName ?? string.Empty) + "\\" + (Environment.UserName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(account.Trim('\\')))
            {
                throw new InvalidOperationException("无法确定当前用户的 SID 或账户名，已拒绝使用未分区状态目录。");
            }
            return "account-" + ComputeStableKey(account);
        }

        private static string ComputeStableKey(string value)
        {
            byte[] digest;
            using (SHA256 sha256 = SHA256.Create())
            {
                digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
            }
            StringBuilder builder = new StringBuilder(digest.Length * 2);
            for (int index = 0; index < digest.Length; index++)
            {
                builder.Append(digest[index].ToString("x2"));
            }
            return builder.ToString();
        }
    }
}
