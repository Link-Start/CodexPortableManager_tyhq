using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexPortableManager
{
    internal static partial class ShellIntegration
    {
        internal static string TryDiscoverPortableInstallRoot(IEnumerable<string> registryPaths)
        {
            if (registryPaths == null)
            {
                return null;
            }

            List<PortableDiscoveryCandidate> candidates = new List<PortableDiscoveryCandidate>();
            foreach (string registryPath in registryPaths)
            {
                if (string.IsNullOrWhiteSpace(registryPath))
                {
                    continue;
                }

                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(registryPath))
                    {
                        string candidate = key == null
                            ? null
                            : key.GetValue(ShellRegistrationWriter.PortableInstallRootValue) as string;
                        object installIdValue = key == null
                            ? null
                            : key.GetValue(ShellRegistrationWriter.PortableInstallIdValue);
                        string normalized;
                        if (ShellOwnershipChecker.TryNormalizeAbsolutePath(candidate, out normalized))
                        {
                            string installId = installIdValue as string;
                            Guid parsedInstallId;
                            candidates.Add(new PortableDiscoveryCandidate
                            {
                                InstallRoot = normalized,
                                InstallId = installId,
                                InvalidInstallId = installIdValue != null &&
                                    !Guid.TryParseExact(installId, "N", out parsedInstallId)
                            });
                        }
                    }
                }
                catch
                {
                    // 单个残留或暂时不可读的 Shell 项不应阻止管理器启动。
                }
            }

            List<string> validCandidates = new List<string>();
            List<string> conflictedCandidates = new List<string>();
            foreach (PortableDiscoveryCandidate candidate in candidates)
            {
                if (candidate.InvalidInstallId)
                {
                    if (!conflictedCandidates.Any(value =>
                        DirectoryPathsEqual(value, candidate.InstallRoot)))
                    {
                        conflictedCandidates.Add(candidate.InstallRoot);
                    }
                    continue;
                }
                PackageProfile profile;
                string validationError;
                if (InstallOwnership.TryValidateOwnedRunnableCodexPayload(
                    candidate.InstallRoot,
                    out profile,
                    out validationError))
                {
                    if (!string.IsNullOrWhiteSpace(candidate.InstallId))
                    {
                        InstallationRecord ownership = InstallOwnership.ReadInstallationRecord(
                            candidate.InstallRoot);
                        if (!string.Equals(
                            ownership.Identity.InstallId,
                            candidate.InstallId,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            if (!conflictedCandidates.Any(value =>
                                DirectoryPathsEqual(value, candidate.InstallRoot)))
                            {
                                conflictedCandidates.Add(candidate.InstallRoot);
                            }
                            continue;
                        }
                    }
                    if (!validCandidates.Any(value => DirectoryPathsEqual(value, candidate.InstallRoot)))
                    {
                        validCandidates.Add(candidate.InstallRoot);
                    }
                }
            }

            validCandidates.RemoveAll(candidate => conflictedCandidates.Any(conflict =>
                DirectoryPathsEqual(candidate, conflict)));

            return validCandidates.Count == 1 ? validCandidates[0] : null;
        }

        private static IEnumerable<string> GetPortableDiscoveryRegistryPaths()
        {
            yield return ShellRegistrationWriter.CapabilitiesPath;
            yield return @"Software\Classes\codex";
            yield return @"Software\Classes\AppUserModelId\" + AppUserModelId;

            foreach (string progId in KnownProgIds)
            {
                yield return @"Software\Classes\" + progId;
            }
            foreach (string executableName in KnownExecutableNames)
            {
                yield return @"Software\Classes\Applications\" + executableName;
                yield return @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + executableName;
            }
        }

        private static List<ShellIntegrationShortcutReceipt> BuildShortcutReceipts(
            IntegrationState state,
            ShellIntegrationCleanupJournalRecord journal,
            IList<string> warnings)
        {
            List<ShellIntegrationShortcutReceipt> receipts =
                new List<ShellIntegrationShortcutReceipt>();
            HashSet<string> candidates = GetPortableShortcutCandidates(state, warnings);
            foreach (string shortcutPath in candidates.OrderBy(
                value => value,
                StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(shortcutPath))
                {
                    continue;
                }

                string target;
                string error;
                if (!ShortcutHelper.TryGetTarget(shortcutPath, out target, out error))
                {
                    warnings.Add("现有快捷方式无法确认归属，已保留（" + shortcutPath + "）：" + error);
                    continue;
                }
                if (!ShellOwnershipChecker.PathBelongsToJournal(target, journal))
                {
                    continue;
                }

                receipts.Add(new ShellIntegrationShortcutReceipt
                {
                    Path = NormalizeRequiredAbsolutePath(shortcutPath, "shortcutPath"),
                    TargetPath = NormalizeRequiredAbsolutePath(target, "shortcutTarget"),
                    FileSha256 = ComputeFileSha256(shortcutPath)
                });
            }
            return receipts;
        }

        private static void DeleteShortcutIfReceiptMatches(
            ShellIntegrationShortcutReceipt receipt,
            ShellIntegrationCleanupJournalRecord journal,
            IList<string> warnings)
        {
            if (!File.Exists(receipt.Path))
            {
                return;
            }
            string currentHash = ComputeFileSha256(receipt.Path);
            if (!string.Equals(currentHash, receipt.FileSha256, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("快捷方式已被替换，已保留新文件：" + receipt.Path);
                return;
            }

            string target;
            string error;
            if (!ShortcutHelper.TryGetTarget(receipt.Path, out target, out error))
            {
                throw new InvalidDataException(
                    "快捷方式损坏或无法解析，已保留以避免误删（" + receipt.Path + "）：" + error);
            }
            if (!DirectoryOrFilePathsEqual(target, receipt.TargetPath))
            {
                warnings.Add("快捷方式目标已经变化，已保留新入口：" + receipt.Path);
                return;
            }
            if (InstallRootWasReusedByAnotherInstallation(journal))
            {
                warnings.Add("安装目录已由另一安装 ID 接管，已保留其快捷方式：" + receipt.Path);
                return;
            }
            Action<string> observer = ShortcutFinalDeleteObserverForTest;
            if (observer != null)
            {
                observer(receipt.Path);
            }
            try
            {
                NativeFileSystem.DeleteFileIfSha256Matches(
                    receipt.Path,
                    receipt.FileSha256);
            }
            catch (InvalidDataException exception)
            {
                warnings.Add(
                    "快捷方式在最终删除前已被替换，已保留新文件（" +
                    receipt.Path + "）：" + exception.Message);
            }
        }

        private static void UpgradeShellRegistryOwnershipMarkers(
            ShellIntegrationCleanupJournalRecord journal)
        {
            foreach (string protocol in journal.Protocols)
            {
                string path = @"Software\Classes\" + protocol;
                UpgradeShellRegistryOwnershipMarker(
                    path,
                    delegate { return ShellOwnershipChecker.GetRegistryCommandTreeOwnership(path, journal); },
                    journal);
            }
            foreach (string progId in journal.ProgIds)
            {
                string path = @"Software\Classes\" + progId;
                UpgradeShellRegistryOwnershipMarker(
                    path,
                    delegate { return ShellOwnershipChecker.GetRegistryCommandTreeOwnership(path, journal); },
                    journal);
            }
            foreach (string executableName in journal.ExecutableNames)
            {
                string applicationPath = @"Software\Classes\Applications\" + executableName;
                UpgradeShellRegistryOwnershipMarker(
                    applicationPath,
                    delegate { return ShellOwnershipChecker.GetRegistryCommandTreeOwnership(applicationPath, journal); },
                    journal);
                string appPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + executableName;
                UpgradeShellRegistryOwnershipMarker(
                    appPath,
                    delegate { return ShellOwnershipChecker.GetRegistryPathEntryOwnership(appPath, journal); },
                    journal);
            }
            UpgradeShellRegistryOwnershipMarker(
                ShellRegistrationWriter.CapabilitiesPath,
                delegate
                {
                    return ShellOwnershipChecker.GetRegistryResourceTreeOwnership(
                        ShellRegistrationWriter.CapabilitiesPath,
                        "ApplicationIcon",
                        journal);
                },
                journal);
            foreach (string appId in journal.AppUserModelIds)
            {
                string path = @"Software\Classes\AppUserModelId\" + appId;
                UpgradeShellRegistryOwnershipMarker(
                    path,
                    delegate { return ShellOwnershipChecker.GetRegistryResourceTreeOwnership(path, "IconUri", journal); },
                    journal);
            }
        }

        private static void UpgradeShellRegistryOwnershipMarker(
            string path,
            Func<ShellRegistryOwnership> ownershipReader,
            ShellIntegrationCleanupJournalRecord journal)
        {
            ShellRegistryOwnership ownership = ownershipReader();
            ShellOwnershipChecker.ThrowIfOwnershipUnknown(ownership, path);
            if (ownership != ShellRegistryOwnership.Owned)
            {
                return;
            }
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path, true))
            {
                if (key == null)
                {
                    throw new IOException("准备清理时注册表项消失：" + path);
                }
                ShellRegistrationWriter.SetOwnershipMarkers(
                    key,
                    journal.RegistrationRoot,
                    journal.PhysicalRoot,
                    journal.InstallId);
            }
        }

        private static string TryResolveCleanupInstallId(
            string registrationRoot,
            string sourceRoot,
            IntegrationState state,
            string expectedInstallId = null)
        {
            Guid parsedInstallId;
            if (!string.IsNullOrWhiteSpace(expectedInstallId))
            {
                if (!Guid.TryParseExact(expectedInstallId, "N", out parsedInstallId))
                {
                    throw new InvalidDataException("预期安装 ID 格式无效。");
                }
                return expectedInstallId;
            }

            string stateInstallId = TryGetValidInstallId(state);
            if (!string.IsNullOrWhiteSpace(stateInstallId) &&
                StateMatchesRoot(state, registrationRoot))
            {
                return stateInstallId;
            }

            foreach (string candidate in new[] { sourceRoot, registrationRoot })
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
                {
                    continue;
                }
                try
                {
                    return InstallOwnership.ReadInstallationRecord(candidate).Identity.InstallId;
                }
                catch
                {
                    // 继续尝试其他可用身份来源。
                }
            }
            return null;
        }

        private static string TryGetValidInstallId(IntegrationState state)
        {
            Guid parsedInstallId;
            return state != null &&
                Guid.TryParseExact(state.InstallId, "N", out parsedInstallId)
                ? state.InstallId
                : null;
        }

        private static IntegrationStateSnapshot ReadIntegrationStateSnapshot()
        {
            // 只从一个稳定文件句柄读取当前状态的字节、摘要和 JSON。
            string path = PortableStorage.IntegrationStateFilePath;
            byte[] bytes;
            try
            {
                using (FileStream stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete))
                using (MemoryStream buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    bytes = buffer.ToArray();
                }
            }
            catch (FileNotFoundException)
            {
                return new IntegrationStateSnapshot(
                    IntegrationStateSnapshotKind.Missing,
                    null,
                    null);
            }
            catch (DirectoryNotFoundException)
            {
                return new IntegrationStateSnapshot(
                    IntegrationStateSnapshotKind.Missing,
                    null,
                    null);
            }

            string sha256;
            using (SHA256 hash = SHA256.Create())
            {
                sha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", string.Empty);
            }

            string json;
            try
            {
                using (MemoryStream input = new MemoryStream(bytes, false))
                using (StreamReader reader = new StreamReader(
                    input,
                    new UTF8Encoding(false, true),
                    true))
                {
                    json = reader.ReadToEnd();
                }
            }
            catch
            {
                return new IntegrationStateSnapshot(
                    IntegrationStateSnapshotKind.Malformed,
                    null,
                    sha256);
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            IntegrationState state;
            try
            {
                state = serializer.Deserialize<IntegrationState>(json);
            }
            catch
            {
                return new IntegrationStateSnapshot(
                    IntegrationStateSnapshotKind.Malformed,
                    null,
                    sha256);
            }
            bool valid = IsSupportedIntegrationState(state);
            return new IntegrationStateSnapshot(
                valid
                    ? IntegrationStateSnapshotKind.Valid
                    : IntegrationStateSnapshotKind.Malformed,
                valid ? state : null,
                sha256);
        }

        private static bool StateBelongsToInstallation(
            IntegrationState state,
            string installId,
            string registrationRoot,
            string physicalRoot,
            string rootIdentity)
        {
            if (state == null || !IsSupportedIntegrationState(state))
            {
                return false;
            }

            if (!string.Equals(state.InstallId, installId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return string.IsNullOrWhiteSpace(rootIdentity) ||
                InstallOwnership.ManagedDirectoryIdentitiesEqual(
                    state.RootIdentity,
                    rootIdentity);
        }

        private static bool StateMatchesRoot(IntegrationState state, string registrationRoot)
        {
            if (state == null || !IsSupportedIntegrationState(state))
            {
                return false;
            }
            string stateRoot = TryResolveStateInstallRoot(state);
            if (!string.IsNullOrWhiteSpace(stateRoot) &&
                DirectoryPathsEqual(stateRoot, registrationRoot))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(state.PhysicalInstallRoot) &&
                DirectoryPathsEqual(state.PhysicalInstallRoot, registrationRoot))
            {
                return true;
            }
            return !string.IsNullOrWhiteSpace(state.ExecutablePath) &&
                ShellOwnershipChecker.IsPathUnderRoot(state.ExecutablePath, registrationRoot);
        }

        private static bool IsSupportedIntegrationState(IntegrationState state)
        {
            if (state == null)
            {
                return false;
            }

            string installRoot;
            string physicalRoot;
            string executablePath;
            if (string.IsNullOrWhiteSpace(TryGetValidInstallId(state)) ||
                !TryNormalizeInstallRoot(state.InstallRoot, out installRoot) ||
                !TryNormalizeInstallRoot(state.PhysicalInstallRoot, out physicalRoot) ||
                !ShellOwnershipChecker.TryNormalizeAbsolutePath(state.ExecutablePath, out executablePath) ||
                !InstallOwnership.IsManagedDirectoryIdentity(state.RootIdentity) ||
                !IsSafeRegistryComponent(state.AppUserModelId) ||
                !IsValidStateList(state.Protocols, IsSafeProtocol) ||
                !IsValidStateList(state.ProgIds, IsSafeRegistryComponent) ||
                !IsValidStateList(state.Extensions, IsSafeExtension) ||
                !IsValidShortcutStateList(state.ShortcutPaths))
            {
                return false;
            }
            return ShellOwnershipChecker.IsPathUnderRoot(executablePath, installRoot) ||
                ShellOwnershipChecker.IsPathUnderRoot(executablePath, physicalRoot);
        }

        private static bool TryNormalizeInstallRoot(string path, out string normalized)
        {
            normalized = null;
            try
            {
                normalized = NormalizeExpectedInstallRoot(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsValidOptionalStateList(
            IEnumerable<string> values,
            Func<string, bool> validator)
        {
            return values == null || IsValidStateList(values, validator);
        }

        private static bool IsValidStateList(
            IEnumerable<string> values,
            Func<string, bool> validator)
        {
            if (values == null || validator == null)
            {
                return false;
            }
            HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in values)
            {
                if (!validator(value) || !unique.Add(value))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsValidShortcutStateList(IEnumerable<string> values)
        {
            if (values == null)
            {
                return false;
            }
            HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in values)
            {
                string normalized;
                if (!ShellOwnershipChecker.TryNormalizeAbsolutePath(value, out normalized) ||
                    !string.Equals(
                        Path.GetExtension(normalized),
                        ".lnk",
                        StringComparison.OrdinalIgnoreCase) ||
                    !unique.Add(normalized))
                {
                    return false;
                }
            }
            return true;
        }

        private static string TryGetStatePhysicalRoot(IntegrationState state, string installId)
        {
            return state != null &&
                string.Equals(
                    TryGetValidInstallId(state),
                    installId,
                    StringComparison.OrdinalIgnoreCase) &&
                ShellOwnershipChecker.TryNormalizeAbsolutePath(state.PhysicalInstallRoot, out string normalized)
                ? normalized
                : null;
        }

        private static string TryGetStateRootIdentity(IntegrationState state, string installId)
        {
            return state != null &&
                string.Equals(
                    TryGetValidInstallId(state),
                    installId,
                    StringComparison.OrdinalIgnoreCase) &&
                InstallOwnership.IsManagedDirectoryIdentity(state.RootIdentity)
                ? state.RootIdentity
                : null;
        }

        private static void EnsureJournalMatches(
            ShellIntegrationCleanupJournalRecord journal,
            string registrationRoot,
            string installId)
        {
            if (journal == null || !JournalMatchesRoot(journal, registrationRoot))
            {
                throw new InvalidDataException("系统集成 cleanup journal 不属于预期安装目录。");
            }
            if (!string.IsNullOrWhiteSpace(installId) &&
                !string.Equals(journal.InstallId, installId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("系统集成 cleanup journal 不属于预期安装 ID。");
            }
        }

        private static void ValidateCleanupPurposeRequest(
            ShellIntegrationCleanupPurpose purpose,
            string deploymentOperationId,
            ShellIntegrationCleanupPhase phase)
        {
            Guid parsedOperationId;
            bool valid = purpose == ShellIntegrationCleanupPurpose.ImmediateCleanup
                ? phase == ShellIntegrationCleanupPhase.Armed &&
                    string.IsNullOrEmpty(deploymentOperationId)
                : purpose == ShellIntegrationCleanupPurpose.DeploymentUninstall &&
                    phase == ShellIntegrationCleanupPhase.Prepared &&
                    Guid.TryParseExact(deploymentOperationId, "N", out parsedOperationId);
            if (!valid)
            {
                throw new InvalidDataException("系统集成清理用途、阶段或部署操作 ID 不一致。");
            }
        }

        private static void EnsureRequestedCleanupPurpose(
            ShellIntegrationCleanupJournalRecord journal,
            ShellIntegrationCleanupPurpose purpose,
            string deploymentOperationId)
        {
            if (journal.Purpose != purpose ||
                purpose == ShellIntegrationCleanupPurpose.DeploymentUninstall &&
                !string.Equals(
                    journal.DeploymentOperationId,
                    deploymentOperationId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("已有系统集成 cleanup journal 属于另一种清理用途或部署事务。");
            }
        }

        private static void EnsureDeploymentCleanupPurpose(
            ShellIntegrationCleanupJournalRecord journal,
            string deploymentOperationId,
            bool requireCommitted)
        {
            Guid parsedOperationId;
            if (!Guid.TryParseExact(deploymentOperationId, "N", out parsedOperationId))
            {
                throw new InvalidDataException("部署操作 ID 格式无效。");
            }
            bool purposeMatches =
                journal.Purpose == ShellIntegrationCleanupPurpose.DeploymentUninstall &&
                string.Equals(
                    journal.DeploymentOperationId,
                    deploymentOperationId,
                    StringComparison.OrdinalIgnoreCase);
            if (!purposeMatches)
            {
                throw new InvalidDataException("系统集成 cleanup journal 不属于当前卸载事务。");
            }
            EnsureMatchingUninstallDeployment(
                GetDeploymentRootCandidates(journal),
                journal.InstallId,
                deploymentOperationId,
                requireCommitted);
        }

        private static DeploymentJournalRecord EnsureMatchingUninstallDeployment(
            IEnumerable<string> candidateRoots,
            string installId,
            string deploymentOperationId,
            bool requireCommitted)
        {
            Guid parsedOperationId;
            Guid parsedInstallId;
            if (!Guid.TryParseExact(deploymentOperationId, "N", out parsedOperationId) ||
                !Guid.TryParseExact(installId, "N", out parsedInstallId))
            {
                throw new InvalidDataException("卸载部署操作 ID 或安装 ID 格式无效。");
            }
            DeploymentJournalRecord deployment = null;
            foreach (string candidateRoot in candidateRoots ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(candidateRoot))
                {
                    continue;
                }
                deployment = DeploymentJournal.Read(candidateRoot);
                if (deployment != null)
                {
                    break;
                }
            }
            if (deployment == null ||
                deployment.Operation != DeploymentOperationKind.Uninstall ||
                !string.Equals(
                    deployment.InstallId,
                    installId,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    deployment.OperationId,
                    deploymentOperationId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "系统集成清理无法与当前卸载 deployment journal 交叉验证。");
            }
            bool committed = deployment.Phase >=
                DeploymentTransactionPhase.UninstallPayloadDetached;
            if (requireCommitted != committed)
            {
                throw new InvalidDataException(
                    requireCommitted
                        ? "卸载 deployment journal 尚未持久化提交点，不能授权系统集成删除。"
                        : "卸载 deployment journal 已越过提交点，不能取消或重新准备系统集成清理。");
            }
            return deployment;
        }

        private static IEnumerable<string> GetDeploymentRootCandidates(
            ShellIntegrationCleanupJournalRecord journal)
        {
            List<string> roots = new List<string>();
            AddPathAlias(roots, journal.RegistrationRoot);
            AddPathAlias(roots, journal.PhysicalRoot);
            foreach (string alias in journal.RootAliases ?? new List<string>())
            {
                AddPathAlias(roots, alias);
            }
            return roots;
        }

        private static bool JournalMatchesRoot(
            ShellIntegrationCleanupJournalRecord journal,
            string path)
        {
            if (journal == null || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
            IEnumerable<string> aliases = (journal.RootAliases ?? new List<string>())
                .Concat(new[] { journal.RegistrationRoot, journal.PhysicalRoot });
            return aliases.Any(alias =>
                !string.IsNullOrWhiteSpace(alias) && DirectoryPathsEqual(alias, path));
        }

        private static void AddPathAlias(ICollection<string> aliases, string path)
        {
            string normalized;
            if (ShellOwnershipChecker.TryNormalizeAbsolutePath(path, out normalized) &&
                !aliases.Any(value => string.Equals(
                    value,
                    normalized,
                    StringComparison.OrdinalIgnoreCase)))
            {
                aliases.Add(normalized);
            }
        }

        private static bool DirectoryOrFilePathsEqual(string first, string second)
        {
            string normalizedFirst;
            string normalizedSecond;
            if (!ShellOwnershipChecker.TryNormalizeAbsolutePath(first, out normalizedFirst) ||
                !ShellOwnershipChecker.TryNormalizeAbsolutePath(second, out normalizedSecond))
            {
                return false;
            }
            if (string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!(File.Exists(normalizedFirst) || Directory.Exists(normalizedFirst)) ||
                !(File.Exists(normalizedSecond) || Directory.Exists(normalizedSecond)))
            {
                return false;
            }
            try
            {
                return string.Equals(
                    NativeFileSystem.GetStablePathForExistingPath(normalizedFirst),
                    NativeFileSystem.GetStablePathForExistingPath(normalizedSecond),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool InstallRootWasReusedByAnotherInstallation(
            ShellIntegrationCleanupJournalRecord journal)
        {
            foreach (string alias in (journal.RootAliases ?? new List<string>())
                .Concat(new[] { journal.RegistrationRoot }))
            {
                if (string.IsNullOrWhiteSpace(alias) || !Directory.Exists(alias))
                {
                    continue;
                }
                try
                {
                    string currentInstallId = InstallOwnership.ReadInstallationRecord(alias).Identity.InstallId;
                    if (!string.Equals(
                        currentInstallId,
                        journal.InstallId,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    return true;
                }
            }
            return false;
        }

        private static void DeleteIntegrationStateIfOwned(
            ShellIntegrationCleanupJournalRecord journal,
            IList<string> warnings)
        {
            IntegrationStateSnapshot snapshot = ReadIntegrationStateSnapshot();
            if (snapshot.Kind == IntegrationStateSnapshotKind.Missing)
            {
                return;
            }
            if (snapshot.Kind == IntegrationStateSnapshotKind.Valid)
            {
                IntegrationState state = snapshot.State;
                string stateInstallId = TryGetValidInstallId(state);
                if (!string.IsNullOrWhiteSpace(stateInstallId))
                {
                    if (string.Equals(
                        stateInstallId,
                        journal.InstallId,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        if (StateBelongsToInstallation(
                            state,
                            journal.InstallId,
                            journal.RegistrationRoot,
                            journal.PhysicalRoot,
                            journal.RootIdentity) ||
                            !string.IsNullOrWhiteSpace(journal.IntegrationStateSha256) &&
                            string.Equals(
                                snapshot.Sha256,
                                journal.IntegrationStateSha256,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            PortableStorage.DeleteIntegrationStateIfSha256Matches(
                                snapshot.Sha256);
                        }
                        else
                        {
                            throw new InvalidDataException(
                                "同安装 ID 的 integration.json 身份或摘要已变化，已保留新状态。");
                        }
                    }
                    else
                    {
                        warnings.Add("integration.json 已由另一安装 ID 接管，已保留新状态。");
                    }
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(journal.IntegrationStateSha256) &&
                string.Equals(
                    snapshot.Sha256,
                    journal.IntegrationStateSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                PortableStorage.DeleteIntegrationStateIfSha256Matches(
                    snapshot.Sha256);
            }
            else
            {
                throw new InvalidDataException(
                    "损坏的 integration.json 已发生变化，已保留且 cleanup journal 将继续等待人工确认。");
            }
        }

        private static string ComputeFileSha256(string path)
        {
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete))
            using (SHA256 sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }

        private sealed class PortableDiscoveryCandidate
        {
            public string InstallRoot { get; set; }
            public string InstallId { get; set; }
            public bool InvalidInstallId { get; set; }
        }


    }
}
