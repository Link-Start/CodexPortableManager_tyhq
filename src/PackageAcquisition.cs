using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CodexPortableManager
{
    internal enum PackageAcquisitionMode
    {
        Cached,
        Incremental,
        FullDownload
    }

    internal sealed class PackageAcquisitionResult
    {
        internal PackageAcquisitionResult(
            PackageAcquisitionMode mode,
            string sha256Base64,
            long targetBytes,
            long reusedBytes,
            long remoteBytes,
            int rangeRequestCount,
            string fallbackReason,
            TimeSpan elapsed,
            DownloadedPackageLease downloadedPackage = null)
        {
            Mode = mode;
            Sha256Base64 = sha256Base64;
            TargetBytes = targetBytes;
            ReusedBytes = reusedBytes;
            RemoteBytes = remoteBytes;
            RangeRequestCount = rangeRequestCount;
            FallbackReason = fallbackReason;
            Elapsed = elapsed;
            DownloadedPackage = downloadedPackage;
        }

        internal PackageAcquisitionMode Mode { get; private set; }
        internal string Sha256Base64 { get; private set; }
        internal long TargetBytes { get; private set; }
        internal long ReusedBytes { get; private set; }
        internal long RemoteBytes { get; private set; }
        internal int RangeRequestCount { get; private set; }
        internal string FallbackReason { get; private set; }
        internal TimeSpan Elapsed { get; private set; }
        internal DownloadedPackageLease DownloadedPackage { get; private set; }

        internal DownloadedPackageLease DetachDownloadedPackage()
        {
            DownloadedPackageLease result = DownloadedPackage;
            DownloadedPackage = null;
            return result;
        }
    }

    internal sealed class DownloadedPackageLease : IDisposable
    {
        private FileStream stream;

        internal DownloadedPackageLease(string sha256Base64, FileStream lockedStream)
        {
            Sha256Base64 = sha256Base64;
            stream = lockedStream ?? throw new ArgumentNullException(nameof(lockedStream));
        }

        internal string Sha256Base64 { get; private set; }

        internal FileStream DetachStream()
        {
            FileStream result = stream;
            stream = null;
            return result;
        }

        public void Dispose()
        {
            FileStream value = stream;
            stream = null;
            if (value != null) value.Dispose();
        }
    }

    internal sealed class PackageCacheCandidate
    {
        internal string Path { get; set; }
        internal Version Version { get; set; }
        internal long Length { get; set; }
        internal string Architecture { get; set; }
    }

    internal enum RollbackTargetKind
    {
        PreviousDirectory,
        CachedPackage
    }

    internal sealed class RollbackPackageTarget
    {
        internal RollbackTargetKind Kind { get; set; }
        internal Version Version { get; set; }
        internal string Path { get; set; }
        internal string Architecture { get; set; }
    }

    internal sealed class IncrementalCandidatePlan
    {
        internal IncrementalCandidatePlan(PackageCacheCandidate candidate, PackageReusePlan plan)
        {
            Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        }

        internal PackageCacheCandidate Candidate { get; private set; }
        internal PackageReusePlan Plan { get; private set; }
    }

    internal static class PackageCacheSelector
    {
        internal const int MaximumCandidateCount = 4;

        internal static IList<PackageCacheCandidate> FindPreviousCandidates(
            string cacheRoot,
            PackageMetadata target,
            string targetPackagePath)
        {
            if (string.IsNullOrWhiteSpace(cacheRoot) || target == null ||
                string.IsNullOrWhiteSpace(target.packageName) || string.IsNullOrWhiteSpace(target.version))
            {
                return new List<PackageCacheCandidate>().AsReadOnly();
            }
            string root = Path.GetFullPath(cacheRoot);
            if (!Directory.Exists(root)) return new List<PackageCacheCandidate>().AsReadOnly();
            Version targetVersion;
            if (!Version.TryParse(target.version, out targetVersion))
            {
                return new List<PackageCacheCandidate>().AsReadOnly();
            }
            string architecture = (target.architecture ?? string.Empty).Trim().ToLowerInvariant();
            string pattern = "^" + Regex.Escape(target.packageName) +
                @"_(?<version>[0-9]+(?:\.[0-9]+){1,3})_" + Regex.Escape(architecture) + @"\.msix$";
            Regex fileNamePattern = new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            string excludedPath = string.IsNullOrWhiteSpace(targetPackagePath)
                ? null
                : Path.GetFullPath(targetPackagePath);
            List<PackageCacheCandidate> candidates = new List<PackageCacheCandidate>();
            string[] paths;
            try
            {
                paths = Directory.EnumerateFiles(root, "*.msix", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                return new List<PackageCacheCandidate>().AsReadOnly();
            }
            foreach (string path in paths)
            {
                string fullPath = Path.GetFullPath(path);
                if (excludedPath != null && string.Equals(fullPath, excludedPath, StringComparison.OrdinalIgnoreCase)) continue;
                Match match = fileNamePattern.Match(Path.GetFileName(fullPath));
                Version version;
                if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out version) ||
                    version >= targetVersion)
                {
                    continue;
                }
                try
                {
                    FileInfo file = new FileInfo(fullPath);
                    if (!file.Exists || file.Length <= 0 || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    candidates.Add(new PackageCacheCandidate
                    {
                        Path = fullPath,
                        Version = version,
                        Length = file.Length,
                        Architecture = architecture
                    });
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                }
            }
            return candidates
                .OrderByDescending(value => value.Version)
                .ThenByDescending(value => value.Length)
                .Take(MaximumCandidateCount)
                .ToList()
                .AsReadOnly();
        }
    }

    internal static class RollbackPackageSelector
    {
        internal static RollbackPackageTarget Select(
            string cacheRoot,
            Version currentVersion,
            Version previousVersion,
            string architecture)
        {
            if (currentVersion == null) return null;

            if (previousVersion != null && previousVersion < currentVersion)
            {
                return new RollbackPackageTarget
                {
                    Kind = RollbackTargetKind.PreviousDirectory,
                    Version = previousVersion
                };
            }

            string currentVersionText;
            try { currentVersionText = currentVersion.ToString(4); }
            catch (ArgumentException) { currentVersionText = null; }
            if (!string.IsNullOrWhiteSpace(currentVersionText))
            {
                PackageMetadata target = new PackageMetadata
                {
                    packageName = CodexMicrosoftStoreSource.PackageName,
                    version = currentVersionText,
                    architecture = architecture
                };
                PackageCacheCandidate candidate = PackageCacheSelector
                    .FindPreviousCandidates(cacheRoot, target, null)
                    .FirstOrDefault();
                if (candidate != null)
                {
                    return new RollbackPackageTarget
                    {
                        Kind = RollbackTargetKind.CachedPackage,
                        Version = candidate.Version,
                        Path = candidate.Path,
                        Architecture = candidate.Architecture
                    };
                }
            }

            // 保留原来的双向槽位回滚：当 .previous 是较新版本时，仍允许再次切换回它。
            if (previousVersion != null)
            {
                return new RollbackPackageTarget
                {
                    Kind = RollbackTargetKind.PreviousDirectory,
                    Version = previousVersion
                };
            }
            return null;
        }

        internal static PackageMetadata CreateLocalPackageMetadata(
            RollbackPackageTarget target)
        {
            if (target == null || target.Kind != RollbackTargetKind.CachedPackage ||
                string.IsNullOrWhiteSpace(target.Path) || target.Version == null)
            {
                throw new ArgumentException("缓存回滚目标无效。", nameof(target));
            }
            if (target.Version.Build < 0 || target.Version.Revision < 0)
            {
                throw new InvalidDataException("缓存回滚包版本不是四段版本。");
            }

            string fullPath = Path.GetFullPath(target.Path);
            FileInfo file = new FileInfo(fullPath);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new FileNotFoundException("缓存回滚包不存在或不是普通文件。", fullPath);
            }
            string architecture = (target.Architecture ?? string.Empty).Trim().ToLowerInvariant();
            if (architecture != "x64" && architecture != "arm64")
            {
                throw new InvalidDataException("缓存回滚包架构无效。");
            }

            string digest;
            using (FileStream stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan))
            using (SHA256 sha256 = SHA256.Create())
            {
                digest = Convert.ToBase64String(sha256.ComputeHash(stream));
            }

            string version = target.Version.ToString(4);
            return new PackageMetadata
            {
                packageName = CodexMicrosoftStoreSource.PackageName,
                architecture = architecture,
                version = version,
                fullName = CodexMicrosoftStoreSource.PackageName + "_" + version + "_" +
                    architecture + "__" + CodexMicrosoftStoreSource.PublisherId,
                digest = digest,
                url = string.Empty,
                sizeInBytes = file.Length,
                localCacheOnly = true
            };
        }
    }

    internal static class IncrementalAcquisitionPolicy
    {
        internal const long MinimumSavingsBytes = 64L * 1024 * 1024;
        internal const double MaximumRemoteFraction = 0.80d;

        internal static bool ShouldUse(
            PackageReusePlan plan,
            long minimumSavingsBytes,
            double maximumRemoteFraction,
            out string reason)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (minimumSavingsBytes < 0) throw new ArgumentOutOfRangeException(nameof(minimumSavingsBytes));
            if (maximumRemoteFraction <= 0 || maximumRemoteFraction > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumRemoteFraction));
            }
            long savings = checked(plan.TargetLength - plan.TargetBytes);
            if (plan.ReusedEntryCount <= 0 || savings < minimumSavingsBytes)
            {
                reason = "预计节省量低于 " +
                    (minimumSavingsBytes / 1048576d).ToString("F1", CultureInfo.InvariantCulture) + " MiB。";
                return false;
            }
            if (plan.TargetBytes > plan.TargetLength * maximumRemoteFraction)
            {
                reason = "预计目标补集超过完整包的 " +
                    (maximumRemoteFraction * 100d).ToString("F0", CultureInfo.InvariantCulture) + "%。";
                return false;
            }
            reason = null;
            return true;
        }
    }
}
