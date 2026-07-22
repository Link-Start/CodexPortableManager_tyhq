using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPortableManager
{
    internal sealed class PackageMaterializationResult
    {
        internal PackageMaterializationResult(
            string outputPath,
            string sha256Base64,
            long targetBytes,
            long reusedBytes,
            long synthesizedBytes,
            int reusedEntryCount,
            int targetEntryCount)
        {
            OutputPath = outputPath;
            Sha256Base64 = sha256Base64;
            TargetBytes = targetBytes;
            ReusedBytes = reusedBytes;
            SynthesizedBytes = synthesizedBytes;
            ReusedEntryCount = reusedEntryCount;
            TargetEntryCount = targetEntryCount;
        }

        internal string OutputPath { get; private set; }
        internal string Sha256Base64 { get; private set; }
        internal long TargetBytes { get; private set; }
        internal long ReusedBytes { get; private set; }
        internal long SynthesizedBytes { get; private set; }
        internal int ReusedEntryCount { get; private set; }
        internal int TargetEntryCount { get; private set; }
    }

    internal static class IncrementalPackageMaterializer
    {
        private const int CopyBufferSize = 1024 * 1024;

        internal static PackageMaterializationResult MaterializeFromLocalTarget(
            string previousPackagePath,
            string targetPackagePath,
            string outputPath,
            PackageReusePlan plan,
            string expectedSha256Base64)
        {
            if (string.IsNullOrWhiteSpace(previousPackagePath)) throw new ArgumentException("旧版 MSIX 路径不能为空。", nameof(previousPackagePath));
            if (string.IsNullOrWhiteSpace(targetPackagePath)) throw new ArgumentException("目标 MSIX 路径不能为空。", nameof(targetPackagePath));
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("物化输出路径不能为空。", nameof(outputPath));
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            byte[] expectedDigest = ParseExpectedDigest(expectedSha256Base64);

            string previousFullPath = Path.GetFullPath(previousPackagePath);
            string targetFullPath = Path.GetFullPath(targetPackagePath);
            string outputFullPath = Path.GetFullPath(outputPath);
            if (PathsEqual(previousFullPath, outputFullPath) || PathsEqual(targetFullPath, outputFullPath))
            {
                throw new InvalidOperationException("增量物化输出不能覆盖旧版或目标 MSIX。");
            }
            string outputDirectory = Path.GetDirectoryName(outputFullPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new InvalidOperationException("增量物化输出目录无效。");
            }
            Directory.CreateDirectory(outputDirectory);

            bool outputCreated = false;
            try
            {
                byte[] actualDigest;
                using (FileStream previous = OpenStableRead(previousFullPath))
                using (FileStream target = OpenStableRead(targetFullPath))
                using (FileStream output = new FileStream(
                    outputFullPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferSize,
                    FileOptions.SequentialScan))
                using (SHA256 sha256 = SHA256.Create())
                {
                    outputCreated = true;
                    if (target.Length != plan.TargetLength)
                    {
                        throw new IOException("目标 MSIX 在复用计划生成后发生变化。");
                    }
                    byte[] buffer = new byte[CopyBufferSize];
                    foreach (PackageMaterializationSegment segment in plan.Segments)
                    {
                        if (output.Position != segment.TargetOffset)
                        {
                            throw new InvalidDataException("增量物化计划的目标偏移不连续。");
                        }
                        if (segment.Source == PackageSegmentSource.Synthesized)
                        {
                            WriteAndHash(output, sha256, segment.SynthesizedBytes, 0, segment.SynthesizedBytes.Length);
                            continue;
                        }

                        FileStream source = segment.Source == PackageSegmentSource.ReusedPackage ? previous : target;
                        CopyAndHash(source, output, sha256, segment.SourceOffset, segment.Length, buffer);
                    }
                    if (output.Position != plan.TargetLength)
                    {
                        throw new InvalidDataException("增量物化结果长度与目标包不一致。");
                    }
                    sha256.TransformFinalBlock(new byte[0], 0, 0);
                    actualDigest = sha256.Hash;
                    output.Flush(true);
                }

                if (!FixedTimeEquals(actualDigest, expectedDigest))
                {
                    throw new InvalidDataException(
                        "增量物化结果 SHA-256 与目标摘要不一致。期望 " +
                        Convert.ToBase64String(expectedDigest) + "，实际 " + Convert.ToBase64String(actualDigest) + "。");
                }
                return new PackageMaterializationResult(
                    outputFullPath,
                    Convert.ToBase64String(actualDigest),
                    plan.TargetBytes,
                    plan.ReusedBytes,
                    plan.SynthesizedBytes,
                    plan.ReusedEntryCount,
                    plan.TargetEntryCount);
            }
            catch
            {
                if (outputCreated)
                {
                    TryDelete(outputFullPath);
                }
                throw;
            }
        }

        internal static async Task<PackageMaterializationResult> MaterializeFromRemoteTargetAsync(
            string previousPackagePath,
            string outputPath,
            PackageReusePlan plan,
            RemoteRangeReader ranges,
            string expectedSha256Base64,
            IProgress<OperationProgress> progress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(previousPackagePath)) throw new ArgumentException("旧版 MSIX 路径不能为空。", nameof(previousPackagePath));
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("物化输出路径不能为空。", nameof(outputPath));
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (ranges == null) throw new ArgumentNullException(nameof(ranges));
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            if (plan.TargetLength != ranges.PackageLength)
            {
                throw new InvalidDataException("远程 Range 总长度与增量复用计划不一致。");
            }
            byte[] expectedDigest = ParseExpectedDigest(expectedSha256Base64);
            string previousFullPath = Path.GetFullPath(previousPackagePath);
            string outputFullPath = Path.GetFullPath(outputPath);
            if (PathsEqual(previousFullPath, outputFullPath))
            {
                throw new InvalidOperationException("增量物化输出不能覆盖旧版 MSIX。");
            }
            string outputDirectory = Path.GetDirectoryName(outputFullPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new InvalidOperationException("增量物化输出目录无效。");
            }
            Directory.CreateDirectory(outputDirectory);

            bool outputCreated = false;
            try
            {
                byte[] actualDigest;
                long remoteConsumed = 0;
                using (FileStream previous = OpenStableRead(previousFullPath))
                using (FileStream output = new FileStream(
                    outputFullPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferSize,
                    true))
                using (SHA256 sha256 = SHA256.Create())
                {
                    outputCreated = true;
                    byte[] localBuffer = new byte[CopyBufferSize];
                    foreach (PackageMaterializationSegment segment in plan.Segments)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (output.Position != segment.TargetOffset)
                        {
                            throw new InvalidDataException("远程增量物化计划的目标偏移不连续。");
                        }
                        if (segment.Source == PackageSegmentSource.Synthesized)
                        {
                            await ranges.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                            await WriteAndHashAsync(
                                output,
                                sha256,
                                segment.SynthesizedBytes,
                                cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        if (segment.Source == PackageSegmentSource.ReusedPackage)
                        {
                            await CopyAndHashAsync(
                                previous,
                                output,
                                sha256,
                                segment.SourceOffset,
                                segment.Length,
                                localBuffer,
                                ranges,
                                cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        long sourceOffset = segment.SourceOffset;
                        long remaining = segment.Length;
                        while (remaining > 0)
                        {
                            ranges.UpdateMaterializationProgress(
                                remoteConsumed,
                                plan.TargetBytes,
                                plan.ReusedBytes);
                            byte[] bytes = await ranges.ReadBestRangeAsync(
                                sourceOffset,
                                remaining,
                                false,
                                cancellationToken).ConfigureAwait(false);
                            await WriteAndHashAsync(output, sha256, bytes, cancellationToken).ConfigureAwait(false);
                            sourceOffset += bytes.Length;
                            remaining -= bytes.Length;
                            remoteConsumed += bytes.Length;
                            ranges.ReportMaterializationProgress(
                                remoteConsumed,
                                plan.TargetBytes,
                                plan.ReusedBytes,
                                progress);
                        }
                    }
                    if (output.Position != plan.TargetLength)
                    {
                        throw new InvalidDataException("远程增量物化结果长度与目标包不一致。");
                    }
                    sha256.TransformFinalBlock(new byte[0], 0, 0);
                    actualDigest = sha256.Hash;
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(true);
                }
                if (!FixedTimeEquals(actualDigest, expectedDigest))
                {
                    throw new InvalidDataException(
                        "远程增量物化结果 SHA-256 与目标摘要不一致。期望 " +
                        Convert.ToBase64String(expectedDigest) + "，实际 " + Convert.ToBase64String(actualDigest) + "。");
                }
                return new PackageMaterializationResult(
                    outputFullPath,
                    Convert.ToBase64String(actualDigest),
                    plan.TargetBytes,
                    plan.ReusedBytes,
                    plan.SynthesizedBytes,
                    plan.ReusedEntryCount,
                    plan.TargetEntryCount);
            }
            catch
            {
                if (outputCreated) TryDelete(outputFullPath);
                throw;
            }
        }

        internal static string ComputeSha256Base64(string path)
        {
            using (FileStream stream = OpenStableRead(Path.GetFullPath(path)))
            using (SHA256 sha256 = SHA256.Create())
            {
                return Convert.ToBase64String(sha256.ComputeHash(stream));
            }
        }

        private static void CopyAndHash(
            FileStream source,
            FileStream output,
            SHA256 sha256,
            long sourceOffset,
            long length,
            byte[] buffer)
        {
            if (sourceOffset < 0 || length < 0 || sourceOffset > source.Length || length > source.Length - sourceOffset)
            {
                throw new InvalidDataException("增量物化源区间越界。");
            }
            source.Position = sourceOffset;
            long remaining = length;
            while (remaining > 0)
            {
                int requested = checked((int)Math.Min(buffer.Length, remaining));
                int read = source.Read(buffer, 0, requested);
                if (read <= 0)
                {
                    throw new EndOfStreamException("读取增量物化源时意外结束。");
                }
                WriteAndHash(output, sha256, buffer, 0, read);
                remaining -= read;
            }
        }

        private static void WriteAndHash(
            FileStream output,
            SHA256 sha256,
            byte[] bytes,
            int offset,
            int count)
        {
            output.Write(bytes, offset, count);
            sha256.TransformBlock(bytes, offset, count, null, 0);
        }

        private static async Task WriteAndHashAsync(
            FileStream output,
            SHA256 sha256,
            byte[] bytes,
            CancellationToken cancellationToken)
        {
            await output.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            sha256.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        private static async Task CopyAndHashAsync(
            FileStream source,
            FileStream output,
            SHA256 sha256,
            long sourceOffset,
            long length,
            byte[] buffer,
            RemoteRangeReader ranges,
            CancellationToken cancellationToken)
        {
            if (sourceOffset < 0 || length < 0 || sourceOffset > source.Length || length > source.Length - sourceOffset)
            {
                throw new InvalidDataException("远程增量物化的本地复用区间越界。");
            }
            source.Position = sourceOffset;
            long remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ranges.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                int requested = checked((int)Math.Min(buffer.Length, remaining));
                int read = await source.ReadAsync(buffer, 0, requested, cancellationToken).ConfigureAwait(false);
                if (read <= 0) throw new EndOfStreamException("读取本地 MSIX 复用源时意外结束。");
                await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                sha256.TransformBlock(buffer, 0, read, null, 0);
                remaining -= read;
            }
        }

        private static byte[] ParseExpectedDigest(string expectedSha256Base64)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256Base64))
            {
                throw new InvalidDataException("目标 SHA-256 不能为空。");
            }
            byte[] digest;
            try
            {
                digest = Convert.FromBase64String(expectedSha256Base64);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("目标 SHA-256 不是有效 Base64。", exception);
            }
            if (digest.Length != 32)
            {
                throw new InvalidDataException("目标摘要不是 SHA-256。");
            }
            return digest;
        }

        private static bool FixedTimeEquals(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length) return false;
            int difference = 0;
            for (int index = 0; index < first.Length; index++)
            {
                difference |= first[index] ^ second[index];
            }
            return difference == 0;
        }

        private static FileStream OpenStableRead(string path)
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.SequentialScan);
        }

        private static bool PathsEqual(string first, string second)
        {
            return string.Equals(
                Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) NativeFileSystem.DeleteFile(path);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.WriteLine(
                    "清理失败的增量物化临时文件失败：" + path + "；" + exception.Message,
                    "CodexPortableManager");
            }
        }
    }
}
