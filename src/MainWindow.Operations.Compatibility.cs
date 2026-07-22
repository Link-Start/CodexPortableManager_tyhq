using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;

namespace CodexPortableManager
{
    internal sealed partial class MainWindow
    {
        private async Task InspectCompatibilitySettingsAsync(
            OperationSnapshot snapshot,
            CancellationToken token)
        {
            CreateProgress().Report(new OperationProgress(
                "验证便携版文件完整性",
                20,
                "正在核对安装来源与关键派生文件摘要；验证不会修改文件。"));
            await LoadCompatibilityOverviewAsync(snapshot, true, token, true);
            CompatibilityOverview overview = compatibilityOverviewPathRevision == snapshot.InstallPathRevision
                ? compatibilityOverview
                : null;
            CreateProgress().Report(new OperationProgress(
                overview != null && overview.State == CompatibilityOverviewState.Verified
                    ? "便携版文件完整性已验证"
                    : "便携版文件完整性需要处理",
                100,
                overview == null || string.IsNullOrWhiteSpace(overview.Detail)
                    ? "无法取得当前功能状态，请查看运行日志。"
                    : overview.Detail));
        }

        private async Task LoadCompatibilityOverviewAsync(
            OperationSnapshot snapshot,
            bool verifyArtifacts,
            CancellationToken token,
            bool allowWhileBusy)
        {
            if (snapshot == null) return;
            if (!portableVersionAvailable || installPathInvalid ||
                string.IsNullOrWhiteSpace(snapshot.InstallRoot))
            {
                if (snapshot.InstallPathRevision == installPathRevision)
                {
                    ResetCompatibilitySwitchesForUnavailableInstallation();
                    compatibilityOverview = null;
                    compatibilityOverviewPathRevision = snapshot.InstallPathRevision;
                    UpdateCompatibilityPresentation();
                    ApplyUiState();
                }
                return;
            }

            CompatibilityOverview overview;
            try
            {
                overview = await service.GetCompatibilityOverviewAsync(
                    snapshot.InstallRoot,
                    verifyArtifacts,
                    token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                overview = new CompatibilityOverview(
                    CompatibilityOverviewState.Unknown,
                    "读取当前兼容状态失败：" + exception.Message,
                    new CompatibilityObservedFeature[0],
                    new string[0],
                    false);
                AppendLog("读取当前兼容状态失败：" + exception.Message);
            }
            if (!IsLoaded || snapshot.InstallPathRevision != installPathRevision ||
                (operationController.State.Busy && !allowWhileBusy))
            {
                return;
            }
            compatibilityOverview = overview;
            compatibilityOverviewPathRevision = snapshot.InstallPathRevision;
            InitializeCompatibilitySwitchesFromOverview(snapshot);
            UpdateCompatibilityPresentation();
            ApplyUiState();
        }

        private void InitializeCompatibilitySwitchesFromOverview(OperationSnapshot snapshot)
        {
            if (snapshot == null ||
                snapshot.InstallPathRevision != installPathRevision ||
                compatibilityOverviewPathRevision != installPathRevision ||
                compatibilityOverview == null)
            {
                return;
            }

            CompatibilitySwitchFacts facts = CompatibilityStatusReader.ResolveSwitchFacts(
                compatibilityOverview);
            bool preserveKnownDraft = compatibilityDraftDirty;

            updatingCompatibilitySwitches = true;
            try
            {
                if (!preserveKnownDraft || !facts.SandboxCompatibilityEnabled.HasValue)
                {
                    sandboxCompatibilityCheckBox.IsChecked =
                        facts.SandboxCompatibilityEnabled.GetValueOrDefault();
                }
                if (!preserveKnownDraft || !facts.UnlockModelCatalogEnabled.HasValue)
                {
                    unlockModelCatalogCheckBox.IsChecked =
                        facts.UnlockModelCatalogEnabled.GetValueOrDefault();
                }
                if (!preserveKnownDraft || !facts.SupplementChineseUiEnabled.HasValue)
                {
                    supplementChineseUiCheckBox.IsChecked =
                        facts.SupplementChineseUiEnabled.GetValueOrDefault();
                }
                if (!preserveKnownDraft || !facts.EnglishTechnicalParametersEnabled.HasValue)
                {
                    englishTechnicalParametersCheckBox.IsChecked =
                        facts.EnglishTechnicalParametersEnabled.GetValueOrDefault();
                }
            }
            finally
            {
                updatingCompatibilitySwitches = false;
            }

            if (preserveKnownDraft) return;
            compatibilityDraftDirty = false;
            AppendLog(facts.AllKnown
                ? "已根据当前便携版的实际功能状态初始化兼容设置开关。"
                : "已按当前便携版的可确认事实刷新兼容设置开关；异常或无法确认的选项已关闭并禁用。");
        }

        private void ResetCompatibilitySwitchesForUnavailableInstallation()
        {
            updatingCompatibilitySwitches = true;
            try
            {
                sandboxCompatibilityCheckBox.IsChecked = false;
                unlockModelCatalogCheckBox.IsChecked = false;
                supplementChineseUiCheckBox.IsChecked = false;
                englishTechnicalParametersCheckBox.IsChecked = false;
            }
            finally
            {
                updatingCompatibilitySwitches = false;
            }
            compatibilityDraftDirty = false;
        }

        internal static bool CanInitializeCompatibilitySwitch(
            CompatibilityOverview overview,
            string featureId)
        {
            return CompatibilityStatusReader.CanResolveFeature(overview, featureId);
        }

        internal static CompatibilityOptions ResolveCompatibilityOptionsForInitialization(
            CompatibilityOverview overview)
        {
            return CompatibilityStatusReader.ResolveOptions(overview);
        }

        private async Task ApplyCompatibilitySettingsAsync(OperationSnapshot snapshot, CancellationToken token)
        {
            if (!snapshot.Compatibility.AnyManaged)
            {
                CreateProgress().Report(new OperationProgress(
                    "没有需要应用的兼容设置",
                    100,
                    "异常或未改动的功能均保持当前文件不变。"));
                return;
            }
            InstallationHealthReport health = await Task.Run(
                () => service.GetInstallationHealth(snapshot.InstallRoot),
                token);
            CompatibilityBaselineApproval baselineApproval = null;
            if (health.Status == InstallationHealthStatus.Unverified)
            {
                string details = health.Errors == null || health.Errors.Count == 0
                    ? string.Empty
                    : "\n\n" + string.Join("\n", health.Errors.Select(error => "- " + error).ToArray());
                if (MessageBox.Show(
                    this,
                    "当前便携安装缺少可追溯的官方来源基线。继续会先按当前文件明确建立本地摘要基线；这不会把来源状态标记为官方已验证。是否继续？" + details,
                    "确认建立兼容维护基线",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    throw new OperationCanceledException("用户取消建立兼容维护基线。");
                }
                baselineApproval = CompatibilityBaselineApproval.Create(snapshot.InstallRoot);
            }
            CreateProgress().Report(new OperationProgress(
                "正在应用便携版功能调整",
                20,
                "将关闭当前便携版，并且只同步本次已确认的更改；异常或未改动功能保持原状。"));
            CompatibilityResult result = await Task.Run(
                () => ApplyCompatibilitySettings(snapshot, baselineApproval),
                token);
            compatibilityDraftDirty = false;
            await LoadCompatibilityOverviewAsync(snapshot, false, token, true);
            string actualStates = result.FeatureResults.Count > 0
                ? string.Join("；", result.FeatureResults.Select(feature =>
                    feature.DisplayName + "=" + FormatCompatibilityStatus(feature.Status) +
                    (string.IsNullOrWhiteSpace(feature.Error) ? string.Empty : "（" + feature.Error + "）")).ToArray())
                : string.Join("、", result.FailedFeatures.ToArray());
            CreateProgress().Report(new OperationProgress(
                result.TransactionCommitted
                    ? (result.AllSucceeded ? "便携版功能调整已应用" : "便携版功能调整部分应用")
                    : "便携版功能调整未应用",
                100,
                result.TransactionCommitted
                    ? (result.AllSucceeded
                        ? "所有功能已按当前选择同步；请重新启动 Codex 查看界面变化。"
                        : "可支持的功能已保留；不支持的功能继续使用官方文件并等待适配。")
                    : "实际状态：" + actualStates +
                      "。本次文件变更已回滚，当前选择已保留以便重试。"));
        }

        private async Task RepairIntegrationAsync(OperationSnapshot snapshot, CancellationToken token)
        {
            CreateProgress().Report(new OperationProgress("正在修复便携版启动入口", 25, "正在重新注册快捷方式、codex://、文件关联和通知标识。"));
            IReadOnlyList<string> warnings = await Task.Run(() => service.CreateIntegration(snapshot.InstallRoot), token);
            CreateProgress().Report(new OperationProgress(
                warnings.Count == 0 ? "便携版启动入口已修复" : "便携版启动入口已部分修复",
                100,
                warnings.Count == 0
                    ? "桌面快捷方式、开始菜单、协议关联和通知标识已重新指向当前便携版目录。"
                    : "仍有项目需要重试，请查看日志。"));
        }

        private CompatibilityResult ApplyCompatibilitySettings(
            OperationSnapshot snapshot,
            CompatibilityBaselineApproval baselineApproval)
        {
            return service.ApplyCompatibilitySettings(
                snapshot.InstallRoot,
                snapshot.Compatibility,
                baselineApproval);
        }

        private static string FormatCompatibilityStatus(CompatibilityFeatureStatus status)
        {
            switch (status)
            {
                case CompatibilityFeatureStatus.Applied: return "已应用";
                case CompatibilityFeatureStatus.AlreadySatisfied: return "已是目标状态";
                case CompatibilityFeatureStatus.NotRequired: return "当前无需修改";
                case CompatibilityFeatureStatus.Unsupported: return "指纹不受支持";
                case CompatibilityFeatureStatus.RolledBack: return "已回滚";
                default: return "等待重试";
            }
        }

        private sealed class CompatibilityItemPresentation
        {
            internal string Text;
            internal string BrushKey;
            internal bool Pending;
            internal bool Blocked;
            internal bool Unknown;
            internal bool CanApply;
        }

        private void UpdateCompatibilityPresentation()
        {
            if (sandboxCompatibilityStatusLabel == null) return;
            CompatibilityItemPresentation sandbox = CreateSimpleCompatibilityPresentation(
                "SandboxCompatibility",
                sandboxCompatibilityCheckBox.IsChecked == true,
                "Enabled",
                "Disabled",
                true);
            CompatibilityItemPresentation model = CreateSimpleCompatibilityPresentation(
                "ModelCatalog",
                unlockModelCatalogCheckBox.IsChecked == true,
                "Patched",
                "Official",
                false);
            CompatibilityItemPresentation chinese = CreateLocalizationPresentation(
                "Menus",
                supplementChineseUiCheckBox.IsChecked == true);
            CompatibilityItemPresentation english = CreateLocalizationPresentation(
                "Reasoning",
                englishTechnicalParametersCheckBox.IsChecked == true);
            SetCompatibilityStatus(sandboxCompatibilityStatusLabel, sandbox);
            SetCompatibilityStatus(modelCatalogStatusLabel, model);
            SetCompatibilityStatus(chineseUiStatusLabel, chinese);
            SetCompatibilityStatus(englishParametersStatusLabel, english);

            CompatibilityItemPresentation[] items = { sandbox, model, chinese, english };
            int pending = items.Count(item => item.Pending);
            int blocked = items.Count(item => item.Blocked);
            int unknown = items.Count(item => item.Unknown);
            bool canApplyUnknown = items.Any(item => item.Unknown && item.CanApply);
            bool hasUnapplicableUnknown = items.Any(item => item.Unknown && !item.CanApply);
            compatibilityApplyNeeded = CanApplyCompatibilityChanges(
                pending,
                canApplyUnknown,
                blocked,
                items.Count(item => item.Unknown && !item.CanApply));

            if (!portableVersionAvailable)
            {
                compatibilitySummaryLabel.Text = "创建便携版后可应用这些设置。";
                applyCompatibilityButton.Content = "安装后可用";
                compatibilityApplyNeeded = false;
            }
            else if (pending > 0)
            {
                int unchanged = blocked + items.Count(item => item.Unknown && !item.CanApply);
                compatibilitySummaryLabel.Text = unchanged > 0
                    ? pending + " 项设置可应用；" + unchanged + " 项不可用设置将保持不变。"
                    : pending + " 项设置尚未同步。";
                applyCompatibilityButton.Content = "应用 " + pending + " 项更改";
            }
            else if (blocked > 0)
            {
                compatibilitySummaryLabel.Text = blocked + " 项设置不受当前版本支持，关闭后不会影响其他功能。";
                applyCompatibilityButton.Content = "不支持项保持不变";
                compatibilityApplyNeeded = false;
            }
            else if (hasUnapplicableUnknown)
            {
                compatibilitySummaryLabel.Text = compatibilityOverview != null &&
                    compatibilityOverview.State == CompatibilityOverviewState.Invalid
                    ? "当前文件状态无法验证，请先检查详情。"
                    : unknown + " 项状态无法读取，将保持当前文件不变。";
                applyCompatibilityButton.Content = "没有可应用的更改";
                compatibilityApplyNeeded = false;
            }
            else if (unknown > 0)
            {
                bool reading = items.All(item => string.Equals(
                    item.Text,
                    "读取中",
                    StringComparison.Ordinal));
                compatibilitySummaryLabel.Text = reading
                    ? "正在读取当前便携版状态。"
                    : compatibilityOverview != null &&
                        compatibilityOverview.State == CompatibilityOverviewState.Invalid
                        ? "当前文件状态无法验证，请先检查详情。"
                        : unknown + " 项状态无法读取，请验证文件完整性。";
                applyCompatibilityButton.Content = compatibilityApplyNeeded
                    ? "检查并应用设置"
                    : reading ? "读取完成后可用" : "状态无法应用";
            }
            else
            {
                compatibilitySummaryLabel.Text = compatibilityOverview != null &&
                    compatibilityOverview.State == CompatibilityOverviewState.Verified
                    ? "当前设置与便携版一致，状态已验证。"
                    : compatibilityOverview != null &&
                        compatibilityOverview.State == CompatibilityOverviewState.Inspected
                        ? "已读取当前文件，设置无需更改。"
                        : "当前设置与上次应用记录一致。";
                applyCompatibilityButton.Content = "已与当前状态一致";
                compatibilityApplyNeeded = false;
            }
        }

        internal static bool CanApplyCompatibilityChanges(
            int pending,
            bool canApplyUnknown,
            int blocked,
            int unapplicableUnknown)
        {
            if (pending < 0 || blocked < 0 || unapplicableUnknown < 0)
            {
                throw new ArgumentOutOfRangeException("兼容状态计数不能为负数。");
            }
            return pending > 0 || canApplyUnknown;
        }

        private CompatibilityItemPresentation CreateSimpleCompatibilityPresentation(
            string featureId,
            bool desired,
            string enabledValue,
            string disabledValue,
            bool sandbox)
        {
            CompatibilityItemPresentation unavailable = CreateUnavailablePresentation();
            if (unavailable != null) return unavailable;
            CompatibilityObservedFeature observed = FindObservedFeature(featureId);
            if (observed != null && !observed.RecipeCurrent)
            {
                return CreateUnknownPresentation("需要重新检查", false);
            }
            if (sandbox && observed != null &&
                observed.Status == CompatibilityFeatureStatus.NotRequired && desired)
            {
                return new CompatibilityItemPresentation
                {
                    Text = "已开启",
                    BrushKey = "SuccessBrush"
                };
            }

            bool actual;
            if (observed != null)
            {
                CompatibilityItemPresentation special = CreateObservedFailurePresentation(
                    observed,
                    desired);
                if (special != null) return special;
                if (string.Equals(observed.After, enabledValue, StringComparison.OrdinalIgnoreCase)) actual = true;
                else if (IsSimpleCompatibilityDisabledState(observed.After, disabledValue)) actual = false;
                else return CreateUnknownPresentation("无法读取", false, "DangerBrush");
            }
            else
            {
                return CreateUnknownPresentation("无法读取", false, "DangerBrush");
            }
            return CreateKnownPresentation(desired, actual, observed);
        }

        private CompatibilityItemPresentation CreateLocalizationPresentation(
            string component,
            bool desired)
        {
            CompatibilityItemPresentation unavailable = CreateUnavailablePresentation();
            if (unavailable != null) return unavailable;
            CompatibilityObservedFeature observed = FindObservedFeature("Localization");
            if (observed != null && !observed.RecipeCurrent)
            {
                return CreateUnknownPresentation("需要重新检查", false);
            }

            bool actual;
            if (observed != null)
            {
                CompatibilityItemPresentation special = CreateObservedFailurePresentation(
                    observed,
                    desired);
                if (special != null) return special;
                string value = GetCompatibilityComponent(observed.After, component);
                if (string.Equals(value, "Patched", StringComparison.OrdinalIgnoreCase)) actual = true;
                else if (string.Equals(
                    value,
                    "PatchedRefreshRequired",
                    StringComparison.OrdinalIgnoreCase))
                {
                    actual = true;
                    if (desired)
                    {
                        return new CompatibilityItemPresentation
                        {
                            Text = "需要刷新",
                            BrushKey = "WarningBrush",
                            Pending = true,
                            CanApply = true
                        };
                    }
                }
                else if (string.Equals(value, "Official", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "NotManaged", StringComparison.OrdinalIgnoreCase)) actual = false;
                else return CreateUnknownPresentation("无法读取", false, "DangerBrush");
            }
            else
            {
                return CreateUnknownPresentation("无法读取", false, "DangerBrush");
            }
            return CreateKnownPresentation(desired, actual, observed);
        }

        private CompatibilityItemPresentation CreateKnownPresentation(
            bool desired,
            bool actual,
            CompatibilityObservedFeature observed)
        {
            if (desired == actual)
            {
                return new CompatibilityItemPresentation
                {
                    Text = actual ? "当前已开启" : "当前未开启",
                    BrushKey = actual ? "SuccessBrush" : "MutedBrush"
                };
            }
            if (observed != null && observed.Status == CompatibilityFeatureStatus.Unsupported)
            {
                return new CompatibilityItemPresentation
                {
                    Text = "新版不支持",
                    BrushKey = "DangerBrush",
                    Blocked = true
                };
            }
            bool retry = observed != null &&
                (observed.Status == CompatibilityFeatureStatus.Failed ||
                 observed.Status == CompatibilityFeatureStatus.RolledBack);
            return new CompatibilityItemPresentation
            {
                Text = retry ? "等待重试" : actual ? "当前已开启" : "当前未开启",
                BrushKey = "WarningBrush",
                Pending = true,
                CanApply = true
            };
        }

        private CompatibilityItemPresentation CreateUnavailablePresentation()
        {
            if (!portableVersionAvailable)
            {
                return CreateUnknownPresentation("安装后可用", false);
            }
            if (compatibilityOverview == null ||
                compatibilityOverviewPathRevision != installPathRevision)
            {
                return CreateUnknownPresentation("读取中", false);
            }
            if (compatibilityOverview.State == CompatibilityOverviewState.Invalid)
            {
                return CreateUnknownPresentation("无法验证", false, "DangerBrush");
            }
            if (compatibilityOverview.State == CompatibilityOverviewState.Unknown ||
                compatibilityOverview.State == CompatibilityOverviewState.Unavailable)
            {
                return CreateUnknownPresentation("无法读取", false, "DangerBrush");
            }
            return null;
        }

        private static CompatibilityItemPresentation CreateObservedFailurePresentation(
            CompatibilityObservedFeature observed,
            bool desired)
        {
            if (observed == null) return null;
            if (observed.Status == CompatibilityFeatureStatus.Unsupported)
            {
                return desired
                    ? new CompatibilityItemPresentation
                    {
                        Text = "新版不支持",
                        BrushKey = "DangerBrush",
                        Blocked = true
                    }
                    : new CompatibilityItemPresentation
                    {
                        Text = "版本不适用",
                        BrushKey = "MutedBrush"
                    };
            }
            if (observed.Status == CompatibilityFeatureStatus.Failed &&
                (string.Equals(observed.After, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                 (observed.After ?? string.Empty).IndexOf(
                    "Mixed",
                    StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return CreateUnknownPresentation(
                    string.Equals(observed.After, "Unknown", StringComparison.OrdinalIgnoreCase)
                        ? "无法读取"
                        : "文件异常",
                    false,
                    "DangerBrush");
            }
            return null;
        }

        private static CompatibilityItemPresentation CreateUnknownPresentation(
            string text,
            bool canApply,
            string brushKey = "MutedBrush")
        {
            return new CompatibilityItemPresentation
            {
                Text = text,
                BrushKey = brushKey,
                Unknown = true,
                CanApply = canApply
            };
        }

        private CompatibilityObservedFeature FindObservedFeature(string featureId)
        {
            return compatibilityOverview == null
                ? null
                : compatibilityOverview.Features.FirstOrDefault(feature => string.Equals(
                    feature.FeatureId,
                    featureId,
                    StringComparison.OrdinalIgnoreCase));
        }

        internal static bool? ResolveSimpleCompatibilityState(
            CompatibilityOverview overview,
            string featureId,
            string enabledValue,
            string disabledValue)
        {
            return CompatibilityStatusReader.ResolveSimpleState(
                overview,
                featureId,
                enabledValue,
                disabledValue);
        }

        private static bool IsSimpleCompatibilityDisabledState(
            string value,
            string disabledValue)
        {
            return string.Equals(value, disabledValue, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "UnmanagedOrOfficial", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool? ResolveLocalizationCompatibilityState(
            CompatibilityOverview overview,
            string component)
        {
            return CompatibilityStatusReader.ResolveLocalizationState(overview, component);
        }

        private static string GetCompatibilityComponent(string value, string component)
        {
            foreach (string pair in (value ?? string.Empty).Split(';'))
            {
                int separator = pair.IndexOf('=');
                if (separator <= 0) continue;
                if (string.Equals(
                    pair.Substring(0, separator),
                    component,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Substring(separator + 1);
                }
            }
            return null;
        }

        private void SetCompatibilityStatus(
            TextBlock label,
            CompatibilityItemPresentation presentation)
        {
            label.Text = presentation.Text;
            label.Foreground = ResolveBrush(presentation.BrushKey ?? "MutedBrush");
        }



    }
}
