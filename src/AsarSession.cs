using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace CodexPortableManager
{
    internal sealed class AsarSession : IDisposable
    {
        private const int MaximumHeaderSize = 64 * 1024 * 1024;
        private const int CopyBufferSize = 1024 * 1024;

        private readonly string path;
        private readonly Dictionary<string, object> header;
        private readonly List<AsarArchiveEntry> entries;
        private readonly long payloadBase;
        private readonly FileIdentity sourceIdentity;
        private FileStream source;

        private AsarSession(
            string archivePath,
            Dictionary<string, object> archiveHeader,
            List<AsarArchiveEntry> archiveEntries,
            long archivePayloadBase,
            FileStream archiveSource,
            FileIdentity archiveSourceIdentity)
        {
            path = archivePath;
            header = archiveHeader;
            entries = archiveEntries;
            payloadBase = archivePayloadBase;
            source = archiveSource;
            sourceIdentity = archiveSourceIdentity;
        }

        public static string GetAsarPath(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new ArgumentException("Codex 主程序路径不能为空。", nameof(executablePath));
            }

            string executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            if (string.IsNullOrWhiteSpace(executableDirectory))
            {
                throw new InvalidDataException("无法确定 Codex 主程序所在目录：" + executablePath);
            }
            return Path.Combine(executableDirectory, "resources", "app.asar");
        }

        public static AsarSession Open(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath))
            {
                throw new ArgumentException("ASAR 路径不能为空。", nameof(archivePath));
            }

            string fullPath = Path.GetFullPath(archivePath);
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("ASAR 源文件不能是重解析点：" + fullPath);
            }

            FileStream stream = null;
            try
            {
                stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSize,
                    FileOptions.RandomAccess);
                FileIdentity identity = GetFileIdentity(stream.SafeFileHandle, fullPath);
                byte[] prefix = new byte[16];
                ReadExactly(stream, prefix, 0, prefix.Length);
                uint markerSize = BitConverter.ToUInt32(prefix, 0);
                uint headerSize = BitConverter.ToUInt32(prefix, 4);
                uint secondaryHeaderSize = BitConverter.ToUInt32(prefix, 8);
                uint jsonSize = BitConverter.ToUInt32(prefix, 12);
                if (markerSize != 4 ||
                    headerSize < 8 ||
                    secondaryHeaderSize != headerSize - 4 ||
                    jsonSize == 0 ||
                    jsonSize > headerSize - 8 ||
                    jsonSize > MaximumHeaderSize ||
                    8L + headerSize > stream.Length)
                {
                    throw new InvalidDataException("ASAR 头部大小无效或超出文件边界。");
                }

                byte[] headerBytes = new byte[jsonSize];
                ReadExactly(stream, headerBytes, 0, headerBytes.Length);
                Dictionary<string, object> archiveHeader = new JavaScriptSerializer
                {
                    MaxJsonLength = int.MaxValue,
                    RecursionLimit = 2048
                }.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(headerBytes));
                if (archiveHeader == null)
                {
                    throw new InvalidDataException("ASAR 头部 JSON 无效。");
                }

                long archivePayloadBase = 8L + headerSize;
                List<AsarArchiveEntry> archiveEntries = new List<AsarArchiveEntry>();
                LoadEntries(archiveHeader, string.Empty, archivePayloadBase, stream.Length, archiveEntries);
                ValidatePayloadLayout(archiveEntries, stream.Length - archivePayloadBase);
                return new AsarSession(
                    fullPath,
                    archiveHeader,
                    archiveEntries,
                    archivePayloadBase,
                    stream,
                    identity);
            }
            catch (EndOfStreamException exception)
            {
                if (stream != null) stream.Dispose();
                throw new InvalidDataException("ASAR 文件过短或头部被截断。", exception);
            }
            catch
            {
                if (stream != null) stream.Dispose();
                throw;
            }
        }

        public static IDictionary<string, int> CountPatterns(string archivePath, IEnumerable<string> patterns)
        {
            if (patterns == null) throw new ArgumentNullException(nameof(patterns));
            List<string> uniquePatterns = patterns
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            Dictionary<string, int> counts = uniquePatterns.ToDictionary(
                value => value,
                value => 0,
                StringComparer.Ordinal);
            if (uniquePatterns.Count == 0) return counts;

            List<byte[]> bytes = uniquePatterns.Select(Encoding.UTF8.GetBytes).ToList();
            int maximumLength = bytes.Max(value => value.Length);
            byte[] buffer = new byte[CopyBufferSize + maximumLength - 1];
            int carry = 0;
            using (FileStream stream = File.OpenRead(archivePath))
            {
                int read;
                while ((read = stream.Read(buffer, carry, CopyBufferSize)) > 0)
                {
                    int length = carry + read;
                    for (int patternIndex = 0; patternIndex < bytes.Count; patternIndex++)
                    {
                        byte[] pattern = bytes[patternIndex];
                        int count = 0;
                        int index = 0;
                        while ((index = FindPattern(
                            buffer,
                            length,
                            pattern,
                            index)) >= 0)
                        {
                            if (carry == 0 || index + pattern.Length > carry) count++;
                            // 允许重叠匹配，保持旧计数语义。
                            index++;
                        }
                        counts[uniquePatterns[patternIndex]] += count;
                    }

                    carry = Math.Min(maximumLength - 1, length);
                    Buffer.BlockCopy(buffer, length - carry, buffer, 0, carry);
                }
            }
            return counts;
        }

        internal IReadOnlyList<AsarArchiveEntry> Entries
        {
            get { return entries.AsReadOnly(); }
        }

        internal long RetainedEntryBytes
        {
            get
            {
                return entries.Sum(entry =>
                    (long)(entry.OriginalData == null ? 0 : entry.OriginalData.Length) +
                    (entry.StagedData == null || ReferenceEquals(entry.StagedData, entry.OriginalData)
                        ? 0L
                        : entry.StagedData.Length));
            }
        }

        internal bool HasChanges
        {
            get { return entries.Any(entry => entry.StagedData != null); }
        }

        internal byte[] ReadEntryData(string entryPath)
        {
            AsarArchiveEntry entry = entries.SingleOrDefault(value =>
                string.Equals(value.Path, entryPath, StringComparison.Ordinal));
            if (entry == null) throw new FileNotFoundException("ASAR 中没有找到条目。", entryPath);
            return GetEntryData(entry);
        }

        internal AsarArchiveEntry FindUniqueEntry(
            Func<AsarArchiveEntry, bool> metadataPredicate,
            Func<byte[], bool> contentPredicate,
            string description)
        {
            if (metadataPredicate == null) throw new ArgumentNullException(nameof(metadataPredicate));
            if (contentPredicate == null) throw new ArgumentNullException(nameof(contentPredicate));

            AsarArchiveEntry match = null;
            int matches = 0;
            foreach (AsarArchiveEntry entry in entries.Where(metadataPredicate))
            {
                byte[] data = entry.StagedData ?? ReadAndValidateEntry(entry, false);
                if (!contentPredicate(data)) continue;
                matches++;
                if (matches == 1)
                {
                    match = entry;
                    if (entry.OriginalData == null) entry.OriginalData = data;
                }
            }

            if (matches != 1)
            {
                throw new InvalidDataException(description + "匹配数量异常：" + matches.ToString(CultureInfo.InvariantCulture));
            }
            return match;
        }

        internal IList<AsarArchiveEntry> FindEntries(
            Func<AsarArchiveEntry, bool> metadataPredicate,
            Func<byte[], bool> contentPredicate)
        {
            if (metadataPredicate == null) throw new ArgumentNullException(nameof(metadataPredicate));
            if (contentPredicate == null) throw new ArgumentNullException(nameof(contentPredicate));

            List<AsarArchiveEntry> matches = new List<AsarArchiveEntry>();
            foreach (AsarArchiveEntry entry in entries.Where(metadataPredicate))
            {
                byte[] data = entry.StagedData ?? ReadAndValidateEntry(entry, false);
                if (!contentPredicate(data)) continue;
                if (entry.OriginalData == null) entry.OriginalData = data;
                matches.Add(entry);
            }
            return matches;
        }

        internal void ScanEntries(
            Func<AsarArchiveEntry, bool> metadataPredicate,
            Action<AsarArchiveEntry, byte[]> visitor)
        {
            if (metadataPredicate == null) throw new ArgumentNullException(nameof(metadataPredicate));
            if (visitor == null) throw new ArgumentNullException(nameof(visitor));

            EnsureSourceAvailable();
            foreach (AsarArchiveEntry entry in entries.Where(metadataPredicate))
            {
                byte[] data = entry.StagedData ?? entry.OriginalData;
                if (data == null) data = ReadAndValidateEntry(source, entry);
                visitor(entry, data);
            }
        }

        internal IDictionary<string, int> CountCurrentPatterns(
            IEnumerable<string> patterns)
        {
            if (patterns == null) throw new ArgumentNullException(nameof(patterns));
            string[] unique = patterns
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Dictionary<string, int> counts = new Dictionary<string, int>(
                CountPatterns(path, unique),
                StringComparer.Ordinal);
            foreach (AsarArchiveEntry entry in entries.Where(value => value.StagedData != null))
            {
                byte[] original = entry.OriginalData ?? ReadAndValidateEntry(entry, true);
                foreach (string pattern in unique)
                {
                    counts[pattern] -= CountAscii(original, pattern);
                    counts[pattern] += CountAscii(entry.StagedData, pattern);
                }
            }
            return counts;
        }

        internal void RetainEntryData(AsarArchiveEntry entry, byte[] data)
        {
            ValidateEntryOwner(entry);
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (entry.OriginalData == null) entry.OriginalData = data;
        }

        internal byte[] GetEntryData(AsarArchiveEntry entry)
        {
            ValidateEntryOwner(entry);
            if (entry.StagedData != null) return entry.StagedData;
            if (entry.OriginalData == null) entry.OriginalData = ReadAndValidateEntry(entry, true);
            return entry.OriginalData;
        }

        internal void StageEntry(AsarArchiveEntry entry, byte[] data)
        {
            ValidateEntryOwner(entry);
            if (data == null) throw new ArgumentNullException(nameof(data));
            entry.StagedData = data;
        }

        internal void RunStagingTransaction(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            Dictionary<AsarArchiveEntry, byte[]> checkpoint = entries.ToDictionary(
                entry => entry,
                entry => entry.StagedData);
            try
            {
                action();
            }
            catch
            {
                foreach (KeyValuePair<AsarArchiveEntry, byte[]> item in checkpoint)
                {
                    item.Key.StagedData = item.Value;
                }
                throw;
            }
        }

        internal void WriteAtomically(Action<AsarSession> validate)
        {
            if (!HasChanges) return;
            EnsureSourceAvailable();

            PrepareOutputMetadata();
            byte[] json = Encoding.UTF8.GetBytes(new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue,
                RecursionLimit = 2048
            }.Serialize(header));
            int paddedJsonSize = checked((json.Length + 3) & ~3);
            uint outputHeaderSize = checked((uint)(paddedJsonSize + 8));
            string temporaryPath = path + ".compatibility-" + Guid.NewGuid().ToString("N") + ".tmp";
            string sourceBackupPath = path + ".compatibility-source-" + Guid.NewGuid().ToString("N") + ".bak";

            try
            {
                using (FileStream destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (BinaryWriter writer = new BinaryWriter(destination, Encoding.UTF8, true))
                {
                    writer.Write((uint)4);
                    writer.Write(outputHeaderSize);
                    writer.Write(outputHeaderSize - 4);
                    writer.Write((uint)json.Length);
                    writer.Write(json);
                    for (int index = json.Length; index < paddedJsonSize; index++) writer.Write((byte)0);

                    byte[] copyBuffer = new byte[CopyBufferSize];
                    foreach (AsarArchiveEntry entry in entries.OrderBy(value => value.OutputOffset))
                    {
                        if (entry.StagedData != null)
                        {
                            writer.Write(entry.StagedData);
                        }
                        else
                        {
                            CopyAndValidateEntry(source, destination, entry, copyBuffer);
                        }
                    }
                    writer.Flush();
                    destination.Flush(true);
                }

                using (AsarSession verified = Open(temporaryPath))
                {
                    verified.ValidateAllEntries();
                    if (validate != null) validate(verified);
                }

                string analyzedSourceHash = ComputeStreamHash(source);
                ReleaseSource();
                ReplaceVerifiedSource(temporaryPath, sourceBackupPath, analyzedSourceHash);
            }
            finally
            {
                if (File.Exists(temporaryPath)) NativeFileSystem.DeleteFile(temporaryPath);
            }
        }

        internal void ValidateAllEntries()
        {
            EnsureSourceAvailable();
            byte[] buffer = new byte[CopyBufferSize];
            foreach (AsarArchiveEntry entry in entries.OrderBy(value => value.Offset))
            {
                CopyAndValidateEntry(source, null, entry, buffer);
            }
        }

        internal static int Count(byte[] data, byte[] pattern)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (pattern == null || pattern.Length == 0) throw new ArgumentException("搜索模式不能为空。", nameof(pattern));
            int count = 0;
            int index = 0;
            while ((index = FindPattern(data, data.Length, pattern, index)) >= 0)
            {
                count++;
                // 允许重叠匹配，保持旧计数语义。
                index++;
            }
            return count;
        }

        internal static int CountAscii(byte[] data, string pattern)
        {
            return Count(data, Encoding.UTF8.GetBytes(pattern));
        }

        internal static bool ContainsAscii(byte[] data, string pattern)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (string.IsNullOrEmpty(pattern))
            {
                throw new ArgumentException("搜索模式不能为空。", nameof(pattern));
            }
            byte[] bytes = Encoding.UTF8.GetBytes(pattern);
            return FindPattern(data, data.Length, bytes, 0) >= 0;
        }

        private static void LoadEntries(
            Dictionary<string, object> node,
            string parent,
            long archivePayloadBase,
            long archiveLength,
            List<AsarArchiveEntry> target)
        {
            object filesObject;
            if (!node.TryGetValue("files", out filesObject)) return;
            Dictionary<string, object> files = filesObject as Dictionary<string, object>;
            if (files == null)
            {
                throw new InvalidDataException("ASAR 目录节点的 files 结构无效：" + DisplayArchivePath(parent));
            }

            foreach (KeyValuePair<string, object> pair in files)
            {
                Dictionary<string, object> child = pair.Value as Dictionary<string, object>;
                string entryPath = string.IsNullOrEmpty(parent) ? pair.Key : parent + "/" + pair.Key;
                if (child == null) throw new InvalidDataException("ASAR 条目结构无效：" + entryPath);
                if (child.ContainsKey("files"))
                {
                    LoadEntries(child, entryPath, archivePayloadBase, archiveLength, target);
                    continue;
                }

                bool hasSize = child.ContainsKey("size");
                bool hasOffset = child.ContainsKey("offset");
                if (!hasSize && !hasOffset) continue;
                if (!hasSize || !hasOffset)
                {
                    object unpackedObject;
                    bool unpacked = hasSize &&
                        child.TryGetValue("unpacked", out unpackedObject) &&
                        Convert.ToBoolean(unpackedObject, CultureInfo.InvariantCulture);
                    if (unpacked) continue;
                    throw new InvalidDataException("ASAR 已打包条目缺少大小或偏移：" + entryPath);
                }

                object integrityObject;
                Dictionary<string, object> integrity = child.TryGetValue("integrity", out integrityObject)
                    ? integrityObject as Dictionary<string, object>
                    : null;
                if (integrity == null)
                {
                    throw new InvalidDataException("ASAR 已打包条目缺少完整性信息：" + entryPath);
                }

                AsarArchiveEntry entry = CreatePackedEntry(entryPath, child, integrity);
                if (entry.Size < 0 || entry.Offset < 0 || entry.BlockSize <= 0)
                {
                    throw new InvalidDataException("ASAR 条目包含非法大小、偏移或分块大小：" + entryPath);
                }
                if (archivePayloadBase < 0 ||
                    archivePayloadBase > archiveLength ||
                    entry.Offset > archiveLength - archivePayloadBase ||
                    entry.Size > archiveLength - archivePayloadBase - entry.Offset)
                {
                    throw new InvalidDataException("ASAR 条目超出文件边界：" + entryPath);
                }
                target.Add(entry);
            }
        }

        private static AsarArchiveEntry CreatePackedEntry(
            string entryPath,
            Dictionary<string, object> node,
            Dictionary<string, object> integrity)
        {
            object algorithmObject;
            object hashObject;
            object blockSizeObject;
            object blocksObject;
            if (!integrity.TryGetValue("algorithm", out algorithmObject) ||
                !integrity.TryGetValue("hash", out hashObject) ||
                !integrity.TryGetValue("blockSize", out blockSizeObject) ||
                !integrity.TryGetValue("blocks", out blocksObject))
            {
                throw new InvalidDataException("ASAR 已打包条目的完整性信息不完整：" + entryPath);
            }

            string algorithm = Convert.ToString(algorithmObject, CultureInfo.InvariantCulture);
            if (!string.Equals(algorithm, "SHA256", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("ASAR 已打包条目使用不支持的完整性算法：" + entryPath + "，算法=" + algorithm);
            }

            string hash = Convert.ToString(hashObject, CultureInfo.InvariantCulture);
            if (!IsSha256Hash(hash))
            {
                throw new InvalidDataException("ASAR 条目的完整性哈希格式无效：" + entryPath);
            }
            IEnumerable blocks = blocksObject as IEnumerable;
            if (blocks == null || blocksObject is string)
            {
                throw new InvalidDataException("ASAR 条目缺少有效的完整性分块列表：" + entryPath);
            }

            AsarArchiveEntry entry;
            try
            {
                entry = new AsarArchiveEntry
                {
                    Path = entryPath,
                    Node = node,
                    Integrity = integrity,
                    Size = Convert.ToInt32(node["size"], CultureInfo.InvariantCulture),
                    Offset = long.Parse(Convert.ToString(node["offset"], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                    Hash = hash,
                    BlockSize = Convert.ToInt32(blockSizeObject, CultureInfo.InvariantCulture)
                };
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("ASAR 条目的大小、偏移或分块大小格式无效：" + entryPath, exception);
            }
            if (entry.Size < 0 || entry.Offset < 0 || entry.BlockSize <= 0)
            {
                throw new InvalidDataException("ASAR 条目包含非法大小、偏移或分块大小：" + entryPath);
            }

            foreach (object block in blocks)
            {
                string blockHash = Convert.ToString(block, CultureInfo.InvariantCulture);
                if (!IsSha256Hash(blockHash))
                {
                    throw new InvalidDataException("ASAR 条目的完整性分块哈希格式无效：" + entryPath);
                }
                entry.BlockHashes.Add(blockHash);
            }
            int expectedBlockCount = entry.Size == 0
                ? 1
                : checked(((entry.Size - 1) / entry.BlockSize) + 1);
            if (entry.BlockHashes.Count != expectedBlockCount)
            {
                throw new InvalidDataException("ASAR 条目的完整性分块数量与大小不一致：" + entryPath);
            }
            return entry;
        }

        private static void ValidatePayloadLayout(IEnumerable<AsarArchiveEntry> archiveEntries, long payloadLength)
        {
            long expectedOffset = 0;
            foreach (AsarArchiveEntry entry in archiveEntries.OrderBy(value => value.Offset).ThenBy(value => value.Size))
            {
                if (entry.Offset != expectedOffset)
                {
                    throw new InvalidDataException(
                        "ASAR payload 未被已打包条目连续覆盖：" + entry.Path +
                        "，实际偏移=" + entry.Offset.ToString(CultureInfo.InvariantCulture) +
                        "，预期偏移=" + expectedOffset.ToString(CultureInfo.InvariantCulture));
                }
                expectedOffset = checked(expectedOffset + entry.Size);
            }
            if (expectedOffset != payloadLength)
            {
                throw new InvalidDataException(
                    "ASAR payload 包含未被已打包条目引用的数据：已引用=" +
                    expectedOffset.ToString(CultureInfo.InvariantCulture) +
                    "，payload=" + payloadLength.ToString(CultureInfo.InvariantCulture));
            }
        }

        private byte[] ReadAndValidateEntry(AsarArchiveEntry entry, bool retain)
        {
            EnsureSourceAvailable();
            byte[] data = ReadAndValidateEntry(source, entry);
            if (retain) entry.OriginalData = data;
            return data;
        }

        private byte[] ReadAndValidateEntry(FileStream stream, AsarArchiveEntry entry)
        {
            byte[] data = new byte[entry.Size];
            stream.Position = payloadBase + entry.Offset;
            ReadExactly(stream, data, 0, data.Length);
            string actualHash = ComputeHash(data, 0, data.Length);
            if (!string.Equals(entry.Hash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("ASAR 条目的完整性哈希校验失败：" + entry.Path);
            }
            List<string> actualBlocks = ComputeBlockHashes(data, entry.BlockSize);
            if (entry.BlockHashes.Count != actualBlocks.Count)
            {
                throw new InvalidDataException("ASAR 条目的完整性分块数量无效：" + entry.Path);
            }
            for (int index = 0; index < actualBlocks.Count; index++)
            {
                if (!string.Equals(entry.BlockHashes[index], actualBlocks[index], StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("ASAR 条目的完整性分块校验失败：" + entry.Path);
                }
            }
            return data;
        }

        private void CopyAndValidateEntry(
            FileStream input,
            Stream output,
            AsarArchiveEntry entry,
            byte[] buffer)
        {
            input.Position = payloadBase + entry.Offset;
            long remaining = entry.Size;
            int blockIndex = 0;
            using (SHA256 fullHash = SHA256.Create())
            {
                if (remaining == 0)
                {
                    fullHash.TransformFinalBlock(new byte[0], 0, 0);
                    ValidateBlockHash(entry, blockIndex++, ComputeEmptyHash());
                }
                else
                {
                    while (remaining > 0)
                    {
                        long blockRemaining = Math.Min((long)entry.BlockSize, remaining);
                        using (SHA256 blockHash = SHA256.Create())
                        {
                            while (blockRemaining > 0)
                            {
                                int requested = (int)Math.Min(
                                    Math.Min((long)buffer.Length, blockRemaining),
                                    remaining);
                                int read = input.Read(buffer, 0, requested);
                                if (read <= 0) throw new EndOfStreamException();
                                if (output != null) output.Write(buffer, 0, read);
                                fullHash.TransformBlock(buffer, 0, read, buffer, 0);
                                blockHash.TransformBlock(buffer, 0, read, buffer, 0);
                                blockRemaining -= read;
                                remaining -= read;
                            }
                            blockHash.TransformFinalBlock(new byte[0], 0, 0);
                            ValidateBlockHash(entry, blockIndex++, ToHex(blockHash.Hash));
                        }
                    }
                    fullHash.TransformFinalBlock(new byte[0], 0, 0);
                }

                if (blockIndex != entry.BlockHashes.Count)
                {
                    throw new InvalidDataException("ASAR 条目的完整性分块数量无效：" + entry.Path);
                }
                if (!string.Equals(entry.Hash, ToHex(fullHash.Hash), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("ASAR 条目的完整性哈希校验失败：" + entry.Path);
                }
            }
        }

        private static void ValidateBlockHash(AsarArchiveEntry entry, int index, string actual)
        {
            if (index >= entry.BlockHashes.Count ||
                !string.Equals(entry.BlockHashes[index], actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("ASAR 条目的完整性分块校验失败：" + entry.Path);
            }
        }

        private static string ComputeEmptyHash()
        {
            using (SHA256 sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(new byte[0]));
            }
        }

        private void ReplaceVerifiedSource(
            string temporaryPath,
            string backupPath,
            string analyzedSourceHash)
        {
            string extendedTemporaryPath = NativeFileSystem.ToExtendedPath(temporaryPath);
            string extendedSourcePath = NativeFileSystem.ToExtendedPath(path);
            string extendedBackupPath = NativeFileSystem.ToExtendedPath(backupPath);
            try
            {
                File.Replace(
                    extendedTemporaryPath,
                    extendedSourcePath,
                    extendedBackupPath,
                    true);
                FileIdentity replacedIdentity;
                using (FileStream backup = new FileStream(
                    extendedBackupPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSize,
                    FileOptions.SequentialScan))
                {
                    replacedIdentity = GetFileIdentity(backup.SafeFileHandle, backupPath);
                }
                if (!sourceIdentity.Equals(replacedIdentity))
                {
                    throw new IOException("ASAR 源路径在分析句柄释放后发生替换，已拒绝提交补丁。");
                }
                if (!ArtifactHash.FixedTimeEquals(
                    ArtifactHash.ComputeSha256(extendedBackupPath),
                    analyzedSourceHash))
                {
                    throw new IOException("ASAR 源文件在分析句柄释放后发生原位改写，已拒绝提交补丁。");
                }
                NativeFileSystem.DeleteFile(backupPath);
            }
            catch (Exception commitException)
            {
                try
                {
                    if (File.Exists(extendedBackupPath))
                    {
                        if (File.Exists(extendedSourcePath))
                        {
                            File.Replace(extendedBackupPath, extendedSourcePath, null, true);
                        }
                        else
                        {
                            File.Move(extendedBackupPath, extendedSourcePath);
                        }
                    }
                }
                catch (Exception restoreException)
                {
                    throw new AggregateException(
                        "ASAR 原子提交失败，并且源文件身份回滚未能完成。",
                        commitException,
                        restoreException);
                }
                throw;
            }
        }

        private void EnsureSourceAvailable()
        {
            if (source == null)
            {
                throw new ObjectDisposedException(nameof(AsarSession));
            }
        }

        private void ReleaseSource()
        {
            FileStream current = source;
            source = null;
            if (current != null) current.Dispose();
        }

        public void Dispose()
        {
            ReleaseSource();
        }

        private static string ComputeStreamHash(FileStream stream)
        {
            long position = stream.Position;
            try
            {
                stream.Position = 0;
                using (SHA256 sha = SHA256.Create())
                {
                    return ToHex(sha.ComputeHash(stream));
                }
            }
            finally
            {
                stream.Position = position;
            }
        }

        private void PrepareOutputMetadata()
        {
            long outputOffset = 0;
            foreach (AsarArchiveEntry entry in entries.OrderBy(value => value.Offset))
            {
                entry.OutputOffset = outputOffset;
                byte[] data = entry.StagedData;
                int outputSize = data == null ? entry.Size : data.Length;
                entry.Node["offset"] = outputOffset.ToString(CultureInfo.InvariantCulture);
                entry.Node["size"] = outputSize;
                if (data != null)
                {
                    entry.Integrity["hash"] = ComputeHash(data, 0, data.Length);
                    entry.Integrity["blocks"] = ComputeBlockHashes(data, entry.BlockSize);
                }
                outputOffset = checked(outputOffset + outputSize);
            }
        }

        private void ValidateEntryOwner(AsarArchiveEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (!entries.Contains(entry)) throw new InvalidOperationException("ASAR 条目不属于当前会话。");
        }

        private static List<string> ComputeBlockHashes(byte[] data, int blockSize)
        {
            if (blockSize <= 0) throw new InvalidDataException("ASAR 完整性分块大小必须大于零。");
            List<string> result = new List<string>();
            if (data.Length == 0) result.Add(ComputeHash(data, 0, 0));
            for (int offset = 0; offset < data.Length; offset += blockSize)
            {
                result.Add(ComputeHash(data, offset, Math.Min(blockSize, data.Length - offset)));
            }
            return result;
        }

        private static string ComputeHash(byte[] data, int offset, int count)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(data, offset, count));
            }
        }

        private static string ToHex(byte[] hash)
        {
            StringBuilder builder = new StringBuilder(hash.Length * 2);
            foreach (byte value in hash)
            {
                builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private static bool IsSha256Hash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char character in value)
            {
                if (!((character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }

        private static int FindPattern(
            byte[] data,
            int length,
            byte[] pattern,
            int start)
        {
            if (data == null || pattern == null || pattern.Length == 0 ||
                start < 0 || length < pattern.Length || start > length - pattern.Length)
            {
                return -1;
            }
            if (pattern.Length == 1)
            {
                for (int index = start; index < length; index++)
                {
                    if (data[index] == pattern[0]) return index;
                }
                return -1;
            }

            int[] shifts = new int[256];
            for (int index = 0; index < shifts.Length; index++) shifts[index] = pattern.Length;
            for (int index = 0; index < pattern.Length - 1; index++)
            {
                shifts[pattern[index]] = pattern.Length - 1 - index;
            }
            int candidate = start;
            int last = pattern.Length - 1;
            while (candidate <= length - pattern.Length)
            {
                int patternIndex = last;
                while (patternIndex >= 0 &&
                    data[candidate + patternIndex] == pattern[patternIndex])
                {
                    patternIndex--;
                }
                if (patternIndex < 0) return candidate;
                candidate += shifts[data[candidate + last]];
            }
            return -1;
        }

        private static void CopyExactly(Stream source, Stream destination, long count, byte[] buffer)
        {
            while (count > 0)
            {
                int requested = (int)Math.Min(buffer.Length, count);
                int read = source.Read(buffer, 0, requested);
                if (read <= 0) throw new EndOfStreamException();
                destination.Write(buffer, 0, read);
                count -= read;
            }
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
                count -= read;
            }
        }

        private static FileIdentity GetFileIdentity(SafeFileHandle handle, string displayPath)
        {
            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(handle, out information))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法读取 ASAR 源文件身份：" + displayPath);
            }
            return new FileIdentity
            {
                VolumeSerialNumber = information.VolumeSerialNumber,
                FileIndexHigh = information.FileIndexHigh,
                FileIndexLow = information.FileIndexLow
            };
        }

        private static string DisplayArchivePath(string entryPath)
        {
            return string.IsNullOrEmpty(entryPath) ? "<根目录>" : entryPath;
        }

        private struct FileIdentity : IEquatable<FileIdentity>
        {
            internal uint VolumeSerialNumber;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;

            public bool Equals(FileIdentity other)
            {
                return VolumeSerialNumber == other.VolumeSerialNumber &&
                    FileIndexHigh == other.FileIndexHigh &&
                    FileIndexLow == other.FileIndexLow;
            }

            public override bool Equals(object value)
            {
                return value is FileIdentity && Equals((FileIdentity)value);
            }

            public override int GetHashCode()
            {
                return unchecked(
                    ((int)VolumeSerialNumber * 397) ^
                    ((int)FileIndexHigh * 31) ^
                    (int)FileIndexLow);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime
        {
            internal uint LowDateTime;
            internal uint HighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal NativeFileTime CreationTime;
            internal NativeFileTime LastAccessTime;
            internal NativeFileTime LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);
    }

    internal sealed class AsarArchiveEntry
    {
        internal string Path;
        internal Dictionary<string, object> Node;
        internal Dictionary<string, object> Integrity;
        internal int Size;
        internal long Offset;
        internal string Hash;
        internal int BlockSize;
        internal List<string> BlockHashes = new List<string>();
        internal byte[] OriginalData;
        internal byte[] StagedData;
        internal long OutputOffset;
    }
}
