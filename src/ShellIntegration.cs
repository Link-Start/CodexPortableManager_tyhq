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
        public const string AppUserModelId = "com.openai.codex";

        private const string ShellIntegrationResourceName = "当前用户 Shell 集成";
        private const uint ShellChangeUpdateItem = 0x00002000;
        private const uint ShellChangeAssociationsChanged = 0x08000000;
        private const uint ShellNotifyPathWFlushNoWait = 0x00002005;
        private const uint ShellNotifyIdListFlushNoWait = 0x00002000;

        private static readonly string[] KnownProtocols = { "codex" };
        private static readonly string[] KnownProgIds =
        {
            "OpenAI.Codex.Spreadsheet",
            "OpenAI.Codex.CodexFile",
            "OpenAI.Codex.Skill"
        };
        private static readonly string[] KnownExtensions =
        {
            ".csv", ".tsv", ".xls", ".xlsm", ".xlsx", ".skill"
        };
        private static readonly string[] KnownExecutableNames =
        {
            "ChatGPT.exe", "Codex.exe", "CodexDesktop.exe"
        };

        internal static Func<string, Exception> CleanupFailureInjectorForTest;
        internal static Func<ShellIntegrationCleanupPhase, Exception> CleanupJournalWriteFailureInjectorForTest;
        internal static Func<Tuple<string, string>> ShortcutRootsProviderForTest;
        internal static Action<string> ShortcutFinalDeleteObserverForTest;

        private enum IntegrationStateSnapshotKind
        {
            Missing = 0,
            Valid = 1,
            Malformed = 2
        }

        private sealed class IntegrationStateSnapshot
        {
            internal IntegrationStateSnapshot(
                IntegrationStateSnapshotKind kind,
                IntegrationState state,
                string sha256)
            {
                Kind = kind;
                State = state;
                Sha256 = sha256;
            }

            internal IntegrationStateSnapshotKind Kind { get; private set; }
            internal IntegrationState State { get; private set; }
            internal string Sha256 { get; private set; }
        }

        public static string TryDiscoverPortableInstallRoot()
        {
            using (OperationFileLock integrationLock = AcquireShellIntegrationLock())
            {
                return TryDiscoverPortableInstallRoot(GetPortableDiscoveryRegistryPaths());
            }
        }

        internal static string TryGetPendingCleanupRoot()
        {
            using (OperationFileLock integrationLock = AcquireShellIntegrationLock())
            {
                ShellIntegrationCleanupJournalRecord journal = ShellIntegrationCleanupJournal.Read();
                if (journal != null)
                {
                    foreach (string candidate in GetDeploymentRootCandidates(journal))
                    {
                        try
                        {
                            return DeploymentEngine.ValidateInstallRoot(candidate);
                        }
                        catch
                        {
                        }
                    }
                    return null;
                }
                IntegrationStateSnapshot snapshot = ReadIntegrationStateSnapshot();
                IntegrationState state = snapshot.Kind == IntegrationStateSnapshotKind.Valid
                    ? snapshot.State
                    : null;
                return state != null && state.CleanupPending
                    ? TryResolveStateInstallRoot(state)
                    : null;
            }
        }

        public static IReadOnlyList<string> Register(
            PackageProfile profile,
            string expectedInstallRoot,
            string exePath,
            string iconPath,
            string managerPath)
        {
            using (OperationFileLock integrationLock = AcquireShellIntegrationLock())
            {
                return RegisterCore(profile, expectedInstallRoot, exePath, iconPath, managerPath);
            }
        }

        public static IReadOnlyList<string> Remove(string expectedInstallRoot)
        {
            return RemoveWithResult(expectedInstallRoot).Warnings;
        }

        internal static ShellIntegrationCleanupResult RemoveWithResult(string expectedInstallRoot)
        {
            using (OperationFileLock integrationLock = AcquireShellIntegrationLock())
            {
                return RemoveWithResultCore(expectedInstallRoot, null, expectedInstallRoot);
            }
        }

        internal static IReadOnlyList<string> PrepareCleanup(
            string registrationRoot,
            string sourceRoot,
            string installId,
            string deploymentOperationId)
        {
            using (OperationFileLock integrationLock = AcquireShellIntegrationLock())
            {
                EnsureMatchingUninstallDeployment(
                    new[] { registrationRoot, sourceRoot },
                    installId,
                    deploymentOperationId,
                    false);
                List<string> warnings = new List<string>();
                PrepareCleanupCore(
                    registrationRoot,
                    sourceRoot,
                    installId,
                    ShellIntegrationCleanupPurpose.DeploymentUninstall,
                    deploymentOperationId,
                    ShellIntegrationCleanupPhase.Prepared,
                    warnings);
                return warnings.AsReadOnly();
            }
        }

        internal static ShellIntegrationCleanupResult CompletePreparedCleanup(
            string registrationRoot,
            string sourceRoot,
            string installId,
            string deploymentOperationId)
        {
            using (OperationFileLock integrationLock = AcquireShellIntegrationLock())
            {
                List<string> warnings = new List<string>();
                ShellIntegrationCleanupJournalRecord journal = ShellIntegrationCleanupJournal.Read();
                if (journal == null)
                {
                    EnsureMatchingUninstallDeployment(
                        new[] { registrationRoot, sourceRoot },
                        installId,
                        deploymentOperationId,
                        true);
                    journal = PrepareCleanupCore(
                        registrationRoot,
                        sourceRoot,
                        installId,
                        ShellIntegrationCleanupPurpose.DeploymentUninstall,
                        deploymentOperationId,
                        ShellIntegrationCleanupPhase.Prepared,
                        warnings);
                }
                EnsureJournalMatches(journal, registrationRoot, installId);
                EnsureDeploymentCleanupPurpose(
                    journal,
                    deploymentOperationId,
                    true);
                if (journal.Phase == ShellIntegrationCleanupPhase.Prepared)
                {
                    journal.Purpose = ShellIntegrationCleanupPurpose.DeploymentUninstall;
                    journal.DeploymentOperationId = deploymentOperationId;
                    journal.Phase = ShellIntegrationCleanupPhase.Armed;
                    WriteCleanupJournal(journal);
                }
                ShellIntegrationCleanupResult result = ExecuteCleanupJournalCore(journal);
                warnings.AddRange(result.Warnings);
                return new ShellIntegrationCleanupResult(result.Complete, warnings.AsReadOnly());
            }
        }

        internal static void CancelPreparedCleanup(
            string registrationRoot,
            string installId,
            string deploymentOperationId)
        {
            using (OperationFileLock integrationLock = AcquireShellIntegrationLock())
            {
                ShellIntegrationCleanupJournalRecord journal = ShellIntegrationCleanupJournal.Read();
                if (journal == null)
                {
                    return;
                }
                EnsureJournalMatches(journal, registrationRoot, installId);
                EnsureDeploymentCleanupPurpose(
                    journal,
                    deploymentOperationId,
                    false);
                if (journal.Phase != ShellIntegrationCleanupPhase.Prepared)
                {
                    throw new InvalidOperationException("已经开始执行的系统集成清理事务不能取消。");
                }
                IntegrationStateSnapshot snapshot = ReadIntegrationStateSnapshot();
                IntegrationState state = snapshot.Kind == IntegrationStateSnapshotKind.Valid
                    ? snapshot.State
                    : null;
                if (state != null &&
                    state.CleanupPending &&
                    StateBelongsToInstallation(
                        state,
                        journal.InstallId,
                        journal.RegistrationRoot,
                        journal.PhysicalRoot,
                        journal.RootIdentity))
                {
                    state.CleanupPending = false;
                    PortableStorage.SaveIntegrationState(state);
                }
                ShellIntegrationCleanupJournal.Delete();
            }
        }

        internal static ShellIntegrationCleanupResult RecoverPendingCleanup()
        {
            using (OperationFileLock integrationLock = AcquireShellIntegrationLock())
            {
                return RecoverPendingCleanupCore();
            }
        }

        internal static bool IsCleanupPendingForRoot(string expectedInstallRoot)
        {
            string installRoot = NormalizeExpectedInstallRoot(expectedInstallRoot);
            using (OperationFileLock integrationLock = AcquireShellIntegrationLock())
            {
                ShellIntegrationCleanupJournalRecord journal;
                try
                {
                    journal = ShellIntegrationCleanupJournal.Read();
                }
                catch
                {
                    return ShellIntegrationCleanupJournal.Exists();
                }
                if (journal != null && JournalMatchesRoot(journal, installRoot))
                {
                    return true;
                }
                IntegrationStateSnapshot snapshot = ReadIntegrationStateSnapshot();
                IntegrationState state = snapshot.Kind == IntegrationStateSnapshotKind.Valid
                    ? snapshot.State
                    : null;
                return state != null &&
                    state.CleanupPending &&
                    StateMatchesRoot(state, installRoot);
            }
        }


    }
}
