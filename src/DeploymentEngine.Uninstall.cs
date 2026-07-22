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
        private DeploymentJournalRecord PrepareAndDetachUninstallPayload(
            DeploymentJournalRecord journal,
            string installRoot,
            string previousRoot,
            string currentTombstone,
            string previousTombstone)
        {
            SafeFileHandle currentHandle = null;
            SafeFileHandle previousHandle = null;
            try
            {
                if (journal.HadCurrent)
                {
                    currentHandle = InstallOwnership.OpenManagedDirectoryHandle(
                        installRoot);
                    InstallOwnership.EnsureOwnedInstallation(
                        installRoot,
                        journal.InstallId,
                        null,
                        log);
                    journal.UninstallCurrentCleanup =
                        DeploymentJournal.CreatePreparedCleanupReceipt(
                            journal,
                            currentHandle,
                            installRoot,
                            currentTombstone);
                }
                if (journal.HadPrevious)
                {
                    previousHandle = InstallOwnership.OpenManagedDirectoryHandle(
                        previousRoot);
                    InstallOwnership.EnsureOwnedInstallation(
                        previousRoot,
                        journal.InstallId,
                        null,
                        log);
                    journal.UninstallPreviousCleanup =
                        DeploymentJournal.CreatePreparedCleanupReceipt(
                            journal,
                            previousHandle,
                            previousRoot,
                            previousTombstone);
                }
                DeploymentJournal.Write(journal);
                PrepareIntegrationCleanup(
                    installRoot,
                    journal.HadCurrent ? installRoot : previousRoot,
                    journal.InstallId,
                    journal.OperationId);

                if (journal.HadPrevious)
                {
                    MoveDirectoryWithRetry(previousRoot, previousTombstone);
                    DeploymentJournalRecord candidate =
                        DeploymentJournal.Clone(journal);
                    candidate.UninstallPreviousCleanup =
                        DeploymentJournal.ArmCleanupReceipt(
                            candidate,
                            candidate.UninstallPreviousCleanup,
                            previousHandle,
                            previousTombstone);
                    candidate.Phase =
                        DeploymentTransactionPhase.UninstallPreviousDetached;
                    DeploymentJournal.Write(candidate);
                    journal = candidate;
                }
                if (journal.HadCurrent)
                {
                    MoveDirectoryWithRetry(installRoot, currentTombstone);
                    DeploymentJournalRecord candidate =
                        DeploymentJournal.Clone(journal);
                    candidate.UninstallCurrentCleanup =
                        DeploymentJournal.ArmCleanupReceipt(
                            candidate,
                            candidate.UninstallCurrentCleanup,
                            currentHandle,
                            currentTombstone);
                    candidate.Phase =
                        DeploymentTransactionPhase.UninstallPayloadDetached;
                    DeploymentJournal.Write(candidate);
                    journal = candidate;
                }
                else
                {
                    journal = PersistJournalPhase(
                        journal,
                        DeploymentTransactionPhase.UninstallPayloadDetached);
                }
                return journal;
            }
            finally
            {
                if (previousHandle != null)
                {
                    previousHandle.Dispose();
                }
                if (currentHandle != null)
                {
                    currentHandle.Dispose();
                }
            }
        }

        private void ValidateOwnedActiveRoot(string root, string installId)
        {
            EnsureDirectoryPathIsNotFile(root, "受管安装槽");
            if (!Directory.Exists(root))
            {
                return;
            }
            if (InstallOwnership.IsDirectoryEmpty(root))
            {
                throw new InvalidDataException("活动安装槽为空，已拒绝自动移动：" + root);
            }
            InstallOwnership.EnsureOwnedInstallation(root, installId, null, log);
        }

        private static void ValidateCleanupRoot(
            DeploymentJournalRecord journal,
            DeploymentCleanupReceipt receipt,
            string cleanupRoot)
        {
            if (!DirectoryExistsStrict(cleanupRoot, "事务清理路径"))
            {
                return;
            }
            string normalizedRoot = Path.GetFullPath(cleanupRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string receiptRoot = receipt == null || string.IsNullOrWhiteSpace(receipt.CleanupRoot)
                ? null
                : Path.GetFullPath(receipt.CleanupRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (receipt == null ||
                !string.Equals(receipt.OperationId, journal.OperationId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(receipt.InstallId, journal.InstallId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(receiptRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                receipt.Phase != DeploymentCleanupReceiptPhase.Armed)
            {
                throw new InvalidDataException("清理目录缺少与当前部署操作匹配的持久凭据：" + cleanupRoot);
            }
            InstallOwnership.EnsureManagedDirectoryIdentity(
                cleanupRoot,
                receipt.DirectoryIdentity);
        }

        private void ValidateCleanupRootForRollback(
            DeploymentJournalRecord journal,
            DeploymentCleanupReceipt receipt,
            string cleanupRoot)
        {
            if (!DirectoryExistsStrict(cleanupRoot, "待回滚事务目录"))
            {
                return;
            }
            if (receipt != null &&
                receipt.Phase == DeploymentCleanupReceiptPhase.Prepared)
            {
                InstallOwnership.EnsureManagedDirectoryIdentity(
                    cleanupRoot,
                    receipt.SourceDirectoryIdentity);
                NativeFileSystem.EnsurePersistentFileIdentity(
                    InstallOwnership.GetMarkerPath(cleanupRoot),
                    receipt.SourceAnchorIdentity);
                InstallOwnership.EnsureOwnedInstallation(
                    cleanupRoot,
                    journal.InstallId,
                    null,
                    log);
                return;
            }
            ValidateCleanupRoot(journal, receipt, cleanupRoot);
        }

        private bool TryDeleteCleanupDirectory(
            DeploymentJournalRecord journal,
            DeploymentCleanupReceipt receipt,
            string path,
            string allowedParent,
            string description)
        {
            try
            {
                if (receipt == null)
                {
                    if (DirectoryExistsStrict(path, description))
                    {
                        return false;
                    }
                    Action<string> observer =
                        MissingCleanupReceiptObservedForTest;
                    if (observer != null)
                    {
                        observer(path);
                    }
                    return true;
                }
                ValidateCleanupRoot(journal, receipt, path);
                DeleteDirectorySafely(
                    path,
                    allowedParent,
                    receipt.DirectoryIdentity);
                return !DirectoryExistsStrict(path, description);
            }
            catch (IOException exception)
            {
                log("警告：无法清理" + description + "：" + path + "。" + exception.Message);
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                log("警告：无法清理" + description + "：" + path + "。" + exception.Message);
                return false;
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                log("警告：无法可靠探测或清理" + description + "：" + path + "。" + exception.Message);
                return false;
            }
        }

        private static Exception InvalidRollbackRecoveryTopology(
            string state,
            bool currentExists,
            bool previousExists,
            bool transactionExists)
        {
            return new IOException(string.Format(
                CultureInfo.InvariantCulture,
                "回滚恢复目录拓扑与阶段 {0} 不一致：current={1}, previous={2}, transaction={3}。",
                state,
                currentExists,
                previousExists,
                transactionExists));
        }

        internal static string GetRollbackRecoveryStatePath(string installRoot)
        {
            return installRoot + ".rollback-recovery.state";
        }

        internal static void WriteRecoveryState(string statePath, string state)
        {
            string temporaryPath = statePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                byte[] content = Encoding.ASCII.GetBytes(state + Environment.NewLine);
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(content, 0, content.Length);
                    stream.Flush(true);
                }
                if (File.Exists(statePath))
                {
                    File.Replace(temporaryPath, statePath, null, true);
                }
                else
                {
                    File.Move(temporaryPath, statePath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    NativeFileSystem.DeleteFile(temporaryPath);
                }
            }
        }

        public UninstallResult UninstallPortable(
            string installRoot,
            LegacyAdoptionApproval adoptionApproval)
        {
            return UninstallPortable(
                installRoot,
                adoptionApproval,
                true);
        }

        internal UninstallResult DetachPortableForUninstall(
            string installRoot,
            LegacyAdoptionApproval adoptionApproval)
        {
            return UninstallPortable(
                installRoot,
                adoptionApproval,
                false);
        }

        private UninstallResult UninstallPortable(
            string installRoot,
            LegacyAdoptionApproval adoptionApproval,
            bool completeDirectoryCleanup)
        {
            installRoot = ValidateInstallRoot(installRoot);
            using (OperationFileLock operationLock = OperationFileLock.Acquire(installRoot))
            {
                RecoverPendingCompatibilityMaintenance(installRoot);
                return UninstallPortableCore(
                    installRoot,
                    adoptionApproval,
                    completeDirectoryCleanup);
            }
        }

        internal void RecoverPendingCompatibilityMaintenance(string installRoot)
        {
            if (!CompatibilityTransaction.Exists(installRoot)) return;
            ProcessesUnderPath.Stop(installRoot);
            ProcessesUnderPath.WaitForExit(installRoot, TimeSpan.FromSeconds(15));
            CompatibilityTransaction.RecoverPending(installRoot, log);
        }

        internal DeploymentRecoveryResult RecoverPendingDeploymentUnderLock(string installRoot)
        {
            installRoot = ValidateInstallRoot(installRoot);
            string parentRoot = Directory.GetParent(installRoot).FullName;
            RecoverPendingCompatibilityMaintenance(installRoot);
            return RecoverInterruptedDeployment(installRoot, parentRoot);
        }

        private UninstallResult UninstallPortableCore(
            string installRoot,
            LegacyAdoptionApproval adoptionApproval,
            bool completeDirectoryCleanup)
        {
            string previousRoot = installRoot + ".previous";
            string parentRoot = Directory.GetParent(installRoot).FullName;
            bool recoveringUninstall = DeploymentJournal.Exists(installRoot);
            DeploymentRecoveryResult recovery = RecoverInterruptedDeployment(
                installRoot,
                parentRoot);
            if (recovery.OldBackupCleanupPending)
            {
                throw new IOException(
                    "上次更新的旧回滚备份仍待清理，完成清理前不能开始卸载。");
            }
            if (!Directory.Exists(installRoot) && !Directory.Exists(previousRoot))
            {
                if (recoveringUninstall)
                {
                    log("上次中断的 Codex 便携版卸载已经恢复完成。");
                    return new UninstallResult(
                        DeploymentJournal.Exists(installRoot),
                        ShellIntegration.IsCleanupPendingForRoot(installRoot),
                        new string[0]);
                }
                throw new InvalidOperationException("没有检测到可卸载的 Codex 便携版。");
            }

            if (Directory.Exists(installRoot) && InstallOwnership.IsDirectoryEmpty(installRoot))
            {
                NativeFileSystem.DeleteEmptyDirectory(installRoot);
            }
            if (Directory.Exists(previousRoot) && InstallOwnership.IsDirectoryEmpty(previousRoot))
            {
                NativeFileSystem.DeleteEmptyDirectory(previousRoot);
            }

            string installId = null;
            if (Directory.Exists(installRoot))
            {
                installId = InstallOwnership.EnsureOwnedInstallation(installRoot, null, adoptionApproval, log);
            }
            if (Directory.Exists(previousRoot))
            {
                installId = InstallOwnership.EnsureOwnedInstallation(previousRoot, installId, adoptionApproval, log);
            }
            if (installId == null)
            {
                throw new InvalidOperationException("安装目录和回滚目录都不是有效的 Codex 便携安装，已拒绝删除。");
            }

            ProcessesUnderPath.Stop(installRoot);
            ProcessesUnderPath.Stop(previousRoot);
            ProcessesUnderPath.WaitForExit(installRoot, TimeSpan.FromSeconds(15));
            ProcessesUnderPath.WaitForExit(previousRoot, TimeSpan.FromSeconds(5));

            string currentTombstone = GetUninstallCurrentTombstone(installRoot);
            string previousTombstone = GetUninstallPreviousTombstone(installRoot);
            if (Directory.Exists(currentTombstone) || Directory.Exists(previousTombstone))
            {
                throw new IOException("检测到没有 journal 的卸载残留目录，已拒绝覆盖：" + installRoot);
            }

            DeploymentJournalRecord journal = new DeploymentJournalRecord
            {
                OperationId = Guid.NewGuid().ToString("N"),
                Operation = DeploymentOperationKind.Uninstall,
                Phase = DeploymentTransactionPhase.UninstallPrepared,
                InstallRoot = installRoot,
                InstallId = installId,
                HadCurrent = Directory.Exists(installRoot),
                HadPrevious = Directory.Exists(previousRoot)
            };
            ShellIntegrationCleanupResult integrationCleanup =
                new ShellIntegrationCleanupResult(true, new string[0]);
            bool directoryCleanupPending = false;

            try
            {
                journal = PrepareAndDetachUninstallPayload(
                    journal,
                    installRoot,
                    previousRoot,
                    currentTombstone,
                    previousTombstone);

                integrationCleanup = RemoveIntegration(
                    installRoot,
                    journal.HadCurrent ? currentTombstone : previousTombstone,
                    installId,
                    journal.OperationId);
                if (integrationCleanup.Complete)
                {
                    journal = PersistJournalPhase(
                        journal,
                        DeploymentTransactionPhase.UninstallExternalStateCleaned);
                }
                else
                {
                    log("程序目录已从活动槽移除，但部分系统集成将在后续启动继续清理。");
                }
                if (completeDirectoryCleanup)
                {
                    directoryCleanupPending = !CompleteUninstallCleanup(journal, parentRoot);
                }
                else
                {
                    directoryCleanupPending = DeploymentJournal.Exists(installRoot);
                    log("Codex 便携版已经完成逻辑卸载，程序文件将由独立后台任务继续清理。");
                }
            }
            catch (Exception operationError)
            {
                bool committed = IsUninstallCommitted(journal);
                Exception recoveryError = null;
                try
                {
                    RecoverInterruptedUninstall(journal, parentRoot);
                }
                catch (Exception exception)
                {
                    recoveryError = exception;
                }
                if (recoveryError != null)
                {
                    throw new AggregateException("卸载失败，且恢复卸载事务时再次失败。", operationError, recoveryError);
                }
                if (!committed)
                {
                    throw;
                }
                log("卸载提交后出现错误，但事务恢复已完成：" + operationError.Message);
                directoryCleanupPending = DeploymentJournal.Exists(installRoot);
            }

            bool integrationCleanupPending =
                !integrationCleanup.Complete ||
                ShellIntegration.IsCleanupPendingForRoot(installRoot);
            log(directoryCleanupPending || integrationCleanupPending
                ? "Codex 便携版已从活动槽移除，剩余清理将在后续操作继续；用户资料和管理器缓存已保留。"
                : "Codex 便携版及其回滚备份已卸载，用户资料和管理器缓存已保留。");
            return new UninstallResult(
                directoryCleanupPending,
                integrationCleanupPending,
                integrationCleanup.Warnings);
        }

        private bool RecoverInterruptedUninstall(DeploymentJournalRecord journal, string parentRoot)
        {
            string installRoot = journal.InstallRoot;
            string previousRoot = installRoot + ".previous";
            string currentTombstone = GetUninstallCurrentTombstone(installRoot);
            string previousTombstone = GetUninstallPreviousTombstone(installRoot);
            ValidateOwnedActiveRoot(installRoot, journal.InstallId);
            ValidateOwnedActiveRoot(previousRoot, journal.InstallId);

            if (!IsUninstallCommitted(journal))
            {
                ValidateCleanupRootForRollback(
                    journal,
                    journal.UninstallCurrentCleanup,
                    currentTombstone);
                ValidateCleanupRootForRollback(
                    journal,
                    journal.UninstallPreviousCleanup,
                    previousTombstone);
                if (Directory.Exists(currentTombstone))
                {
                    if (Directory.Exists(installRoot)) throw new IOException("恢复卸载时 current 及其 tombstone 同时存在。");
                    MoveDirectoryWithRetry(currentTombstone, installRoot);
                }
                if (Directory.Exists(previousTombstone))
                {
                    if (Directory.Exists(previousRoot)) throw new IOException("恢复卸载时 previous 及其 tombstone 同时存在。");
                    MoveDirectoryWithRetry(previousTombstone, previousRoot);
                }
                CancelPreparedIntegrationCleanup(
                    installRoot,
                    journal.InstallId,
                    journal.OperationId);
                DeploymentJournal.Delete(installRoot);
                log("已恢复尚未提交的卸载事务，current/previous 保持原位。");
                return false;
            }

            if (Directory.Exists(installRoot) || Directory.Exists(previousRoot))
            {
                throw new IOException("已提交卸载的正式槽位重新出现，已拒绝继续自动清理。");
            }
            bool journalChanged = false;
            if (journal.Phase < DeploymentTransactionPhase.UninstallPayloadDetached)
            {
                journal.Phase = DeploymentTransactionPhase.UninstallPayloadDetached;
                journalChanged = true;
            }
            if (journalChanged)
            {
                DeploymentJournal.Write(journal);
            }
            ValidateCleanupRoot(journal, journal.UninstallCurrentCleanup, currentTombstone);
            ValidateCleanupRoot(journal, journal.UninstallPreviousCleanup, previousTombstone);
            if (journal.Phase < DeploymentTransactionPhase.UninstallExternalStateCleaned)
            {
                string cleanupSource = DirectoryExistsStrict(
                    currentTombstone,
                    "卸载事务 current tombstone")
                    ? currentTombstone
                    : previousTombstone;
                ShellIntegrationCleanupResult integrationCleanup = RemoveIntegration(
                    installRoot,
                    cleanupSource,
                    journal.InstallId,
                    journal.OperationId);
                if (integrationCleanup.Complete)
                {
                    journal.Phase = DeploymentTransactionPhase.UninstallExternalStateCleaned;
                    DeploymentJournal.Write(journal);
                }
                else
                {
                    log("卸载程序目录已经提交，系统集成仍待后续启动继续清理。");
                }
            }
            return !CompleteUninstallCleanup(journal, parentRoot);
        }

        private bool CompleteUninstallCleanup(
            DeploymentJournalRecord journal,
            string parentRoot)
        {
            string currentTombstone = GetUninstallCurrentTombstone(journal.InstallRoot);
            string previousTombstone = GetUninstallPreviousTombstone(journal.InstallRoot);
            bool currentRemoved = TryDeleteCleanupDirectory(
                    journal,
                    journal.UninstallCurrentCleanup,
                    currentTombstone,
                    parentRoot,
                    "卸载事务中的当前版本");
            bool previousRemoved = TryDeleteCleanupDirectory(
                    journal,
                    journal.UninstallPreviousCleanup,
                    previousTombstone,
                    parentRoot,
                    "卸载事务中的回滚版本");
            bool cleanupStateChanged = false;
            if (currentRemoved &&
                journal.HadCurrent &&
                !journal.UninstallCurrentCleanupCompleted)
            {
                journal.UninstallCurrentCleanup = null;
                journal.UninstallCurrentCleanupCompleted = true;
                cleanupStateChanged = true;
            }
            if (previousRemoved &&
                journal.HadPrevious &&
                !journal.UninstallPreviousCleanupCompleted)
            {
                journal.UninstallPreviousCleanup = null;
                journal.UninstallPreviousCleanupCompleted = true;
                cleanupStateChanged = true;
            }
            if (currentRemoved && previousRemoved)
            {
                if (TryDeleteDeploymentJournal(journal.InstallRoot, "已完成卸载事务"))
                {
                    log("卸载事务的程序目录清理完成。");
                    return true;
                }
                log("卸载目录已经清理，但事务元数据仍待清理。");
                return false;
            }
            if (cleanupStateChanged)
            {
                try
                {
                    DeploymentJournal.Write(journal);
                }
                catch (Exception exception)
                {
                    log("警告：卸载目录已清理，但完成状态暂未写回 journal：" + exception.Message);
                    return false;
                }
            }
            log("卸载已提交，但部分 tombstone 暂未清理；后续操作会继续恢复。");
            return false;
        }

        private static void EnsureNoPendingDeploymentCleanup(
            DeploymentRecoveryResult recovery)
        {
            if (recovery == null || !recovery.HasPendingCleanup)
            {
                return;
            }
            if (recovery.OldBackupCleanupPending)
            {
                throw new IOException(
                    "上次更新的旧回滚备份仍待清理，请关闭占用文件的程序后重试。");
            }
            throw new IOException(
                "上次卸载的程序目录仍待清理，请关闭占用文件的程序后重试。");
        }

        private static bool IsUninstallCommitted(DeploymentJournalRecord journal)
        {
            if (journal.Phase >= DeploymentTransactionPhase.UninstallPayloadDetached)
            {
                return true;
            }
            return false;
        }

        private static void EnsureDirectoryPathIsNotFile(
            string path,
            string description)
        {
            DirectoryExistsStrict(path, description);
        }

        private static DeploymentJournalRecord PersistJournalPhase(
            DeploymentJournalRecord journal,
            DeploymentTransactionPhase phase)
        {
            DeploymentJournalRecord candidate = DeploymentJournal.Clone(journal);
            candidate.Phase = phase;
            DeploymentJournal.Write(candidate);
            return candidate;
        }

        private static bool DirectoryExistsStrict(string path, string description)
        {
            NativePathKind kind = NativeFileSystem.GetPathKind(path);
            if (kind == NativePathKind.Missing)
            {
                return false;
            }
            if (kind == NativePathKind.Directory)
            {
                return true;
            }
            throw new IOException(
                description + (kind == NativePathKind.ReparsePoint
                    ? "被重解析点占用："
                    : "被普通文件占用：") + path);
        }

        internal static string GetUninstallCurrentTombstone(string installRoot)
        {
            return installRoot + ".uninstall-current";
        }

        internal static string GetUninstallPreviousTombstone(string installRoot)
        {
            return installRoot + ".uninstall-previous";
        }


    }
}
