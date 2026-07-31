using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CodexPortableManager
{
    internal sealed class InstallationIdentity
    {
        public string InstallId { get; set; }
        public string PackageName { get; set; }
        public string PackageVersion { get; set; }
    }

    internal sealed class InstallationRecord
    {
        public InstallationIdentity Identity { get; set; }
        public ArtifactProvenance Provenance { get; set; }
        public string UpdatedUtc { get; set; }
    }

    internal interface ITrustedArtifactDigestSource
    {
        bool TryGetTrustedDigest(string root, string relativePath, out string sha256);
    }

    internal sealed class ArtifactProvenance
    {
        public string SourcePackageFullName { get; set; }
        public string SourcePackageSha256 { get; set; }
        public string SourceArchitecture { get; set; }
        public List<string> AppliedFeatures { get; set; }
        public List<string> IncompleteFeatures { get; set; }
        public List<CompatibilityFeatureRecord> CompatibilityFeatures { get; set; }
        public List<ArtifactDigest> Artifacts { get; set; }

        internal static ArtifactProvenance Capture(
            string installRoot,
            PackageProfile profile,
            PackageMetadata sourcePackage,
            ArtifactProvenance previousSource)
        {
            return CaptureCore(
                installRoot,
                profile,
                sourcePackage,
                previousSource,
                null,
                null,
                null);
        }

        internal static ArtifactProvenance Capture(
            string installRoot,
            PackageProfile profile,
            PackageMetadata sourcePackage,
            ArtifactProvenance previousSource,
            ITrustedArtifactDigestSource trustedDigestSource)
        {
            if (trustedDigestSource == null) throw new ArgumentNullException(nameof(trustedDigestSource));
            return CaptureCore(
                installRoot,
                profile,
                sourcePackage,
                previousSource,
                null,
                null,
                trustedDigestSource);
        }

        internal static ArtifactProvenance Capture(
            string installRoot,
            PackageProfile profile,
            PackageMetadata sourcePackage,
            ArtifactProvenance previousSource,
            CompatibilityOptions options,
            CompatibilityResult result)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (result == null) throw new ArgumentNullException(nameof(result));
            return CaptureCore(
                installRoot,
                profile,
                sourcePackage,
                previousSource,
                options,
                result,
                null);
        }

        internal static ArtifactProvenance UpdateCompatibilityArtifacts(
            string installRoot,
            ArtifactProvenance previous,
            CompatibilityOptions options,
            CompatibilityResult result,
            IEnumerable<CompatibilityArtifactState> changedArtifacts)
        {
            if (previous == null) throw new ArgumentNullException(nameof(previous));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (changedArtifacts == null) throw new ArgumentNullException(nameof(changedArtifacts));

            ArtifactProvenance updated = Clone(previous);
            HashSet<string> managedFeatures = GetManagedCompatibilityFeatureIds(options);
            updated.AppliedFeatures = updated.AppliedFeatures
                .Where(feature => !managedFeatures.Contains(feature))
                .ToList();
            AddAppliedCompatibilityFeatures(updated.AppliedFeatures, result);
            HashSet<string> managedDisplayNames = GetManagedCompatibilityDisplayNames(options);
            updated.IncompleteFeatures = updated.IncompleteFeatures
                .Where(feature => !managedDisplayNames.Contains(feature))
                .Concat(result.FailedFeatures.Where(feature =>
                    managedDisplayNames.Contains(feature)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            updated.CompatibilityFeatures = updated.CompatibilityFeatures
                .Where(feature => feature != null &&
                    !managedFeatures.Contains(feature.FeatureId))
                .Concat(CaptureFeatureRecords(result).Where(feature =>
                    managedFeatures.Contains(feature.FeatureId)))
                .ToList();
            string root = Path.GetFullPath(installRoot);
            Dictionary<string, CompatibilityArtifactState> changed = changedArtifacts
                .Where(artifact => artifact != null)
                .ToDictionary(
                    artifact => NormalizeRelativePath(artifact.RelativePath),
                    artifact => artifact,
                    StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, CompatibilityArtifactState> pair in changed)
            {
                string relativePath = pair.Key;
                CompatibilityArtifactState state = pair.Value;
                ResolveRelativePath(root, relativePath);
                updated.Artifacts.RemoveAll(artifact => string.Equals(
                    artifact.RelativePath,
                    relativePath,
                    StringComparison.OrdinalIgnoreCase));
                if (state.Exists)
                {
                    if (string.IsNullOrWhiteSpace(state.Sha256))
                    {
                        throw new InvalidDataException("兼容维护制品缺少事务捕获的目标摘要：" + relativePath);
                    }
                    updated.Artifacts.Add(new ArtifactDigest
                    {
                        RelativePath = relativePath,
                        Sha256 = state.Sha256
                    });
                }
            }

            return updated;
        }

        private static ArtifactProvenance CaptureCore(
            string installRoot,
            PackageProfile profile,
            PackageMetadata sourcePackage,
            ArtifactProvenance previousSource,
            CompatibilityOptions options,
            CompatibilityResult result,
            ITrustedArtifactDigestSource trustedDigestSource)
        {
            if (string.IsNullOrWhiteSpace(installRoot)) throw new ArgumentException("安装目录不能为空。", nameof(installRoot));
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            string root = Path.GetFullPath(installRoot);
            string executable = PackageProfileReader.GetExecutablePath(root, profile);
            string executableRelativePath = NormalizeRelativePath(profile.ExecutableRelativePath);
            string executableDirectory = Path.GetDirectoryName(executable);
            if (string.IsNullOrWhiteSpace(executableDirectory))
            {
                throw new InvalidDataException("无法确定主程序目录，不能记录派生制品来源。");
            }

            List<string> features = new List<string>();
            if (options != null && result != null)
            {
                AddAppliedCompatibilityFeatures(features, result);
            }

            string stableIcon = Path.Combine(root, "Codex.ico");
            if (File.Exists(stableIcon) && IconResourcePatcher.HaveSameIconsFromIco(stableIcon, executable))
            {
                features.Add("VisualIcons");
            }

            ArtifactProvenance provenance = new ArtifactProvenance
            {
                SourcePackageFullName = sourcePackage != null
                    ? sourcePackage.fullName
                    : previousSource == null ? null : previousSource.SourcePackageFullName,
                SourcePackageSha256 = sourcePackage != null
                    ? sourcePackage.digest
                    : previousSource == null ? null : previousSource.SourcePackageSha256,
                SourceArchitecture = sourcePackage != null
                    ? sourcePackage.architecture
                    : previousSource == null ? null : previousSource.SourceArchitecture,
                AppliedFeatures = features,
                IncompleteFeatures = result == null ? new List<string>() : result.FailedFeatures.ToList(),
                CompatibilityFeatures = result == null
                    ? new List<CompatibilityFeatureRecord>()
                    : CaptureFeatureRecords(result),
                Artifacts = new List<ArtifactDigest>()
            };

            AddRequiredArtifact(provenance, root, "AppxManifest.xml", trustedDigestSource);
            AddRequiredArtifact(provenance, root, executableRelativePath, trustedDigestSource);
            string executableDirectoryRelative = NormalizeRelativePath(Path.GetDirectoryName(profile.ExecutableRelativePath));
            AddRequiredArtifact(provenance, root, CombineRelative(executableDirectoryRelative, "resources/app.asar"), trustedDigestSource);
            AddRequiredArtifact(provenance, root, CombineRelative(executableDirectoryRelative, "resources/codex.exe"), trustedDigestSource);
            AddOptionalArtifact(provenance, root, "Codex.ico", trustedDigestSource);
            AddOptionalArtifact(provenance, root, CombineRelative(executableDirectoryRelative, "resources/icon-chatgpt.ico"), trustedDigestSource);
            AddOptionalArtifact(provenance, root, CombineRelative(executableDirectoryRelative, "resources/codex-windows-sandbox-setup.exe"), trustedDigestSource);
            return provenance;
        }

        private static void AddRequiredArtifact(
            ArtifactProvenance provenance,
            string root,
            string relativePath,
            ITrustedArtifactDigestSource trustedDigestSource)
        {
            string fullPath = ResolveRelativePath(root, relativePath);
            FileInfo file = new FileInfo(fullPath);
            if (!file.Exists || file.Length == 0)
            {
                throw new InvalidDataException("无法记录缺失或为空的关键派生制品：" + relativePath);
            }
            provenance.Artifacts.Add(CreateDigest(relativePath, fullPath, root, trustedDigestSource));
        }

        private static void AddOptionalArtifact(
            ArtifactProvenance provenance,
            string root,
            string relativePath,
            ITrustedArtifactDigestSource trustedDigestSource)
        {
            string fullPath = ResolveRelativePath(root, relativePath);
            if (File.Exists(fullPath))
            {
                provenance.Artifacts.Add(CreateDigest(relativePath, fullPath, root, trustedDigestSource));
            }
        }

        private static ArtifactDigest CreateDigest(
            string relativePath,
            string fullPath,
            string root,
            ITrustedArtifactDigestSource trustedDigestSource)
        {
            string sha256;
            if (trustedDigestSource == null ||
                !trustedDigestSource.TryGetTrustedDigest(root, relativePath, out sha256))
            {
                sha256 = ArtifactHash.ComputeSha256(fullPath);
            }
            return new ArtifactDigest
            {
                RelativePath = NormalizeRelativePath(relativePath),
                Sha256 = sha256
            };
        }

        internal static string ResolveRelativePath(string root, string relativePath)
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string normalizedRelative = NormalizeRelativePath(relativePath);
            if (string.IsNullOrWhiteSpace(normalizedRelative) || Path.IsPathRooted(normalizedRelative))
            {
                throw new InvalidDataException("派生制品相对路径无效：" + relativePath);
            }
            string fullPath = Path.GetFullPath(Path.Combine(root, normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("派生制品路径越出安装目录：" + relativePath);
            }
            return fullPath;
        }

        private static string CombineRelative(string parent, string child)
        {
            if (string.IsNullOrWhiteSpace(parent)) return NormalizeRelativePath(child);
            return NormalizeRelativePath(parent).TrimEnd('/') + "/" + NormalizeRelativePath(child).TrimStart('/');
        }

        internal static string NormalizeRelativePath(string path)
        {
            return (path ?? string.Empty)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')
                .TrimStart('/');
        }

        private static ArtifactProvenance Clone(ArtifactProvenance source)
        {
            return new ArtifactProvenance
            {
                SourcePackageFullName = source.SourcePackageFullName,
                SourcePackageSha256 = source.SourcePackageSha256,
                SourceArchitecture = source.SourceArchitecture,
                AppliedFeatures = new List<string>(source.AppliedFeatures ?? Enumerable.Empty<string>()),
                IncompleteFeatures = new List<string>(source.IncompleteFeatures ?? Enumerable.Empty<string>()),
                CompatibilityFeatures = (source.CompatibilityFeatures ?? new List<CompatibilityFeatureRecord>())
                    .Where(feature => feature != null)
                    .Select(feature => new CompatibilityFeatureRecord
                    {
                        FeatureId = feature.FeatureId,
                        Before = feature.Before,
                        Desired = feature.Desired,
                        After = feature.After,
                        Changed = feature.Changed,
                        Status = feature.Status,
                        Error = feature.Error,
                        RecipeId = feature.RecipeId
                    })
                    .ToList(),
                Artifacts = (source.Artifacts ?? new List<ArtifactDigest>())
                    .Where(artifact => artifact != null)
                    .Select(artifact => new ArtifactDigest
                    {
                        RelativePath = artifact.RelativePath,
                        Sha256 = artifact.Sha256
                    })
                    .ToList()
            };
        }

        private static List<CompatibilityFeatureRecord> CaptureFeatureRecords(CompatibilityResult result)
        {
            return result.FeatureResults.Select(feature => new CompatibilityFeatureRecord
            {
                FeatureId = feature.FeatureId,
                Before = feature.Before,
                Desired = feature.Desired,
                After = feature.After,
                Changed = feature.Changed,
                Status = feature.Status,
                Error = feature.Error,
                RecipeId = feature.RecipeId
            }).ToList();
        }

        private static HashSet<string> GetManagedCompatibilityFeatureIds(
            CompatibilityOptions options)
        {
            HashSet<string> features = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            if (options.ManageModelCatalog) features.Add("ModelCatalog");
            if (options.ManageLocalization) features.Add("Localization");
            if (options.ManageSandboxCompatibility) features.Add("SandboxCompatibility");
            if (options.ManageReasoningDisplay)
            {
                features.Add(ReasoningDisplayCompatibility.FeatureId);
            }
            return features;
        }

        private static HashSet<string> GetManagedCompatibilityDisplayNames(
            CompatibilityOptions options)
        {
            HashSet<string> features = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            if (options.ManageModelCatalog) features.Add("模型目录");
            if (options.ManageLocalization) features.Add("界面语言");
            if (options.ManageSandboxCompatibility) features.Add("Windows 沙箱兼容");
            if (options.ManageReasoningDisplay) features.Add("模型推理显示");
            return features;
        }

        private static void AddAppliedCompatibilityFeatures(
            ICollection<string> features,
            CompatibilityResult result)
        {
            if (features == null || result == null) return;
            if (HasSuccessfulAfterState(result.ModelCatalog, "Patched")) features.Add("ModelCatalog");
            if (result.Localization != null &&
                result.Localization.Status != CompatibilityFeatureStatus.Failed &&
                result.Localization.Status != CompatibilityFeatureStatus.RolledBack &&
                result.Localization.After != null &&
                result.Localization.After.IndexOf("=Patched", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                features.Add("Localization");
            }
            if (HasSuccessfulAfterState(result.Sandbox, "Enabled")) features.Add("SandboxCompatibility");
            if (HasSuccessfulAfterState(result.ReasoningDisplay, "Patched"))
            {
                features.Add(ReasoningDisplayCompatibility.FeatureId);
            }
        }

        private static bool HasSuccessfulAfterState(CompatibilityFeatureResult feature, string expected)
        {
            return feature != null && feature.Succeeded &&
                string.Equals(feature.After, expected, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class ArtifactDigest
    {
        public string RelativePath { get; set; }
        public string Sha256 { get; set; }
    }

    internal sealed class CompatibilityFeatureRecord
    {
        public string FeatureId { get; set; }
        public string Before { get; set; }
        public string Desired { get; set; }
        public string After { get; set; }
        public bool Changed { get; set; }
        public CompatibilityFeatureStatus Status { get; set; }
        public string Error { get; set; }
        public string RecipeId { get; set; }
    }

    internal enum InstallationHealthStatus
    {
        Healthy,
        Unverified,
        Tampered,
        Invalid
    }

    internal sealed class InstallationHealthReport
    {
        internal InstallationHealthReport(InstallationHealthStatus status, IEnumerable<string> errors)
        {
            Status = status;
            Errors = new List<string>(errors ?? Enumerable.Empty<string>()).AsReadOnly();
        }

        public InstallationHealthStatus Status { get; private set; }
        public IReadOnlyList<string> Errors { get; private set; }
    }

    internal static class InstallationHealth
    {
        internal static InstallationHealthReport Evaluate(string installRoot)
        {
            PackageProfile profile;
            string payloadError;
            if (!InstallOwnership.TryValidateRunnableCodexPayload(installRoot, out profile, out payloadError))
            {
                return new InstallationHealthReport(
                    InstallationHealthStatus.Invalid,
                    new[] { payloadError ?? "便携版 payload 无效。" });
            }

            InstallationRecord record;
            try
            {
                record = InstallOwnership.ReadInstallationRecord(installRoot);
            }
            catch (Exception exception)
            {
                return new InstallationHealthReport(InstallationHealthStatus.Invalid, new[] { exception.Message });
            }

            if (record.Provenance == null)
            {
                return new InstallationHealthReport(
                    InstallationHealthStatus.Unverified,
                    new[] { "该安装来自无标记目录接管，没有官方包摘要和派生制品摘要。" });
            }

            List<string> errors = new List<string>();
            bool sourceUnverified = string.IsNullOrWhiteSpace(record.Provenance.SourcePackageSha256);
            if (!string.Equals(record.Identity.PackageVersion, profile.Version, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("安装身份版本与 AppxManifest.xml 不一致。");
            }
            if (record.Provenance.Artifacts == null || record.Provenance.Artifacts.Count == 0)
            {
                errors.Add("派生制品摘要清单为空。");
            }
            else
            {
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ArtifactDigest artifact in record.Provenance.Artifacts)
                {
                    if (artifact == null || string.IsNullOrWhiteSpace(artifact.RelativePath) ||
                        string.IsNullOrWhiteSpace(artifact.Sha256) || !seen.Add(artifact.RelativePath))
                    {
                        errors.Add("派生制品摘要记录格式无效或路径重复。");
                        continue;
                    }

                    string fullPath;
                    try { fullPath = ArtifactProvenance.ResolveRelativePath(installRoot, artifact.RelativePath); }
                    catch (Exception exception)
                    {
                        errors.Add(exception.Message);
                        continue;
                    }
                    if (!File.Exists(fullPath))
                    {
                        errors.Add("派生制品缺失：" + artifact.RelativePath);
                        continue;
                    }
                    string actual = ArtifactHash.ComputeSha256(fullPath);
                    if (!ArtifactHash.FixedTimeEquals(actual, artifact.Sha256))
                    {
                        errors.Add("派生制品摘要不匹配：" + artifact.RelativePath);
                    }
                }
            }

            if (errors.Count > 0)
            {
                return new InstallationHealthReport(InstallationHealthStatus.Tampered, errors);
            }
            if (sourceUnverified)
            {
                return new InstallationHealthReport(
                    InstallationHealthStatus.Unverified,
                    new[] { "派生制品摘要有效，但无标记目录接管的安装没有可追溯的官方 MSIX 摘要。" });
            }
            return new InstallationHealthReport(InstallationHealthStatus.Healthy, new string[0]);
        }
    }

    internal static class ArtifactHash
    {
        internal static string ComputeSha256(string path)
        {
            using (FileStream input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(input);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        internal static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null) return false;
            int difference = left.Length ^ right.Length;
            int length = Math.Min(left.Length, right.Length);
            for (int index = 0; index < length; index++)
            {
                difference |= char.ToUpperInvariant(left[index]) ^ char.ToUpperInvariant(right[index]);
            }
            return difference == 0;
        }
    }
}
