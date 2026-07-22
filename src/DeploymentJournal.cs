using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace CodexPortableManager
{
    internal enum DeploymentOperationKind
    {
        Update = 1,
        Rollback = 2,
        Uninstall = 3
    }

    internal enum DeploymentTransactionPhase
    {
        UpdatePrepared = 10,
        UpdateOldPreviousDetached = 20,
        UpdateCurrentDetached = 30,
        UpdatePayloadActivated = 40,
        UpdateExternalStateUpdated = 50,

        RollbackPrepared = 60,
        RollbackCurrentDetached = 70,
        RollbackPreviousActivated = 80,
        RollbackSwapCompleted = 90,
        RollbackExternalStateUpdated = 95,
        RollbackRestoreRequested = 96,
        RollbackRestoreSwapped = 97,
        RollbackRestorePreviousDetached = 98,
        RollbackRestoreCurrentActivated = 99,

        UninstallPrepared = 100,
        UninstallPreviousDetached = 110,
        UninstallPayloadDetached = 120,
        UninstallExternalStateCleaned = 130
    }

    internal enum DeploymentCleanupReceiptPhase
    {
        Prepared = 1,
        Armed = 2
    }

    internal sealed class DeploymentJournalRecord
    {
        public string OperationId { get; set; }
        public DeploymentOperationKind Operation { get; set; }
        public DeploymentTransactionPhase Phase { get; set; }
        public string InstallRoot { get; set; }
        public string InstallId { get; set; }
        public bool HadCurrent { get; set; }
        public bool HadPrevious { get; set; }
        public bool CreateIntegration { get; set; }
        public DeploymentCleanupReceipt UpdateOldPreviousCleanup { get; set; }
        public DeploymentCleanupReceipt UninstallCurrentCleanup { get; set; }
        public DeploymentCleanupReceipt UninstallPreviousCleanup { get; set; }
        public bool UninstallCurrentCleanupCompleted { get; set; }
        public bool UninstallPreviousCleanupCompleted { get; set; }
        public string UpdatedUtc { get; set; }
    }

    internal sealed class DeploymentCleanupReceipt
    {
        public DeploymentCleanupReceiptPhase Phase { get; set; }
        public string OperationId { get; set; }
        public string InstallId { get; set; }
        public string CleanupRoot { get; set; }
        public string SourceDirectoryIdentity { get; set; }
        public string SourceAnchorIdentity { get; set; }
        public string DirectoryIdentity { get; set; }
    }

    internal static class DeploymentJournal
    {
        internal static Func<SafeFileHandle, string, string>
            CleanupReceiptIdentityProviderForTest { get; set; }

        public static string GetPath(string installRoot)
        {
            return installRoot + ".deployment-journal.json";
        }

        public static bool Exists(string installRoot)
        {
            string path = GetPath(installRoot);
            NativePathKind kind = NativeFileSystem.GetPathKind(path);
            if (kind == NativePathKind.Missing)
            {
                return false;
            }
            if (kind == NativePathKind.File)
            {
                return true;
            }
            throw new IOException("部署事务状态路径不是普通文件：" + path);
        }

        public static DeploymentJournalRecord Read(string installRoot)
        {
            string path = GetPath(installRoot);
            NativePathKind kind = NativeFileSystem.GetPathKind(path);
            if (kind == NativePathKind.Missing)
            {
                return null;
            }
            if (kind != NativePathKind.File)
            {
                throw new IOException("部署事务状态路径不是普通文件：" + path);
            }

            DeploymentJournalRecord record;
            try
            {
                string json = File.ReadAllText(
                    NativeFileSystem.ToExtendedPath(path),
                    Encoding.UTF8);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                ValidateSerializedShape(serializer.DeserializeObject(json), path);
                record = serializer.Deserialize<DeploymentJournalRecord>(json);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("部署事务状态损坏，已拒绝猜测恢复：" + path, exception);
            }
            Validate(record, installRoot, path);
            return record;
        }

        public static void Write(DeploymentJournalRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            record.UpdatedUtc = DateTime.UtcNow.ToString("O");
            string path = GetPath(record.InstallRoot);
            Validate(record, record.InstallRoot, path);
            NativePathKind existingKind = NativeFileSystem.GetPathKind(path);
            if (existingKind != NativePathKind.Missing &&
                existingKind != NativePathKind.File)
            {
                throw new IOException("部署事务状态路径不是普通文件位置：" + path);
            }
            string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            string extendedPath = NativeFileSystem.ToExtendedPath(path);
            string extendedTemporaryPath =
                NativeFileSystem.ToExtendedPath(temporaryPath);
            try
            {
                using (FileStream stream = new FileStream(
                    extendedTemporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(new JavaScriptSerializer().Serialize(record));
                    writer.Flush();
                    stream.Flush(true);
                }
                if (existingKind == NativePathKind.File)
                {
                    File.Replace(
                        extendedTemporaryPath,
                        extendedPath,
                        null,
                        true);
                }
                else
                {
                    File.Move(extendedTemporaryPath, extendedPath);
                }
            }
            finally
            {
                if (File.Exists(extendedTemporaryPath))
                {
                    NativeFileSystem.DeleteFile(temporaryPath);
                }
            }
        }

        public static void Delete(string installRoot)
        {
            string path = GetPath(installRoot);
            NativePathKind kind = NativeFileSystem.GetPathKind(path);
            if (kind == NativePathKind.Missing)
            {
                return;
            }
            if (kind != NativePathKind.File)
            {
                throw new IOException("部署事务状态路径不是普通文件：" + path);
            }
            NativeFileSystem.DeleteFile(path);
        }

        public static DeploymentCleanupReceipt CreateCleanupReceipt(
            DeploymentJournalRecord record,
            string sourceRoot,
            string cleanupRoot)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (string.IsNullOrWhiteSpace(sourceRoot)) throw new ArgumentException("清理来源目录不能为空。", nameof(sourceRoot));
            if (string.IsNullOrWhiteSpace(cleanupRoot)) throw new ArgumentException("清理目标目录不能为空。", nameof(cleanupRoot));
            string identity = InstallOwnership.GetManagedDirectoryIdentity(
                sourceRoot);
            string markerPath = InstallOwnership.GetMarkerPath(sourceRoot);
            return new DeploymentCleanupReceipt
            {
                Phase = DeploymentCleanupReceiptPhase.Armed,
                OperationId = record.OperationId,
                InstallId = record.InstallId,
                CleanupRoot = Path.GetFullPath(cleanupRoot),
                SourceDirectoryIdentity = identity,
                SourceAnchorIdentity = File.Exists(markerPath)
                    ? NativeFileSystem.GetPersistentFileIdentity(markerPath)
                    : null,
                DirectoryIdentity = identity
            };
        }

        public static DeploymentCleanupReceipt CreatePreparedCleanupReceipt(
            DeploymentJournalRecord record,
            string sourceRoot,
            string cleanupRoot)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            using (SafeFileHandle sourceHandle =
                InstallOwnership.OpenManagedDirectoryHandle(sourceRoot))
            {
                return CreatePreparedCleanupReceipt(
                    record,
                    sourceHandle,
                    sourceRoot,
                    cleanupRoot);
            }
        }

        public static DeploymentCleanupReceipt CreatePreparedCleanupReceipt(
            DeploymentJournalRecord record,
            SafeFileHandle sourceHandle,
            string sourceRoot,
            string cleanupRoot)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (sourceHandle == null || sourceHandle.IsInvalid)
            {
                throw new ArgumentException("待移动来源目录句柄无效。", nameof(sourceHandle));
            }
            if (string.IsNullOrWhiteSpace(sourceRoot)) throw new ArgumentException("清理来源目录不能为空。", nameof(sourceRoot));
            if (string.IsNullOrWhiteSpace(cleanupRoot)) throw new ArgumentException("清理目标目录不能为空。", nameof(cleanupRoot));
            string fullSourceRoot = Path.GetFullPath(sourceRoot);
            string sourceIdentity = InstallOwnership.GetManagedDirectoryIdentity(
                sourceHandle,
                fullSourceRoot);
            InstallOwnership.EnsureManagedDirectoryIdentity(
                fullSourceRoot,
                sourceIdentity);
            string sourceAnchorIdentity =
                NativeFileSystem.GetPersistentFileIdentity(
                    InstallOwnership.GetMarkerPath(fullSourceRoot));
            return new DeploymentCleanupReceipt
            {
                Phase = DeploymentCleanupReceiptPhase.Prepared,
                OperationId = record.OperationId,
                InstallId = record.InstallId,
                CleanupRoot = Path.GetFullPath(cleanupRoot),
                SourceDirectoryIdentity = sourceIdentity,
                SourceAnchorIdentity = sourceAnchorIdentity
            };
        }

        public static DeploymentCleanupReceipt ArmCleanupReceipt(
            DeploymentJournalRecord record,
            DeploymentCleanupReceipt receipt,
            SafeFileHandle movedDirectoryHandle,
            string cleanupRoot)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            if (movedDirectoryHandle == null || movedDirectoryHandle.IsInvalid)
            {
                throw new ArgumentException("移动后的目录句柄无效。", nameof(movedDirectoryHandle));
            }
            string fullCleanupRoot = Path.GetFullPath(cleanupRoot);
            if (receipt.Phase != DeploymentCleanupReceiptPhase.Prepared ||
                !InstallOwnership.IsManagedDirectoryIdentity(
                    receipt.SourceDirectoryIdentity) ||
                !NativeFileSystem.IsPersistentFileIdentity(
                    receipt.SourceAnchorIdentity) ||
                !string.Equals(receipt.OperationId, record.OperationId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(receipt.InstallId, record.InstallId, StringComparison.OrdinalIgnoreCase) ||
                !PathsEqual(receipt.CleanupRoot, fullCleanupRoot))
            {
                throw new InvalidDataException("只能推进与当前部署操作匹配的 Prepared 清理凭据。");
            }

            Func<SafeFileHandle, string, string> provider =
                CleanupReceiptIdentityProviderForTest;
            string identity = provider == null
                ? InstallOwnership.GetManagedDirectoryIdentity(
                    movedDirectoryHandle,
                    fullCleanupRoot)
                : provider(movedDirectoryHandle, fullCleanupRoot);
            if (!InstallOwnership.IsManagedDirectoryIdentity(identity))
            {
                throw new InvalidDataException("移动后的清理目录身份格式无效：" + fullCleanupRoot);
            }
            InstallOwnership.EnsureManagedDirectoryIdentity(
                fullCleanupRoot,
                identity);
            return new DeploymentCleanupReceipt
            {
                Phase = DeploymentCleanupReceiptPhase.Armed,
                OperationId = receipt.OperationId,
                InstallId = receipt.InstallId,
                CleanupRoot = fullCleanupRoot,
                SourceDirectoryIdentity = receipt.SourceDirectoryIdentity,
                SourceAnchorIdentity = receipt.SourceAnchorIdentity,
                DirectoryIdentity = identity
            };
        }

        public static DeploymentJournalRecord Clone(
            DeploymentJournalRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            return new DeploymentJournalRecord
            {
                OperationId = record.OperationId,
                Operation = record.Operation,
                Phase = record.Phase,
                InstallRoot = record.InstallRoot,
                InstallId = record.InstallId,
                HadCurrent = record.HadCurrent,
                HadPrevious = record.HadPrevious,
                CreateIntegration = record.CreateIntegration,
                UpdateOldPreviousCleanup = CloneCleanupReceipt(
                    record.UpdateOldPreviousCleanup),
                UninstallCurrentCleanup = CloneCleanupReceipt(
                    record.UninstallCurrentCleanup),
                UninstallPreviousCleanup = CloneCleanupReceipt(
                    record.UninstallPreviousCleanup),
                UninstallCurrentCleanupCompleted =
                    record.UninstallCurrentCleanupCompleted,
                UninstallPreviousCleanupCompleted =
                    record.UninstallPreviousCleanupCompleted,
                UpdatedUtc = record.UpdatedUtc
            };
        }

        private static void Validate(DeploymentJournalRecord record, string expectedInstallRoot, string path)
        {
            Guid operationId;
            Guid installId;
            if (record == null ||
                !Guid.TryParseExact(record.OperationId, "N", out operationId) ||
                !Guid.TryParseExact(record.InstallId, "N", out installId) ||
                !Enum.IsDefined(typeof(DeploymentTransactionPhase), record.Phase) ||
                string.IsNullOrWhiteSpace(record.InstallRoot) ||
                !PathsEqual(record.InstallRoot, expectedInstallRoot))
            {
                throw new InvalidDataException("部署事务状态格式无效：" + path);
            }
            bool phaseValid =
                (record.Operation == DeploymentOperationKind.Update &&
                 record.Phase >= DeploymentTransactionPhase.UpdatePrepared &&
                 record.Phase <= DeploymentTransactionPhase.UpdateExternalStateUpdated) ||
                (record.Operation == DeploymentOperationKind.Rollback &&
                 record.Phase >= DeploymentTransactionPhase.RollbackPrepared &&
                 record.Phase <= DeploymentTransactionPhase.RollbackRestoreCurrentActivated) ||
                (record.Operation == DeploymentOperationKind.Uninstall &&
                 record.Phase >= DeploymentTransactionPhase.UninstallPrepared &&
                 record.Phase <= DeploymentTransactionPhase.UninstallExternalStateCleaned);
            if (!phaseValid)
            {
                throw new InvalidDataException("部署事务的操作类型与阶段不匹配：" + path);
            }

            bool hasCleanupReceipt = record.UpdateOldPreviousCleanup != null ||
                record.UninstallCurrentCleanup != null ||
                record.UninstallPreviousCleanup != null;
            bool hasCompletedCleanup = record.UninstallCurrentCleanupCompleted ||
                record.UninstallPreviousCleanupCompleted;

            if (record.Operation == DeploymentOperationKind.Update)
            {
                if (record.UninstallCurrentCleanup != null ||
                    record.UninstallPreviousCleanup != null ||
                    hasCompletedCleanup)
                {
                    throw new InvalidDataException("更新事务包含了卸载清理状态：" + path);
                }
                ValidateCleanupReceipt(
                    record.UpdateOldPreviousCleanup,
                    record,
                    record.InstallRoot + ".previous.transaction-old",
                    path);
                if (record.HadPrevious != (record.UpdateOldPreviousCleanup != null))
                {
                    throw new InvalidDataException("更新事务的旧回滚清理凭据与原始拓扑不一致：" + path);
                }
                if (record.UpdateOldPreviousCleanup != null)
                {
                    DeploymentCleanupReceiptPhase expectedPhase =
                        record.Phase == DeploymentTransactionPhase.UpdatePrepared
                            ? DeploymentCleanupReceiptPhase.Prepared
                            : DeploymentCleanupReceiptPhase.Armed;
                    EnsureCleanupReceiptPhase(
                        record.UpdateOldPreviousCleanup,
                        expectedPhase,
                        path);
                }
            }
            else if (record.Operation == DeploymentOperationKind.Uninstall)
            {
                if (record.UpdateOldPreviousCleanup != null)
                {
                    throw new InvalidDataException("卸载事务包含了更新清理凭据：" + path);
                }
                ValidateCleanupReceipt(
                    record.UninstallCurrentCleanup,
                    record,
                    record.InstallRoot + ".uninstall-current",
                    path);
                ValidateCleanupReceipt(
                    record.UninstallPreviousCleanup,
                    record,
                    record.InstallRoot + ".uninstall-previous",
                    path);
                if (hasCompletedCleanup &&
                    record.Phase < DeploymentTransactionPhase.UninstallPayloadDetached)
                {
                    throw new InvalidDataException("卸载事务在提交点前记录了已完成清理：" + path);
                }
                ValidateUninstallCleanupSlot(
                    record.HadCurrent,
                    record.UninstallCurrentCleanup,
                    record.UninstallCurrentCleanupCompleted,
                    path);
                ValidateUninstallCleanupSlot(
                    record.HadPrevious,
                    record.UninstallPreviousCleanup,
                    record.UninstallPreviousCleanupCompleted,
                    path);
                if (record.UninstallCurrentCleanup != null)
                {
                    EnsureCleanupReceiptPhase(
                        record.UninstallCurrentCleanup,
                        record.Phase < DeploymentTransactionPhase.UninstallPayloadDetached
                            ? DeploymentCleanupReceiptPhase.Prepared
                            : DeploymentCleanupReceiptPhase.Armed,
                        path);
                }
                if (record.UninstallPreviousCleanup != null)
                {
                    EnsureCleanupReceiptPhase(
                        record.UninstallPreviousCleanup,
                        record.Phase < DeploymentTransactionPhase.UninstallPreviousDetached
                            ? DeploymentCleanupReceiptPhase.Prepared
                            : DeploymentCleanupReceiptPhase.Armed,
                        path);
                }
            }
            else if (hasCleanupReceipt || hasCompletedCleanup)
            {
                throw new InvalidDataException("回滚事务不能包含清理状态：" + path);
            }
        }

        private static void ValidateSerializedShape(object value, string path)
        {
            IDictionary<string, object> fields = value as IDictionary<string, object>;
            if (fields == null ||
                !HasStrictInt32(fields, "Operation") ||
                !HasStrictInt32(fields, "Phase") ||
                !HasStrictBoolean(fields, "HadCurrent") ||
                !HasStrictBoolean(fields, "HadPrevious") ||
                !HasStrictBoolean(fields, "CreateIntegration") ||
                !HasStrictBoolean(fields, "UninstallCurrentCleanupCompleted") ||
                !HasStrictBoolean(fields, "UninstallPreviousCleanupCompleted") ||
                !HasStrictReceiptPhase(fields, "UpdateOldPreviousCleanup") ||
                !HasStrictReceiptPhase(fields, "UninstallCurrentCleanup") ||
                !HasStrictReceiptPhase(fields, "UninstallPreviousCleanup"))
            {
                throw new InvalidDataException(
                    "部署事务状态缺少必需字段或字段类型无效：" + path);
            }
        }

        private static bool HasStrictBoolean(
            IDictionary<string, object> fields,
            string name)
        {
            object value;
            return fields.TryGetValue(name, out value) && value is bool;
        }

        private static bool HasStrictInt32(
            IDictionary<string, object> fields,
            string name)
        {
            int ignored;
            return TryGetStrictInt32(fields, name, out ignored);
        }

        private static bool TryGetStrictInt32(
            IDictionary<string, object> fields,
            string name,
            out int result)
        {
            object value;
            if (fields.TryGetValue(name, out value) && value is int)
            {
                result = (int)value;
                return true;
            }
            result = 0;
            return false;
        }

        private static bool HasStrictReceiptPhase(
            IDictionary<string, object> fields,
            string name)
        {
            object value;
            if (!fields.TryGetValue(name, out value) || value == null)
            {
                return true;
            }
            IDictionary<string, object> receipt =
                value as IDictionary<string, object>;
            return receipt != null && HasStrictInt32(receipt, "Phase");
        }

        private static void ValidateUninstallCleanupSlot(
            bool existedBeforeUninstall,
            DeploymentCleanupReceipt receipt,
            bool cleanupCompleted,
            string journalPath)
        {
            if (!existedBeforeUninstall)
            {
                if (receipt != null || cleanupCompleted)
                {
                    throw new InvalidDataException("卸载事务为原本不存在的槽位记录了清理状态：" + journalPath);
                }
                return;
            }

            if ((receipt != null) == cleanupCompleted)
            {
                throw new InvalidDataException("卸载事务的清理状态与原始拓扑不一致：" + journalPath);
            }
        }

        private static void ValidateCleanupReceipt(
            DeploymentCleanupReceipt receipt,
            DeploymentJournalRecord record,
            string expectedCleanupRoot,
            string journalPath)
        {
            if (receipt == null)
            {
                return;
            }
            if (!string.Equals(receipt.OperationId, record.OperationId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(receipt.InstallId, record.InstallId, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(receipt.CleanupRoot) ||
                !PathsEqual(receipt.CleanupRoot, expectedCleanupRoot))
            {
                throw new InvalidDataException("部署事务的清理凭据格式无效：" + journalPath);
            }
            if (!Enum.IsDefined(
                    typeof(DeploymentCleanupReceiptPhase),
                    receipt.Phase) ||
                receipt.Phase == DeploymentCleanupReceiptPhase.Prepared &&
                    (!InstallOwnership.IsManagedDirectoryIdentity(
                        receipt.SourceDirectoryIdentity) ||
                     !NativeFileSystem.IsPersistentFileIdentity(
                        receipt.SourceAnchorIdentity) ||
                     !string.IsNullOrWhiteSpace(receipt.DirectoryIdentity)) ||
                receipt.Phase == DeploymentCleanupReceiptPhase.Armed &&
                    (!InstallOwnership.IsManagedDirectoryIdentity(
                        receipt.SourceDirectoryIdentity) ||
                     !string.IsNullOrWhiteSpace(receipt.SourceAnchorIdentity) &&
                        !NativeFileSystem.IsPersistentFileIdentity(
                            receipt.SourceAnchorIdentity) ||
                     !InstallOwnership.IsManagedDirectoryIdentity(
                        receipt.DirectoryIdentity)))
            {
                throw new InvalidDataException("部署事务的清理凭据阶段无效：" + journalPath);
            }
        }

        private static void EnsureCleanupReceiptPhase(
            DeploymentCleanupReceipt receipt,
            DeploymentCleanupReceiptPhase expectedPhase,
            string journalPath)
        {
            if (receipt.Phase != expectedPhase)
            {
                throw new InvalidDataException("部署事务的清理凭据阶段与目录移动阶段不一致：" + journalPath);
            }
        }

        private static DeploymentCleanupReceipt CloneCleanupReceipt(
            DeploymentCleanupReceipt receipt)
        {
            if (receipt == null)
            {
                return null;
            }
            return new DeploymentCleanupReceipt
            {
                Phase = receipt.Phase,
                OperationId = receipt.OperationId,
                InstallId = receipt.InstallId,
                CleanupRoot = receipt.CleanupRoot,
                SourceDirectoryIdentity = receipt.SourceDirectoryIdentity,
                SourceAnchorIdentity = receipt.SourceAnchorIdentity,
                DirectoryIdentity = receipt.DirectoryIdentity
            };
        }

        private static bool PathsEqual(string first, string second)
        {
            try
            {
                string normalizedFirst = Path.GetFullPath(first)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedSecond = Path.GetFullPath(second)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if ((File.Exists(normalizedFirst) || Directory.Exists(normalizedFirst)) &&
                    (File.Exists(normalizedSecond) || Directory.Exists(normalizedSecond)))
                {
                    return string.Equals(
                        NativeFileSystem.GetStablePathForExistingPath(normalizedFirst),
                        NativeFileSystem.GetStablePathForExistingPath(normalizedSecond),
                        StringComparison.OrdinalIgnoreCase);
                }

                string firstParent = Path.GetDirectoryName(normalizedFirst);
                string secondParent = Path.GetDirectoryName(normalizedSecond);
                return !string.IsNullOrWhiteSpace(firstParent) &&
                    !string.IsNullOrWhiteSpace(secondParent) &&
                    Directory.Exists(firstParent) &&
                    Directory.Exists(secondParent) &&
                    string.Equals(
                        Path.GetFileName(normalizedFirst),
                        Path.GetFileName(normalizedSecond),
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        NativeFileSystem.GetStablePathForExistingPath(firstParent),
                        NativeFileSystem.GetStablePathForExistingPath(secondParent),
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
