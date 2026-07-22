using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace CodexPortableManager
{
    internal static class InstallOwnership
    {
        private const string ExpectedPackageName = "OpenAI.Codex";
        internal const string MarkerFileName = ".codex-portable-manager.json";

        public static string PrepareInstall(
            string installRoot,
            string previousRoot,
            LegacyAdoptionApproval adoptionApproval,
            Action<string> log)
        {
            string installId = null;
            if (Directory.Exists(installRoot) && !IsDirectoryEmpty(installRoot))
            {
                installId = EnsureOwnedInstallation(installRoot, null, adoptionApproval, log);
            }

            if (Directory.Exists(previousRoot) && !IsDirectoryEmpty(previousRoot))
            {
                string previousId = EnsureOwnedInstallation(previousRoot, installId, adoptionApproval, log);
                if (installId == null)
                {
                    installId = previousId;
                }
            }

            return installId ?? Guid.NewGuid().ToString("N");
        }

        public static bool RequiresLegacyAdoption(string installRoot)
        {
            try
            {
                if (!Directory.Exists(installRoot) || IsDirectoryEmpty(installRoot)) return false;
                if (File.Exists(GetMarkerPath(installRoot))) return false;
                PackageProfile profile;
                string validationError;
                return TryValidateCodexPayload(installRoot, out profile, out validationError);
            }
            catch
            {
                return false;
            }
        }

        public static bool HasOwnershipMarker(string installRoot)
        {
            if (!Directory.Exists(installRoot))
            {
                return false;
            }
            EnsureManagedDirectoryPath(installRoot, false);
            return File.Exists(GetMarkerPath(installRoot));
        }

        public static string EnsureOwnedInstallation(
            string installRoot,
            string expectedInstallId,
            LegacyAdoptionApproval adoptionApproval,
            Action<string> log)
        {
            PackageProfile profile;
            string validationError;
            if (!TryValidateCodexPayload(installRoot, out profile, out validationError))
            {
                throw new InvalidOperationException(
                    "拒绝操作未确认属于 Codex Portable Manager 的目录：" + installRoot +
                    Environment.NewLine + validationError);
            }

            string markerPath = GetMarkerPath(installRoot);
            if (File.Exists(markerPath))
            {
                InstallationRecord record = ReadInstallationRecord(installRoot);
                ValidateRecord(record, markerPath, expectedInstallId);
                return record.Identity.InstallId;
            }

            if (adoptionApproval == null || !adoptionApproval.Covers(installRoot))
            {
                throw new InvalidOperationException(
                    "安装目录缺少所有权标记，且当前操作没有针对该目录的显式接管批准，已拒绝继续：" + installRoot);
            }

            string adoptedId = string.IsNullOrWhiteSpace(expectedInstallId)
                ? Guid.NewGuid().ToString("N")
                : expectedInstallId;
            WriteMarker(installRoot, adoptedId, profile.Version);
            if (log != null)
            {
                log("已验证并接管无所有权标记的 Codex 便携目录：" + installRoot);
            }
            return adoptedId;
        }

        public static bool TryValidateCodexPayload(string installRoot, out PackageProfile profile, out string error)
        {
            profile = null;
            error = null;
            try
            {
                if (!Directory.Exists(installRoot))
                {
                    error = "目录不存在。";
                    return false;
                }

                EnsureManagedDirectoryPath(installRoot, false);

                profile = PackageProfileReader.Read(installRoot);
                if (!string.Equals(profile.PackageName, ExpectedPackageName, StringComparison.Ordinal))
                {
                    error = "AppxManifest.xml 的包名不是 " + ExpectedPackageName + "。";
                    return false;
                }

                Version parsedVersion;
                if (!Version.TryParse(profile.Version, out parsedVersion))
                {
                    error = "AppxManifest.xml 的版本号无效。";
                    return false;
                }

                string executable = PackageProfileReader.GetExecutablePath(installRoot, profile);
                FileInfo executableInfo = new FileInfo(executable);
                if (!executableInfo.Exists || executableInfo.Length == 0)
                {
                    error = "清单声明的 Codex 主程序不存在或为空。";
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        /// <summary>
        /// 在包身份和主程序之外，再确认当前桌面版实际启动所需的核心资源完整存在。
        /// 所有权/卸载仍可使用较宽松的 TryValidateCodexPayload；版本识别、启动和回滚
        /// 应使用本方法，避免把缺少 app.asar 等关键文件的目录当作可用版本。
        /// </summary>
        public static bool TryValidateRunnableCodexPayload(
            string installRoot,
            out PackageProfile profile,
            out string error)
        {
            if (!TryValidateCodexPayload(installRoot, out profile, out error))
            {
                return false;
            }

            try
            {
                string executable = PackageProfileReader.GetExecutablePath(installRoot, profile);
                string resourcesRoot = Path.Combine(Path.GetDirectoryName(executable), "resources");
                string[] requiredFiles =
                {
                    Path.Combine(resourcesRoot, "app.asar"),
                    Path.Combine(resourcesRoot, "codex.exe")
                };
                foreach (string requiredFile in requiredFiles)
                {
                    FileInfo file = new FileInfo(requiredFile);
                    if (!file.Exists || file.Length == 0)
                    {
                        error = "缺少或为空的关键运行组件：" + requiredFile;
                        return false;
                    }
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public static bool TryValidateOwnedRunnableCodexPayload(
            string installRoot,
            out PackageProfile profile,
            out string error)
        {
            if (!TryValidateRunnableCodexPayload(installRoot, out profile, out error))
            {
                return false;
            }

            string markerPath = GetMarkerPath(installRoot);
            if (!File.Exists(markerPath))
            {
                error = "安装目录缺少 Codex Portable Manager 所有权标记。";
                return false;
            }

            try
            {
                InstallationRecord record = ReadInstallationRecord(installRoot);
                ValidateRecord(record, markerPath, null);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public static void WriteMarker(string installRoot, string installId, string packageVersion)
        {
            WriteMarker(installRoot, installId, packageVersion, null);
        }

        public static void WriteMarker(
            string installRoot,
            string installId,
            string packageVersion,
            ArtifactProvenance provenance)
        {
            Guid parsedId;
            if (!Guid.TryParseExact(installId, "N", out parsedId))
            {
                throw new InvalidDataException("安装 ID 格式无效。");
            }

            EnsureManagedDirectoryPath(installRoot, true);
            Directory.CreateDirectory(installRoot);
            EnsureManagedDirectoryPath(installRoot, false);
            Version parsedVersion;
            if (!Version.TryParse(packageVersion, out parsedVersion))
            {
                throw new InvalidDataException("安装记录中的包版本格式无效。");
            }

            InstallationRecord record = new InstallationRecord
            {
                Identity = new InstallationIdentity
                {
                    InstallId = installId,
                    PackageName = ExpectedPackageName,
                    PackageVersion = packageVersion
                },
                Provenance = provenance,
                UpdatedUtc = DateTime.UtcNow.ToString("O")
            };
            ValidateRecord(record, GetMarkerPath(installRoot), null);
            AtomicWrite(GetMarkerPath(installRoot), new JavaScriptSerializer().Serialize(record));
        }

        public static InstallationRecord ReadInstallationRecord(string installRoot)
        {
            string markerPath = GetMarkerPath(installRoot);
            if (!File.Exists(markerPath))
            {
                throw new FileNotFoundException("安装目录缺少 Codex Portable Manager 所有权标记。", markerPath);
            }

            try
            {
                string json = File.ReadAllText(markerPath, Encoding.UTF8);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                InstallationRecord record = serializer.Deserialize<InstallationRecord>(json);
                ValidateRecord(record, markerPath, null);
                return record;
            }
            catch (Exception exception) when (!(exception is InvalidDataException))
            {
                throw new InvalidDataException("安装所有权标记损坏，已拒绝继续：" + markerPath, exception);
            }
        }

        public static bool IsDirectoryEmpty(string path)
        {
            if (!Directory.Exists(path))
            {
                return false;
            }
            EnsureManagedDirectoryPath(path, false);
            using (var enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator())
            {
                return !enumerator.MoveNext();
            }
        }

        public static void EnsureManagedDirectoryPath(string path, bool allowMissingComponents)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("受管安装目录不能为空。", nameof(path));
            }

            string fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string rootPath = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new InvalidDataException("无法确定受管安装目录的文件系统根路径：" + fullPath);
            }

            string current = fullPath;
            while (!string.IsNullOrWhiteSpace(current))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (FileNotFoundException)
                {
                    if (!allowMissingComponents)
                    {
                        throw new DirectoryNotFoundException("受管安装目录不存在：" + fullPath);
                    }
                    attributes = default(FileAttributes);
                }
                catch (DirectoryNotFoundException)
                {
                    if (!allowMissingComponents)
                    {
                        throw new DirectoryNotFoundException("受管安装目录不存在：" + fullPath);
                    }
                    attributes = default(FileAttributes);
                }

                if (attributes != 0)
                {
                    bool isFinalComponent = string.Equals(current, fullPath, StringComparison.OrdinalIgnoreCase);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException(
                            isFinalComponent
                                ? "受管安装目录不能是 junction、符号链接或其他重解析点：" + current
                                : "受管安装目录不能位于 junction、符号链接或其他重解析点祖先下：" + current);
                    }
                    if ((attributes & FileAttributes.Directory) == 0)
                    {
                        throw new InvalidDataException(
                            isFinalComponent
                                ? "受管安装路径不是目录：" + current
                                : "受管安装目录的祖先路径不是目录：" + current);
                    }
                }

                if (DirectoryPathsEqual(current, rootPath))
                {
                    break;
                }

                string parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) || DirectoryPathsEqual(parent, current))
                {
                    throw new InvalidDataException("无法安全遍历受管安装目录的祖先路径：" + fullPath);
                }
                current = parent;
            }
        }

        internal static string GetManagedDirectoryIdentity(string path)
        {
            string fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            using (SafeFileHandle handle = OpenManagedDirectoryHandle(fullPath))
            {
                return GetManagedDirectoryIdentity(handle, fullPath);
            }
        }

        internal static SafeFileHandle OpenManagedDirectoryHandle(string path)
        {
            EnsureManagedDirectoryPath(path, false);
            string fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            SafeFileHandle handle = CreateFile(
                ToExtendedPath(fullPath),
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(
                    error,
                    "无法打开受管目录并锁定移动身份：" + fullPath);
            }
            try
            {
                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "无法复验待移动受管目录：" + fullPath);
                }
                if ((information.FileAttributes & FileAttributeDirectory) == 0 ||
                    (information.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "待移动受管路径不是普通目录：" + fullPath);
                }
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        internal static string GetManagedDirectoryIdentity(
            SafeFileHandle handle,
            string displayPath)
        {
            if (handle == null || handle.IsInvalid)
            {
                throw new ArgumentException("受管目录句柄无效。", nameof(handle));
            }
            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(handle, out information))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法读取受管目录的清理身份：" + displayPath);
            }
            if ((information.FileAttributes & FileAttributeDirectory) == 0 ||
                (information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new InvalidDataException("清理身份只能绑定普通目录：" + displayPath);
            }
            string persistentIdentity;
            if (NativeFileSystem.TryGetPersistentDirectoryIdentity(
                handle,
                out persistentIdentity))
            {
                return persistentIdentity;
            }
            throw new IOException(
                "当前文件系统无法提供可靠的 128 位持久目录身份：" + displayPath);
        }

        internal static void EnsureManagedDirectoryIdentity(string path, string expectedIdentity)
        {
            if (!IsManagedDirectoryIdentity(expectedIdentity))
            {
                throw new InvalidDataException("清理目录身份格式无效：" + path);
            }
            string fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            using (SafeFileHandle handle = CreateFile(
                ToExtendedPath(fullPath),
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "无法打开受管目录以复验清理身份：" + fullPath);
                }
                EnsureManagedDirectoryIdentity(handle, fullPath, expectedIdentity);
            }
        }

        internal static void EnsureManagedDirectoryIdentity(
            SafeFileHandle handle,
            string displayPath,
            string expectedIdentity)
        {
            if (!IsManagedDirectoryIdentity(expectedIdentity))
            {
                throw new InvalidDataException("清理目录身份格式无效：" + displayPath);
            }
            string actualIdentity = GetManagedDirectoryIdentity(handle, displayPath);
            if (!string.Equals(actualIdentity, expectedIdentity, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("清理目录已被替换，已拒绝继续删除：" + displayPath);
            }
        }

        internal static bool IsManagedDirectoryIdentity(string value)
        {
            ulong volumeSerial;
            ulong fileIdHigh;
            ulong fileIdLow;
            return TryParseManagedDirectoryIdentity(
                value,
                out volumeSerial,
                out fileIdHigh,
                out fileIdLow) &&
                volumeSerial != 0 &&
                (fileIdHigh != 0 || fileIdLow != 0);
        }

        internal static bool ManagedDirectoryIdentitiesEqual(string first, string second)
        {
            if (!IsManagedDirectoryIdentity(first) ||
                !IsManagedDirectoryIdentity(second))
            {
                return false;
            }
            ulong firstVolume;
            ulong secondVolume;
            ulong firstHigh;
            ulong secondHigh;
            ulong firstLow;
            ulong secondLow;
            if (!TryParseManagedDirectoryIdentity(
                    first,
                    out firstVolume,
                    out firstHigh,
                    out firstLow) ||
                !TryParseManagedDirectoryIdentity(
                    second,
                    out secondVolume,
                    out secondHigh,
                    out secondLow))
            {
                return false;
            }
            return firstVolume == secondVolume &&
                firstHigh == secondHigh &&
                firstLow == secondLow;
        }

        private static bool TryParseManagedDirectoryIdentity(
            string value,
            out ulong volumeSerial,
            out ulong fileIdHigh,
            out ulong fileIdLow)
        {
            volumeSerial = 0;
            fileIdHigh = 0;
            fileIdLow = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            string[] parts = value.Split(Convert.ToChar(124));
            if (parts.Length != 3)
            {
                return false;
            }
            if (!string.Equals(parts[0], "directory-identity", StringComparison.Ordinal) ||
                parts[1].Length != 16 ||
                parts[2].Length != 32 ||
                !ulong.TryParse(
                    parts[1],
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out volumeSerial) ||
                !ulong.TryParse(
                    parts[2].Substring(0, 16),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out fileIdHigh) ||
                !ulong.TryParse(
                    parts[2].Substring(16, 16),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out fileIdLow))
            {
                return false;
            }
            return true;
        }

        private static bool DirectoryPathsEqual(string first, string second)
        {
            return string.Equals(
                first.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                second.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
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

        private static void ValidateRecord(InstallationRecord record, string markerPath, string expectedInstallId)
        {
            Guid parsedId;
            Version parsedVersion;
            if (record == null ||
                record.Identity == null ||
                !string.Equals(record.Identity.PackageName, ExpectedPackageName, StringComparison.Ordinal) ||
                !Guid.TryParseExact(record.Identity.InstallId, "N", out parsedId) ||
                !Version.TryParse(record.Identity.PackageVersion, out parsedVersion))
            {
                throw new InvalidDataException("安装所有权标记格式无效：" + markerPath);
            }
            if (!string.IsNullOrWhiteSpace(expectedInstallId) &&
                !string.Equals(record.Identity.InstallId, expectedInstallId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("当前版本与回滚版本不属于同一次便携安装，已拒绝操作。");
            }
            if (record.Provenance != null)
            {
                ValidateProvenance(record.Provenance, markerPath);
            }
        }

        private static void ValidateProvenance(ArtifactProvenance provenance, string markerPath)
        {
            if (provenance.AppliedFeatures == null ||
                provenance.IncompleteFeatures == null ||
                provenance.Artifacts == null ||
                provenance.Artifacts.Count == 0)
            {
                throw new InvalidDataException("派生制品来源记录格式无效：" + markerPath);
            }
            if (!string.IsNullOrWhiteSpace(provenance.SourcePackageSha256))
            {
                byte[] digest;
                try { digest = Convert.FromBase64String(provenance.SourcePackageSha256); }
                catch (FormatException exception)
                {
                    throw new InvalidDataException("官方包 SHA-256 记录格式无效：" + markerPath, exception);
                }
                if (digest.Length != 32 ||
                    string.IsNullOrWhiteSpace(provenance.SourcePackageFullName) ||
                    string.IsNullOrWhiteSpace(provenance.SourceArchitecture))
                {
                    throw new InvalidDataException("官方包来源记录不完整：" + markerPath);
                }
            }

            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ArtifactDigest artifact in provenance.Artifacts)
            {
                if (artifact == null ||
                    string.IsNullOrWhiteSpace(artifact.RelativePath) ||
                    !paths.Add(artifact.RelativePath) ||
                    !IsSha256Hex(artifact.Sha256))
                {
                    throw new InvalidDataException("派生制品摘要记录格式无效：" + markerPath);
                }
            }

            if (provenance.CompatibilityFeatures != null)
            {
                HashSet<string> featureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (CompatibilityFeatureRecord feature in provenance.CompatibilityFeatures)
                {
                    if (feature == null ||
                        string.IsNullOrWhiteSpace(feature.FeatureId) ||
                        !featureIds.Add(feature.FeatureId) ||
                        string.IsNullOrWhiteSpace(feature.Before) ||
                        string.IsNullOrWhiteSpace(feature.Desired) ||
                        string.IsNullOrWhiteSpace(feature.After) ||
                        string.IsNullOrWhiteSpace(feature.RecipeId) ||
                        !Enum.IsDefined(typeof(CompatibilityFeatureStatus), feature.Status))
                    {
                        throw new InvalidDataException("兼容功能结果记录格式无效：" + markerPath);
                    }
                }
            }
        }

        private static bool IsSha256Hex(string value)
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

        internal static string GetMarkerPath(string installRoot)
        {
            return Path.Combine(installRoot, MarkerFileName);
        }

        private static void AtomicWrite(string destinationPath, string content)
        {
            string temporaryPath = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(content);
                    writer.Flush();
                    stream.Flush(true);
                }
                if (File.Exists(destinationPath))
                {
                    File.Replace(temporaryPath, destinationPath, null, true);
                }
                else
                {
                    File.Move(temporaryPath, destinationPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    NativeFileSystem.DeleteFile(temporaryPath);
                }
            }
        }

        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeReparsePoint = 0x00000400;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public NativeFileTime CreationTime;
            public NativeFileTime LastAccessTime;
            public NativeFileTime LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
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
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation fileInformation);
    }
}
