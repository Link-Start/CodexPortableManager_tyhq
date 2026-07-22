using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPortableManager
{
    internal static class RemoteMsixLayoutReader
    {
        private const int MaximumEndRecordSearch = 65557;
        private const long MaximumBlockMapRecordLength = 34L * 1024 * 1024;

        internal static async Task<MsixZipLayout> ReadAsync(
            RemoteRangeReader ranges,
            string packageIdentity,
            CancellationToken cancellationToken)
        {
            if (ranges == null) throw new ArgumentNullException(nameof(ranges));
            int tailLength = checked((int)Math.Min(ranges.PackageLength, MaximumEndRecordSearch));
            await ranges.ReadRangeAsync(
                ranges.PackageLength - tailLength,
                tailLength,
                true,
                cancellationToken).ConfigureAwait(false);

            MsixZipDirectoryInfo directory;
            using (Stream cached = ranges.OpenCachedStream())
            {
                directory = MsixZipLayout.ReadDirectoryInfo(cached);
            }
            await EnsureCachedAsync(
                ranges,
                directory.CentralDirectoryOffset,
                directory.CentralDirectorySize,
                cancellationToken).ConfigureAwait(false);

            List<MsixZipEntry> entries;
            using (Stream cached = ranges.OpenCachedStream())
            {
                entries = MsixZipLayout.ReadCentralDirectory(cached, directory);
            }
            MsixZipEntry[] physicalEntries = entries.OrderBy(value => value.LocalHeaderOffset).ToArray();
            MsixZipEntry blockMap = entries.SingleOrDefault(value =>
                string.Equals(value.CanonicalName, "AppxBlockMap.xml", StringComparison.OrdinalIgnoreCase));
            if (blockMap == null)
            {
                throw new InvalidDataException("远程 MSIX 中央目录缺少唯一 AppxBlockMap.xml。");
            }
            int blockMapIndex = Array.IndexOf(physicalEntries, blockMap);
            long recordEnd = blockMapIndex + 1 < physicalEntries.Length
                ? physicalEntries[blockMapIndex + 1].LocalHeaderOffset
                : directory.CentralDirectoryOffset;
            long recordLength = checked(recordEnd - blockMap.LocalHeaderOffset);
            if (recordLength <= 0 || recordLength > MaximumBlockMapRecordLength)
            {
                throw new InvalidDataException("远程 AppxBlockMap.xml 物理记录大小超出限制。");
            }
            await EnsureCachedAsync(
                ranges,
                blockMap.LocalHeaderOffset,
                recordLength,
                cancellationToken).ConfigureAwait(false);

            using (Stream cached = ranges.OpenCachedStream())
            {
                return MsixZipLayout.CompleteRemoteRead(
                    packageIdentity,
                    ranges.PackageLength,
                    cached,
                    directory,
                    entries);
            }
        }

        private static async Task EnsureCachedAsync(
            RemoteRangeReader ranges,
            long offset,
            long length,
            CancellationToken cancellationToken)
        {
            if (offset < 0 || length <= 0 || offset > ranges.PackageLength || length > ranges.PackageLength - offset)
            {
                throw new InvalidDataException("远程 MSIX bootstrap Range 越界。");
            }
            long cursor = offset;
            long remaining = length;
            while (remaining > 0)
            {
                int chunk = checked((int)Math.Min(RemoteRangeReader.MaximumSingleRangeLength, remaining));
                await ranges.ReadRangeAsync(cursor, chunk, true, cancellationToken).ConfigureAwait(false);
                cursor += chunk;
                remaining -= chunk;
            }
        }
    }
}
