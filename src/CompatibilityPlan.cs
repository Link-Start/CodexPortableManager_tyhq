using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexPortableManager
{
    internal enum CompatibilityPatchState
    {
        Official,
        Patched,
        Mixed,
        Unsupported
    }

    internal sealed class CompatibilityPlanResult
    {
        internal bool ModelCatalogSucceeded = true;
        internal bool SandboxSucceeded = true;
        internal bool LocalizationSucceeded = true;
        internal CompatibilityFeatureChange ModelCatalogChange;
        internal CompatibilityFeatureChange SandboxChange;
        internal CompatibilityFeatureChange LocalizationChange;
    }

    internal sealed class CompatibilityFeatureChange
    {
        internal bool Succeeded;
        internal bool Changed;
        internal string CompletionMessage;
        internal Action<AsarSession> Verify;
        internal string Before;
        internal string Desired;
        internal string After;
        internal CompatibilityFeatureStatus Status;
        internal string Error;
        internal string RecipeId;

        internal static CompatibilityFeatureChange Failure(
            string error = null,
            CompatibilityFeatureStatus status = CompatibilityFeatureStatus.Failed)
        {
            return new CompatibilityFeatureChange
            {
                Succeeded = false,
                Status = status,
                Error = error
            };
        }

        internal static CompatibilityFeatureChange Unmanaged(string desired)
        {
            return new CompatibilityFeatureChange
            {
                Succeeded = true,
                Changed = false,
                Before = "UnmanagedOrOfficial",
                Desired = desired,
                After = "UnmanagedOrOfficial",
                Status = CompatibilityFeatureStatus.AlreadySatisfied
            };
        }

        internal CompatibilityFeatureResult ToFeatureResult(
            string featureId,
            string displayName,
            string desired,
            string recipeId)
        {
            return new CompatibilityFeatureResult
            {
                FeatureId = featureId,
                DisplayName = displayName,
                Before = Before ?? "Unknown",
                Desired = Desired ?? desired,
                After = After ?? (Succeeded ? Desired ?? desired : Before ?? "Unknown"),
                Changed = Changed,
                Status = Status,
                Error = Error,
                RecipeId = RecipeId ?? recipeId
            };
        }
    }

    internal sealed class CompatibilityPlan
    {
        private readonly Action<string> log;

        internal CompatibilityPlan(Action<string> logAction)
        {
            log = logAction ?? delegate { };
        }

        internal CompatibilityPlanResult Apply(string executablePath, CompatibilityOptions options)
        {
            return Apply(executablePath, options, false);
        }

        internal CompatibilityPlanResult Apply(
            string executablePath,
            CompatibilityOptions options,
            bool defaultUnsupportedToDisabled)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!options.ManageModelCatalog &&
                !options.ManageSandboxCompatibility &&
                !options.ManageLocalization)
            {
                return new CompatibilityPlanResult();
            }
            return ApplyInternal(
                executablePath,
                options.ManageModelCatalog,
                options.UnlockModelCatalogEnabled,
                options.ManageSandboxCompatibility,
                options.SandboxCompatibilityEnabled,
                options.ManageLocalization,
                options.SupplementChineseUiEnabled,
                options.EnglishTechnicalParametersEnabled,
                defaultUnsupportedToDisabled);
        }

        internal bool ApplyModel(string executablePath, bool enabled)
        {
            return ApplyInternal(executablePath, true, enabled, false, false, false, false, false, false)
                .ModelCatalogSucceeded;
        }

        internal bool ApplySandbox(string executablePath, bool enabled)
        {
            return ApplyInternal(executablePath, false, false, true, enabled, false, false, false, false)
                .SandboxSucceeded;
        }

        internal bool ApplyLocalization(string executablePath, bool chineseMenusEnabled, bool englishReasoningEnabled)
        {
            return ApplyInternal(
                executablePath,
                false,
                false,
                false,
                false,
                true,
                chineseMenusEnabled,
                englishReasoningEnabled,
                false).LocalizationSucceeded;
        }

        private CompatibilityPlanResult ApplyInternal(
            string executablePath,
            bool includeModel,
            bool modelEnabled,
            bool includeSandbox,
            bool sandboxEnabled,
            bool includeLocalization,
            bool chineseMenusEnabled,
            bool englishReasoningEnabled,
            bool defaultUnsupportedToDisabled)
        {
            CompatibilityPlanResult result = new CompatibilityPlanResult();
            string asarPath;
            try
            {
                asarPath = AsarSession.GetAsarPath(executablePath);
            }
            catch (Exception exception)
            {
                LogUnavailableFeatures(
                    includeModel,
                    modelEnabled,
                    includeSandbox,
                    sandboxEnabled,
                    includeLocalization,
                    exception);
                SetIncludedFailures(result, includeModel, includeSandbox, includeLocalization);
                return result;
            }

            if (!File.Exists(asarPath))
            {
                FileNotFoundException exception = new FileNotFoundException("没有找到 Codex app.asar。", asarPath);
                LogUnavailableFeatures(
                    includeModel,
                    modelEnabled,
                    includeSandbox,
                    sandboxEnabled,
                    includeLocalization,
                    exception);
                SetIncludedFailures(result, includeModel, includeSandbox, includeLocalization);
                return result;
            }

            List<string> markers = new List<string>();
            if (includeModel) markers.AddRange(ModelCatalogCompatibility.ManagedMarkers);
            if (includeSandbox) markers.Add(SandboxCompatibility.ManagedMarker);
            if (includeLocalization) markers.AddRange(CodexLocalizationCompatibility.ManagedMarkers);

            IDictionary<string, int> markerCounts;
            try
            {
                markerCounts = AsarSession.CountPatterns(asarPath, markers);
            }
            catch (Exception exception)
            {
                LogUnavailableFeatures(
                    includeModel,
                    modelEnabled,
                    includeSandbox,
                    sandboxEnabled,
                    includeLocalization,
                    exception);
                SetIncludedFailures(result, includeModel, includeSandbox, includeLocalization);
                return result;
            }

            bool modelMarkerPresent = includeModel &&
                ModelCatalogCompatibility.ManagedMarkers.Any(value => Count(markerCounts, value) > 0);
            bool sandboxMarkerPresent = includeSandbox && Count(markerCounts, SandboxCompatibility.ManagedMarker) > 0;
            bool menuMarkerPresent = includeLocalization && CodexLocalizationCompatibility.MenuMarkers.Any(value => Count(markerCounts, value) > 0);
            bool reasoningMarkerPresent = includeLocalization &&
                Count(markerCounts, CodexLocalizationCompatibility.ReasoningFamilyMarker) > 0;
            bool modelActive = includeModel && (modelEnabled || modelMarkerPresent);
            bool sandboxActive = includeSandbox && (sandboxEnabled || sandboxMarkerPresent);
            bool localizationActive = includeLocalization &&
                (chineseMenusEnabled || englishReasoningEnabled || menuMarkerPresent || reasoningMarkerPresent);

            if (includeModel && !modelActive)
            {
                SafeLog("未检测到本工具的模型 catalog 补丁，已保留官方或未管理的 app.asar。");
            }
            if (includeLocalization && !localizationActive)
            {
                SafeLog("未检测到本工具的界面语言补丁，已保留官方或未管理的 app.asar。");
            }
            if (includeSandbox && !sandboxActive)
            {
                SafeLog("未检测到本工具的 Windows 沙箱账户名补丁，已保留官方 app.asar。");
            }
            if (!modelActive && !sandboxActive && !localizationActive) return result;

            AsarSession session;
            try
            {
                session = AsarSession.Open(asarPath);
            }
            catch (Exception exception)
            {
                LogUnavailableFeatures(
                    modelActive,
                    modelEnabled,
                    sandboxActive,
                    sandboxEnabled,
                    localizationActive,
                    exception);
                SetIncludedFailures(result, modelActive, sandboxActive, localizationActive);
                return result;
            }

            try
            {
            CompatibilityFeatureChange modelChange = modelActive
                ? ModelCatalogCompatibility.Plan(
                    session,
                    modelEnabled,
                    log)
                : CompatibilityFeatureChange.Unmanaged(CompatibilityPatchState.Official.ToString());
            if (defaultUnsupportedToDisabled &&
                modelEnabled &&
                modelChange.Status == CompatibilityFeatureStatus.Unsupported &&
                !modelChange.Changed &&
                string.Equals(
                    modelChange.Before,
                    CompatibilityPatchState.Official.ToString(),
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    modelChange.After,
                    CompatibilityPatchState.Official.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                string reason = modelChange.Error;
                modelChange.Succeeded = true;
                modelChange.Desired = CompatibilityPatchState.Official.ToString();
                modelChange.Status = CompatibilityFeatureStatus.NotRequired;
                modelChange.Error = string.IsNullOrWhiteSpace(reason)
                    ? "当前版本不支持该功能，已默认关闭。"
                    : "当前版本不支持该功能，已默认关闭：" + reason;
                modelChange.CompletionMessage =
                    "当前版本没有可安全修改的模型白名单入口，外部模型显示功能已默认关闭。";
            }
            CompatibilityFeatureChange sandboxChange = sandboxActive
                ? SandboxCompatibility.Plan(
                    session,
                    sandboxEnabled,
                    log)
                : CompatibilityFeatureChange.Unmanaged(CompatibilityPatchState.Official.ToString());
            CompatibilityFeatureChange localizationChange = localizationActive
                ? CodexLocalizationCompatibility.Plan(
                    session,
                    chineseMenusEnabled,
                    englishReasoningEnabled,
                    chineseMenusEnabled || menuMarkerPresent,
                    englishReasoningEnabled || reasoningMarkerPresent,
                    log)
                : CompatibilityFeatureChange.Unmanaged("Menus=NotManaged;Reasoning=NotManaged");

            result.ModelCatalogChange = includeModel ? modelChange : null;
            result.SandboxChange = includeSandbox ? sandboxChange : null;
            result.LocalizationChange = includeLocalization ? localizationChange : null;
            result.ModelCatalogSucceeded = !includeModel || modelChange.Succeeded;
            result.SandboxSucceeded = !includeSandbox || sandboxChange.Succeeded;
            result.LocalizationSucceeded = !includeLocalization || localizationChange.Succeeded;

            List<CompatibilityFeatureChange> changed = new List<CompatibilityFeatureChange>();
            if (modelChange.Succeeded && modelChange.Changed) changed.Add(modelChange);
            if (sandboxChange.Succeeded && sandboxChange.Changed) changed.Add(sandboxChange);
            if (localizationChange.Changed &&
                (localizationChange.Succeeded ||
                 localizationChange.Status == CompatibilityFeatureStatus.Unsupported))
            {
                changed.Add(localizationChange);
            }
            if (changed.Count == 0)
            {
                try
                {
                    session.ValidateAllEntries();
                }
                catch (Exception exception)
                {
                    if (modelActive)
                    {
                        result.ModelCatalogSucceeded = false;
                        MarkValidationFailure(modelChange, exception);
                    }
                    if (localizationActive)
                    {
                        result.LocalizationSucceeded = false;
                        MarkValidationFailure(localizationChange, exception);
                    }
                    if (sandboxActive)
                    {
                        result.SandboxSucceeded = false;
                        MarkValidationFailure(sandboxChange, exception);
                    }
                    LogUnavailableFeatures(
                        modelActive,
                        modelEnabled,
                        sandboxActive,
                        sandboxEnabled,
                        localizationActive,
                        exception);
                    return result;
                }
                LogCompletion(modelChange);
                LogCompletion(sandboxChange);
                LogCompletion(localizationChange);
                return result;
            }

            try
            {
                session.WriteAtomically(verified =>
                {
                    foreach (CompatibilityFeatureChange feature in changed)
                    {
                        if (feature.Verify != null) feature.Verify(verified);
                    }
                });
                LogCompletion(modelChange);
                LogCompletion(sandboxChange);
                LogCompletion(localizationChange);
            }
            catch (Exception exception)
            {
                if (modelChange.Changed)
                {
                    result.ModelCatalogSucceeded = false;
                    modelChange.Status = CompatibilityFeatureStatus.Failed;
                    modelChange.Error = exception.Message;
                    modelChange.After = modelChange.Before;
                    modelChange.Changed = false;
                    SafeLog("警告：模型 catalog 兼容设置未能完成。统一 ASAR 临时文件验证失败，正式文件未被替换；原因：" + exception.Message);
                }
                if (localizationChange.Changed)
                {
                    result.LocalizationSucceeded = false;
                    localizationChange.Status = CompatibilityFeatureStatus.Failed;
                    localizationChange.Error = exception.Message;
                    localizationChange.After = localizationChange.Before;
                    localizationChange.Changed = false;
                    SafeLog("警告：Codex 界面语言兼容设置未能完成。统一 ASAR 临时文件验证失败，正式文件未被替换；原因：" + exception.Message);
                }
                if (sandboxChange.Changed)
                {
                    result.SandboxSucceeded = false;
                    sandboxChange.Status = CompatibilityFeatureStatus.Failed;
                    sandboxChange.Error = exception.Message;
                    sandboxChange.After = sandboxChange.Before;
                    sandboxChange.Changed = false;
                    SafeLog("警告：Windows 沙箱账户名兼容设置未能完成。统一 ASAR 临时文件验证失败，正式文件未被替换；原因：" + exception.Message);
                }
            }
            return result;
            }
            finally
            {
                session.Dispose();
            }
        }

        private void LogUnavailableFeatures(
            bool model,
            bool modelEnabled,
            bool sandbox,
            bool sandboxEnabled,
            bool localization,
            Exception exception)
        {
            if (model) ModelCatalogCompatibility.LogUnavailable(log, modelEnabled, exception);
            if (sandbox)
            {
                SafeLog("警告：Windows 沙箱账户名兼容设置与当前 app.asar 不兼容，已保留完整文件。原因：" + exception.Message);
            }
            if (localization) CodexLocalizationCompatibility.LogUnavailable(log, exception);
        }

        private static void SetIncludedFailures(
            CompatibilityPlanResult result,
            bool model,
            bool sandbox,
            bool localization)
        {
            if (model)
            {
                result.ModelCatalogSucceeded = false;
                result.ModelCatalogChange = CompatibilityFeatureChange.Failure("无法读取或分析 app.asar。");
            }
            if (sandbox)
            {
                result.SandboxSucceeded = false;
                result.SandboxChange = CompatibilityFeatureChange.Failure("无法读取或分析 app.asar。");
            }
            if (localization)
            {
                result.LocalizationSucceeded = false;
                result.LocalizationChange = CompatibilityFeatureChange.Failure("无法读取或分析 app.asar。");
            }
        }

        private void LogCompletion(CompatibilityFeatureChange change)
        {
            if (change != null && change.Succeeded && !string.IsNullOrWhiteSpace(change.CompletionMessage))
            {
                SafeLog(change.CompletionMessage);
            }
        }

        private static void MarkValidationFailure(
            CompatibilityFeatureChange change,
            Exception exception)
        {
            if (change == null) return;
            change.Succeeded = false;
            change.Changed = false;
            change.Status = CompatibilityFeatureStatus.Failed;
            change.Error = exception == null ? "ASAR 全条目完整性验证失败。" : exception.Message;
            change.After = change.Before;
        }

        private static int Count(IDictionary<string, int> counts, string pattern)
        {
            int value;
            return counts.TryGetValue(pattern, out value) ? value : 0;
        }

        private void SafeLog(string message)
        {
            try { log(message); }
            catch { }
        }
    }
}
