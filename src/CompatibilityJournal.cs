using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexPortableManager
{
    internal enum CompatibilityTransactionPhase
    {
        Preparing = 10,
        Prepared = 20,
        Mutating = 30,
        FilesChanged = 40,
        Committed = 50
    }

    internal sealed class CompatibilityJournalArtifact
    {
        public string RelativePath { get; set; }
        public bool OriginalExists { get; set; }
        public string OriginalSha256 { get; set; }
        public string BackupName { get; set; }
        public bool TargetExists { get; set; }
        public string TargetSha256 { get; set; }
        public bool Modified { get; set; }
    }

    internal sealed class CompatibilityArtifactState
    {
        public string RelativePath { get; internal set; }
        public bool Exists { get; internal set; }
        public string Sha256 { get; internal set; }
    }

    internal sealed class CompatibilityJournalRecord
    {
        public int SchemaVersion { get; set; }
        public string OperationId { get; set; }
        public string InstallRoot { get; set; }
        public string InstallRootIdentity { get; set; }
        public string InstallId { get; set; }
        public bool InstallMarkerRequired { get; set; }
        public CompatibilityTransactionPhase Phase { get; set; }
        public CompatibilityOptionsSnapshot TargetOptions { get; set; }
        public List<CompatibilityJournalArtifact> Artifacts { get; set; }
        public string BackupDirectoryIdentity { get; set; }
        public string UpdatedUtc { get; set; }
    }

    internal sealed class CompatibilityOptionsSnapshot
    {
        public bool SandboxCompatibilityEnabled { get; set; }
        public bool UnlockModelCatalogEnabled { get; set; }
        public bool SupplementChineseUiEnabled { get; set; }
        public bool EnglishTechnicalParametersEnabled { get; set; }
        public bool ReasoningDisplayEnabled { get; set; }
        public bool ManageSandboxCompatibility { get; set; }
        public bool ManageModelCatalog { get; set; }
        public bool ManageLocalization { get; set; }
        public bool ManageReasoningDisplay { get; set; }

        internal static CompatibilityOptionsSnapshot From(CompatibilityOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            return new CompatibilityOptionsSnapshot
            {
                SandboxCompatibilityEnabled = options.SandboxCompatibilityEnabled,
                UnlockModelCatalogEnabled = options.UnlockModelCatalogEnabled,
                SupplementChineseUiEnabled = options.SupplementChineseUiEnabled,
                EnglishTechnicalParametersEnabled = options.EnglishTechnicalParametersEnabled,
                ReasoningDisplayEnabled = options.ReasoningDisplayEnabled,
                ManageSandboxCompatibility = options.ManageSandboxCompatibility,
                ManageModelCatalog = options.ManageModelCatalog,
                ManageLocalization = options.ManageLocalization,
                ManageReasoningDisplay = options.ManageReasoningDisplay
            };
        }
    }

    internal sealed class CompatibilityTransaction
    {
        private const int CurrentSchemaVersion = 2;
        private readonly CompatibilityJournalRecord record;

        private CompatibilityTransaction(CompatibilityJournalRecord journalRecord)
        {
            record = journalRecord;
        }

        internal IReadOnlyList<CompatibilityArtifactState> ChangedArtifacts
        {
            get
            {
                return record.Artifacts
                    .Where(artifact => artifact.Modified)
                    .Select(artifact => new CompatibilityArtifactState
                    {
                        RelativePath = artifact.RelativePath,
                        Exists = artifact.TargetExists,
                        Sha256 = artifact.TargetSha256
                    })
                    .ToList()
                    .AsReadOnly();
            }
        }

        internal static string GetJournalPath(string installRoot)
        {
            return NormalizeRoot(installRoot) + ".compatibility-journal.json";
        }

        internal static bool Exists(string installRoot)
        {
            string path = GetJournalPath(installRoot);
            return File.Exists(path) || Directory.Exists(path);
        }

        internal static CompatibilityTransaction Begin(
            string installRoot,
            string installId,
            CompatibilityOptions options,
            IEnumerable<string> candidateRelativePaths)
        {
            string root = NormalizeRoot(installRoot);
            Guid parsedInstallId;
            if (!Guid.TryParseExact(installId, "N", out parsedInstallId))
            {
                throw new InvalidDataException("兼容维护事务的安装 ID 格式无效。");
            }
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (candidateRelativePaths == null) throw new ArgumentNullException(nameof(candidateRelativePaths));
            InstallOwnership.EnsureManagedDirectoryPath(root, false);
            string installRootIdentity = InstallOwnership.GetManagedDirectoryIdentity(root);
            string markerPath = InstallOwnership.GetMarkerPath(root);
            if (Directory.Exists(markerPath))
            {
                throw new InvalidDataException("兼容维护安装 marker 路径被目录占用：" + markerPath);
            }
            bool installMarkerRequired = File.Exists(markerPath);
            if (installMarkerRequired)
            {
                EnsureCurrentInstallId(root, installId);
            }
            if (File.Exists(GetJournalPath(root)) || Directory.Exists(GetJournalPath(root)))
            {
                throw new IOException("存在尚未恢复的兼容维护事务，必须先完成恢复：" + GetJournalPath(root));
            }

            List<string> candidates = candidateRelativePaths
                .Select(NormalizeRelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException("兼容维护事务没有需要保护的制品。");
            }

            CompatibilityJournalRecord journal = new CompatibilityJournalRecord
            {
                SchemaVersion = CurrentSchemaVersion,
                OperationId = Guid.NewGuid().ToString("N"),
                InstallRoot = root,
                InstallRootIdentity = installRootIdentity,
                InstallId = installId,
                InstallMarkerRequired = installMarkerRequired,
                Phase = CompatibilityTransactionPhase.Preparing,
                TargetOptions = CompatibilityOptionsSnapshot.From(options),
                Artifacts = candidates.Select(path => new CompatibilityJournalArtifact
                {
                    RelativePath = path
                }).ToList()
            };

            Write(journal);
            string backupRoot = GetBackupRoot(journal);
            try
            {
                Directory.CreateDirectory(backupRoot);
                if ((File.GetAttributes(backupRoot) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("兼容维护备份目录不能是重解析点：" + backupRoot);
                }
                journal.BackupDirectoryIdentity =
                    InstallOwnership.GetManagedDirectoryIdentity(backupRoot);
                Write(journal);
                for (int index = 0; index < journal.Artifacts.Count; index++)
                {
                    CompatibilityJournalArtifact artifact = journal.Artifacts[index];
                    string sourcePath = ResolveProtectedArtifactPath(
                        root,
                        artifact.RelativePath);
                    if (Directory.Exists(sourcePath))
                    {
                        throw new IOException("兼容维护制品路径被目录占用：" + sourcePath);
                    }
                    if (!File.Exists(sourcePath)) continue;
                    if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException("兼容维护制品不能是重解析点：" + sourcePath);
                    }

                    artifact.OriginalExists = true;
                    artifact.BackupName = index.ToString("D4", CultureInfo.InvariantCulture) + ".bak";
                    artifact.OriginalSha256 = CopyAndHash(
                        sourcePath,
                        Path.Combine(backupRoot, artifact.BackupName));
                }

                journal.Phase = CompatibilityTransactionPhase.Prepared;
                Write(journal);
                return new CompatibilityTransaction(journal);
            }
            catch
            {
                TryDeleteJournalAndBackups(journal);
                throw;
            }
        }

        internal void BeginMutation()
        {
            RequirePhase(CompatibilityTransactionPhase.Prepared);
            record.Phase = CompatibilityTransactionPhase.Mutating;
            Write(record);
        }

        internal void VerifyOriginalArtifacts(
            ArtifactProvenance baseline,
            IEnumerable<string> relativePaths)
        {
            RequirePhase(CompatibilityTransactionPhase.Prepared);
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            if (relativePaths == null) throw new ArgumentNullException(nameof(relativePaths));
            Dictionary<string, ArtifactDigest> expected = (baseline.Artifacts ?? new List<ArtifactDigest>())
                .Where(artifact => artifact != null)
                .ToDictionary(
                    artifact => NormalizeRelativePath(artifact.RelativePath),
                    artifact => artifact,
                    StringComparer.OrdinalIgnoreCase);
            Dictionary<string, CompatibilityJournalArtifact> originals = record.Artifacts
                .ToDictionary(
                    artifact => artifact.RelativePath,
                    artifact => artifact,
                    StringComparer.OrdinalIgnoreCase);

            foreach (string requestedPath in relativePaths.Select(NormalizeRelativePath))
            {
                CompatibilityJournalArtifact original;
                if (!originals.TryGetValue(requestedPath, out original))
                {
                    throw new InvalidOperationException("兼容维护事务没有保护待验证制品：" + requestedPath);
                }
                ArtifactDigest expectedArtifact;
                bool expectedExists = expected.TryGetValue(requestedPath, out expectedArtifact);
                if (original.OriginalExists != expectedExists ||
                    expectedExists && !ArtifactHash.FixedTimeEquals(
                        original.OriginalSha256,
                        expectedArtifact.Sha256))
                {
                    throw new InvalidDataException(
                        "兼容维护制品在健康预检与事务备份之间发生变化，已拒绝继续：" + requestedPath);
                }
            }
        }

        internal void CaptureChanges()
        {
            if (record.Phase != CompatibilityTransactionPhase.Mutating &&
                record.Phase != CompatibilityTransactionPhase.FilesChanged)
            {
                throw new InvalidOperationException("兼容维护事务尚未进入可记录变更的阶段。");
            }

            CaptureCurrentState(record);
            record.Phase = CompatibilityTransactionPhase.FilesChanged;
            Write(record);
        }

        internal void Commit(string markerRelativePath)
        {
            RequirePhase(CompatibilityTransactionPhase.FilesChanged);
            string normalizedMarker = NormalizeRelativePath(markerRelativePath);
            CompatibilityJournalArtifact marker = record.Artifacts.SingleOrDefault(artifact => string.Equals(
                artifact.RelativePath,
                normalizedMarker,
                StringComparison.OrdinalIgnoreCase));
            if (marker == null)
            {
                throw new InvalidOperationException("兼容维护事务没有保护安装 marker。");
            }
            foreach (CompatibilityJournalArtifact artifact in record.Artifacts)
            {
                if (ReferenceEquals(artifact, marker)) continue;
                VerifyTarget(record, artifact);
            }
            CaptureCurrentState(record, marker);
            record.Phase = CompatibilityTransactionPhase.Committed;
            Write(record);
            try { Cleanup(record); }
            catch
            {
                // Committed 已经持久化；后续维护会只完成清理，不得反向回滚已提交结果。
            }
        }

        internal void Rollback()
        {
            RestoreOriginals(record);
            Cleanup(record);
        }

        internal static bool RecoverPending(string installRoot, Action<string> log)
        {
            string root = NormalizeRoot(installRoot);
            CompatibilityJournalRecord journal = Read(root);
            if (journal == null) return false;
            EnsureRecoveryTargetIdentity(journal);

            if (journal.Phase == CompatibilityTransactionPhase.Preparing)
            {
                Cleanup(journal);
                SafeLog(log, "已清理未进入修改阶段的兼容维护事务。");
                return true;
            }
            if (journal.Phase == CompatibilityTransactionPhase.Committed)
            {
                VerifyTargets(journal);
                Cleanup(journal);
                SafeLog(log, "已完成上次兼容维护事务的提交后清理。");
                return true;
            }

            RestoreOriginals(journal);
            Cleanup(journal);
            SafeLog(log, "检测到未提交的兼容维护事务，已从可信备份恢复全部相关文件。");
            return true;
        }

        internal static CompatibilityMaintenancePreflight PreflightPendingRecovery(
            string installRoot)
        {
            string root = NormalizeRoot(installRoot);
            CompatibilityJournalRecord journal = Read(root);
            if (journal == null)
            {
                throw new InvalidOperationException("兼容维护事务已在预检期间消失，请重试操作。");
            }
            EnsureRecoveryTargetIdentity(journal);
            return new CompatibilityMaintenancePreflight(
                root,
                journal.InstallRootIdentity,
                journal.InstallId,
                false);
        }

        private static CompatibilityJournalRecord Read(string installRoot)
        {
            string path = GetJournalPath(installRoot);
            if (Directory.Exists(path))
            {
                throw new IOException("兼容维护事务状态路径被目录占用：" + path);
            }
            if (!File.Exists(path)) return null;

            CompatibilityJournalRecord journal;
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                bool legacy = ValidateSerializedShape(
                    serializer.DeserializeObject(json),
                    path);
                journal = serializer.Deserialize<CompatibilityJournalRecord>(json);
                if (legacy)
                {
                    // 旧日志已经按七字段原始形状完成严格验证；升级仅用于继续安全恢复。
                    journal.SchemaVersion = CurrentSchemaVersion;
                }
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("兼容维护事务状态损坏，已拒绝猜测恢复：" + path, exception);
            }
            Validate(journal, installRoot, path);
            return journal;
        }

        private static void Write(CompatibilityJournalRecord journal)
        {
            if (journal == null) throw new ArgumentNullException(nameof(journal));
            journal.UpdatedUtc = DateTime.UtcNow.ToString("O");
            string path = GetJournalPath(journal.InstallRoot);
            Validate(journal, journal.InstallRoot, path);
            string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(new JavaScriptSerializer().Serialize(journal));
                    writer.Flush();
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporaryPath, path, null, true);
                else File.Move(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath)) NativeFileSystem.DeleteFile(temporaryPath);
            }
        }

        private static void CaptureCurrentState(CompatibilityJournalRecord journal)
        {
            foreach (CompatibilityJournalArtifact artifact in journal.Artifacts)
            {
                CaptureCurrentState(journal, artifact);
            }
        }

        private static void CaptureCurrentState(
            CompatibilityJournalRecord journal,
            CompatibilityJournalArtifact artifact)
        {
            string path = ResolveProtectedArtifactPath(
                journal.InstallRoot,
                artifact.RelativePath);
            if (Directory.Exists(path))
            {
                throw new IOException("兼容维护制品路径被目录占用：" + path);
            }
            if (File.Exists(path) && IsReparsePoint(path))
            {
                throw new InvalidDataException("兼容维护制品在事务期间变为重解析点：" + path);
            }
            artifact.TargetExists = File.Exists(path);
            artifact.TargetSha256 = artifact.TargetExists ? HashFile(path) : null;
            artifact.Modified = artifact.OriginalExists != artifact.TargetExists ||
                (artifact.OriginalExists && !ArtifactHash.FixedTimeEquals(
                    artifact.OriginalSha256,
                    artifact.TargetSha256));
        }

        private static void RestoreOriginals(CompatibilityJournalRecord journal)
        {
            bool requiresBackup = journal.Artifacts.Any(artifact =>
                artifact.OriginalExists && !MatchesOriginal(journal, artifact));
            string backupRoot = GetBackupRoot(journal);
            if (requiresBackup &&
                (!Directory.Exists(backupRoot) ||
                 (File.GetAttributes(backupRoot) & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidDataException("兼容维护备份目录缺失或已变为重解析点：" + backupRoot);
            }
            string markerName = Path.GetFileName(InstallOwnership.GetMarkerPath(journal.InstallRoot));
            IEnumerable<CompatibilityJournalArtifact> ordered = journal.Artifacts
                .OrderBy(artifact => string.Equals(
                    artifact.RelativePath,
                    markerName,
                    StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            foreach (CompatibilityJournalArtifact artifact in ordered)
            {
                RestoreArtifact(journal, artifact);
            }
            foreach (CompatibilityJournalArtifact artifact in journal.Artifacts)
            {
                if (!MatchesOriginal(journal, artifact))
                {
                    throw new IOException("兼容维护事务回滚后制品仍与原摘要不一致：" + artifact.RelativePath);
                }
            }
        }

        private static void RestoreArtifact(
            CompatibilityJournalRecord journal,
            CompatibilityJournalArtifact artifact)
        {
            string destination = ResolveProtectedArtifactPath(
                journal.InstallRoot,
                artifact.RelativePath);
            if (MatchesOriginal(journal, artifact)) return;
            if (!artifact.OriginalExists)
            {
                if (Directory.Exists(destination))
                {
                    throw new IOException("兼容维护回滚目标被目录占用：" + destination);
                }
                if (File.Exists(destination)) NativeFileSystem.DeleteFile(destination);
                return;
            }

            string backup = Path.Combine(GetBackupRoot(journal), artifact.BackupName);
            if (!File.Exists(backup) || !ArtifactHash.FixedTimeEquals(HashFile(backup), artifact.OriginalSha256))
            {
                throw new InvalidDataException("兼容维护备份缺失或摘要不匹配：" + artifact.RelativePath);
            }
            if (Directory.Exists(destination))
            {
                throw new IOException("兼容维护回滚目标被目录占用：" + destination);
            }

            if (File.Exists(destination) && IsReparsePoint(destination))
            {
                NativeFileSystem.DeleteFile(destination);
            }

            if (File.Exists(destination))
            {
                File.Replace(backup, destination, null, true);
            }
            else
            {
                File.Move(backup, destination);
            }
        }

        private static bool MatchesOriginal(
            CompatibilityJournalRecord journal,
            CompatibilityJournalArtifact artifact)
        {
            string path = ResolveProtectedArtifactPath(
                journal.InstallRoot,
                artifact.RelativePath);
            if (Directory.Exists(path)) return false;
            bool exists = File.Exists(path);
            if (exists && IsReparsePoint(path)) return false;
            if (exists != artifact.OriginalExists) return false;
            return !exists || ArtifactHash.FixedTimeEquals(HashFile(path), artifact.OriginalSha256);
        }

        private static void VerifyTargets(CompatibilityJournalRecord journal)
        {
            foreach (CompatibilityJournalArtifact artifact in journal.Artifacts)
            {
                VerifyTarget(journal, artifact);
            }
        }

        private static void VerifyTarget(
            CompatibilityJournalRecord journal,
            CompatibilityJournalArtifact artifact)
        {
            string path = ResolveProtectedArtifactPath(
                journal.InstallRoot,
                artifact.RelativePath);
            if (Directory.Exists(path))
            {
                throw new IOException("已提交的兼容维护制品路径被目录占用：" + path);
            }
            bool exists = File.Exists(path);
            if (exists && IsReparsePoint(path))
            {
                throw new InvalidDataException(
                    "已提交的兼容维护制品变为重解析点，已拒绝自动清理：" + artifact.RelativePath);
            }
            if (exists != artifact.TargetExists ||
                (exists && !ArtifactHash.FixedTimeEquals(HashFile(path), artifact.TargetSha256)))
            {
                throw new InvalidDataException(
                    "兼容维护制品在摘要捕获后发生变化，已拒绝提交或自动清理：" + artifact.RelativePath);
            }
        }

        private static string CopyAndHash(string sourcePath, string destinationPath)
        {
            string digest;
            using (FileStream input = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan))
            using (FileStream output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                output.Flush(true);
                digest = ToHex(sha.Hash);
            }
            if (!ArtifactHash.FixedTimeEquals(HashFile(destinationPath), digest))
            {
                throw new IOException("兼容维护备份写入后摘要复验失败：" + destinationPath);
            }
            return digest;
        }

        private static string HashFile(string path)
        {
            return ArtifactHash.ComputeSha256(path);
        }

        private static bool IsReparsePoint(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        private static string ToHex(byte[] hash)
        {
            StringBuilder builder = new StringBuilder(hash.Length * 2);
            foreach (byte value in hash)
            {
                builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private static void Cleanup(CompatibilityJournalRecord journal)
        {
            string backupRoot = GetBackupRoot(journal);
            if (Directory.Exists(backupRoot))
            {
                if (!InstallOwnership.IsManagedDirectoryIdentity(
                    journal.BackupDirectoryIdentity))
                {
                    throw new InvalidDataException(
                        "兼容维护备份目录缺少持久身份，已拒绝递归清理：" + backupRoot);
                }
                NativeFileSystem.DeleteDirectoryRecursively(
                    backupRoot,
                    journal.BackupDirectoryIdentity);
            }
            string journalPath = GetJournalPath(journal.InstallRoot);
            if (File.Exists(journalPath)) NativeFileSystem.DeleteFile(journalPath);
        }

        private static void TryDeleteJournalAndBackups(CompatibilityJournalRecord journal)
        {
            bool backupCleanupComplete = false;
            try
            {
                string backupRoot = GetBackupRoot(journal);
                if (!Directory.Exists(backupRoot))
                {
                    backupCleanupComplete = true;
                }
                else if (InstallOwnership.IsManagedDirectoryIdentity(
                    journal.BackupDirectoryIdentity))
                {
                    NativeFileSystem.DeleteDirectoryRecursively(
                        backupRoot,
                        journal.BackupDirectoryIdentity);
                    backupCleanupComplete = true;
                }
            }
            catch { }
            if (!backupCleanupComplete) return;
            try
            {
                string journalPath = GetJournalPath(journal.InstallRoot);
                if (File.Exists(journalPath)) NativeFileSystem.DeleteFile(journalPath);
            }
            catch { }
        }

        private static string GetBackupRoot(CompatibilityJournalRecord journal)
        {
            return NormalizeRoot(journal.InstallRoot) + ".compatibility-backup-" + journal.OperationId;
        }

        private static void EnsureRecoveryTargetIdentity(
            CompatibilityJournalRecord journal)
        {
            InstallOwnership.EnsureManagedDirectoryIdentity(
                journal.InstallRoot,
                journal.InstallRootIdentity);
            string markerPath = InstallOwnership.GetMarkerPath(journal.InstallRoot);
            if (Directory.Exists(markerPath))
            {
                throw new InvalidDataException(
                    "兼容维护恢复目标的安装 marker 路径被目录占用：" + markerPath);
            }
            if (HasMatchingRecoveryMarker(journal.InstallRoot, journal.InstallId))
            {
                return;
            }

            if (journal.Phase != CompatibilityTransactionPhase.FilesChanged)
            {
                throw new InvalidDataException(
                    "兼容维护恢复目标的安装 marker 缺失或损坏，且当前事务阶段不允许降级恢复：" +
                    journal.Phase + "。");
            }

            EnsureKnownRecoveryArtifactStates(journal);
        }

        private static bool HasMatchingRecoveryMarker(
            string installRoot,
            string expectedInstallId)
        {
            InstallationRecord current;
            try
            {
                current = InstallOwnership.ReadInstallationRecord(installRoot);
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (InvalidDataException)
            {
                return false;
            }
            string actualInstallId = current == null || current.Identity == null
                ? null
                : current.Identity.InstallId;
            if (!string.Equals(
                actualInstallId,
                expectedInstallId,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "兼容维护目标已被其他安装替换，已拒绝恢复：" + installRoot);
            }
            return true;
        }

        private static void EnsureKnownRecoveryArtifactStates(
            CompatibilityJournalRecord journal)
        {
            string markerName = NormalizeRelativePath(
                Path.GetFileName(InstallOwnership.GetMarkerPath(journal.InstallRoot)));
            if (!journal.Artifacts.Any(artifact => string.Equals(
                artifact.RelativePath,
                markerName,
                StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException(
                    "兼容维护事务没有保护缺失或损坏的安装 marker，已拒绝降级恢复。");
            }

            foreach (CompatibilityJournalArtifact artifact in journal.Artifacts)
            {
                if (string.Equals(
                    artifact.RelativePath,
                    markerName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (MatchesOriginal(journal, artifact) ||
                    MatchesCapturedTarget(journal, artifact))
                {
                    continue;
                }
                throw new InvalidDataException(
                    "兼容维护恢复目标包含原始态和已捕获目标态之外的制品，已保留现场并拒绝恢复：" +
                    artifact.RelativePath);
            }
        }

        private static bool MatchesCapturedTarget(
            CompatibilityJournalRecord journal,
            CompatibilityJournalArtifact artifact)
        {
            string path = ResolveProtectedArtifactPath(
                journal.InstallRoot,
                artifact.RelativePath);
            if (Directory.Exists(path)) return false;
            bool exists = File.Exists(path);
            if (exists && IsReparsePoint(path)) return false;
            if (exists != artifact.TargetExists) return false;
            if (!exists) return string.IsNullOrEmpty(artifact.TargetSha256);
            return IsSha256(artifact.TargetSha256) &&
                ArtifactHash.FixedTimeEquals(HashFile(path), artifact.TargetSha256);
        }

        private static void EnsureCurrentInstallId(
            string installRoot,
            string expectedInstallId)
        {
            InstallationRecord current;
            try
            {
                current = InstallOwnership.ReadInstallationRecord(installRoot);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(
                    "无法确认兼容维护目标仍属于原安装：" + installRoot,
                    exception);
            }
            string actualInstallId = current == null || current.Identity == null
                ? null
                : current.Identity.InstallId;
            if (!string.Equals(
                actualInstallId,
                expectedInstallId,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "兼容维护目标已被其他安装替换，已拒绝恢复：" + installRoot);
            }
        }

        private static string ResolveProtectedArtifactPath(
            string installRoot,
            string relativePath)
        {
            string fullPath = ArtifactProvenance.ResolveRelativePath(
                installRoot,
                relativePath);
            string parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(parent))
            {
                throw new InvalidDataException(
                    "兼容维护制品路径缺少父目录：" + relativePath);
            }
            InstallOwnership.EnsureManagedDirectoryPath(parent, false);
            return fullPath;
        }

        private static void Validate(
            CompatibilityJournalRecord journal,
            string expectedInstallRoot,
            string path)
        {
            Guid operationId;
            Guid installId;
            if (journal == null ||
                journal.SchemaVersion != CurrentSchemaVersion ||
                !Guid.TryParseExact(journal.OperationId, "N", out operationId) ||
                !Guid.TryParseExact(journal.InstallId, "N", out installId) ||
                !RootsEqual(journal.InstallRoot, expectedInstallRoot) ||
                !InstallOwnership.IsManagedDirectoryIdentity(
                    journal.InstallRootIdentity) ||
                !string.IsNullOrEmpty(journal.BackupDirectoryIdentity) &&
                    !InstallOwnership.IsManagedDirectoryIdentity(
                        journal.BackupDirectoryIdentity) ||
                journal.Phase != CompatibilityTransactionPhase.Preparing &&
                    !InstallOwnership.IsManagedDirectoryIdentity(
                        journal.BackupDirectoryIdentity) ||
                journal.TargetOptions == null ||
                journal.Artifacts == null ||
                journal.Artifacts.Count == 0 ||
                !Enum.IsDefined(typeof(CompatibilityTransactionPhase), journal.Phase))
            {
                throw new InvalidDataException("兼容维护事务状态格式无效：" + path);
            }

            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CompatibilityJournalArtifact artifact in journal.Artifacts)
            {
                string normalized;
                try { normalized = NormalizeRelativePath(artifact == null ? null : artifact.RelativePath); }
                catch (Exception exception)
                {
                    throw new InvalidDataException("兼容维护事务制品路径无效：" + path, exception);
                }
                if (!paths.Add(normalized) ||
                    artifact.OriginalExists &&
                    (!IsSha256(artifact.OriginalSha256) || !IsBackupName(artifact.BackupName)) ||
                    !artifact.OriginalExists &&
                    (!string.IsNullOrEmpty(artifact.OriginalSha256) || !string.IsNullOrEmpty(artifact.BackupName)) ||
                    (journal.Phase == CompatibilityTransactionPhase.FilesChanged ||
                     journal.Phase == CompatibilityTransactionPhase.Committed) &&
                    (artifact.TargetExists
                        ? !IsSha256(artifact.TargetSha256)
                        : !string.IsNullOrEmpty(artifact.TargetSha256)))
                {
                    throw new InvalidDataException("兼容维护事务制品记录无效：" + path);
                }
                artifact.RelativePath = normalized;
            }
        }

        private static bool ValidateSerializedShape(object value, string path)
        {
            IDictionary<string, object> fields = value as IDictionary<string, object>;
            object targetOptionsValue;
            object artifactsValue;
            object schemaVersionValue;
            bool legacy = fields != null &&
                !fields.TryGetValue("SchemaVersion", out schemaVersionValue);
            if (fields == null ||
                !legacy &&
                    (!HasStrictInt32(fields, "SchemaVersion") ||
                     Convert.ToInt32(
                         fields["SchemaVersion"],
                         CultureInfo.InvariantCulture) != CurrentSchemaVersion) ||
                !HasStrictString(fields, "OperationId", false) ||
                !HasStrictString(fields, "InstallRoot", false) ||
                !HasStrictString(fields, "InstallRootIdentity", false) ||
                !HasStrictString(fields, "InstallId", false) ||
                !HasStrictBoolean(fields, "InstallMarkerRequired") ||
                !HasStrictInt32(fields, "Phase") ||
                !fields.TryGetValue("TargetOptions", out targetOptionsValue) ||
                !ValidateOptionsShape(targetOptionsValue, legacy) ||
                !fields.TryGetValue("Artifacts", out artifactsValue) ||
                !ValidateArtifactsShape(artifactsValue) ||
                !HasStrictString(fields, "BackupDirectoryIdentity", true) ||
                !HasStrictString(fields, "UpdatedUtc", false))
            {
                throw new InvalidDataException(
                    "兼容维护事务状态缺少必需字段或字段类型无效：" + path);
            }
            return legacy;
        }

        private static bool ValidateOptionsShape(object value, bool legacy)
        {
            IDictionary<string, object> fields = value as IDictionary<string, object>;
            if (fields == null || fields.Count != (legacy ? 7 : 9)) return false;
            return
                HasStrictBoolean(fields, "SandboxCompatibilityEnabled") &&
                HasStrictBoolean(fields, "UnlockModelCatalogEnabled") &&
                HasStrictBoolean(fields, "SupplementChineseUiEnabled") &&
                HasStrictBoolean(fields, "EnglishTechnicalParametersEnabled") &&
                (legacy || HasStrictBoolean(fields, "ReasoningDisplayEnabled")) &&
                HasStrictBoolean(fields, "ManageSandboxCompatibility") &&
                HasStrictBoolean(fields, "ManageModelCatalog") &&
                HasStrictBoolean(fields, "ManageLocalization") &&
                (legacy || HasStrictBoolean(fields, "ManageReasoningDisplay"));
        }

        private static bool ValidateArtifactsShape(object value)
        {
            object[] artifacts = value as object[];
            if (artifacts == null || artifacts.Length == 0)
            {
                return false;
            }
            foreach (object artifactValue in artifacts)
            {
                IDictionary<string, object> artifact =
                    artifactValue as IDictionary<string, object>;
                if (artifact == null ||
                    !HasStrictString(artifact, "RelativePath", false) ||
                    !HasStrictBoolean(artifact, "OriginalExists") ||
                    !HasStrictString(artifact, "OriginalSha256", true) ||
                    !HasStrictString(artifact, "BackupName", true) ||
                    !HasStrictBoolean(artifact, "TargetExists") ||
                    !HasStrictString(artifact, "TargetSha256", true) ||
                    !HasStrictBoolean(artifact, "Modified"))
                {
                    return false;
                }
            }
            return true;
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
            object value;
            return fields.TryGetValue(name, out value) && value is int;
        }

        private static bool HasStrictString(
            IDictionary<string, object> fields,
            string name,
            bool allowNull)
        {
            object value;
            return fields.TryGetValue(name, out value) &&
                (value is string || allowNull && value == null);
        }

        private static bool IsBackupName(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) &&
                value.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char character in value)
            {
                if (!((character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }

        private void RequirePhase(CompatibilityTransactionPhase expected)
        {
            if (record.Phase != expected)
            {
                throw new InvalidOperationException(
                    "兼容维护事务阶段无效，当前=" + record.Phase + "，预期=" + expected + "。");
            }
        }

        private static string NormalizeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            {
                throw new InvalidDataException("兼容维护制品相对路径无效：" + path);
            }
            string normalized = path
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')
                .Trim('/');
            if (string.IsNullOrWhiteSpace(normalized) ||
                normalized.Split('/').Any(component => component == "." || component == ".." || component.Length == 0))
            {
                throw new InvalidDataException("兼容维护制品相对路径无效：" + path);
            }
            return normalized;
        }

        private static string NormalizeRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("兼容维护事务的安装根不能为空。", nameof(path));
            }
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool RootsEqual(string first, string second)
        {
            try
            {
                return string.Equals(NormalizeRoot(first), NormalizeRoot(second), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void SafeLog(Action<string> log, string message)
        {
            if (log == null) return;
            try { log(message); }
            catch { }
        }
    }
}
