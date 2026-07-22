using System;
using System.Collections.Generic;
using System.IO;

namespace CodexPortableManager
{
    internal enum PackageSegmentSource
    {
        TargetPackage,
        ReusedPackage,
        Synthesized
    }

    internal sealed class PackageMaterializationSegment
    {
        internal PackageSegmentSource Source { get; set; }
        internal long TargetOffset { get; set; }
        internal long Length { get; set; }
        internal long SourceOffset { get; set; }
        internal byte[] SynthesizedBytes { get; set; }
    }

    internal sealed class PackageReusePlan
    {
        internal PackageReusePlan(
            long targetLength,
            IList<PackageMaterializationSegment> segments,
            int reusedEntryCount,
            int targetEntryCount,
            long reusedBytes,
            long targetBytes,
            long synthesizedBytes)
        {
            TargetLength = targetLength;
            Segments = segments;
            ReusedEntryCount = reusedEntryCount;
            TargetEntryCount = targetEntryCount;
            ReusedBytes = reusedBytes;
            TargetBytes = targetBytes;
            SynthesizedBytes = synthesizedBytes;
        }

        internal long TargetLength { get; private set; }
        internal IList<PackageMaterializationSegment> Segments { get; private set; }
        internal int ReusedEntryCount { get; private set; }
        internal int TargetEntryCount { get; private set; }
        internal long ReusedBytes { get; private set; }
        internal long TargetBytes { get; private set; }
        internal long SynthesizedBytes { get; private set; }
    }

    internal static class PackageReusePlanner
    {
        private const int LocalHeaderTimestampOffset = 10;
        private const int LocalHeaderTimestampLength = 4;

        internal static PackageReusePlan Create(MsixZipLayout previous, MsixZipLayout target)
        {
            if (target != null && target.IsRemote)
            {
                throw new ArgumentException("远程目标布局必须使用 CreateForRemoteTarget。", nameof(target));
            }
            return CreateCore(previous, target, false);
        }

        internal static PackageReusePlan CreateForRemoteTarget(MsixZipLayout previous, MsixZipLayout target)
        {
            if (target == null || !target.IsRemote)
            {
                throw new ArgumentException("目标布局不是远程 bootstrap 布局。", nameof(target));
            }
            return CreateCore(previous, target, true);
        }

