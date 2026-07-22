using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexPortableManager
{
    internal enum ShellIntegrationCleanupPhase
    {
        Prepared = 1,
        Armed = 2,
        Completed = 3
    }

    internal enum ShellIntegrationCleanupPurpose
    {
        ImmediateCleanup = 1,
        DeploymentUninstall = 2
    }

    internal sealed class ShellIntegrationShortcutReceipt
    {
        public string Path { get; set; }
        public string TargetPath { get; set; }
        public string FileSha256 { get; set; }
    }

    internal sealed class ShellIntegrationCleanupJournalRecord
    {
        public string OperationId { get; set; }
        public ShellIntegrationCleanupPhase Phase { get; set; }
        public ShellIntegrationCleanupPurpose Purpose { get; set; }
        public string DeploymentOperationId { get; set; }
        public string InstallId { get; set; }
        public string RegistrationRoot { get; set; }
        public string PhysicalRoot { get; set; }
        public string RootIdentity { get; set; }
        public List<string> RootAliases { get; set; }
        public List<string> Protocols { get; set; }
        public List<string> ProgIds { get; set; }
        public List<string> Extensions { get; set; }
        public List<string> ExecutableNames { get; set; }
        public List<string> AppUserModelIds { get; set; }
        public List<ShellIntegrationShortcutReceipt> Shortcuts { get; set; }
        public string IntegrationStateSha256 { get; set; }
        public string CreatedUtc { get; set; }
        public string UpdatedUtc { get; set; }
    }

    internal static class ShellIntegrationCleanupJournal
    {
        private const string JournalFileName = "integration-cleanup.json";

        internal static string FilePath
        {
            get { return Path.Combine(PortableStorage.UserDataRoot, JournalFileName); }
        }

        public static bool Exists()
        {
            NativePathKind kind = NativeFileSystem.GetPathKind(FilePath);
            if (kind == NativePathKind.Missing)
            {
                return false;
            }
            if (kind == NativePathKind.File)
            {
                return true;
            }
            throw new IOException("Shell 集成清理 journal 路径不是普通文件：" + FilePath);
        }

        public static ShellIntegrationCleanupJournalRecord Read()
        {
            string path = FilePath;
            NativePathKind kind = NativeFileSystem.GetPathKind(path);
            if (kind == NativePathKind.Missing)
            {
                return null;
            }
            if (kind != NativePathKind.File)
            {
                throw new IOException("Shell 集成清理 journal 路径不是普通文件：" + path);
            }

            ShellIntegrationCleanupJournalRecord record;
            try
            {
                record = new JavaScriptSerializer().Deserialize<ShellIntegrationCleanupJournalRecord>(
                    File.ReadAllText(path, Encoding.UTF8));
            }
            catch (IOException)
            {
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("Shell 集成清理 journal 损坏：" + path, exception);
            }

            Validate(record, path);
            return record;
        }

        public static void Write(ShellIntegrationCleanupJournalRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            string path = FilePath;
            EnsurePathIsNotDirectory();
            string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(record.CreatedUtc))
            {
                record.CreatedUtc = now;
            }
            record.UpdatedUtc = now;
            Validate(record, path);

            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            EnsurePathIsNotDirectory();
            string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(new JavaScriptSerializer().Serialize(record));
                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, null, true);
                }
                else
                {
                    File.Move(temporaryPath, path);
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

        public static void Delete()
        {
            NativePathKind kind = NativeFileSystem.GetPathKind(FilePath);
            if (kind == NativePathKind.Missing)
            {
                return;
            }
            if (kind != NativePathKind.File)
            {
                throw new IOException("Shell 集成清理 journal 路径不是普通文件：" + FilePath);
            }
            NativeFileSystem.DeleteFile(FilePath);
        }

        private static void Validate(
            ShellIntegrationCleanupJournalRecord record,
            string path)
        {
            Guid operationId;
            Guid installId;
            Guid deploymentOperationId;
            DateTime createdUtc;
            DateTime updatedUtc;
            if (record == null ||
                !Guid.TryParseExact(record.OperationId, "N", out operationId) ||
                !Guid.TryParseExact(record.InstallId, "N", out installId) ||
                !Enum.IsDefined(typeof(ShellIntegrationCleanupPhase), record.Phase) ||
                !TryValidateAbsolutePath(record.RegistrationRoot, true, out _) ||
                (!string.IsNullOrEmpty(record.PhysicalRoot) &&
                    !TryValidateAbsolutePath(record.PhysicalRoot, true, out _)) ||
                !InstallOwnership.IsManagedDirectoryIdentity(record.RootIdentity) ||
                !IsUtcTimestamp(record.CreatedUtc, out createdUtc) ||
                !IsUtcTimestamp(record.UpdatedUtc, out updatedUtc) ||
                updatedUtc < createdUtc ||
                !string.IsNullOrEmpty(record.IntegrationStateSha256) &&
                    !IsSha256(record.IntegrationStateSha256))
            {
                throw new InvalidDataException("Shell 集成清理 journal 格式无效：" + path);
            }

            bool purposeValid;
            if (record.Purpose == ShellIntegrationCleanupPurpose.ImmediateCleanup)
            {
                purposeValid = record.Phase != ShellIntegrationCleanupPhase.Prepared &&
                    string.IsNullOrEmpty(record.DeploymentOperationId);
            }
            else if (record.Purpose == ShellIntegrationCleanupPurpose.DeploymentUninstall)
            {
                purposeValid = Guid.TryParseExact(
                    record.DeploymentOperationId,
                    "N",
                    out deploymentOperationId);
            }
            else
            {
                purposeValid = false;
            }
            if (!purposeValid)
            {
                throw new InvalidDataException("Shell 集成清理 journal 的用途与阶段不一致：" + path);
            }

            ValidatePathList(record.RootAliases, "RootAliases", true, path);
            ValidateStringList(record.Protocols, "Protocols", ShellResourceNameRules.IsSafeProtocol, path);
            ValidateStringList(record.ProgIds, "ProgIds", ShellResourceNameRules.IsSafeRegistryComponent, path);
            ValidateStringList(record.Extensions, "Extensions", ShellResourceNameRules.IsSafeExtension, path);
            ValidateStringList(record.ExecutableNames, "ExecutableNames", ShellResourceNameRules.IsSafeExecutableName, path);
            ValidateStringList(record.AppUserModelIds, "AppUserModelIds", ShellResourceNameRules.IsSafeRegistryComponent, path);
            ValidateShortcuts(record.Shortcuts, path);
        }

        private static void ValidatePathList(
            IList<string> values,
            string fieldName,
            bool rejectRoot,
            string journalPath)
        {
            if (values == null)
            {
                throw new InvalidDataException(
                    "Shell 集成清理 journal 的 " + fieldName + " 不能为空：" + journalPath);
            }

            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in values)
            {
                string normalized;
                if (!TryValidateAbsolutePath(value, rejectRoot, out normalized) ||
                    !paths.Add(normalized))
                {
                    throw new InvalidDataException(
                        "Shell 集成清理 journal 的 " + fieldName + " 含无效或重复路径：" + journalPath);
                }
            }
        }

        private static void ValidateStringList(
            IList<string> values,
            string fieldName,
            Func<string, bool> validator,
            string journalPath)
        {
            if (values == null)
            {
                throw new InvalidDataException(
                    "Shell 集成清理 journal 的 " + fieldName + " 不能为空：" + journalPath);
            }

            HashSet<string> entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in values)
            {
                if (!validator(value) || !entries.Add(value))
                {
                    throw new InvalidDataException(
                        "Shell 集成清理 journal 的 " + fieldName + " 含无效或重复条目：" + journalPath);
                }
            }
        }

        private static void ValidateShortcuts(
            IList<ShellIntegrationShortcutReceipt> shortcuts,
            string journalPath)
        {
            if (shortcuts == null)
            {
                throw new InvalidDataException(
                    "Shell 集成清理 journal 的 Shortcuts 不能为空：" + journalPath);
            }

            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ShellIntegrationShortcutReceipt shortcut in shortcuts)
            {
                string normalizedPath;
                string normalizedTarget;
                if (shortcut == null ||
                    !TryValidateAbsolutePath(shortcut.Path, true, out normalizedPath) ||
                    !string.Equals(
                        Path.GetExtension(normalizedPath),
                        ".lnk",
                        StringComparison.OrdinalIgnoreCase) ||
                    !TryValidateAbsolutePath(shortcut.TargetPath, true, out normalizedTarget) ||
                    !IsSha256(shortcut.FileSha256) ||
                    !paths.Add(normalizedPath))
                {
                    throw new InvalidDataException(
                        "Shell 集成清理 journal 含无效或重复的快捷方式 receipt：" + journalPath);
                }
            }
        }

        private static bool TryValidateAbsolutePath(
            string value,
            bool rejectRoot,
            out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(value) ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                string candidate = value;
                if (candidate.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = @"\\" + candidate.Substring(8);
                }
                else if (candidate.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = candidate.Substring(4);
                }

                if (!Path.IsPathRooted(candidate))
                {
                    return false;
                }
                string root = Path.GetPathRoot(candidate);
                if (string.IsNullOrWhiteSpace(root) || root.EndsWith(":", StringComparison.Ordinal))
                {
                    return false;
                }

                string fullPath = Path.GetFullPath(candidate);
                string fullRoot = Path.GetPathRoot(fullPath);
                normalized = string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
                    ? fullPath
                    : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !rejectRoot ||
                    !string.Equals(normalized, fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                normalized = null;
                return false;
            }
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64)
            {
                return false;
            }
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

        private static bool IsUtcTimestamp(string value, out DateTime timestamp)
        {
            timestamp = default(DateTime);
            return !string.IsNullOrWhiteSpace(value) &&
                value.EndsWith("Z", StringComparison.Ordinal) &&
                DateTime.TryParseExact(
                    value,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out timestamp) &&
                timestamp.Kind == DateTimeKind.Utc;
        }

        private static void EnsurePathIsNotDirectory()
        {
            NativePathKind kind = NativeFileSystem.GetPathKind(FilePath);
            if (kind == NativePathKind.Directory || kind == NativePathKind.ReparsePoint)
            {
                throw new IOException("Shell 集成清理 journal 路径不是普通文件位置：" + FilePath);
            }
        }
    }
}
