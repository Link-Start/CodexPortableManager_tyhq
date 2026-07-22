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
        private static ShellIntegrationCleanupResult RemoveWithResultCore(
            string registrationRoot,
            string expectedInstallId,
            string sourceRoot)
        {
            List<string> warnings = new List<string>();
            ShellIntegrationCleanupJournalRecord existingJournal = ShellIntegrationCleanupJournal.Read();
            if (existingJournal != null)
            {
                if (JournalMatchesRoot(existingJournal, registrationRoot) &&
                    (string.IsNullOrWhiteSpace(expectedInstallId) ||
                     string.Equals(
                         existingJournal.InstallId,
                         expectedInstallId,
                         StringComparison.OrdinalIgnoreCase)))
                {
                    if (existingJournal.Phase == ShellIntegrationCleanupPhase.Prepared)
                    {
                        warnings.Add(
                            "系统集成清理范围仍在等待对应卸载事务确认，当前即时清理未取得删除授权。");
                        return new ShellIntegrationCleanupResult(false, warnings.AsReadOnly());
                    }
                    return ExecuteCleanupJournalCore(existingJournal);
                }

                ShellIntegrationCleanupResult pending = RecoverPendingCleanupCore();
                warnings.AddRange(pending.Warnings);
                if (!pending.Complete)
                {
                    warnings.Add("另一套系统集成清理事务尚未完成，当前清理已暂停。");
                    return new ShellIntegrationCleanupResult(false, warnings.AsReadOnly());
                }
            }

            ShellIntegrationCleanupJournalRecord journal;
            try
            {
                journal = PrepareCleanupCore(
                    registrationRoot,
                    sourceRoot,
                    expectedInstallId,
                    ShellIntegrationCleanupPurpose.ImmediateCleanup,
                    null,
                    ShellIntegrationCleanupPhase.Armed,
                    warnings);
            }
            catch (Exception exception)
            {
                warnings.Add("准备系统集成清理事务失败：" + exception.Message);
                return new ShellIntegrationCleanupResult(false, warnings.AsReadOnly());
            }

            ShellIntegrationCleanupResult result = ExecuteCleanupJournalCore(journal);
            warnings.AddRange(result.Warnings);
            return new ShellIntegrationCleanupResult(result.Complete, warnings.AsReadOnly());
        }

        private static ShellIntegrationCleanupResult RecoverPendingCleanupCore()
        {
            ShellIntegrationCleanupJournalRecord journal = ShellIntegrationCleanupJournal.Read();
            if (journal != null)
            {
                if (journal.Phase == ShellIntegrationCleanupPhase.Prepared)
                {
                    return new ShellIntegrationCleanupResult(
                        false,
                        new[] { "系统集成清理范围已经准备，但卸载提交点尚未确认，已保留现场等待部署恢复。" });
                }
                return ExecuteCleanupJournalCore(journal);
            }

            IntegrationStateSnapshot snapshot = ReadIntegrationStateSnapshot();
            IntegrationState state = snapshot.Kind == IntegrationStateSnapshotKind.Valid
                ? snapshot.State
                : null;
            if (state == null || !state.CleanupPending)
            {
                return new ShellIntegrationCleanupResult(true, new string[0]);
            }

            string registrationRoot = TryResolveStateInstallRoot(state);
            if (string.IsNullOrWhiteSpace(registrationRoot))
            {
                return new ShellIntegrationCleanupResult(
                    false,
                    new[] { "待清理的 integration.json 无法确定原安装目录，已保留现场。" });
            }

            string installId = TryResolveCleanupInstallId(registrationRoot, registrationRoot, state);
            if (string.IsNullOrWhiteSpace(installId))
            {
                return new ShellIntegrationCleanupResult(
                    false,
                    new[] { "当前系统集成状态无法证明对应的安装 ID，已保留现场而未猜测清理。" });
            }

            return RemoveWithResultCore(registrationRoot, installId, registrationRoot);
        }

        private static ShellIntegrationCleanupJournalRecord PrepareCleanupCore(
            string registrationRoot,
            string sourceRoot,
            string expectedInstallId,
            ShellIntegrationCleanupPurpose purpose,
            string deploymentOperationId,
            ShellIntegrationCleanupPhase requestedPhase,
            IList<string> warnings)
        {
            if (warnings == null) throw new ArgumentNullException(nameof(warnings));
            if (requestedPhase != ShellIntegrationCleanupPhase.Prepared &&
                requestedPhase != ShellIntegrationCleanupPhase.Armed)
            {
                throw new ArgumentOutOfRangeException(nameof(requestedPhase));
            }
            ValidateCleanupPurposeRequest(
                purpose,
                deploymentOperationId,
                requestedPhase);

            string normalizedRegistrationRoot = NormalizeExpectedInstallRoot(registrationRoot);
            string normalizedSourceRoot = string.IsNullOrWhiteSpace(sourceRoot)
                ? normalizedRegistrationRoot
                : NormalizeExpectedInstallRoot(sourceRoot);
            ShellIntegrationCleanupJournalRecord existingJournal = ShellIntegrationCleanupJournal.Read();
            if (existingJournal != null)
            {
                EnsureJournalMatches(existingJournal, normalizedRegistrationRoot, expectedInstallId);
                EnsureRequestedCleanupPurpose(
                    existingJournal,
                    purpose,
                    deploymentOperationId);
                return existingJournal;
            }

            IntegrationStateSnapshot stateSnapshot = ReadIntegrationStateSnapshot();
            bool stateFileExisted = stateSnapshot.Kind != IntegrationStateSnapshotKind.Missing;
            IntegrationState state = stateSnapshot.Kind == IntegrationStateSnapshotKind.Valid
                ? stateSnapshot.State
                : null;
            string installId = TryResolveCleanupInstallId(
                normalizedRegistrationRoot,
                normalizedSourceRoot,
                state,
                expectedInstallId);
            Guid parsedInstallId;
            if (!Guid.TryParseExact(installId, "N", out parsedInstallId))
            {
                throw new InvalidDataException("无法确定系统集成清理事务对应的安装 ID。");
            }

            string statePhysicalRoot = TryGetStatePhysicalRoot(state, installId);
            string stateRootIdentity = TryGetStateRootIdentity(state, installId);
            string physicalRoot = statePhysicalRoot;
            string rootIdentity = stateRootIdentity;
            bool actualRootIdentityObserved = false;
            if (Directory.Exists(normalizedRegistrationRoot))
            {
                physicalRoot = NormalizeExpectedInstallRoot(
                    NativeFileSystem.GetStablePathForExistingPath(normalizedRegistrationRoot));
                rootIdentity = InstallOwnership.GetManagedDirectoryIdentity(normalizedRegistrationRoot);
                actualRootIdentityObserved = true;
            }
            else if (Directory.Exists(normalizedSourceRoot))
            {
                rootIdentity = InstallOwnership.GetManagedDirectoryIdentity(normalizedSourceRoot);
                actualRootIdentityObserved = true;
                if (!string.Equals(
                    stateRootIdentity,
                    rootIdentity,
                    StringComparison.OrdinalIgnoreCase))
                {
                    physicalRoot = null;
                }
            }
            else
            {
                // 没有实际目录身份可复验时，不把状态文件中的物理别名扩入删除范围。
                physicalRoot = null;
            }

            bool stateOwned = StateBelongsToInstallation(
                state,
                installId,
                normalizedRegistrationRoot,
                physicalRoot,
                rootIdentity);
            bool stateInstallIdMatches = state != null && string.Equals(
                TryGetValidInstallId(state),
                installId,
                StringComparison.OrdinalIgnoreCase);
            List<string> aliases = new List<string>();
            AddPathAlias(aliases, normalizedRegistrationRoot);
            AddPathAlias(aliases, physicalRoot);
            if (stateOwned && actualRootIdentityObserved)
            {
                AddPathAlias(aliases, TryResolveStateInstallRoot(state));
                AddPathAlias(aliases, state.PhysicalInstallRoot);
            }

            HashSet<string> protocols = new HashSet<string>(KnownProtocols, StringComparer.OrdinalIgnoreCase);
            HashSet<string> progIds = new HashSet<string>(KnownProgIds, StringComparer.OrdinalIgnoreCase);
            HashSet<string> extensions = new HashSet<string>(KnownExtensions, StringComparer.OrdinalIgnoreCase);
            HashSet<string> executableNames = new HashSet<string>(KnownExecutableNames, StringComparer.OrdinalIgnoreCase);
            HashSet<string> appUserModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                AppUserModelId
            };
            if (stateOwned)
            {
                AddSafeProtocolCandidates(protocols, state.Protocols, warnings, "状态文件");
                AddSafeRegistryCandidates(progIds, state.ProgIds, warnings, "状态文件 ProgID");
                AddSafeExtensionCandidates(extensions, state.Extensions, warnings, "状态文件");
                if (IsSafeRegistryComponent(state.AppUserModelId))
                {
                    appUserModelIds.Add(state.AppUserModelId.Trim());
                }
                string executableName = Path.GetFileName(state.ExecutablePath);
                if (ShellResourceNameRules.IsSafeExecutableName(executableName))
                {
                    executableNames.Add(executableName);
                }
            }
            else if (stateSnapshot.Kind == IntegrationStateSnapshotKind.Malformed)
            {
                warnings.Add("integration.json 已损坏，清理范围将从安装清单和固定标识重建。");
            }
            AddProfileCandidates(
                normalizedSourceRoot,
                protocols,
                progIds,
                extensions,
                executableNames,
                appUserModelIds);

            ShellIntegrationCleanupJournalRecord journal = new ShellIntegrationCleanupJournalRecord
            {
                OperationId = Guid.NewGuid().ToString("N"),
                Phase = requestedPhase,
                Purpose = purpose,
                DeploymentOperationId = deploymentOperationId,
                InstallId = installId,
                RegistrationRoot = normalizedRegistrationRoot,
                PhysicalRoot = physicalRoot,
                RootIdentity = rootIdentity,
                RootAliases = aliases,
                Protocols = protocols.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                ProgIds = progIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                Extensions = extensions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                ExecutableNames = executableNames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                AppUserModelIds = appUserModelIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                IntegrationStateSha256 = stateFileExisted &&
                    (stateOwned ||
                     stateInstallIdMatches ||
                     stateSnapshot.Kind == IntegrationStateSnapshotKind.Malformed)
                    ? stateSnapshot.Sha256
                    : null
            };
            journal.Shortcuts = BuildShortcutReceipts(stateOwned ? state : null, journal, warnings);

            WriteCleanupJournal(journal);
            UpgradeShellRegistryOwnershipMarkers(journal);
            if (stateOwned && !state.CleanupPending)
            {
                state.CleanupPending = true;
                try
                {
                    PortableStorage.SaveIntegrationState(state);
                }
                catch (Exception exception)
                {
                    warnings.Add("独立清理 journal 已保存，但无法同步旧 integration.json 待清理标记：" + exception.Message);
                }
            }
            return journal;
        }

        private static ShellIntegrationCleanupResult ExecuteCleanupJournalCore(
            ShellIntegrationCleanupJournalRecord journal)
        {
            if (journal == null) throw new ArgumentNullException(nameof(journal));
            List<string> warnings = new List<string>();
            if (journal.Phase == ShellIntegrationCleanupPhase.Prepared)
            {
                return new ShellIntegrationCleanupResult(
                    false,
                    new[] { "系统集成清理事务尚未越过卸载提交点，未执行任何删除。" });
            }
            if (journal.Phase == ShellIntegrationCleanupPhase.Completed)
            {
                bool deleted = TryCleanup(
                    warnings,
                    "integration-cleanup.json",
                    ShellIntegrationCleanupJournal.Delete);
                return new ShellIntegrationCleanupResult(deleted, warnings.AsReadOnly());
            }
            if (journal.Phase != ShellIntegrationCleanupPhase.Armed)
            {
                throw new InvalidDataException("系统集成清理事务阶段无效。");
            }

            bool cleanupFailed = false;
            foreach (ShellIntegrationShortcutReceipt shortcut in journal.Shortcuts.OrderBy(
                value => value.Path,
                StringComparer.OrdinalIgnoreCase))
            {
                if (!TryCleanup(warnings, "快捷方式 " + shortcut.Path, delegate
                {
                    DeleteShortcutIfReceiptMatches(shortcut, journal, warnings);
                }))
                {
                    cleanupFailed = true;
                }
            }

            foreach (string protocol in journal.Protocols.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                string registryPath = @"Software\Classes\" + protocol;
                if (!TryCleanup(warnings, "协议 " + protocol, delegate
                {
                    ShellRegistryOwnership ownership = ShellOwnershipChecker.GetRegistryCommandTreeOwnership(
                        registryPath,
                        journal);
                    ShellOwnershipChecker.ThrowIfOwnershipUnknown(ownership, registryPath);
                    if (ownership == ShellRegistryOwnership.Owned)
                    {
                        Registry.CurrentUser.DeleteSubKeyTree(registryPath, false);
                    }
                }))
                {
                    cleanupFailed = true;
                }
            }

            Dictionary<string, bool> ownedProgIdTrees =
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> referenceCleanupProgIds =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string progId in journal.ProgIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                string registryPath = @"Software\Classes\" + progId;
                if (!TryCleanup(warnings, "ProgID " + progId + " 归属检查", delegate
                {
                    bool exists = ShellOwnershipChecker.RegistryKeyExists(registryPath);
                    ShellRegistryOwnership ownership = exists
                        ? ShellOwnershipChecker.GetRegistryCommandTreeOwnership(registryPath, journal)
                        : ShellRegistryOwnership.NotOwned;
                    ShellOwnershipChecker.ThrowIfOwnershipUnknown(ownership, registryPath);
                    bool owned = ownership == ShellRegistryOwnership.Owned;
                    ownedProgIdTrees[progId] = owned;
                    if (owned || !exists)
                    {
                        referenceCleanupProgIds.Add(progId);
                    }
                }))
                {
                    cleanupFailed = true;
                }
            }

            foreach (string progId in referenceCleanupProgIds.OrderBy(
                value => value,
                StringComparer.OrdinalIgnoreCase))
            {
                bool referencesCleaned = true;
                foreach (string extension in journal.Extensions.OrderBy(
                    value => value,
                    StringComparer.OrdinalIgnoreCase))
                {
                    if (!TryCleanup(warnings, extension + " -> " + progId, delegate
                    {
                        DeleteRegistryValue(
                            @"Software\Classes\" + extension + @"\OpenWithProgids",
                            progId);
                    }))
                    {
                        referencesCleaned = false;
                        cleanupFailed = true;
                    }
                }
                bool treeOwned;
                if (referencesCleaned &&
                    ownedProgIdTrees.TryGetValue(progId, out treeOwned) &&
                    treeOwned &&
                    !TryCleanup(warnings, "ProgID " + progId, delegate
                    {
                        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + progId, false);
                    }))
                {
                    cleanupFailed = true;
                }
            }

            foreach (string executableName in journal.ExecutableNames.OrderBy(
                value => value,
                StringComparer.OrdinalIgnoreCase))
            {
                string applicationPath = @"Software\Classes\Applications\" + executableName;
                if (!TryCleanup(warnings, "Applications\\" + executableName, delegate
                {
                    ShellRegistryOwnership ownership = ShellOwnershipChecker.GetRegistryCommandTreeOwnership(
                        applicationPath,
                        journal);
                    ShellOwnershipChecker.ThrowIfOwnershipUnknown(ownership, applicationPath);
                    if (ownership == ShellRegistryOwnership.Owned)
                    {
                        Registry.CurrentUser.DeleteSubKeyTree(applicationPath, false);
                    }
                }))
                {
                    cleanupFailed = true;
                }

                string appPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + executableName;
                if (!TryCleanup(warnings, "App Paths\\" + executableName, delegate
                {
                    ShellRegistryOwnership ownership = ShellOwnershipChecker.GetRegistryPathEntryOwnership(appPath, journal);
                    ShellOwnershipChecker.ThrowIfOwnershipUnknown(ownership, appPath);
                    if (ownership == ShellRegistryOwnership.Owned)
                    {
                        Registry.CurrentUser.DeleteSubKeyTree(appPath, false);
                    }
                }))
                {
                    cleanupFailed = true;
                }
            }

            bool capabilitiesOwned = false;
            bool capabilitiesRegistrationMayBeRemoved = false;
            if (!TryCleanup(warnings, "默认应用 Capabilities 归属检查", delegate
            {
                bool exists = ShellOwnershipChecker.RegistryKeyExists(ShellRegistrationWriter.CapabilitiesPath);
                ShellRegistryOwnership ownership = exists
                    ? ShellOwnershipChecker.GetRegistryResourceTreeOwnership(
                        ShellRegistrationWriter.CapabilitiesPath,
                        "ApplicationIcon",
                        journal)
                    : ShellRegistryOwnership.NotOwned;
                ShellOwnershipChecker.ThrowIfOwnershipUnknown(ownership, ShellRegistrationWriter.CapabilitiesPath);
                capabilitiesOwned = ownership == ShellRegistryOwnership.Owned;
                capabilitiesRegistrationMayBeRemoved = capabilitiesOwned || !exists;
            }))
            {
                cleanupFailed = true;
            }
            bool registeredApplicationCleaned = true;
            if (capabilitiesRegistrationMayBeRemoved &&
                !TryCleanup(warnings, "RegisteredApplications\\Codex", delegate
            {
                using (RegistryKey registered = Registry.CurrentUser.OpenSubKey(ShellRegistrationWriter.RegisteredApplicationsPath, true))
                {
                    if (registered != null && string.Equals(
                        registered.GetValue("Codex") as string,
                        ShellRegistrationWriter.CapabilitiesPath,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        registered.DeleteValue("Codex", false);
                    }
                }
            }))
            {
                registeredApplicationCleaned = false;
                cleanupFailed = true;
            }
            if (capabilitiesOwned &&
                registeredApplicationCleaned &&
                !TryCleanup(warnings, "默认应用 Capabilities", delegate
                {
                    Registry.CurrentUser.DeleteSubKeyTree(ShellRegistrationWriter.CapabilitiesPath, false);
                }))
            {
                cleanupFailed = true;
            }

            foreach (string appId in journal.AppUserModelIds.OrderBy(
                value => value,
                StringComparer.OrdinalIgnoreCase))
            {
                string appIdPath = @"Software\Classes\AppUserModelId\" + appId;
                if (!TryCleanup(warnings, "AppUserModelId " + appId, delegate
                {
                    ShellRegistryOwnership ownership = ShellOwnershipChecker.GetRegistryResourceTreeOwnership(
                        appIdPath,
                        "IconUri",
                        journal);
                    ShellOwnershipChecker.ThrowIfOwnershipUnknown(ownership, appIdPath);
                    if (ownership == ShellRegistryOwnership.Owned)
                    {
                        Registry.CurrentUser.DeleteSubKeyTree(appIdPath, false);
                    }
                }))
                {
                    cleanupFailed = true;
                }
            }

            if (!cleanupFailed &&
                !TryCleanup(warnings, "integration.json", delegate
                {
                    DeleteIntegrationStateIfOwned(journal, warnings);
                }))
            {
                cleanupFailed = true;
            }
            if (!cleanupFailed)
            {
                try
                {
                    journal.Phase = ShellIntegrationCleanupPhase.Completed;
                    WriteCleanupJournal(journal);
                }
                catch (Exception exception)
                {
                    warnings.Add("记录系统集成清理完成阶段失败：" + exception.Message);
                    cleanupFailed = true;
                }
            }
            if (!cleanupFailed &&
                !TryCleanup(
                    warnings,
                    "integration-cleanup.json",
                    ShellIntegrationCleanupJournal.Delete))
            {
                cleanupFailed = true;
            }
            if (cleanupFailed)
            {
                warnings.Add("部分系统集成尚未清理，独立 cleanup journal 已保留，后续可安全重试。");
            }

            try
            {
                NotifyShellChanged();
            }
            catch (Exception exception)
            {
                warnings.Add("通知 Windows Shell 刷新失败：" + exception.Message);
            }
            return new ShellIntegrationCleanupResult(!cleanupFailed, warnings.AsReadOnly());
        }

        private static void WriteCleanupJournal(ShellIntegrationCleanupJournalRecord journal)
        {
            Func<ShellIntegrationCleanupPhase, Exception> injector =
                CleanupJournalWriteFailureInjectorForTest;
            Exception injected = injector == null ? null : injector(journal.Phase);
            if (injected != null)
            {
                throw injected;
            }
            ShellIntegrationCleanupJournal.Write(journal);
        }

        private static bool TryCleanup(IList<string> warnings, string label, Action action)
        {
            try
            {
                Func<string, Exception> injector = CleanupFailureInjectorForTest;
                Exception injected = injector == null ? null : injector(label);
                if (injected != null)
                {
                    throw injected;
                }
                action();
                return true;
            }
            catch (Exception exception)
            {
                warnings.Add(label + " 清理失败：" + exception.Message);
                return false;
            }
        }


    }
}
