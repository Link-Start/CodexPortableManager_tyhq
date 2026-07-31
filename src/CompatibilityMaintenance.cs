using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexPortableManager
{
    internal sealed class CompatibilityMaintenancePreflight
    {
        private readonly string installRoot;
        private readonly string installRootIdentity;
        private readonly string installId;
        private readonly bool requireReadableMarker;

        internal CompatibilityMaintenancePreflight(
            string root,
            string rootIdentity,
            string expectedInstallId,
            bool markerMustBeReadable)
        {
            installRoot = Path.GetFullPath(root);
            installRootIdentity = rootIdentity;
            installId = expectedInstallId;
            requireReadableMarker = markerMustBeReadable;
        }

        internal void EnsureTargetUnchanged()
        {
            try
            {
                InstallOwnership.EnsureManagedDirectoryIdentity(
                    installRoot,
                    installRootIdentity);
                if (!requireReadableMarker) return;

                InstallationRecord current = InstallOwnership.ReadInstallationRecord(installRoot);
                string currentInstallId = current == null || current.Identity == null
                    ? null
                    : current.Identity.InstallId;
                if (!string.Equals(
                    currentInstallId,
                    installId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("安装 ID 已变化。");
                }
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(
                    "兼容维护目标在预检后已被替换，已拒绝继续修改文件：" + installRoot,
                    exception);
            }
        }
    }

    internal sealed class CompatibilityMaintenance
    {
        private readonly Func<string, CompatibilityOptions, CompatibilityResult> applyCompatibility;
        private readonly Func<string, CompatibilityOptions, CompatibilityResult> applyStagingCompatibility;
        private readonly Action<string, string, string, ArtifactProvenance> writeMarker;
        private readonly Action<string> log;

        internal CompatibilityMaintenance(CompatibilityCoordinator coordinator, Action<string> logAction)
            : this(
                coordinator == null
                    ? (Func<string, CompatibilityOptions, CompatibilityResult>)null
                    : coordinator.Apply,
                coordinator == null
                    ? (Func<string, CompatibilityOptions, CompatibilityResult>)null
                    : coordinator.ApplyOfficialStaging,
                InstallOwnership.WriteMarker,
                logAction)
        {
        }

        internal CompatibilityMaintenance(
            Func<string, CompatibilityOptions, CompatibilityResult> applyAction,
            Action<string, string, string, ArtifactProvenance> markerWriter,
            Action<string> logAction)
            : this(applyAction, applyAction, markerWriter, logAction)
        {
        }

        private CompatibilityMaintenance(
            Func<string, CompatibilityOptions, CompatibilityResult> applyAction,
            Func<string, CompatibilityOptions, CompatibilityResult> applyStagingAction,
            Action<string, string, string, ArtifactProvenance> markerWriter,
            Action<string> logAction)
        {
            applyCompatibility = applyAction ?? throw new ArgumentNullException(nameof(applyAction));
            applyStagingCompatibility = applyStagingAction ??
                throw new ArgumentNullException(nameof(applyStagingAction));
            writeMarker = markerWriter ?? throw new ArgumentNullException(nameof(markerWriter));
            log = logAction ?? delegate { };
        }

        internal CompatibilityResult Apply(
            string installRoot,
            CompatibilityOptions options,
            CompatibilityBaselineApproval baselineApproval)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            CompatibilityTransaction.RecoverPending(installRoot, log);

            InstallationHealthReport health = InstallationHealth.Evaluate(installRoot);
            ValidateHealthGate(installRoot, health, baselineApproval);

            PackageProfile profile = PackageProfileReader.Read(installRoot);
            string executablePath = PackageProfileReader.GetExecutablePath(installRoot, profile);
            InstallationRecord record = InstallOwnership.ReadInstallationRecord(installRoot);
            ArtifactProvenance baseline = record.Provenance;
            if (baseline == null)
            {
                using (AsarSession session = AsarSession.Open(AsarSession.GetAsarPath(executablePath)))
                {
                    session.ValidateAllEntries();
                }
                baseline = ArtifactProvenance.Capture(installRoot, profile, null, null);
            }

            return ApplyCore(
                installRoot,
                profile,
                record.Identity.InstallId,
                options,
                baseline,
                false,
                GetProtectedArtifacts(profile, options));
        }

        internal CompatibilityMaintenancePreflight PreflightApply(
            string installRoot,
            CompatibilityBaselineApproval baselineApproval)
        {
            if (CompatibilityTransaction.Exists(installRoot))
            {
                return CompatibilityTransaction.PreflightPendingRecovery(installRoot);
            }

            string rootIdentity = InstallOwnership.GetManagedDirectoryIdentity(installRoot);
            InstallationHealthReport health = InstallationHealth.Evaluate(installRoot);
            ValidateHealthGate(installRoot, health, baselineApproval);
            InstallationRecord record = InstallOwnership.ReadInstallationRecord(installRoot);
            if (record == null || record.Identity == null)
            {
                throw new InvalidDataException("兼容维护目标缺少安装身份。");
            }

            CompatibilityMaintenancePreflight preflight = new CompatibilityMaintenancePreflight(
                installRoot,
                rootIdentity,
                record.Identity.InstallId,
                true);
            preflight.EnsureTargetUnchanged();
            return preflight;
        }

        private static void ValidateHealthGate(
            string installRoot,
            InstallationHealthReport health,
            CompatibilityBaselineApproval baselineApproval)
        {
            if (health.Status == InstallationHealthStatus.Invalid ||
                health.Status == InstallationHealthStatus.Tampered)
            {
                throw new InvalidDataException(
                    "当前便携安装健康状态为 " + health.Status + "，已拒绝重新登记摘要或修改文件。" +
                    FormatHealthErrors(health));
            }
            if (health.Status == InstallationHealthStatus.Unverified &&
                (baselineApproval == null || !baselineApproval.Covers(installRoot)))
            {
                throw new InvalidOperationException(
                    "当前便携安装尚无可验证的完整来源基线。必须由用户明确批准按当前文件重新建立本地基线后，才能执行兼容维护。" +
                    FormatHealthErrors(health));
            }
        }

        internal CompatibilityResult ApplyTrustedStaging(
            string stagingRoot,
            PackageProfile profile,
            string installId,
            CompatibilityOptions options,
            ArtifactProvenance officialBaseline)
        {
            if (string.IsNullOrWhiteSpace(stagingRoot)) throw new ArgumentException("staging 目录不能为空。", nameof(stagingRoot));
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (officialBaseline == null) throw new ArgumentNullException(nameof(officialBaseline));
            string root = Path.GetFullPath(stagingRoot);

            if (!options.AnyEnabled)
            {
                CompatibilityResult official = CreateOfficialResult();
                WriteOutcomeMarker(root, profile, installId, options, official, officialBaseline);
                official.TransactionCommitted = true;
                return official;
            }

            return ApplyCore(
                root,
                profile,
                installId,
                options,
                officialBaseline,
                true,
                GetStagingProtectedArtifacts(profile, options));
        }

        private CompatibilityResult ApplyCore(
            string installRoot,
            PackageProfile profile,
            string installId,
            CompatibilityOptions options,
            ArtifactProvenance baseline,
            bool preserveFailureOutcome,
            IEnumerable<string> protectedArtifacts)
        {
            string executablePath = PackageProfileReader.GetExecutablePath(installRoot, profile);
            List<string> protectedPaths = (protectedArtifacts ?? throw new ArgumentNullException(nameof(protectedArtifacts)))
                .Select(ArtifactProvenance.NormalizeRelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            CompatibilityTransaction transaction = null;
            bool finished = false;
            try
            {
                transaction = CompatibilityTransaction.Begin(
                    installRoot,
                    installId,
                    options,
                    protectedPaths);
                transaction.VerifyOriginalArtifacts(
                    baseline,
                    protectedPaths.Where(path => !string.Equals(
                        path,
                        InstallOwnership.MarkerFileName,
                        StringComparison.OrdinalIgnoreCase)));
                transaction.BeginMutation();
                CompatibilityResult result = (preserveFailureOutcome
                    ? applyStagingCompatibility
                    : applyCompatibility)(executablePath, options);
                if (result == null)
                {
                    throw new InvalidOperationException("兼容协调器没有返回应用结果。");
                }
                transaction.CaptureChanges();

                if (!CanCommitResult(result))
                {
                    transaction.Rollback();
                    finished = true;
                    result.MarkChangedFeaturesRolledBack();
                    result.TransactionCommitted = false;
                    if (preserveFailureOutcome)
                    {
                        WriteOutcomeMarker(
                            installRoot,
                            profile,
                            installId,
                            options,
                            result,
                            baseline);
                    }
                    SafeLog("兼容设置未全部达到目标状态，本次文件变更已整体回滚；期望设置已保留以便重试。");
                    return result;
                }

                if (!result.AllSucceeded)
                {
                    SafeLog("部分兼容设置已达到目标；失败且未改写文件的功能保持原状，其余成功变更继续提交。");
                }

                string markerRelativePath = ArtifactProvenance.NormalizeRelativePath(
                    Path.GetFileName(InstallOwnership.GetMarkerPath(installRoot)));
                IReadOnlyList<CompatibilityArtifactState> changedArtifacts = transaction.ChangedArtifacts
                    .Where(artifact => !string.Equals(
                        artifact.RelativePath,
                        markerRelativePath,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList()
                    .AsReadOnly();
                ArtifactProvenance provenance = ArtifactProvenance.UpdateCompatibilityArtifacts(
                    installRoot,
                    baseline,
                    options,
                    result,
                    changedArtifacts);
                writeMarker(
                    installRoot,
                    installId,
                    profile.Version,
                    provenance);
                transaction.Commit(markerRelativePath);
                finished = true;
                result.TransactionCommitted = true;
                return result;
            }
            catch (Exception exception)
            {
                if (finished) throw;
                if (transaction != null)
                {
                    try
                    {
                        transaction.Rollback();
                        finished = true;
                    }
                    catch (Exception rollbackException)
                    {
                        throw new AggregateException(
                            "兼容维护失败，并且可信备份恢复未能完整完成。下次维护会依据事务日志继续恢复。",
                            exception,
                            rollbackException);
                    }
                }
                if (!preserveFailureOutcome) throw;

                CompatibilityResult failure = CreateFailureResult(options, exception);
                WriteOutcomeMarker(
                    installRoot,
                    profile,
                    installId,
                    options,
                    failure,
                    baseline);
                SafeLog("兼容设置在 staging 中执行失败，已恢复官方文件并继续更新：" + exception.Message);
                return failure;
            }
        }

        private static bool CanCommitResult(CompatibilityResult result)
        {
            if (result == null) return false;
            return CanCommitFeature("ModelCatalog", result.ModelCatalogSucceeded, result.ModelCatalog) &&
                CanCommitFeature("SandboxCompatibility", result.SandboxSucceeded, result.Sandbox) &&
                CanCommitFeature("Localization", result.LocalizationSucceeded, result.Localization) &&
                CanCommitFeature(
                    ReasoningDisplayCompatibility.FeatureId,
                    result.ReasoningDisplaySucceeded,
                    result.ReasoningDisplay);
        }

        private static bool CanCommitFeature(
            string expectedFeatureId,
            bool declaredSucceeded,
            CompatibilityFeatureResult feature)
        {
            if (feature == null || !string.Equals(
                feature.FeatureId,
                expectedFeatureId,
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (declaredSucceeded) return feature.Succeeded;
            if (feature.Succeeded) return false;

            if (string.Equals(expectedFeatureId, "Localization", StringComparison.OrdinalIgnoreCase) &&
                feature.Status == CompatibilityFeatureStatus.Unsupported)
            {
                return true;
            }

            return !feature.Changed &&
                !string.IsNullOrWhiteSpace(feature.Before) &&
                !string.IsNullOrWhiteSpace(feature.After) &&
                string.Equals(feature.Before, feature.After, StringComparison.OrdinalIgnoreCase);
        }

        internal static IEnumerable<string> GetProtectedArtifacts(
            PackageProfile profile,
            CompatibilityOptions options)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (options == null) throw new ArgumentNullException(nameof(options));
            string executableDirectory = ArtifactProvenance.NormalizeRelativePath(
                Path.GetDirectoryName(profile.ExecutableRelativePath));
            string resources = CombineRelative(executableDirectory, "resources");
            List<string> artifacts = new List<string>
            {
                InstallOwnership.MarkerFileName
            };
            if (options.ManageModelCatalog ||
                options.ManageSandboxCompatibility ||
                options.ManageLocalization ||
                options.ManageReasoningDisplay)
            {
                artifacts.Add(CombineRelative(resources, "app.asar"));
            }
            return artifacts;
        }

        internal static IEnumerable<string> GetStagingProtectedArtifacts(
            PackageProfile profile,
            CompatibilityOptions options)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (options == null) throw new ArgumentNullException(nameof(options));
            string executableDirectory = ArtifactProvenance.NormalizeRelativePath(
                Path.GetDirectoryName(profile.ExecutableRelativePath));
            string resources = CombineRelative(executableDirectory, "resources");
            List<string> artifacts = new List<string> { InstallOwnership.MarkerFileName };
            if (options.UnlockModelCatalogEnabled ||
                options.SandboxCompatibilityEnabled ||
                options.SupplementChineseUiEnabled ||
                options.EnglishTechnicalParametersEnabled ||
                options.ReasoningDisplayEnabled)
            {
                artifacts.Add(CombineRelative(resources, "app.asar"));
            }
            return artifacts;
        }

        private void WriteOutcomeMarker(
            string root,
            PackageProfile profile,
            string installId,
            CompatibilityOptions options,
            CompatibilityResult result,
            ArtifactProvenance baseline)
        {
            ArtifactProvenance provenance = ArtifactProvenance.UpdateCompatibilityArtifacts(
                root,
                baseline,
                options,
                result,
                new CompatibilityArtifactState[0]);
            writeMarker(root, installId, profile.Version, provenance);
        }

        private static CompatibilityResult CreateOfficialResult()
        {
            return new CompatibilityResult
            {
                ModelCatalogSucceeded = true,
                SandboxSucceeded = true,
                LocalizationSucceeded = true,
                ReasoningDisplaySucceeded = true,
                ModelCatalog = CreateFeature(
                    "ModelCatalog",
                    "模型目录",
                    "Official",
                    "Official",
                    CompatibilityFeatureStatus.AlreadySatisfied,
                    ModelCatalogCompatibility.RecipeId,
                    null),
                Sandbox = CreateFeature(
                    "SandboxCompatibility",
                    "Windows 沙箱兼容",
                    "Disabled",
                    "Disabled",
                    CompatibilityFeatureStatus.AlreadySatisfied,
                    CompatibilityCoordinator.SandboxRecipeId,
                    null),
                Localization = CreateFeature(
                    "Localization",
                    "界面语言",
                    "Menus=Official;Reasoning=Official",
                    "Menus=Official;Reasoning=Official",
                    CompatibilityFeatureStatus.AlreadySatisfied,
                    CodexLocalizationCompatibility.RecipeId,
                    null),
                ReasoningDisplay = CreateFeature(
                    ReasoningDisplayCompatibility.FeatureId,
                    "模型推理显示",
                    "Official",
                    "Official",
                    CompatibilityFeatureStatus.AlreadySatisfied,
                    ReasoningDisplayCompatibility.RecipeId,
                    null)
            };
        }

        private static CompatibilityResult CreateFailureResult(
            CompatibilityOptions options,
            Exception exception)
        {
            string error = "staging 兼容变换未完成：" + exception.Message;
            return new CompatibilityResult
            {
                ModelCatalogSucceeded = false,
                SandboxSucceeded = false,
                LocalizationSucceeded = false,
                ReasoningDisplaySucceeded = false,
                ModelCatalog = CreateFeature(
                    "ModelCatalog",
                    "模型目录",
                    options.UnlockModelCatalogEnabled ? "Patched" : "Official",
                    "Official",
                    CompatibilityFeatureStatus.Failed,
                    ModelCatalogCompatibility.RecipeId,
                    error),
                Sandbox = CreateFeature(
                    "SandboxCompatibility",
                    "Windows 沙箱兼容",
                    options.SandboxCompatibilityEnabled ? "Enabled" : "Disabled",
                    "Disabled",
                    CompatibilityFeatureStatus.Failed,
                    CompatibilityCoordinator.SandboxRecipeId,
                    error),
                Localization = CreateFeature(
                    "Localization",
                    "界面语言",
                    "Menus=" + (options.SupplementChineseUiEnabled ? "Patched" : "Official") +
                        ";Reasoning=" + (options.EnglishTechnicalParametersEnabled ? "Patched" : "Official"),
                    "Menus=Official;Reasoning=Official",
                    CompatibilityFeatureStatus.Failed,
                    CodexLocalizationCompatibility.RecipeId,
                    error),
                ReasoningDisplay = CreateFeature(
                    ReasoningDisplayCompatibility.FeatureId,
                    "模型推理显示",
                    options.ReasoningDisplayEnabled ? "Patched" : "Official",
                    "Official",
                    CompatibilityFeatureStatus.Failed,
                    ReasoningDisplayCompatibility.RecipeId,
                    error)
            };
        }

        private static CompatibilityFeatureResult CreateFeature(
            string featureId,
            string displayName,
            string desired,
            string after,
            CompatibilityFeatureStatus status,
            string recipeId,
            string error)
        {
            return new CompatibilityFeatureResult
            {
                FeatureId = featureId,
                DisplayName = displayName,
                Before = after,
                Desired = desired,
                After = after,
                Changed = false,
                Status = status,
                RecipeId = recipeId,
                Error = error
            };
        }

        private static string CombineRelative(string parent, string child)
        {
            if (string.IsNullOrWhiteSpace(parent)) return ArtifactProvenance.NormalizeRelativePath(child);
            return ArtifactProvenance.NormalizeRelativePath(parent).TrimEnd('/') + "/" +
                ArtifactProvenance.NormalizeRelativePath(child).TrimStart('/');
        }

        private static string FormatHealthErrors(InstallationHealthReport health)
        {
            if (health == null || health.Errors == null || health.Errors.Count == 0) return string.Empty;
            return Environment.NewLine + string.Join(Environment.NewLine, health.Errors.Select(error => "- " + error).ToArray());
        }

        private void SafeLog(string message)
        {
            try { log(message); }
            catch { }
        }
    }
}
