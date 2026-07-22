using System;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

namespace CodexPortableManager
{
    /// <summary>
    /// 对微软商店返回的 Codex 主 MSIX 建立解包前信任，并可把只读锁保持到解包结束。
    /// </summary>
    internal static class MsixPackageTrust
    {
        private const string ManifestEntryName = "AppxManifest.xml";
        private const string SignatureEntryName = "AppxSignature.p7x";
        private const int MaximumManifestCharacters = 4 * 1024 * 1024;
        private const int MaximumSignatureBytes = 4 * 1024 * 1024;
        private const int AppxSignatureHeaderLength = 4;
        private const int CryptENoRevocationCheck = unchecked((int)0x80092012);
        private const int CryptERevocationOffline = unchecked((int)0x80092013);
        private const int CryptENotInRevocationDatabase = unchecked((int)0x80092014);
        private const int CertERevocationFailure = unchecked((int)0x800B010E);
        private const int CryptEFileError = unchecked((int)0x80092003);
        private const int HResultSharingViolation = unchecked((int)0x80070020);
        private const int HResultLockViolation = unchecked((int)0x80070021);

        private static readonly Guid WinTrustActionGenericVerifyV2 =
            new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

        /// <summary>
        /// 校验并返回持有 MSIX 只读共享锁的租约。调用方应将租约保持到 staging 流式构建结束。
        /// </summary>
        internal static VerifiedArtifactLease VerifyAndLock(
            string packagePath,
            PackageMetadata metadata,
            string expectedArchitecture,
            Action<string> log,
            FileStream lockedStream = null,
            string trustedSha256Base64 = null)
        {
            if (string.IsNullOrWhiteSpace(packagePath))
            {
                throw new ArgumentException("MSIX 路径不能为空。", nameof(packagePath));
            }
            if (metadata == null)
            {
                throw new ArgumentNullException(nameof(metadata));
            }
            if (log == null) log = delegate { };

            string architecture = NormalizeArchitecture(expectedArchitecture);
            Version expectedVersion = ParseFourPartVersion(metadata.version, "微软元数据版本");
            PackageFullNameParts fullName = ParseAndValidateFullName(metadata.fullName);
            if (!string.Equals(fullName.Name, CodexMicrosoftStoreSource.PackageName, StringComparison.Ordinal) ||
                !string.Equals(fullName.Version, expectedVersion.ToString(4), StringComparison.Ordinal) ||
                !string.Equals(fullName.Architecture, architecture, StringComparison.OrdinalIgnoreCase) ||
                fullName.ResourceId.Length != 0 ||
                !string.Equals(fullName.PublisherId, CodexMicrosoftStoreSource.PublisherId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "微软元数据 fullName 与预期 Codex 主包身份不一致：" + metadata.fullName);
            }

            byte[] expectedDigest = ParseSha256Digest(metadata.digest);
            string fullPath = Path.GetFullPath(packagePath);
            EnsurePackageFileIsNotReparsePoint(fullPath);
            if (lockedStream == null && !string.IsNullOrWhiteSpace(trustedSha256Base64))
            {
                throw new InvalidDataException("只有持续持有下载文件稳定句柄时才能复用流式 SHA-256。");
            }

            FileStream packageLock = lockedStream;
            try
            {
                if (packageLock == null)
                {
                    packageLock = new FileStream(
                        fullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        1024 * 1024,
                        FileOptions.SequentialScan);
                }
                else if (!packageLock.CanRead)
                {
                    throw new InvalidDataException("下载完成后的稳定程序包句柄不可读。");
                }

                // 允许便携目录位于 junction/symlink 祖先下。后续路径操作必须使用从句柄解析的稳定路径，
                // 防止祖先重解析点在租约存续期间换向到另一个文件。
                string stablePath = NativeFileSystem.GetStablePathFromHandle(packageLock.SafeFileHandle);
                EnsurePackageFileIsNotReparsePoint(fullPath);

                if (metadata.sizeInBytes > 0 && packageLock.Length != metadata.sizeInBytes)
                {
                    string message = "MSIX 文件大小与微软元数据不一致：" +
                        packageLock.Length + " != " + metadata.sizeInBytes + "。";
                    throw new InvalidDataException(message, new MsixPackageDigestMismatchException(message));
                }

                byte[] actualDigest = string.IsNullOrWhiteSpace(trustedSha256Base64)
                    ? ComputeSha256(packageLock)
                    : ParseSha256Digest(trustedSha256Base64);
                if (!FixedTimeEquals(expectedDigest, actualDigest))
                {
                    string message = "MSIX SHA-256 与微软元数据不一致。期望 " +
                        Convert.ToBase64String(expectedDigest) + "，实际 " +
                        Convert.ToBase64String(actualDigest) + "。";
                    throw new InvalidDataException(message, new MsixPackageDigestMismatchException(message));
                }

                VerifyPackageSignature(stablePath, packageLock.SafeFileHandle, log);
                ManifestIdentity identity = ReadManifestIdentity(packageLock);
                ValidateManifestIdentity(identity, expectedVersion, architecture);
                ValidateSignaturePublisher(identity.SignerSubjectName, identity.Publisher);

                log(
                    "MSIX 官方完整性校验通过：" + metadata.fullName +
                    "；Publisher=" + identity.Publisher +
                    "；Architecture=" + identity.Architecture +
                    "；SHA-256=" + ToHex(expectedDigest) + "。" );

                VerifiedArtifactLease lease = new VerifiedArtifactLease(stablePath, packageLock);
                packageLock = null;
                return lease;
            }
            catch
            {
                if (packageLock != null) packageLock.Dispose();
                throw;
            }
        }

