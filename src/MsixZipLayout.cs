using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CodexPortableManager
{
    internal sealed class MsixZipLayout
    {
        private const uint EndOfCentralDirectorySignature = 0x06054b50;
        private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
        private const uint Zip64EndOfCentralDirectoryLocatorSignature = 0x07064b50;
        private const uint CentralDirectoryHeaderSignature = 0x02014b50;
        private const uint LocalFileHeaderSignature = 0x04034b50;
        private const uint DataDescriptorSignature = 0x08074b50;
        private const ushort Zip64ExtraFieldId = 0x0001;
        private const ushort Utf8Flag = 0x0800;
        private const ushort DataDescriptorFlag = 0x0008;
        private const ushort EncryptedFlag = 0x0001;
        private const ushort PatchedDataFlag = 0x0020;
        private const ushort StrongEncryptionFlag = 0x0040;
        private const ushort MaskedHeaderFlag = 0x2000;
        private const long MaximumCentralDirectorySize = 64L * 1024 * 1024;
        private const long MaximumBlockMapSize = 32L * 1024 * 1024;
        private const int MaximumEntryCount = 100000;
        private const int MaximumPathBytes = 4096;
        private const int MaximumNormalizedPathLength = 4096;
        private const int MaximumBlockCount = 4000000;
        private const int BlockSize = 64 * 1024;
        private const string BlockMapName = "AppxBlockMap.xml";
        private const string BlockMapNamespace = "http://schemas.microsoft.com/appx/2010/blockmap";
        private const string Sha256HashMethod = "http://www.w3.org/2001/04/xmlenc#sha256";

        private readonly Dictionary<string, MsixZipEntry> entriesByName;
        private readonly Dictionary<string, MsixBlockMapFile> blockMapFilesByName;

        private MsixZipLayout(
            string packagePath,
            long packageLength,
            long centralDirectoryOffset,
            long centralDirectorySize,
            long endRecordsOffset,
            IList<MsixZipEntry> entries,
            IDictionary<string, MsixBlockMapFile> blockMapFiles,
            bool isRemote)
        {
            PackagePath = packagePath;
            PackageLength = packageLength;
            CentralDirectoryOffset = centralDirectoryOffset;
            CentralDirectorySize = centralDirectorySize;
            EndRecordsOffset = endRecordsOffset;
            Entries = entries;
            PhysicalEntries = entries.OrderBy(value => value.LocalHeaderOffset).ToArray();
            entriesByName = entries.ToDictionary(value => value.CanonicalName, StringComparer.OrdinalIgnoreCase);
            blockMapFilesByName = new Dictionary<string, MsixBlockMapFile>(blockMapFiles, StringComparer.OrdinalIgnoreCase);
            IsRemote = isRemote;
        }

        internal string PackagePath { get; private set; }
        internal long PackageLength { get; private set; }
        internal long CentralDirectoryOffset { get; private set; }
        internal long CentralDirectorySize { get; private set; }
        internal long EndRecordsOffset { get; private set; }
        internal bool IsRemote { get; private set; }
        internal IList<MsixZipEntry> Entries { get; private set; }
        internal IList<MsixZipEntry> PhysicalEntries { get; private set; }
        internal IDictionary<string, MsixBlockMapFile> BlockMapFiles { get { return blockMapFilesByName; } }

        internal static MsixZipLayout Read(string packagePath)
        {
            if (string.IsNullOrWhiteSpace(packagePath)) throw new ArgumentException("MSIX 路径不能为空。", nameof(packagePath));
            string fullPath = Path.GetFullPath(packagePath);
            using (FileStream stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.RandomAccess))
            {
                if (stream.Length < 22)
                {
                    throw new InvalidDataException("MSIX 文件过短，不包含有效 ZIP 结束记录。");
                }

                MsixZipDirectoryInfo directory = ReadDirectoryInfo(stream);
                List<MsixZipEntry> entries = ReadCentralDirectory(stream, directory);
                ValidateLocalRecords(stream, directory, entries);
                Dictionary<string, MsixBlockMapFile> blockMapFiles = ReadBlockMap(stream, entries);
                ValidateBlockMap(stream, entries, blockMapFiles);
                return new MsixZipLayout(
                    fullPath,
                    stream.Length,
                    directory.CentralDirectoryOffset,
                    directory.CentralDirectorySize,
                    directory.EndRecordsOffset,
                    entries,
                    blockMapFiles,
                    false);
            }
        }

        internal static MsixZipLayout CompleteRemoteRead(
            string packageIdentity,
            long packageLength,
            Stream cachedRanges,
            MsixZipDirectoryInfo directory,
            List<MsixZipEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(packageIdentity)) throw new ArgumentException("远程 MSIX 标识不能为空。", nameof(packageIdentity));
            if (cachedRanges == null) throw new ArgumentNullException(nameof(cachedRanges));
            if (directory == null) throw new ArgumentNullException(nameof(directory));
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            if (cachedRanges.Length != packageLength || packageLength <= 0)
            {
                throw new InvalidDataException("远程 MSIX 长度与目录元数据不一致。");
            }

            MsixZipEntry[] physicalEntries = entries.OrderBy(value => value.LocalHeaderOffset).ToArray();
            ValidatePhysicalOffsets(physicalEntries, directory.CentralDirectoryOffset);
            MsixZipEntry blockMapEntry = entries.SingleOrDefault(value =>
                string.Equals(value.CanonicalName, BlockMapName, StringComparison.OrdinalIgnoreCase));
            if (blockMapEntry == null)
            {
                throw new InvalidDataException("远程 MSIX 缺少唯一 AppxBlockMap.xml。");
            }
            int blockMapIndex = Array.IndexOf(physicalEntries, blockMapEntry);
            long blockMapRecordEnd = blockMapIndex + 1 < physicalEntries.Length
                ? physicalEntries[blockMapIndex + 1].LocalHeaderOffset
                : directory.CentralDirectoryOffset;
            ValidateLocalRecord(cachedRanges, blockMapEntry, blockMapRecordEnd);

            Dictionary<string, MsixBlockMapFile> blockMapFiles = ReadBlockMap(cachedRanges, entries);
            foreach (int index in Enumerable.Range(0, physicalEntries.Length))
            {
                MsixZipEntry entry = physicalEntries[index];
                long recordEnd = index + 1 < physicalEntries.Length
                    ? physicalEntries[index + 1].LocalHeaderOffset
                    : directory.CentralDirectoryOffset;
                if (ReferenceEquals(entry, blockMapEntry)) continue;

                MsixBlockMapFile file;
                if (!blockMapFiles.TryGetValue(entry.CanonicalName, out file))
                {
                    entry.LocalHeaderLength = 0;
                    entry.DataOffset = entry.LocalHeaderOffset;
                    entry.DataDescriptorOffset = entry.LocalHeaderOffset;
                    entry.DataDescriptorLength = 0;
                    entry.RecordEndOffset = recordEnd;
                    continue;
                }

                if (file.LocalHeaderSize < 30 || file.LocalHeaderSize > 30L + ushort.MaxValue + ushort.MaxValue)
                {
                    throw new InvalidDataException("BlockMap 本地头大小超出 ZIP 格式范围：" + file.OriginalName);
                }
                long dataOffset = CheckedAdd(entry.LocalHeaderOffset, file.LocalHeaderSize, "远程 MSIX 本地头范围溢出。");
                long dataEnd = CheckedAdd(dataOffset, entry.CompressedSize, "远程 MSIX 条目数据范围溢出。");
                if (dataEnd > recordEnd)
                {
                    throw new InvalidDataException("远程 MSIX 条目压缩数据与后续记录重叠：" + entry.OriginalName);
                }
                int descriptorLength = checked((int)(recordEnd - dataEnd));
                bool hasDescriptor = (entry.Flags & DataDescriptorFlag) != 0;
                if ((hasDescriptor && descriptorLength != 12 && descriptorLength != 16 &&
                     descriptorLength != 20 && descriptorLength != 24) ||
                    (!hasDescriptor && descriptorLength != 0))
                {
                    throw new InvalidDataException("远程 MSIX 数据描述符布局无效：" + entry.OriginalName);
                }
                entry.LocalHeaderLength = file.LocalHeaderSize;
                entry.DataOffset = dataOffset;
                entry.DataDescriptorOffset = dataEnd;
                entry.DataDescriptorLength = descriptorLength;
                entry.RecordEndOffset = recordEnd;
            }

            ValidateBlockMap(null, entries, blockMapFiles);
            return new MsixZipLayout(
                packageIdentity,
                packageLength,
                directory.CentralDirectoryOffset,
                directory.CentralDirectorySize,
                directory.EndRecordsOffset,
                entries,
                blockMapFiles,
                true);
        }

        internal bool TryGetEntry(string canonicalName, out MsixZipEntry entry)
        {
            return entriesByName.TryGetValue(canonicalName, out entry);
        }

        internal bool TryGetBlockMapFile(string canonicalName, out MsixBlockMapFile file)
        {
            return blockMapFilesByName.TryGetValue(canonicalName, out file);
        }

        internal static string NormalizePackagePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("MSIX 包含空路径。");
            }

            string decoded = DecodePercentEscapes(value);
            string normalized = decoded.Replace('\\', '/');
            if (normalized.Length == 0 || normalized.Length > MaximumNormalizedPathLength ||
                normalized[0] == '/' || normalized[normalized.Length - 1] == '/')
            {
                throw new InvalidDataException("MSIX 包含无效路径：" + value);
            }

            string[] segments = normalized.Split('/');
            foreach (string segment in segments)
            {
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    throw new InvalidDataException("MSIX 包含越界或歧义路径：" + value);
                }
                foreach (char character in segment)
                {
                    if (character < 0x20 || character == '<' || character == '>' || character == ':' ||
                        character == '"' || character == '|' || character == '?' || character == '*')
                    {
                        throw new InvalidDataException("MSIX 包含 Windows 不支持的路径字符：" + value);
                    }
                }
            }
            return string.Join("/", segments);
        }

        internal static MsixZipDirectoryInfo ReadDirectoryInfo(Stream stream)
        {
            int tailLength = checked((int)Math.Min(stream.Length, 65557L));
            byte[] tail = ReadBytes(stream, stream.Length - tailLength, tailLength);
            int eocdIndex = -1;
            for (int index = tail.Length - 22; index >= 0; index--)
            {
                if (ReadUInt32(tail, index) != EndOfCentralDirectorySignature) continue;
                ushort commentLength = ReadUInt16(tail, index + 20);
                if (index + 22 + commentLength == tail.Length)
                {
                    eocdIndex = index;
                    break;
                }
            }
            if (eocdIndex < 0)
            {
                throw new InvalidDataException("MSIX 缺少有效 ZIP 结束记录，或记录后存在未声明数据。");
            }

            long eocdOffset = checked(stream.Length - tailLength + eocdIndex);
            ushort diskNumber = ReadUInt16(tail, eocdIndex + 4);
            ushort centralDisk = ReadUInt16(tail, eocdIndex + 6);
            ushort entriesOnDisk16 = ReadUInt16(tail, eocdIndex + 8);
            ushort entryCount16 = ReadUInt16(tail, eocdIndex + 10);
            uint centralSize32 = ReadUInt32(tail, eocdIndex + 12);
            uint centralOffset32 = ReadUInt32(tail, eocdIndex + 16);
            bool needsZip64 = entriesOnDisk16 == ushort.MaxValue || entryCount16 == ushort.MaxValue ||
                centralSize32 == uint.MaxValue || centralOffset32 == uint.MaxValue;

            long entryCount;
            long centralSize;
            long centralOffset;
            long endRecordsOffset;
            if (needsZip64)
            {
                long locatorOffset = checked(eocdOffset - 20);
                if (locatorOffset < 0)
                {
                    throw new InvalidDataException("ZIP64 结束记录定位器缺失。");
                }
                byte[] locator = ReadBytes(stream, locatorOffset, 20);
                if (ReadUInt32(locator, 0) != Zip64EndOfCentralDirectoryLocatorSignature ||
                    ReadUInt32(locator, 4) != 0 || ReadUInt32(locator, 16) != 1)
                {
                    throw new InvalidDataException("ZIP64 结束记录定位器无效或使用了多磁盘布局。");
                }

                long zip64Offset = ToInt64(ReadUInt64(locator, 8), "ZIP64 结束记录偏移超出支持范围。");
                byte[] zip64Header = ReadBytes(stream, zip64Offset, 56);
                if (ReadUInt32(zip64Header, 0) != Zip64EndOfCentralDirectorySignature)
                {
                    throw new InvalidDataException("ZIP64 结束记录签名无效。");
                }
                long recordSize = ToInt64(ReadUInt64(zip64Header, 4), "ZIP64 结束记录长度超出支持范围。");
                if (recordSize < 44 || checked(zip64Offset + 12 + recordSize) != locatorOffset)
                {
                    throw new InvalidDataException("ZIP64 结束记录长度或位置无效。");
                }
                if (ReadUInt32(zip64Header, 16) != 0 || ReadUInt32(zip64Header, 20) != 0)
                {
                    throw new InvalidDataException("不支持多磁盘 ZIP64 MSIX。");
                }
                long entriesOnDisk = ToInt64(ReadUInt64(zip64Header, 24), "ZIP64 条目数超出支持范围。");
                entryCount = ToInt64(ReadUInt64(zip64Header, 32), "ZIP64 条目数超出支持范围。");
                if (entriesOnDisk != entryCount)
                {
                    throw new InvalidDataException("ZIP64 中央目录跨磁盘，不受支持。");
                }
                centralSize = ToInt64(ReadUInt64(zip64Header, 40), "ZIP64 中央目录过大。");
                centralOffset = ToInt64(ReadUInt64(zip64Header, 48), "ZIP64 中央目录偏移超出支持范围。");
                endRecordsOffset = zip64Offset;
            }
            else
            {
                if (diskNumber != 0 || centralDisk != 0 || entriesOnDisk16 != entryCount16)
                {
                    throw new InvalidDataException("不支持多磁盘 ZIP MSIX。");
                }
                entryCount = entryCount16;
                centralSize = centralSize32;
                centralOffset = centralOffset32;
                endRecordsOffset = eocdOffset;
            }

            if (entryCount <= 0 || entryCount > MaximumEntryCount)
            {
                throw new InvalidDataException("MSIX 中央目录条目数超出限制：" + entryCount.ToString(CultureInfo.InvariantCulture));
            }
            if (centralSize <= 0 || centralSize > MaximumCentralDirectorySize)
            {
                throw new InvalidDataException("MSIX 中央目录大小超出限制：" + centralSize.ToString(CultureInfo.InvariantCulture));
            }
            long centralEnd = CheckedAdd(centralOffset, centralSize, "MSIX 中央目录范围溢出。");
            if (centralOffset < 0 || centralEnd != endRecordsOffset || centralEnd > stream.Length)
            {
                throw new InvalidDataException("MSIX 中央目录范围与结束记录不连续。");
            }

            return new MsixZipDirectoryInfo
            {
                EntryCount = checked((int)entryCount),
                CentralDirectoryOffset = centralOffset,
                CentralDirectorySize = centralSize,
                EndRecordsOffset = endRecordsOffset
            };
        }

        internal static List<MsixZipEntry> ReadCentralDirectory(Stream stream, MsixZipDirectoryInfo directory)
        {
            List<MsixZipEntry> entries = new List<MsixZipEntry>(directory.EntryCount);
            Dictionary<string, string> canonicalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long cursor = directory.CentralDirectoryOffset;
            long centralEnd = checked(directory.CentralDirectoryOffset + directory.CentralDirectorySize);
            for (int index = 0; index < directory.EntryCount; index++)
            {
                byte[] header = ReadBytes(stream, cursor, 46);
                if (ReadUInt32(header, 0) != CentralDirectoryHeaderSignature)
                {
                    throw new InvalidDataException("MSIX 中央目录条目签名无效，索引=" + index.ToString(CultureInfo.InvariantCulture));
                }

                ushort flags = ReadUInt16(header, 8);
                ushort compressionMethod = ReadUInt16(header, 10);
                ValidateCompression(flags, compressionMethod);
                ushort nameLength = ReadUInt16(header, 28);
                ushort extraLength = ReadUInt16(header, 30);
                ushort commentLength = ReadUInt16(header, 32);
                if (nameLength == 0 || nameLength > MaximumPathBytes)
                {
                    throw new InvalidDataException("MSIX 中央目录路径长度无效。");
                }
                long recordLength = checked(46L + nameLength + extraLength + commentLength);
                if (CheckedAdd(cursor, recordLength, "MSIX 中央目录条目长度溢出。") > centralEnd)
                {
                    throw new InvalidDataException("MSIX 中央目录条目越界。");
                }

                byte[] nameBytes = ReadBytes(stream, cursor + 46, nameLength);
                byte[] extra = ReadBytes(stream, cursor + 46 + nameLength, extraLength);
                string originalName = DecodeEntryName(nameBytes, flags);
                string canonicalName = NormalizePackagePath(originalName);
                string previousName;
                if (canonicalNames.TryGetValue(canonicalName, out previousName))
                {
                    throw new InvalidDataException("MSIX 包含重复、大小写冲突或编码歧义路径：" + previousName + " / " + originalName);
                }
                canonicalNames.Add(canonicalName, originalName);

                ulong uncompressedSize = ReadUInt32(header, 24);
                ulong compressedSize = ReadUInt32(header, 20);
                ulong localHeaderOffset = ReadUInt32(header, 42);
                bool needsUncompressed = uncompressedSize == uint.MaxValue;
                bool needsCompressed = compressedSize == uint.MaxValue;
                bool needsOffset = localHeaderOffset == uint.MaxValue;
                bool needsDisk = ReadUInt16(header, 34) == ushort.MaxValue;
                uint diskStart = ReadUInt16(header, 34);
                if (needsUncompressed || needsCompressed || needsOffset || needsDisk)
                {
                    ReadZip64Extra(
                        extra,
                        needsUncompressed,
                        needsCompressed,
                        needsOffset,
                        needsDisk,
                        ref uncompressedSize,
                        ref compressedSize,
                        ref localHeaderOffset,
                        ref diskStart);
                }
                if (diskStart != 0)
                {
                    throw new InvalidDataException("MSIX 条目位于非零磁盘，不受支持：" + originalName);
                }

                entries.Add(new MsixZipEntry
                {
                    OriginalName = originalName,
                    CanonicalName = canonicalName,
                    NameBytes = nameBytes,
                    Flags = flags,
                    CompressionMethod = compressionMethod,
                    LastModTime = ReadUInt16(header, 12),
                    LastModDate = ReadUInt16(header, 14),
                    Crc32 = ReadUInt32(header, 16),
                    CompressedSize = ToInt64(compressedSize, "MSIX 条目压缩大小超出支持范围：" + originalName),
                    UncompressedSize = ToInt64(uncompressedSize, "MSIX 条目大小超出支持范围：" + originalName),
                    LocalHeaderOffset = ToInt64(localHeaderOffset, "MSIX 本地头偏移超出支持范围：" + originalName),
                    CentralDirectoryOffset = cursor,
                    CentralDirectoryLength = recordLength
                });
                cursor = checked(cursor + recordLength);
            }

            if (cursor != centralEnd)
            {
                throw new InvalidDataException("MSIX 中央目录包含未解析或多余记录。");
            }
            return entries;
        }

        private static void ValidateLocalRecords(Stream stream, MsixZipDirectoryInfo directory, List<MsixZipEntry> entries)
        {
            MsixZipEntry[] physicalEntries = entries.OrderBy(value => value.LocalHeaderOffset).ToArray();
            ValidatePhysicalOffsets(physicalEntries, directory.CentralDirectoryOffset);
            for (int index = 0; index < physicalEntries.Length; index++)
            {
                MsixZipEntry entry = physicalEntries[index];
                long recordEnd = index + 1 < physicalEntries.Length
                    ? physicalEntries[index + 1].LocalHeaderOffset
                    : directory.CentralDirectoryOffset;
                ValidateLocalRecord(stream, entry, recordEnd);
            }
        }

        private static void ValidatePhysicalOffsets(MsixZipEntry[] physicalEntries, long centralDirectoryOffset)
        {
            long previousOffset = -1;
            foreach (MsixZipEntry entry in physicalEntries)
            {
                if (entry.LocalHeaderOffset < 0 || entry.LocalHeaderOffset >= centralDirectoryOffset ||
                    entry.LocalHeaderOffset == previousOffset)
                {
                    throw new InvalidDataException("MSIX 本地条目偏移重复或越界：" + entry.OriginalName);
                }
                previousOffset = entry.LocalHeaderOffset;
            }
        }

        private static void ValidateLocalRecord(Stream stream, MsixZipEntry entry, long recordEnd)
        {
            byte[] header = ReadBytes(stream, entry.LocalHeaderOffset, 30);
            if (ReadUInt32(header, 0) != LocalFileHeaderSignature)
            {
                throw new InvalidDataException("MSIX 本地文件头签名无效：" + entry.OriginalName);
            }
            ushort localFlags = ReadUInt16(header, 6);
            ushort localMethod = ReadUInt16(header, 8);
            if (localFlags != entry.Flags || localMethod != entry.CompressionMethod ||
                ReadUInt16(header, 10) != entry.LastModTime || ReadUInt16(header, 12) != entry.LastModDate)
            {
                throw new InvalidDataException("MSIX 本地文件头与中央目录元数据不一致：" + entry.OriginalName);
            }
            ushort nameLength = ReadUInt16(header, 26);
            ushort extraLength = ReadUInt16(header, 28);
            if (nameLength == 0 || nameLength > MaximumPathBytes)
            {
                throw new InvalidDataException("MSIX 本地文件头路径长度无效：" + entry.OriginalName);
            }
            byte[] localName = ReadBytes(stream, entry.LocalHeaderOffset + 30, nameLength);
            if (!BytesEqual(localName, entry.NameBytes))
            {
                throw new InvalidDataException("MSIX 本地文件头路径与中央目录不一致：" + entry.OriginalName);
            }

            long localHeaderLength = checked(30L + nameLength + extraLength);
            long dataOffset = CheckedAdd(entry.LocalHeaderOffset, localHeaderLength, "MSIX 本地文件头范围溢出。");
            long dataEnd = CheckedAdd(dataOffset, entry.CompressedSize, "MSIX 条目数据范围溢出。");
            if (dataEnd > recordEnd)
            {
                throw new InvalidDataException("MSIX 条目压缩数据与后续记录重叠：" + entry.OriginalName);
            }

            int descriptorLength = checked((int)(recordEnd - dataEnd));
            if ((entry.Flags & DataDescriptorFlag) != 0)
            {
                ValidateDeferredLocalFields(entry, header);
                ValidateDataDescriptor(stream, entry, dataEnd, descriptorLength);
            }
            else
            {
                if (descriptorLength != 0)
                {
                    throw new InvalidDataException("MSIX 本地条目后存在未声明数据：" + entry.OriginalName);
                }
                ValidateLocalSizes(stream, entry, header, extraLength);
            }

            entry.LocalHeaderLength = localHeaderLength;
            entry.DataOffset = dataOffset;
            entry.DataDescriptorOffset = dataEnd;
            entry.DataDescriptorLength = descriptorLength;
            entry.RecordEndOffset = recordEnd;
        }

        private static void ValidateDeferredLocalFields(MsixZipEntry entry, byte[] header)
        {
            uint localCrc = ReadUInt32(header, 14);
            uint localCompressedSize = ReadUInt32(header, 18);
            uint localUncompressedSize = ReadUInt32(header, 22);
            bool compressedSizeValid = localCompressedSize == 0 || localCompressedSize == uint.MaxValue ||
                (entry.CompressedSize <= uint.MaxValue && localCompressedSize == entry.CompressedSize);
            bool uncompressedSizeValid = localUncompressedSize == 0 || localUncompressedSize == uint.MaxValue ||
                (entry.UncompressedSize <= uint.MaxValue && localUncompressedSize == entry.UncompressedSize);
            if ((localCrc != 0 && localCrc != entry.Crc32) || !compressedSizeValid || !uncompressedSizeValid)
            {
                throw new InvalidDataException("MSIX 延迟数据描述符的本地头占位字段无效：" + entry.OriginalName);
            }
        }

        private static void ValidateLocalSizes(Stream stream, MsixZipEntry entry, byte[] header, int extraLength)
        {
            ulong compressedSize = ReadUInt32(header, 18);
            ulong uncompressedSize = ReadUInt32(header, 22);
            if (compressedSize == uint.MaxValue || uncompressedSize == uint.MaxValue)
            {
                byte[] extra = ReadBytes(stream, entry.LocalHeaderOffset + 30 + entry.NameBytes.Length, extraLength);
                ulong unusedOffset = 0;
                uint unusedDisk = 0;
                ReadZip64Extra(
                    extra,
                    uncompressedSize == uint.MaxValue,
                    compressedSize == uint.MaxValue,
                    false,
                    false,
                    ref uncompressedSize,
                    ref compressedSize,
                    ref unusedOffset,
                    ref unusedDisk);
            }
            if (ReadUInt32(header, 14) != entry.Crc32 ||
                ToInt64(compressedSize, "MSIX 本地压缩大小超出支持范围。") != entry.CompressedSize ||
                ToInt64(uncompressedSize, "MSIX 本地文件大小超出支持范围。") != entry.UncompressedSize)
            {
                throw new InvalidDataException("MSIX 本地文件头大小或 CRC 与中央目录不一致：" + entry.OriginalName);
            }
        }

        private static void ValidateDataDescriptor(
            Stream stream,
            MsixZipEntry entry,
            long descriptorOffset,
            int descriptorLength)
        {
            bool zip64 = descriptorLength == 20 || descriptorLength == 24;
            int bodyLength = zip64 ? 20 : 12;
            if ((descriptorLength != bodyLength && descriptorLength != bodyLength + 4) ||
                (!zip64 && (entry.CompressedSize > uint.MaxValue || entry.UncompressedSize > uint.MaxValue)))
            {
                throw new InvalidDataException("MSIX 数据描述符长度无效：" + entry.OriginalName);
            }
            byte[] descriptor = ReadBytes(stream, descriptorOffset, descriptorLength);
            int cursor = 0;
            if (descriptorLength == bodyLength + 4)
            {
                if (ReadUInt32(descriptor, 0) != DataDescriptorSignature)
                {
                    throw new InvalidDataException("MSIX 数据描述符签名无效：" + entry.OriginalName);
                }
                cursor = 4;
            }
            if (ReadUInt32(descriptor, cursor) != entry.Crc32)
            {
                throw new InvalidDataException("MSIX 数据描述符 CRC 不一致：" + entry.OriginalName);
            }
            cursor += 4;
            long compressedSize = zip64
                ? ToInt64(ReadUInt64(descriptor, cursor), "MSIX 数据描述符压缩大小超出支持范围。")
                : ReadUInt32(descriptor, cursor);
            cursor += zip64 ? 8 : 4;
            long uncompressedSize = zip64
                ? ToInt64(ReadUInt64(descriptor, cursor), "MSIX 数据描述符文件大小超出支持范围。")
                : ReadUInt32(descriptor, cursor);
            if (compressedSize != entry.CompressedSize || uncompressedSize != entry.UncompressedSize)
            {
                throw new InvalidDataException("MSIX 数据描述符大小与中央目录不一致：" + entry.OriginalName);
            }
        }

        private static Dictionary<string, MsixBlockMapFile> ReadBlockMap(Stream stream, List<MsixZipEntry> entries)
        {
            MsixZipEntry blockMapEntry = entries.SingleOrDefault(value =>
                string.Equals(value.CanonicalName, BlockMapName, StringComparison.OrdinalIgnoreCase));
            if (blockMapEntry == null)
            {
                throw new InvalidDataException("MSIX 缺少唯一 AppxBlockMap.xml。");
            }
            if (blockMapEntry.UncompressedSize <= 0 || blockMapEntry.UncompressedSize > MaximumBlockMapSize)
            {
                throw new InvalidDataException("AppxBlockMap.xml 解压大小超出限制。");
            }

            byte[] xmlBytes = ReadEntryContents(stream, blockMapEntry);
            XmlReaderSettings settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumBlockMapSize,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };
            XDocument document;
            using (MemoryStream xmlStream = new MemoryStream(xmlBytes, false))
            using (XmlReader reader = XmlReader.Create(xmlStream, settings))
            {
                document = XDocument.Load(reader, LoadOptions.None);
            }

            XNamespace ns = BlockMapNamespace;
            XElement root = document.Root;
            if (root == null || root.Name != ns + "BlockMap" ||
                !string.Equals((string)root.Attribute("HashMethod"), Sha256HashMethod, StringComparison.Ordinal))
            {
                throw new InvalidDataException("AppxBlockMap.xml 根元素或哈希算法无效。");
            }

            Dictionary<string, MsixBlockMapFile> files = new Dictionary<string, MsixBlockMapFile>(StringComparer.OrdinalIgnoreCase);
            int totalBlocks = 0;
            foreach (XElement fileElement in root.Elements(ns + "File"))
            {
                string originalName = RequireAttribute(fileElement, "Name");
                string canonicalName = NormalizePackagePath(originalName);
                if (files.ContainsKey(canonicalName))
                {
                    throw new InvalidDataException("AppxBlockMap.xml 包含重复或大小写冲突路径：" + originalName);
                }
                long size = ParseNonNegativeInt64(RequireAttribute(fileElement, "Size"), "BlockMap 文件大小无效：" + originalName);
                long localHeaderSize = ParseNonNegativeInt64(RequireAttribute(fileElement, "LfhSize"), "BlockMap 本地头大小无效：" + originalName);
                List<MsixBlockMapBlock> blocks = new List<MsixBlockMapBlock>();
                foreach (XElement blockElement in fileElement.Elements(ns + "Block"))
                {
                    byte[] hash;
                    try
                    {
                        hash = Convert.FromBase64String(RequireAttribute(blockElement, "Hash"));
                    }
                    catch (FormatException exception)
                    {
                        throw new InvalidDataException("BlockMap 块哈希不是有效 Base64：" + originalName, exception);
                    }
                    if (hash.Length != 32)
                    {
                        throw new InvalidDataException("BlockMap 块哈希不是 SHA-256：" + originalName);
                    }
                    XAttribute compressedSizeAttribute = blockElement.Attribute("Size");
                    long? compressedSize = compressedSizeAttribute == null
                        ? (long?)null
                        : ParseNonNegativeInt64(compressedSizeAttribute.Value, "BlockMap 块压缩大小无效：" + originalName);
                    blocks.Add(new MsixBlockMapBlock(hash, compressedSize));
                    totalBlocks++;
                    if (totalBlocks > MaximumBlockCount)
                    {
                        throw new InvalidDataException("AppxBlockMap.xml 块数量超出限制。");
                    }
                }
                long expectedBlocks = size == 0 ? 0 : checked(((size - 1) / BlockSize) + 1);
                if (blocks.Count != expectedBlocks)
                {
                    throw new InvalidDataException("BlockMap 块数量与文件大小不一致：" + originalName);
                }
                files.Add(canonicalName, new MsixBlockMapFile(originalName, canonicalName, size, localHeaderSize, blocks));
            }
            if (files.Count == 0)
            {
                throw new InvalidDataException("AppxBlockMap.xml 不包含文件记录。");
            }
            return files;
        }

        private static void ValidateBlockMap(
            Stream stream,
            List<MsixZipEntry> entries,
            Dictionary<string, MsixBlockMapFile> blockMapFiles)
        {
            Dictionary<string, MsixZipEntry> byName = entries.ToDictionary(value => value.CanonicalName, StringComparer.OrdinalIgnoreCase);
            foreach (MsixBlockMapFile file in blockMapFiles.Values)
            {
                MsixZipEntry entry;
                if (!byName.TryGetValue(file.CanonicalName, out entry))
                {
                    throw new InvalidDataException("BlockMap 文件在 ZIP 中不存在：" + file.OriginalName);
                }
                if (file.Size != entry.UncompressedSize || file.LocalHeaderSize != entry.LocalHeaderLength)
                {
                    throw new InvalidDataException("BlockMap 文件大小或本地头大小与 ZIP 不一致：" + file.OriginalName);
                }

                bool hasCompressedSizes = file.Blocks.Any(value => value.CompressedSize.HasValue);
                bool missingCompressedSizes = file.Blocks.Any(value => !value.CompressedSize.HasValue);
                if (hasCompressedSizes && missingCompressedSizes)
                {
                    throw new InvalidDataException("BlockMap 块压缩大小记录不完整：" + file.OriginalName);
                }
                if (entry.CompressionMethod == 8 && file.Blocks.Count > 0 && !hasCompressedSizes)
                {
                    throw new InvalidDataException("Deflate 条目缺少 BlockMap 块压缩大小：" + file.OriginalName);
                }
                if (hasCompressedSizes)
                {
                    long compressedTotal = 0;
                    foreach (MsixBlockMapBlock block in file.Blocks)
                    {
                        compressedTotal = CheckedAdd(compressedTotal, block.CompressedSize.Value, "BlockMap 压缩大小总和溢出。");
                    }
                    if (compressedTotal != entry.CompressedSize)
                    {
                        if (entry.CompressionMethod != 8 ||
                            checked(compressedTotal + 2) != entry.CompressedSize ||
                            (stream != null && !HasMsixDeflateTerminator(stream, entry)))
                        {
                            throw new InvalidDataException("BlockMap 块压缩大小总和与 ZIP 不一致：" + file.OriginalName);
                        }
                    }
                }
                else if (entry.CompressionMethod == 0 && entry.CompressedSize != entry.UncompressedSize)
                {
                    throw new InvalidDataException("Stored 条目的压缩大小与原始大小不一致：" + file.OriginalName);
                }
            }
        }

        private static bool HasMsixDeflateTerminator(Stream stream, MsixZipEntry entry)
        {
            if (entry.CompressedSize < 2) return false;
            byte[] terminator = ReadBytes(stream, entry.DataOffset + entry.CompressedSize - 2, 2);
            return terminator[0] == 0x03 && terminator[1] == 0x00;
        }

        private static byte[] ReadEntryContents(Stream stream, MsixZipEntry entry)
        {
            if (entry.UncompressedSize > MaximumBlockMapSize)
            {
                throw new InvalidDataException("MSIX 条目解压大小超出限制：" + entry.OriginalName);
            }
            stream.Position = entry.DataOffset;
            using (LimitedReadStream compressed = new LimitedReadStream(stream, entry.CompressedSize))
            using (Stream contents = entry.CompressionMethod == 0
                ? (Stream)compressed
                : new DeflateStream(compressed, CompressionMode.Decompress, true))
            using (MemoryStream output = new MemoryStream(checked((int)entry.UncompressedSize)))
            {
                byte[] buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = contents.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total = CheckedAdd(total, read, "MSIX 条目解压大小溢出。");
                    if (total > entry.UncompressedSize || total > MaximumBlockMapSize)
                    {
                        throw new InvalidDataException("MSIX 条目实际解压大小超出声明：" + entry.OriginalName);
                    }
                    output.Write(buffer, 0, read);
                }
                if (total != entry.UncompressedSize)
                {
                    throw new InvalidDataException("MSIX 条目实际解压大小与中央目录不一致：" + entry.OriginalName);
                }
                return output.ToArray();
            }
        }

        private static void ValidateCompression(ushort flags, ushort method)
        {
            ushort rejectedFlags = EncryptedFlag | PatchedDataFlag | StrongEncryptionFlag | MaskedHeaderFlag;
            ushort allowedFlags = 0x0006 | DataDescriptorFlag | Utf8Flag;
            if ((flags & rejectedFlags) != 0 || (flags & ~allowedFlags) != 0)
            {
                throw new InvalidDataException("MSIX ZIP 条目使用了不支持或不安全的 flags：0x" + flags.ToString("X4", CultureInfo.InvariantCulture));
            }
            if (method != 0 && method != 8)
            {
                throw new InvalidDataException("MSIX ZIP 条目使用了不支持的压缩方法：" + method.ToString(CultureInfo.InvariantCulture));
            }
            if (method == 0 && (flags & 0x0006) != 0)
            {
                throw new InvalidDataException("Stored ZIP 条目包含无效压缩选项 flags。");
            }
        }

        private static void ReadZip64Extra(
            byte[] extra,
            bool needsUncompressed,
            bool needsCompressed,
            bool needsOffset,
            bool needsDisk,
            ref ulong uncompressedSize,
            ref ulong compressedSize,
            ref ulong localHeaderOffset,
            ref uint diskStart)
        {
            int cursor = 0;
            while (cursor + 4 <= extra.Length)
            {
                ushort id = ReadUInt16(extra, cursor);
                int length = ReadUInt16(extra, cursor + 2);
                cursor += 4;
                if (cursor + length > extra.Length)
                {
                    throw new InvalidDataException("ZIP extra field 长度越界。");
                }
                if (id == Zip64ExtraFieldId)
                {
                    int fieldEnd = cursor + length;
                    if (needsUncompressed) uncompressedSize = ReadZip64UInt64(extra, ref cursor, fieldEnd);
                    if (needsCompressed) compressedSize = ReadZip64UInt64(extra, ref cursor, fieldEnd);
                    if (needsOffset) localHeaderOffset = ReadZip64UInt64(extra, ref cursor, fieldEnd);
                    if (needsDisk)
                    {
                        if (cursor + 4 > fieldEnd) throw new InvalidDataException("ZIP64 extra field 缺少磁盘编号。");
                        diskStart = ReadUInt32(extra, cursor);
                    }
                    return;
                }
                cursor += length;
            }
            throw new InvalidDataException("ZIP64 条目缺少必要 ZIP64 extra field。");
        }

        private static ulong ReadZip64UInt64(byte[] bytes, ref int cursor, int end)
        {
            if (cursor + 8 > end) throw new InvalidDataException("ZIP64 extra field 长度不足。");
            ulong value = ReadUInt64(bytes, cursor);
            cursor += 8;
            return value;
        }

        private static string DecodeEntryName(byte[] nameBytes, ushort flags)
        {
            try
            {
                Encoding encoding = (flags & Utf8Flag) != 0
                    ? (Encoding)new UTF8Encoding(false, true)
                    : Encoding.GetEncoding(437, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return encoding.GetString(nameBytes);
            }
            catch (Exception exception) when (exception is DecoderFallbackException || exception is ArgumentException)
            {
                throw new InvalidDataException("MSIX ZIP 条目路径编码无效。", exception);
            }
        }

        private static string DecodePercentEscapes(string value)
        {
            if (value.IndexOf('%') < 0) return value;
            StringBuilder decoded = new StringBuilder(value.Length);
            UTF8Encoding utf8 = new UTF8Encoding(false, true);
            for (int index = 0; index < value.Length;)
            {
                if (value[index] != '%')
                {
                    decoded.Append(value[index++]);
                    continue;
                }
                List<byte> bytes = new List<byte>();
                while (index < value.Length && value[index] == '%')
                {
                    if (index + 2 >= value.Length || !IsHex(value[index + 1]) || !IsHex(value[index + 2]))
                    {
                        throw new InvalidDataException("MSIX 路径包含无效百分号编码：" + value);
                    }
                    bytes.Add((byte)((HexValue(value[index + 1]) << 4) | HexValue(value[index + 2])));
                    index += 3;
                }
                try
                {
                    decoded.Append(utf8.GetString(bytes.ToArray()));
                }
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidDataException("MSIX 路径包含无效 UTF-8 百分号编码：" + value, exception);
                }
            }
            return decoded.ToString();
        }

        private static bool IsHex(char value)
        {
            return (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f') || (value >= 'A' && value <= 'F');
        }

        private static int HexValue(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            return value - 'A' + 10;
        }

        private static string RequireAttribute(XElement element, string name)
        {
            XAttribute attribute = element.Attribute(name);
            if (attribute == null || string.IsNullOrWhiteSpace(attribute.Value))
            {
                throw new InvalidDataException("AppxBlockMap.xml 缺少属性：" + name);
            }
            return attribute.Value;
        }

        private static long ParseNonNegativeInt64(string value, string message)
        {
            long result;
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) || result < 0)
            {
                throw new InvalidDataException(message);
            }
            return result;
        }

        private static byte[] ReadBytes(Stream stream, long offset, int length)
        {
            if (offset < 0 || length < 0 || offset > stream.Length || length > stream.Length - offset)
            {
                throw new InvalidDataException("MSIX ZIP 读取范围越界。");
            }
            byte[] bytes = new byte[length];
            stream.Position = offset;
            int total = 0;
            while (total < length)
            {
                int read = stream.Read(bytes, total, length - total);
                if (read <= 0) throw new EndOfStreamException("读取 MSIX ZIP 记录时意外结束。");
                total += read;
            }
            return bytes;
        }

        private static bool BytesEqual(byte[] first, byte[] second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first == null || second == null || first.Length != second.Length) return false;
            for (int index = 0; index < first.Length; index++)
            {
                if (first[index] != second[index]) return false;
            }
            return true;
        }

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            if (offset < 0 || offset + 2 > bytes.Length) throw new InvalidDataException("ZIP 记录字段越界。");
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            if (offset < 0 || offset + 4 > bytes.Length) throw new InvalidDataException("ZIP 记录字段越界。");
            return (uint)(bytes[offset] |
                (bytes[offset + 1] << 8) |
                (bytes[offset + 2] << 16) |
                (bytes[offset + 3] << 24));
        }

        private static ulong ReadUInt64(byte[] bytes, int offset)
        {
            uint low = ReadUInt32(bytes, offset);
            uint high = ReadUInt32(bytes, offset + 4);
            return low | ((ulong)high << 32);
        }

        private static long ToInt64(ulong value, string message)
        {
            if (value > long.MaxValue) throw new InvalidDataException(message);
            return (long)value;
        }

        private static long CheckedAdd(long first, long second, string message)
        {
            try
            {
                return checked(first + second);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(message, exception);
            }
        }

        private sealed class LimitedReadStream : Stream
        {
            private readonly Stream inner;
            private long remaining;

            internal LimitedReadStream(Stream inner, long length)
            {
                this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
                if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
                remaining = length;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (remaining == 0) return 0;
                int requested = checked((int)Math.Min(count, remaining));
                int read = inner.Read(buffer, offset, requested);
                remaining -= read;
                return read;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }

    internal sealed class MsixZipDirectoryInfo
    {
        internal int EntryCount;
        internal long CentralDirectoryOffset;
        internal long CentralDirectorySize;
        internal long EndRecordsOffset;
    }

    internal sealed class MsixZipEntry
    {
        internal string OriginalName { get; set; }
        internal string CanonicalName { get; set; }
        internal byte[] NameBytes { get; set; }
        internal ushort Flags { get; set; }
        internal ushort CompressionMethod { get; set; }
        internal ushort LastModTime { get; set; }
        internal ushort LastModDate { get; set; }
        internal uint Crc32 { get; set; }
        internal long CompressedSize { get; set; }
        internal long UncompressedSize { get; set; }
        internal long LocalHeaderOffset { get; set; }
        internal long LocalHeaderLength { get; set; }
        internal long DataOffset { get; set; }
        internal long DataDescriptorOffset { get; set; }
        internal int DataDescriptorLength { get; set; }
        internal long RecordEndOffset { get; set; }
        internal long CentralDirectoryOffset { get; set; }
        internal long CentralDirectoryLength { get; set; }
        internal long RecordLength { get { return checked(RecordEndOffset - LocalHeaderOffset); } }
    }

    internal sealed class MsixBlockMapFile
    {
        internal MsixBlockMapFile(
            string originalName,
            string canonicalName,
            long size,
            long localHeaderSize,
            IList<MsixBlockMapBlock> blocks)
        {
            OriginalName = originalName;
            CanonicalName = canonicalName;
            Size = size;
            LocalHeaderSize = localHeaderSize;
            Blocks = blocks;
        }

        internal string OriginalName { get; private set; }
        internal string CanonicalName { get; private set; }
        internal long Size { get; private set; }
        internal long LocalHeaderSize { get; private set; }
        internal IList<MsixBlockMapBlock> Blocks { get; private set; }
    }

    internal sealed class MsixBlockMapBlock
    {
        internal MsixBlockMapBlock(byte[] hash, long? compressedSize)
        {
            Hash = hash;
            CompressedSize = compressedSize;
        }

        internal byte[] Hash { get; private set; }
        internal long? CompressedSize { get; private set; }
    }
}
