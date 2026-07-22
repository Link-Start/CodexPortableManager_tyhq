using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPortableManager
{
    /// <summary>
    /// 对单个共享缓存目标提供跨进程、跨登录会话和跨账户的文件锁。
    /// 下载、摘要校验、缓存发布和建立已验证制品租约在同一把锁的生命周期内完成。
    /// </summary>
    internal sealed class CacheFileLock : IDisposable
    {
        // 大型程序包下载和可信校验可能较慢；租约建立后应尽快释放缓存发布锁，
        // 解包由租约继续保持源文件不可写、不可删除。
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);
        private FileStream stream;

        private CacheFileLock(FileStream lockStream)
        {
            stream = lockStream;
        }

        public static Task<CacheFileLock> AcquireAsync(string targetCachePath, CancellationToken cancellationToken)
        {
            string key = CreatePathKey(targetCachePath);
            return CrossProcessFileLock.AcquireAsync(
                "cache",
                key,
                DefaultTimeout,
                cancellationToken,
                value => new CacheFileLock(value),
                "另一个 Codex Portable Manager 操作正在使用该缓存文件：" + targetCachePath);
        }

        public static CacheFileLock Acquire(string targetCachePath)
        {
            string key = CreatePathKey(targetCachePath);
            return CrossProcessFileLock.Acquire(
                "cache",
                key,
                DefaultTimeout,
                value => new CacheFileLock(value),
                "另一个 Codex Portable Manager 操作正在使用该缓存文件：" + targetCachePath);
        }

        public static CacheFileLock TryAcquire(string targetCachePath)
        {
            string key = CreatePathKey(targetCachePath);
            return CrossProcessFileLock.TryAcquire(
                "cache",
                key,
                value => new CacheFileLock(value));
        }

        public static Task<CacheFileLock> AcquirePackageAsync(
            string cacheRoot,
            string packageName,
            string packageVersion,
            string architecture,
            CancellationToken cancellationToken)
        {
            return AcquireAsync(GetPackagePath(cacheRoot, packageName, packageVersion, architecture), cancellationToken);
        }

        public static CacheFileLock AcquirePackage(
            string cacheRoot,
            string packageName,
            string packageVersion,
            string architecture)
        {
            return Acquire(GetPackagePath(cacheRoot, packageName, packageVersion, architecture));
        }

        public static string GetPackagePath(
            string cacheRoot,
            string packageName,
            string packageVersion,
            string architecture)
        {
            if (string.IsNullOrWhiteSpace(cacheRoot))
            {
                throw new ArgumentException("缓存目录不能为空。", nameof(cacheRoot));
            }
            if (string.IsNullOrWhiteSpace(packageName) ||
                packageName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("程序包名称不能用于缓存文件名。", nameof(packageName));
            }

            Version parsedVersion;
            if (!Version.TryParse(packageVersion, out parsedVersion))
            {
                throw new ArgumentException("程序包版本格式无效。", nameof(packageVersion));
            }

            string normalizedArchitecture = (architecture ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.Equals(normalizedArchitecture, "x64", StringComparison.Ordinal) &&
                !string.Equals(normalizedArchitecture, "arm64", StringComparison.Ordinal))
            {
                throw new ArgumentException("程序包架构必须是 x64 或 arm64。", nameof(architecture));
            }

            return Path.Combine(
                Path.GetFullPath(cacheRoot),
                packageName + "_" + parsedVersion + "_" + normalizedArchitecture + ".msix");
        }

        public void Dispose()
        {
            FileStream current = Interlocked.Exchange(ref stream, null);
            if (current != null)
            {
                current.Dispose();
            }
        }

        private static string CreatePathKey(string targetCachePath)
        {
            return "cache-path|" + CrossProcessFileLock.NormalizePathKey(targetCachePath);
        }
    }
}
