using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexPortableManager
{
    internal static class RenderTestRunner
    {
        public static void Run(
            string imagePath,
            double targetWidth = 0,
            double targetHeight = 0,
            double verticalOffset = 0,
            string displayState = null)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            Application application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ManagerSettings renderSettings = new ManagerSettings
            {
                InstallRoot = @"C:\CodexPortableManagerRender\Codex"
            };
            MainWindow window = new MainWindow(false, false, renderSettings) { ShowInTaskbar = false };
            if (targetWidth > 0) window.Width = Math.Max(window.MinWidth, targetWidth);
            if (targetHeight > 0) window.Height = Math.Max(window.MinHeight, targetHeight);
            bool compatibilityDisplay = string.Equals(
                displayState,
                "compatibility",
                StringComparison.OrdinalIgnoreCase);
            bool aboutDisplay = string.Equals(
                displayState,
                "about",
                StringComparison.OrdinalIgnoreCase);
            if (compatibilityDisplay || aboutDisplay)
            {
                TabControl tabs = window.FindName("mainTabControl") as TabControl;
                if (tabs != null) tabs.SelectedIndex = aboutDisplay ? 2 : 1;
            }
            window.Show();
            window.UpdateLayout();
            if (string.Equals(displayState, "download", StringComparison.OrdinalIgnoreCase))
            {
                Button pauseButton = window.FindName("pauseButton") as Button;
                Button cancelButton = window.FindName("cancelButton") as Button;
                TextBlock progressLabel = window.FindName("progressLabel") as TextBlock;
                TextBlock progressDetail = window.FindName("progressDetailLabel") as TextBlock;
                if (pauseButton != null)
                {
                    pauseButton.Visibility = Visibility.Visible;
                    pauseButton.IsEnabled = true;
                }
                if (cancelButton != null)
                {
                    cancelButton.Visibility = Visibility.Visible;
                    cancelButton.IsEnabled = true;
                }
                if (progressLabel != null) progressLabel.Text = "下载微软官方程序包";
                if (progressDetail != null) progressDetail.Text = "278.0 / 694.9 MiB · 9.3 MiB/s · 预计剩余 45 秒";
                TextBlock progressMeta = window.FindName("progressMetaLabel") as TextBlock;
                ProgressBar progressBar = window.FindName("progressBar") as ProgressBar;
                if (progressMeta != null) progressMeta.Text = "下载 40% · 已用 0:31";
                if (progressBar != null)
                {
                    progressBar.Visibility = Visibility.Visible;
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = 40;
                }
                window.UpdateLayout();
            }
            else if (string.Equals(displayState, "check-running", StringComparison.OrdinalIgnoreCase))
            {
                TextBlock latestValue = window.FindName("latestValueLabel") as TextBlock;
                TextBlock statusValue = window.FindName("statusValueLabel") as TextBlock;
                Border statusIndicator = window.FindName("statusIndicator") as Border;
                SolidColorBrush runningBrush =
                    (SolidColorBrush)window.FindResource("PrimaryBrush");
                if (latestValue != null) latestValue.Text = "检查中...";
                if (statusValue != null)
                {
                    statusValue.Text = "正在检查";
                    statusValue.Foreground = runningBrush;
                }
                if (statusIndicator != null) statusIndicator.Background = runningBrush;
                ApplyTaskState(
                    window,
                    "正在检查 Codex 版本与安装状态",
                    "正在同时检测本机官方桌面版、便携版、回滚备份和微软最新版本。",
                    "处理中 · 已用 0:02",
                    true,
                    true,
                    0);
            }
            else if (string.Equals(displayState, "install-stage-timing", StringComparison.OrdinalIgnoreCase))
            {
                ApplyTaskState(
                    window,
                    "创建回滚备份",
                    "上一安装的进程已经全部退出，正在把当前版本保留为 .previous。",
                    "93% · 阶段 0:04 · 总计 2:51",
                    true,
                    false,
                    93);
            }
            else if (string.Equals(displayState, "download-canceled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(displayState, "install-canceled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(displayState, "check-canceled", StringComparison.OrdinalIgnoreCase))
            {
                bool downloadCanceled = displayState.StartsWith("download", StringComparison.OrdinalIgnoreCase);
                bool checkCanceled = displayState.StartsWith("check", StringComparison.OrdinalIgnoreCase);
                TextBlock progressLabel = window.FindName("progressLabel") as TextBlock;
                TextBlock progressDetail = window.FindName("progressDetailLabel") as TextBlock;
                TextBlock progressMeta = window.FindName("progressMetaLabel") as TextBlock;
                TextBlock latestValue = window.FindName("latestValueLabel") as TextBlock;
                ProgressBar progressBar = window.FindName("progressBar") as ProgressBar;
                if (progressLabel != null)
                {
                    progressLabel.Text = downloadCanceled
                        ? "官方 MSIX 下载已取消"
                        : (checkCanceled ? "版本检查已取消" : "创建 / 更新 / 修复已取消");
                }
                if (progressDetail != null)
                {
                    progressDetail.Text = downloadCanceled
                        ? "本次未生成新的官方 MSIX；未验证的临时下载已清理。"
                        : (checkCanceled
                            ? "版本与安装状态检查已停止，可以重新检查。"
                            : "创建 / 更新 / 修复已在安全检查点停止；当前便携版状态已重新检查。");
                }
                if (checkCanceled && latestValue != null) latestValue.Text = "未完成检查";
                if (progressMeta != null) progressMeta.Text = "已取消 · 用时 0:05";
                if (progressBar != null) progressBar.Visibility = Visibility.Collapsed;
                window.UpdateLayout();
            }
            else if (string.Equals(displayState, "rollback-completed", StringComparison.OrdinalIgnoreCase))
            {
                TextBlock progressLabel = window.FindName("progressLabel") as TextBlock;
                TextBlock progressDetail = window.FindName("progressDetailLabel") as TextBlock;
                TextBlock progressMeta = window.FindName("progressMetaLabel") as TextBlock;
                ProgressBar progressBar = window.FindName("progressBar") as ProgressBar;
                if (progressLabel != null) progressLabel.Text = "已回滚到 Codex 26.707.9564.0";
                if (progressDetail != null)
                {
                    progressDetail.Text = "版本 26.707.9564.0 已恢复；回滚前版本已保留在 .previous，可再次回滚切换。";
                }
                if (progressMeta != null) progressMeta.Text = "100% · 已用 0:06";
                if (progressBar != null)
                {
                    progressBar.Visibility = Visibility.Visible;
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = 100;
                }
                window.UpdateLayout();
            }
            else if (string.Equals(displayState, "cancel-requested", StringComparison.OrdinalIgnoreCase))
            {
                ApplyTaskState(
                    window,
                    "正在取消官方 MSIX 下载",
                    "正在停止下载，并清理本次任务尚未验证的临时文件。",
                    "正在取消 · 已用 0:32",
                    true,
                    true,
                    0);
            }
            else if (string.Equals(displayState, "check-no-path", StringComparison.OrdinalIgnoreCase))
            {
                ApplyTaskState(
                    window,
                    "检查完成：尚未选择便携版目标目录",
                    "微软最新包版本为 26.707.9981.0；官方桌面版未安装。请先选择便携版目标目录。",
                    "100% · 已用 0:03",
                    true,
                    false,
                    100);
            }
            else if (string.Equals(displayState, "store-installed", StringComparison.OrdinalIgnoreCase))
            {
                window.ApplyStatusForRenderTest(
                    new PortableLocalStatus(null, null, false, null, true),
                    new PortableStatus
                    {
                        StoreState = StorePackageState.Installed,
                        LatestPackage = new PackageMetadata { version = "26.707.12708.0" }
                    });
            }
            else if (string.Equals(displayState, "migration-completed", StringComparison.OrdinalIgnoreCase))
            {
                ApplyTaskState(
                    window,
                    "迁移完成，部分系统集成未完成",
                    "官方桌面版已卸载；便携版已验证并发起启动；部分系统集成未完成，可使用“修复启动入口”重试；详情请查看日志。",
                    "100% · 已用 2:18",
                    true,
                    false,
                    100);
            }
            else if (string.Equals(displayState, "compatibility-update-warning", StringComparison.OrdinalIgnoreCase))
            {
                ApplyTaskState(
                    window,
                    "Codex 便携版更新完成，部分功能设置等待适配",
                    "版本 26.707.9564.0 已就绪；部分兼容设置未能适配新版本，已恢复官方程序文件并保留当前选择；详情请查看日志。",
                    "100% · 已用 1:42",
                    true,
                    false,
                    100);
            }
            else if (string.Equals(displayState, "uninstall-cleanup-pending", StringComparison.OrdinalIgnoreCase))
            {
                window.ApplyStatusForRenderTest(
                    new PortableLocalStatus(
                        null,
                        null,
                        false,
                        null,
                        true,
                        false,
                        true,
                        true),
                    new PortableStatus
                    {
                        StoreState = StorePackageState.NotInstalled,
                        LatestPackage = new PackageMetadata
                        {
                            version = "26.707.12708.0"
                        }
                    });
            }
            else if (string.Equals(displayState, "uninstall-completed", StringComparison.OrdinalIgnoreCase))
            {
                ApplyTaskState(
                    window,
                    "Codex 便携版已卸载",
                    "当前版本和 .previous 回滚备份已删除；已请求清理系统集成。用户资料、管理器缓存和日志均已保留。",
                    "100% · 已用 0:12",
                    true,
                    false,
                    100);
            }
            else if (string.Equals(displayState, "uninstall-background-cleanup", StringComparison.OrdinalIgnoreCase))
            {
                ApplyTaskState(
                    window,
                    "Codex 便携版已卸载，后台清理中",
                    "当前版本和 .previous 回滚备份已从活动槽移除；程序文件已移入隔离目录，正在独立后台清理，不影响关闭管理器。用户资料、管理器缓存和日志均已保留。",
                    "100% · 用时 0:03",
                    true,
                    false,
                    100);
            }
            else if (string.Equals(displayState, "passive-launch", StringComparison.OrdinalIgnoreCase))
            {
                ApplyTaskState(
                    window,
                    "已发起 Codex 便携版启动",
                    "启动请求已提交到：D:\\Program\\OpenAI\\CodexDesktop",
                    "已完成",
                    false,
                    false,
                    0);
            }
            else if (string.Equals(displayState, "operation-failed", StringComparison.OrdinalIgnoreCase))
            {
                ApplyTaskState(
                    window,
                    "创建 / 更新 / 修复失败",
                    "无法下载微软官方程序包：网络连接在恢复窗口内仍未恢复。详细过程已写入运行日志。",
                    "失败 · 用时 30:00",
                    false,
                    false,
                    0);
            }
            else if (string.Equals(displayState, "previous-only", StringComparison.OrdinalIgnoreCase))
            {
                Button rollbackButton = window.FindName("rollbackButton") as Button;
                Button uninstallButton = window.FindName("uninstallPortableButton") as Button;
                TextBlock uninstallDescription = window.FindName("uninstallDescriptionLabel") as TextBlock;
                if (rollbackButton != null) rollbackButton.IsEnabled = false;
                if (uninstallButton != null)
                {
                    uninstallButton.IsEnabled = true;
                    uninstallButton.Content = "删除遗留回滚备份";
                }
                if (uninstallDescription != null)
                {
                    uninstallDescription.Text = "删除仅剩的 .previous，保留用户资料与管理器数据";
                }
                ApplyTaskState(
                    window,
                    "已检测到遗留回滚备份",
                    "当前版本不存在，仅检测到可安全删除的 .previous 回滚备份。",
                    "就绪",
                    false,
                    false,
                    0);
            }
            else if (string.Equals(displayState, "compatibility", StringComparison.OrdinalIgnoreCase))
            {
                ApplyCompatibilityPreview(window);
            }
            ScrollViewer scrollViewer = aboutDisplay
                ? window.FindName("aboutScrollViewer") as ScrollViewer
                : compatibilityDisplay
                    ? window.FindName("compatibilityScrollViewer") as ScrollViewer
                    : window.FindName("mainScrollViewer") as ScrollViewer;
            if (scrollViewer != null && verticalOffset > 0)
            {
                scrollViewer.ScrollToVerticalOffset(verticalOffset);
                window.UpdateLayout();
            }
            window.Dispatcher.Invoke(delegate { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            int pixelWidth = Math.Max(1, (int)window.ActualWidth);
            int pixelHeight = Math.Max(1, (int)window.ActualHeight);
            RenderTargetBitmap bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(imagePath)) encoder.Save(stream);
            window.Close();
            application.Shutdown();
        }

        private static void ApplyTaskState(
            MainWindow window,
            string title,
            string detail,
            string meta,
            bool progressVisible,
            bool indeterminate,
            double value)
        {
            TextBlock progressLabel = window.FindName("progressLabel") as TextBlock;
            TextBlock progressDetail = window.FindName("progressDetailLabel") as TextBlock;
            TextBlock progressMeta = window.FindName("progressMetaLabel") as TextBlock;
            ProgressBar progressBar = window.FindName("progressBar") as ProgressBar;
            if (progressLabel != null) progressLabel.Text = title;
            if (progressDetail != null) progressDetail.Text = detail;
            if (progressMeta != null) progressMeta.Text = meta;
            if (progressBar != null)
            {
                progressBar.Visibility = progressVisible ? Visibility.Visible : Visibility.Collapsed;
                progressBar.IsIndeterminate = indeterminate;
                progressBar.Value = value;
            }
            window.UpdateLayout();
        }

        private static void ApplyCompatibilityPreview(MainWindow window)
        {
            SetTogglePreview(window, "sandboxCompatibilityCheckBox", "sandboxCompatibilityStatusLabel", true, false);
            SetTogglePreview(window, "unlockModelCatalogCheckBox", "modelCatalogStatusLabel", true, true);
            SetTogglePreview(window, "supplementChineseUiCheckBox", "chineseUiStatusLabel", false, true);
            SetTogglePreview(window, "englishTechnicalParametersCheckBox", "englishParametersStatusLabel", false, false);
            TextBlock summary = window.FindName("compatibilitySummaryLabel") as TextBlock;
            if (summary != null) summary.Text = "2 项设置尚未同步。";
            Button apply = window.FindName("applyCompatibilityButton") as Button;
            if (apply != null)
            {
                apply.Content = "应用 2 项更改";
                apply.IsEnabled = true;
            }
            Button check = window.FindName("checkCompatibilityStatusButton") as Button;
            if (check != null) check.IsEnabled = true;
            window.UpdateLayout();
        }

        private static void SetTogglePreview(
            MainWindow window,
            string checkBoxName,
            string labelName,
            bool desired,
            bool actual)
        {
            CheckBox checkBox = window.FindName(checkBoxName) as CheckBox;
            if (checkBox != null) checkBox.IsChecked = desired;
            SetText(
                window,
                labelName,
                actual ? "当前已开启" : "当前未开启",
                desired != actual ? "WarningBrush" : actual ? "SuccessBrush" : "MutedBrush");
        }

        private static void SetText(
            MainWindow window,
            string name,
            string text,
            string brushKey)
        {
            TextBlock label = window.FindName(name) as TextBlock;
            if (label == null) return;
            label.Text = text;
            label.Foreground = (Brush)window.FindResource(brushKey);
        }
    }
}
