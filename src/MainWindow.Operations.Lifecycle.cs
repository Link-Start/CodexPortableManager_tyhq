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
        private int operationRevision;
        private OperationDisplayKind activeOperationDisplayKind;
        private OperationProgress lastOperationProgress;
        private bool operationEnteredMeasuredDownload;
        private string activeProgressScope;

        private enum TaskProgressMode
        {
            Hidden,
            Indeterminate,
            Determinate
        }

        private enum OperationDisplayKind
        {
            Generic,
            Check,
            Install,
            DownloadPackage,
            Migrate,
            Rollback,
            Uninstall,
            CleanupBackup,
            CompatibilityCheck,
            Compatibility,
            Integration
        }

        private async Task RunOperationAsync(
            Func<OperationSnapshot, CancellationToken, Task> operation,
            bool canCancel = true,
            bool lockInterface = true,
            OperationDisplayKind displayKind = OperationDisplayKind.Generic,
            bool compatibilityChangesOnly = false)
        {
            OperationSnapshot snapshot = compatibilityChangesOnly
                ? CaptureCompatibilityApplySnapshot()
                : CaptureOperationSnapshot();
            OperationContext context;
            if (!operationController.TryBegin(snapshot, canCancel, lockInterface, out context)) return;
            operationRevision++;
            activeOperationDisplayKind = displayKind;
            lastOperationProgress = null;
            operationEnteredMeasuredDownload = false;
            activeProgressScope = null;
            bool canceled = false;
            Exception failure = null;
            operationStopwatch.Restart(); operationStageStopwatch.Restart(); lastProgressLogMessage = null; lastProgressLoggedPercent = -10;
            elapsedTimer.Start();
            PrepareOperationState(displayKind);
            ApplyUiState();
            try { await operation(context.Snapshot, context.Token); }
            catch (OperationCanceledException)
            {
                canceled = true;
                AppendLog(GetCanceledTitle(displayKind) + "。");
            }
            catch (Exception exception)
            {
                failure = exception;
                string failureMessage = FormatOperationFailure(exception);
                AppendLog(GetFailedTitle(displayKind) + "：" + failureMessage);
                MessageBox.Show(this, failureMessage, GetFailedTitle(displayKind), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                bool cancellationWasRequested = operationController.State.CancellationRequested;
                operationController.Complete();
                operationStopwatch.Stop(); operationStageStopwatch.Stop(); elapsedTimer.Stop();
                if (!statusMatchesCurrentPath)
                {
                    await RefreshLocalPathStatusAsync(CaptureOperationSnapshot(), false, false);
                }
                if (canceled) ShowCanceledOperationState(displayKind, operationStopwatch.Elapsed);
                else if (failure != null) ShowFailedOperationState(displayKind, failure, operationStopwatch.Elapsed);
                else if (lastOperationProgress == null || !lastOperationProgress.Percent.HasValue || lastOperationProgress.Percent.Value < 100)
                {
                    ShowTaskState(
                        GetCompletedFallbackTitle(displayKind),
                        GetCompletedFallbackDetail(displayKind),
                        "已完成 · 用时 " + FormatElapsed(operationStopwatch.Elapsed),
                        TaskProgressMode.Hidden);
                    AppendLog(GetCompletedFallbackTitle(displayKind) + " — " + GetCompletedFallbackDetail(displayKind));
                }
                else
                {
                    ShowTaskState(
                        lastOperationProgress.Message,
                        string.IsNullOrWhiteSpace(lastOperationProgress.Detail) ? "当前任务已完成。" : lastOperationProgress.Detail,
                        "100% · 用时 " + FormatElapsed(operationStopwatch.Elapsed),
                        TaskProgressMode.Determinate,
                        100);
                }
                if (!canceled && failure == null && cancellationWasRequested)
                {
                    AppendLog("取消请求未在任务完成前生效；" + GetOperationName(displayKind) + "已正常完成。");
                }
                ApplyUiState();
                activeOperationDisplayKind = OperationDisplayKind.Generic;
            }
        }

        private void PrepareOperationState(OperationDisplayKind displayKind)
        {
            ShowTaskState(
                GetPreparingTitle(displayKind),
                GetPreparingDetail(displayKind),
                "准备中 · 已用 0:00",
                TaskProgressMode.Indeterminate);
        }

        private void ShowCanceledOperationState(OperationDisplayKind displayKind, TimeSpan elapsed)
        {
            ShowTaskState(
                GetCanceledTitle(displayKind),
                GetCanceledDetail(displayKind),
                "已取消 · 用时 " + FormatElapsed(elapsed),
                TaskProgressMode.Hidden);
        }

        private void ShowFailedOperationState(
            OperationDisplayKind displayKind,
            Exception failure,
            TimeSpan elapsed)
        {
            string failureMessage = FormatOperationFailure(failure);
            string detail = string.IsNullOrWhiteSpace(failureMessage)
                ? "当前任务未能完成，详情请查看运行日志。"
                : failureMessage + " 详细过程已写入运行日志。";
            ShowTaskState(
                GetFailedTitle(displayKind),
                detail,
                "失败 · 用时 " + FormatElapsed(elapsed),
                TaskProgressMode.Hidden);
        }

        private static string GetPreparingTitle(OperationDisplayKind displayKind)
        {
            return "正在准备" + GetOperationName(displayKind);
        }

        private static string GetPreparingDetail(OperationDisplayKind displayKind)
        {
            switch (displayKind)
            {
                case OperationDisplayKind.Check:
                    return "正在读取本地安装状态，并连接微软版本与官方桌面版检测服务。";
                case OperationDisplayKind.Install:
                    return "正在确认目标目录、现有版本、缓存和微软官方程序包信息。";
                case OperationDisplayKind.DownloadPackage:
                    return "正在确认保存位置，并连接微软官方程序包服务。";
                case OperationDisplayKind.Migrate:
                    return "将先验证并启动便携版，确认可用后再卸载官方桌面版。";
                case OperationDisplayKind.Rollback:
                    return "正在确认当前版本与 .previous 回滚版本可以安全交换。";
                case OperationDisplayKind.Uninstall:
                    return "正在确认便携版归属以及可安全删除的当前版本和回滚备份。";
                case OperationDisplayKind.CleanupBackup:
                    return "正在确认遗留 .previous 回滚备份的归属和可删除范围。";
                case OperationDisplayKind.CompatibilityCheck:
                    return "正在只读核对当前便携版的来源记录与关键文件摘要。";
                case OperationDisplayKind.Compatibility:
                    return "正在读取当前选择，并验证便携版目录中的可调整文件。";
                case OperationDisplayKind.Integration:
                    return "正在验证便携版目录和用户级快捷方式、协议与文件关联。";
                default:
                    return "正在准备当前任务。";
            }
        }

        private static string GetCanceledTitle(OperationDisplayKind displayKind)
        {
            return GetOperationName(displayKind) + "已取消";
        }

        private static string GetCanceledDetail(OperationDisplayKind displayKind)
        {
            switch (displayKind)
            {
                case OperationDisplayKind.Check:
                    return "版本与安装状态检查已停止，可以重新检查。";
                case OperationDisplayKind.Install:
                    return "创建 / 更新 / 修复已在安全检查点停止；当前便携版状态已重新检查。";
                case OperationDisplayKind.DownloadPackage:
                    return "本次未生成新的官方 MSIX；未验证的临时下载已清理。";
                case OperationDisplayKind.Migrate:
                    return "迁移已在可取消阶段停止，官方桌面版尚未卸载。";
                default:
                    return "当前任务已停止，可以重新开始操作。";
            }
        }

        private static string GetFailedTitle(OperationDisplayKind displayKind)
        {
            return GetOperationName(displayKind) + "失败";
        }

        internal static string FormatOperationFailure(Exception failure)
        {
            if (failure == null) return string.Empty;
            AggregateException aggregate = failure as AggregateException;
            if (aggregate == null) return failure.Message;

            string[] messages = aggregate.Flatten().InnerExceptions
                .Select(exception => exception == null ? null : exception.Message)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return messages.Length == 0 ? aggregate.Message : string.Join("；", messages);
        }

        private static string GetCompletedFallbackTitle(OperationDisplayKind displayKind)
        {
            return GetOperationName(displayKind) + "已完成";
        }

        private static string GetCompletedFallbackDetail(OperationDisplayKind displayKind)
        {
            switch (displayKind)
            {
                case OperationDisplayKind.Check:
                    return "版本与安装状态已刷新。";
                case OperationDisplayKind.Install:
                    return "便携版维护流程已经结束，本地状态已重新检查。";
                case OperationDisplayKind.DownloadPackage:
                    return "官方 MSIX 下载流程已经结束。";
                case OperationDisplayKind.Migrate:
                    return "便携版与官方桌面版状态已重新检查。";
                case OperationDisplayKind.Rollback:
                    return "回滚流程已经结束，本地版本状态已重新检查。";
                case OperationDisplayKind.Uninstall:
                    return "卸载流程已经结束，本地状态已重新检查。";
                case OperationDisplayKind.CleanupBackup:
                    return "遗留回滚备份清理流程已经结束，本地状态已重新检查。";
                case OperationDisplayKind.CompatibilityCheck:
                    return "便携版功能状态已经刷新。";
                case OperationDisplayKind.Compatibility:
                    return "便携版功能调整流程已经结束。";
                case OperationDisplayKind.Integration:
                    return "便携版启动入口修复流程已经结束。";
                default:
                    return "当前任务已经结束。";
            }
        }

        private static string GetOperationName(OperationDisplayKind displayKind)
        {
            switch (displayKind)
            {
                case OperationDisplayKind.Check: return "版本检查";
                case OperationDisplayKind.Install: return "创建 / 更新 / 修复";
                case OperationDisplayKind.DownloadPackage: return "官方 MSIX 下载";
                case OperationDisplayKind.Migrate: return "迁移";
                case OperationDisplayKind.Rollback: return "回滚";
                case OperationDisplayKind.Uninstall: return "卸载";
                case OperationDisplayKind.CleanupBackup: return "回滚备份清理";
                case OperationDisplayKind.CompatibilityCheck: return "功能状态检查";
                case OperationDisplayKind.Compatibility: return "功能调整";
                case OperationDisplayKind.Integration: return "启动入口修复";
                default: return "操作";
            }
        }

        private IProgress<OperationProgress> CreateProgress()
        {
            int revision = operationRevision;
            return new DirectProgress<OperationProgress>(value =>
            {
                if (Dispatcher.CheckAccess())
                {
                    ApplyOperationProgress(value, revision);
                    return;
                }

                try
                {
                    if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    {
                        Dispatcher.Invoke(new Action(() => ApplyOperationProgress(value, revision)));
                    }
                }
                catch (TaskCanceledException) { }
                catch (InvalidOperationException) { }
            });
        }

        private void ApplyOperationProgress(OperationProgress value, int revision)
        {
            if (value == null || revision != operationRevision || !operationController.State.Busy || !IsLoaded) return;
            bool stageChanged = lastOperationProgress == null ||
                !string.Equals(lastOperationProgress.Message, value.Message, StringComparison.Ordinal);
            if (stageChanged) operationStageStopwatch.Restart();
            lastOperationProgress = value;
            operationController.SetCancellationAvailability(value.CanCancel);
            if (operationController.State.CancellationRequested)
            {
                operationController.SetPauseAvailability(false);
                ShowTaskState(
                    "正在取消" + GetOperationName(activeOperationDisplayKind),
                    GetCancellationRequestedDetail(activeOperationDisplayKind),
                    CreateProgressMeta(),
                    TaskProgressMode.Indeterminate);
            }
            else
            {
                operationController.SetPauseAvailability(value.CanPause);
                int? displayPercent = ResolveVisibleProgressPercent(value, operationEnteredMeasuredDownload);
                if (value.CanPause) operationEnteredMeasuredDownload = true;
                activeProgressScope = value.CanPause && displayPercent.HasValue ? "下载" : null;
                bool paused = value.CanPause && operationController.State.IsPaused;
                string title = paused ? "下载已暂停" : value.Message;
                string detail = paused
                    ? (string.IsNullOrWhiteSpace(value.Detail) ? "当前下载断点已保留。" : value.Detail + " · 已暂停")
                    : (string.IsNullOrWhiteSpace(value.Detail) ? "正在处理，请勿关闭管理器。" : value.Detail);
                ShowTaskState(
                    title,
                    detail,
                    CreateProgressMeta(displayPercent),
                    displayPercent.HasValue ? TaskProgressMode.Determinate : TaskProgressMode.Indeterminate,
                    displayPercent.GetValueOrDefault());
            }
            bool changed = !string.Equals(lastProgressLogMessage, value.Message, StringComparison.Ordinal);
            bool milestone = value.Percent.HasValue && (value.Percent.Value == 100 || value.Percent.Value >= lastProgressLoggedPercent + 10);
            if (changed || milestone)
            {
                string entry = value.Message;
                if (!string.IsNullOrWhiteSpace(value.Detail)) entry += " — " + value.Detail;
                int? loggedPercent = ResolveVisibleProgressPercent(value, operationEnteredMeasuredDownload);
                if (loggedPercent.HasValue) entry += "（" + loggedPercent.Value + "%）";
                AppendLog(entry); lastProgressLogMessage = value.Message;
                if (value.Percent.HasValue) lastProgressLoggedPercent = value.Percent.Value;
            }
            ApplyUiState();
        }

        private void UpdateProgressMeta(int? reportedPercent = null)
        {
            progressMetaLabel.Text = CreateProgressMeta(reportedPercent);
        }

        private string CreateProgressMeta(int? reportedPercent = null)
        {
            if (operationController.State.CancellationRequested)
            {
                return "正在取消 · 总计 " + FormatElapsed(operationStopwatch.Elapsed);
            }
            int percent = reportedPercent ?? (progressBar.IsIndeterminate ? -1 : (int)progressBar.Value);
            string text = percent >= 0
                ? (string.IsNullOrWhiteSpace(activeProgressScope) ? percent + "%" : activeProgressScope + " " + percent + "%")
                : "处理中";
            if (operationStopwatch.IsRunning || operationStopwatch.Elapsed > TimeSpan.Zero)
            {
                text += " · 阶段 " + FormatElapsed(operationStageStopwatch.Elapsed) +
                    " · 总计 " + FormatElapsed(operationStopwatch.Elapsed);
            }
            return text;
        }

        internal static int? ResolveVisibleProgressPercent(
            OperationProgress value,
            bool measuredDownloadStarted)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.Percent.HasValue && value.Percent.Value >= 100) return 100;
            if (value.CanPause) return value.DisplayPercent;
            return measuredDownloadStarted ? null : value.DisplayPercent;
        }

        private void ShowTaskState(
            string title,
            string detail,
            string meta,
            TaskProgressMode progressMode,
            int percent = 0)
        {
            progressLabel.Text = string.IsNullOrWhiteSpace(title) ? "就绪" : title;
            progressDetailLabel.Text = string.IsNullOrWhiteSpace(detail) ? "等待操作" : detail;
            progressMetaLabel.Text = string.IsNullOrWhiteSpace(meta) ? "就绪" : meta;
            progressBar.Visibility = progressMode == TaskProgressMode.Hidden
                ? Visibility.Collapsed
                : Visibility.Visible;
            progressBar.IsIndeterminate = progressMode == TaskProgressMode.Indeterminate;
            progressBar.Value = progressMode == TaskProgressMode.Determinate
                ? Math.Max(0, Math.Min(100, percent))
                : 0;
        }

        private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds) : string.Format("{0}:{1:00}", (int)elapsed.TotalMinutes, elapsed.Seconds);

        private void ApplyUiState()
        {
            UpdateCompatibilityPresentation();
            OperationUiState operation = operationController.State;
            CompatibilitySwitchFacts compatibilityFacts =
                CompatibilityStatusReader.ResolveSwitchFacts(compatibilityOverview);
            UiState state = UiState.Create(new UiStateInput(
                operation,
                statusMatchesCurrentPath,
                portableVersionAvailable,
                previousVersionAvailable,
                storeVersionInstalled,
                !string.IsNullOrWhiteSpace(installPathTextBox.Text) && !installPathInvalid,
                deploymentCleanupPending,
                uninstallBackgroundCleanupActive || postDeploymentCleanupActive,
                compatibilityOverviewPathRevision == installPathRevision,
                compatibilityFacts,
                compatibilityApplyNeeded,
                cachedRollbackVersionAvailable));
            installPathTextBox.IsEnabled = browseButton.IsEnabled =
                state.InputEnabled;
            sandboxCompatibilityCheckBox.IsEnabled = state.SandboxCompatibilityEnabled;
            unlockModelCatalogCheckBox.IsEnabled = state.UnlockModelCatalogEnabled;
            supplementChineseUiCheckBox.IsEnabled = state.SupplementChineseUiEnabled;
            englishTechnicalParametersCheckBox.IsEnabled = state.EnglishTechnicalParametersEnabled;
            checkButton.IsEnabled = state.CheckEnabled;
            downloadPackageButton.IsEnabled = state.DownloadEnabled;
            installButton.IsEnabled = state.InstallEnabled;
            openFolderButton.IsEnabled = state.OpenFolderEnabled;
            launchButton.IsEnabled = state.LaunchEnabled;
            rollbackButton.IsEnabled = state.RollbackEnabled;
            uninstallPortableButton.IsEnabled = state.UninstallEnabled;
            if (portableVersionAvailable && previousVersionAvailable)
            {
                uninstallPortableButton.Content = "卸载便携版及回滚备份";
                uninstallDescriptionLabel.Text = "删除当前版本和 .previous，保留用户资料与管理器数据";
            }
            else if (portableVersionAvailable)
            {
                uninstallPortableButton.Content = "卸载当前便携版";
                uninstallDescriptionLabel.Text = "删除当前版本，保留用户资料与管理器数据";
            }
            else if (previousVersionAvailable)
            {
                uninstallPortableButton.Content = "删除遗留回滚备份";
                uninstallDescriptionLabel.Text = "删除仅剩的 .previous，保留用户资料与管理器数据";
            }
            else
            {
                uninstallPortableButton.Content = "卸载当前便携版";
                uninstallDescriptionLabel.Text = "删除当前版本和 .previous，保留用户资料与管理器数据";
            }
            applyCompatibilityButton.IsEnabled = state.ApplyCompatibilityEnabled;
            checkCompatibilityStatusButton.IsEnabled = state.CheckCompatibilityStatusEnabled;
            repairIntegrationButton.IsEnabled = state.RepairIntegrationEnabled;
            migrateButton.IsEnabled = state.MigrateEnabled;
            pauseButton.IsEnabled = state.PauseEnabled;
            pauseButton.Visibility = state.PauseEnabled ? Visibility.Visible : Visibility.Collapsed;
            pauseButton.Content = ResolvePauseButtonText(state.PauseActive, lastOperationProgress);
            cancelButton.IsEnabled = state.CancelEnabled;
            cancelButton.Visibility = cancelButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (!operation.Busy) progressBar.IsIndeterminate = false;
        }

        internal static string ResolvePauseButtonText(bool pauseActive, OperationProgress progress)
        {
            if (pauseActive) return "继续下载";
            return progress != null && progress.IsNetworkWaiting
                ? "立即重试"
                : "暂停下载";
        }

        private void PauseButton_Click(object sender, RoutedEventArgs args)
        {
            if (lastOperationProgress != null &&
                lastOperationProgress.IsNetworkWaiting &&
                operationController.RequestDownloadRetry())
            {
                ShowTaskState(
                    "正在重新连接",
                    "已中断当前网络探测，正在立即重新连接微软 CDN。",
                    CreateProgressMeta(),
                    TaskProgressMode.Determinate,
                    (int)progressBar.Value);
                AppendLog("已请求立即重试下载，正在重新连接微软 CDN。");
                ApplyUiState();
                return;
            }
            bool paused = operationController.TogglePause();
            if (paused)
            {
                ShowTaskState(
                    "下载已暂停",
                    "当前进度已保留；点击“继续下载”后将从断点继续。",
                    CreateProgressMeta(),
                    TaskProgressMode.Determinate,
                    (int)progressBar.Value);
                AppendLog("下载已暂停，当前临时文件和断点保持不变。");
            }
            else if (operationController.State.CanPause)
            {
                ShowTaskState(
                    "正在继续下载",
                    "正在从已保留的下载断点继续。",
                    CreateProgressMeta(),
                    TaskProgressMode.Determinate,
                    (int)progressBar.Value);
                AppendLog("下载已继续。");
            }
            ApplyUiState();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs args)
        {
            if (!operationController.RequestCancellation()) return;

            ShowTaskState(
                "正在取消" + GetOperationName(activeOperationDisplayKind),
                GetCancellationRequestedDetail(activeOperationDisplayKind),
                "正在取消 · 已用 " + FormatElapsed(operationStopwatch.Elapsed),
                TaskProgressMode.Indeterminate);
            AppendLog("已请求取消" + GetOperationName(activeOperationDisplayKind) + "；正在等待安全停止点。");
            ApplyUiState();
        }

        private static string GetCancellationRequestedDetail(OperationDisplayKind displayKind)
        {
            switch (displayKind)
            {
                case OperationDisplayKind.Check:
                    return "正在停止网络请求；已经完成的本地检测结果会保留。";
                case OperationDisplayKind.Install:
                    return "正在等待安全检查点；若版本切换已经开始，管理器会先完成或恢复目录事务。";
                case OperationDisplayKind.DownloadPackage:
                    return "正在停止下载，并清理本次任务尚未验证的临时文件。";
                case OperationDisplayKind.Migrate:
                    return "正在可取消阶段停止迁移；官方桌面版不会在取消后被卸载。";
                default:
                    return "取消请求已经提交，正在等待当前任务到达安全停止点。";
            }
        }



    }
}
