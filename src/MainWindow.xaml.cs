using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexPortableManager
{
    internal sealed partial class MainWindow : Window
    {
        private readonly CodexPortableService service;
        private readonly DispatcherTimer elapsedTimer;
        private readonly DispatcherTimer pathStatusTimer;
        private readonly Stopwatch operationStopwatch = new Stopwatch();
        private readonly Stopwatch operationStageStopwatch = new Stopwatch();
        private readonly OperationController operationController = new OperationController();
        private readonly object pendingLogSync = new object();
        private readonly object sessionLogSync = new object();
        private readonly StringBuilder pendingUiLog = new StringBuilder();
        private readonly string sessionLogPath;
        private const double WideLayoutBreakpoint = 1060;
        private const double WideLayoutHysteresis = 24;
        private const double CompactHeightBreakpoint = 700;
        private const double CompactHeightHysteresis = 24;
        private const int MaximumUiLogCharacters = 256 * 1024;
        private const int RetainedUiLogCharacters = 192 * 1024;
        private const int DwmWindowAttributeBorderColor = 34;
        private bool statusMatchesCurrentPath;
        private bool portableVersionAvailable;
        private bool previousVersionAvailable;
        private bool cachedRollbackVersionAvailable;
        private bool storeVersionInstalled;
        private bool installPathInvalid;
        private bool deploymentCleanupPending;
        private bool uninstallBackgroundCleanupActive;
        private bool postDeploymentCleanupActive;
        private bool narrowLogExpanded;
        private bool responsiveLayoutInitialized;
        private bool appliedWideLayout;
        private bool appliedCompactHeight;
        private bool appliedNarrowLogExpanded;
        private bool scrollEdgeUpdatePending;
        private bool uiLogFlushPending;
        private int installPathRevision;
        private CompatibilityOverview compatibilityOverview;
        private int compatibilityOverviewPathRevision = -1;
        private bool compatibilityApplyNeeded;
        private bool compatibilityDraftDirty;
        private bool compatibilityOverviewLoadActive;
        private bool updatingCompatibilitySwitches;
        private string lastProgressLogMessage;
        private int lastProgressLoggedPercent = -10;

        public MainWindow(bool autoRefresh)
            : this(autoRefresh, true, null)
        {
        }

        internal MainWindow(
            bool autoRefresh,
            bool initializeOnLoaded,
            ManagerSettings initialSettings)
        {
            ManagerSettings managerSettings = initialSettings ?? PortableStorage.LoadSettings();
            if (initialSettings == null)
            {
                managerSettings.InstallRoot = InstallLocationResolver.ResolveInstallRoot(managerSettings.InstallRoot);
            }

            InitializeComponent();
            TryApplyManagerIcon();
            InitializeAboutPage();

            installPathTextBox.Text = managerSettings.InstallRoot;
            sandboxCompatibilityCheckBox.IsChecked = false;
            unlockModelCatalogCheckBox.IsChecked = false;
            supplementChineseUiCheckBox.IsChecked = false;
            englishTechnicalParametersCheckBox.IsChecked = false;

            Directory.CreateDirectory(PortableStorage.LogsRoot);
            sessionLogPath = Path.Combine(
                PortableStorage.LogsRoot,
                "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            service = new CodexPortableService(AppendLog);
            elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            elapsedTimer.Tick += (sender, args) => UpdateProgressMeta();
            pathStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            pathStatusTimer.Tick += async (sender, args) =>
            {
                pathStatusTimer.Stop();
                if (!operationController.State.Busy) await RefreshLocalPathStatusAsync(CaptureOperationSnapshot());
            };

            ApplyUiState();
            WireEvents(autoRefresh, initializeOnLoaded);
        }

        private void WireEvents(bool autoRefresh, bool initializeOnLoaded)
        {
            WireAboutEvents();
            browseButton.Click += BrowseButton_Click;
            checkButton.Click += async (sender, args) => await RunOperationAsync(
                RefreshStatusAsync,
                true,
                false,
                OperationDisplayKind.Check);
            installButton.Click += async (sender, args) => await InstallButton_Click();
            downloadPackageButton.Click += async (sender, args) => await DownloadPackageButton_Click();
            migrateButton.Click += async (sender, args) => await MigrateAsync();
            launchButton.Click += LaunchButton_Click;
            rollbackButton.Click += async (sender, args) => await RollbackButton_Click();
            uninstallPortableButton.Click += async (sender, args) => await UninstallPortableAsync();
            openFolderButton.Click += OpenFolderButton_Click;
            pauseButton.Click += PauseButton_Click;
            cancelButton.Click += CancelButton_Click;
            applyCompatibilityButton.Click += async (sender, args) => await RunOperationAsync(
                ApplyCompatibilitySettingsAsync,
                false,
                true,
                OperationDisplayKind.Compatibility,
                true);
            checkCompatibilityStatusButton.Click += async (sender, args) => await RunOperationAsync(
                InspectCompatibilitySettingsAsync,
                false,
                true,
                OperationDisplayKind.CompatibilityCheck);
            repairIntegrationButton.Click += async (sender, args) => await RunOperationAsync(
                RepairIntegrationAsync,
                false,
                true,
                OperationDisplayKind.Integration);
            openLogButton.Click += OpenLogButton_Click;
            clearLogButton.Click += (sender, args) => logBox.Clear();
            toggleLogButton.Click += ToggleLogButton_Click;
            mainScrollViewer.ScrollChanged += (sender, args) => QueueMainScrollEdgeUpdate();
            compatibilityScrollViewer.ScrollChanged += (sender, args) => QueueMainScrollEdgeUpdate();
            aboutScrollViewer.ScrollChanged += (sender, args) => QueueMainScrollEdgeUpdate();
            mainTabControl.SelectionChanged += async (sender, args) =>
            {
                if (!ReferenceEquals(args.Source, mainTabControl)) return;
                QueueMainScrollEdgeUpdate();
                if (mainTabControl.SelectedIndex == 1 && IsLoaded)
                {
                    await EnsureCompatibilityOverviewLoadedAsync(
                        CaptureOperationSnapshot(),
                        System.Threading.CancellationToken.None,
                        true);
                }
            };
            installPathTextBox.TextChanged += InstallPathTextBox_TextChanged;
            sandboxCompatibilityCheckBox.Checked += SettingsCheckBox_Changed;
            sandboxCompatibilityCheckBox.Unchecked += SettingsCheckBox_Changed;
            unlockModelCatalogCheckBox.Checked += SettingsCheckBox_Changed;
            unlockModelCatalogCheckBox.Unchecked += SettingsCheckBox_Changed;
            supplementChineseUiCheckBox.Checked += SettingsCheckBox_Changed;
            supplementChineseUiCheckBox.Unchecked += SettingsCheckBox_Changed;
            englishTechnicalParametersCheckBox.Checked += SettingsCheckBox_Changed;
            englishTechnicalParametersCheckBox.Unchecked += SettingsCheckBox_Changed;
            SizeChanged += (sender, args) => UpdateResponsiveLayout();
            SourceInitialized += (sender, args) => ApplyCompositionBackground();
            Closing += MainWindow_Closing;
            Closed += MainWindow_Closed;
            Loaded += async (sender, args) =>
            {
                UpdateResponsiveLayout();
                if (!initializeOnLoaded) return;
                await Dispatcher.InvokeAsync(delegate { }, DispatcherPriority.ContextIdle).Task;
                if (IsLoaded) await InitializeWindowAsync(autoRefresh);
            };
        }

        private void ToggleLogButton_Click(object sender, RoutedEventArgs args)
        {
            narrowLogExpanded = !narrowLogExpanded;
            UpdateResponsiveLayout();
        }

        private void UpdateResponsiveLayout()
        {
            double availableWidth = ActualWidth > 0 ? ActualWidth : Width;
            double availableHeight = ActualHeight > 0 ? ActualHeight : Height;
            bool wideLayout = responsiveLayoutInitialized
                ? appliedWideLayout
                    ? availableWidth >= WideLayoutBreakpoint - WideLayoutHysteresis
                    : availableWidth >= WideLayoutBreakpoint + WideLayoutHysteresis
                : availableWidth >= WideLayoutBreakpoint;
            bool compactHeight = responsiveLayoutInitialized
                ? appliedCompactHeight
                    ? availableHeight < CompactHeightBreakpoint + CompactHeightHysteresis
                    : availableHeight < CompactHeightBreakpoint - CompactHeightHysteresis
                : availableHeight < CompactHeightBreakpoint;
            bool widthModeChanged = !responsiveLayoutInitialized || appliedWideLayout != wideLayout;
            bool heightModeChanged = !responsiveLayoutInitialized || appliedCompactHeight != compactHeight;
            bool narrowLogModeChanged = !responsiveLayoutInitialized ||
                appliedNarrowLogExpanded != narrowLogExpanded;

            if (heightModeChanged)
            {
                statusSummaryCard.Visibility = compactHeight
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            if (widthModeChanged)
            {
                UpdateStatusSummaryLayout(wideLayout);
                Grid.SetRow(mainTabControl, 0);
                Grid.SetColumn(mainTabControl, 0);
                Grid.SetColumnSpan(mainTabControl, 1);

                if (wideLayout)
                {
                    workspaceGrid.ColumnDefinitions[1].Width = new GridLength(14);
                    workspaceGrid.ColumnDefinitions[2].Width = new GridLength(360);
                    Grid.SetRow(activityPane, 0);
                    Grid.SetColumn(activityPane, 2);
                    activityPane.Height = double.NaN;
                    activityPane.Margin = new Thickness(0);
                    activityPane.VerticalAlignment = VerticalAlignment.Stretch;
                    logSection.Visibility = Visibility.Visible;
                    toggleLogButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    workspaceGrid.ColumnDefinitions[1].Width = new GridLength(0);
                    workspaceGrid.ColumnDefinitions[2].Width = new GridLength(0);
                    Grid.SetRow(activityPane, 1);
                    Grid.SetColumn(activityPane, 0);
                    activityPane.Margin = new Thickness(0, 12, 4, 0);
                    activityPane.VerticalAlignment = VerticalAlignment.Bottom;
                    toggleLogButton.Visibility = Visibility.Visible;
                }
            }

            if (!wideLayout && (widthModeChanged || narrowLogModeChanged))
            {
                activityPane.Height = narrowLogExpanded ? 250 : double.NaN;
                logSection.Visibility = narrowLogExpanded ? Visibility.Visible : Visibility.Collapsed;
                toggleLogButton.Content = narrowLogExpanded ? "收起日志" : "展开日志";
            }

            responsiveLayoutInitialized = true;
            appliedWideLayout = wideLayout;
            appliedCompactHeight = compactHeight;
            appliedNarrowLogExpanded = narrowLogExpanded;
            if (widthModeChanged || heightModeChanged || narrowLogModeChanged)
            {
                QueueMainScrollEdgeUpdate();
            }
        }

        private void UpdateStatusSummaryLayout(bool wideLayout)
        {
            statusSummaryGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            statusSummaryGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            statusSummaryGrid.ColumnDefinitions[2].Width = wideLayout
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
            statusSummaryGrid.ColumnDefinitions[3].Width = wideLayout
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
            statusSummaryGrid.RowDefinitions[1].Height = wideLayout
                ? new GridLength(0)
                : GridLength.Auto;

            Grid.SetRow(portablePackageSummaryPanel, 0);
            Grid.SetColumn(portablePackageSummaryPanel, 0);
            Grid.SetRow(portableApplicationSummaryPanel, 0);
            Grid.SetColumn(portableApplicationSummaryPanel, 1);
            Grid.SetRow(latestPackageSummaryPanel, wideLayout ? 0 : 1);
            Grid.SetColumn(latestPackageSummaryPanel, wideLayout ? 2 : 0);
            Grid.SetRow(overallStatusSummaryPanel, wideLayout ? 0 : 1);
            Grid.SetColumn(overallStatusSummaryPanel, wideLayout ? 3 : 1);

            latestPackageSummaryPanel.Margin = new Thickness(22, wideLayout ? 0 : 14, 16, 0);
            overallStatusSummaryPanel.Margin = new Thickness(22, wideLayout ? 0 : 14, 10, 0);
            statusWideSeparator0.Visibility = wideLayout ? Visibility.Visible : Visibility.Collapsed;
            statusWideSeparator1.Visibility = wideLayout ? Visibility.Visible : Visibility.Collapsed;
            statusWideSeparator2.Visibility = wideLayout ? Visibility.Visible : Visibility.Collapsed;
            statusNarrowTopSeparator.Visibility = wideLayout ? Visibility.Collapsed : Visibility.Visible;
            statusNarrowBottomSeparator.Visibility = wideLayout ? Visibility.Collapsed : Visibility.Visible;
            statusNarrowRowSeparator.Visibility = wideLayout ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateMainScrollEdges()
        {
            if (mainScrollViewer == null || mainScrollTopEdge == null || mainScrollBottomEdge == null) return;

            ScrollViewer activeScrollViewer = mainScrollViewer;
            if (mainTabControl != null && mainTabControl.SelectedIndex == 1)
            {
                activeScrollViewer = compatibilityScrollViewer;
            }
            else if (mainTabControl != null && mainTabControl.SelectedIndex == 2)
            {
                activeScrollViewer = aboutScrollViewer;
            }
            if (activeScrollViewer == null) return;
            const double threshold = 0.5;
            bool hasScrollableContent = activeScrollViewer.ScrollableHeight > threshold;
            bool hasContentAbove = hasScrollableContent && activeScrollViewer.VerticalOffset > threshold;
            bool hasContentBelow = hasScrollableContent &&
                activeScrollViewer.VerticalOffset < activeScrollViewer.ScrollableHeight - threshold;
            Visibility topVisibility = hasContentAbove ? Visibility.Visible : Visibility.Collapsed;
            Visibility bottomVisibility = hasContentBelow ? Visibility.Visible : Visibility.Collapsed;
            if (mainScrollTopEdge.Visibility != topVisibility)
            {
                mainScrollTopEdge.Visibility = topVisibility;
            }
            if (mainScrollBottomEdge.Visibility != bottomVisibility)
            {
                mainScrollBottomEdge.Visibility = bottomVisibility;
            }
        }

        private void QueueMainScrollEdgeUpdate()
        {
            if (scrollEdgeUpdatePending || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            scrollEdgeUpdatePending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                scrollEdgeUpdatePending = false;
                UpdateMainScrollEdges();
            }));
        }

        private void ApplyCompositionBackground()
        {
            try
            {
                HwndSource source = PresentationSource.FromVisual(this) as HwndSource;
                SolidColorBrush background = TryFindResource("WindowBackgroundBrush") as SolidColorBrush;
                if (source != null && source.CompositionTarget != null && background != null)
                {
                    source.CompositionTarget.BackgroundColor = background.Color;
                    uint borderColor = background.Color.R |
                        ((uint)background.Color.G << 8) |
                        ((uint)background.Color.B << 16);
                    DwmSetWindowAttribute(
                        source.Handle,
                        DwmWindowAttributeBorderColor,
                        ref borderColor,
                        Marshal.SizeOf(typeof(uint)));
                }
            }
            catch
            {
                // 合成背景和原生边框只是窗口切换时的视觉保护，不应阻止管理器启动。
            }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr windowHandle,
            int attribute,
            ref uint attributeValue,
            int attributeSize);

        private async System.Threading.Tasks.Task InitializeWindowAsync(bool autoRefresh)
        {
            OperationSnapshot initial = CaptureOperationSnapshot();
            AppendLog("管理器已启动。版本：" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);
            AppendLog(string.IsNullOrWhiteSpace(initial.InstallRoot)
                ? "便携版目标目录：没有有效的成功记录或注册表目录，等待用户选择。"
                : "便携版目标目录：" + initial.InstallRoot);
            AppendLog("缓存目录：" + PortableStorage.CacheRoot);
            AppendLog("本次日志：" + sessionLogPath);
            try
            {
                if (autoRefresh) await RunOperationAsync(
                    RefreshStatusAsync,
                    true,
                    false,
                    OperationDisplayKind.Check);
                else await RefreshLocalPathStatusAsync(initial);
            }
            finally
            {
                try
                {
                    if (deploymentCleanupPending &&
                        !string.IsNullOrWhiteSpace(initial.InstallRoot))
                    {
                        postDeploymentCleanupActive = true;
                        ApplyUiState();
                        System.Threading.Tasks.Task cleanupObservation = ObservePostDeploymentCleanupAsync(
                            initial,
                            service.StartPostDeploymentCleanupAsync(initial.InstallRoot));
                    }
                    else
                    {
                        ObserveStorageMaintenanceAsync(
                            service.StartStorageMaintenanceAsync());
                    }
                }
                catch (Exception exception)
                {
                    AppendLog("启动辅助维护异常已降级处理：" + exception.Message);
                }
            }
        }

    }
}
