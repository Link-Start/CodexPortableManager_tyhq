using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexPortableManager
{
    internal sealed class PackageMetadata
    {
        public string packageName { get; set; }
        public string architecture { get; set; }
        public string version { get; set; }
        public string fullName { get; set; }
        public string digest { get; set; }
        public string url { get; set; }
        public long sizeInBytes { get; set; }
        internal bool localCacheOnly { get; set; }
    }

    internal sealed class OperationProgress
    {
        public OperationProgress(
            string message,
            int? percent = null,
            string detail = null,
            bool canPause = false,
            bool canCancel = true)
            : this(message, percent, detail, canPause, percent, false, canCancel)
        {
        }

        public OperationProgress(
            string message,
            int? percent,
            string detail,
            bool canPause,
            int? displayPercent,
            bool isNetworkWaiting = false,
            bool canCancel = true)
        {
            Message = message;
            Percent = percent;
            Detail = detail;
            CanPause = canPause;
            DisplayPercent = displayPercent;
            IsNetworkWaiting = isNetworkWaiting;
            CanCancel = canCancel;
        }

        public string Message { get; private set; }
        public int? Percent { get; private set; }
        public string Detail { get; private set; }
        public bool CanPause { get; private set; }
        public int? DisplayPercent { get; private set; }
        public bool IsNetworkWaiting { get; private set; }
        public bool CanCancel { get; private set; }
    }

    internal sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; }
        public string StandardError { get; set; }
    }

    internal sealed class PortableStatus
    {
        public StorePackageState StoreState { get; set; }
        public bool StoreInstalled { get { return StoreState == StorePackageState.Installed; } }
        public string StoreDetectionError { get; set; }
        public Version PortableVersion { get; set; }
        public string PortableApplicationVersion { get; set; }
        public PackageMetadata LatestPackage { get; set; }
        public bool PreviousVersionAvailable { get; set; }
        public bool CachedRollbackVersionAvailable { get; set; }
        public bool RollbackVersionAvailable
        {
            get { return PreviousVersionAvailable || CachedRollbackVersionAvailable; }
        }
    }

    internal sealed class PortableLocalStatus
    {
        public PortableLocalStatus(
            Version portableVersion,
            string portableApplicationVersion,
            bool previousVersionAvailable,
            string error,
            bool hasInstallRoot,
            bool oldBackupCleanupPending = false,
            bool uninstallDirectoryCleanupPending = false,
            bool shellIntegrationCleanupPending = false,
            bool cachedRollbackVersionAvailable = false)
        {
            PortableVersion = portableVersion;
            PortableApplicationVersion = portableApplicationVersion;
            PreviousVersionAvailable = previousVersionAvailable;
            Error = error;
            HasInstallRoot = hasInstallRoot;
            OldBackupCleanupPending = oldBackupCleanupPending;
            UninstallDirectoryCleanupPending = uninstallDirectoryCleanupPending;
            ShellIntegrationCleanupPending = shellIntegrationCleanupPending;
            CachedRollbackVersionAvailable = cachedRollbackVersionAvailable;
        }

        public Version PortableVersion { get; private set; }
        public string PortableApplicationVersion { get; private set; }
        public bool PreviousVersionAvailable { get; private set; }
        public bool CachedRollbackVersionAvailable { get; private set; }
        public bool RollbackVersionAvailable
        {
            get { return PreviousVersionAvailable || CachedRollbackVersionAvailable; }
        }
        public string Error { get; private set; }
        public bool HasInstallRoot { get; private set; }
        public bool OldBackupCleanupPending { get; private set; }
        public bool UninstallDirectoryCleanupPending { get; private set; }
        public bool ShellIntegrationCleanupPending { get; private set; }
    }

    internal enum StorePackageState
    {
        Unknown,
        NotInstalled,
        Installed
    }

    internal enum CompatibilityFeatureStatus
    {
        Applied,
        AlreadySatisfied,
        NotRequired,
        Unsupported,
        Failed,
        RolledBack
    }

    internal sealed class CompatibilityFeatureResult
    {
        public string FeatureId { get; internal set; }
        public string DisplayName { get; internal set; }
        public string Before { get; internal set; }
        public string Desired { get; internal set; }
        public string After { get; internal set; }
        public bool Changed { get; internal set; }
        public CompatibilityFeatureStatus Status { get; internal set; }
        public string Error { get; internal set; }
        public string RecipeId { get; internal set; }

        public bool Succeeded
        {
            get
            {
                return Status == CompatibilityFeatureStatus.Applied ||
                    Status == CompatibilityFeatureStatus.AlreadySatisfied ||
                    Status == CompatibilityFeatureStatus.NotRequired;
            }
        }
    }

    internal sealed class CompatibilityResult
    {
        public bool ModelCatalogSucceeded { get; set; }
        public bool SandboxSucceeded { get; set; }
        public bool LocalizationSucceeded { get; set; }
        public bool TransactionCommitted { get; internal set; }
        public CompatibilityFeatureResult ModelCatalog { get; internal set; }
        public CompatibilityFeatureResult Sandbox { get; internal set; }
        public CompatibilityFeatureResult Localization { get; internal set; }

        public IReadOnlyList<CompatibilityFeatureResult> FeatureResults
        {
            get
            {
                return new[] { ModelCatalog, Sandbox, Localization }
                    .Where(feature => feature != null)
                    .ToList()
                    .AsReadOnly();
            }
        }

        public bool AllSucceeded
        {
            get { return ModelCatalogSucceeded && SandboxSucceeded && LocalizationSucceeded; }
        }

        public bool HasPartialSuccess
        {
            get { return TransactionCommitted && !AllSucceeded; }
        }

        public IList<string> FailedFeatures
        {
            get
            {
                List<string> failed = new List<string>();
                if (!ModelCatalogSucceeded) failed.Add("模型目录");
                if (!SandboxSucceeded) failed.Add("Windows 沙箱兼容");
                if (!LocalizationSucceeded) failed.Add("界面语言");
                return failed.AsReadOnly();
            }
        }

        internal void MarkChangedFeaturesRolledBack()
        {
            foreach (CompatibilityFeatureResult feature in FeatureResults.Where(value => value.Changed))
            {
                feature.After = feature.Before;
                feature.Changed = false;
                feature.Status = CompatibilityFeatureStatus.RolledBack;
                if (string.IsNullOrWhiteSpace(feature.Error))
                {
                    feature.Error = "同一兼容维护事务中至少一项未达到目标状态，已恢复事务前文件。";
                }
            }
        }
    }

    internal enum CompatibilityOverviewState
    {
        Unavailable,
        Unknown,
        Recorded,
        Inspected,
        Verified,
        Invalid
    }

    internal sealed class CompatibilityObservedFeature
    {
        internal CompatibilityObservedFeature(
            string featureId,
            string after,
            CompatibilityFeatureStatus status,
            string error,
            string recipeId,
            bool recipeCurrent)
        {
            FeatureId = featureId;
            After = after;
            Status = status;
            Error = error;
            RecipeId = recipeId;
            RecipeCurrent = recipeCurrent;
        }

        internal string FeatureId { get; private set; }
        internal string After { get; private set; }
        internal CompatibilityFeatureStatus Status { get; private set; }
        internal string Error { get; private set; }
        internal string RecipeId { get; private set; }
        internal bool RecipeCurrent { get; private set; }
    }

    internal sealed class CompatibilityOverview
    {
        internal CompatibilityOverview(
            CompatibilityOverviewState state,
            string detail,
            IEnumerable<CompatibilityObservedFeature> features,
            IEnumerable<string> appliedFeatures,
            bool hasOfficialBaseline)
        {
            State = state;
            Detail = detail;
            Features = new List<CompatibilityObservedFeature>(
                features ?? Enumerable.Empty<CompatibilityObservedFeature>()).AsReadOnly();
            AppliedFeatures = new List<string>(
                appliedFeatures ?? Enumerable.Empty<string>()).AsReadOnly();
            HasOfficialBaseline = hasOfficialBaseline;
        }

        internal CompatibilityOverviewState State { get; private set; }
        internal string Detail { get; private set; }
        internal IReadOnlyList<CompatibilityObservedFeature> Features { get; private set; }
        internal IReadOnlyList<string> AppliedFeatures { get; private set; }
        internal bool HasOfficialBaseline { get; private set; }
    }

    internal sealed class DeploymentResult
    {
        public DeploymentResult(
            bool integrationRequested,
            IReadOnlyList<string> integrationWarnings,
            bool oldBackupCleanupPending = false,
            CompatibilityResult compatibility = null)
        {
            if (integrationWarnings == null) throw new ArgumentNullException(nameof(integrationWarnings));

            IntegrationRequested = integrationRequested;
            IntegrationWarnings = new List<string>(integrationWarnings).AsReadOnly();
            OldBackupCleanupPending = oldBackupCleanupPending;
            Compatibility = compatibility;
        }

        public bool IntegrationRequested { get; private set; }
        public IReadOnlyList<string> IntegrationWarnings { get; private set; }
        public bool OldBackupCleanupPending { get; private set; }
        public CompatibilityResult Compatibility { get; private set; }

        public bool IntegrationSucceeded
        {
            get { return !IntegrationRequested || IntegrationWarnings.Count == 0; }
        }

        public bool HasWarnings
        {
            get { return !IntegrationSucceeded || OldBackupCleanupPending || !CompatibilitySucceeded; }
        }

        public bool CompatibilitySucceeded
        {
            get { return Compatibility == null || Compatibility.TransactionCommitted && Compatibility.AllSucceeded; }
        }
    }

    internal sealed class DeploymentRecoveryResult
    {
        internal DeploymentRecoveryResult(
            bool oldBackupCleanupPending,
            bool uninstallDirectoryCleanupPending)
        {
            OldBackupCleanupPending = oldBackupCleanupPending;
            UninstallDirectoryCleanupPending = uninstallDirectoryCleanupPending;
        }

        internal bool OldBackupCleanupPending { get; private set; }
        internal bool UninstallDirectoryCleanupPending { get; private set; }

        internal bool HasPendingCleanup
        {
            get { return OldBackupCleanupPending || UninstallDirectoryCleanupPending; }
        }
    }

    internal sealed class PackageProfile
    {
        public string PackageName { get; set; }
        public string Version { get; set; }
        public string DisplayName { get; set; }
        public string ExecutableRelativePath { get; set; }
        public string AppUserModelId { get; set; }
        public List<string> Protocols { get; set; }
        public List<FileAssociationProfile> FileAssociations { get; set; }
    }

    internal sealed class FileAssociationProfile
    {
        public string Name { get; set; }
        public List<string> Extensions { get; set; }
    }

    internal sealed class IntegrationState
    {
        public string InstallId { get; set; }
        public string InstallRoot { get; set; }
        public string PhysicalInstallRoot { get; set; }
        public string RootIdentity { get; set; }
        public string ExecutablePath { get; set; }
        public string AppUserModelId { get; set; }
        public List<string> Protocols { get; set; }
        public List<string> ProgIds { get; set; }
        public List<string> Extensions { get; set; }
        public List<string> ShortcutPaths { get; set; }
        public bool CleanupPending { get; set; }
    }

    internal sealed class ShellIntegrationCleanupResult
    {
        public ShellIntegrationCleanupResult(
            bool complete,
            IReadOnlyList<string> warnings)
        {
            Complete = complete;
            Warnings = warnings ?? new string[0];
        }

        public bool Complete { get; private set; }
        public IReadOnlyList<string> Warnings { get; private set; }
    }

    internal sealed class UninstallResult
    {
        public UninstallResult(
            bool directoryCleanupPending,
            bool integrationCleanupPending,
            IReadOnlyList<string> integrationWarnings)
        {
            DirectoryCleanupPending = directoryCleanupPending;
            IntegrationCleanupPending = integrationCleanupPending;
            IntegrationWarnings = integrationWarnings ?? new string[0];
        }

        public bool DirectoryCleanupPending { get; private set; }
        public bool IntegrationCleanupPending { get; private set; }
        public IReadOnlyList<string> IntegrationWarnings { get; private set; }

        public bool CleanupPending
        {
            get { return DirectoryCleanupPending || IntegrationCleanupPending; }
        }
    }

    internal sealed class CompatibilityOptions
    {
        public CompatibilityOptions(
            bool sandboxCompatibilityEnabled,
            bool unlockModelCatalogEnabled,
            bool supplementChineseUiEnabled,
            bool englishTechnicalParametersEnabled)
            : this(
                sandboxCompatibilityEnabled,
                unlockModelCatalogEnabled,
                supplementChineseUiEnabled,
                englishTechnicalParametersEnabled,
                true,
                true,
                true)
        {
        }

        internal CompatibilityOptions(
            bool sandboxCompatibilityEnabled,
            bool unlockModelCatalogEnabled,
            bool supplementChineseUiEnabled,
            bool englishTechnicalParametersEnabled,
            bool manageSandboxCompatibility,
            bool manageModelCatalog,
            bool manageLocalization)
        {
            SandboxCompatibilityEnabled = sandboxCompatibilityEnabled;
            UnlockModelCatalogEnabled = unlockModelCatalogEnabled;
            SupplementChineseUiEnabled = supplementChineseUiEnabled;
            EnglishTechnicalParametersEnabled = englishTechnicalParametersEnabled;
            ManageSandboxCompatibility = manageSandboxCompatibility;
            ManageModelCatalog = manageModelCatalog;
            ManageLocalization = manageLocalization;
        }

        public bool SandboxCompatibilityEnabled { get; private set; }
        public bool UnlockModelCatalogEnabled { get; private set; }
        public bool SupplementChineseUiEnabled { get; private set; }
        public bool EnglishTechnicalParametersEnabled { get; private set; }
        internal bool ManageSandboxCompatibility { get; private set; }
        internal bool ManageModelCatalog { get; private set; }
        internal bool ManageLocalization { get; private set; }

        internal bool AnyManaged
        {
            get
            {
                return ManageSandboxCompatibility ||
                    ManageModelCatalog ||
                    ManageLocalization;
            }
        }

        internal bool AnyEnabled
        {
            get
            {
                return SandboxCompatibilityEnabled ||
                    UnlockModelCatalogEnabled ||
                    SupplementChineseUiEnabled ||
                    EnglishTechnicalParametersEnabled;
            }
        }
    }

    internal sealed class CompatibilitySwitchFacts
    {
        internal CompatibilitySwitchFacts(
            bool? sandboxCompatibilityEnabled,
            bool? unlockModelCatalogEnabled,
            bool? supplementChineseUiEnabled,
            bool? englishTechnicalParametersEnabled,
            bool localizationNeedsRefresh = false)
        {
            SandboxCompatibilityEnabled = sandboxCompatibilityEnabled;
            UnlockModelCatalogEnabled = unlockModelCatalogEnabled;
            SupplementChineseUiEnabled = supplementChineseUiEnabled;
            EnglishTechnicalParametersEnabled = englishTechnicalParametersEnabled;
            LocalizationNeedsRefresh = localizationNeedsRefresh;
        }

        internal bool? SandboxCompatibilityEnabled { get; private set; }
        internal bool? UnlockModelCatalogEnabled { get; private set; }
        internal bool? SupplementChineseUiEnabled { get; private set; }
        internal bool? EnglishTechnicalParametersEnabled { get; private set; }
        internal bool LocalizationNeedsRefresh { get; private set; }

        internal bool AllKnown
        {
            get
            {
                return SandboxCompatibilityEnabled.HasValue &&
                    UnlockModelCatalogEnabled.HasValue &&
                    SupplementChineseUiEnabled.HasValue &&
                    EnglishTechnicalParametersEnabled.HasValue;
            }
        }
    }

    internal sealed class ManagerSettings
    {
        public string InstallRoot { get; set; }
    }

    internal sealed class DirectProgress<T> : IProgress<T>
    {
        private readonly Action<T> callback;

        public DirectProgress(Action<T> action)
        {
            callback = action;
        }

        public void Report(T value)
        {
            callback(value);
        }
    }
}
