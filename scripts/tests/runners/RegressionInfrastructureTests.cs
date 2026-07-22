using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPortableManager
{
internal static partial class RegressionTestRunner
{
    private static void TestCrossProcessOperationLock()
    {
        string caseRoot = NewCaseRoot("operation-lock");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string readyPath = Path.Combine(caseRoot, "child-ready.txt");
        string harnessPath = Process.GetCurrentProcess().MainModule.FileName;
        WithIsolatedLocalAppData("operation-lock-profile", delegate
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = harnessPath,
                Arguments = string.Join(" ", new[]
                {
                    QuoteArgument("--hold-lock"),
                    QuoteArgument(managerPath),
                    QuoteArgument(installRoot),
                    QuoteArgument(readyPath),
                    QuoteArgument("2500")
                }),
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process child = Process.Start(startInfo))
            {
                Stopwatch readyWait = Stopwatch.StartNew();
                while (!File.Exists(readyPath) && !child.HasExited && readyWait.Elapsed < TimeSpan.FromSeconds(8))
                {
                    Thread.Sleep(25);
                }
                Assert(File.Exists(readyPath), "持锁子进程未在超时内就绪，退出码：" + (child.HasExited ? child.ExitCode.ToString(CultureInfo.InvariantCulture) : "仍在运行"));

                string secondProfile = Path.Combine(caseRoot, "second-profile");
                string secondLocalAppData = Path.Combine(secondProfile, "AppData", "Local");
                Directory.CreateDirectory(secondLocalAppData);
                Environment.SetEnvironmentVariable("USERPROFILE", secondProfile);
                Environment.SetEnvironmentVariable("LOCALAPPDATA", secondLocalAppData);

                bool cancelled = false;
                Stopwatch blocked = Stopwatch.StartNew();
                using (CancellationTokenSource cancellation = new CancellationTokenSource(700))
                {
                    Task<OperationFileLock> waitTask = OperationFileLock.AcquireAsync(
                        installRoot,
                        cancellation.Token);
                    try
                    {
                        waitTask.GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }
                }
                blocked.Stop();

                Assert(cancelled, "同一安装根的第二个进程在首个锁释放前错误获得了锁。");
                Assert(blocked.Elapsed >= TimeSpan.FromMilliseconds(500), "第二个进程没有实际等待首个进程的操作锁。");
                Assert(!child.HasExited, "首个持锁进程在互斥断言前已提前退出。");
                Assert(child.WaitForExit(8000), "持锁子进程未正常退出。");
                Assert(child.ExitCode == 0, "持锁子进程退出码异常：" + child.ExitCode.ToString(CultureInfo.InvariantCulture));
            }

            Stopwatch reacquireTime = Stopwatch.StartNew();
            using (OperationFileLock reacquired = OperationFileLock.Acquire(installRoot))
            {
                reacquireTime.Stop();
            }
            Assert(reacquireTime.Elapsed < TimeSpan.FromSeconds(2), "首个进程退出后未能及时重新获得操作锁。");

            string abandonedReadyPath = Path.Combine(caseRoot, "abandoned-ready.txt");
            ProcessStartInfo abandonedStartInfo = new ProcessStartInfo
            {
                FileName = harnessPath,
                Arguments = string.Join(" ", new[]
                {
                    QuoteArgument("--hold-lock"),
                    QuoteArgument(managerPath),
                    QuoteArgument(installRoot),
                    QuoteArgument(abandonedReadyPath),
                    QuoteArgument("10000")
                }),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (Process abandonedChild = Process.Start(abandonedStartInfo))
            {
                Stopwatch readyWait = Stopwatch.StartNew();
                while (!File.Exists(abandonedReadyPath) &&
                    !abandonedChild.HasExited &&
                    readyWait.Elapsed < TimeSpan.FromSeconds(8))
                {
                    Thread.Sleep(25);
                }
                Assert(File.Exists(abandonedReadyPath), "异常退出测试的持锁子进程未在超时内就绪。");
                abandonedChild.Kill();
                Assert(abandonedChild.WaitForExit(8000), "异常退出测试的持锁子进程未终止。");
            }

            Stopwatch abandonedAcquireTime = Stopwatch.StartNew();
            using (OperationFileLock recovered = OperationFileLock.Acquire(installRoot))
            {
                abandonedAcquireTime.Stop();
            }
            Assert(abandonedAcquireTime.Elapsed < TimeSpan.FromSeconds(2),
                "持锁进程异常退出后未能及时接管 abandoned mutex。");
        });
    }

    private static void TestOperationLockPathAliases()
    {
        string caseRoot = NewCaseRoot("operation-lock-aliases");
        string existingTarget = Path.Combine(caseRoot, "existing-target");
        string existingAlias = Path.Combine(caseRoot, "existing-alias");
        Directory.CreateDirectory(existingTarget);
        CreateJunction(existingAlias, existingTarget);
        AssertOperationLockBlocksAcrossPaths(
            existingTarget,
            existingAlias,
            Path.Combine(caseRoot, "existing-ready.txt"),
            "已存在安装目录的 junction 别名");

        string physicalParent = Path.Combine(caseRoot, "physical-parent");
        string parentAlias = Path.Combine(caseRoot, "parent-alias");
        Directory.CreateDirectory(physicalParent);
        CreateJunction(parentAlias, physicalParent);
        AssertOperationLockBlocksAcrossPaths(
            Path.Combine(physicalParent, "not-created", "CodexDesktop"),
            Path.Combine(parentAlias, "not-created", "CodexDesktop"),
            Path.Combine(caseRoot, "missing-ready.txt"),
            "尚未创建安装目录的 junction 祖先别名");
    }

    private static void AssertOperationLockBlocksAcrossPaths(
        string heldPath,
        string contenderPath,
        string readyPath,
        string scenario)
    {
        string harnessPath = Process.GetCurrentProcess().MainModule.FileName;
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = harnessPath,
            Arguments = string.Join(" ", new[]
            {
                QuoteArgument("--hold-lock"),
                QuoteArgument(managerPath),
                QuoteArgument(heldPath),
                QuoteArgument(readyPath),
                QuoteArgument("2500")
            }),
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (Process child = Process.Start(startInfo))
        {
            Stopwatch readyWait = Stopwatch.StartNew();
            while (!File.Exists(readyPath) && !child.HasExited && readyWait.Elapsed < TimeSpan.FromSeconds(8))
            {
                Thread.Sleep(25);
            }
            Assert(File.Exists(readyPath), scenario + "：持锁子进程未在超时内就绪。");

            bool cancelled = false;
            using (CancellationTokenSource cancellation = new CancellationTokenSource(700))
            {
                Task<OperationFileLock> waitTask = OperationFileLock.AcquireAsync(
                    contenderPath,
                    cancellation.Token);
                try
                {
                    waitTask.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
            }

            Assert(cancelled, scenario + "：竞争进程通过路径别名错误获得了操作锁。");
            Assert(!child.HasExited, scenario + "：互斥断言前持锁子进程已提前退出。");
            Assert(child.WaitForExit(8000), scenario + "：持锁子进程未正常退出。");
            Assert(child.ExitCode == 0, scenario + "：持锁子进程退出码异常。");
        }
    }

    private static void TestOperationLockPreservesExistingTargets()
    {
        string caseRoot = NewCaseRoot("operation-lock-existing-target");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        WithIsolatedLocalAppData("operation-lock-target-profile", delegate
        {
            string normalized = CrossProcessFileLock.NormalizePathKey(installRoot);
            string key = "operation|" + normalized;
            string lockPath = GetCrossProcessLockPath("operations", key);

            byte[] existingContent = Encoding.UTF8.GetBytes("预先存在的锁文件内容不得被截断或覆盖");
            File.WriteAllBytes(lockPath, existingContent);
            using (OperationFileLock firstLock = OperationFileLock.Acquire(installRoot)) { }
            Assert(File.Exists(lockPath), "预先存在的锁文件被错误删除。");
            Assert(BytesEqual(File.ReadAllBytes(lockPath), existingContent), "预先存在的锁文件被错误改写。");

            File.Delete(lockPath);
            string hardLinkTarget = Path.Combine(Path.GetDirectoryName(lockPath), "hardlink-target.bin");
            byte[] hardLinkContent = Encoding.UTF8.GetBytes("硬链接目标内容不得被改写");
            File.WriteAllBytes(hardLinkTarget, hardLinkContent);
            if (!CreateHardLink(lockPath, hardLinkTarget, IntPtr.Zero))
            {
                throw new IOException("无法创建锁文件硬链接，Win32=" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
            }

            using (OperationFileLock secondLock = OperationFileLock.Acquire(installRoot)) { }
            Assert(File.Exists(lockPath) && File.Exists(hardLinkTarget), "锁释放时错误删除了硬链接或其目标。");
            Assert(BytesEqual(File.ReadAllBytes(lockPath), hardLinkContent), "硬链接锁文件内容被错误改写。");
            Assert(BytesEqual(File.ReadAllBytes(hardLinkTarget), hardLinkContent), "硬链接目标内容被错误改写。");
        });
    }

    private static void TestLocalStatusReadUsesOperationLock()
    {
        string caseRoot = NewCaseRoot("local-status-operation-lock");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        Task<PortableLocalStatus> statusTask;
        using (CodexPortableService service = new CodexPortableService(delegate { }))
        {
            using (OperationFileLock held = OperationFileLock.Acquire(installRoot))
            {
                statusTask = service.GetLocalStatusAsync(installRoot, CancellationToken.None);
                Thread.Sleep(400);
                Assert(!statusTask.IsCompleted,
                    "本地状态探测没有等待同一路径的写操作锁。");
            }

            PortableLocalStatus status = statusTask.GetAwaiter().GetResult();
            Assert(status.HasInstallRoot && status.PortableVersion == null,
                "释放操作锁后本地状态探测没有正常完成。");
        }
    }

    private static void TestPackageResolverArtifactPipelineSeparation()
    {
        Assert(typeof(PackageResolver).GetMethod("ResolveLatestAsync", AnyInstance) != null,
            "PackageResolver 缺少元数据解析入口。");
        Assert(typeof(ArtifactPipeline).GetMethod("GetLatestPackageAsync", AnyInstance) == null,
            "ArtifactPipeline 不应继续负责查询最新版本。");
        Assert(typeof(ArtifactPipeline).GetField("packageSource", AnyInstance) == null,
            "ArtifactPipeline 不应持有 Microsoft Store Source。");
        MethodInfo download = typeof(ArtifactPipeline).GetMethod("DownloadOfficialPackageAsync", AnyInstance);
        Assert(download != null && download.GetParameters().Length > 0 &&
            download.GetParameters()[0].ParameterType == typeof(PackageMetadata),
            "ArtifactPipeline 必须消费已经选择的 PackageMetadata。");

    }

    private static void TestVerifiedArtifactLeaseOutlivesCacheLock()
    {
        string caseRoot = NewCaseRoot("verified-artifact-lease");
        string packagePath = Path.Combine(caseRoot, "official.msix");
        File.WriteAllBytes(packagePath, Enumerable.Range(0, 256).Select(value => (byte)value).ToArray());
        FileStream lockedStream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        VerifiedArtifactLease lease = new VerifiedArtifactLease(packagePath, lockedStream);
        try
        {
            using (CacheFileLock cacheLock = CacheFileLock.Acquire(packagePath)) { }

            Exception writeFailure = CaptureFailure(delegate
            {
                using (FileStream output = new FileStream(packagePath, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    output.WriteByte(0xFF);
                }
            });
            Assert(writeFailure is IOException,
                "缓存发布锁释放后，已验证制品租约仍应拒绝写入或替换底层文件。");
        }
        finally
        {
            lease.Dispose();
        }

        using (FileStream output = new FileStream(packagePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            output.WriteByte(0x7F);
        }
    }

    private static void TestOperationControllerStateMachine()
    {
        OperationSnapshot snapshot = new OperationSnapshot(
            @"C:\Portable\Codex",
            true,
            false,
            true,
            false,
            7);
        OperationController controller = new OperationController();
        try
        {
            OperationUiState initialState = controller.State;
            Assert(!initialState.Busy,
                "OperationController 初始状态不应忙碌。");

            OperationContext context;
            bool started = controller.TryBegin(snapshot, true, false, out context);
            Assert(started && context != null, "OperationController 未能开始首个操作。");
            CancellationToken token = context.Token;
            OperationUiState runningState = controller.State;
            Assert(runningState.Busy && runningState.CanCancel && !runningState.LocksInterface,
                "可取消且不锁界面的运行状态不正确。");
            UiState runningUi = CreateUiState(runningState, true, true, true, true, true);
            Assert(runningUi.InputEnabled && !runningUi.CheckEnabled && runningUi.CancelEnabled,
                "运行时 UiState 没有同时保持输入、禁用命令并开放取消。");

            controller.SetCancellationAvailability(false);
            Assert(!controller.State.CanCancel &&
                controller.GetClosingMessage().IndexOf("不能安全取消", StringComparison.Ordinal) >= 0,
                "临时不可取消阶段没有关闭取消入口或更新关闭提示。");
            controller.SetCancellationAvailability(true);
            Assert(controller.State.CanCancel,
                "临时不可取消阶段结束后没有恢复取消入口。");

            controller.SetPauseAvailability(true);
            CancellationToken firstInterruption = controller.PauseToken.InterruptionToken;
            CancellationToken firstRetry = controller.PauseToken.RetryInterruptionToken;
            Assert(controller.RequestDownloadRetry() && firstRetry.IsCancellationRequested &&
                !controller.PauseToken.RetryInterruptionToken.IsCancellationRequested,
                "立即重试没有中断旧网络探测并创建新请求代次。");
            bool paused = controller.TogglePause();
            OperationUiState pausedState = controller.State;
            UiState pausedUi = CreateUiState(pausedState, true, true, true, true, true);
            Assert(paused &&
                pausedState.CanPause && pausedState.IsPaused &&
                pausedUi.PauseEnabled && pausedUi.PauseActive,
                "下载暂停状态没有统一传播到 OperationController 和 UiState。");
            Assert(firstInterruption.IsCancellationRequested,
                "暂停没有中断正在进行的网络读取。");
            Assert(!controller.TogglePause(),
                "继续下载后暂停状态没有解除。");
            Assert(!controller.PauseToken.InterruptionToken.IsCancellationRequested,
                "继续下载后没有创建新的网络请求代次。");

            OperationContext rejectedContext;
            Assert(!controller.TryBegin(snapshot, true, true, out rejectedContext),
                "OperationController 忙碌时不应接受第二个操作。");
            Assert(controller.RequestCancellation(),
                "首次取消请求没有被 OperationController 接受。");
            Assert(token.IsCancellationRequested, "取消请求没有传播到操作快照对应的 token。");
            OperationUiState cancellationState = controller.State;
            Assert(cancellationState.CancellationRequested,
                "UI 状态没有反映已提交的取消请求。");
            UiState cancellationUi = CreateUiState(cancellationState, true, true, true, true, true);
            Assert(!cancellationState.CanCancel && !cancellationState.CanPause &&
                !cancellationUi.CancelEnabled && !cancellationUi.PauseEnabled,
                "取消请求提交后仍暴露取消或暂停入口。");
            Assert(!controller.RequestCancellation(),
                "重复取消请求不应再次改变状态。");
            Assert(!controller.TogglePause(),
                "取消请求提交后不应再次进入暂停状态。");

            Assert(!controller.TryEnterNonCancelablePhase(),
                "取消请求成立后仍进入了不可取消提交阶段。");

            controller.Complete();
            OperationContext commitContext;
            Assert(controller.TryBegin(snapshot, true, true, out commitContext),
                "OperationController 未能开始提交阶段测试操作。");
            Assert(controller.TryEnterNonCancelablePhase(),
                "未取消的操作无法原子进入不可取消提交阶段。");
            controller.SetCancellationAvailability(true);
            OperationUiState committedState = controller.State;
            Assert(!committedState.CanCancel && committedState.LocksInterface,
                "进入不可取消阶段后 UI 状态没有锁定。");
            UiState committedUi = CreateUiState(committedState, true, true, true, true, true);
            Assert(!committedUi.InputEnabled && !committedUi.CancelEnabled,
                "不可取消阶段的 UiState 没有锁定输入并关闭取消入口。");
            string closingMessage = controller.GetClosingMessage();
            Assert(closingMessage.IndexOf("不能安全取消", StringComparison.Ordinal) >= 0,
                "不可取消提交阶段的关闭提示不正确。");

            controller.Complete();
            OperationUiState idleState = controller.State;
            Assert(!idleState.Busy,
                "操作完成后忙碌状态没有清除。");
            UiState previousOnlyUi = CreateUiState(idleState, true, false, true, false, true);
            Assert(!previousOnlyUi.RollbackEnabled && previousOnlyUi.UninstallEnabled,
                "仅剩 .previous 时不应开放版本交换，但应允许删除遗留回滚备份。");
            UiState cleanupPendingUi = CreateUiState(
                idleState,
                true,
                false,
                false,
                false,
                true,
                true);
            Assert(!cleanupPendingUi.InputEnabled &&
                cleanupPendingUi.CheckEnabled &&
                !cleanupPendingUi.InstallEnabled &&
                !cleanupPendingUi.OpenFolderEnabled,
                "卸载清理待办期间应锁定路径切换和目录重建，但仍允许重新检查并继续恢复。");
            UiState oldBackupCleanupPendingUi = CreateUiState(
                idleState,
                true,
                true,
                true,
                false,
                true,
                true);
            Assert(oldBackupCleanupPendingUi.OpenFolderEnabled,
                "仅旧回滚备份待清理且当前版本可用时，应允许打开现有安装目录。");
        }
        finally
        {
            controller.Dispose();
        }
    }

    private static void TestVerifiedPackageCopyCancellation()
    {
        string caseRoot = NewCaseRoot("verified-copy-cancel");
        string sourcePath = Path.Combine(caseRoot, "source.msix");
        string destinationPath = Path.Combine(caseRoot, "saved.msix");
        byte[] source = new byte[3 * 1024 * 1024];
        for (int index = 0; index < source.Length; index++)
        {
            source[index] = (byte)(index % 251);
        }
        byte[] originalDestination = Encoding.UTF8.GetBytes("existing verified package");
        File.WriteAllBytes(sourcePath, source);
        File.WriteAllBytes(destinationPath, originalDestination);

        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            int progressReports = 0;
            Exception failure = CaptureFailure(delegate
            {
                ArtifactPipeline.CopyVerifiedPackageAsync(
                    sourcePath,
                    destinationPath,
                    new DirectProgress<OperationProgress>(value =>
                    {
                        if (Interlocked.Increment(ref progressReports) == 1)
                        {
                            cancellation.Cancel();
                        }
                    }),
                    cancellation.Token).GetAwaiter().GetResult();
            });
            Assert(failure is OperationCanceledException && progressReports == 1,
                "官方 MSIX 保存复制没有在首个分块后响应取消。实际异常：" +
                (failure == null ? "无" : failure.ToString()));
        }

        Assert(BytesEqual(File.ReadAllBytes(destinationPath), originalDestination),
            "取消保存官方 MSIX 时覆盖了原目标文件。");
        Assert(!Directory.EnumerateFiles(
            caseRoot,
            "saved.msix.download-*.msix",
            SearchOption.TopDirectoryOnly).Any(),
            "取消保存官方 MSIX 后遗留了未验证临时文件。");

        File.WriteAllBytes(destinationPath, originalDestination);
        using (CancellationTokenSource commitCancellation = new CancellationTokenSource())
        {
            bool commitBoundaryObserved = false;
            Exception failure = CaptureFailure(delegate
            {
                ArtifactPipeline.CopyVerifiedPackageAsync(
                    sourcePath,
                    destinationPath,
                    new DirectProgress<OperationProgress>(value =>
                    {
                        if (value != null && !value.CanCancel)
                        {
                            commitBoundaryObserved = true;
                            commitCancellation.Cancel();
                        }
                    }),
                    commitCancellation.Token).GetAwaiter().GetResult();
            });
            Assert(commitBoundaryObserved && failure is OperationCanceledException,
                "保存官方 MSIX 进入提交边界后没有在替换目标前兑现已受理的取消。");
        }
        Assert(BytesEqual(File.ReadAllBytes(destinationPath), originalDestination) &&
            !Directory.EnumerateFiles(
                caseRoot,
                "saved.msix.download-*.msix",
                SearchOption.TopDirectoryOnly).Any(),
            "提交边界取消后覆盖了原目标或遗留了临时文件。");

        ArtifactPipeline.CopyVerifiedPackageAsync(
            sourcePath,
            destinationPath,
            new DirectProgress<OperationProgress>(delegate { }),
            CancellationToken.None).GetAwaiter().GetResult();
        Assert(BytesEqual(File.ReadAllBytes(destinationPath), source),
            "未取消的官方 MSIX 分块复制没有原子替换目标文件。");
    }

    private static void TestUiStateOwnsCompleteControlMatrix()
    {
        using (OperationController controller = new OperationController())
        {
            OperationUiState idle = controller.State;
            CompatibilitySwitchFacts knownFacts = new CompatibilitySwitchFacts(
                false,
                true,
                false,
                true);
            UiState enabled = UiState.Create(new UiStateInput(
                idle,
                true,
                true,
                true,
                false,
                true,
                false,
                false,
                true,
                knownFacts,
                true));
            Assert(enabled.CheckEnabled &&
                enabled.ApplyCompatibilityEnabled &&
                enabled.CheckCompatibilityStatusEnabled &&
                enabled.SandboxCompatibilityEnabled &&
                enabled.UnlockModelCatalogEnabled &&
                enabled.SupplementChineseUiEnabled &&
                enabled.EnglishTechnicalParametersEnabled,
                "UiState 没有统一开放可用的命令和兼容开关。");

            UiState backgroundCleanup = UiState.Create(new UiStateInput(
                idle,
                true,
                true,
                false,
                false,
                true,
                false,
                true,
                true,
                knownFacts,
                true));
            Assert(!backgroundCleanup.CheckEnabled &&
                backgroundCleanup.ApplyCompatibilityEnabled,
                "后台卸载清理状态没有只禁止重新检查，或错误禁用了独立兼容操作。");

            UiState unknownCompatibility = UiState.Create(new UiStateInput(
                idle,
                true,
                true,
                false,
                false,
                true,
                false,
                false,
                true,
                new CompatibilitySwitchFacts(false, null, null, true),
                false));
            Assert(unknownCompatibility.SandboxCompatibilityEnabled &&
                !unknownCompatibility.UnlockModelCatalogEnabled &&
                !unknownCompatibility.SupplementChineseUiEnabled &&
                unknownCompatibility.EnglishTechnicalParametersEnabled &&
                !unknownCompatibility.ApplyCompatibilityEnabled &&
                unknownCompatibility.CheckCompatibilityStatusEnabled,
                "UiState 没有按现场事实独立控制兼容开关和应用命令。");

            UiState cachedRollbackOnly = UiState.Create(new UiStateInput(
                idle,
                true,
                true,
                false,
                false,
                true,
                false,
                false,
                false,
                null,
                false,
                true));
            Assert(cachedRollbackOnly.RollbackEnabled &&
                cachedRollbackOnly.UninstallEnabled,
                "只有缓存低版本时，回滚或当前便携版卸载被错误禁用。");

            UiState cachedRollbackWithoutCurrent = UiState.Create(new UiStateInput(
                idle,
                true,
                false,
                false,
                false,
                true,
                false,
                false,
                false,
                null,
                false,
                true));
            Assert(!cachedRollbackWithoutCurrent.RollbackEnabled &&
                !cachedRollbackWithoutCurrent.UninstallEnabled,
                "缓存低版本被错误当作活动版本或 .previous 备份参与卸载。");
        }

        MainWindow window = null;
        try
        {
            window = new MainWindow(false, false, new ManagerSettings
            {
                InstallRoot = Path.Combine(NewCaseRoot("cached-rollback-ui"), "Codex")
            })
            {
                ShowInTaskbar = false,
                ShowActivated = false
            };
            window.Show();
            typeof(MainWindow).GetField("statusMatchesCurrentPath", AnyInstance)
                .SetValue(window, true);
            typeof(MainWindow).GetField("portableVersionAvailable", AnyInstance)
                .SetValue(window, true);
            typeof(MainWindow).GetField("previousVersionAvailable", AnyInstance)
                .SetValue(window, false);
            typeof(MainWindow).GetField("cachedRollbackVersionAvailable", AnyInstance)
                .SetValue(window, true);
            typeof(MainWindow).GetMethod("ApplyUiState", AnyInstance)
                .Invoke(window, null);

            System.Windows.Controls.Button rollback =
                (System.Windows.Controls.Button)window.FindName("rollbackButton");
            System.Windows.Controls.Button uninstall =
                (System.Windows.Controls.Button)window.FindName("uninstallPortableButton");
            Assert(rollback.IsEnabled,
                "只有缓存低版本时，窗口没有开放回滚命令。");
            Assert(string.Equals(
                    uninstall.Content as string,
                    "卸载当前便携版",
                    StringComparison.Ordinal),
                "缓存低版本被窗口误报为卸载时会删除的 .previous 回滚备份。");
        }
        finally
        {
            if (window != null) window.Close();
        }
    }

    private static UiState CreateUiState(
        OperationUiState operation,
        bool statusMatchesCurrentPath,
        bool portableVersionAvailable,
        bool previousVersionAvailable,
        bool storeVersionInstalled,
        bool hasInstallRoot,
        bool deploymentCleanupPending = false)
    {
        return UiState.Create(new UiStateInput(
            operation,
            statusMatchesCurrentPath,
            portableVersionAvailable,
            previousVersionAvailable,
            storeVersionInstalled,
            hasInstallRoot,
            deploymentCleanupPending,
            false,
            false,
            null,
            false));
    }

    private static void TestVisibleProgressDoesNotRegressAfterDownload()
    {
        OperationProgress preparing = new OperationProgress("查询版本", 2, null, false, 2);
        OperationProgress downloading = new OperationProgress("下载程序包", 28, null, true, 40);
        OperationProgress verifying = new OperationProgress("验证程序包", 58, null, false, 58);
        OperationProgress completed = new OperationProgress("下载完成", 100, null, false, 100);

        Assert(MainWindow.ResolveVisibleProgressPercent(preparing, false) == 2,
            "进入下载前的总体进度没有正常显示。");
        Assert(MainWindow.ResolveVisibleProgressPercent(downloading, false) == 40,
            "下载阶段没有显示真实文件下载百分比。");
        Assert(MainWindow.ResolveVisibleProgressPercent(verifying, true) == null,
            "下载结束后的校验阶段仍回退显示较低的总体百分比。");
        Assert(MainWindow.ResolveVisibleProgressPercent(completed, true) == 100,
            "任务完成后没有恢复明确的 100% 终态。");
    }

    private static void TestOperationFailureExpandsAggregateReasons()
    {
        AggregateException failure = new AggregateException(
            "事务失败",
            new IOException("版本目录切换失败"),
            new InvalidOperationException("恢复原版本失败"));
        string message = MainWindow.FormatOperationFailure(failure);
        Assert(message.IndexOf("版本目录切换失败", StringComparison.Ordinal) >= 0 &&
            message.IndexOf("恢复原版本失败", StringComparison.Ordinal) >= 0 &&
            message.IndexOf("事务失败", StringComparison.Ordinal) < 0,
            "AggregateException 仍只显示外层泛化错误。");
    }

    private static void TestExplorerLaunchQuotesDirectoryPath()
    {
        string directoryPath = @"C:\Program Files\Codex Portable Manager\logs";
        ProcessStartInfo startInfo = CodexPortableService.CreateExplorerStartInfo(directoryPath);
        Assert(string.Equals(startInfo.FileName, "explorer.exe", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(startInfo.Arguments, "\"" + directoryPath + "\"", StringComparison.Ordinal) &&
            startInfo.UseShellExecute,
            "资源管理器目录参数没有统一使用完整路径和引号。");

        bool rejectedBlank = false;
        try
        {
            CodexPortableService.CreateExplorerStartInfo(" ");
        }
        catch (ArgumentException)
        {
            rejectedBlank = true;
        }
        Assert(rejectedBlank, "资源管理器启动入口接受了空目录。");
    }

    private static void TestOpenInstallFolderDoesNotRecreatePendingRoot()
    {
        string caseRoot = NewCaseRoot("open-folder-pending-cleanup");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        File.WriteAllText(DeploymentJournal.GetPath(installRoot), "pending", Encoding.ASCII);

        bool rejected = false;
        using (CodexPortableService service = new CodexPortableService(delegate { }))
        {
            try
            {
                service.OpenInstallFolder(installRoot);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
        }

        Assert(rejected, "打开目录没有拒绝部署清理待办。");
        Assert(!Directory.Exists(installRoot),
            "打开目录在部署清理待办期间重新创建了活动安装根。");
    }

    private static void TestRunProcessExitWinsLateCancellation()
    {
        string pwsh = "pwsh.exe";
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            Task<ProcessResult> task = CodexPortableService.RunProcessAsync(
                pwsh,
                "-NoProfile -NonInteractive -Command \"[Console]::Out.WriteLine('CPM_STDOUT'); [Console]::Error.WriteLine('CPM_STDERR'); exit 7\"",
                cancellation.Token);
            ProcessResult result = task.GetAwaiter().GetResult();
            cancellation.Cancel();

            Assert(result.ExitCode == 7, "正常退出代码被迟到取消覆盖。");
            Assert(result.StandardOutput.Contains("CPM_STDOUT"),
                "标准输出没有完整收集。");
            Assert(result.StandardError.Contains("CPM_STDERR"),
                "标准错误没有完整收集。");
        }
    }

    private static void TestRunProcessCancellationWaitsForExit()
    {
        string caseRoot = NewCaseRoot("run-process-cancellation");
        string pidPath = Path.Combine(caseRoot, "child.pid");
        string pwsh = "pwsh.exe";
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            string command = "$PID | Set-Content -LiteralPath '" + pidPath.Replace("'", "''") + "' -Encoding Ascii; Start-Sleep -Seconds 30";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            Task<ProcessResult> task = CodexPortableService.RunProcessAsync(
                pwsh,
                "-NoProfile -NonInteractive -EncodedCommand " + encoded,
                cancellation.Token);

            Stopwatch startup = Stopwatch.StartNew();
            int processId;
            while (!TryReadProcessId(pidPath, out processId) &&
                !task.IsCompleted &&
                startup.Elapsed < TimeSpan.FromSeconds(8))
            {
                Thread.Sleep(20);
            }
            Assert(processId > 0, "子进程没有写出可读取的 PID，无法验证取消后的进程状态。");

            Stopwatch cancellationWait = Stopwatch.StartNew();
            cancellation.Cancel();
            bool canceled = false;
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            cancellationWait.Stop();
            Assert(canceled, "取消请求没有以 OperationCanceledException 完成。");
            Assert(cancellationWait.Elapsed < TimeSpan.FromSeconds(8), "取消子进程等待时间过长。");

            try
            {
                using (Process child = Process.GetProcessById(processId))
                {
                    Assert(child.HasExited, "取消任务完成时子进程仍在运行。");
                }
            }
            catch (ArgumentException)
            {
                // 进程已经退出且从进程表中移除。
            }
        }
    }

    private static bool TryReadProcessId(string path, out int processId)
    {
        processId = 0;
        try
        {
            using (FileStream input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(input, Encoding.ASCII, false))
            {
                return int.TryParse(
                    reader.ReadToEnd().Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out processId) &&
                    processId > 0;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TestStorePackageDetectionFiltersIdentity()
    {
        FakeStorePackageGateway gateway = new FakeStorePackageGateway();
        gateway.Packages.Add(new StorePackageRegistration
        {
            Name = CodexMicrosoftStoreSource.PackageName,
            FamilyName = "OpenAI.Codex_untrusted",
            FullName = "OpenAI.Codex_1.0.0.0_x64__untrusted"
        });
        gateway.Packages.Add(CreateTrustedStorePackage("1.2.3.4", @"C:\Program Files\WindowsApps\OpenAI.Codex"));
        StorePackageLifecycle lifecycle = new StorePackageLifecycle(
            gateway,
            delegate { throw new InvalidOperationException("检测不应停止进程。"); },
            delegate { throw new InvalidOperationException("检测不应等待进程。"); },
            delegate { });

        bool installed = lifecycle.IsInstalledAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert(installed, "官方 Codex 包没有被识别为已安装。");
        Assert(gateway.QueryCount == 1, "Store 包检测没有只查询一次当前用户登记。");
        Assert(gateway.LastPackageName == CodexMicrosoftStoreSource.PackageName, "Store 包检测使用了错误的包名。");

        gateway.Packages.RemoveAt(1);
        installed = lifecycle.IsInstalledAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert(!installed, "非官方包族不应被识别为 Codex 官方桌面版。");

        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            int queriesBeforeCancellation = gateway.QueryCount;
            bool canceled = false;
            try
            {
                lifecycle.IsInstalledAsync(cancellation.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            Assert(canceled, "已取消的 Store 包检测没有返回取消结果。");
            Assert(gateway.QueryCount == queriesBeforeCancellation, "已取消的 Store 包检测仍访问了包管理器。");
        }
    }

    private static void TestTargetFrameworkBaseline()
    {
        object[] attributes = typeof(CodexPortableService).Assembly.GetCustomAttributes(
            typeof(System.Runtime.Versioning.TargetFrameworkAttribute),
            false);
        Assert(attributes.Length == 1, "正式程序集缺少唯一的 TargetFrameworkAttribute。");
        System.Runtime.Versioning.TargetFrameworkAttribute target =
            (System.Runtime.Versioning.TargetFrameworkAttribute)attributes[0];
        Assert(
            string.Equals(target.FrameworkName, ".NETFramework,Version=v4.6.2", StringComparison.Ordinal),
            "正式程序集目标框架不是预期的 .NET Framework 4.6.2：" + target.FrameworkName);

        PortableExecutableKinds peKind;
        ImageFileMachine machine;
        typeof(CodexPortableService).Assembly.ManifestModule.GetPEKind(out peKind, out machine);
        Assert((peKind & PortableExecutableKinds.Required32Bit) == 0,
            "正式程序集仍被标记为强制 32 位：" + peKind);
        Assert((peKind & PortableExecutableKinds.Preferred32Bit) == 0,
            "正式程序集仍偏好以 32 位进程运行：" + peKind);
    }

    private static void TestProcessesUnderPathStops64BitProcess()
    {
        string caseRoot = NewCaseRoot("processes-under-path-x64");
        string fixtureSource = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "CodexPortableManager.PortableExitFixture.exe");
        string fixturePath = Path.Combine(caseRoot, "app", "ChatGPT.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fixturePath));
        File.Copy(fixtureSource, fixturePath, true);

        string previousHold = Environment.GetEnvironmentVariable("CPM_REGRESSION_CHILD_HOLD_MS");
        Process child = null;
        try
        {
            Environment.SetEnvironmentVariable("CPM_REGRESSION_CHILD_HOLD_MS", "30000");
            child = Process.Start(new ProcessStartInfo(fixturePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(fixturePath)
            });
            Assert(child != null, "无法启动 64 位目录进程夹具。");
            Thread.Sleep(300);
            Assert(ProcessesUnderPath.FindProcessIds(caseRoot).Contains(child.Id),
                "管理器没有跨位数发现安装目录中的 64 位进程。");

            ProcessesUnderPath.Stop(caseRoot);
            ProcessesUnderPath.WaitForExit(caseRoot, TimeSpan.FromSeconds(5));
            Assert(child.HasExited || child.WaitForExit(1000),
                "管理器没有停止安装目录中的 64 位进程。");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CPM_REGRESSION_CHILD_HOLD_MS", previousHold);
            if (child != null)
            {
                try
                {
                    if (!child.HasExited) child.Kill();
                }
                catch { }
                child.Dispose();
            }
        }
    }

    private static void TestStorePackageUninstallUsesSingleSnapshot()
    {
        string installLocation = @"C:\Program Files\WindowsApps\OpenAI.Codex_1.2.3.4";
        List<string> events = new List<string>();
        List<string> logs = new List<string>();
        FakeStorePackageGateway gateway = new FakeStorePackageGateway(events);
        gateway.Packages.Add(CreateTrustedStorePackage("1.2.3.4", installLocation));
        gateway.Packages.Add(new StorePackageRegistration
        {
            Name = "Other.Package",
            FamilyName = "Other.Package_publisher",
            FullName = "Other.Package_1.0.0.0_x64__publisher",
            InstallLocation = @"C:\Program Files\WindowsApps\Other.Package"
        });
        StorePackageLifecycle lifecycle = new StorePackageLifecycle(
            gateway,
            path => events.Add("stop:" + path),
            (path, timeout) => events.Add("wait:" + path + ":" + timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)),
            logs.Add);

        lifecycle.UninstallAsync(CancellationToken.None).GetAwaiter().GetResult();

        string[] expected =
        {
            "find:" + CodexMicrosoftStoreSource.PackageName,
            "stop:" + installLocation,
            "wait:" + installLocation + ":5",
            "remove:OpenAI.Codex_1.2.3.4_x64__" + CodexMicrosoftStoreSource.PublisherId
        };
        Assert(events.SequenceEqual(expected), "Store 包卸载执行顺序不符合登记快照、关闭进程、原生卸载边界：" + string.Join(" | ", events.ToArray()));
        Assert(gateway.QueryCount == 1, "Store 包卸载重复查询了包登记，可能产生检查与执行竞态。");
        Assert(gateway.RemovedPackages.Count == 1, "Store 包卸载处理了非官方包，或没有处理官方包。");
        Assert(logs.Any(line => line.IndexOf("已卸载", StringComparison.Ordinal) >= 0), "Store 包卸载完成后没有记录成功日志。");
    }

    private static void TestWindowsPackageManagerQuery()
    {
        IStorePackageGateway gateway = new WindowsStorePackageGateway();
        IReadOnlyList<StorePackageRegistration> packages =
            gateway.FindPackagesForCurrentUser(CodexMicrosoftStoreSource.PackageName);

        Assert(packages != null, "WinRT PackageManager 返回了空登记集合。");
        Assert(packages.All(package =>
            package != null &&
            string.Equals(package.Name, CodexMicrosoftStoreSource.PackageName, StringComparison.Ordinal)),
            "WinRT PackageManager 的包名过滤结果不符合请求。");
    }

    private static StorePackageRegistration CreateTrustedStorePackage(string version, string installLocation)
    {
        return new StorePackageRegistration
        {
            Name = CodexMicrosoftStoreSource.PackageName,
            FamilyName = CodexMicrosoftStoreSource.PackageFamilyName,
            FullName = CodexMicrosoftStoreSource.PackageName + "_" + version + "_x64__" + CodexMicrosoftStoreSource.PublisherId,
            InstallLocation = installLocation
        };
    }

    private sealed class FakeStorePackageGateway : IStorePackageGateway
    {
        private readonly List<string> events;

        internal FakeStorePackageGateway()
            : this(new List<string>())
        {
        }

        internal FakeStorePackageGateway(List<string> eventLog)
        {
            events = eventLog;
            Packages = new List<StorePackageRegistration>();
            RemovedPackages = new List<string>();
        }

        internal List<StorePackageRegistration> Packages { get; private set; }
        internal List<string> RemovedPackages { get; private set; }
        internal int QueryCount { get; private set; }
        internal string LastPackageName { get; private set; }

        public IReadOnlyList<StorePackageRegistration> FindPackagesForCurrentUser(string packageName)
        {
            QueryCount++;
            LastPackageName = packageName;
            events.Add("find:" + packageName);
            return Packages.ToArray();
        }

        public Task RemovePackageForCurrentUserAsync(
            string packageFullName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemovedPackages.Add(packageFullName);
            events.Add("remove:" + packageFullName);
            return Task.FromResult(0);
        }
    }

    private static void TestPortableImmediateZeroExitIsAccepted()
    {
        TimeSpan elapsed;
        Exception failure = StartPortableWithImmediateExit(0, out elapsed);

        Assert(failure == null,
            "便携版启动进程以代码 0 正常退出时不应误报启动失败。实际：" + (failure == null ? "无异常" : failure.ToString()));
        Assert(elapsed < TimeSpan.FromSeconds(5), "便携版零代码立即退出检查耗时异常。");
    }

    private static void TestPortableImmediateFailureIsRejected()
    {
        TimeSpan elapsed;
        Exception failure = StartPortableWithImmediateExit(7, out elapsed);

        Assert(failure is InvalidOperationException &&
            failure.Message.IndexOf("立即异常退出", StringComparison.Ordinal) >= 0 &&
            failure.Message.IndexOf("退出代码：7", StringComparison.Ordinal) >= 0,
            "便携版启动进程非零退出时没有返回明确失败。实际：" + (failure == null ? "无异常" : failure.ToString()));
        Assert(elapsed < TimeSpan.FromSeconds(5), "便携版非零立即退出检查耗时异常。");
    }

    private static Exception StartPortableWithImmediateExit(int exitCode, out TimeSpan elapsed)
    {
        string installRoot = Path.Combine(NewCaseRoot("portable-immediate-exit-" + exitCode.ToString(CultureInfo.InvariantCulture)), "CodexDesktop");
        CreateMinimalCodex(installRoot, "1.0.0.0", Guid.NewGuid().ToString("N"), "immediate-exit");
        string appRoot = Path.Combine(installRoot, "app");
        string resourcesRoot = Path.Combine(appRoot, "resources");
        Directory.CreateDirectory(resourcesRoot);
        string exitFixturePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "CodexPortableManager.PortableExitFixture.exe");
        if (!File.Exists(exitFixturePath))
        {
            throw new FileNotFoundException("缺少便携启动退出测试夹具。", exitFixturePath);
        }
        File.Copy(exitFixturePath, Path.Combine(appRoot, "Codex.exe"), true);
        File.WriteAllText(Path.Combine(resourcesRoot, "app.asar"), "asar", Encoding.ASCII);
        File.WriteAllText(Path.Combine(resourcesRoot, "codex.exe"), "codex", Encoding.ASCII);

        CodexPortableService service = CreateService(new List<string>());
        Exception failure = null;
        string previousChildExitCode = Environment.GetEnvironmentVariable("CPM_REGRESSION_CHILD_EXIT_CODE");
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            Environment.SetEnvironmentVariable("CPM_REGRESSION_CHILD_EXIT_CODE", exitCode.ToString(CultureInfo.InvariantCulture));
            service.StartPortable(installRoot);
        }
        catch (Exception exception)
        {
            failure = Unwrap(exception);
        }
        finally
        {
            stopwatch.Stop();
            Environment.SetEnvironmentVariable("CPM_REGRESSION_CHILD_EXIT_CODE", previousChildExitCode);
            service.Dispose();
        }

        elapsed = stopwatch.Elapsed;
        return failure;
    }
}
}
