using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPortableManager
{
    internal sealed class CodexPortableService : IDisposable
    {
        private readonly PackageResolver packageResolver;
        private readonly ArtifactPipeline artifactPipeline;
        private readonly DeploymentEngine deploymentEngine;
        private readonly CompatibilityCoordinator compatibilityCoordinator;
        private readonly CompatibilityMaintenance compatibilityMaintenance;
        private readonly ShellIntegrationCoordinator shellIntegrationCoordinator;
        private readonly StorePackageLifecycle storePackageLifecycle;
        private readonly Action<string> stopProcesses;
        private readonly Action<string, TimeSpan> waitForProcesses;
        private readonly Action<string> log;

        public CodexPortableService(Action<string> logAction)
            : this(logAction, ProcessesUnderPath.Stop, ProcessesUnderPath.WaitForExit)
        {
        }

        internal CodexPortableService(
            Action<string> logAction,
            Action<string> stopProcessesAction,
            Action<string, TimeSpan> waitForProcessesAction)
        {
            log = logAction ?? delegate { };
            stopProcesses = stopProcessesAction ?? throw new ArgumentNullException(nameof(stopProcessesAction));
            waitForProcesses = waitForProcessesAction ?? throw new ArgumentNullException(nameof(waitForProcessesAction));
            packageResolver = new PackageResolver(log);
            artifactPipeline = new ArtifactPipeline(log, RunProcessAsync);
            compatibilityCoordinator = new CompatibilityCoordinator(log);
            compatibilityMaintenance = new CompatibilityMaintenance(compatibilityCoordinator, log);
            shellIntegrationCoordinator = new ShellIntegrationCoordinator(log);
            storePackageLifecycle = new StorePackageLifecycle(log);
            deploymentEngine = new DeploymentEngine(
                log,
                artifactPipeline,
                compatibilityCoordinator,
                shellIntegrationCoordinator);
        }

        public async Task<PackageMetadata> GetLatestPackageAsync(CancellationToken cancellationToken)
        {
            return await packageResolver.ResolveLatestAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> DownloadOfficialPackageAsync(
            string destinationPath,
            IProgress<OperationProgress> progress,
            OperationPauseToken pauseToken,
            CancellationToken cancellationToken)
        {
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            try
            {
                progress.Report(new OperationProgress("查询微软最新版本", 2, "正在连接微软官方程序包服务。"));
                PackageMetadata package = await packageResolver.ResolveLatestAsync(cancellationToken).ConfigureAwait(false);
                return await artifactPipeline.DownloadOfficialPackageAsync(
                    package,
                    destinationPath,
                    progress,
                    pauseToken,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                RunStorageMaintenanceBestEffort();
            }
        }

        public Version GetPortableVersion(string installRoot)
        {
            try
            {
                PackageProfile profile;
                string validationError;
                if (!InstallOwnership.TryValidateRunnableCodexPayload(installRoot, out profile, out validationError))
                {
                    return null;
                }
                Version parsed;
                return Version.TryParse(profile.Version, out parsed) ? parsed : null;
            }
            catch
            {
                return null;
            }
        }

        public string GetPortableApplicationVersion(string installRoot)
        {
            try
            {
                PackageProfile profile;
                string validationError;
                if (!InstallOwnership.TryValidateRunnableCodexPayload(installRoot, out profile, out validationError))
                {
                    return null;
                }
                string executablePath = PackageProfileReader.GetExecutablePath(installRoot, profile);
                string asarPath = Path.Combine(Path.GetDirectoryName(executablePath), "resources", "app.asar");
                return AsarPackageMetadata.ReadApplicationVersion(asarPath);
            }
            catch
            {
                return null;
            }
        }

        public bool RequiresLegacyAdoption(string installRoot)
        {
            installRoot = DeploymentEngine.ValidateInstallRoot(installRoot);
            return InstallOwnership.RequiresLegacyAdoption(installRoot) ||
                InstallOwnership.RequiresLegacyAdoption(installRoot + ".previous");
        }

        public bool IsPreviousVersionAvailable(string installRoot)
        {
            try
            {
                PackageProfile profile;
                string validationError;
                return InstallOwnership.TryValidateRunnableCodexPayload(
                    installRoot + ".previous",
                    out profile,
                    out validationError);
            }
            catch { return false; }
        }

        public bool IsCachedRollbackVersionAvailable(string installRoot)
        {
            try
            {
                Version currentVersion = GetPortableVersion(installRoot);
                return RollbackPackageSelector.Select(
                    PortableStorage.CacheRoot,
                    currentVersion,
                    null,
                    CodexMicrosoftStoreSource.GetCurrentArchitecture()) != null;
            }
            catch { return false; }
        }

        public async Task<PortableLocalStatus> GetLocalStatusAsync(
            string installRoot,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                return new PortableLocalStatus(null, null, false, null, false);
            }

            try
            {
                string root = DeploymentEngine.ValidateInstallRoot(installRoot);
                using (OperationFileLock operationLock = await OperationFileLock
                    .AcquireAsync(root, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return await Task.Run(
                        () => GetLocalStatusCore(root),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new PortableLocalStatus(null, null, false, exception.Message, true);
            }
        }

        internal PortableLocalStatus GetLocalStatus(string installRoot)
        {
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                return new PortableLocalStatus(null, null, false, null, false);
            }

            try
            {
                string root = DeploymentEngine.ValidateInstallRoot(installRoot);
                using (OperationFileLock operationLock = OperationFileLock.Acquire(root))
                {
                    return GetLocalStatusCore(root);
                }
            }
            catch (Exception exception)
            {
                return new PortableLocalStatus(null, null, false, exception.Message, true);
            }
        }

        private PortableLocalStatus GetLocalStatusCore(string root)
        {
            DeploymentRecoveryResult recovery = new DeploymentRecoveryResult(false, false);
            if (DeploymentJournal.Exists(root) || CompatibilityTransaction.Exists(root))
            {
                recovery = deploymentEngine.RecoverPendingDeploymentUnderLock(root);
            }
            PackageProfile profile;
            string validationError;
            Version portableVersion = null;
            string applicationVersion = null;
            if (InstallOwnership.TryValidateRunnableCodexPayload(root, out profile, out validationError) &&
                Version.TryParse(profile.Version, out portableVersion))
            {
                try
                {
                    string executablePath = PackageProfileReader.GetExecutablePath(root, profile);
                    string asarPath = Path.Combine(Path.GetDirectoryName(executablePath), "resources", "app.asar");
                    applicationVersion = AsarPackageMetadata.ReadApplicationVersion(asarPath);
                }
                catch
                {
                    applicationVersion = null;
                }
            }

            bool shellIntegrationCleanupPending = false;
            try
            {
                if (ShellIntegration.IsCleanupPendingForRoot(root))
                {
                    shellIntegrationCoordinator.RecoverPendingCleanup();
                }
                shellIntegrationCleanupPending = ShellIntegration.IsCleanupPendingForRoot(root);
            }
            catch
            {
                shellIntegrationCleanupPending = true;
            }

            return new PortableLocalStatus(
                portableVersion,
                applicationVersion,
                IsPreviousVersionAvailable(root),
                null,
                true,
                recovery.OldBackupCleanupPending,
                recovery.UninstallDirectoryCleanupPending,
                shellIntegrationCleanupPending,
                IsCachedRollbackVersionAvailable(root));
        }

        public Task<bool> IsStorePackageInstalledAsync(CancellationToken cancellationToken)
        {
            return storePackageLifecycle.IsInstalledAsync(cancellationToken);
        }

        public async Task<PortableStatus> GetStatusAsync(string installRoot, CancellationToken cancellationToken)
        {
            Task<PortableLocalStatus> localTask = GetLocalStatusAsync(installRoot, cancellationToken);
            return await GetStatusAsync(localTask, cancellationToken).ConfigureAwait(false);
        }

        internal async Task<PortableStatus> GetStatusAsync(
            Task<PortableLocalStatus> localTask,
            CancellationToken cancellationToken)
        {
            if (localTask == null) throw new ArgumentNullException(nameof(localTask));
            Task<bool> storeTask = Task.Run(
                async () => await IsStorePackageInstalledAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken);
            Task<PackageMetadata> packageTask = GetLatestPackageAsync(cancellationToken);
            StorePackageState storeState = StorePackageState.Unknown;
            string storeDetectionError = null;
            try
            {
                storeState = await storeTask.ConfigureAwait(false)
                    ? StorePackageState.Installed
                    : StorePackageState.NotInstalled;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                storeDetectionError = exception.Message;
                log("官方桌面版检测警告：" + exception.Message);
            }

            PackageMetadata latestPackage = await packageTask.ConfigureAwait(false);
            PortableLocalStatus localStatus = await localTask.ConfigureAwait(false);
            return new PortableStatus
            {
                StoreState = storeState,
                StoreDetectionError = storeDetectionError,
                PortableVersion = localStatus.PortableVersion,
                PortableApplicationVersion = localStatus.PortableApplicationVersion,
                LatestPackage = latestPackage,
                PreviousVersionAvailable = localStatus.PreviousVersionAvailable,
                CachedRollbackVersionAvailable = localStatus.CachedRollbackVersionAvailable
            };
        }

        public async Task<DeploymentResult> InstallOrUpdateAsync(
            string installRoot,
            bool force,
            IProgress<OperationProgress> progress,
            OperationPauseToken pauseToken,
            CancellationToken cancellationToken,
            bool createIntegration,
            LegacyAdoptionApproval adoptionApproval = null)
        {
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            progress.Report(new OperationProgress("查询微软最新版本", 2, "正在连接微软官方程序包服务。"));
            PackageMetadata package = await packageResolver.ResolveLatestAsync(cancellationToken).ConfigureAwait(false);
            return await deploymentEngine.InstallOrUpdateAsync(
                package,
                installRoot,
                force,
                progress,
                pauseToken,
                cancellationToken,
                createIntegration,
                adoptionApproval).ConfigureAwait(false);
        }

        public Task UninstallStorePackageAsync(CancellationToken cancellationToken)
        {
            return storePackageLifecycle.UninstallAsync(cancellationToken);
        }

        public DeploymentResult Rollback(
            string installRoot,
            bool createIntegration,
            LegacyAdoptionApproval adoptionApproval = null)
        {
            return deploymentEngine.Rollback(installRoot, createIntegration, adoptionApproval);
        }

        public Task<DeploymentResult> RollbackAvailableAsync(
            string installRoot,
            IProgress<OperationProgress> progress,
            OperationPauseToken pauseToken,
            CancellationToken cancellationToken,
            bool createIntegration,
            LegacyAdoptionApproval adoptionApproval)
        {
            return deploymentEngine.RollbackAvailableAsync(
                installRoot,
                progress,
                pauseToken,
                cancellationToken,
                createIntegration,
                adoptionApproval);
        }

        public UninstallResult UninstallPortable(string installRoot)
        {
            return UninstallPortable(installRoot, null);
        }

        public UninstallResult UninstallPortable(
            string installRoot,
            LegacyAdoptionApproval adoptionApproval)
        {
            return deploymentEngine.UninstallPortable(installRoot, adoptionApproval);
        }

        public UninstallResult DetachPortableForUninstall(
            string installRoot,
            LegacyAdoptionApproval adoptionApproval)
        {
            return deploymentEngine.DetachPortableForUninstall(
                installRoot,
                adoptionApproval);
        }

        public Task<int> StartUninstallCleanupAsync(string installRoot)
        {
            installRoot = DeploymentEngine.ValidateInstallRoot(installRoot);
            return UninstallCleanupWorker.StartAsync(installRoot, log);
        }

        internal bool CompletePendingUninstallCleanup(string installRoot)
        {
            installRoot = DeploymentEngine.ValidateInstallRoot(installRoot);
            using (OperationFileLock operationLock = OperationFileLock.Acquire(installRoot))
            {
                if (DeploymentJournal.Exists(installRoot) ||
                    CompatibilityTransaction.Exists(installRoot))
                {
                    deploymentEngine.RecoverPendingDeploymentUnderLock(installRoot);
                }
                if (ShellIntegration.IsCleanupPendingForRoot(installRoot))
                {
                    shellIntegrationCoordinator.RecoverPendingCleanup();
                }
                return !DeploymentJournal.Exists(installRoot) &&
                    !ShellIntegration.IsCleanupPendingForRoot(installRoot);
            }
        }

        public IReadOnlyList<string> CreateIntegration(string installRoot)
        {
            installRoot = DeploymentEngine.ValidateInstallRoot(installRoot);
            using (OperationFileLock operationLock = OperationFileLock.Acquire(installRoot))
            {
                deploymentEngine.RecoverPendingCompatibilityMaintenance(installRoot);
                return shellIntegrationCoordinator.Create(installRoot);
            }
        }

        public CompatibilityResult ApplyCompatibilitySettings(
            string installRoot,
            CompatibilityOptions compatibility)
        {
            return ApplyCompatibilitySettings(installRoot, compatibility, null);
        }

        public CompatibilityResult ApplyCompatibilitySettings(
            string installRoot,
            CompatibilityOptions compatibility,
            CompatibilityBaselineApproval baselineApproval)
        {
            if (compatibility == null) throw new ArgumentNullException(nameof(compatibility));
            installRoot = DeploymentEngine.ValidateInstallRoot(installRoot);
            using (OperationFileLock operationLock = OperationFileLock.Acquire(installRoot))
            {
                CompatibilityMaintenancePreflight preflight =
                    compatibilityMaintenance.PreflightApply(installRoot, baselineApproval);
                stopProcesses(installRoot);
                waitForProcesses(installRoot, TimeSpan.FromSeconds(15));
                preflight.EnsureTargetUnchanged();
                return compatibilityMaintenance.Apply(installRoot, compatibility, baselineApproval);
            }
        }

        public InstallationHealthReport GetInstallationHealth(string installRoot)
        {
            installRoot = DeploymentEngine.ValidateInstallRoot(installRoot);
            using (OperationFileLock operationLock = OperationFileLock.Acquire(installRoot))
            {
                return InstallationHealth.Evaluate(installRoot);
            }
        }

        public CompatibilityOverview GetCompatibilityOverview(
            string installRoot,
            bool verifyArtifacts)
        {
            installRoot = DeploymentEngine.ValidateInstallRoot(installRoot);
            using (OperationFileLock operationLock = OperationFileLock.Acquire(installRoot))
            {
                return CompatibilityStatusReader.Read(installRoot, verifyArtifacts);
            }
        }

        public async Task<CompatibilityOverview> GetCompatibilityOverviewAsync(
            string installRoot,
            bool verifyArtifacts,
            CancellationToken cancellationToken)
        {
            installRoot = DeploymentEngine.ValidateInstallRoot(installRoot);
            using (OperationFileLock operationLock = await OperationFileLock
                .AcquireAsync(installRoot, cancellationToken)
                .ConfigureAwait(false))
            {
                return await Task.Run(
                    () => CompatibilityStatusReader.Read(installRoot, verifyArtifacts),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        public void StartPortable(string installRoot)
        {
            installRoot = DeploymentEngine.ValidateInstallRoot(installRoot);
            using (OperationFileLock operationLock = OperationFileLock.Acquire(installRoot))
            {
                deploymentEngine.RecoverPendingCompatibilityMaintenance(installRoot);
                PackageProfile profile;
                string validationError;
                if (!InstallOwnership.TryValidateRunnableCodexPayload(
                    installRoot,
                    out profile,
                    out validationError))
                {
                    throw new InvalidDataException("便携版 Codex 不完整，无法启动：" + validationError);
                }
                string exePath = PackageProfileReader.GetExecutablePath(installRoot, profile);
                // 启动不主动重写用户文件；这里只恢复由本工具 journal 明确拥有的未提交维护事务。
                using (Process process = Process.Start(new ProcessStartInfo(exePath) { WorkingDirectory = Path.GetDirectoryName(exePath) }))
                {
                    if (process == null) throw new InvalidOperationException("无法启动便携版 Codex。");
                    if (process.WaitForExit(500))
                    {
                        int exitCode = process.ExitCode;
                        if (exitCode != 0)
                        {
                            throw new InvalidOperationException("便携版 Codex 启动进程立即异常退出，退出代码：" + exitCode + "。");
                        }

                        log("Codex 启动进程已正常结束（退出代码 0）；可能是用户主动关闭，或启动请求已交给现有 Codex 实例。");
                    }
                }
            }
        }

        public void OpenInstallFolder(string installRoot)
        {
            installRoot = DeploymentEngine.ValidateInstallRoot(installRoot);
            using (OperationFileLock operationLock = OperationFileLock.Acquire(installRoot))
            {
                if (!Directory.Exists(installRoot))
                {
                    bool cleanupPending;
                    try
                    {
                        cleanupPending = DeploymentJournal.Exists(installRoot) ||
                            ShellIntegration.IsCleanupPendingForRoot(installRoot);
                    }
                    catch (Exception exception)
                    {
                        throw new IOException(
                            "无法确认目标目录是否仍有未完成清理，已拒绝重新创建活动安装根。",
                            exception);
                    }
                    if (cleanupPending)
                    {
                        throw new InvalidOperationException(
                            "目标目录仍有未完成的部署或系统集成清理，不能重新创建活动安装根。请先重新检查状态。");
                    }
                    Directory.CreateDirectory(installRoot);
                }
            }
            OpenFolderInExplorer(installRoot);
        }

        internal static void OpenFolderInExplorer(string directoryPath)
        {
            using (Process process = Process.Start(CreateExplorerStartInfo(directoryPath)))
            {
                if (process == null) throw new InvalidOperationException("无法启动 Windows 资源管理器。");
            }
        }

        internal static ProcessStartInfo CreateExplorerStartInfo(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                throw new ArgumentException("待打开目录不能为空。", nameof(directoryPath));
            }

            string fullPath = Path.GetFullPath(directoryPath);
            return new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = Quote(fullPath),
                UseShellExecute = true
            };
        }

        public void MaintainStorage()
        {
            RunStartupMaintenanceBestEffort(
                delegate { shellIntegrationCoordinator.RecoverPendingCleanup(); },
                RunStorageMaintenanceBestEffort,
                log);
        }

        internal static void RunStartupMaintenanceBestEffort(
            Action recoverShellIntegration,
            Action maintainStorage,
            Action<string> log)
        {
            if (recoverShellIntegration == null) throw new ArgumentNullException(nameof(recoverShellIntegration));
            if (maintainStorage == null) throw new ArgumentNullException(nameof(maintainStorage));
            if (log == null) log = delegate { };

            try
            {
                recoverShellIntegration();
            }
            catch (Exception exception)
            {
                log("警告：启动时恢复系统集成清理失败，将在后续操作重试：" + exception.Message);
            }

            try
            {
                maintainStorage();
            }
            catch (Exception exception)
            {
                log("警告：启动时维护管理器存储失败：" + exception.Message);
            }
        }

        public void Dispose()
        {
            artifactPipeline.Dispose();
            packageResolver.Dispose();
        }

        internal static Task<ProcessResult> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
        {
            return RunProcessCoreAsync(fileName, arguments, cancellationToken);
        }

        private static async Task<ProcessResult> RunProcessCoreAsync(string fileName, string arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<bool> exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Process process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };

            StringBuilder output = new StringBuilder();
            StringBuilder error = new StringBuilder();
            object outputGate = new object();
            object errorGate = new object();
            int completionState = 0; // 0=运行中，1=进程先结束，2=取消先到达
            process.OutputDataReceived += (sender, args) =>
            {
                if (args.Data == null) return;
                lock (outputGate) output.AppendLine(args.Data);
            };
            process.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data == null) return;
                lock (errorGate) error.AppendLine(args.Data);
            };
            process.Exited += (sender, args) =>
            {
                bool canceled = Interlocked.CompareExchange(ref completionState, 1, 0) == 2;
                exited.TrySetResult(canceled);
            };

            using (process)
            {
                if (!process.Start()) throw new InvalidOperationException("无法启动子进程：" + fileName);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                using (CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() =>
                {
                    try
                    {
                        if (process.HasExited) return;
                        if (Interlocked.CompareExchange(ref completionState, 2, 0) != 0) return;
                        process.Kill();
                    }
                    catch
                    {
                        // 进程可能已在取消请求到达前退出，退出事件会负责完成结果。
                    }
                }))
                {
                    bool canceled = await exited.Task.ConfigureAwait(false);
                    process.WaitForExit();
                    string standardOutput;
                    string standardError;
                    lock (outputGate) standardOutput = output.ToString();
                    lock (errorGate) standardError = error.ToString();
                    ProcessResult result = new ProcessResult
                    {
                        ExitCode = process.ExitCode,
                        StandardOutput = standardOutput,
                        StandardError = standardError
                    };
                    if (canceled) throw new OperationCanceledException(cancellationToken);
                    return result;
                }
            }
        }

        private void RunStorageMaintenanceBestEffort()
        {
            StorageMaintenance.RunBestEffort(log);
        }
        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }
    }
}
