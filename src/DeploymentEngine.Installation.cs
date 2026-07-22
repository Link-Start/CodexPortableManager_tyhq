using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace CodexPortableManager
{
    internal sealed partial class DeploymentEngine
    {
        public async Task<DeploymentResult> InstallOrUpdateAsync(
            PackageMetadata package,
            string installRoot,
            bool force,
            IProgress<OperationProgress> progress,
            OperationPauseToken pauseToken,
            CancellationToken cancellationToken,
            bool createIntegration,
            LegacyAdoptionApproval adoptionApproval)
        {
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            if (package == null) throw new ArgumentNullException(nameof(package));
            installRoot = ValidateInstallRoot(installRoot);
            using (OperationFileLock operationLock = await OperationFileLock
                .AcquireAsync(installRoot, cancellationToken)
                .ConfigureAwait(false))
            {
                RecoverPendingCompatibilityMaintenance(installRoot);
                return await InstallOrUpdateCoreAsync(
                    package,
                    installRoot,
                    force,
                    progress,
                    pauseToken,
                    cancellationToken,
                    createIntegration,
                    adoptionApproval,
                    false,
                    false).ConfigureAwait(false);
            }
        }

        private async Task<DeploymentResult> InstallOrUpdateCoreAsync(
            PackageMetadata package,
            string installRoot,
            bool force,
            IProgress<OperationProgress> progress,
            OperationPauseToken pauseToken,
            CancellationToken cancellationToken,
            bool createIntegration,
            LegacyAdoptionApproval adoptionApproval,
            bool allowDowngrade,
            bool allowUnknownCompatibility)
        {
            string parentRoot = Directory.GetParent(installRoot).FullName;
            string previousRoot = installRoot + ".previous";
            string installId = PrepareInstallTopology(installRoot, adoptionApproval);

            Version remoteVersion = new Version(package.version);
            Version currentVersion = GetPortableVersion(installRoot);
            string architecture = package.architecture;
            string cacheRoot = PortableStorage.CacheRoot;
            Directory.CreateDirectory(cacheRoot);
            log("目标安装目录：" + installRoot);
            log("当前便携版版本：" + (currentVersion == null ? "未安装" : currentVersion.ToString()) + "；目标版本：" + remoteVersion + "。");
            using (CacheFileLock migrationLock = await CacheFileLock.AcquireAsync(
                Path.Combine(cacheRoot, ".legacy-cache-migration"),
                cancellationToken).ConfigureAwait(false))
            {
                await PortableStorage.MigrateLegacyCacheAsync(progress, cancellationToken).ConfigureAwait(false);
            }
            if (!package.localCacheOnly)
            {
                StorageMaintenance.RunBestEffort(log);
            }

            if (!allowDowngrade && currentVersion != null &&
                (currentVersion > remoteVersion || (!force && currentVersion == remoteVersion)))
            {
                progress.Report(new OperationProgress(
                    "刷新当前版本系统集成",
                    80,
                    "当前版本无需重新解包，正在更新快捷方式、协议和文件关联。"));
                IReadOnlyList<string> integrationWarnings = new string[0];
                if (createIntegration)
                {
                    integrationWarnings = CreateIntegrationCore(installRoot);
                }
                DeploymentResult currentResult = new DeploymentResult(
                    createIntegration,
                    integrationWarnings);
                progress.Report(DeploymentCompletion.ForCurrentVersion(currentVersion, remoteVersion, currentResult));
                return currentResult;
            }

            CompatibilityOptions compatibility = ResolveInheritedCompatibilityOptions(
                installRoot,
                currentVersion,
                allowUnknownCompatibility);

            string workParent;
            string workRoot = CreateWorkRoot(installRoot, parentRoot, out workParent);
            string workRootIdentity =
                InstallOwnership.GetManagedDirectoryIdentity(workRoot);
            string stagingRoot = Path.Combine(workRoot, "s");
            string packagePath = CacheFileLock.GetPackagePath(
                cacheRoot,
                package.packageName,
                package.version,
                architecture);
            string downloadPath = packagePath + ".download-" + Guid.NewGuid().ToString("N") + ".msix";

            try
            {
                StorageMaintenance.WriteWorkMarker(workRoot, installRoot);
                Directory.CreateDirectory(stagingRoot);
                log("程序包缓存：" + packagePath);
                log("临时工作目录：" + workRoot);

                CompatibilityResult compatibilityResult;
                using (StagingBuildResult stagedPackage = await artifactPipeline.PrepareStagedPackageAsync(
                    package,
                    architecture,
                    packagePath,
                    downloadPath,
                    stagingRoot,
                    progress,
                    pauseToken,
                    cancellationToken).ConfigureAwait(false))
                {
                    PackageProfile stagedProfile = stagedPackage.Profile;
                    string stagedExe = PackageProfileReader.GetExecutablePath(stagingRoot, stagedProfile);
                    log("程序包验证通过：" + stagedProfile.DisplayName + " " + stagedProfile.Version + "。");

                    string executableDirectory = Path.GetDirectoryName(stagedProfile.ExecutableRelativePath) ?? string.Empty;
                    stagedPackage.ReleaseOfficialArtifactDigest(stagedProfile.ExecutableRelativePath);
                    stagedPackage.ReleaseOfficialArtifactDigest(Path.Combine(
                        executableDirectory,
                        "resources",
                        "icon-chatgpt.ico"));

                    progress.Report(new OperationProgress("应用便携版视觉资源", 84, "正在统一桌面、任务栏、窗口和托盘图标。"));
                    compatibilityCoordinator.ApplyVisual(stagingRoot, stagedExe);
                    ArtifactProvenance stagedProvenance = ArtifactProvenance.Capture(
                        stagingRoot,
                        stagedProfile,
                        package,
                        null,
                        stagedPackage);
                    log(string.Format(
                        CultureInfo.InvariantCulture,
                        "官方 provenance 基线完成：复用 staging 摘要 {0} 个/{1:F1} MiB，重算 {2} 个。",
                        stagedPackage.ReusedArtifactDigestCount,
                        stagedPackage.ReusedArtifactDigestBytes / 1048576d,
                        stagedProvenance.Artifacts.Count - stagedPackage.ReusedArtifactDigestCount));

                    if (compatibility.AnyEnabled)
                    {
                        foreach (string protectedArtifact in CompatibilityMaintenance.GetStagingProtectedArtifacts(
                            stagedProfile,
                            compatibility))
                        {
                            stagedPackage.ReleaseOfficialArtifactDigest(protectedArtifact);
                        }
                    }
                    progress.Report(new OperationProgress(
                        "继承便携版功能设置",
                        87,
                        compatibility.AnyEnabled
                            ? "正在新版本官方 staging 上事务化继承当前安装的实际兼容状态。"
                            : "当前安装未启用兼容功能，正在登记新版本官方状态。"));
                    compatibilityResult = compatibilityMaintenance.ApplyTrustedStaging(
                        stagingRoot,
                        stagedProfile,
                        installId,
                        compatibility,
                        stagedProvenance);
                    log(!compatibilityResult.TransactionCommitted
                        ? "新版本兼容设置未全部达到现场继承目标，已恢复官方文件并记录实际结果。"
                        : compatibilityResult.AllSucceeded
                            ? "新版本兼容设置已达到保存的目标状态。"
                            : "新版本已提交可支持的兼容设置；失败且未改写文件的功能保持原状并等待适配。");
                }

                progress.Report(new OperationProgress("准备切换版本", 90, "正在关闭安装目录中的 Codex 进程，最多等待 15 秒。"));
                int[] activeProcessIds = ProcessesUnderPath.FindProcessIds(installRoot);
                log(activeProcessIds.Length == 0
                    ? "版本切换前未发现安装目录内的运行进程。"
                    : "版本切换前发现 " + activeProcessIds.Length + " 个安装目录内进程，正在停止：PID " +
                        string.Join("、", activeProcessIds.Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray()) + "。");
                ProcessesUnderPath.Stop(installRoot);
                await Task.Run(
                    () => ProcessesUnderPath.WaitForExit(installRoot, TimeSpan.FromSeconds(15)),
                    cancellationToken).ConfigureAwait(false);
                log("安装目录内进程已全部退出，可以进入版本切换事务。");
                cancellationToken.ThrowIfCancellationRequested();

                string transactionOld = previousRoot + ".transaction-old";
                bool oldPreviousMoved = false;
                bool currentMoved = false;
                bool newVersionMoved = false;
                bool oldBackupCleanupPending = false;
                IReadOnlyList<string> integrationWarnings = new string[0];
                DeploymentJournalRecord updateJournal = CreateJournalRecord(
                    DeploymentOperationKind.Update,
                    DeploymentTransactionPhase.UpdatePrepared,
                    installRoot,
                    installId,
                    Directory.Exists(installRoot),
                    Directory.Exists(previousRoot),
                    createIntegration);
                try
                {
                    updateJournal = PrepareUpdateJournalAndDetachOldPrevious(
                        updateJournal,
                        previousRoot,
                        transactionOld,
                        out oldPreviousMoved);
                    if (Directory.Exists(installRoot))
                    {
                        progress.Report(new OperationProgress(
                            "创建回滚备份",
                            93,
                            "上一安装的进程已经全部退出，正在把当前版本保留为 .previous。"));
                        MoveDirectoryWithRetry(installRoot, previousRoot);
                        log("当前版本已切换为新的回滚备份：" + previousRoot);
                        currentMoved = true;
                        updateJournal = PersistJournalPhase(
                            updateJournal,
                            DeploymentTransactionPhase.UpdateCurrentDetached);
                    }
                    progress.Report(new OperationProgress("启用新版本", 96, "正在原子切换安装目录并刷新系统集成。"));
                    MoveDirectoryWithRetry(stagingRoot, installRoot);
                    log("新版本 staging 已原子启用为当前安装目录：" + installRoot);
                    newVersionMoved = true;
                    updateJournal = PersistJournalPhase(
                        updateJournal,
                        DeploymentTransactionPhase.UpdatePayloadActivated);
                    if (createIntegration)
                    {
                        integrationWarnings = CreateIntegrationCore(installRoot);
                    }
                    updateJournal = PersistJournalPhase(
                        updateJournal,
                        DeploymentTransactionPhase.UpdateExternalStateUpdated);
                    if (oldPreviousMoved)
                    {
                        progress.Report(new OperationProgress("清理旧回滚备份", 98, "正在删除更早的 .previous 版本；大量小文件可能需要一些时间。"));
                        Stopwatch cleanupStopwatch = Stopwatch.StartNew();
                        log("开始清理旧回滚备份：" + transactionOld);
                        oldBackupCleanupPending = !TryDeleteCleanupDirectory(
                            updateJournal,
                            updateJournal.UpdateOldPreviousCleanup,
                            transactionOld,
                            parentRoot,
                            "旧回滚备份");
                        cleanupStopwatch.Stop();
                        log(string.Format(
                            CultureInfo.InvariantCulture,
                            oldBackupCleanupPending
                                ? "旧回滚备份暂未清理完成，耗时 {0:F1} 秒；下次启动将继续处理。"
                                : "旧回滚备份清理完成，耗时 {0:F1} 秒。",
                            cleanupStopwatch.Elapsed.TotalSeconds));
                    }
                    if (!oldBackupCleanupPending)
                    {
                        oldBackupCleanupPending = !TryDeleteDeploymentJournal(
                            installRoot,
                            "已完成更新事务");
                    }
                }
                catch (Exception operationError)
                {
                    log("版本切换失败，正在按 journal 恢复原目录拓扑：" + operationError.Message);
                    bool committed = updateJournal.Phase >= DeploymentTransactionPhase.UpdatePayloadActivated;
                    Exception recoveryError = null;
                    try
                    {
                        RecoverFailedUpdateSwitch(
                            updateJournal,
                            parentRoot,
                            workRoot,
                            oldPreviousMoved,
                            currentMoved,
                            newVersionMoved);
                    }
                    catch (Exception exception)
                    {
                        recoveryError = exception;
                    }
                    if (recoveryError != null)
                    {
                        throw new AggregateException(
                            committed
                                ? "更新已到提交点，但向前完成事务时再次失败。"
                                : "更新失败，且自动恢复原版本时再次失败。",
                            operationError,
                            recoveryError);
                    }
                    if (!committed)
                    {
                        throw;
                    }
                    oldBackupCleanupPending = DeploymentJournal.Exists(installRoot);
                    log("更新在提交点后出现错误，已保留新版本并按 journal 向前完成事务。");
                }

                DeploymentResult deploymentResult = new DeploymentResult(
                    createIntegration,
                    integrationWarnings,
                    oldBackupCleanupPending,
                    compatibilityResult);
                progress.Report(DeploymentCompletion.ForInstalledVersion(remoteVersion, deploymentResult));
                log("便携版安装目录：" + installRoot);
                return deploymentResult;
            }
            finally
            {
                if (File.Exists(downloadPath))
                {
                    TryDeleteFile(downloadPath, "未完成的下载临时文件");
                }
                if (Directory.Exists(workRoot))
                {
                    TryDeleteDirectory(
                        workRoot,
                        workParent,
                        "临时工作目录",
                        workRootIdentity);
                }
                StorageMaintenance.RunBestEffort(log);
            }
        }

        private CompatibilityOptions ResolveInheritedCompatibilityOptions(
            string installRoot,
            Version currentVersion,
            bool allowUnknownCompatibility)
        {
            if (currentVersion == null)
            {
                log("当前为新安装，兼容功能默认保持关闭；安装完成后可在兼容控制中显式应用。");
                return new CompatibilityOptions(false, false, false, false);
            }

            CompatibilityOverview overview = CompatibilityStatusReader.Read(installRoot, false);
            CompatibilityOptions options = CompatibilityStatusReader.ResolveOptions(overview);
            if (options == null)
            {
                if (allowUnknownCompatibility)
                {
                    log("无法可靠继承当前兼容状态；本次明确缓存回退将以官方默认关闭兼容项部署，当前版本仍会保留为 .previous。");
                    return new CompatibilityOptions(false, false, false, false);
                }
                throw new InvalidDataException(
                    "无法从当前文件可靠判断实际兼容状态，已停止更新以避免覆盖现有功能。请先在兼容控制中检查状态。" +
                    (string.IsNullOrWhiteSpace(overview.Detail) ? string.Empty : " 详情：" + overview.Detail));
            }

            log("已直接检查当前安装文件，并确定增量更新需要继承的兼容功能。");
            return options;
        }

        internal string PrepareInstallTopology(
            string installRoot,
            LegacyAdoptionApproval adoptionApproval)
        {
            installRoot = ValidateInstallRoot(installRoot);
            string parentRoot = Directory.GetParent(installRoot).FullName;
            string previousRoot = installRoot + ".previous";
            Directory.CreateDirectory(parentRoot);
            EnsureNoPendingDeploymentCleanup(
                RecoverInterruptedDeployment(installRoot, parentRoot));
            if (Directory.Exists(installRoot) && InstallOwnership.IsDirectoryEmpty(installRoot))
            {
                NativeFileSystem.DeleteEmptyDirectory(installRoot);
            }
            if (Directory.Exists(previousRoot) && InstallOwnership.IsDirectoryEmpty(previousRoot))
            {
                NativeFileSystem.DeleteEmptyDirectory(previousRoot);
            }
            string installId = InstallOwnership.PrepareInstall(
                installRoot,
                previousRoot,
                adoptionApproval,
                log);
            NormalizePreviousOnlyDeployment(installRoot, previousRoot);
            CleanupOwnedWorkDirectoriesBestEffort(
                installRoot,
                parentRoot,
                TimeSpan.FromHours(24));
            return installId;
        }

        private static Version GetPortableVersion(string installRoot)
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

        private static DeploymentJournalRecord CreateJournalRecord(
            DeploymentOperationKind operation,
            DeploymentTransactionPhase phase,
            string installRoot,
            string installId,
            bool hadCurrent,
            bool hadPrevious,
            bool createIntegration)
        {
            return new DeploymentJournalRecord
            {
                OperationId = Guid.NewGuid().ToString("N"),
                Operation = operation,
                Phase = phase,
                InstallRoot = installRoot,
                InstallId = installId,
                HadCurrent = hadCurrent,
                HadPrevious = hadPrevious,
                CreateIntegration = createIntegration
            };
        }

        private DeploymentJournalRecord PrepareUpdateJournalAndDetachOldPrevious(
            DeploymentJournalRecord journal,
            string previousRoot,
            string transactionRoot,
            out bool oldPreviousMoved)
        {
            oldPreviousMoved = false;
            if (!journal.HadPrevious)
            {
                DeploymentJournal.Write(journal);
                return journal;
            }
            EnsureDirectoryPathIsNotFile(
                transactionRoot,
                "更新事务的旧回滚清理路径");
            if (Directory.Exists(transactionRoot))
            {
                throw new IOException("检测到未恢复的更新事务目录：" + transactionRoot);
            }

            using (SafeFileHandle cleanupHandle =
                InstallOwnership.OpenManagedDirectoryHandle(previousRoot))
            {
                InstallOwnership.EnsureOwnedInstallation(
                    previousRoot,
                    journal.InstallId,
                    null,
                    log);
                journal.UpdateOldPreviousCleanup =
                    DeploymentJournal.CreatePreparedCleanupReceipt(
                        journal,
                        cleanupHandle,
                        previousRoot,
                        transactionRoot);
                DeploymentJournal.Write(journal);

                MoveDirectoryWithRetry(previousRoot, transactionRoot);
                log("旧回滚备份已暂存到更新事务目录：" + transactionRoot);
                oldPreviousMoved = true;
                DeploymentJournalRecord candidate =
                    DeploymentJournal.Clone(journal);
                candidate.UpdateOldPreviousCleanup =
                    DeploymentJournal.ArmCleanupReceipt(
                        candidate,
                        candidate.UpdateOldPreviousCleanup,
                        cleanupHandle,
                        transactionRoot);
                candidate.Phase =
                    DeploymentTransactionPhase.UpdateOldPreviousDetached;
                DeploymentJournal.Write(candidate);
                return candidate;
            }
        }


    }
}