        private static PackageReusePlan CreateCore(
            MsixZipLayout previous,
            MsixZipLayout target,
            bool remoteTarget)
        {
            if (previous == null) throw new ArgumentNullException(nameof(previous));
            if (target == null) throw new ArgumentNullException(nameof(target));

            List<PackageMaterializationSegment> segments = new List<PackageMaterializationSegment>();
            int reusedEntries = 0;
            long reusedBytes = 0;
            long targetBytes = 0;
            long synthesizedBytes = 0;
            long cursor = 0;

            using (FileStream previousStream = OpenStableRead(previous.PackagePath))
            using (FileStream targetStream = remoteTarget ? null : OpenStableRead(target.PackagePath))
            {
                ValidateStableLength(previousStream, previous.PackageLength, "旧版 MSIX");
                if (targetStream != null) ValidateStableLength(targetStream, target.PackageLength, "目标 MSIX");

                foreach (MsixZipEntry targetEntry in target.PhysicalEntries)
                {
                    if (targetEntry.LocalHeaderOffset < cursor)
                    {
                        throw new InvalidDataException("目标 MSIX 物理条目发生重叠：" + targetEntry.OriginalName);
                    }
                    if (targetEntry.LocalHeaderOffset > cursor)
                    {
                        AddSourceSegment(
                            segments,
                            PackageSegmentSource.TargetPackage,
                            cursor,
                            cursor,
                            targetEntry.LocalHeaderOffset - cursor);
                        targetBytes = checked(targetBytes + targetEntry.LocalHeaderOffset - cursor);
                    }

                    MsixZipEntry previousEntry;
                    byte[] synthesizedHeader = null;
                    bool metadataReusable = previous.TryGetEntry(targetEntry.CanonicalName, out previousEntry) &&
                        CanReuseEntry(previous, target, previousEntry, targetEntry);
                    bool headerReusable = metadataReusable && (remoteTarget
                        ? TrySynthesizeRemoteHeader(previousStream, previousEntry, targetEntry, out synthesizedHeader)
                        : TrySynthesizeHeader(previousStream, targetStream, previousEntry, targetEntry, out synthesizedHeader));
                    bool descriptorReusable = headerReusable && (remoteTarget ||
                        DataDescriptorsEqual(previousStream, targetStream, previousEntry, targetEntry));
                    if (descriptorReusable)
                    {
                        AddSynthesizedSegment(segments, targetEntry.LocalHeaderOffset, synthesizedHeader);
                        synthesizedBytes = checked(synthesizedBytes + synthesizedHeader.Length);

                        long reusableLength = checked(targetEntry.CompressedSize + targetEntry.DataDescriptorLength);
                        AddSourceSegment(
                            segments,
                            PackageSegmentSource.ReusedPackage,
                            targetEntry.DataOffset,
                            previousEntry.DataOffset,
                            reusableLength);
                        reusedBytes = checked(reusedBytes + reusableLength);
                        reusedEntries++;
                    }
                    else
                    {
                        AddSourceSegment(
                            segments,
                            PackageSegmentSource.TargetPackage,
                            targetEntry.LocalHeaderOffset,
                            targetEntry.LocalHeaderOffset,
                            targetEntry.RecordLength);
                        targetBytes = checked(targetBytes + targetEntry.RecordLength);
                    }
                    cursor = targetEntry.RecordEndOffset;
                }

                if (cursor < target.PackageLength)
                {
                    AddSourceSegment(
                        segments,
                        PackageSegmentSource.TargetPackage,
                        cursor,
                        cursor,
                        target.PackageLength - cursor);
                    targetBytes = checked(targetBytes + target.PackageLength - cursor);
                }
            }

            ValidateCoverage(segments, target.PackageLength, previous.PackageLength);
            if (checked(reusedBytes + targetBytes + synthesizedBytes) != target.PackageLength)
            {
                throw new InvalidDataException("MSIX 复用计划的字节统计未覆盖完整目标包。");
            }
            return new PackageReusePlan(
                target.PackageLength,
                segments,
                reusedEntries,
                target.Entries.Count,
                reusedBytes,
                targetBytes,
                synthesizedBytes);
        }

