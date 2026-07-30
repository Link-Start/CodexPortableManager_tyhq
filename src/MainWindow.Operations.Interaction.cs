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
        private void UpdateAvailabilityButtons()
        {
            ApplyUiState();
        }

        private void InvalidatePathStatus()
        {
            statusMatchesCurrentPath = portableVersionAvailable = previousVersionAvailable = false;
            cachedRollbackVersionAvailable = false;
            installPathInvalid = false;
            deploymentCleanupPending = false;
            compatibilityOverview = null;
            compatibilityOverviewPathRevision = -1;
            UpdateCompatibilityPresentation();
            UpdateAvailabilityButtons();
        }

        private void InstallPathTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs args)
        {
            installPathRevision++;
            ResetCompatibilitySwitchesForUnavailableInstallation();
            InvalidatePathStatus();
            portableValueLabel.Text = portableApplicationValueLabel.Text = "检查中...";
            SetStatusSummary("检查目录中", "MutedBrush");
            pathStatusTimer.Stop();
            if (!operationController.State.Busy) pathStatusTimer.Start();
        }

        private void SettingsCheckBox_Changed(object sender, RoutedEventArgs args)
        {
            if (!IsLoaded || updatingCompatibilitySwitches) return;
            compatibilityDraftDirty = true;
            UpdateCompatibilityPresentation();
            ApplyUiState();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs args)
        {
            OperationSnapshot snapshot = CaptureOperationSnapshot();
            using (WinForms.FolderBrowserDialog dialog = new WinForms.FolderBrowserDialog())
            {
                dialog.Description = "选择 Codex 便携版的存放位置；若该位置非空且不是现有便携版，管理器会自动选择独立的 Codex 或 Codex-N 目录。";
                string initialPath = ResolveFolderBrowserInitialPath(snapshot.InstallRoot);
                if (!string.IsNullOrEmpty(initialPath)) dialog.SelectedPath = initialPath;
                if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;
                try
                {
                    string destination = InstallLocationResolver.ResolveInstallDestination(dialog.SelectedPath);
                    installPathTextBox.Text = destination;
                    installPathTextBox.CaretIndex = installPathTextBox.Text.Length;
                    if (!PathsEqual(dialog.SelectedPath, destination))
                    {
                        AppendLog("所选位置不适合直接写入，已使用独立的便携版目标目录：" + destination);
                    }
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this, exception.Message, "目录不可用", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private bool TryResolveInstallDestination()
        {
            OperationSnapshot snapshot = CaptureOperationSnapshot();
            try
            {
                string destination = InstallLocationResolver.ResolveInstallDestination(snapshot.InstallRoot);
                if (!PathsEqual(snapshot.InstallRoot, destination))
                {
                    SetResolvedInstallPathPreservingCompatibility(destination);
                    AppendLog("当前目标位置不适合直接写入，最终便携版目录已调整为：" + destination);
                }
                return true;
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "目录不可用", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        internal void SetResolvedInstallPathPreservingCompatibility(string destination)
        {
            if (string.IsNullOrWhiteSpace(destination))
            {
                throw new ArgumentException("最终安装目录不能为空。", nameof(destination));
            }

            CompatibilityOptions compatibility = CaptureOperationSnapshot().Compatibility;
            bool dirty = compatibilityDraftDirty;
            installPathTextBox.Text = destination;
            installPathTextBox.CaretIndex = installPathTextBox.Text.Length;
            RestoreCompatibilitySelection(compatibility, dirty);
        }

        private void RestoreCompatibilitySelection(
            CompatibilityOptions compatibility,
            bool dirty)
        {
            if (compatibility == null) throw new ArgumentNullException(nameof(compatibility));
            updatingCompatibilitySwitches = true;
            try
            {
                sandboxCompatibilityCheckBox.IsChecked = compatibility.SandboxCompatibilityEnabled;
                unlockModelCatalogCheckBox.IsChecked = compatibility.UnlockModelCatalogEnabled;
                supplementChineseUiCheckBox.IsChecked = compatibility.SupplementChineseUiEnabled;
                englishTechnicalParametersCheckBox.IsChecked = compatibility.EnglishTechnicalParametersEnabled;
            }
            finally
            {
                updatingCompatibilitySwitches = false;
            }
            compatibilityDraftDirty = dirty;
            UpdateCompatibilityPresentation();
            ApplyUiState();
        }

        private static bool PathsEqual(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(Environment.ExpandEnvironmentVariables(first.Trim()))
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(Environment.ExpandEnvironmentVariables(second.Trim()))
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveFolderBrowserInitialPath(string installRoot)
        {
            if (string.IsNullOrWhiteSpace(installRoot)) return string.Empty;
            try
            {
                string candidate = Path.GetFullPath(installRoot);
                while (!string.IsNullOrWhiteSpace(candidate))
                {
                    if (Directory.Exists(candidate)) return candidate;
                    candidate = Path.GetDirectoryName(candidate);
                }
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
            catch (System.Security.SecurityException) { }
            return string.Empty;
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs args)
        {
            OperationSnapshot snapshot = CaptureOperationSnapshot();
            try
            {
                service.StartPortable(snapshot.InstallRoot);
                AppendLog("已发起 Codex 便携版启动：" + snapshot.InstallRoot);
                operationStopwatch.Reset();
                ShowTaskState(
                    "已发起 Codex 便携版启动",
                    "启动请求已提交到：" + snapshot.InstallRoot,
                    "已完成",
                    TaskProgressMode.Hidden);
            }
            catch (Exception exception)
            {
                AppendLog("Codex 便携版启动失败：" + exception.Message);
                operationStopwatch.Reset();
                ShowTaskState(
                    "Codex 便携版启动失败",
                    exception.Message,
                    "失败",
                    TaskProgressMode.Hidden);
                MessageBox.Show(this, exception.Message, "无法启动", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs args)
        {
            OperationSnapshot snapshot = CaptureOperationSnapshot();
            try
            {
                service.OpenInstallFolder(snapshot.InstallRoot);
                AppendLog("已打开 Codex 便携版目录：" + snapshot.InstallRoot);
                operationStopwatch.Reset();
                ShowTaskState(
                    "已打开 Codex 便携版目录",
                    snapshot.InstallRoot,
                    "已完成",
                    TaskProgressMode.Hidden);
            }
            catch (Exception exception)
            {
                AppendLog("打开 Codex 便携版目录失败：" + exception.Message);
                operationStopwatch.Reset();
                ShowTaskState(
                    "无法打开 Codex 便携版目录",
                    exception.Message,
                    "失败",
                    TaskProgressMode.Hidden);
                MessageBox.Show(this, exception.Message, "无法打开目录", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenLogButton_Click(object sender, RoutedEventArgs args)
        {
            try
            {
                CodexPortableService.OpenFolderInExplorer(PortableStorage.LogsRoot);
                AppendLog("已打开管理器日志目录：" + PortableStorage.LogsRoot);
            }
            catch (Exception exception)
            {
                AppendLog("打开管理器日志目录失败：" + exception.Message);
                MessageBox.Show(this, exception.Message, "无法打开日志目录", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AppendLog(string message)
        {
            string uiLine = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " +
                message + Environment.NewLine;
            string fileLine = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                "] " + message + Environment.NewLine;
            lock (sessionLogSync)
            {
                try { File.AppendAllText(sessionLogPath, fileLine, System.Text.Encoding.UTF8); }
                catch { }
            }

            bool scheduleFlush = false;
            lock (pendingLogSync)
            {
                pendingUiLog.Append(uiLine);
                if (!uiLogFlushPending)
                {
                    uiLogFlushPending = true;
                    scheduleFlush = true;
                }
            }
            if (!scheduleFlush || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(FlushPendingUiLog));
            }
            catch (TaskCanceledException)
            {
                ResetPendingUiLogSchedule();
            }
            catch (InvalidOperationException)
            {
                ResetPendingUiLogSchedule();
            }
        }

        private void ResetPendingUiLogSchedule()
        {
            lock (pendingLogSync) uiLogFlushPending = false;
        }

        private void FlushPendingUiLog()
        {
            string text;
            lock (pendingLogSync)
            {
                text = pendingUiLog.ToString();
                pendingUiLog.Clear();
                uiLogFlushPending = false;
            }
            if (logBox == null || text.Length == 0) return;

            logBox.AppendText(text);
            if (logBox.Text.Length > MaximumUiLogCharacters)
            {
                int retainFrom = logBox.Text.Length - RetainedUiLogCharacters;
                int lineStart = logBox.Text.IndexOf(Environment.NewLine, retainFrom, StringComparison.Ordinal);
                logBox.Text = lineStart >= 0
                    ? logBox.Text.Substring(lineStart + Environment.NewLine.Length)
                    : logBox.Text.Substring(retainFrom);
                logBox.CaretIndex = logBox.Text.Length;
            }
            logBox.ScrollToEnd();
        }

        private bool TryApproveLegacyAdoption(
            string installRoot,
            out LegacyAdoptionApproval adoptionApproval)
        {
            adoptionApproval = null;
            if (string.IsNullOrWhiteSpace(installRoot)) return true;
            try
            {
                if (!service.RequiresLegacyAdoption(installRoot)) return true;
            }
            catch
            {
                return true;
            }

            bool approved = MessageBox.Show(
                this,
                "当前目录包含看起来像 Codex 的程序文件，但没有本工具的所有权标记。\n\n如果这是其他工具或手工解包的目录，继续后卸载可能删除其中的程序文件。是否确认接管并继续？",
                "确认接管无标记便携目录",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (approved)
            {
                adoptionApproval = LegacyAdoptionApproval.Create(installRoot);
            }
            return approved;
        }

        private void TryApplyManagerIcon()
        {
            try
            {
                using (System.Drawing.Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName))
                {
                    if (icon != null) Icon = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                }
            }
            catch { }
        }

        private OperationSnapshot CaptureOperationSnapshot() => new OperationSnapshot(Environment.ExpandEnvironmentVariables(installPathTextBox.Text.Trim()), sandboxCompatibilityCheckBox.IsChecked == true, unlockModelCatalogCheckBox.IsChecked == true, supplementChineseUiCheckBox.IsChecked == true, englishTechnicalParametersCheckBox.IsChecked == true, installPathRevision);

        private OperationSnapshot CaptureCompatibilityApplySnapshot()
        {
            CompatibilitySwitchFacts facts = compatibilityOverviewPathRevision == installPathRevision
                ? CompatibilityStatusReader.ResolveSwitchFacts(compatibilityOverview)
                : new CompatibilitySwitchFacts(null, null, null, null);
            bool sandboxEnabled = sandboxCompatibilityCheckBox.IsChecked == true;
            bool modelEnabled = unlockModelCatalogCheckBox.IsChecked == true;
            bool chineseEnabled = supplementChineseUiCheckBox.IsChecked == true;
            bool englishEnabled = englishTechnicalParametersCheckBox.IsChecked == true;
            bool manageSandbox = facts.SandboxCompatibilityEnabled.HasValue &&
                facts.SandboxCompatibilityEnabled.Value != sandboxEnabled;
            bool manageModel = facts.UnlockModelCatalogEnabled.HasValue &&
                facts.UnlockModelCatalogEnabled.Value != modelEnabled;
            bool manageLocalization = facts.SupplementChineseUiEnabled.HasValue &&
                facts.EnglishTechnicalParametersEnabled.HasValue &&
                (facts.LocalizationNeedsRefresh ||
                 facts.SupplementChineseUiEnabled.Value != chineseEnabled ||
                 facts.EnglishTechnicalParametersEnabled.Value != englishEnabled);
            return new OperationSnapshot(
                Environment.ExpandEnvironmentVariables(installPathTextBox.Text.Trim()),
                sandboxEnabled,
                modelEnabled,
                chineseEnabled,
                englishEnabled,
                installPathRevision,
                manageSandbox,
                manageModel,
                manageLocalization);
        }

        private void MainWindow_Closing(object sender, CancelEventArgs args)
        {
            string message = operationController.GetClosingMessage();
            if (message == null) return;
            args.Cancel = true;
            MessageBox.Show(this, message, "操作尚未完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void MainWindow_Closed(object sender, EventArgs args) { elapsedTimer.Stop(); pathStatusTimer.Stop(); operationController.Dispose(); service.Dispose(); }

        private void SetStatusSummary(StatusSummaryPresentation presentation)
        {
            if (presentation == null) throw new ArgumentNullException(nameof(presentation));
            SetStatusSummary(presentation.Text, presentation.BrushKey);
        }

        private void SetStatusSummary(string text, string brushKey)
        {
            System.Windows.Media.Brush brush = ResolveBrush(brushKey);
            statusValueLabel.Text = text;
            statusValueLabel.Foreground = brush;
            statusIndicator.Background = brush;
        }

        private System.Windows.Media.Brush ResolveBrush(string resourceKey)
        {
            System.Windows.Media.Brush brush = TryFindResource(resourceKey) as System.Windows.Media.Brush;
            if (brush == null)
            {
                throw new InvalidOperationException("界面语义颜色资源不存在：" + resourceKey);
            }
            return brush;
        }

    }
}
