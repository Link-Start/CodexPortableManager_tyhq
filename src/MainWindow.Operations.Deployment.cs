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
        private async Task InstallOrUpdateAsync(
            OperationSnapshot snapshot,
            CancellationToken token,
            LegacyAdoptionApproval adoptionApproval)
        {
            InvalidatePathStatus();
            IProgress<OperationProgress> progress = CreateProgress();
            await service.InstallOrUpdateAsync(
                snapshot.InstallRoot,
                true,
                progress,
                operationController.PauseToken,
                token,
                true,
                adoptionApproval);
            PortableLocalStatus status = await RefreshLocalPathStatusAsync(snapshot, true, false);
            if (!string.IsNullOrWhiteSpace(status.Error) || status.PortableVersion == null)
            {
                progress.Report(new OperationProgress(
                    "便携版部署已完成，但状态复核未通过",
                    100,
                    string.IsNullOrWhiteSpace(status.Error)
                        ? "部署事务已经结束，但当前目录未能识别出有效便携版；请重新检查版本并查看日志。"
                        : "部署事务已经结束，但重新读取目标目录时失败：" + status.Error));
            }
        }

        private async Task InstallButton_Click()
        {
            if (!TryResolveInstallDestination()) return;
            LegacyAdoptionApproval adoptionApproval;
            if (!TryApproveLegacyAdoption(CaptureOperationSnapshot().InstallRoot, out adoptionApproval)) return;
            await RunOperationAsync(
                (snapshot, token) => InstallOrUpdateAsync(snapshot, token, adoptionApproval),
                true,
                true,
                OperationDisplayKind.Install);
        }

        private async Task DownloadPackageButton_Click()
        {
            using (WinForms.SaveFileDialog dialog = new WinForms.SaveFileDialog())
            {
                dialog.Title = "保存微软官方 Codex 安装包";
                dialog.Filter = "MSIX 安装包 (*.msix)|*.msix|所有文件 (*.*)|*.*";
                dialog.DefaultExt = "msix";
                dialog.AddExtension = true;
                dialog.OverwritePrompt = true;
                dialog.CheckPathExists = true;
                dialog.FileName = "OpenAI.Codex.msix";
                if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;

                string destination = dialog.FileName;
                string downloadedPath = null;
                await RunOperationAsync(async (snapshot, token) =>
                {
                    downloadedPath = await service.DownloadOfficialPackageAsync(
                        destination,
                        CreateProgress(),
                        operationController.PauseToken,
                        token);
                }, true, true, OperationDisplayKind.DownloadPackage);

                if (string.IsNullOrWhiteSpace(downloadedPath) || !File.Exists(downloadedPath)) return;
                if (MessageBox.Show(
                    this,
                    "官方 MSIX 已下载并通过完整性、签名和包身份校验。\n\n安装后将成为由 Windows 管理的官方桌面版，程序文件通常位于 WindowsApps；本工具的便携版兼容设置不适用于该版本，也不会修改其包内文件。\n\n是否立即打开 Windows 应用安装程序？",
                    "官方安装包已准备好",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information) != MessageBoxResult.Yes)
                {
                    AppendLog("用户选择暂不打开 Windows 应用安装程序：" + downloadedPath);
                    ShowTaskState(
                        "官方 MSIX 已保存",
                        "已完成验证并保存到：" + downloadedPath,
                        "100% · 用时 " + FormatElapsed(operationStopwatch.Elapsed),
                        TaskProgressMode.Determinate,
                        100);
                    return;
                }

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = downloadedPath,
                        UseShellExecute = true
                    });
                    AppendLog("已打开 Windows 应用安装程序：" + downloadedPath);
                    ShowTaskState(
                        "官方 MSIX 已下载并打开系统安装器",
                        "请在 Windows 应用安装程序中确认是否安装；管理器没有自动执行安装。",
                        "100% · 用时 " + FormatElapsed(operationStopwatch.Elapsed),
                        TaskProgressMode.Determinate,
                        100);
                }
                catch (Exception exception)
                {
                    AppendLog("无法打开 Windows 应用安装程序：" + exception.Message);
                    ShowTaskState(
                        "官方 MSIX 已保存，但未能打开系统安装器",
                        "安装包仍保存在：" + downloadedPath + "。" + exception.Message,
                        "100% · 用时 " + FormatElapsed(operationStopwatch.Elapsed),
                        TaskProgressMode.Determinate,
                        100);
                    MessageBox.Show(this, "无法打开 Windows 应用安装程序：" + exception.Message + "\n\n安装包仍保存在：\n" + downloadedPath, "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private async Task RollbackAsync(
            OperationSnapshot snapshot,
            CancellationToken token,
            LegacyAdoptionApproval adoptionApproval)
        {
            InvalidatePathStatus();
            IProgress<OperationProgress> progress = CreateProgress();
            progress.Report(new OperationProgress(
                "正在回滚 Codex 便携版",
                15,
                "正在选择较早版本；优先使用较低的 .previous，否则验证缓存中的官方版本。"));
            DeploymentResult result = await service.RollbackAvailableAsync(
                snapshot.InstallRoot,
                progress,
                operationController.PauseToken,
                token,
                true,
                adoptionApproval);
            PortableLocalStatus status = await RefreshLocalPathStatusAsync(snapshot, true, false);
            progress.Report(CreateRollbackCompletion(status.PortableVersion, result));
        }

        internal static OperationProgress CreateRollbackCompletion(
            Version restoredVersion,
            DeploymentResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            string versionText = restoredVersion == null ? null : restoredVersion.ToString();
            string message = versionText == null
                ? "Codex 便携版已完成回滚"
                : "已回滚到 Codex " + versionText;
            string detail = versionText == null
                ? "上一版本已恢复；回滚前版本已保留在 .previous，可再次回滚切换。"
                : "版本 " + versionText + " 已恢复；回滚前版本已保留在 .previous，可再次回滚切换。";
            if (!result.IntegrationSucceeded)
            {
                detail += " 部分系统集成未完成，可使用“修复启动入口”重试；详情请查看日志。";
            }
            return new OperationProgress(message, 100, detail);
        }

        private async Task RollbackButton_Click()
        {
            LegacyAdoptionApproval adoptionApproval;
            if (!TryApproveLegacyAdoption(CaptureOperationSnapshot().InstallRoot, out adoptionApproval)) return;
            await RunOperationAsync(
                (snapshot, token) => RollbackAsync(snapshot, token, adoptionApproval),
                false,
                true,
                OperationDisplayKind.Rollback);
        }

        private async Task MigrateAsync()
        {
            if (!TryResolveInstallDestination()) return;
            if (MessageBox.Show(this, "该操作会先确保便携版可用，再卸载当前用户的官方桌面版（Microsoft Store / MSIX）。继续吗？", "迁移到 Codex 便携版", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            LegacyAdoptionApproval adoptionApproval;
            if (!TryApproveLegacyAdoption(CaptureOperationSnapshot().InstallRoot, out adoptionApproval)) return;
            await RunOperationAsync(async (snapshot, token) =>
            {
                InvalidatePathStatus();
                DeploymentResult deploymentResult = await service.InstallOrUpdateAsync(
                    snapshot.InstallRoot,
                    false,
                    CreateProgress(),
                    operationController.PauseToken,
                    token,
                    true,
                    adoptionApproval);
                service.StartPortable(snapshot.InstallRoot);
                if (!operationController.TryEnterNonCancelablePhase())
                {
                    token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("迁移操作无法进入不可取消的卸载阶段。");
                }
                ApplyUiState();
                CreateProgress().Report(new OperationProgress("便携版已验证并发起启动，正在卸载官方桌面版", null, "迁移已进入不可取消阶段，请等待 Windows 完成包卸载。"));
                try
                {
                    await service.UninstallStorePackageAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    AppendLog("便携版已完成，但官方桌面版卸载失败：" + FormatOperationFailure(exception));
                    StorePackageState refreshedStoreState = await RefreshStorePackageStateAfterMigrationAsync();
                    await RefreshLocalPathStatusAsync(snapshot, true, false);
                    OperationProgress partialCompletion = CreateMigrationStoreUninstallFailure(
                        deploymentResult,
                        exception,
                        refreshedStoreState);
                    CreateProgress().Report(partialCompletion);
                    MessageBox.Show(
                        this,
                        partialCompletion.Detail,
                        partialCompletion.Message,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                ApplyStorePackagePresentation(StorePackageState.NotInstalled, false);
                await RefreshLocalPathStatusAsync(snapshot, true, false);
                CreateProgress().Report(CreateMigrationCompletion(deploymentResult));
            }, true, true, OperationDisplayKind.Migrate);
        }

        private async Task<StorePackageState> RefreshStorePackageStateAfterMigrationAsync()
        {
            StorePackageState state = StorePackageState.Unknown;
            try
            {
                state = await service.IsStorePackageInstalledAsync(CancellationToken.None)
                    ? StorePackageState.Installed
                    : StorePackageState.NotInstalled;
            }
            catch (Exception exception)
            {
                AppendLog("官方桌面版卸载失败后的状态复查未完成：" + exception.Message);
            }
            ApplyStorePackagePresentation(state, true);
            UpdateAvailabilityButtons();
            return state;
        }

        internal static OperationProgress CreateMigrationCompletion(DeploymentResult result)
        {
            return DeploymentCompletion.ForMigration(result);
        }

        internal static OperationProgress CreateMigrationStoreUninstallFailure(
            DeploymentResult result,
            Exception failure,
            StorePackageState refreshedStoreState)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (failure == null) throw new ArgumentNullException(nameof(failure));

            List<string> details = new List<string>
            {
                "便携版部署已完成并已发起启动"
            };
            if (refreshedStoreState == StorePackageState.Installed)
            {
                details.Add("复查确认官方桌面版仍然存在");
            }
            else if (refreshedStoreState == StorePackageState.NotInstalled)
            {
                details.Add("复查未检测到官方桌面版，但 Windows 卸载调用返回了错误");
            }
            else
            {
                details.Add("官方桌面版当前状态未能确认，已保留迁移入口以便重试");
            }
            if (!result.IntegrationSucceeded)
            {
                details.Add("部分系统集成未完成，可使用“修复启动入口”重试");
            }
            if (!result.CompatibilitySucceeded)
            {
                details.Add("部分兼容设置等待适配，可在功能调整区查看状态");
            }
            details.Add("Windows 卸载错误：" + FormatOperationFailure(failure));
            details.Add("便携版不会因官方版卸载失败而回退");
            return new OperationProgress(
                "便携版已完成，官方版卸载失败",
                100,
                string.Join("；", details.ToArray()) + "。");
        }

        private async Task UninstallPortableAsync()
        {
            bool hadPortableVersion = portableVersionAvailable;
            bool hadPreviousVersion = previousVersionAvailable;
            Task<int> backgroundCleanupTask = null;
            OperationSnapshot cleanupSnapshot = null;
            string confirmationMessage = hadPortableVersion
                ? "将删除当前便携版 Codex 及其回滚备份，并清理快捷方式和系统集成。\n\n管理器、安装包缓存和用户资料会保留。继续吗？"
                : "当前目录只检测到遗留的 .previous 回滚备份。将删除该备份，并保留管理器、安装包缓存和用户资料。继续吗？";
            string confirmationTitle = hadPortableVersion ? "卸载 Codex 便携版" : "删除遗留回滚备份";
            if (MessageBox.Show(this, confirmationMessage, confirmationTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            LegacyAdoptionApproval adoptionApproval;
            if (!TryApproveLegacyAdoption(CaptureOperationSnapshot().InstallRoot, out adoptionApproval)) return;
            await RunOperationAsync(async (snapshot, token) =>
            {
                InvalidatePathStatus();
                CreateProgress().Report(hadPortableVersion
                    ? new OperationProgress("正在卸载 Codex 便携版", 20, "将关闭 Codex，删除当前版本和回滚备份。")
                    : new OperationProgress("正在删除遗留回滚备份", 20, "将删除当前目标目录旁仅剩的 .previous 回滚备份。"));
                UninstallResult result = await Task.Run(
                    () => service.DetachPortableForUninstall(
                        snapshot.InstallRoot,
                        adoptionApproval));
                if (result.DirectoryCleanupPending)
                {
                    cleanupSnapshot = snapshot;
                    uninstallBackgroundCleanupActive = true;
                    backgroundCleanupTask = service.StartUninstallCleanupAsync(
                        snapshot.InstallRoot);
                }
                foreach (string warning in result.IntegrationWarnings)
                {
                    AppendLog("系统集成清理警告：" + warning);
                }
                if (!result.CleanupPending)
                {
                    PortableStorage.ClearRecordedInstallRootIfMatches(snapshot.InstallRoot);
                    installPathTextBox.Text = string.Empty;
                }
                portableValueLabel.Text = result.CleanupPending ? "已移除（清理中）" : "未安装";
                portableApplicationValueLabel.Text = result.CleanupPending ? "清理待完成" : "未安装";
                portableVersionAvailable = previousVersionAvailable = false;
                cachedRollbackVersionAvailable = false;
                deploymentCleanupPending = result.CleanupPending;
                // 操作期间路径输入被锁定；若这里清空文本，revision 变化只来自本次程序化同步。
                statusMatchesCurrentPath = true;
                SetStatusSummary(
                    result.DirectoryCleanupPending
                        ? "已卸载，后台清理中"
                        : result.CleanupPending
                            ? "卸载清理待完成"
                            : "尚未安装",
                    result.CleanupPending ? "WarningBrush" : "MutedBrush");
                UpdateAvailabilityButtons();
                string removedItems = hadPortableVersion
                    ? "当前版本" + (hadPreviousVersion ? "和 .previous 回滚备份" : string.Empty)
                    : ".previous 回滚备份";
                List<string> details = new List<string>
                {
                    removedItems + "已从活动槽移除",
                    "用户资料、管理器缓存和日志均已保留"
                };
                if (result.DirectoryCleanupPending)
                {
                    details.Add("程序文件已移入隔离目录，正在独立后台清理，不影响关闭管理器");
                }
                if (result.IntegrationCleanupPending)
                {
                    details.Add("部分快捷方式或注册表项待清理，下次启动将自动重试");
                }
                CreateProgress().Report(new OperationProgress(
                    result.DirectoryCleanupPending
                        ? "Codex 便携版已卸载，后台清理中"
                        : result.CleanupPending
                        ? "Codex 便携版已移除，清理待完成"
                        : (hadPortableVersion ? "Codex 便携版已卸载" : "遗留回滚备份已删除"),
                    100,
                    string.Join("；", details.ToArray()) + "。"));
            }, false, true, hadPortableVersion ? OperationDisplayKind.Uninstall : OperationDisplayKind.CleanupBackup);

            if (backgroundCleanupTask != null && cleanupSnapshot != null)
            {
                await ObserveUninstallCleanupAsync(
                    cleanupSnapshot,
                    backgroundCleanupTask);
            }
        }

        private async Task ObserveUninstallCleanupAsync(
            OperationSnapshot snapshot,
            Task<int> cleanupTask)
        {
            int exitCode;
            try
            {
                exitCode = await cleanupTask;
            }
            catch (Exception exception)
            {
                uninstallBackgroundCleanupActive = false;
                if (IsLoaded) ApplyUiState();
                AppendLog("无法启动或监视卸载后台清理：" + exception.Message);
                return;
            }
            uninstallBackgroundCleanupActive = false;
            if (IsLoaded) ApplyUiState();
            if (exitCode != 0)
            {
                return;
            }

            PortableStorage.ClearRecordedInstallRootIfMatches(
                snapshot.InstallRoot);
            while (IsLoaded && operationController.State.Busy)
            {
                await Task.Delay(100);
            }
            if (!IsLoaded ||
                snapshot.InstallPathRevision != installPathRevision ||
                !PathsEqual(
                    Environment.ExpandEnvironmentVariables(installPathTextBox.Text.Trim()),
                    snapshot.InstallRoot))
            {
                return;
            }

            deploymentCleanupPending = false;
            installPathTextBox.Text = string.Empty;
            AppendLog("Codex 便携版卸载后台清理完成，旧目标目录记录已清除。");
        }



    }
}
