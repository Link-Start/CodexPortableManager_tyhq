using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexPortableManager
{
    internal static class AsarPackageMetadata
    {
        private const string ExpectedElectronPackageName = "openai-codex-electron";
        private const int MaximumHeaderSize = 64 * 1024 * 1024;
        private const int MaximumPackageJsonSize = 1024 * 1024;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static string ReadApplicationVersion(string asarPath)
        {
            if (string.IsNullOrWhiteSpace(asarPath))
            {
                throw new ArgumentException("app.asar 路径不能为空。", nameof(asarPath));
            }

            using (FileStream stream = new FileStream(
                Path.GetFullPath(asarPath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] prefix = new byte[16];
                ReadExactly(stream, prefix, 0, prefix.Length);
                uint headerSize = BitConverter.ToUInt32(prefix, 4);
                uint jsonSize = BitConverter.ToUInt32(prefix, 12);
                if (headerSize < 8 ||
                    jsonSize == 0 ||
                    jsonSize > headerSize - 8 ||
                    jsonSize > MaximumHeaderSize ||
                    8L + headerSize > stream.Length)
                {
                    throw new InvalidDataException("app.asar 头部大小无效。");
                }

                byte[] headerBytes = new byte[jsonSize];
                ReadExactly(stream, headerBytes, 0, headerBytes.Length);
                Dictionary<string, object> header = DeserializeObject(
                    Encoding.UTF8.GetString(headerBytes),
                    MaximumHeaderSize);
                Dictionary<string, object> packageEntry = FindRootPackageEntry(header);
                int packageSize = Convert.ToInt32(packageEntry["size"]);
                long packageOffset = long.Parse(Convert.ToString(packageEntry["offset"]));
                long payloadBase = 8L + headerSize;
                if (packageSize <= 0 ||
                    packageSize > MaximumPackageJsonSize ||
                    packageOffset < 0 ||
                    packageOffset > stream.Length - payloadBase ||
                    packageSize > stream.Length - payloadBase - packageOffset)
                {
                    throw new InvalidDataException("app.asar 中 package.json 的范围无效。");
                }

                byte[] packageBytes = new byte[packageSize];
                stream.Position = payloadBase + packageOffset;
                ReadExactly(stream, packageBytes, 0, packageBytes.Length);
                Dictionary<string, object> package = DeserializePackageJson(packageBytes);
                object versionValue;
                string version = package.TryGetValue("version", out versionValue)
                    ? Convert.ToString(versionValue)
                    : null;
                if (string.IsNullOrWhiteSpace(version))
                {
                    throw new InvalidDataException("app.asar 的 package.json 缺少应用版本。");
                }
                return version.Trim();
            }
        }

        internal static AsarArchiveEntry ResolveElectronMainEntry(AsarSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            AsarArchiveEntry packageEntry = null;
            int packageEntryCount = 0;
            foreach (AsarArchiveEntry entry in session.Entries)
            {
                if (!string.Equals(entry.Path, "package.json", StringComparison.Ordinal)) continue;
                packageEntry = entry;
                packageEntryCount++;
            }
            if (packageEntryCount != 1 || packageEntry == null)
            {
                throw new InvalidDataException(
                    "app.asar 根 package.json 的已打包条目数量异常：" + packageEntryCount + "。");
            }
            if (packageEntry.Size <= 0 || packageEntry.Size > MaximumPackageJsonSize)
            {
                throw new InvalidDataException("app.asar 根 package.json 的大小无效。");
            }

            Dictionary<string, object> package = DeserializePackageJson(
                session.GetEntryData(packageEntry));
            object nameValue;
            string name = package.TryGetValue("name", out nameValue)
                ? nameValue as string
                : null;
            if (!string.Equals(name, ExpectedElectronPackageName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "app.asar 的 package.json 不是受支持的官方 Electron 包。实际 name=" +
                    (string.IsNullOrEmpty(name) ? "<missing>" : name) + "。");
            }

            object mainValue;
            string main = package.TryGetValue("main", out mainValue)
                ? mainValue as string
                : null;
            string mainPath = NormalizeElectronMainPath(main);

            AsarArchiveEntry mainEntry = null;
            int mainEntryCount = 0;
            foreach (AsarArchiveEntry entry in session.Entries)
            {
                if (!string.Equals(entry.Path, mainPath, StringComparison.Ordinal)) continue;
                mainEntry = entry;
                mainEntryCount++;
            }
            if (mainEntryCount != 1 || mainEntry == null)
            {
                throw new InvalidDataException(
                    "package.json.main 没有精确对应唯一的已打包 JavaScript 条目：" + mainPath + "。");
            }
            if (mainEntry.Size <= 0)
            {
                throw new InvalidDataException("package.json.main 指向的 JavaScript 条目为空。");
            }
            return mainEntry;
        }

        private static string NormalizeElectronMainPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("app.asar 的 package.json 缺少 main 入口。");
            }
            if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidDataException("package.json.main 入口不能包含首尾空白。");
            }
            if (value[0] == '/' ||
                value.IndexOf('\\') >= 0 ||
                value.IndexOf(':') >= 0 ||
                value.IndexOf('\0') >= 0)
            {
                throw new InvalidDataException("package.json.main 必须是无歧义的 ASAR 相对路径。");
            }

            string[] segments = value.Split('/');
            foreach (string segment in segments)
            {
                if (segment.Length == 0 ||
                    string.Equals(segment, ".", StringComparison.Ordinal) ||
                    string.Equals(segment, "..", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "package.json.main 包含空段、当前目录或父目录段。");
                }
                foreach (char character in segment)
                {
                    if (char.IsControl(character))
                    {
                        throw new InvalidDataException("package.json.main 包含控制字符。");
                    }
                }
            }

            string normalized = string.Join("/", segments);
            if (!normalized.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("package.json.main 必须指向已打包 JavaScript 条目。");
            }
            return normalized;
        }

        private static Dictionary<string, object> DeserializePackageJson(byte[] packageBytes)
        {
            if (packageBytes == null ||
                packageBytes.Length == 0 ||
                packageBytes.Length > MaximumPackageJsonSize)
            {
                throw new InvalidDataException("app.asar 根 package.json 的大小无效。");
            }

            string json;
            try
            {
                json = StrictUtf8.GetString(packageBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("app.asar 根 package.json 不是有效 UTF-8。", exception);
            }
            if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
            return DeserializeObject(json, MaximumPackageJsonSize);
        }

        private static Dictionary<string, object> FindRootPackageEntry(Dictionary<string, object> header)
        {
            object filesValue;
            Dictionary<string, object> files =
                header.TryGetValue("files", out filesValue)
                    ? filesValue as Dictionary<string, object>
                    : null;
            object packageValue;
            Dictionary<string, object> packageEntry =
                files != null && files.TryGetValue("package.json", out packageValue)
                    ? packageValue as Dictionary<string, object>
                    : null;
            if (packageEntry == null ||
                !packageEntry.ContainsKey("size") ||
                !packageEntry.ContainsKey("offset"))
            {
                throw new InvalidDataException("app.asar 头部缺少根 package.json。");
            }
            return packageEntry;
        }

        private static Dictionary<string, object> DeserializeObject(string json, int maximumLength)
        {
            Dictionary<string, object> value = new JavaScriptSerializer
            {
                MaxJsonLength = maximumLength,
                RecursionLimit = 2048
            }.Deserialize<Dictionary<string, object>>(json);
            if (value == null)
            {
                throw new InvalidDataException("ASAR JSON 元数据无效。");
            }
            return value;
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
    }
}
