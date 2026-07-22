using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace CodexPortableManager
{
    internal sealed class OperationFileLock : IDisposable
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
        private OperationMutexCoordinator coordinator;

        private OperationFileLock(OperationMutexCoordinator value)
        {
            coordinator = value;
        }

        public static Task<OperationFileLock> AcquireAsync(string installRoot, CancellationToken cancellationToken)
        {
            return OperationMutexCoordinator.Start(
                OperationLockIdentity.GetKeys(installRoot),
                installRoot,
                DefaultTimeout,
                cancellationToken);
        }

        public static OperationFileLock Acquire(string installRoot)
        {
            return AcquireAsync(installRoot, CancellationToken.None).GetAwaiter().GetResult();
        }

        internal static Task<OperationFileLock> AcquireResourceAsync(
            string resourceKey,
            string displayName,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(resourceKey))
            {
                throw new ArgumentException("全局资源锁标识不能为空。", nameof(resourceKey));
            }

            string normalizedKey = resourceKey.Trim();
            string normalizedDisplayName = string.IsNullOrWhiteSpace(displayName)
                ? normalizedKey
                : displayName.Trim();
            return OperationMutexCoordinator.Start(
                new[] { "resource|" + normalizedKey },
                normalizedDisplayName,
                DefaultTimeout,
                cancellationToken);
        }

        internal static OperationFileLock AcquireResource(string resourceKey, string displayName)
        {
            return AcquireResourceAsync(resourceKey, displayName, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        public void Dispose()
        {
            OperationMutexCoordinator current = Interlocked.Exchange(ref coordinator, null);
            if (current != null)
            {
                current.Release();
            }
            GC.SuppressFinalize(this);
        }

        ~OperationFileLock()
        {
            Dispose();
        }

        private sealed class OperationMutexCoordinator
        {
            private const int RetryDelayMilliseconds = 150;
            private const string MutexNamePrefix = @"Global\OpenAI.CodexPortableManager.Operation.";

            private readonly string[] keys;
            private readonly string installRoot;
            private readonly TimeSpan timeout;
            private readonly CancellationToken cancellationToken;
            private readonly ManualResetEvent releaseRequested = new ManualResetEvent(false);
            private TaskCompletionSource<OperationFileLock> completionSource;
            private int releaseSignaled;

            private OperationMutexCoordinator(
                string[] operationKeys,
                string displayInstallRoot,
                TimeSpan waitTimeout,
                CancellationToken token)
            {
                keys = operationKeys;
                installRoot = displayInstallRoot;
                timeout = waitTimeout;
                cancellationToken = token;
                completionSource = new TaskCompletionSource<OperationFileLock>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public static Task<OperationFileLock> Start(
                string[] keys,
                string installRoot,
                TimeSpan timeout,
                CancellationToken cancellationToken)
            {
                OperationMutexCoordinator coordinator = new OperationMutexCoordinator(
                    keys,
                    installRoot,
                    timeout,
                    cancellationToken);
                Task<OperationFileLock> completionTask = coordinator.completionSource.Task;
                Thread worker = new Thread(coordinator.Run)
                {
                    IsBackground = true,
                    Name = "Codex Portable Manager 操作锁"
                };
                worker.Start();
                return completionTask;
            }

            public void Release()
            {
                if (Interlocked.Exchange(ref releaseSignaled, 1) == 0)
                {
                    releaseRequested.Set();
                }
            }

            private void Run()
            {
                List<Mutex> opened = new List<Mutex>();
                List<Mutex> acquired = new List<Mutex>();
                TaskCompletionSource<OperationFileLock> completion = completionSource;
                try
                {
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    foreach (string key in keys)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Mutex mutex = OpenGlobalMutex(key);
                        opened.Add(mutex);

                        while (!TryWait(mutex, RetryDelayMilliseconds))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (stopwatch.Elapsed >= timeout)
                            {
                                throw new IOException(
                                    "另一个 Codex Portable Manager 操作正在使用该安装目录：" + installRoot);
                            }
                        }
                        acquired.Add(mutex);
                    }

                    completion.TrySetResult(new OperationFileLock(this));
                    completionSource = null;
                    completion = null;
                    releaseRequested.WaitOne();
                }
                catch (OperationCanceledException)
                {
                    if (completion != null)
                    {
                        completion.TrySetCanceled();
                    }
                }
                catch (Exception exception)
                {
                    if (completion != null)
                    {
                        completion.TrySetException(exception);
                    }
                }
                finally
                {
                    for (int index = acquired.Count - 1; index >= 0; index--)
                    {
                        try
                        {
                            acquired[index].ReleaseMutex();
                        }
                        catch (ApplicationException)
                        {
                            // 仅在异常清理中容忍已失去所有权；正常路径始终由本线程释放。
                        }
                    }
                    foreach (Mutex mutex in opened)
                    {
                        mutex.Dispose();
                    }
                    releaseRequested.Dispose();
                }
            }

            private static bool TryWait(Mutex mutex, int milliseconds)
            {
                try
                {
                    return mutex.WaitOne(milliseconds);
                }
                catch (AbandonedMutexException)
                {
                    return true;
                }
            }

            private static Mutex OpenGlobalMutex(string key)
            {
                string name = MutexNamePrefix + CrossProcessFileLock.ComputeKeyHash("operation|" + key);
                MutexSecurity security = new MutexSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new MutexAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    MutexRights.Synchronize | MutexRights.Modify,
                    AccessControlType.Allow));

                while (true)
                {
                    try
                    {
                        bool createdNew;
                        return new Mutex(false, name, out createdNew, security);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        try
                        {
                            return Mutex.OpenExisting(
                                name,
                                MutexRights.Synchronize | MutexRights.Modify);
                        }
                        catch (WaitHandleCannotBeOpenedException)
                        {
                            Thread.Yield();
                        }
                    }
                }
            }
        }
    }

    internal static class OperationLockIdentity
    {
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint VolumeNameNt = 0x00000002;

        public static string[] GetKeys(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("锁定路径不能为空。", nameof(path));
            }

            string fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
            string root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                throw new IOException("无法确定安装目录所在的文件系统根路径：" + fullPath);
            }

            while (fullPath.Length > root.Length &&
                (fullPath[fullPath.Length - 1] == Path.DirectorySeparatorChar ||
                 fullPath[fullPath.Length - 1] == Path.AltDirectorySeparatorChar))
            {
                fullPath = fullPath.Substring(0, fullPath.Length - 1);
            }

            string relativePath = fullPath.Substring(root.Length);
            string[] components = relativePath.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);

            // 同时锁定目标对象身份，以及每个现有祖先身份与剩余相对路径的组合。
            // 前者合并 junction 等别名，后者保证首次安装期间目录从不存在变为存在时锁键不漂移。
            for (int prefixLength = components.Length; prefixLength >= 0; prefixLength--)
            {
                string prefixPath = CombineComponents(root, components, prefixLength);
                string identity;
                if (!TryGetIdentity(prefixPath, out identity))
                {
                    continue;
                }

                string remainingPath = JoinComponents(components, prefixLength);
                keys.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "entry|{0}|{1}",
                    identity,
                    remainingPath.ToUpperInvariant()));
            }

            if (keys.Count == 0)
            {
                throw new IOException("无法读取安装目录或其任何现有祖先的文件系统身份：" + fullPath);
            }

            string[] result = new string[keys.Count];
            keys.CopyTo(result);
            Array.Sort(result, StringComparer.Ordinal);
            return result;
        }

        private static string CombineComponents(string root, string[] components, int count)
        {
            string result = root;
            for (int index = 0; index < count; index++)
            {
                result = Path.Combine(result, components[index]);
            }
            return result;
        }

        private static string JoinComponents(string[] components, int startIndex)
        {
            if (startIndex >= components.Length)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder();
            for (int index = startIndex; index < components.Length; index++)
            {
                if (builder.Length > 0)
                {
                    builder.Append(Path.DirectorySeparatorChar);
                }
                builder.Append(components[index]);
            }
            return builder.ToString();
        }

        private static bool TryGetIdentity(string path, out string identity)
        {
            identity = null;
            using (SafeFileHandle handle = CreateFile(
                ToExtendedPath(path),
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    return false;
                }

                FileIdInfo fileId;
                if (GetFileInformationByHandleEx(
                    handle,
                    FileInfoByHandleClass.FileIdInfo,
                    out fileId,
                    Marshal.SizeOf(typeof(FileIdInfo))))
                {
                    identity = string.Format(
                        CultureInfo.InvariantCulture,
                        "file-id|{0:x16}|{1:x16}{2:x16}",
                        fileId.VolumeSerialNumber,
                        fileId.FileIdHigh,
                        fileId.FileIdLow);
                    return true;
                }

                identity = "final-path|" + GetFinalNtPath(handle).ToUpperInvariant();
                return true;
            }
        }

        private static string GetFinalNtPath(SafeFileHandle handle)
        {
            StringBuilder path = new StringBuilder(512);
            uint length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, VolumeNameNt);
            if (length == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取安装目录祖先的最终句柄路径。");
            }
            if (length >= path.Capacity)
            {
                path = new StringBuilder(checked((int)length + 1));
                length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, VolumeNameNt);
                if (length == 0 || length >= path.Capacity)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取安装目录祖先的最终句柄路径。");
                }
            }
            return path.ToString();
        }

        private static string ToExtendedPath(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return path;
            }
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return @"\\?\UNC\" + path.Substring(2);
            }
            return @"\\?\" + path;
        }

        private enum FileInfoByHandleClass
        {
            FileIdInfo = 18
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdInfo
        {
            public ulong VolumeSerialNumber;
            public ulong FileIdLow;
            public ulong FileIdHigh;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            FileInfoByHandleClass fileInformationClass,
            out FileIdInfo fileInformation,
            int bufferSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);
    }

    /// <summary>
    /// 在便携数据目录的共享 locks 子目录中保存持久锁文件。锁路径与共享缓存具有相同的
    /// 账户可见范围，Windows 文件共享锁因此可以覆盖跨进程、跨会话和跨账户访问。
    /// </summary>
    internal static class CrossProcessFileLock
    {
        private const int RetryDelayMilliseconds = 150;

        public static async Task<T> AcquireAsync<T>(
            string category,
            string key,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Func<FileStream, T> factory,
            string timeoutMessage)
            where T : class
        {
            string path = GetLockPath(category, key);
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileStream acquired = TryAcquire(path);
                if (acquired != null)
                {
                    try
                    {
                        return factory(acquired);
                    }
                    catch
                    {
                        acquired.Dispose();
                        throw;
                    }
                }

                if (stopwatch.Elapsed >= timeout)
                {
                    throw new IOException(timeoutMessage);
                }
                await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        public static T Acquire<T>(
            string category,
            string key,
            TimeSpan timeout,
            Func<FileStream, T> factory,
            string timeoutMessage)
            where T : class
        {
            string path = GetLockPath(category, key);
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (true)
            {
                FileStream acquired = TryAcquire(path);
                if (acquired != null)
                {
                    try
                    {
                        return factory(acquired);
                    }
                    catch
                    {
                        acquired.Dispose();
                        throw;
                    }
                }

                if (stopwatch.Elapsed >= timeout)
                {
                    throw new IOException(timeoutMessage);
                }
                Thread.Sleep(RetryDelayMilliseconds);
            }
        }

        public static T TryAcquire<T>(string category, string key, Func<FileStream, T> factory)
            where T : class
        {
            string path = GetLockPath(category, key);
            FileStream acquired = TryAcquire(path);
            if (acquired == null)
            {
                return null;
            }

            try
            {
                return factory(acquired);
            }
            catch
            {
                acquired.Dispose();
                throw;
            }
        }

        public static string NormalizePathKey(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("锁定路径不能为空。", nameof(path));
            }

            string fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            string root = Path.GetPathRoot(fullPath);
            while (fullPath.Length > root.Length &&
                (fullPath[fullPath.Length - 1] == Path.DirectorySeparatorChar ||
                 fullPath[fullPath.Length - 1] == Path.AltDirectorySeparatorChar))
            {
                fullPath = fullPath.Substring(0, fullPath.Length - 1);
            }
            return fullPath.ToUpperInvariant();
        }

        public static string ComputeKeyHash(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("锁键不能为空。", nameof(key));
            }

            byte[] digest;
            using (SHA256 sha256 = SHA256.Create())
            {
                digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));
            }

            StringBuilder builder = new StringBuilder(digest.Length * 2);
            for (int index = 0; index < digest.Length; index++)
            {
                builder.Append(digest[index].ToString("x2"));
            }
            return builder.ToString();
        }

        private static string GetLockPath(string category, string key)
        {
            ValidateCategory(category);
            string managerRoot = PortableStorage.DataRoot;
            string lockRoot = PortableStorage.SharedLocksRoot;
            string categoryRoot = Path.Combine(lockRoot, category);
            EnsureOrdinaryDirectory(managerRoot);
            EnsureOrdinaryDirectory(lockRoot);
            EnsureOrdinaryDirectory(categoryRoot);
            return Path.Combine(categoryRoot, ComputeKeyHash(key) + ".lock");
        }

        private static void EnsureOrdinaryDirectory(string path)
        {
            Directory.CreateDirectory(path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("锁目录不能是 junction、符号链接或其他重解析点：" + path);
            }
        }

        private static void ValidateCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category) ||
                category.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                category.IndexOf(Path.AltDirectorySeparatorChar) >= 0 ||
                category.IndexOf(':') >= 0)
            {
                throw new ArgumentException("锁分类名称无效。", nameof(category));
            }
        }

        private static FileStream TryAcquire(string path)
        {
            try
            {
                // CreateNew 只会创建属于本工具的空锁文件；已存在时改为只读独占打开，
                // 绝不调用 SetLength、Write 或 Delete，避免破坏未知文件或硬链接目标。
                return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException)
            {
                try
                {
                    return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.None);
                }
                catch (IOException)
                {
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
