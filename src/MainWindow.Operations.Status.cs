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
        internal sealed class StatusSummaryPresentation
        {
            internal StatusSummaryPresentation(string text, string brushKey)
            {
                Text = text;
                BrushKey = brushKey;
            }

            internal string Text { get; private set; }
            internal string BrushKey { get; private set; }
        }

        private async Task RefreshStatusAsync(OperationSnapshot snapshot, CancellationToken token)
        {
            string previousLatestVersion = latestValueLabel.Text;
            string previousStatusSummary = statusValueLabel.Text;
            System.Windows.Media.Brush previousStatusForeground = statusValueLabel.Foreground;
            System.Windows.Media.Brush previousStatusIndicator = statusIndicator.Background;
            PortableLocalStatus appliedLocalStatus = null;
            ShowCheckRunningPresentation();
            try
            {
                await RefreshStatusCoreAsync(
                    snapshot,
                    token,
                    status => appliedLocalStatus = status);
            }
            catch (OperationCanceledException)
            {
                RestoreLatestVersionAfterIncompleteCheck(previousLatestVersion, "未完成检查");
                RestoreStatusAfterIncompleteCheck(
                    snapshot,
                    appliedLocalStatus,
                    previousStatusSummary,
                    previousStatusForeground,
                    previousStatusIndicator);
                throw;
            }
            catch
            {
                RestoreLatestVersionAfterIncompleteCheck(previousLatestVersion, "检查失败");
                RestoreStatusAfterIncompleteCheck(
                    snapshot,
                    appliedLocalStatus,
                    previousStatusSummary,
                    previousStatusForeground,
                    previousStatusIndicator);
                throw;
            }
        }

        private void RestoreStatusAfterIncompleteCheck(
            OperationSnapshot snapshot,
            PortableLocalStatus appliedLocalStatus,
            string previousText,
            System.Windows.Media.Brush previousForeground,
            System.Windows.Media.Brush previousIndicator)
        {
            if (appliedLocalStatus != null &&
                snapshot.InstallPathRevision == installPathRevision)
            {
                ApplyLocalPathStatus(snapshot, appliedLocalStatus, true, false, true);
                return;
            }
            RestoreStatusSummaryAfterIncompleteCheck(
                previousText,
                previousForeground,
                previousIndicator);
        }

        private void ShowCheckRunningPresentation()
        {
            latestValueLabel.Text = "检查中...";
            SetStatusSummary("正在检查", "PrimaryBrush");
        }

        private void RestoreStatusSummaryAfterIncompleteCheck(
            string previousText,
            System.Windows.Media.Brush previousForeground,
            System.Windows.Media.Brush previousIndicator)
        {
            if (!IsLoaded) return;
            statusValueLabel.Text = previousText;
            statusValueLabel.Foreground = previousForeground;
            statusIndicator.Background = previousIndicator;
        }

        private async Task RefreshStatusCoreAsync(
            OperationSnapshot snapshot,
            CancellationToken token,
            Action<PortableLocalStatus> localStatusApplied)
        {
            IProgress<OperationProgress> progress = CreateProgress();
            progress.Report(CreateCheckRunningProgress());
            Task<PortableLocalStatus> localTask = service.GetLocalStatusAsync(snapshot.InstallRoot, token);
            Task<PortableStatus> statusTask = service.GetStatusAsync(localTask, token);
            PortableLocalStatus localStatus;
            PortableStatus status;
            try
            {
                localStatus = await localTask;
                ApplyLocalPathStatus(snapshot, localStatus, true, false, false);
                if (localStatusApplied != null) localStatusApplied(localStatus);
                if (localStatus.OldBackupCleanupPending)
                {
                    ResetCompatibilityOverviewForPendingCleanup();
                }
                else
                {
                    await EnsureCompatibilityOverviewLoadedAsync(snapshot, token, true);
                }
                status = await statusTask;
            }
            catch
            {
                try { await statusTask; }
                catch { }
                throw;
            }
            if (!IsLoaded) return;
            ApplyStorePackagePresentation(status.StoreState, false);
            latestValueLabel.Text = status.LatestPackage.version;
            UpdateAvailabilityButtons();

            if (snapshot.InstallPathRevision != installPathRevision)
            {
                progress.Report(new OperationProgress(
                    "版本检查完成，目标目录已变更",
                    100,
                    "微软最新包版本为 " + status.LatestPackage.version + "；正在按新的目标目录刷新本地状态。"));
                return;
            }

            ApplyCheckStatusSummary(localStatus, status);
            progress.Report(CreateCheckCompletion(localStatus, status));
        }

        internal static OperationProgress CreateCheckRunningProgress()
        {
            return new OperationProgress(
                "正在检查 Codex 版本与安装状态",
                null,
                "正在同时检测本机官方桌面版、便携版、回滚备份和微软最新版本。");
        }

        internal static OperationProgress CreateCheckCompletion(
            PortableLocalStatus localStatus,
            PortableStatus status)
        {
            if (localStatus == null) throw new ArgumentNullException(nameof(localStatus));
            if (status == null || status.LatestPackage == null) throw new ArgumentNullException(nameof(status));

            Version latest = new Version(status.LatestPackage.version);
            string storeDetail;
            switch (status.StoreState)
            {
                case StorePackageState.Installed:
                    storeDetail = "官方桌面版已安装";
                    break;
                case StorePackageState.NotInstalled:
                    storeDetail = "官方桌面版未安装";
                    break;
                default:
                    storeDetail = "官方桌面版检测未完成，详情请查看日志";
                    break;
            }

            if (!localStatus.HasInstallRoot)
            {
                return new OperationProgress(
                    "检查完成：尚未选择便携版目标目录",
                    100,
                    "微软最新包版本为 " + latest + "；" + storeDetail + "。请先选择便携版目标目录。");
            }
            if (!string.IsNullOrWhiteSpace(localStatus.Error))
            {
                return new OperationProgress(
                    "版本检查完成，但目标目录无法读取",
                    100,
                    "微软最新包版本为 " + latest + "；" + storeDetail + "。目标目录错误：" + localStatus.Error);
            }
            if (localStatus.UninstallDirectoryCleanupPending ||
                localStatus.ShellIntegrationCleanupPending)
            {
                return new OperationProgress(
                    "版本检查完成：上次卸载清理待完成",
                    100,
                    "当前版本已从活动槽移除，但仍有程序目录或系统入口待清理；" +
                    storeDetail + "。关闭占用程序后再次检查即可继续清理。");
            }

            string rollbackDetail = localStatus.RollbackVersionAvailable
                ? "回滚目标可用"
                : "没有可用的回滚目标";
            string cleanupDetail = localStatus.OldBackupCleanupPending
                ? "；旧回滚备份暂未清理，不影响当前版本启动"
                : string.Empty;
            if (localStatus.PortableVersion == null)
            {
                return new OperationProgress(
                    "检查完成：当前目录尚未创建 Codex 便携版",
                    100,
                    "微软最新包版本为 " + latest + "；" + storeDetail + "；" + rollbackDetail +
                    cleanupDetail + "。可使用“创建 / 更新 / 修复”创建便携版。");
            }
            if (localStatus.PortableVersion == latest)
            {
                return new OperationProgress(
                    "版本检查完成：当前便携版已是最新版本",
                    100,
                    "当前便携版与微软最新包版本均为 " + localStatus.PortableVersion + "；" +
                    storeDetail + "；" + rollbackDetail + cleanupDetail + "。");
            }
            if (localStatus.PortableVersion > latest)
            {
                return new OperationProgress(
                    "微软当前提供的版本低于本地版本",
                    100,
                    "当前便携版为 " + localStatus.PortableVersion + "，微软当前提供 " + latest +
                    "；可能正在回退或分阶段发布，管理器不会自动降级；" +
                    storeDetail + "；" + rollbackDetail + cleanupDetail + "。");
            }
            return new OperationProgress(
                "发现 Codex 新版本 " + latest,
                100,
                "当前便携版为 " + localStatus.PortableVersion + "，可以更新到 " + latest + "；" +
                storeDetail + "；" + rollbackDetail + cleanupDetail + "。");
        }

        private void ApplyCheckStatusSummary(
            PortableLocalStatus localStatus,
            PortableStatus status)
        {
            Version latest;
            if (!Version.TryParse(status.LatestPackage.version, out latest))
            {
                latest = null;
            }
            SetStatusSummary(ResolveStatusSummary(
                localStatus,
                status.StoreState == StorePackageState.Installed,
                latest));
        }

        internal static StatusSummaryPresentation ResolveStatusSummary(
            PortableLocalStatus localStatus,
            bool storeInstalled,
            Version latest)
        {
            if (localStatus == null) throw new ArgumentNullException(nameof(localStatus));
            if (!localStatus.HasInstallRoot)
            {
                return storeInstalled
                    ? new StatusSummaryPresentation("检测到官方桌面版", "WarningActionBrush")
                    : new StatusSummaryPresentation("未选择目标目录", "MutedBrush");
            }
            if (!string.IsNullOrWhiteSpace(localStatus.Error))
            {
                return new StatusSummaryPresentation("路径无效", "DangerBrush");
            }
            if (localStatus.UninstallDirectoryCleanupPending ||
                localStatus.ShellIntegrationCleanupPending)
            {
                return new StatusSummaryPresentation("卸载清理待完成", "WarningBrush");
            }
            if (localStatus.OldBackupCleanupPending)
            {
                return new StatusSummaryPresentation("已安装，待清理", "WarningBrush");
            }
            if (localStatus.PortableVersion == null)
            {
                return storeInstalled
                    ? new StatusSummaryPresentation("检测到官方桌面版", "WarningActionBrush")
                    : new StatusSummaryPresentation("尚未安装", "MutedBrush");
            }
            if (latest == null)
            {
                return new StatusSummaryPresentation("已检测到安装", "SuccessBrush");
            }
            if (localStatus.PortableVersion == latest)
            {
                return new StatusSummaryPresentation("已是最新版本", "SuccessBrush");
            }
            return localStatus.PortableVersion > latest
                ? new StatusSummaryPresentation("本地版本较高", "WarningBrush")
                : new StatusSummaryPresentation("发现新版本", "WarningActionBrush");
        }

        private void ApplyStorePackagePresentation(
            StorePackageState state,
            bool preserveWarningOnUnknown)
        {
            if (state == StorePackageState.Installed)
            {
                storeVersionInstalled = true;
                storeValueLabel.Text = "已安装官方桌面版（Microsoft Store / MSIX）";
                storeWarningCard.Visibility = Visibility.Visible;
                return;
            }
            if (state == StorePackageState.NotInstalled)
            {
                storeVersionInstalled = false;
                storeValueLabel.Text = "未安装官方桌面版";
                storeWarningCard.Visibility = Visibility.Collapsed;
                return;
            }

            storeVersionInstalled = preserveWarningOnUnknown;
            storeValueLabel.Text = preserveWarningOnUnknown
                ? "官方桌面版卸载失败，当前状态待确认"
                : "官方桌面版检测失败";
            storeWarningCard.Visibility = preserveWarningOnUnknown
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        internal void ApplyStatusForRenderTest(
            PortableLocalStatus localStatus,
            PortableStatus status)
        {
            if (localStatus == null) throw new ArgumentNullException(nameof(localStatus));
            if (status == null || status.LatestPackage == null) throw new ArgumentNullException(nameof(status));

            OperationSnapshot snapshot = CaptureOperationSnapshot();
            ApplyLocalPathStatus(snapshot, localStatus, true, false, false);
            ApplyStorePackagePresentation(status.StoreState, false);
            latestValueLabel.Text = status.LatestPackage.version;
            ApplyCheckStatusSummary(localStatus, status);
            OperationProgress completion = CreateCheckCompletion(localStatus, status);
            ShowTaskState(
                completion.Message,
                completion.Detail,
                "100% · 已用 0:04",
                TaskProgressMode.Determinate,
                100);
            ApplyUiState();
        }

        private void RestoreLatestVersionAfterIncompleteCheck(
            string previousLatestVersion,
            string fallbackText)
        {
            if (!IsLoaded) return;
            latestValueLabel.Text = ResolveLatestVersionAfterIncompleteCheckText(
                previousLatestVersion,
                fallbackText);
        }

        internal static string ResolveLatestVersionAfterIncompleteCheckText(
            string previousLatestVersion,
            string fallbackText)
        {
            Version parsed;
            return Version.TryParse(previousLatestVersion, out parsed)
                ? previousLatestVersion
                : fallbackText;
        }

        private async Task<PortableLocalStatus> RefreshLocalPathStatusAsync(
            OperationSnapshot snapshot,
            bool allowWhileBusy = false,
            bool updateProgressText = true)
        {
            PortableLocalStatus status = await service.GetLocalStatusAsync(snapshot.InstallRoot, CancellationToken.None);
            ApplyLocalPathStatus(snapshot, status, allowWhileBusy, updateProgressText);
            if (status.OldBackupCleanupPending)
            {
                ResetCompatibilityOverviewForPendingCleanup();
            }
            else
            {
                await EnsureCompatibilityOverviewLoadedAsync(
                    snapshot,
                    CancellationToken.None,
                    allowWhileBusy);
            }
            return status;
        }

        private void ResetCompatibilityOverviewForPendingCleanup()
        {
            compatibilityOverview = null;
            compatibilityOverviewPathRevision = -1;
            compatibilityApplyNeeded = false;
            UpdateCompatibilityPresentation();
            ApplyUiState();
        }

        private void ApplyLocalPathStatus(
            OperationSnapshot snapshot,
            PortableLocalStatus status,
            bool allowWhileBusy,
            bool updateProgressText,
            bool updateStatusSummary = true)
        {
            if (!IsLoaded || snapshot.InstallPathRevision != installPathRevision ||
                (operationController.State.Busy && !allowWhileBusy)) return;
            portableVersionAvailable = status.PortableVersion != null;
            previousVersionAvailable = status.PreviousVersionAvailable;
            cachedRollbackVersionAvailable = status.CachedRollbackVersionAvailable;
            installPathInvalid = !string.IsNullOrWhiteSpace(status.Error);
            deploymentCleanupPending =
                status.OldBackupCleanupPending ||
                status.UninstallDirectoryCleanupPending ||
                status.ShellIntegrationCleanupPending;
            statusMatchesCurrentPath = true;
            if (!status.HasInstallRoot)
            {
                portableValueLabel.Text = portableApplicationValueLabel.Text = "未选择";
                if (updateStatusSummary)
                    SetStatusSummary(ResolveStatusSummary(status, storeVersionInstalled, null));
                if (updateProgressText)
                {
                    ShowTaskState(
                        "尚未选择 Codex 便携版目标位置",
                        "未找到有效的成功记录或注册表目录，请点击“选择位置”。",
                        "就绪",
                        TaskProgressMode.Hidden);
                }
                UpdateAvailabilityButtons();
                return;
            }
            bool uninstallCleanupPending =
                status.UninstallDirectoryCleanupPending ||
                status.ShellIntegrationCleanupPending;
            portableValueLabel.Text = uninstallCleanupPending
                ? "已移除（清理中）"
                : string.IsNullOrWhiteSpace(status.Error)
                    ? (status.PortableVersion == null ? "未安装" : status.PortableVersion.ToString())
                    : "路径无效";
            portableApplicationValueLabel.Text = uninstallCleanupPending
                ? "清理待完成"
                : string.IsNullOrWhiteSpace(status.Error)
                    ? (status.PortableVersion == null
                        ? "未安装"
                        : (string.IsNullOrWhiteSpace(status.PortableApplicationVersion)
                            ? "未知"
                            : status.PortableApplicationVersion))
                    : "路径无效";
            UpdateAvailabilityButtons();
            if (!string.IsNullOrWhiteSpace(status.Error))
            {
                if (updateStatusSummary)
                    SetStatusSummary(ResolveStatusSummary(status, storeVersionInstalled, null));
                if (updateProgressText)
                {
                    ShowTaskState(
                        "无法检查当前便携版目标目录",
                        status.Error,
                        "就绪",
                        TaskProgressMode.Hidden);
                }
                return;
            }
            if (uninstallCleanupPending)
            {
                if (updateStatusSummary)
                    SetStatusSummary(ResolveStatusSummary(status, storeVersionInstalled, null));
                if (updateProgressText)
                {
                    ShowTaskState(
                        "Codex 便携版已移除，卸载清理待完成",
                        status.UninstallDirectoryCleanupPending
                            ? "待删除的程序目录仍被占用。关闭占用程序后再次检查，管理器会继续清理。"
                            : "部分快捷方式或注册表项仍待清理，管理器会在启动和后续检查时继续重试。",
                        "就绪",
                        TaskProgressMode.Hidden);
                }
                return;
            }
            Version latest;
            if (updateStatusSummary)
            {
                if (!Version.TryParse(latestValueLabel.Text, out latest)) latest = null;
                SetStatusSummary(ResolveStatusSummary(status, storeVersionInstalled, latest));
            }
            if (status.PortableVersion != null)
            {
                try { InstallLocationResolver.SaveConfirmedInstallRoot(snapshot.InstallRoot); }
                catch (Exception exception) { AppendLog("保存已确认便携版目录失败：" + exception.Message); }
            }
            if (updateProgressText)
            {
                ShowTaskState(
                    status.PortableVersion == null ? "当前目录未检测到 Codex 便携版" : "已检测到 Codex 便携版 " + status.PortableVersion,
                    status.OldBackupCleanupPending
                        ? "当前版本有效且可以启动；更早的回滚备份仍被占用，关闭占用程序后再次检查即可继续清理。"
                        : status.RollbackVersionAvailable
                            ? "当前目标目录存在可用便携版，并检测到 .previous 或缓存中的较早官方版本。"
                            : (status.PortableVersion == null
                                ? "“创建 / 更新 / 修复”会将便携版创建到当前目标目录。"
                                : "当前目标目录存在可用便携版，没有检测到较早版本或回滚备份。"),
                    "就绪",
                    TaskProgressMode.Hidden);
            }
        }



    }
}
