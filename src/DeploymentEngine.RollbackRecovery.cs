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
        public DeploymentResult Rollback(
            string installRoot,
            bool createIntegrationEnabled,
            LegacyAdoptionApproval adoptionApproval)
        {
            installRoot = ValidateInstallRoot(installRoot);
            using (OperationFileLock operationLock = OperationFileLock.Acquire(installRoot))
            {
                RecoverPendingCompatibilityMaintenance(installRoot);
                return RollbackCore(installRoot, createIntegrationEnabled, adoptionApproval);
            }
        }

        public async Task<DeploymentResult> RollbackAvailableAsync(
            string installRoot,
            IProgress<OperationProgress> progress,
            OperationPauseToken pauseToken,
            CancellationToken cancellationToken,
            bool createIntegrationEnabled,
            LegacyAdoptionApproval adoptionApproval)
        {
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            installRoot = ValidateInstallRoot(installRoot);
            using (OperationFileLock operationLock = await OperationFileLock
                .AcquireAsync(installRoot, cancellationToken)
                .ConfigureAwait(false))
            {
                RecoverPendingCompatibilityMaintenance(installRoot);
                Version currentVersion = GetPortableVersion(installRoot);
                Version previousVersion = GetPortableVersion(installRoot + ".previous");
                RollbackPackageTarget target = RollbackPackageSelector.Select(
                    PortableStorage.CacheRoot,
                    currentVersion,
                    previousVersion,
                    CodexMicrosoftStoreSource.GetCurrentArchitecture());
                if (target == null)
                {
                    throw new InvalidOperationException("没有可回滚的较早版本或缓存官方程序包。");
                }
                if (target.Kind == RollbackTargetKind.PreviousDirectory)
                {
                    return RollbackCore(installRoot, createIntegrationEnabled, adoptionApproval);
                }

                progress.Report(new OperationProgress(
                    "准备缓存回滚版本",
                    5,
                    "发现官方缓存版本 " + target.Version + "，正在验证后部署；当前版本将保留为新的 .previous。"));
                PackageMetadata package = await Task.Run(
                    () => RollbackPackageSelector.CreateLocalPackageMetadata(target),
                    cancellationToken).ConfigureAwait(false);
                log("缓存回滚目标：" + package.fullName + "；路径=" + target.Path);
                return await InstallOrUpdateCoreAsync(
                    package,
                    installRoot,
                    true,
                    progress,
                    pauseToken,
                    cancellationToken,
                    createIntegrationEnabled,
                    adoptionApproval,
                    true,
                    true).ConfigureAwait(false);
            }
        }

        private bool IsPreviousVersionAvailable(string installRoot)
        {
            try
            {
                string previousRoot = installRoot + ".previous";
                PackageProfile profile;
                string validationError;
                return InstallOwnership.TryValidateRunnableCodexPayload(previousRoot, out profile, out validationError);
            }
            catch
            {
                return false;
            }
        }

        private IReadOnlyList<string> CreateIntegrationCore(string installRoot)
        {
            return shellIntegrationCoordinator.Create(installRoot);
        }

        private void PrepareIntegrationCleanup(
            string registrationRoot,
            string sourceRoot,
            string installId,
            string deploymentOperationId)
        {
            shellIntegrationCoordinator.PrepareCleanup(
                registrationRoot,
                sourceRoot,
                installId,
                deploymentOperationId);
        }

        private ShellIntegrationCleanupResult RemoveIntegration(
            string registrationRoot,
            string sourceRoot,
            string installId,
            string deploymentOperationId)
        {
            return shellIntegrationCoordinator.CompletePreparedCleanup(
                registrationRoot,
                sourceRoot,
                installId,
                deploymentOperationId);
        }

        private void CancelPreparedIntegrationCleanup(
            string registrationRoot,
            string installId,
            string deploymentOperationId)
        {
            shellIntegrationCoordinator.CancelPreparedCleanup(
                registrationRoot,
                installId,
                deploymentOperationId);
        }

        internal static string CreateWorkRoot(string installRoot, string fallbackParent, out string workParent)
        {
            string name = ".cpm-" + Guid.NewGuid().ToString("N");
            string volumeRoot = Path.GetPathRoot(installRoot);
            string preferred = Path.Combine(volumeRoot, name);
            try
            {
                Directory.CreateDirectory(preferred);
                workParent = volumeRoot;
                return preferred;
            }
            catch (UnauthorizedAccessException)
            {
                // 某些系统盘根目录不允许普通用户创建目录，回退到安装目录旁。
            }
            catch (IOException)
            {
                // 网络卷或特殊文件系统可能不允许在卷根创建目录。
            }

            string fallback = Path.Combine(fallbackParent, name);
            Directory.CreateDirectory(fallback);
            workParent = fallbackParent;
            return fallback;
        }

        private DeploymentResult RollbackCore(
            string installRoot,
            bool createIntegration,
            LegacyAdoptionApproval adoptionApproval)
        {
            string previousRoot = installRoot + ".previous";
            string parentRoot = Directory.GetParent(installRoot).FullName;
            EnsureNoPendingDeploymentCleanup(
                RecoverInterruptedDeployment(installRoot, parentRoot));
            if (!IsPreviousVersionAvailable(installRoot))
            {
                throw new InvalidOperationException("没有可回滚的上一版本。");
            }

            string installId = InstallOwnership.EnsureOwnedInstallation(installRoot, null, adoptionApproval, log);
            InstallOwnership.EnsureOwnedInstallation(previousRoot, installId, adoptionApproval, log);

            ProcessesUnderPath.Stop(installRoot);
            ProcessesUnderPath.Stop(previousRoot);
            ProcessesUnderPath.WaitForExit(installRoot, TimeSpan.FromSeconds(15));
            ProcessesUnderPath.WaitForExit(previousRoot, TimeSpan.FromSeconds(5));
            string transactionRoot = installRoot + ".rollback-transaction";
            IReadOnlyList<string> integrationWarnings = new string[0];
            DeploymentJournalRecord rollbackJournal = CreateJournalRecord(
                DeploymentOperationKind.Rollback,
                DeploymentTransactionPhase.RollbackPrepared,
                installRoot,
                installId,
                true,
                true,
                createIntegration);
            DeploymentJournal.Write(rollbackJournal);
            try
            {
                if (Directory.Exists(transactionRoot))
                {
                    throw new IOException("检测到未恢复的回滚事务目录：" + transactionRoot);
                }
                MoveDirectoryWithRetry(installRoot, transactionRoot);
                rollbackJournal.Phase = DeploymentTransactionPhase.RollbackCurrentDetached;
                DeploymentJournal.Write(rollbackJournal);
                MoveDirectoryWithRetry(previousRoot, installRoot);
                rollbackJournal.Phase = DeploymentTransactionPhase.RollbackPreviousActivated;
                DeploymentJournal.Write(rollbackJournal);
                MoveDirectoryWithRetry(transactionRoot, previousRoot);
                rollbackJournal.Phase = DeploymentTransactionPhase.RollbackSwapCompleted;
                DeploymentJournal.Write(rollbackJournal);
                if (createIntegration)
                {
                    integrationWarnings = CreateIntegrationCore(installRoot);
                }
                rollbackJournal.Phase = DeploymentTransactionPhase.RollbackExternalStateUpdated;
                DeploymentJournal.Write(rollbackJournal);
            }
            catch (Exception operationError)
            {
                Exception recoveryError = null;
                try
                {
                    ProcessesUnderPath.Stop(installRoot);
                    ProcessesUnderPath.Stop(previousRoot);
                    ProcessesUnderPath.WaitForExit(installRoot, TimeSpan.FromSeconds(5));
                    ProcessesUnderPath.WaitForExit(previousRoot, TimeSpan.FromSeconds(5));
                    RestoreRollbackOriginal(rollbackJournal);
                    if (createIntegration && Directory.Exists(installRoot))
                    {
                        CreateIntegrationCore(installRoot);
                    }
                }
                catch (Exception exception)
                {
                    recoveryError = exception;
                }
                if (recoveryError != null)
                {
                    throw new AggregateException("回滚失败，且自动恢复原版本时再次失败。", operationError, recoveryError);
                }
                throw;
            }
            DeploymentJournal.Delete(installRoot);
            log("已回滚到上一版本，回滚前版本已保留为新的 .previous。");
            return new DeploymentResult(
                createIntegration,
                integrationWarnings);
        }

        internal DeploymentRecoveryResult RecoverInterruptedDeployment(string installRoot, string parentRoot)
        {
            string previousRoot = installRoot + ".previous";
            string updateTransactionRoot = previousRoot + ".transaction-old";
            string rollbackTransactionRoot = installRoot + ".rollback-transaction";
            string rollbackRecoveryState = GetRollbackRecoveryStatePath(installRoot);
            DeploymentJournalRecord deploymentJournal = DeploymentJournal.Read(installRoot);
            bool oldBackupCleanupPending = false;
            bool uninstallDirectoryCleanupPending = false;

            if (deploymentJournal != null)
            {
                if (deploymentJournal.Operation == DeploymentOperationKind.Update)
                {
                    if (Directory.Exists(rollbackTransactionRoot) || File.Exists(rollbackRecoveryState))
                    {
                        throw new IOException("更新 journal 与回滚事务同时存在，已拒绝猜测恢复顺序。");
                    }
                    oldBackupCleanupPending = RecoverInterruptedUpdate(
                        deploymentJournal,
                        parentRoot);
                }
                else if (deploymentJournal.Operation == DeploymentOperationKind.Rollback)
                {
                    if (Directory.Exists(updateTransactionRoot) || File.Exists(rollbackRecoveryState))
                    {
                        throw new IOException("回滚 journal 与更新或独立回滚恢复状态同时存在，已拒绝猜测恢复顺序。");
                    }
                    RecoverInterruptedRollback(deploymentJournal);
                }
                else
                {
                    if (Directory.Exists(updateTransactionRoot) ||
                        Directory.Exists(rollbackTransactionRoot) ||
                        File.Exists(rollbackRecoveryState))
                    {
                        throw new IOException("卸载事务与更新或回滚事务同时存在，已拒绝猜测恢复顺序。");
                    }
                    uninstallDirectoryCleanupPending = RecoverInterruptedUninstall(
                        deploymentJournal,
                        parentRoot);
                }
                if (DeploymentJournal.Exists(installRoot))
                {
                    if (oldBackupCleanupPending || uninstallDirectoryCleanupPending)
                    {
                        return new DeploymentRecoveryResult(
                            oldBackupCleanupPending,
                            uninstallDirectoryCleanupPending);
                    }
                    throw new IOException("部署事务的残留目录暂时无法清理，请关闭占用文件的程序后重试。");
                }
            }

            if (Directory.Exists(rollbackRecoveryState))
            {
                throw new IOException("回滚恢复状态路径被目录占用，已拒绝自动处理：" + rollbackRecoveryState);
            }
            if (Directory.Exists(updateTransactionRoot) &&
                (Directory.Exists(rollbackTransactionRoot) || File.Exists(rollbackRecoveryState)))
            {
                throw new IOException("同时检测到更新与回滚的未完成事务，已拒绝猜测目录归属。请先保留现场并人工检查。");
            }

            if (File.Exists(rollbackRecoveryState))
            {
                log("检测到未完成的回滚失败恢复，正在按持久化阶段继续恢复原版本。");
                RecoverRollbackReversal(installRoot, previousRoot, rollbackTransactionRoot, rollbackRecoveryState);
            }

            if (Directory.Exists(rollbackTransactionRoot))
            {
                RecoverInterruptedRollbackSwap(installRoot, previousRoot, rollbackTransactionRoot);
            }

            if (Directory.Exists(updateTransactionRoot))
            {
                RecoverInterruptedUpdate(installRoot, previousRoot, updateTransactionRoot, parentRoot);
            }
            return new DeploymentRecoveryResult(false, false);
        }

        internal void NormalizePreviousOnlyDeployment(string installRoot, string previousRoot)
        {
            if (Directory.Exists(installRoot) || !Directory.Exists(previousRoot))
            {
                return;
            }

            // 历史异常或手工删除可能留下“仅 previous”拓扑。PrepareInstall 已在调用本方法前
            // 验证其所有权；先恢复为 current，避免成功更新把唯一可用版本当作过期备份删除。
            MoveDirectoryWithRetry(previousRoot, installRoot);
            log("检测到仅剩 .previous 的安装，已先恢复为当前版本再继续更新。");
        }

        private bool RecoverInterruptedUpdate(DeploymentJournalRecord journal, string parentRoot)
        {
            string installRoot = journal.InstallRoot;
            string previousRoot = installRoot + ".previous";
            string transactionRoot = previousRoot + ".transaction-old";
            ValidateOwnedActiveRoot(installRoot, journal.InstallId);
            ValidateOwnedActiveRoot(previousRoot, journal.InstallId);
            if (!journal.HadPrevious && Directory.Exists(transactionRoot))
            {
                throw new InvalidDataException(
                    "更新事务出现了未由原始拓扑授权的 transaction-old 目录。");
            }
            if (!Directory.Exists(installRoot) &&
                !Directory.Exists(previousRoot) &&
                !Directory.Exists(transactionRoot))
            {
                if (journal.Phase == DeploymentTransactionPhase.UpdatePrepared &&
                    !journal.HadCurrent &&
                    !journal.HadPrevious)
                {
                    DeploymentJournal.Delete(installRoot);
                    log("已清理首次安装在 payload 激活前留下的空部署事务。");
                    return false;
                }
                throw new InvalidDataException("更新事务恢复时没有找到任何受管目录。");
            }

            if (!IsUpdateCommitted(journal, installRoot, previousRoot, transactionRoot))
            {
                if (journal.HadCurrent && !Directory.Exists(installRoot) && Directory.Exists(previousRoot))
                {
                    MoveDirectoryWithRetry(previousRoot, installRoot);
                }
                if (journal.HadPrevious && Directory.Exists(transactionRoot))
                {
                    ValidateCleanupRootForRollback(
                        journal,
                        journal.UpdateOldPreviousCleanup,
                        transactionRoot);
                    if (Directory.Exists(previousRoot))
                    {
                        throw new IOException("恢复未提交更新时 previous 目标仍然存在。");
                    }
                    MoveDirectoryWithRetry(transactionRoot, previousRoot);
                }
                DeploymentJournal.Delete(installRoot);
                log("已按 journal 恢复尚未提交的更新事务。");
                return false;
            }

            ValidateCleanupRoot(
                journal,
                journal.UpdateOldPreviousCleanup,
                transactionRoot);

            if (!Directory.Exists(installRoot))
            {
                throw new IOException("更新 journal 已到提交点，但 current 不存在。");
            }
            if (journal.HadCurrent && !Directory.Exists(previousRoot))
            {
                throw new IOException("更新 journal 已到提交点，但回滚版本不存在。");
            }
            if (journal.Phase < DeploymentTransactionPhase.UpdateExternalStateUpdated)
            {
                if (journal.CreateIntegration)
                {
                    CreateIntegrationCore(installRoot);
                }
                journal.Phase = DeploymentTransactionPhase.UpdateExternalStateUpdated;
                DeploymentJournal.Write(journal);
            }

            bool oldPreviousRemoved = TryDeleteCleanupDirectory(
                    journal,
                    journal.UpdateOldPreviousCleanup,
                    transactionRoot,
                    parentRoot,
                    "更新事务中的旧回滚备份");
            if (oldPreviousRemoved)
            {
                if (TryDeleteDeploymentJournal(installRoot, "已恢复更新事务"))
                {
                    log("已按 journal 完成中断的更新事务。");
                    return false;
                }
                log("更新内容已经恢复完成，但事务元数据仍待清理。");
                return true;
            }
            log("更新已经提交，但旧回滚备份仍待清理。");
            return true;
        }

        private static bool IsUpdateCommitted(
            DeploymentJournalRecord journal,
            string installRoot,
            string previousRoot,
            string transactionRoot)
        {
            if (journal.Phase >= DeploymentTransactionPhase.UpdatePayloadActivated)
            {
                return true;
            }
            if (!Directory.Exists(installRoot))
            {
                return false;
            }
            if (!journal.HadCurrent)
            {
                return true;
            }
            if (!Directory.Exists(previousRoot))
            {
                return false;
            }
            return !journal.HadPrevious || Directory.Exists(transactionRoot);
        }

        internal bool RecoverFailedUpdateSwitch(
            DeploymentJournalRecord journal,
            string parentRoot,
            string workRoot,
            bool oldPreviousMoved,
            bool currentMoved,
            bool newVersionMoved)
        {
            if (journal == null) throw new ArgumentNullException(nameof(journal));
            bool committed = journal.Phase >= DeploymentTransactionPhase.UpdatePayloadActivated;
            if (committed)
            {
                RecoverInterruptedUpdate(journal, parentRoot);
                return true;
            }

            string installRoot = journal.InstallRoot;
            string previousRoot = installRoot + ".previous";
            string transactionRoot = previousRoot + ".transaction-old";
            ProcessesUnderPath.Stop(installRoot);
            ProcessesUnderPath.WaitForExit(installRoot, TimeSpan.FromSeconds(5));
            string failedNewRoot = Path.Combine(workRoot, "failed-new");
            if (newVersionMoved && Directory.Exists(installRoot))
            {
                MoveDirectoryWithRetry(installRoot, failedNewRoot);
            }
            if (currentMoved && Directory.Exists(previousRoot) && !Directory.Exists(installRoot))
            {
                MoveDirectoryWithRetry(previousRoot, installRoot);
            }
            if (oldPreviousMoved && Directory.Exists(transactionRoot))
            {
                ValidateCleanupRootForRollback(
                    journal,
                    journal.UpdateOldPreviousCleanup,
                    transactionRoot);
                if (Directory.Exists(previousRoot))
                {
                    throw new IOException("恢复旧回滚备份时目标目录仍然存在：" + previousRoot);
                }
                MoveDirectoryWithRetry(transactionRoot, previousRoot);
            }
            if (journal.CreateIntegration && Directory.Exists(installRoot))
            {
                CreateIntegrationCore(installRoot);
            }
            DeploymentJournal.Delete(installRoot);
            log("版本切换失败后的原版本与旧回滚备份已恢复。");
            return false;
        }

        private void RecoverInterruptedRollback(DeploymentJournalRecord journal)
        {
            if (journal.Phase >= DeploymentTransactionPhase.RollbackRestoreRequested)
            {
                RestoreRollbackOriginal(journal);
                return;
            }

            string installRoot = journal.InstallRoot;
            string previousRoot = installRoot + ".previous";
            string transactionRoot = installRoot + ".rollback-transaction";
            ValidateOwnedTransactionRoots(installRoot, previousRoot, transactionRoot);

            if (journal.Phase == DeploymentTransactionPhase.RollbackPrepared)
            {
                if (Directory.Exists(installRoot) && Directory.Exists(previousRoot) && !Directory.Exists(transactionRoot))
                {
                    MoveDirectoryWithRetry(installRoot, transactionRoot);
                }
                else if (!(!Directory.Exists(installRoot) && Directory.Exists(previousRoot) && Directory.Exists(transactionRoot)))
                {
                    throw InvalidRollbackRecoveryTopology(
                        journal.Phase.ToString(),
                        Directory.Exists(installRoot),
                        Directory.Exists(previousRoot),
                        Directory.Exists(transactionRoot));
                }
                journal.Phase = DeploymentTransactionPhase.RollbackCurrentDetached;
                DeploymentJournal.Write(journal);
            }

            if (journal.Phase == DeploymentTransactionPhase.RollbackCurrentDetached)
            {
                if (!Directory.Exists(installRoot) && Directory.Exists(previousRoot) && Directory.Exists(transactionRoot))
                {
                    MoveDirectoryWithRetry(previousRoot, installRoot);
                }
                else if (!(Directory.Exists(installRoot) && !Directory.Exists(previousRoot) && Directory.Exists(transactionRoot)))
                {
                    throw InvalidRollbackRecoveryTopology(
                        journal.Phase.ToString(),
                        Directory.Exists(installRoot),
                        Directory.Exists(previousRoot),
                        Directory.Exists(transactionRoot));
                }
                journal.Phase = DeploymentTransactionPhase.RollbackPreviousActivated;
                DeploymentJournal.Write(journal);
            }

            if (journal.Phase == DeploymentTransactionPhase.RollbackPreviousActivated)
            {
                if (Directory.Exists(installRoot) && !Directory.Exists(previousRoot) && Directory.Exists(transactionRoot))
                {
                    MoveDirectoryWithRetry(transactionRoot, previousRoot);
                }
                else if (!(Directory.Exists(installRoot) && Directory.Exists(previousRoot) && !Directory.Exists(transactionRoot)))
                {
                    throw InvalidRollbackRecoveryTopology(
                        journal.Phase.ToString(),
                        Directory.Exists(installRoot),
                        Directory.Exists(previousRoot),
                        Directory.Exists(transactionRoot));
                }
                journal.Phase = DeploymentTransactionPhase.RollbackSwapCompleted;
                DeploymentJournal.Write(journal);
            }

            if (journal.Phase == DeploymentTransactionPhase.RollbackSwapCompleted)
            {
                if (!Directory.Exists(installRoot) || !Directory.Exists(previousRoot) || Directory.Exists(transactionRoot))
                {
                    throw InvalidRollbackRecoveryTopology(
                        journal.Phase.ToString(),
                        Directory.Exists(installRoot),
                        Directory.Exists(previousRoot),
                        Directory.Exists(transactionRoot));
                }
                if (journal.CreateIntegration)
                {
                    CreateIntegrationCore(installRoot);
                }
                journal.Phase = DeploymentTransactionPhase.RollbackExternalStateUpdated;
                DeploymentJournal.Write(journal);
            }

            if (journal.Phase == DeploymentTransactionPhase.RollbackExternalStateUpdated)
            {
                DeploymentJournal.Delete(installRoot);
                log("已按 journal 完成中断的回滚事务。");
            }
        }

        private void RestoreRollbackOriginal(DeploymentJournalRecord journal)
        {
            string installRoot = journal.InstallRoot;
            string previousRoot = installRoot + ".previous";
            string transactionRoot = installRoot + ".rollback-transaction";
            ValidateOwnedTransactionRoots(installRoot, previousRoot, transactionRoot);

            if (journal.Phase == DeploymentTransactionPhase.RollbackPrepared)
            {
                if (Directory.Exists(installRoot) && Directory.Exists(previousRoot) && !Directory.Exists(transactionRoot))
                {
                    DeploymentJournal.Delete(installRoot);
                    return;
                }
                if (!Directory.Exists(installRoot) && Directory.Exists(previousRoot) && Directory.Exists(transactionRoot))
                {
                    MoveDirectoryWithRetry(transactionRoot, installRoot);
                    DeploymentJournal.Delete(installRoot);
                    return;
                }
            }

            if (journal.Phase == DeploymentTransactionPhase.RollbackCurrentDetached)
            {
                if (!Directory.Exists(installRoot) && Directory.Exists(previousRoot) && Directory.Exists(transactionRoot))
                {
                    MoveDirectoryWithRetry(transactionRoot, installRoot);
                    DeploymentJournal.Delete(installRoot);
                    return;
                }
                if (Directory.Exists(installRoot) && !Directory.Exists(previousRoot) && Directory.Exists(transactionRoot))
                {
                    journal.Phase = DeploymentTransactionPhase.RollbackRestoreRequested;
                    DeploymentJournal.Write(journal);
                }
            }

            if (journal.Phase == DeploymentTransactionPhase.RollbackPreviousActivated &&
                Directory.Exists(installRoot) &&
                Directory.Exists(previousRoot) &&
                !Directory.Exists(transactionRoot))
            {
                journal.Phase = DeploymentTransactionPhase.RollbackRestoreSwapped;
                DeploymentJournal.Write(journal);
            }
            else if (journal.Phase == DeploymentTransactionPhase.RollbackPreviousActivated ||
                journal.Phase == DeploymentTransactionPhase.RollbackRestoreRequested)
            {
                journal.Phase = DeploymentTransactionPhase.RollbackRestoreRequested;
                DeploymentJournal.Write(journal);
                if (Directory.Exists(installRoot) && !Directory.Exists(previousRoot) && Directory.Exists(transactionRoot))
                {
                    MoveDirectoryWithRetry(installRoot, previousRoot);
                }
                if (!Directory.Exists(installRoot) && Directory.Exists(previousRoot) && Directory.Exists(transactionRoot))
                {
                    MoveDirectoryWithRetry(transactionRoot, installRoot);
                    DeploymentJournal.Delete(installRoot);
                    log("已按 journal 恢复回滚前的 current/previous 目录。");
                    return;
                }
            }

            if (journal.Phase == DeploymentTransactionPhase.RollbackSwapCompleted ||
                journal.Phase == DeploymentTransactionPhase.RollbackExternalStateUpdated)
            {
                journal.Phase = DeploymentTransactionPhase.RollbackRestoreSwapped;
                DeploymentJournal.Write(journal);
            }

            if (journal.Phase == DeploymentTransactionPhase.RollbackRestoreSwapped)
            {
                if (Directory.Exists(installRoot) && Directory.Exists(previousRoot) && !Directory.Exists(transactionRoot))
                {
                    MoveDirectoryWithRetry(installRoot, transactionRoot);
                }
                else if (!(!Directory.Exists(installRoot) && Directory.Exists(previousRoot) && Directory.Exists(transactionRoot)))
                {
                    throw InvalidRollbackRecoveryTopology(
                        journal.Phase.ToString(),
                        Directory.Exists(installRoot),
                        Directory.Exists(previousRoot),
                        Directory.Exists(transactionRoot));
                }
                journal.Phase = DeploymentTransactionPhase.RollbackRestorePreviousDetached;
                DeploymentJournal.Write(journal);
            }

            if (journal.Phase == DeploymentTransactionPhase.RollbackRestorePreviousDetached)
            {
                if (!Directory.Exists(installRoot) && Directory.Exists(previousRoot) && Directory.Exists(transactionRoot))
                {
                    MoveDirectoryWithRetry(previousRoot, installRoot);
                }
                else if (!(Directory.Exists(installRoot) && !Directory.Exists(previousRoot) && Directory.Exists(transactionRoot)))
                {
                    throw InvalidRollbackRecoveryTopology(
                        journal.Phase.ToString(),
                        Directory.Exists(installRoot),
                        Directory.Exists(previousRoot),
                        Directory.Exists(transactionRoot));
                }
                journal.Phase = DeploymentTransactionPhase.RollbackRestoreCurrentActivated;
                DeploymentJournal.Write(journal);
            }

            if (journal.Phase == DeploymentTransactionPhase.RollbackRestoreCurrentActivated)
            {
                if (Directory.Exists(installRoot) && !Directory.Exists(previousRoot) && Directory.Exists(transactionRoot))
                {
                    MoveDirectoryWithRetry(transactionRoot, previousRoot);
                }
                else if (!(Directory.Exists(installRoot) && Directory.Exists(previousRoot) && !Directory.Exists(transactionRoot)))
                {
                    throw InvalidRollbackRecoveryTopology(
                        journal.Phase.ToString(),
                        Directory.Exists(installRoot),
                        Directory.Exists(previousRoot),
                        Directory.Exists(transactionRoot));
                }
                DeploymentJournal.Delete(installRoot);
                log("已按 journal 恢复回滚前的 current/previous 目录。");
                return;
            }

            throw InvalidRollbackRecoveryTopology(
                journal.Phase.ToString(),
                Directory.Exists(installRoot),
                Directory.Exists(previousRoot),
                Directory.Exists(transactionRoot));
        }

        private void RecoverInterruptedUpdate(
            string installRoot,
            string previousRoot,
            string transactionRoot,
            string parentRoot)
        {
            string transactionIdentity = Directory.Exists(transactionRoot)
                ? InstallOwnership.GetManagedDirectoryIdentity(transactionRoot)
                : null;
            ValidateOwnedTransactionRoots(installRoot, previousRoot, transactionRoot);
            bool currentExists = Directory.Exists(installRoot);
            bool previousExists = Directory.Exists(previousRoot);

            if (!currentExists && !previousExists)
            {
                // 两个正式槽位都缺失时，transaction-old 是唯一幸存的已验证版本。
                // 优先恢复为 current，确保下次启动至少有一个可用版本。
                MoveDirectoryWithRetry(transactionRoot, installRoot);
                log("更新事务只剩一个版本，已优先恢复为当前版本。");
                return;
            }

            // current 缺失而 previous 存在，说明崩溃发生在 current -> previous 之后。
            // 先恢复 current，再把更旧的回滚备份放回 previous；每一步再次崩溃也可重入。
            if (!currentExists && previousExists)
            {
                MoveDirectoryWithRetry(previousRoot, installRoot);
                currentExists = true;
                previousExists = false;
                log("已从 .previous 恢复更新前的当前版本。");
            }

            if (!previousExists)
            {
                MoveDirectoryWithRetry(transactionRoot, previousRoot);
                log("已恢复更新事务暂存的旧回滚版本。");
                return;
            }

            // current、previous 和 transaction-old 同时存在，只可能是新版本已经提交、
            // 但旧的第二份备份尚未清理。删除前已验证三者属于同一安装 ID。
            if (!TryDeleteDirectory(
                transactionRoot,
                parentRoot,
                "更新事务中的旧回滚备份",
                transactionIdentity))
            {
                throw new IOException("无法完成未结束更新事务的清理：" + transactionRoot);
            }
            log("已完成上次更新遗留事务的提交清理。");
        }

        private void RecoverInterruptedRollbackSwap(
            string installRoot,
            string previousRoot,
            string transactionRoot)
        {
            ValidateOwnedTransactionRoots(installRoot, previousRoot, transactionRoot);
            bool currentExists = Directory.Exists(installRoot);
            bool previousExists = Directory.Exists(previousRoot);
            bool transactionExists = Directory.Exists(transactionRoot);
            if (currentExists && previousExists && transactionExists)
            {
                throw new IOException("回滚事务目录拓扑无效：current、previous 和 transaction 不应同时存在。");
            }

            // 没有 current 但仍有 previous，表示第一步 current -> transaction 已完成。
            // 继续完成交换，而不是删除任何一个有效版本。
            if (!currentExists && previousExists)
            {
                MoveDirectoryWithRetry(previousRoot, installRoot);
                currentExists = true;
                previousExists = false;
                log("已继续完成中断的回滚版本切换。");
            }
            else if (!currentExists && !previousExists)
            {
                // 异常拓扑下优先把唯一保留的 transaction 恢复为 current，保证可启动版本不丢失。
                MoveDirectoryWithRetry(transactionRoot, installRoot);
                log("回滚事务只剩一个版本，已优先恢复为当前版本。");
                return;
            }

            if (!previousExists && Directory.Exists(transactionRoot))
            {
                MoveDirectoryWithRetry(transactionRoot, previousRoot);
                log("已完成中断的回滚交换，两个版本均已保留。");
            }
        }

        private void RestoreRollbackOriginal(
            string installRoot,
            string previousRoot,
            string transactionRoot,
            bool currentMoved,
            bool previousMoved,
            bool swapCompleted)
        {
            if (!currentMoved)
            {
                return;
            }

            string statePath = GetRollbackRecoveryStatePath(installRoot);
            string state;
            if (swapCompleted)
            {
                state = "restore-swapped-step1";
            }
            else if (previousMoved)
            {
                state = "restore-previous-moved-step1";
            }
            else
            {
                state = "restore-current-moved";
            }

            WriteRecoveryState(statePath, state);
            RecoverRollbackReversal(installRoot, previousRoot, transactionRoot, statePath);
        }

        private void RecoverRollbackReversal(
            string installRoot,
            string previousRoot,
            string transactionRoot,
            string statePath)
        {
            FileInfo stateInfo = new FileInfo(statePath);
            if (!stateInfo.Exists || stateInfo.Length <= 0 || stateInfo.Length > 128)
            {
                throw new InvalidDataException("回滚恢复状态文件无效：" + statePath);
            }

            ValidateOwnedTransactionRoots(installRoot, previousRoot, transactionRoot);
            string state = File.ReadAllText(statePath, Encoding.ASCII).Trim();
            while (true)
            {
                bool currentExists = Directory.Exists(installRoot);
                bool previousExists = Directory.Exists(previousRoot);
                bool transactionExists = Directory.Exists(transactionRoot);

                if (string.Equals(state, "restore-current-moved", StringComparison.Ordinal))
                {
                    if (!currentExists && previousExists && transactionExists)
                    {
                        MoveDirectoryWithRetry(transactionRoot, installRoot);
                    }
                    else if (!(currentExists && previousExists && !transactionExists))
                    {
                        throw InvalidRollbackRecoveryTopology(state, currentExists, previousExists, transactionExists);
                    }
                    NativeFileSystem.DeleteFile(statePath);
                    log("已恢复回滚前的 current/previous 目录。");
                    return;
                }

                if (string.Equals(state, "restore-previous-moved-step1", StringComparison.Ordinal))
                {
                    if (currentExists && !previousExists && transactionExists)
                    {
                        MoveDirectoryWithRetry(installRoot, previousRoot);
                    }
                    else if (!(!currentExists && previousExists && transactionExists))
                    {
                        if (currentExists && previousExists && !transactionExists)
                        {
                            NativeFileSystem.DeleteFile(statePath);
                            return;
                        }
                        throw InvalidRollbackRecoveryTopology(state, currentExists, previousExists, transactionExists);
                    }
                    state = "restore-previous-moved-step2";
                    WriteRecoveryState(statePath, state);
                    continue;
                }

                if (string.Equals(state, "restore-previous-moved-step2", StringComparison.Ordinal))
                {
                    if (!currentExists && previousExists && transactionExists)
                    {
                        MoveDirectoryWithRetry(transactionRoot, installRoot);
                    }
                    else if (!(currentExists && previousExists && !transactionExists))
                    {
                        throw InvalidRollbackRecoveryTopology(state, currentExists, previousExists, transactionExists);
                    }
                    NativeFileSystem.DeleteFile(statePath);
                    log("已恢复回滚前的 current/previous 目录。");
                    return;
                }

                if (string.Equals(state, "restore-swapped-step1", StringComparison.Ordinal))
                {
                    if (currentExists && previousExists && !transactionExists)
                    {
                        MoveDirectoryWithRetry(installRoot, transactionRoot);
                    }
                    else if (!(!currentExists && previousExists && transactionExists))
                    {
                        throw InvalidRollbackRecoveryTopology(state, currentExists, previousExists, transactionExists);
                    }
                    state = "restore-swapped-step2";
                    WriteRecoveryState(statePath, state);
                    continue;
                }

                if (string.Equals(state, "restore-swapped-step2", StringComparison.Ordinal))
                {
                    if (!currentExists && previousExists && transactionExists)
                    {
                        MoveDirectoryWithRetry(previousRoot, installRoot);
                    }
                    else if (!(currentExists && !previousExists && transactionExists))
                    {
                        throw InvalidRollbackRecoveryTopology(state, currentExists, previousExists, transactionExists);
                    }
                    state = "restore-swapped-step3";
                    WriteRecoveryState(statePath, state);
                    continue;
                }

                if (string.Equals(state, "restore-swapped-step3", StringComparison.Ordinal))
                {
                    if (currentExists && !previousExists && transactionExists)
                    {
                        MoveDirectoryWithRetry(transactionRoot, previousRoot);
                    }
                    else if (!(currentExists && previousExists && !transactionExists))
                    {
                        throw InvalidRollbackRecoveryTopology(state, currentExists, previousExists, transactionExists);
                    }
                    NativeFileSystem.DeleteFile(statePath);
                    log("已恢复回滚前的 current/previous 目录。");
                    return;
                }

                throw new InvalidDataException("未知的回滚恢复阶段：" + state);
            }
        }

        private void ValidateOwnedTransactionRoots(params string[] roots)
        {
            string installId = null;
            int validRootCount = 0;
            foreach (string root in roots)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }
                if (InstallOwnership.IsDirectoryEmpty(root))
                {
                    throw new InvalidDataException("事务目录为空，已拒绝自动移动或删除：" + root);
                }
                installId = InstallOwnership.EnsureOwnedInstallation(root, installId, null, log);
                validRootCount++;
            }
            if (validRootCount == 0)
            {
                throw new InvalidDataException("事务恢复时没有找到任何有效的 Codex 版本目录。");
            }
        }


    }
}