        private static bool CanReuseEntry(
            MsixZipLayout previous,
            MsixZipLayout target,
            MsixZipEntry previousEntry,
            MsixZipEntry targetEntry)
        {
            if (!string.Equals(previousEntry.CanonicalName, targetEntry.CanonicalName, StringComparison.Ordinal) ||
                previousEntry.Flags != targetEntry.Flags ||
                previousEntry.CompressionMethod != targetEntry.CompressionMethod ||
                previousEntry.Crc32 != targetEntry.Crc32 ||
                previousEntry.CompressedSize != targetEntry.CompressedSize ||
                previousEntry.UncompressedSize != targetEntry.UncompressedSize ||
                previousEntry.LocalHeaderLength != targetEntry.LocalHeaderLength ||
                previousEntry.DataDescriptorLength != targetEntry.DataDescriptorLength ||
                !BytesEqual(previousEntry.NameBytes, targetEntry.NameBytes))
            {
                return false;
            }

            MsixBlockMapFile previousFile;
            MsixBlockMapFile targetFile;
            if (!previous.TryGetBlockMapFile(previousEntry.CanonicalName, out previousFile) ||
                !target.TryGetBlockMapFile(targetEntry.CanonicalName, out targetFile))
            {
                return false;
            }
            if (previousFile.Size != targetFile.Size ||
                previousFile.LocalHeaderSize != targetFile.LocalHeaderSize ||
                previousFile.Blocks.Count != targetFile.Blocks.Count)
            {
                return false;
            }
            for (int index = 0; index < previousFile.Blocks.Count; index++)
            {
                MsixBlockMapBlock previousBlock = previousFile.Blocks[index];
                MsixBlockMapBlock targetBlock = targetFile.Blocks[index];
                if (previousBlock.CompressedSize != targetBlock.CompressedSize ||
                    !BytesEqual(previousBlock.Hash, targetBlock.Hash))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TrySynthesizeRemoteHeader(
            FileStream previousStream,
            MsixZipEntry previousEntry,
            MsixZipEntry targetEntry,
            out byte[] synthesized)
        {
            synthesized = null;
            if (previousEntry.LocalHeaderLength != targetEntry.LocalHeaderLength ||
                targetEntry.LocalHeaderLength < LocalHeaderTimestampOffset + LocalHeaderTimestampLength ||
                targetEntry.LocalHeaderLength > int.MaxValue)
            {
                return false;
            }
            byte[] previousHeader = ReadBytes(
                previousStream,
                previousEntry.LocalHeaderOffset,
                checked((int)previousEntry.LocalHeaderLength));
            WriteUInt16(previousHeader, LocalHeaderTimestampOffset, targetEntry.LastModTime);
            WriteUInt16(previousHeader, LocalHeaderTimestampOffset + 2, targetEntry.LastModDate);
            synthesized = previousHeader;
            return true;
        }

        private static bool TrySynthesizeHeader(
            FileStream previousStream,
            FileStream targetStream,
            MsixZipEntry previousEntry,
            MsixZipEntry targetEntry,
            out byte[] synthesized)
        {
            synthesized = null;
            if (previousEntry.LocalHeaderLength != targetEntry.LocalHeaderLength ||
                targetEntry.LocalHeaderLength < LocalHeaderTimestampOffset + LocalHeaderTimestampLength ||
                targetEntry.LocalHeaderLength > int.MaxValue)
            {
                return false;
            }

            byte[] previousHeader = ReadBytes(
                previousStream,
                previousEntry.LocalHeaderOffset,
                checked((int)previousEntry.LocalHeaderLength));
            byte[] targetHeader = ReadBytes(
                targetStream,
                targetEntry.LocalHeaderOffset,
                checked((int)targetEntry.LocalHeaderLength));
            for (int index = 0; index < previousHeader.Length; index++)
            {
                if (index >= LocalHeaderTimestampOffset &&
                    index < LocalHeaderTimestampOffset + LocalHeaderTimestampLength)
                {
                    continue;
                }
                if (previousHeader[index] != targetHeader[index])
                {
                    return false;
                }
            }

            Array.Copy(
                targetHeader,
                LocalHeaderTimestampOffset,
                previousHeader,
                LocalHeaderTimestampOffset,
                LocalHeaderTimestampLength);
            if (!BytesEqual(previousHeader, targetHeader))
            {
                return false;
            }
            synthesized = previousHeader;
            return true;
        }

        private static bool DataDescriptorsEqual(
            FileStream previousStream,
            FileStream targetStream,
            MsixZipEntry previousEntry,
            MsixZipEntry targetEntry)
        {
            if (previousEntry.DataDescriptorLength != targetEntry.DataDescriptorLength) return false;
            if (targetEntry.DataDescriptorLength == 0) return true;
            byte[] previousDescriptor = ReadBytes(
                previousStream,
                previousEntry.DataDescriptorOffset,
                previousEntry.DataDescriptorLength);
            byte[] targetDescriptor = ReadBytes(
                targetStream,
                targetEntry.DataDescriptorOffset,
                targetEntry.DataDescriptorLength);
            return BytesEqual(previousDescriptor, targetDescriptor);
        }

        private static void AddSourceSegment(
            List<PackageMaterializationSegment> segments,
            PackageSegmentSource source,
            long targetOffset,
            long sourceOffset,
            long length)
        {
            if (length <= 0) return;
            PackageMaterializationSegment previous = segments.Count == 0 ? null : segments[segments.Count - 1];
            if (previous != null && previous.Source == source && source != PackageSegmentSource.Synthesized &&
                checked(previous.TargetOffset + previous.Length) == targetOffset &&
                checked(previous.SourceOffset + previous.Length) == sourceOffset)
            {
                previous.Length = checked(previous.Length + length);
                return;
            }
            segments.Add(new PackageMaterializationSegment
            {
                Source = source,
                TargetOffset = targetOffset,
                SourceOffset = sourceOffset,
                Length = length
            });
        }

        private static void AddSynthesizedSegment(
            List<PackageMaterializationSegment> segments,
            long targetOffset,
            byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) throw new ArgumentException("合成本地文件头不能为空。", nameof(bytes));
            segments.Add(new PackageMaterializationSegment
            {
                Source = PackageSegmentSource.Synthesized,
                TargetOffset = targetOffset,
                SourceOffset = -1,
                Length = bytes.Length,
                SynthesizedBytes = bytes
            });
        }

        private static void ValidateCoverage(
            IList<PackageMaterializationSegment> segments,
            long targetLength,
            long previousLength)
        {
            long cursor = 0;
            foreach (PackageMaterializationSegment segment in segments)
            {
                if (segment == null || segment.Length <= 0 || segment.TargetOffset != cursor)
                {
                    throw new InvalidDataException("MSIX 复用计划存在空段、重叠或缺口。");
                }
                long segmentEnd = checked(segment.TargetOffset + segment.Length);
                if (segmentEnd > targetLength)
                {
                    throw new InvalidDataException("MSIX 复用计划超出目标包范围。");
                }
                if (segment.Source == PackageSegmentSource.Synthesized)
                {
                    if (segment.SynthesizedBytes == null || segment.SynthesizedBytes.LongLength != segment.Length)
                    {
                        throw new InvalidDataException("MSIX 复用计划的合成段长度无效。");
                    }
                }
                else
                {
                    long sourceLength = segment.Source == PackageSegmentSource.TargetPackage
                        ? targetLength
                        : previousLength;
                    if (segment.SourceOffset < 0 || segment.SourceOffset > sourceLength ||
                        segment.Length > sourceLength - segment.SourceOffset)
                    {
                        throw new InvalidDataException("MSIX 复用计划的源范围越界。");
                    }
                }
                cursor = segmentEnd;
            }
            if (cursor != targetLength)
            {
                throw new InvalidDataException("MSIX 复用计划没有覆盖完整目标包。");
            }
        }

        private static FileStream OpenStableRead(string path)
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.RandomAccess);
        }

        private static void ValidateStableLength(FileStream stream, long expectedLength, string description)
        {
            if (stream.Length != expectedLength)
            {
                throw new IOException(description + "在布局解析后发生变化。");
            }
        }

        private static byte[] ReadBytes(FileStream stream, long offset, int length)
        {
            if (offset < 0 || length < 0 || offset > stream.Length || length > stream.Length - offset)
            {
                throw new InvalidDataException("读取 MSIX 复用源时范围越界。");
            }
            byte[] bytes = new byte[length];
            stream.Position = offset;
            int total = 0;
            while (total < bytes.Length)
            {
                int read = stream.Read(bytes, total, bytes.Length - total);
                if (read <= 0) throw new EndOfStreamException("读取 MSIX 复用源时意外结束。");
                total += read;
            }
            return bytes;
        }

        private static bool BytesEqual(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length) return false;
            for (int index = 0; index < first.Length; index++)
            {
                if (first[index] != second[index]) return false;
            }
            return true;
        }

        private static void WriteUInt16(byte[] bytes, int offset, ushort value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }
    }
}
