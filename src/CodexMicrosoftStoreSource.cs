using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPortableManager
{
    internal sealed class CodexMicrosoftStoreSource
    {
        internal const string ProductId = "9PLM9XGG6VKS";
        internal const string PackageName = "OpenAI.Codex";
        internal const string PublisherId = "2p2nqsd0c76g0";
        internal const string PackageFamilyName = PackageName + "_" + PublisherId;
        internal const string StorePublisher = "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B";

        private readonly MicrosoftStoreProtocolClient protocolClient;
        private readonly TimeSpan publicationRetryDelay;

        internal CodexMicrosoftStoreSource(MicrosoftStoreProtocolClient client)
            : this(client, TimeSpan.FromSeconds(2))
        {
        }

        internal CodexMicrosoftStoreSource(
            MicrosoftStoreProtocolClient client,
            TimeSpan syncRetryDelay)
        {
            protocolClient = client ?? throw new ArgumentNullException(nameof(client));
            publicationRetryDelay = syncRetryDelay < TimeSpan.Zero ? TimeSpan.Zero : syncRetryDelay;
        }

        internal Task<PackageMetadata> ResolveLatestAsync(CancellationToken cancellationToken)
        {
            return ResolveLatestAsync(GetCurrentArchitecture(), cancellationToken);
        }

        internal async Task<PackageMetadata> ResolveLatestAsync(
            string architecture,
            CancellationToken cancellationToken)
        {
            architecture = NormalizeArchitecture(architecture);
            MicrosoftStoreProtocolClient.CatalogProduct product = await protocolClient
                .GetCatalogProductAsync(ProductId, cancellationToken)
                .ConfigureAwait(false);
            CatalogSelection selection = SelectLatestPackage(
                product,
                architecture,
                GetCurrentWindowsPlatformVersion());
            MicrosoftStoreProtocolClient.DeliveryFile delivery = null;
            MicrosoftStorePublicationPendingException publicationPending = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    delivery = await protocolClient.ResolvePackageFileAsync(
                        selection.WuCategoryId,
                        selection.Metadata.fullName,
                        architecture,
                        cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (MicrosoftStorePublicationPendingException exception)
                {
                    publicationPending = exception;
                    if (attempt == 1)
                    {
                        throw new MicrosoftStorePublicationPendingException(
                            "微软正在同步 Codex " + selection.Metadata.version +
                            "，目录与 Windows Update 暂时尚未一致，请稍后重试。",
                            exception);
                    }
                    if (publicationRetryDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(publicationRetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            if (delivery == null)
            {
                if (publicationPending != null) throw publicationPending;
                throw new InvalidOperationException("Codex 程序包分发结果为空。");
            }
            ValidateCatalogAndDelivery(selection.Metadata, delivery);
            selection.Metadata.url = delivery.Url;
            return selection.Metadata;
        }

        internal static CatalogSelection SelectLatestPackage(
            MicrosoftStoreProtocolClient.CatalogProduct product,
            string architecture,
            long platformVersion)
        {
            if (product == null)
            {
                throw new InvalidDataException("微软商店目录没有返回 Codex 产品数据。");
            }
            architecture = NormalizeArchitecture(architecture);
            if (!string.Equals(product.ProductId, ProductId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(product.PackageIdentityName, PackageName, StringComparison.Ordinal) ||
                !string.Equals(product.PackageFamilyName, PackageFamilyName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("微软商店目录返回了不匹配的 Codex 程序包身份。");
            }

            List<CatalogSelection> candidates = new List<CatalogSelection>();
            foreach (MicrosoftStoreProtocolClient.CatalogPackage package in
                product.Packages ?? new List<MicrosoftStoreProtocolClient.CatalogPackage>())
            {
                Guid categoryId;
                Version version;
                if (package == null ||
                    !Guid.TryParse(package.WuCategoryId, out categoryId) ||
                    !TryParsePackageFullName(package, architecture, out version) ||
                    !SupportsWindowsDesktop(package.PlatformDependencies, platformVersion) ||
                    !IsValidSha256(package.Hash) ||
                    !string.Equals(package.HashAlgorithm, "SHA256", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(package.PackageFormat, "Msix", StringComparison.OrdinalIgnoreCase) ||
                    package.MaxDownloadSizeInBytes <= 0)
                {
                    continue;
                }

                candidates.Add(new CatalogSelection
                {
                    Version = version,
                    WuCategoryId = categoryId.ToString("D"),
                    Metadata = new PackageMetadata
                    {
                        packageName = PackageName,
                        architecture = architecture,
                        version = version.ToString(4),
                        fullName = package.PackageFullName,
                        digest = package.Hash,
                        sizeInBytes = package.MaxDownloadSizeInBytes
                    }
                });
            }

            CatalogSelection latest = candidates
                .OrderByDescending(candidate => candidate.Version)
                .FirstOrDefault();
            if (latest == null)
            {
                throw new InvalidDataException("微软商店目录没有返回适用于当前架构的 Codex MSIX 主包。");
            }

            foreach (CatalogSelection duplicate in candidates.Where(candidate => candidate.Version == latest.Version))
            {
                if (!string.Equals(duplicate.Metadata.fullName, latest.Metadata.fullName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(duplicate.Metadata.digest, latest.Metadata.digest, StringComparison.Ordinal) ||
                    duplicate.Metadata.sizeInBytes != latest.Metadata.sizeInBytes)
                {
                    throw new InvalidDataException("微软商店目录对 Codex 最新版本返回了相互冲突的程序包元数据。");
                }
            }
            return latest;
        }

        internal static string GetCurrentArchitecture()
        {
            string architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ??
                Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? string.Empty;
            return architecture.IndexOf("ARM64", StringComparison.OrdinalIgnoreCase) >= 0 ? "arm64" : "x64";
        }

        private static void ValidateCatalogAndDelivery(
            PackageMetadata catalog,
            MicrosoftStoreProtocolClient.DeliveryFile delivery)
        {
            if (delivery == null ||
                catalog.sizeInBytes != delivery.SizeInBytes ||
                !string.Equals(catalog.digest, delivery.Sha256Digest, StringComparison.Ordinal))
            {
                throw new InvalidDataException("微软商店目录与 Windows Update 返回的 Codex 文件元数据不一致。");
            }
        }

        private static bool TryParsePackageFullName(
            MicrosoftStoreProtocolClient.CatalogPackage package,
            string architecture,
            out Version version)
        {
            version = null;
            if (package.Architectures == null ||
                !package.Architectures.Any(value => string.Equals(value, architecture, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            Match match = Regex.Match(
                package.PackageFullName ?? string.Empty,
                "^" + Regex.Escape(PackageName) + "_(?<version>[0-9]+(?:\\.[0-9]+){3})_(?<architecture>x64|arm64)__(?<publisher>[a-z0-9]+)$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            return match.Success &&
                string.Equals(match.Groups["architecture"].Value, architecture, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(match.Groups["publisher"].Value, PublisherId, StringComparison.OrdinalIgnoreCase) &&
                Version.TryParse(match.Groups["version"].Value, out version);
        }

        private static bool SupportsWindowsDesktop(
            List<MicrosoftStoreProtocolClient.CatalogPlatformDependency> dependencies,
            long platformVersion)
        {
            return platformVersion > 0 && dependencies != null && dependencies.Any(dependency =>
                dependency != null &&
                string.Equals(dependency.PlatformName, "Windows.Desktop", StringComparison.OrdinalIgnoreCase) &&
                dependency.MinVersion > 0 &&
                platformVersion >= dependency.MinVersion);
        }

        private static bool IsValidSha256(string value)
        {
            try
            {
                return Convert.FromBase64String(value ?? string.Empty).Length == 32;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static string NormalizeArchitecture(string architecture)
        {
            if (string.Equals(architecture, "x64", StringComparison.OrdinalIgnoreCase)) return "x64";
            if (string.Equals(architecture, "arm64", StringComparison.OrdinalIgnoreCase)) return "arm64";
            throw new ArgumentException("不支持的 Codex 程序包架构：" + architecture, nameof(architecture));
        }

        internal static long GetCurrentWindowsPlatformVersion()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        long major = Convert.ToInt64(key.GetValue("CurrentMajorVersionNumber", 10), CultureInfo.InvariantCulture);
                        long minor = Convert.ToInt64(key.GetValue("CurrentMinorVersionNumber", 0), CultureInfo.InvariantCulture);
                        long build = Convert.ToInt64(key.GetValue("CurrentBuildNumber", "0"), CultureInfo.InvariantCulture);
                        long revision = Convert.ToInt64(key.GetValue("UBR", 0), CultureInfo.InvariantCulture);
                        if (major >= 0 && major <= ushort.MaxValue && minor >= 0 && minor <= ushort.MaxValue &&
                            build >= 0 && build <= ushort.MaxValue && revision >= 0)
                        {
                            return (major << 48) | (minor << 32) | (build << 16) | (revision & ushort.MaxValue);
                        }
                    }
                }
            }
            catch
            {
            }

            Version version = Environment.OSVersion.Version;
            if (version.Major < 0 || version.Major > ushort.MaxValue ||
                version.Minor < 0 || version.Minor > ushort.MaxValue ||
                version.Build < 0 || version.Build > ushort.MaxValue)
            {
                throw new InvalidOperationException("无法确定当前 Windows 平台版本。");
            }
            return ((long)version.Major << 48) |
                ((long)version.Minor << 32) |
                ((long)version.Build << 16) |
                ((long)Math.Max(0, version.Revision) & ushort.MaxValue);
        }

        internal sealed class CatalogSelection
        {
            public Version Version { get; set; }
            public string WuCategoryId { get; set; }
            public PackageMetadata Metadata { get; set; }
        }
    }
}