        private static void ValidateManifestIdentity(
            ManifestIdentity identity,
            Version expectedVersion,
            string expectedArchitecture)
        {
            if (!string.Equals(identity.Name, CodexMicrosoftStoreSource.PackageName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("MSIX Manifest 包名不是 " + CodexMicrosoftStoreSource.PackageName + "：" + identity.Name);
            }
            Version manifestVersion = ParseFourPartVersion(identity.Version, "MSIX Manifest 版本");
            if (manifestVersion != expectedVersion)
            {
                throw new InvalidDataException(
                    "MSIX Manifest 版本与微软元数据不一致：" + identity.Version + " != " + expectedVersion.ToString(4));
            }
            if (!string.Equals(identity.Publisher, CodexMicrosoftStoreSource.StorePublisher, StringComparison.Ordinal))
            {
                throw new InvalidDataException("MSIX Manifest Publisher 不受信任：" + identity.Publisher);
            }
            if (!string.Equals(identity.Architecture, expectedArchitecture, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "MSIX Manifest ProcessorArchitecture 与请求架构不一致：" +
                    identity.Architecture + " != " + expectedArchitecture);
            }
        }

        private static ManifestIdentity ReadManifestIdentity(FileStream packageLock)
        {
            packageLock.Position = 0;
            using (ZipArchive archive = new ZipArchive(packageLock, ZipArchiveMode.Read, true))
            {
                ZipArchiveEntry manifestEntry = GetUniqueEntry(archive, ManifestEntryName);
                ZipArchiveEntry signatureEntry = GetUniqueEntry(archive, SignatureEntryName);
                if (manifestEntry.Length <= 0 || manifestEntry.Length > MaximumManifestCharacters)
                {
                    throw new InvalidDataException("MSIX AppxManifest.xml 大小无效：" + manifestEntry.Length);
                }
                if (signatureEntry.Length <= 0)
                {
                    throw new InvalidDataException("MSIX AppxSignature.p7x 为空。");
                }

                X500DistinguishedName signerSubjectName =
                    DecodeAppxSignatureSignerSubject(ReadSignatureEntry(signatureEntry));

                XmlReaderSettings settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaximumManifestCharacters,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true
                };
                XDocument document;
                using (Stream input = manifestEntry.Open())
                using (XmlReader reader = XmlReader.Create(input, settings))
                {
                    document = XDocument.Load(reader, LoadOptions.None);
                }

                XNamespace packageNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
                XElement identity = document.Root == null
                    ? null
                    : document.Root.Element(packageNamespace + "Identity");
                if (identity == null)
                {
                    throw new InvalidDataException("MSIX AppxManifest.xml 缺少 Identity。" );
                }

                return new ManifestIdentity
                {
                    Name = RequiredAttribute(identity, "Name"),
                    Version = RequiredAttribute(identity, "Version"),
                    Publisher = RequiredAttribute(identity, "Publisher"),
                    Architecture = RequiredAttribute(identity, "ProcessorArchitecture"),
                    SignerSubjectName = signerSubjectName
                };
            }
        }

