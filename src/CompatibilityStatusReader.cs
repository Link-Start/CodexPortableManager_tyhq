using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexPortableManager
{
    internal static class CompatibilityStatusReader
    {
        internal static CompatibilityOverview Read(string installRoot, bool verifyArtifacts)
        {
            PackageProfile profile;
            string payloadError;
            if (!InstallOwnership.TryValidateOwnedRunnableCodexPayload(
                installRoot,
                out profile,
                out payloadError))
            {
                return Create(
                    CompatibilityOverviewState.Unavailable,
                    string.IsNullOrWhiteSpace(payloadError)
                        ? "当前目录没有可读取的受管便携版。"
                        : payloadError);
            }

            InstallationHealthReport health = null;
            if (verifyArtifacts)
            {
                health = InstallationHealth.Evaluate(installRoot);
                if (health.Status == InstallationHealthStatus.Tampered ||
                    health.Status == InstallationHealthStatus.Invalid)
                {
                    return Create(
                        CompatibilityOverviewState.Invalid,
                        FormatHealthDetail(health));
                }
            }

            InstallationRecord record;
            try
            {
                record = InstallOwnership.ReadInstallationRecord(installRoot);
            }
            catch (Exception exception)
            {
                return Create(CompatibilityOverviewState.Invalid, exception.Message);
            }

            ArtifactProvenance provenance = record.Provenance;
            bool hasOfficialBaseline = provenance != null &&
                !string.IsNullOrWhiteSpace(provenance.SourcePackageSha256);
            return InspectCurrentState(
                installRoot,
                profile,
                hasOfficialBaseline,
                verifyArtifacts && health != null &&
                    health.Status == InstallationHealthStatus.Healthy,
                verifyArtifacts && health != null &&
                    health.Status != InstallationHealthStatus.Healthy);
        }

        internal static CompatibilityOptions ResolveOptions(CompatibilityOverview overview)
        {
            CompatibilitySwitchFacts facts = ResolveSwitchFacts(overview);
            if (!facts.AllKnown) return null;

            return new CompatibilityOptions(
                facts.SandboxCompatibilityEnabled.Value,
                facts.UnlockModelCatalogEnabled.Value,
                facts.SupplementChineseUiEnabled.Value,
                facts.EnglishTechnicalParametersEnabled.Value);
        }

        internal static CompatibilitySwitchFacts ResolveSwitchFacts(
            CompatibilityOverview overview)
        {
            bool sandboxAvailable = CanResolveFeature(overview, "SandboxCompatibility");
            bool modelAvailable = CanResolveFeature(overview, "ModelCatalog");
            bool localizationAvailable = CanResolveFeature(overview, "Localization");
            bool localizationNeedsRefresh = localizationAvailable &&
                string.Equals(
                    GetLocalizationComponent(overview, "Menus"),
                    "PatchedRefreshRequired",
                    StringComparison.OrdinalIgnoreCase);
            return new CompatibilitySwitchFacts(
                sandboxAvailable
                    ? ResolveSimpleState(overview, "SandboxCompatibility", "Enabled", "Disabled")
                    : null,
                modelAvailable
                    ? ResolveSimpleState(overview, "ModelCatalog", "Patched", "Official")
                    : null,
                localizationAvailable
                    ? ResolveLocalizationState(overview, "Menus")
                    : null,
                localizationAvailable
                    ? ResolveLocalizationState(overview, "Reasoning")
                    : null,
                localizationNeedsRefresh);
        }

        internal static bool CanResolveFeature(
            CompatibilityOverview overview,
            string featureId)
        {
            if (overview == null ||
                (overview.State != CompatibilityOverviewState.Recorded &&
                 overview.State != CompatibilityOverviewState.Inspected &&
                 overview.State != CompatibilityOverviewState.Verified))
            {
                return false;
            }

            CompatibilityObservedFeature observed = overview.Features.FirstOrDefault(feature =>
                string.Equals(feature.FeatureId, featureId, StringComparison.OrdinalIgnoreCase));
            if (observed == null) return false;
            if (!observed.RecipeCurrent) return false;
            return observed.Status != CompatibilityFeatureStatus.Failed &&
                observed.Status != CompatibilityFeatureStatus.RolledBack &&
                observed.Status != CompatibilityFeatureStatus.Unsupported;
        }

        internal static bool? ResolveSimpleState(
            CompatibilityOverview overview,
            string featureId,
            string enabledValue,
            string disabledValue)
        {
            if (overview == null) return null;
            CompatibilityObservedFeature observed = overview.Features.FirstOrDefault(feature =>
                string.Equals(feature.FeatureId, featureId, StringComparison.OrdinalIgnoreCase));
            if (observed != null)
            {
                if (string.Equals(observed.After, enabledValue, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(observed.After, disabledValue, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(observed.After, "UnmanagedOrOfficial", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(observed.After, "NativeSupported", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return null;
            }
            return null;
        }

        internal static bool? ResolveLocalizationState(
            CompatibilityOverview overview,
            string component)
        {
            if (overview == null) return null;
            CompatibilityObservedFeature observed = overview.Features.FirstOrDefault(feature =>
                string.Equals(feature.FeatureId, "Localization", StringComparison.OrdinalIgnoreCase));
            if (observed != null)
            {
                string value = GetComponent(observed.After, component);
                if (string.Equals(value, "Patched", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "PatchedRefreshRequired", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(value, "Official", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "NotManaged", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "UnmanagedOrOfficial", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "NativeSupported", StringComparison.OrdinalIgnoreCase)) return false;
                return null;
            }
            return null;
        }

        private static string GetLocalizationComponent(
            CompatibilityOverview overview,
            string component)
        {
            if (overview == null) return null;
            CompatibilityObservedFeature observed = overview.Features.FirstOrDefault(feature =>
                string.Equals(feature.FeatureId, "Localization", StringComparison.OrdinalIgnoreCase));
            return observed == null ? null : GetComponent(observed.After, component);
        }

        private static string GetComponent(string value, string component)
        {
            foreach (string pair in (value ?? string.Empty).Split(';'))
            {
                int separator = pair.IndexOf('=');
                if (separator > 0 && string.Equals(
                    pair.Substring(0, separator),
                    component,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Substring(separator + 1);
                }
            }
            return null;
        }

        private static CompatibilityOverview InspectCurrentState(
            string installRoot,
            PackageProfile profile,
            bool hasOfficialBaseline,
            bool artifactsVerified,
            bool baselineUnverified)
        {
            List<CompatibilityObservedFeature> features = new List<CompatibilityObservedFeature>();
            string executablePath = PackageProfileReader.GetExecutablePath(installRoot, profile);

            try
            {
                using (AsarSession session = AsarSession.Open(AsarSession.GetAsarPath(executablePath)))
                {
                    CompatibilityFeatureChange sandbox = SandboxCompatibility.InspectFeature(session);
                    features.Add(CreateObserved("SandboxCompatibility", sandbox));
                    CompatibilityFeatureChange model = ModelCatalogCompatibility.Inspect(session);
                    features.Add(CreateObserved("ModelCatalog", model));
                    CompatibilityFeatureChange localization =
                        CodexLocalizationCompatibility.Inspect(session);
                    features.Add(CreateObserved("Localization", localization));
                }
            }
            catch (Exception exception)
            {
                AddFailedAsarFeatures(features, exception.Message);
            }

            string[] appliedFeatures = features
                .Where(IsApplied)
                .Select(feature => feature.FeatureId)
                .ToArray();
            bool hasFailures = features.Any(feature =>
                feature.Status == CompatibilityFeatureStatus.Failed ||
                feature.Status == CompatibilityFeatureStatus.Unsupported);
            string detail = hasFailures
                ? "已尝试读取当前功能状态，但部分文件结构无法解析；请查看各项状态。"
                : artifactsVerified
                    ? "关键派生文件摘要一致，已按当前配方读取功能状态。"
                    : baselineUnverified || !hasOfficialBaseline
                    ? "已直接读取当前功能状态；该安装缺少可追溯的官方来源基线。"
                    : "兼容配方记录已变化，已按当前文件重新读取功能状态。";
            return new CompatibilityOverview(
                artifactsVerified && !hasFailures
                    ? CompatibilityOverviewState.Verified
                    : CompatibilityOverviewState.Inspected,
                detail,
                features,
                appliedFeatures,
                hasOfficialBaseline);
        }

        private static CompatibilityObservedFeature CreateObserved(
            string featureId,
            CompatibilityFeatureChange change)
        {
            if (change == null)
            {
                return CreateObserved(
                    featureId,
                    "Unknown",
                    CompatibilityFeatureStatus.Failed,
                    "功能分析没有返回结果。",
                    GetRecipeId(featureId));
            }
            return CreateObserved(
                featureId,
                change.After ?? change.Before ?? "Unknown",
                change.Status,
                change.Error,
                change.RecipeId ?? GetRecipeId(featureId));
        }

        private static CompatibilityObservedFeature CreateObserved(
            string featureId,
            string after,
            CompatibilityFeatureStatus status,
            string error,
            string recipeId)
        {
            return new CompatibilityObservedFeature(
                featureId,
                after,
                status,
                error,
                recipeId,
                true);
        }

        private static void AddFailedAsarFeatures(
            ICollection<CompatibilityObservedFeature> features,
            string error)
        {
            string[] missing = { "SandboxCompatibility", "ModelCatalog", "Localization" };
            foreach (string featureId in missing.Where(id => features.All(feature =>
                !string.Equals(feature.FeatureId, id, StringComparison.OrdinalIgnoreCase))))
            {
                features.Add(CreateObserved(
                    featureId,
                    "Unknown",
                    CompatibilityFeatureStatus.Failed,
                    error,
                    GetRecipeId(featureId)));
            }
        }

        internal static bool IsApplied(CompatibilityObservedFeature feature)
        {
            if (feature == null) return false;
            if (string.Equals(feature.FeatureId, "SandboxCompatibility", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(feature.After, "Enabled", StringComparison.OrdinalIgnoreCase);
            }
            if (string.Equals(feature.FeatureId, "Localization", StringComparison.OrdinalIgnoreCase))
            {
                return (feature.After ?? string.Empty).IndexOf(
                    "Patched",
                    StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return string.Equals(feature.After, "Patched", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRecipeId(string featureId)
        {
            if (string.Equals(featureId, "SandboxCompatibility", StringComparison.OrdinalIgnoreCase))
            {
                return CompatibilityCoordinator.SandboxRecipeId;
            }
            return string.Equals(featureId, "ModelCatalog", StringComparison.OrdinalIgnoreCase)
                ? ModelCatalogCompatibility.RecipeId
                : CodexLocalizationCompatibility.RecipeId;
        }

        private static CompatibilityOverview Create(
            CompatibilityOverviewState state,
            string detail)
        {
            return new CompatibilityOverview(
                state,
                detail,
                new CompatibilityObservedFeature[0],
                new string[0],
                false);
        }

        private static string FormatHealthDetail(InstallationHealthReport health)
        {
            if (health == null) return "无法读取当前安装健康状态。";
            if (health.Errors == null || health.Errors.Count == 0)
            {
                return "当前安装健康状态为 " + health.Status + "。";
            }
            return string.Join("；", health.Errors.ToArray());
        }
    }
}
