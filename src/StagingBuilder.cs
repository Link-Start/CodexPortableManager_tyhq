using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPortableManager
{
    internal sealed class StagingBuildResult : ITrustedArtifactDigestSource, IDisposable
    {
        private readonly string stagingRoot;
        private readonly Dictionary<string, StagedArtifactDigestLease> artifactDigests =
            new Dictionary<string, StagedArtifactDigestLease>(StringComparer.OrdinalIgnoreCase);
        private readonly object sync = new object();
        private bool disposed;

        internal StagingBuildResult(string root)
        {
            stagingRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        internal int ExtractedFileCount { get; set; }
        internal long ExtractedBytes { get; set; }
        internal long VerifiedBlockCount { get; set; }
        internal int FootprintFileCount { get; set; }
        internal PackageProfile Profile { get; set; }
        internal int OfficialArtifactDigestCount { get; private set; }
        internal long OfficialArtifactDigestBytes { get; private set; }
        internal int ReusedArtifactDigestCount { get; private set; }
        internal long ReusedArtifactDigestBytes { get; private set; }
        internal int ValidatedDirectoryCount { get; set; }
        internal long SkippedDirectoryProbeCount { get; set; }
        internal int WorkerCount { get; set; }

        internal void AddOfficialArtifactDigest(
            string relativePath,
            string sha256,
            long length,
            FileStream lockedFile)
        {
            if (lockedFile == null) throw new ArgumentNullException(nameof(lockedFile));
            string normalized = ArtifactProvenance.NormalizeRelativePath(relativePath);
            lock (sync)
            {
                artifactDigests.Add(normalized, new StagedArtifactDigestLease(sha256, length, lockedFile));
                OfficialArtifactDigestCount++;
                OfficialArtifactDigestBytes = checked(OfficialArtifactDigestBytes + length);
            }
        }

        internal void MergeExtractionMetrics(int files, long bytes, long blocks, int footprintFiles)
        {
            lock (sync)
            {
                ExtractedFileCount = checked(ExtractedFileCount + files);
                ExtractedBytes = checked(ExtractedBytes + bytes);
                VerifiedBlockCount = checked(VerifiedBlockCount + blocks);
                FootprintFileCount = checked(FootprintFileCount + footprintFiles);
            }
        }

        internal void ReleaseOfficialArtifactDigest(string relativePath)
        {
            ThrowIfDisposed();
            string normalized = ArtifactProvenance.NormalizeRelativePath(relativePath);
            StagedArtifactDigestLease lease;
            if (!artifactDigests.TryGetValue(normalized, out lease)) return;
            artifactDigests.Remove(normalized);
            lease.Dispose();
        }

        public bool TryGetTrustedDigest(string root, string relativePath, out string sha256)
        {
            ThrowIfDisposed();
            sha256 = null;
            string requestedRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (!string.Equals(stagingRoot, requestedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string normalized = ArtifactProvenance.NormalizeRelativePath(relativePath);
            StagedArtifactDigestLease lease;
            if (!artifactDigests.TryGetValue(normalized, out lease)) return false;
            string fullPath = ArtifactProvenance.ResolveRelativePath(stagingRoot, normalized);
            using (FileStream currentFile = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite))
            {
                if (lease.LockedFile.Length != lease.Length ||
                    currentFile.Length != lease.Length ||
                    !NativeFileSystem.ReferToSameFile(
                        lease.LockedFile.SafeFileHandle,
                        currentFile.SafeFileHandle))
                {
                    throw new InvalidDataException("staging 关键制品在摘要复用前发生身份变化：" + normalized);
                }
            }

            sha256 = lease.Sha256;
            if (!lease.Consumed)
            {
                lease.Consumed = true;
                ReusedArtifactDigestCount++;
                ReusedArtifactDigestBytes = checked(ReusedArtifactDigestBytes + lease.Length);
            }
            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (StagedArtifactDigestLease lease in artifactDigests.Values) lease.Dispose();
            artifactDigests.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(StagingBuildResult));
        }

        private sealed class StagedArtifactDigestLease : IDisposable
        {
            internal StagedArtifactDigestLease(string sha256, long length, FileStream lockedFile)
            {
                Sha256 = sha256;
                Length = length;
                LockedFile = lockedFile;
            }

            internal string Sha256 { get; private set; }
            internal long Length { get; private set; }
            internal FileStream LockedFile { get; private set; }
            internal bool Consumed { get; set; }

            public void Dispose()
            {
                FileStream file = LockedFile;
                LockedFile = null;
                if (file != null) file.Dispose();
            }
        }
    }

    internal static class StagingBuilder
    {
        private const int BlockSize = 64 * 1024;
        private const int MaximumWorkerCount = 4;

        internal static Task<StagingBuildResult> ExtractAndValidateAsync(
            string packagePath,
            string stagingRoot,
            CancellationToken cancellationToken)
        {
            return Task.Run(
                () => ExtractAndValidate(packagePath, stagingRoot, cancellationToken),
                cancellationToken);
        }

        internal static StagingBuildResult ExtractAndValidate(
            string packagePath,
            string stagingRoot,
            CancellationToken cancellationToken)
        {
            return ExtractAndValidate(packagePath, stagingRoot, cancellationToken, null);
        }

        internal static StagingBuildResult ExtractAndValidate(
            string packagePath,
            string stagingRoot,
            CancellationToken cancellationToken,
            int? workerCountOverride)
        {
            string packageFullPath = Path.GetFullPath(packagePath);
            string root = PrepareEmptyStagingRoot(stagingRoot);
            MsixZipLayout layout = MsixZipLayout.Read(packageFullPath);
            ValidateFileHierarchy(layout.Entries);
            StagingBuildResult result = new StagingBuildResult(root);
            HashSet<string> checkedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                root.TrimEnd(Path.DirectorySeparatorChar)
            };

            try
            {
                using (FileStream package = new FileStream(
                    packageFullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.RandomAccess))
                using (ZipArchive archive = new ZipArchive(package, ZipArchiveMode.Read, false))
                {
                    Dictionary<string, ZipArchiveEntry> archiveEntries = BuildArchiveIndex(archive, layout);
                    ZipArchiveEntry manifestEntry;
                    if (archiveEntries.TryGetValue("AppxManifest.xml", out manifestEntry))
                    {
                        using (Stream manifest = manifestEntry.Open())
                        {
                            result.Profile = PackageProfileReader.Read(manifest);
                        }
                    }
                }

                StagingWorkItem[] workItems = new StagingWorkItem[layout.PhysicalEntries.Count];
                for (int index = 0; index < layout.PhysicalEntries.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MsixZipEntry layoutEntry = layout.PhysicalEntries[index];
                    string destination = ResolveDestination(root, layoutEntry.CanonicalName);
                    result.SkippedDirectoryProbeCount += EnsureParentDirectories(
                        root,
                        destination,
                        checkedDirectories);
                    workItems[index] = new StagingWorkItem(layoutEntry, destination);
                }
                result.ValidatedDirectoryCount = Math.Max(0, checkedDirectories.Count - 1);
                result.WorkerCount = ResolveWorkerCount(workItems.Length, workerCountOverride);
                ExtractWorkItems(
                    packageFullPath,
                    layout,
                    workItems,
                    result,
                    result.WorkerCount,
                    cancellationToken);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static Dictionary<string, ZipArchiveEntry> BuildArchiveIndex(
            ZipArchive archive,
            MsixZipLayout layout)
        {
            Dictionary<string, ZipArchiveEntry> archiveEntries = new Dictionary<string, ZipArchiveEntry>(
                StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry archiveEntry in archive.Entries)
            {
                string canonicalName = MsixZipLayout.NormalizePackagePath(archiveEntry.FullName);
                if (archiveEntries.ContainsKey(canonicalName))
                {
                    throw new InvalidDataException("ZipArchive 解码后出现重复路径：" + archiveEntry.FullName);
                }
                MsixZipEntry layoutEntry;
                if (!layout.TryGetEntry(canonicalName, out layoutEntry) ||
                    archiveEntry.Length != layoutEntry.UncompressedSize ||
                    archiveEntry.CompressedLength != layoutEntry.CompressedSize)
                {
                    throw new InvalidDataException("ZipArchive 条目与已验证 MSIX 布局不一致：" + archiveEntry.FullName);
                }
                archiveEntries.Add(canonicalName, archiveEntry);
            }
            if (archiveEntries.Count != layout.Entries.Count)
            {
                throw new InvalidDataException("ZipArchive 条目数与已验证 MSIX 中央目录不一致。");
            }
            return archiveEntries;
        }

        private static int ResolveWorkerCount(int itemCount, int? workerCountOverride)
        {
            int requested = workerCountOverride ?? Math.Min(MaximumWorkerCount, Environment.ProcessorCount);
            if (requested <= 0 || requested > MaximumWorkerCount)
            {
                throw new ArgumentOutOfRangeException(nameof(workerCountOverride), "staging 工作线程数必须位于 1 到 4。");
            }
            return Math.Max(1, Math.Min(requested, itemCount));
        }

        private static void ExtractWorkItems(
            string packagePath,
            MsixZipLayout layout,
            StagingWorkItem[] workItems,
            StagingBuildResult result,
            int workerCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int nextIndex = -1;
            if (workerCount == 1)
            {
                WorkerMetrics metrics = ExtractWorker(
                    packagePath,
                    layout,
                    workItems,
                    result,
                    ref nextIndex,
                    cancellationToken);
                result.MergeExtractionMetrics(
                    metrics.FileCount,
                    metrics.ExtractedBytes,
                    metrics.VerifiedBlockCount,
                    metrics.FootprintFileCount);
                return;
            }

            Exception firstFailure = null;
            using (CancellationTokenSource workersCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task[] workers = new Task[workerCount];
                for (int workerIndex = 0; workerIndex < workers.Length; workerIndex++)
                {
                    workers[workerIndex] = Task.Run(delegate
                    {
                        try
                        {
                            WorkerMetrics metrics = ExtractWorker(
                                packagePath,
                                layout,
                                workItems,
                                result,
                                ref nextIndex,
                                workersCancellation.Token);
                            result.MergeExtractionMetrics(
                                metrics.FileCount,
                                metrics.ExtractedBytes,
                                metrics.VerifiedBlockCount,
                                metrics.FootprintFileCount);
                        }
                        catch (Exception exception)
                        {
                            Interlocked.CompareExchange(ref firstFailure, exception, null);
                            workersCancellation.Cancel();
                            throw;
                        }
                    });
                }

                try
                {
                    Task.WhenAll(workers).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    ExceptionDispatchInfo.Capture(firstFailure ?? exception).Throw();
                    throw;
                }
            }
        }

        private static WorkerMetrics ExtractWorker(
            string packagePath,
            MsixZipLayout layout,
            StagingWorkItem[] workItems,
            StagingBuildResult result,
            ref int nextIndex,
            CancellationToken cancellationToken)
        {
            WorkerMetrics metrics = new WorkerMetrics();
            byte[] buffer = new byte[BlockSize];
            using (SHA256 blockSha256 = SHA256.Create())
            using (FileStream package = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.RandomAccess))
            using (ZipArchive archive = new ZipArchive(package, ZipArchiveMode.Read, false))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int index = Interlocked.Increment(ref nextIndex);
                    if (index >= workItems.Length) break;
                    StagingWorkItem item = workItems[index];
                    ZipArchiveEntry archiveEntry = archive.GetEntry(item.LayoutEntry.OriginalName);
                    if (archiveEntry == null ||
                        !string.Equals(
                            MsixZipLayout.NormalizePackagePath(archiveEntry.FullName),
                            item.LayoutEntry.CanonicalName,
                            StringComparison.OrdinalIgnoreCase) ||
                        archiveEntry.Length != item.LayoutEntry.UncompressedSize ||
                        archiveEntry.CompressedLength != item.LayoutEntry.CompressedSize)
                    {
                        throw new InvalidDataException("并行 MSIX 解包缺少或误读中央目录条目：" + item.LayoutEntry.OriginalName);
                    }

                    ExtractWorkItem(
                        archiveEntry,
                        item,
                        layout,
                        result,
                        metrics,
                        buffer,
                        blockSha256,
                        cancellationToken);
                    metrics.FileCount++;
                }
            }
            return metrics;
        }

        private static void ExtractWorkItem(
            ZipArchiveEntry archiveEntry,
            StagingWorkItem item,
            MsixZipLayout layout,
            StagingBuildResult result,
            WorkerMetrics metrics,
            byte[] buffer,
            SHA256 blockSha256,
            CancellationToken cancellationToken)
        {
            bool captureDigest = ShouldCaptureOfficialArtifactDigest(item.LayoutEntry.CanonicalName);
            FileStream output = new FileStream(
                NativeFileSystem.ToExtendedPath(item.Destination),
                FileMode.CreateNew,
                FileAccess.Write,
                captureDigest ? FileShare.Read : FileShare.None,
                BlockSize,
                FileOptions.SequentialScan);
            try
            {
                string digest;
                MsixBlockMapFile blockMapFile;
                using (Stream input = archiveEntry.Open())
                {
                    digest = layout.TryGetBlockMapFile(item.LayoutEntry.CanonicalName, out blockMapFile)
                        ? ExtractVerifiedFile(
                            input,
                            output,
                            blockMapFile,
                            metrics,
                            captureDigest,
                            buffer,
                            blockSha256,
                            cancellationToken)
                        : ExtractFootprintFile(
                            input,
                            output,
                            item.LayoutEntry.UncompressedSize,
                            item.LayoutEntry.CanonicalName,
                            metrics,
                            captureDigest,
                            buffer,
                            cancellationToken);
                }
                if (captureDigest)
                {
                    output.Flush(true);
                    result.AddOfficialArtifactDigest(
                        item.LayoutEntry.CanonicalName,
                        digest,
                        item.LayoutEntry.UncompressedSize,
                        output);
                    output = null;
                }
            }
            finally
            {
                if (output != null) output.Dispose();
            }
        }

        private static string ExtractVerifiedFile(
            Stream input,
            FileStream output,
            MsixBlockMapFile file,
            WorkerMetrics metrics,
            bool captureDigest,
            byte[] buffer,
            SHA256 sha256,
            CancellationToken cancellationToken)
        {
            long written = 0;
            using (SHA256 fileSha256 = captureDigest ? SHA256.Create() : null)
            {
                foreach (MsixBlockMapBlock block in file.Blocks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int expected = checked((int)Math.Min(BlockSize, file.Size - written));
                    ReadExactly(input, buffer, expected, file.CanonicalName);
                    byte[] actualHash = sha256.ComputeHash(buffer, 0, expected);
                    if (!FixedTimeEquals(actualHash, block.Hash))
                    {
                        throw new InvalidDataException("官方包文件块哈希不匹配：" + file.CanonicalName);
                    }
                    if (fileSha256 != null) fileSha256.TransformBlock(buffer, 0, expected, buffer, 0);
                    output.Write(buffer, 0, expected);
                    written += expected;
                    metrics.ExtractedBytes += expected;
                    metrics.VerifiedBlockCount++;
                }
                if (written != file.Size || input.ReadByte() != -1)
                {
                    throw new InvalidDataException(string.Format(
                        CultureInfo.InvariantCulture,
                        "官方包文件长度与 BlockMap 不一致：{0}，预期 {1} 字节，实际至少 {2} 字节。",
                        file.CanonicalName,
                        file.Size,
                        written));
                }
                if (fileSha256 == null) return null;
                fileSha256.TransformFinalBlock(new byte[0], 0, 0);
                return ToHex(fileSha256.Hash);
            }
        }

        private static string ExtractFootprintFile(
            Stream input,
            FileStream output,
            long expectedSize,
            string canonicalName,
            WorkerMetrics metrics,
            bool captureDigest,
            byte[] buffer,
            CancellationToken cancellationToken)
        {
            long written = 0;
            using (SHA256 fileSha256 = captureDigest ? SHA256.Create() : null)
            {
                while (written < expectedSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int requested = checked((int)Math.Min(buffer.Length, expectedSize - written));
                    int read = input.Read(buffer, 0, requested);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException("读取 MSIX footprint 条目时意外结束：" + canonicalName);
                    }
                    if (fileSha256 != null) fileSha256.TransformBlock(buffer, 0, read, buffer, 0);
                    output.Write(buffer, 0, read);
                    written += read;
                    metrics.ExtractedBytes += read;
                }
                if (input.ReadByte() != -1)
                {
                    throw new InvalidDataException("MSIX footprint 条目超过中央目录声明大小：" + canonicalName);
                }
                metrics.FootprintFileCount++;
                if (fileSha256 == null) return null;
                fileSha256.TransformFinalBlock(new byte[0], 0, 0);
                return ToHex(fileSha256.Hash);
            }
        }

        private static void ReadExactly(Stream input, byte[] buffer, int count, string canonicalName)
        {
            int total = 0;
            while (total < count)
            {
                int read = input.Read(buffer, total, count - total);
                if (read <= 0)
                {
                    throw new EndOfStreamException("读取 BlockMap 文件块时意外结束：" + canonicalName);
                }
                total += read;
            }
        }

        private static string PrepareEmptyStagingRoot(string stagingRoot)
        {
            if (string.IsNullOrWhiteSpace(stagingRoot)) throw new ArgumentException("staging 目录不能为空。", nameof(stagingRoot));
            string root = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string extendedRoot = NativeFileSystem.ToExtendedPath(root);
            if (Directory.Exists(extendedRoot))
            {
                if ((File.GetAttributes(extendedRoot) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("staging 根目录不能是重解析点：" + root);
                }
                if (Directory.EnumerateFileSystemEntries(extendedRoot).Any())
                {
                    throw new InvalidDataException("staging 根目录必须为空：" + root);
                }
            }
            else
            {
                Directory.CreateDirectory(extendedRoot);
            }
            return root + Path.DirectorySeparatorChar;
        }

        private static void ValidateFileHierarchy(IList<MsixZipEntry> entries)
        {
            HashSet<string> files = new HashSet<string>(
                entries.Select(value => value.CanonicalName),
                StringComparer.OrdinalIgnoreCase);
            foreach (string path in files)
            {
                int separator = path.IndexOf('/');
                while (separator >= 0)
                {
                    string prefix = path.Substring(0, separator);
                    if (files.Contains(prefix))
                    {
                        throw new InvalidDataException("MSIX 路径同时作为文件和父目录：" + prefix);
                    }
                    separator = path.IndexOf('/', separator + 1);
                }
            }
        }

        private static string ResolveDestination(string root, string canonicalName)
        {
            string destination = Path.GetFullPath(Path.Combine(
                root,
                canonicalName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("MSIX 条目路径越出 staging：" + canonicalName);
            }
            return destination;
        }

        private static int EnsureParentDirectories(
            string root,
            string destination,
            HashSet<string> checkedDirectories)
        {
            string parent = Path.GetDirectoryName(destination);
            string rootDirectory = root.TrimEnd(Path.DirectorySeparatorChar);
            bool isRoot = string.Equals(parent, rootDirectory, StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(parent) ||
                (!isRoot && !parent.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("MSIX 条目父目录越出 staging：" + destination);
            }
            string relative = isRoot
                ? string.Empty
                : parent.Substring(root.Length).Trim(Path.DirectorySeparatorChar);
            string current = rootDirectory;
            if (relative.Length == 0) return 0;
            int skippedProbes = 0;
            foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                if (!checkedDirectories.Add(current))
                {
                    skippedProbes++;
                    continue;
                }
                string extendedCurrent = NativeFileSystem.ToExtendedPath(current);
                if (!Directory.Exists(extendedCurrent)) Directory.CreateDirectory(extendedCurrent);
                if ((File.GetAttributes(extendedCurrent) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("staging 子目录不能是重解析点：" + current);
                }
            }
            return skippedProbes;
        }

        private sealed class StagingWorkItem
        {
            internal StagingWorkItem(MsixZipEntry layoutEntry, string destination)
            {
                LayoutEntry = layoutEntry;
                Destination = destination;
            }

            internal MsixZipEntry LayoutEntry { get; private set; }
            internal string Destination { get; private set; }
        }

        private sealed class WorkerMetrics
        {
            internal int FileCount;
            internal long ExtractedBytes;
            internal long VerifiedBlockCount;
            internal int FootprintFileCount;
        }

        private static bool ShouldCaptureOfficialArtifactDigest(string canonicalName)
        {
            if (string.Equals(canonicalName, "AppxManifest.xml", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            string[] resourceNames =
            {
                "app.asar",
                "icon-chatgpt.ico",
                "codex-windows-sandbox-setup.exe"
            };
            if (canonicalName.EndsWith("/codex.exe", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string resourceName in resourceNames)
            {
                if (canonicalName.EndsWith(
                    "/resources/" + resourceName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string ToHex(byte[] hash)
        {
            char[] characters = new char[hash.Length * 2];
            const string alphabet = "0123456789abcdef";
            for (int index = 0; index < hash.Length; index++)
            {
                characters[index * 2] = alphabet[hash[index] >> 4];
                characters[index * 2 + 1] = alphabet[hash[index] & 0x0f];
            }
            return new string(characters);
        }

        private static bool FixedTimeEquals(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length) return false;
            int difference = 0;
            for (int index = 0; index < first.Length; index++) difference |= first[index] ^ second[index];
            return difference == 0;
        }
    }
}