        private static byte[] ReadSignatureEntry(ZipArchiveEntry signatureEntry)
        {
            if (signatureEntry.Length <= AppxSignatureHeaderLength ||
                signatureEntry.Length > MaximumSignatureBytes)
            {
                throw new InvalidDataException(
                    "MSIX AppxSignature.p7x 大小无效：" + signatureEntry.Length);
            }

            byte[] bytes = new byte[checked((int)signatureEntry.Length)];
            int offset = 0;
            using (Stream input = signatureEntry.Open())
            {
                while (offset < bytes.Length)
                {
                    int read = input.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0)
                    {
                        throw new InvalidDataException("MSIX AppxSignature.p7x 提前结束。");
                    }
                    offset += read;
                }
            }
            return bytes;
        }

        internal static X500DistinguishedName DecodeAppxSignatureSignerSubject(byte[] signatureBytes)
        {
            if (signatureBytes == null)
            {
                throw new ArgumentNullException(nameof(signatureBytes));
            }
            if (signatureBytes.Length <= AppxSignatureHeaderLength ||
                signatureBytes.Length > MaximumSignatureBytes)
            {
                throw new InvalidDataException(
                    "MSIX AppxSignature.p7x 大小无效：" + signatureBytes.Length);
            }
            if (signatureBytes[0] != (byte)'P' ||
                signatureBytes[1] != (byte)'K' ||
                signatureBytes[2] != (byte)'C' ||
                signatureBytes[3] != (byte)'X')
            {
                throw new InvalidDataException("MSIX AppxSignature.p7x 缺少 PKCX 标头。");
            }

            byte[] cmsBytes = new byte[signatureBytes.Length - AppxSignatureHeaderLength];
            Buffer.BlockCopy(
                signatureBytes,
                AppxSignatureHeaderLength,
                cmsBytes,
                0,
                cmsBytes.Length);

            SignedCms signedCms = new SignedCms();
            try
            {
                signedCms.Decode(cmsBytes);
                // WinVerifyTrust 负责证书链与吊销状态；这里复验 PKCS#7 内容签名并提取绑定的签名者。
                signedCms.CheckSignature(true);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException(
                    "MSIX AppxSignature.p7x 不是有效的 PKCS#7 签名。",
                    exception);
            }

            if (signedCms.SignerInfos.Count != 1)
            {
                throw new InvalidDataException(
                    "MSIX AppxSignature.p7x 必须包含唯一签名者，实际为 " +
                    signedCms.SignerInfos.Count + "。");
            }

            X509Certificate2 signerCertificate = signedCms.SignerInfos[0].Certificate;
            if (signerCertificate == null)
            {
                throw new InvalidDataException("MSIX AppxSignature.p7x 缺少签名者证书。");
            }
            return new X500DistinguishedName(signerCertificate.SubjectName.RawData);
        }

        private static ZipArchiveEntry GetUniqueEntry(ZipArchive archive, string expectedName)
        {
            ZipArchiveEntry match = null;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!string.Equals(entry.FullName, expectedName, StringComparison.OrdinalIgnoreCase)) continue;
                if (match != null)
                {
                    throw new InvalidDataException("MSIX 包含重复条目：" + expectedName);
                }
                match = entry;
            }
            if (match == null)
            {
                throw new InvalidDataException("MSIX 缺少必要条目：" + expectedName);
            }
            return match;
        }

        private static string RequiredAttribute(XElement element, string name)
        {
            string value = (string)element.Attribute(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("MSIX Manifest Identity 缺少 " + name + "。" );
            }
            return value.Trim();
        }

        private static void VerifyPackageSignature(
            string path,
            SafeFileHandle fileHandle,
            Action<string> log)
        {
            int trustResult = RetryTransientFileTrust(
                () => VerifyEmbeddedSignature(path, fileHandle, true),
                delay => Thread.Sleep(delay),
                log);
            if (trustResult == 0) return;

            if (!IsRevocationStatusUnavailable(trustResult))
            {
                throw new InvalidDataException(
                    "MSIX AppxSignature 信任校验失败，WinVerifyTrust=0x" +
                    unchecked((uint)trustResult).ToString("X8") + "。" );
            }

            // 仅在严格校验明确表示“无法取得吊销状态”时重验一次；
            // CERT_E_REVOKED、摘要错误、无签名和不可信链等错误不会进入此分支。
            int fallbackResult = RetryTransientFileTrust(
                () => VerifyEmbeddedSignature(path, fileHandle, false),
                delay => Thread.Sleep(delay),
                log);
            if (fallbackResult != 0)
            {
                throw new InvalidDataException(
                    "MSIX 吊销状态不可获取，且基础签名链校验失败。严格结果=0x" +
                    unchecked((uint)trustResult).ToString("X8") +
                    "，基础结果=0x" + unchecked((uint)fallbackResult).ToString("X8") + "。" );
            }

            log(
                "警告：MSIX 签名链有效，但系统无法取得证书吊销状态（WinVerifyTrust=0x" +
                unchecked((uint)trustResult).ToString("X8") +
                "）；本次仅跳过吊销状态检查，签名、摘要、Publisher 和包身份仍严格校验。" );
        }

        private static void ValidateSignaturePublisher(
            X500DistinguishedName certificateSubject,
            string manifestPublisher)
        {
            if (certificateSubject == null)
            {
                throw new ArgumentNullException(nameof(certificateSubject));
            }
            X500DistinguishedName manifestName = new X500DistinguishedName(manifestPublisher);
            if (!FixedTimeEquals(manifestName.RawData, certificateSubject.RawData))
            {
                throw new InvalidDataException(
                    "MSIX 签名证书 Subject 与 Manifest Publisher 不一致：" +
                    certificateSubject.Name + " != " + manifestPublisher);
            }
            X500DistinguishedName expectedName =
                new X500DistinguishedName(CodexMicrosoftStoreSource.StorePublisher);
            if (!FixedTimeEquals(expectedName.RawData, certificateSubject.RawData))
            {
                throw new InvalidDataException(
                    "MSIX 签名证书 Subject 不属于预期 Store Publisher：" +
                    certificateSubject.Name);
            }
        }

        internal static bool IsRevocationStatusUnavailable(int trustResult)
        {
            return trustResult == CryptENoRevocationCheck ||
                trustResult == CryptERevocationOffline ||
                trustResult == CryptENotInRevocationDatabase ||
                trustResult == CertERevocationFailure;
        }

        internal static int RetryTransientFileTrust(
            Func<int> verify,
            Action<TimeSpan> delay,
            Action<string> log)
        {
            if (verify == null) throw new ArgumentNullException(nameof(verify));
            if (delay == null) throw new ArgumentNullException(nameof(delay));
            if (log == null) log = delegate { };
            int[] delaysInSeconds = { 1, 2, 4, 8 };
            int result = verify();
            for (int attempt = 0;
                attempt < delaysInSeconds.Length && IsTransientTrustFileAccessError(result);
                attempt++)
            {
                TimeSpan wait = TimeSpan.FromSeconds(delaysInSeconds[attempt]);
                log(
                    "Windows 签名校验暂时无法读取刚发布的 MSIX（WinVerifyTrust=0x" +
                    unchecked((uint)result).ToString("X8") + "），将在 " +
                    delaysInSeconds[attempt] + " 秒后重试。");
                delay(wait);
                result = verify();
            }
            return result;
        }

        private static bool IsTransientTrustFileAccessError(int trustResult)
        {
            return trustResult == CryptEFileError ||
                trustResult == HResultSharingViolation ||
                trustResult == HResultLockViolation;
        }

        private static int VerifyEmbeddedSignature(
            string path,
            SafeFileHandle fileHandle,
            bool checkRevocation)
        {
            try
            {
                using (WinTrustFileInfo fileInfo = new WinTrustFileInfo(path, fileHandle))
                using (WinTrustData trustData = new WinTrustData(fileInfo, checkRevocation))
                {
                    return WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, trustData);
                }
            }
            finally
            {
                // WinTrustFileInfo 只携带原生句柄值；确保 SafeHandle 至少存活到 P/Invoke 返回。
                GC.KeepAlive(fileHandle);
            }
        }

        private static PackageFullNameParts ParseAndValidateFullName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("微软元数据缺少 package.fullName。" );
            }
            string fullName = value.Trim();
            int publisherSeparator = fullName.LastIndexOf('_');
            int resourceSeparator = publisherSeparator <= 0 ? -1 : fullName.LastIndexOf('_', publisherSeparator - 1);
            int architectureSeparator = resourceSeparator <= 0 ? -1 : fullName.LastIndexOf('_', resourceSeparator - 1);
            int versionSeparator = architectureSeparator <= 0 ? -1 : fullName.LastIndexOf('_', architectureSeparator - 1);
            if (versionSeparator <= 0 ||
                architectureSeparator <= versionSeparator + 1 ||
                resourceSeparator <= architectureSeparator + 1 ||
                publisherSeparator < resourceSeparator + 1 ||
                publisherSeparator == fullName.Length - 1)
            {
                throw new InvalidDataException("微软元数据 package.fullName 格式无效：" + fullName);
            }

            PackageFullNameParts parts = new PackageFullNameParts
            {
                Name = fullName.Substring(0, versionSeparator),
                Version = fullName.Substring(versionSeparator + 1, architectureSeparator - versionSeparator - 1),
                Architecture = fullName.Substring(architectureSeparator + 1, resourceSeparator - architectureSeparator - 1),
                ResourceId = fullName.Substring(resourceSeparator + 1, publisherSeparator - resourceSeparator - 1),
                PublisherId = fullName.Substring(publisherSeparator + 1)
            };
            ParseFourPartVersion(parts.Version, "微软元数据 fullName 版本");
            return parts;
        }

        private static Version ParseFourPartVersion(string value, string description)
        {
            Version parsed;
            if (!Version.TryParse(value, out parsed) ||
                parsed.Major < 0 || parsed.Minor < 0 || parsed.Build < 0 || parsed.Revision < 0)
            {
                throw new InvalidDataException(description + "无效：" + (value ?? "<null>"));
            }
            return parsed;
        }

        private static string NormalizeArchitecture(string value)
        {
            string architecture = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (architecture != "x64" && architecture != "arm64")
            {
                throw new InvalidDataException("不支持的目标架构：" + (value ?? "<null>"));
            }
            return architecture;
        }

        private static byte[] ParseSha256Digest(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("微软元数据缺少 SHA-256 digest。" );
            }
            try
            {
                byte[] digest = Convert.FromBase64String(value.Trim());
                if (digest.Length != 32)
                {
                    throw new InvalidDataException("微软元数据 digest 不是 SHA-256。" );
                }
                return digest;
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("微软元数据 digest 不是有效 Base64。", exception);
            }
        }

        private static byte[] ComputeSha256(FileStream input)
        {
            input.Position = 0;
            byte[] hash;
            using (SHA256 algorithm = SHA256.Create())
            {
                hash = algorithm.ComputeHash(input);
            }
            input.Position = 0;
            return hash;
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null) return false;
            int difference = left.Length ^ right.Length;
            int length = Math.Min(left.Length, right.Length);
            for (int index = 0; index < length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }

        private static string ToHex(byte[] value)
        {
            return BitConverter.ToString(value).Replace("-", string.Empty);
        }

        private static void EnsurePackageFileIsNotReparsePoint(string filePath)
        {
            FileInfo file = new FileInfo(filePath);
            if (!file.Exists)
            {
                throw new FileNotFoundException("没有找到待校验的 MSIX。", filePath);
            }
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("MSIX 文件不能是重解析点：" + filePath);
            }
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int WinVerifyTrust(
            IntPtr windowHandle,
            [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
            [In, Out] WinTrustData trustData);

        private enum WinTrustDataStateAction : uint
        {
            Ignore = 0
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustFileInfo : IDisposable
        {
            public WinTrustFileInfo(string filePath, SafeFileHandle fileHandle)
            {
                StructSize = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo));
                FilePath = Marshal.StringToCoTaskMemUni(filePath);
                FileHandle = fileHandle == null || fileHandle.IsInvalid
                    ? IntPtr.Zero
                    : fileHandle.DangerousGetHandle();
            }

            public uint StructSize;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;

            public void Dispose()
            {
                if (FilePath != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(FilePath);
                    FilePath = IntPtr.Zero;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustData : IDisposable
        {
            public WinTrustData(WinTrustFileInfo fileInfo, bool checkRevocation)
            {
                StructSize = (uint)Marshal.SizeOf(typeof(WinTrustData));
                UIChoice = 2; // WTD_UI_NONE
                RevocationChecks = checkRevocation ? 1u : 0u; // WTD_REVOKE_WHOLECHAIN / NONE
                UnionChoice = 1; // WTD_CHOICE_FILE
                StateAction = WinTrustDataStateAction.Ignore;
                ProviderFlags = (checkRevocation ? 0x00000080u : 0u) | 0x00002000u;
                File = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(fileInfo, File, false);
            }

            public uint StructSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UIChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr File;
            public WinTrustDataStateAction StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UIContext;

            public void Dispose()
            {
                if (File != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(File);
                    File = IntPtr.Zero;
                }
            }
        }

        private sealed class ManifestIdentity
        {
            public string Name { get; set; }
            public string Version { get; set; }
            public string Publisher { get; set; }
            public string Architecture { get; set; }
            public X500DistinguishedName SignerSubjectName { get; set; }
        }

        private sealed class PackageFullNameParts
        {
            public string Name { get; set; }
            public string Version { get; set; }
            public string Architecture { get; set; }
            public string ResourceId { get; set; }
            public string PublisherId { get; set; }
        }
    }

    internal sealed class MsixPackageDigestMismatchException : Exception
    {
        internal MsixPackageDigestMismatchException(string message)
            : base(message)
        {
        }
    }

    internal sealed class VerifiedArtifactLease : IDisposable
    {
        private FileStream packageLock;

        internal VerifiedArtifactLease(string packagePath, FileStream lockedStream)
        {
            PackagePath = packagePath;
            packageLock = lockedStream ?? throw new ArgumentNullException(nameof(lockedStream));
        }

        public string PackagePath { get; private set; }

        public void Dispose()
        {
            FileStream stream = packageLock;
            packageLock = null;
            if (stream != null) stream.Dispose();
        }
    }
}
