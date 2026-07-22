using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPortableManager
{
internal static partial class RegressionTestRunner
{
    private static readonly IntPtr HkeyCurrentUser =
        new IntPtr(unchecked((int)0x80000001));

    private static void TestShellResourceNameRules()
    {
        Assert(ShellResourceNameRules.IsSafeProtocol("codex+local-1.0"),
            "统一规则拒绝了有效协议名。");
        Assert(!ShellResourceNameRules.IsSafeProtocol(" codex") &&
            !ShellResourceNameRules.IsSafeProtocol("1codex"),
            "统一规则接受了非 canonical 或非法协议名。");
        Assert(ShellResourceNameRules.IsSafeExtension(".xlsx") &&
            !ShellResourceNameRules.IsSafeExtension(".xlsx ") &&
            !ShellResourceNameRules.IsSafeExtension(".bad/path"),
            "统一规则没有正确约束扩展名。");
        Assert(ShellResourceNameRules.IsSafeRegistryComponent("OpenAI.Codex.File") &&
            !ShellResourceNameRules.IsSafeRegistryComponent(" OpenAI.Codex.File") &&
            !ShellResourceNameRules.IsSafeRegistryComponent("OpenAI\\Codex"),
            "统一规则没有正确约束注册表组件。");
        Assert(ShellResourceNameRules.IsSafeExecutableName("Codex.exe") &&
            !ShellResourceNameRules.IsSafeExecutableName("..") &&
            !ShellResourceNameRules.IsSafeExecutableName("C:Codex.exe"),
            "统一规则没有正确约束可执行文件名。");
    }

    private static void TestShellIntegrationResourceLock()
    {
        string resourceKey = "shell-integration-regression-" + Guid.NewGuid().ToString("N");
        OperationFileLock first = OperationFileLock.AcquireResource(resourceKey, "Shell 集成回归资源");
        try
        {
            bool cancelled = false;
            using (CancellationTokenSource cancellation = new CancellationTokenSource(600))
            {
                Task<OperationFileLock> second = OperationFileLock.AcquireResourceAsync(
                    resourceKey,
                    "Shell 集成回归资源",
                    cancellation.Token);
                try
                {
                    using (second.GetAwaiter().GetResult()) { }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
            }
            Assert(cancelled, "同一用户级 Shell 资源锁被并发获得。");
        }
        finally
        {
            first.Dispose();
        }

        using (OperationFileLock reacquired =
            OperationFileLock.AcquireResource(resourceKey, "Shell 集成回归资源"))
        {
        }
    }

    private static void TestPortableShortcutConflictIsPreserved()
    {
        string caseRoot = NewCaseRoot("shortcut-conflict");
        string installRoot = Path.Combine(caseRoot, "Codex");
        string otherRoot = Path.Combine(caseRoot, "Other");
        string shortcutRoot = Path.Combine(caseRoot, "Shortcuts");
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(otherRoot);
        Directory.CreateDirectory(shortcutRoot);
        string portableExe = Path.Combine(installRoot, "Codex.exe");
        string otherExe = Path.Combine(otherRoot, "Other.exe");
        File.WriteAllBytes(portableExe, new byte[] { 1 });
        File.WriteAllBytes(otherExe, new byte[] { 2 });

        string preferred = Path.Combine(shortcutRoot, "Codex.lnk");
        string fallback = Path.Combine(shortcutRoot, "Codex Portable.lnk");
        ShortcutHelper.Create(
            preferred,
            otherExe,
            string.Empty,
            otherRoot,
            otherExe,
            "其他程序",
            null);
        byte[] originalPreferred = File.ReadAllBytes(preferred);
        List<string> warnings = new List<string>();
        string selected = ShellIntegration.SelectPortableShortcutPath(
            preferred,
            fallback,
            installRoot,
            warnings);
        Assert(PathsEqual(selected, fallback), "同名快捷方式冲突时没有选择便携版备用名称。");
        Assert(BytesEqual(originalPreferred, File.ReadAllBytes(preferred)),
            "选择备用快捷方式时改写了原有 Codex.lnk。");

        ShortcutHelper.Create(
            fallback,
            otherExe,
            string.Empty,
            otherRoot,
            otherExe,
            "其他程序备用入口",
            null);
        byte[] originalFallback = File.ReadAllBytes(fallback);
        selected = ShellIntegration.SelectPortableShortcutPath(
            preferred,
            fallback,
            installRoot,
            warnings);
        Assert(string.IsNullOrWhiteSpace(selected), "两个名称均被占用时仍选择了覆盖目标。");
        Assert(BytesEqual(originalPreferred, File.ReadAllBytes(preferred)),
            "冲突检测改写了原有 Codex.lnk。");
        Assert(BytesEqual(originalFallback, File.ReadAllBytes(fallback)),
            "冲突检测改写了原有 Codex Portable.lnk。");
    }

    private static void TestShellCleanupRemainsRetryable()
    {
        string caseRoot = NewCaseRoot("shell-cleanup-retry");
        string installRoot = Path.Combine(caseRoot, "Codex");
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(
            installRoot,
            "1.0.0.0",
            installId,
            "shell-cleanup-retry");
        string executablePath = Path.Combine(installRoot, "app", "Codex.exe");
        string physicalRoot = NativeFileSystem.GetStablePathForExistingPath(installRoot);
        string isolatedRegistryPath =
            @"Software\CodexPortableManagerRegression\" + Guid.NewGuid().ToString("N");
        RegistryKey overrideRoot = Registry.CurrentUser.CreateSubKey(isolatedRegistryPath);
        if (overrideRoot == null)
        {
            throw new InvalidOperationException("无法创建隔离注册表测试根。");
        }

        try
        {
            int overrideResult = RegOverridePredefKey(HkeyCurrentUser, overrideRoot.Handle);
            if (overrideResult != 0)
            {
                throw new InvalidOperationException(
                    "无法重定向 HKCU，Win32=" + overrideResult.ToString(CultureInfo.InvariantCulture));
            }
            try
            {
                PortableStorage.DeleteIntegrationState();
                PortableStorage.SaveIntegrationState(new IntegrationState
                {
                    InstallId = installId,
                    InstallRoot = installRoot,
                    PhysicalInstallRoot = physicalRoot,
                    RootIdentity = InstallOwnership.GetManagedDirectoryIdentity(installRoot),
                    ExecutablePath = executablePath,
                    AppUserModelId = ShellIntegration.AppUserModelId,
                    Protocols = new List<string>(),
                    ProgIds = new List<string> { "OpenAI.Codex.Spreadsheet" },
                    Extensions = new List<string> { ".csv" },
                    ShortcutPaths = new List<string>(),
                    CleanupPending = false
                });

                using (RegistryKey progId = Registry.CurrentUser.CreateSubKey(
                    @"Software\Classes\OpenAI.Codex.Spreadsheet"))
                {
                    progId.SetValue("CodexPortableInstallRoot", installRoot);
                    using (RegistryKey command = progId.CreateSubKey(@"shell\open\command"))
                    {
                        command.SetValue(string.Empty, "\"" + executablePath + "\" \"%1\"");
                    }
                }
                using (RegistryKey openWith = Registry.CurrentUser.CreateSubKey(
                    @"Software\Classes\.csv\OpenWithProgids"))
                {
                    openWith.SetValue(
                        "OpenAI.Codex.Spreadsheet",
                        new byte[0],
                        RegistryValueKind.None);
                }
                using (RegistryKey capabilities = Registry.CurrentUser.CreateSubKey(
                    @"Software\OpenAI\CodexPortable\Capabilities"))
                {
                    capabilities.SetValue("CodexPortableInstallRoot", installRoot);
                    capabilities.SetValue("ApplicationIcon", executablePath + ",0");
                }
                using (RegistryKey registered = Registry.CurrentUser.CreateSubKey(
                    @"Software\RegisteredApplications"))
                {
                    registered.SetValue(
                        "Codex",
                        @"Software\OpenAI\CodexPortable\Capabilities");
                }

                ShellIntegration.CleanupFailureInjectorForTest = label =>
                    string.Equals(
                        label,
                        ".csv -> OpenAI.Codex.Spreadsheet",
                        StringComparison.Ordinal) ||
                    string.Equals(
                        label,
                        @"RegisteredApplications\Codex",
                        StringComparison.Ordinal)
                        ? new IOException("注入的清理失败")
                        : null;
                ShellIntegrationCleanupResult first =
                    ShellIntegration.RemoveWithResult(installRoot);
                Assert(!first.Complete, "依赖项清理失败时错误报告为完整成功。");
                using (RegistryKey progId = Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\OpenAI.Codex.Spreadsheet"))
                {
                    Assert(progId != null,
                        "OpenWith 清理失败后提前删除了 ProgID 归属证据。");
                }
                using (RegistryKey capabilities = Registry.CurrentUser.OpenSubKey(
                    @"Software\OpenAI\CodexPortable\Capabilities"))
                {
                    Assert(capabilities != null,
                        "RegisteredApplications 清理失败后提前删除了 Capabilities 归属证据。");
                }
                IntegrationState pending = PortableStorage.LoadIntegrationState();
                Assert(pending != null && pending.CleanupPending,
                    "部分清理失败后没有持久化待重试状态。");

                ShellIntegration.CleanupFailureInjectorForTest = null;
                ShellIntegrationCleanupResult second =
                    ShellIntegration.RemoveWithResult(installRoot);
                Assert(second.Complete, "清理故障解除后没有完成可重入清理。");
                using (RegistryKey progId = Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\OpenAI.Codex.Spreadsheet"))
                {
                    Assert(progId == null, "第二次清理后 ProgID 仍存在。");
                }
                using (RegistryKey openWith = Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\.csv\OpenWithProgids"))
                {
                    Assert(openWith == null ||
                        Array.IndexOf(openWith.GetValueNames(), "OpenAI.Codex.Spreadsheet") < 0,
                        "第二次清理后 OpenWithProgids 引用仍存在。");
                }
                using (RegistryKey capabilities = Registry.CurrentUser.OpenSubKey(
                    @"Software\OpenAI\CodexPortable\Capabilities"))
                {
                    Assert(capabilities == null, "第二次清理后 Capabilities 仍存在。");
                }
                using (RegistryKey registered = Registry.CurrentUser.OpenSubKey(
                    @"Software\RegisteredApplications"))
                {
                    Assert(registered == null || registered.GetValue("Codex") == null,
                        "第二次清理后 RegisteredApplications 引用仍存在。");
                }
                Assert(!PortableStorage.IntegrationStateFileExists(),
                    "完整清理后 integration.json 没有删除。");
            }
            finally
            {
                ShellIntegration.CleanupFailureInjectorForTest = null;
                RegOverridePredefKeyRaw(HkeyCurrentUser, IntPtr.Zero);
                PortableStorage.DeleteIntegrationState();
            }
        }
        finally
        {
            overrideRoot.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(isolatedRegistryPath, false);
        }
    }

    private static void TestUninstallReportsPendingShellCleanup()
    {
        string caseRoot = NewCaseRoot("uninstall-shell-cleanup-pending");
        string installRoot = Path.Combine(caseRoot, "Codex");
        string installId = Guid.NewGuid().ToString("N");
        CreateRunnableCodex(
            installRoot,
            "1.0.0.0",
            installId,
            "uninstall-shell-cleanup-pending");
        PackageProfile profile = PackageProfileReader.Read(installRoot);
        string executablePath = PackageProfileReader.GetExecutablePath(installRoot, profile);
        string physicalRoot = NativeFileSystem.GetStablePathForExistingPath(installRoot);
        PortableStorage.DeleteIntegrationState();
        PortableStorage.SaveIntegrationState(new IntegrationState
        {
            InstallId = installId,
            InstallRoot = installRoot,
            PhysicalInstallRoot = physicalRoot,
            RootIdentity = InstallOwnership.GetManagedDirectoryIdentity(installRoot),
            ExecutablePath = executablePath,
            AppUserModelId = ShellIntegration.AppUserModelId,
            Protocols = new List<string>(),
            ProgIds = new List<string>(),
            Extensions = new List<string>(),
            ShortcutPaths = new List<string>(),
            CleanupPending = false
        });

        try
        {
            ShellIntegration.CleanupFailureInjectorForTest = label =>
                string.Equals(label, "integration.json", StringComparison.Ordinal)
                    ? new IOException("注入的状态删除失败")
                    : null;
            using (CodexPortableService service = CreateService(new List<string>()))
            {
                UninstallResult result = service.UninstallPortable(installRoot);
                Assert(!result.DirectoryCleanupPending,
                    "程序 tombstone 已清理时错误报告目录清理待完成。");
                Assert(result.IntegrationCleanupPending,
                    "integration.json 删除失败时未报告系统集成待清理。");
                Assert(!Directory.Exists(installRoot),
                    "系统集成清理失败错误回滚了已提交的程序卸载。");
                Assert(!DeploymentJournal.Exists(installRoot),
                    "仅系统集成待清理时仍残留部署 journal。");
            }

            IntegrationState pending = PortableStorage.LoadIntegrationState();
            Assert(pending != null && pending.CleanupPending,
                "卸载后的系统集成待清理状态没有持久化。");
            ShellIntegration.CleanupFailureInjectorForTest = null;
            ShellIntegrationCleanupResult recovered =
                ShellIntegration.RecoverPendingCleanup();
            Assert(recovered.Complete && !PortableStorage.IntegrationStateFileExists(),
                "故障解除后启动恢复没有完成系统集成清理。");
        }
        finally
        {
            ShellIntegration.CleanupFailureInjectorForTest = null;
            PortableStorage.DeleteIntegrationState();
        }
    }

    private static void TestPendingUninstallRootIsRecoveredOnStatus()
    {
        string caseRoot = NewCaseRoot("pending-uninstall-root");
        string installRoot = Path.Combine(caseRoot, "Codex");
        string tombstone = DeploymentEngine.GetUninstallCurrentTombstone(installRoot);
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(
            installRoot,
            "1.0.0.0",
            installId,
            "pending-uninstall-root");
        Directory.Move(installRoot, tombstone);
        CreateUninstallJournal(
            installRoot,
            installId,
            "UninstallPayloadDetached",
            true,
            false);

        string resolved = InstallLocationResolver.ResolveInstallRoot(
            installRoot,
            () => null);
        Assert(PathsEqual(resolved, installRoot),
            "仅剩卸载 journal/tombstone 时启动路径解析丢失了恢复根。");

        using (CodexPortableService service = CreateService(new List<string>()))
        {
            PortableLocalStatus status = service.GetLocalStatus(installRoot);
            Assert(status.HasInstallRoot && status.PortableVersion == null &&
                string.IsNullOrWhiteSpace(status.Error),
                "本地状态刷新没有自动完成待处理卸载事务。");
        }
        Assert(!Directory.Exists(tombstone) && !DeploymentJournal.Exists(installRoot),
            "本地状态刷新后仍残留卸载 tombstone 或 journal。");
    }

    private static void TestShellCleanupJournalSurvivesSubstAliases()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            RunShellCleanupSubstAliasScenario(true);
            RunShellCleanupSubstAliasScenario(false);
        });
    }

    private static void RunShellCleanupSubstAliasScenario(bool stateUsesAlias)
    {
        string caseRoot = NewCaseRoot(stateUsesAlias
            ? "shell-cleanup-subst-state-alias"
            : "shell-cleanup-subst-request-alias");
        string physicalCaseRoot = NativeFileSystem.GetStablePathForExistingPath(caseRoot);
        string physicalRoot = Path.Combine(physicalCaseRoot, "Codex");
        string detachedRoot = Path.Combine(physicalCaseRoot, "Codex.detached");
        string installId = Guid.NewGuid().ToString("N");
        string deploymentOperationId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(
            physicalRoot,
            "1.0.0.0",
            installId,
            stateUsesAlias ? "subst-state-alias" : "subst-request-alias");

        string substDrive = null;
        try
        {
            substDrive = CreateSubstDrive(physicalCaseRoot);
            string aliasRoot = substDrive + @"\Codex";
            string recordedRoot = stateUsesAlias ? aliasRoot : physicalRoot;
            string requestedRoot = stateUsesAlias ? physicalRoot : aliasRoot;
            string rootIdentity = InstallOwnership.GetManagedDirectoryIdentity(physicalRoot);
            string stableRoot = NativeFileSystem.GetStablePathForExistingPath(physicalRoot);

            RunWithIsolatedShellRegistry(
                stateUsesAlias ? "subst-state-alias" : "subst-request-alias",
                delegate
                {
                    SaveOwnedShellIntegrationState(
                        recordedRoot,
                        stableRoot,
                        rootIdentity,
                        installId);
                    WriteProtocolRegistration(recordedRoot, null, null);
                    CreateShellCleanupDeploymentJournal(
                        physicalRoot,
                        installId,
                        deploymentOperationId);

                    ShellIntegration.PrepareCleanup(
                        requestedRoot,
                        physicalRoot,
                        installId,
                        deploymentOperationId);
                    ShellIntegrationCleanupJournalRecord prepared =
                        ShellIntegrationCleanupJournal.Read();
                    Assert(prepared != null &&
                        prepared.Phase == ShellIntegrationCleanupPhase.Prepared,
                        "SUBST 别名清理没有先持久化 Prepared journal。");

                    Directory.Move(physicalRoot, detachedRoot);
                    RemoveSubstDrive(substDrive);
                    substDrive = null;
                    CommitShellCleanupDeploymentJournal(physicalRoot);

                    if (!stateUsesAlias)
                    {
                        ShellIntegration.CleanupFailureInjectorForTest = label =>
                            label.StartsWith("协议 ", StringComparison.Ordinal)
                                ? new IOException("模拟持续 Shell 清理失败")
                                : null;
                        ShellIntegrationCleanupResult pending =
                            ShellIntegration.CompletePreparedCleanup(
                                requestedRoot,
                                detachedRoot,
                                installId,
                                deploymentOperationId);
                        Assert(!pending.Complete,
                            "SUBST 映射解除后的持续故障没有保留 cleanup journal。");
                        string pendingRoot = ShellIntegration.TryGetPendingCleanupRoot();
                        Assert(PathsEqual(pendingRoot, physicalRoot),
                            "失效 SUBST 注册根没有回落到物理待办根。");
                        string resolvedPendingRoot =
                            InstallLocationResolver.ResolveInstallRoot(aliasRoot, () => null);
                        Assert(PathsEqual(resolvedPendingRoot, physicalRoot),
                            "状态路径解析丢失了失效 SUBST 对应的 Shell 清理待办。");
                        ShellIntegration.CleanupFailureInjectorForTest = null;
                    }

                    ShellIntegrationCleanupResult result =
                        ShellIntegration.CompletePreparedCleanup(
                            requestedRoot,
                            detachedRoot,
                            installId,
                            deploymentOperationId);
                    Assert(result.Complete,
                        "SUBST 映射解除后没有按准备阶段的物理身份完成清理。");
                    AssertProtocolRegistrationMissing(
                        "SUBST 映射解除后仍残留 codex 协议注册。");
                    Assert(!PortableStorage.IntegrationStateFileExists(),
                        "SUBST 映射解除后仍残留 integration.json。");
                    Assert(!ShellIntegrationCleanupJournal.Exists(),
                        "SUBST 映射解除后仍残留 cleanup journal。");
                });
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(substDrive))
            {
                RemoveSubstDrive(substDrive);
            }
            ShellIntegration.CleanupFailureInjectorForTest = null;
            ShellIntegration.CleanupJournalWriteFailureInjectorForTest = null;
            ShellIntegrationCleanupJournal.Delete();
            PortableStorage.DeleteIntegrationState();
        }
    }

    private static void TestDamagedIntegrationStateFirstCleanupFailureRecovers()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            RunWithIsolatedShellRegistry("damaged-state-first-failure", delegate
            {
                string caseRoot = NewCaseRoot("damaged-state-first-failure");
                string installRoot = Path.Combine(caseRoot, "Codex");
                string installId = Guid.NewGuid().ToString("N");
                CreateRunnableCodex(
                    installRoot,
                    "1.0.0.0",
                    installId,
                    "damaged-state-first-failure");

                Directory.CreateDirectory(Path.GetDirectoryName(
                    PortableStorage.IntegrationStateFilePath));
                File.WriteAllText(
                    PortableStorage.IntegrationStateFilePath,
                    "{invalid-json",
                    new UTF8Encoding(false));
                WriteProtocolRegistration(installRoot, null, null);

                ShellIntegration.CleanupFailureInjectorForTest = label =>
                    string.Equals(label, "协议 codex", StringComparison.Ordinal)
                        ? new IOException("注入的首次协议清理失败")
                        : null;
                UninstallResult result;
                using (CodexPortableService service = CreateService(new List<string>()))
                {
                    result = service.UninstallPortable(installRoot);
                }

                Assert(result.IntegrationCleanupPending,
                    "Shell 首次动作失败时没有报告系统集成待清理。");
                Assert(!Directory.Exists(installRoot),
                    "Shell 首次动作失败错误回滚了已提交的程序卸载。");
                Assert(result.DirectoryCleanupPending ==
                    DeploymentJournal.Exists(installRoot),
                    "程序目录清理待办与部署 journal 的实际状态不一致。");
                ShellIntegrationCleanupJournalRecord pending =
                    ShellIntegrationCleanupJournal.Read();
                Assert(pending != null &&
                    pending.Phase == ShellIntegrationCleanupPhase.Armed,
                    "Shell 首次动作失败后 cleanup journal 未保持 Armed。");
                AssertProtocolRegistrationExists(
                    "注入失败后 codex 协议注册被意外删除。");
                Assert(PortableStorage.IntegrationStateFileExists(),
                    "注入失败后损坏的 integration.json 被提前删除。");

                ShellIntegration.CleanupFailureInjectorForTest = null;
                ShellIntegrationCleanupResult recovered =
                    ShellIntegration.RecoverPendingCleanup();
                Assert(recovered.Complete,
                    "清除首次动作故障后没有完成 journal 恢复清理。");
                AssertProtocolRegistrationMissing(
                    "恢复清理后 codex 协议注册仍存在。");
                Assert(!PortableStorage.IntegrationStateFileExists(),
                    "恢复清理后损坏的 integration.json 仍存在。");
                Assert(!ShellIntegrationCleanupJournal.Exists(),
                    "恢复清理后 cleanup journal 仍存在。");
            });
        });
    }

    private static void TestMalformedIntegrationStateIsRemovedByDigest()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            RunWithIsolatedShellRegistry("malformed-state-digest", delegate
            {
                string caseRoot = NewCaseRoot("shell-malformed-state-digest");
                string installRoot = Path.Combine(caseRoot, "Codex");
                string installId = Guid.NewGuid().ToString("N");
                CreateRunnableCodex(
                    installRoot,
                    "1.0.0.0",
                    installId,
                    "malformed-state-digest");
                Directory.CreateDirectory(Path.GetDirectoryName(
                    PortableStorage.IntegrationStateFilePath));
                File.WriteAllText(
                    PortableStorage.IntegrationStateFilePath,
                    "{}",
                    new UTF8Encoding(false));
                WriteProtocolRegistration(installRoot, null, null);

                ShellIntegrationCleanupResult result =
                    ShellIntegration.RemoveWithResult(installRoot);
                Assert(result.Complete,
                    "语义损坏的 integration.json 没有按准备阶段摘要完成清理。");
                AssertProtocolRegistrationMissing(
                    "语义损坏状态清理后仍残留 codex 协议注册。");
                Assert(!PortableStorage.IntegrationStateFileExists(),
                    "语义损坏的 integration.json 在摘要未变化时仍被保留。");
                Assert(!ShellIntegrationCleanupJournal.Exists(),
                    "语义损坏状态清理完成后仍残留 cleanup journal。");
            });
        });
    }

    private static void TestIntegrationStateDigestDeletePreservesReplacement()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            IntegrationState first = new IntegrationState
            {
                InstallId = Guid.NewGuid().ToString("N"),
                InstallRoot = @"C:\Portable\First"
            };
            PortableStorage.SaveIntegrationState(first);
            byte[] firstBytes = File.ReadAllBytes(
                PortableStorage.IntegrationStateFilePath);
            string firstSha256 = ComputeSha256Hex(firstBytes);

            IntegrationState replacement = new IntegrationState
            {
                InstallId = Guid.NewGuid().ToString("N"),
                InstallRoot = @"C:\Portable\Replacement"
            };
            PortableStorage.SaveIntegrationState(replacement);
            byte[] replacementBytes = File.ReadAllBytes(
                PortableStorage.IntegrationStateFilePath);
            string replacementSha256 = ComputeSha256Hex(replacementBytes);

            Exception mismatch = CaptureFailure(delegate
            {
                PortableStorage.DeleteIntegrationStateIfSha256Matches(
                    firstSha256);
            });
            Assert(mismatch is InvalidDataException,
                "旧摘要删除没有拒绝原位替换的新 integration.json。");
            Assert(BytesEqual(
                replacementBytes,
                File.ReadAllBytes(PortableStorage.IntegrationStateFilePath)),
                "旧摘要删除修改或删除了替换后的 integration.json。");

            PortableStorage.DeleteIntegrationStateIfSha256Matches(
                replacementSha256);
            Assert(!PortableStorage.IntegrationStateFileExists(),
                "匹配摘要没有删除 integration.json。");
        });
    }

    private static void TestLockedIntegrationStateBlocksCleanupUntilReadable()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            RunWithIsolatedShellRegistry("locked-integration-state", delegate
            {
                string caseRoot = NewCaseRoot("shell-locked-integration-state");
                string installRoot = Path.Combine(caseRoot, "Codex");
                string installId = Guid.NewGuid().ToString("N");
                CreateMinimalCodex(
                    installRoot,
                    "1.0.0.0",
                    installId,
                    "locked-integration-state");
                string stableRoot = NativeFileSystem.GetStablePathForExistingPath(installRoot);
                SaveOwnedShellIntegrationState(
                    installRoot,
                    stableRoot,
                    InstallOwnership.GetManagedDirectoryIdentity(installRoot),
                    installId);
                WriteProtocolRegistration(installRoot, stableRoot, installId);

                ShellIntegrationCleanupResult first;
                using (FileStream held = new FileStream(
                    PortableStorage.IntegrationStateFilePath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None))
                {
                    first = ShellIntegration.RemoveWithResult(installRoot);
                    Assert(!first.Complete,
                        "不可读的 integration.json 被错误视为可安全清理。");
                    AssertProtocolRegistrationExists(
                        "integration.json 不可读时仍删除了 codex 协议注册。");
                    Assert(!ShellIntegrationCleanupJournal.Exists(),
                        "准备阶段无法读取 integration.json 时仍留下了 Armed journal。");
                }

                ShellIntegrationCleanupResult second =
                    ShellIntegration.RemoveWithResult(installRoot);
                Assert(second.Complete,
                    "解除 integration.json 占用后没有完成可重试清理。");
                AssertProtocolRegistrationMissing(
                    "解除 integration.json 占用后仍残留 codex 协议注册。");
                Assert(!PortableStorage.IntegrationStateFileExists(),
                    "解除 integration.json 占用后仍残留状态文件。");
            });
        });
    }

    private static void TestMismatchedIntegrationIdentityDoesNotExpandCleanupAliases()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            RunWithIsolatedShellRegistry("mismatched-state-identity", delegate
            {
                string caseRoot = NewCaseRoot("shell-mismatched-state-identity");
                string registrationRoot = Path.Combine(caseRoot, "Codex");
                string sourceRoot = Path.Combine(caseRoot, "Codex.detached");
                string forgedRoot = Path.Combine(caseRoot, "ForgedAlias");
                string installId = Guid.NewGuid().ToString("N");
                string deploymentOperationId = Guid.NewGuid().ToString("N");
                CreateMinimalCodex(
                    registrationRoot,
                    "1.0.0.0",
                    installId,
                    "mismatched-state-identity");
                Directory.Move(registrationRoot, sourceRoot);
                Directory.CreateDirectory(forgedRoot);
                PortableStorage.SaveIntegrationState(new IntegrationState
                {
                    InstallId = installId,
                    InstallRoot = registrationRoot,
                    PhysicalInstallRoot = forgedRoot,
                    RootIdentity = InstallOwnership.GetManagedDirectoryIdentity(forgedRoot),
                    ExecutablePath = Path.Combine(registrationRoot, "app", "Codex.exe"),
                    AppUserModelId = ShellIntegration.AppUserModelId,
                    Protocols = new List<string> { "codex" },
                    ProgIds = new List<string>(),
                    Extensions = new List<string>(),
                    ShortcutPaths = new List<string>(),
                    CleanupPending = false
                });
                WriteProtocolRegistration(forgedRoot, null, null);
                CreateShellCleanupDeploymentJournal(
                    registrationRoot,
                    installId,
                    deploymentOperationId);
                CommitShellCleanupDeploymentJournal(registrationRoot);

                ShellIntegrationCleanupResult result =
                    ShellIntegration.CompletePreparedCleanup(
                        registrationRoot,
                        sourceRoot,
                        installId,
                        deploymentOperationId);
                Assert(result.Complete,
                    "伪造状态身份存在时其余受管清理没有安全完成。\n" +
                    string.Join(" | ", result.Warnings));
                AssertProtocolRegistrationExists(
                    "伪造 PhysicalInstallRoot 被错误加入清理别名并删除了外部协议。");
                Assert(!PortableStorage.IntegrationStateFileExists(),
                    "同安装 ID 但身份不匹配的损坏状态没有按摘要清理。");
                Assert(!ShellIntegrationCleanupJournal.Exists(),
                    "身份不匹配状态处理完成后仍残留 cleanup journal。");
            });
        });
    }

    private static void TestShellCleanupPreservesReusedInstallRoot()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            RunWithIsolatedShellRegistry("install-root-reused", delegate
            {
                string caseRoot = NewCaseRoot("shell-install-root-reused");
                string installRoot = Path.Combine(caseRoot, "Codex");
                string detachedRoot = Path.Combine(caseRoot, "Codex.detached");
                string firstInstallId = Guid.NewGuid().ToString("N");
                string secondInstallId = Guid.NewGuid().ToString("N");
                string deploymentOperationId = Guid.NewGuid().ToString("N");
                CreateMinimalCodex(
                    installRoot,
                    "1.0.0.0",
                    firstInstallId,
                    "install-a");
                string firstStableRoot =
                    NativeFileSystem.GetStablePathForExistingPath(installRoot);
                SaveOwnedShellIntegrationState(
                    installRoot,
                    firstStableRoot,
                    InstallOwnership.GetManagedDirectoryIdentity(installRoot),
                    firstInstallId);
                WriteProtocolRegistration(
                    installRoot,
                    firstStableRoot,
                    firstInstallId);
                CreateShellCleanupDeploymentJournal(
                    installRoot,
                    firstInstallId,
                    deploymentOperationId);

                ShellIntegration.PrepareCleanup(
                    installRoot,
                    installRoot,
                    firstInstallId,
                    deploymentOperationId);
                Directory.Move(installRoot, detachedRoot);

                CreateMinimalCodex(
                    installRoot,
                    "2.0.0.0",
                    secondInstallId,
                    "install-b");
                string secondStableRoot =
                    NativeFileSystem.GetStablePathForExistingPath(installRoot);
                SaveOwnedShellIntegrationState(
                    installRoot,
                    secondStableRoot,
                    InstallOwnership.GetManagedDirectoryIdentity(installRoot),
                    secondInstallId);
                WriteProtocolRegistration(
                    installRoot,
                    secondStableRoot,
                    secondInstallId);
                CommitShellCleanupDeploymentJournal(installRoot);

                ShellIntegrationCleanupResult result =
                    ShellIntegration.CompletePreparedCleanup(
                        installRoot,
                        detachedRoot,
                        firstInstallId,
                        deploymentOperationId);
                Assert(result.Complete,
                    "旧安装清理没有把已由新 InstallId 接管的资源视为安全完成。");
                using (RegistryKey protocol = Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\codex"))
                {
                    Assert(protocol != null,
                        "旧安装清理误删了新安装的 codex 协议注册。");
                    Assert(string.Equals(
                        protocol.GetValue("CodexPortableInstallId") as string,
                        secondInstallId,
                        StringComparison.OrdinalIgnoreCase),
                        "旧安装清理改写了新安装的注册表 InstallId。");
                }
                IntegrationState state = PortableStorage.LoadIntegrationState();
                Assert(state != null && string.Equals(
                    state.InstallId,
                    secondInstallId,
                    StringComparison.OrdinalIgnoreCase),
                    "旧安装清理误删或改写了新安装的 integration.json。");
                Assert(!ShellIntegrationCleanupJournal.Exists(),
                    "安装根复用保护完成后仍残留旧 cleanup journal。");
            });
        });
    }

    private static void TestImmediateCleanupArmedWriteFailureLeavesNoPreparedJournal()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            RunWithIsolatedShellRegistry("immediate-armed-write-failure", delegate
            {
                string caseRoot = NewCaseRoot("shell-immediate-armed-write-failure");
                string installRoot = Path.Combine(caseRoot, "Codex");
                string installId = Guid.NewGuid().ToString("N");
                CreateMinimalCodex(
                    installRoot,
                    "1.0.0.0",
                    installId,
                    "immediate-armed-write-failure");
                string stableRoot = NativeFileSystem.GetStablePathForExistingPath(installRoot);
                SaveOwnedShellIntegrationState(
                    installRoot,
                    stableRoot,
                    InstallOwnership.GetManagedDirectoryIdentity(installRoot),
                    installId);
                WriteProtocolRegistration(installRoot, stableRoot, installId);

                ShellIntegration.CleanupJournalWriteFailureInjectorForTest = phase =>
                    phase == ShellIntegrationCleanupPhase.Armed
                        ? new IOException("注入的即时 Armed 首写失败")
                        : null;
                ShellIntegrationCleanupResult first =
                    ShellIntegration.RemoveWithResult(installRoot);
                Assert(!first.Complete,
                    "即时 Armed 首写失败被错误报告为清理完成。");
                AssertProtocolRegistrationExists(
                    "即时 Armed 授权未持久化时仍删除了 codex 协议注册。");
                Assert(PortableStorage.IntegrationStateFileExists(),
                    "即时 Armed 授权未持久化时仍删除了 integration.json。");
                Assert(!ShellIntegrationCleanupJournal.Exists(),
                    "即时清理首写 Armed 失败后遗留了歧义 Prepared journal。");

                ShellIntegration.CleanupJournalWriteFailureInjectorForTest = null;
                ShellIntegrationCleanupResult second =
                    ShellIntegration.RemoveWithResult(installRoot);
                Assert(second.Complete,
                    "解除即时 Armed 首写故障后没有完成清理。");
                AssertProtocolRegistrationMissing(
                    "第二次即时清理后仍残留 codex 协议注册。");
                Assert(!PortableStorage.IntegrationStateFileExists(),
                    "第二次即时清理后仍残留 integration.json。");
            });
        });
    }

    private static void TestShellCleanupPreservesRegistryContentWithStaleMarker()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            RunWithIsolatedShellRegistry("stale-install-id-marker", delegate
            {
                string caseRoot = NewCaseRoot("shell-stale-install-id-marker");
                string installRoot = Path.Combine(caseRoot, "Codex");
                string otherRoot = Path.Combine(caseRoot, "Other");
                string installId = Guid.NewGuid().ToString("N");
                string deploymentOperationId = Guid.NewGuid().ToString("N");
                CreateMinimalCodex(
                    installRoot,
                    "1.0.0.0",
                    installId,
                    "stale-install-id-marker");
                Directory.CreateDirectory(otherRoot);
                string otherExecutable = Path.Combine(otherRoot, "Other.exe");
                File.WriteAllBytes(otherExecutable, new byte[] { 1, 2, 3 });
                string stableRoot =
                    NativeFileSystem.GetStablePathForExistingPath(installRoot);
                SaveOwnedShellIntegrationState(
                    installRoot,
                    stableRoot,
                    InstallOwnership.GetManagedDirectoryIdentity(installRoot),
                    installId);
                WriteProtocolRegistration(installRoot, stableRoot, installId);
                CreateShellCleanupDeploymentJournal(
                    installRoot,
                    installId,
                    deploymentOperationId);
                ShellIntegration.PrepareCleanup(
                    installRoot,
                    installRoot,
                    installId,
                    deploymentOperationId);

                using (RegistryKey command = Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\codex\shell\open\command",
                    true))
                {
                    command.SetValue(string.Empty, "\"" + otherExecutable + "\" \"%1\"");
                }
                CommitShellCleanupDeploymentJournal(installRoot);
                ShellIntegrationCleanupResult result =
                    ShellIntegration.CompletePreparedCleanup(
                        installRoot,
                        installRoot,
                        installId,
                        deploymentOperationId);

                Assert(result.Complete,
                    "外部内容接管注册项后其余 Shell 清理没有完成。");
                using (RegistryKey protocol = Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\codex"))
                using (RegistryKey command = protocol == null
                    ? null
                    : protocol.OpenSubKey(@"shell\open\command"))
                {
                    Assert(command != null && string.Equals(
                        command.GetValue(string.Empty) as string,
                        "\"" + otherExecutable + "\" \"%1\"",
                        StringComparison.Ordinal),
                        "Shell 清理仅凭陈旧 InstallId marker 删除了外部接管的协议。");
                }
                Assert(!ShellIntegrationCleanupJournal.Exists(),
                    "保留外部接管注册项后仍残留 cleanup journal。");
            });
        });
    }

    private static void TestPreparedJournalCannotBePromotedByImmediateCleanup()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            RunWithIsolatedShellRegistry("prepared-promotion-blocked", delegate
            {
                string caseRoot = NewCaseRoot("shell-prepared-promotion-blocked");
                string installRoot = Path.Combine(caseRoot, "Codex");
                string installId = Guid.NewGuid().ToString("N");
                string deploymentOperationId = Guid.NewGuid().ToString("N");
                CreateMinimalCodex(
                    installRoot,
                    "1.0.0.0",
                    installId,
                    "prepared-promotion-blocked");
                string stableRoot = NativeFileSystem.GetStablePathForExistingPath(installRoot);
                SaveOwnedShellIntegrationState(
                    installRoot,
                    stableRoot,
                    InstallOwnership.GetManagedDirectoryIdentity(installRoot),
                    installId);
                WriteProtocolRegistration(installRoot, stableRoot, installId);
                CreateShellCleanupDeploymentJournal(
                    installRoot,
                    installId,
                    deploymentOperationId);
                ShellIntegration.PrepareCleanup(
                    installRoot,
                    installRoot,
                    installId,
                    deploymentOperationId);

                ShellIntegrationCleanupResult blocked =
                    ShellIntegration.RemoveWithResult(installRoot);
                Assert(!blocked.Complete,
                    "存在 Prepared journal 时即时清理仍提前取得删除授权。");
                AssertProtocolRegistrationExists(
                    "Prepared journal 被即时清理错误提升后删除了协议。");
                Assert(ShellIntegrationCleanupJournal.Read().Phase ==
                    ShellIntegrationCleanupPhase.Prepared,
                    "被部署事务保护的当前 journal 不再保持 Prepared。");
                Exception wrongDeployment = CaptureFailure(delegate
                {
                    ShellIntegration.CompletePreparedCleanup(
                        installRoot,
                        installRoot,
                        installId,
                        Guid.NewGuid().ToString("N"));
                });
                Assert(wrongDeployment is InvalidDataException,
                    "Prepared journal 接受了无法与 deployment journal 交叉验证的操作 ID。");

                DeploymentJournal.Delete(installRoot);
                ShellIntegrationCleanupResult stillBlocked =
                    ShellIntegration.RemoveWithResult(installRoot);
                Assert(!stillBlocked.Complete,
                    "deployment journal 消失后 Prepared 被现场状态自动提升。");
                AssertProtocolRegistrationExists(
                    "deployment journal 消失后 Prepared 仍删除了协议。");
                Assert(ShellIntegrationCleanupJournal.Read().Phase ==
                    ShellIntegrationCleanupPhase.Prepared,
                    "即时清理尝试改变了 Prepared journal。");

                CreateShellCleanupDeploymentJournal(
                    installRoot,
                    installId,
                    deploymentOperationId);
                ShellIntegration.CancelPreparedCleanup(
                    installRoot,
                    installId,
                    deploymentOperationId);
                DeploymentJournal.Delete(installRoot);
            });
        });
    }

    private static void TestArmedJournalWriteFailureDeletesNothing()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            RunWithIsolatedShellRegistry("armed-write-failure", delegate
            {
                string caseRoot = NewCaseRoot("shell-armed-write-failure");
                string installRoot = Path.Combine(caseRoot, "Codex");
                string installId = Guid.NewGuid().ToString("N");
                string deploymentOperationId = Guid.NewGuid().ToString("N");
                CreateMinimalCodex(
                    installRoot,
                    "1.0.0.0",
                    installId,
                    "armed-write-failure");
                string stableRoot =
                    NativeFileSystem.GetStablePathForExistingPath(installRoot);
                SaveOwnedShellIntegrationState(
                    installRoot,
                    stableRoot,
                    InstallOwnership.GetManagedDirectoryIdentity(installRoot),
                    installId);
                WriteProtocolRegistration(installRoot, stableRoot, installId);
                CreateShellCleanupDeploymentJournal(
                    installRoot,
                    installId,
                    deploymentOperationId);
                ShellIntegration.PrepareCleanup(
                    installRoot,
                    installRoot,
                    installId,
                    deploymentOperationId);
                CommitShellCleanupDeploymentJournal(installRoot);

                ShellIntegration.CleanupJournalWriteFailureInjectorForTest = phase =>
                    phase == ShellIntegrationCleanupPhase.Armed
                        ? new IOException("注入的 Armed journal 写入失败")
                        : null;
                bool failed = false;
                try
                {
                    ShellIntegration.CompletePreparedCleanup(
                        installRoot,
                        installRoot,
                        installId,
                        deploymentOperationId);
                }
                catch (IOException)
                {
                    failed = true;
                }
                Assert(failed, "Armed journal 写入失败没有向调用方报告。");
                AssertProtocolRegistrationExists(
                    "Armed journal 未持久化时仍删除了 codex 协议注册。");
                Assert(PortableStorage.IntegrationStateFileExists(),
                    "Armed journal 未持久化时仍删除了 integration.json。");
                ShellIntegrationCleanupJournalRecord journal =
                    ShellIntegrationCleanupJournal.Read();
                Assert(journal != null &&
                    journal.Phase == ShellIntegrationCleanupPhase.Prepared,
                    "Armed journal 写入失败后磁盘阶段不再是 Prepared。");

                ShellIntegration.CleanupJournalWriteFailureInjectorForTest = null;
                Assert(ShellIntegration.CompletePreparedCleanup(
                    installRoot,
                    installRoot,
                    installId,
                    deploymentOperationId).Complete,
                    "解除 Armed 写入故障后没有完成已提交清理。");
            });
        });
    }

    private static void TestShellCleanupRejectsMismatchedDeploymentOperationId()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            RunWithIsolatedShellRegistry("deployment-operation-mismatch", delegate
            {
                string caseRoot = NewCaseRoot("shell-deployment-operation-mismatch");
                string installRoot = Path.Combine(caseRoot, "Codex");
                string installId = Guid.NewGuid().ToString("N");
                string expectedOperationId = Guid.NewGuid().ToString("N");
                string wrongOperationId = Guid.NewGuid().ToString("N");
                CreateMinimalCodex(
                    installRoot,
                    "1.0.0.0",
                    installId,
                    "deployment-operation-mismatch");
                string stableRoot = NativeFileSystem.GetStablePathForExistingPath(installRoot);
                SaveOwnedShellIntegrationState(
                    installRoot,
                    stableRoot,
                    InstallOwnership.GetManagedDirectoryIdentity(installRoot),
                    installId);
                WriteProtocolRegistration(installRoot, stableRoot, installId);
                CreateShellCleanupDeploymentJournal(
                    installRoot,
                    installId,
                    expectedOperationId);
                CommitShellCleanupDeploymentJournal(installRoot);
                Exception missingJournalFailure = CaptureFailure(delegate
                {
                    ShellIntegration.CompletePreparedCleanup(
                        installRoot,
                        installRoot,
                        installId,
                        wrongOperationId);
                });
                Assert(missingJournalFailure is InvalidDataException &&
                    !ShellIntegrationCleanupJournal.Exists(),
                    "journal 缺失时错误部署操作 ID 仍写入了 Prepared Shell journal。");
                DeploymentJournalRecord preparedDeployment =
                    DeploymentJournal.Read(installRoot);
                preparedDeployment.Phase = DeploymentTransactionPhase.UninstallPrepared;
                DeploymentJournal.Write(preparedDeployment);
                ShellIntegration.PrepareCleanup(
                    installRoot,
                    installRoot,
                    installId,
                    expectedOperationId);

                Exception completeFailure = CaptureFailure(delegate
                {
                    ShellIntegration.CompletePreparedCleanup(
                        installRoot,
                        installRoot,
                        installId,
                        wrongOperationId);
                });
                Exception cancelFailure = CaptureFailure(delegate
                {
                    ShellIntegration.CancelPreparedCleanup(
                        installRoot,
                        installId,
                        wrongOperationId);
                });
                Assert(completeFailure is InvalidDataException &&
                    cancelFailure is InvalidDataException,
                    "错误部署操作 ID 没有被 Complete/Cancel 同时拒绝。");
                AssertProtocolRegistrationExists(
                    "错误部署操作 ID 仍删除了 codex 协议注册。");
                Assert(PortableStorage.IntegrationStateFileExists(),
                    "错误部署操作 ID 仍删除了 integration.json。");
                Assert(ShellIntegrationCleanupJournal.Read().Phase ==
                    ShellIntegrationCleanupPhase.Prepared,
                    "错误部署操作 ID 改变了 Prepared 阶段。");

                CreateShellCleanupDeploymentJournal(
                    installRoot,
                    installId,
                    wrongOperationId);
                Exception actualCancelMismatch = CaptureFailure(delegate
                {
                    ShellIntegration.CancelPreparedCleanup(
                        installRoot,
                        installId,
                        expectedOperationId);
                });
                CommitShellCleanupDeploymentJournal(installRoot);
                Exception actualCompleteMismatch = CaptureFailure(delegate
                {
                    ShellIntegration.CompletePreparedCleanup(
                        installRoot,
                        installRoot,
                        installId,
                        expectedOperationId);
                });
                Assert(actualCancelMismatch is InvalidDataException &&
                    actualCompleteMismatch is InvalidDataException,
                    "Shell journal A 在实际 deployment journal 已变为 B 时仍被授权。");

                CreateShellCleanupDeploymentJournal(
                    installRoot,
                    installId,
                    expectedOperationId);
                CommitShellCleanupDeploymentJournal(installRoot);
                Assert(ShellIntegration.CompletePreparedCleanup(
                    installRoot,
                    installRoot,
                    installId,
                    expectedOperationId).Complete,
                    "使用正确部署操作 ID 后没有完成清理。");
            });
        });
    }

    private static void TestCompletedJournalWriteFailureRemainsRetryable()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            RunWithIsolatedShellRegistry("completed-write-failure", delegate
            {
                string caseRoot = NewCaseRoot("shell-completed-write-failure");
                string installRoot = Path.Combine(caseRoot, "Codex");
                string installId = Guid.NewGuid().ToString("N");
                string deploymentOperationId = Guid.NewGuid().ToString("N");
                CreateMinimalCodex(
                    installRoot,
                    "1.0.0.0",
                    installId,
                    "completed-write-failure");
                string stableRoot = NativeFileSystem.GetStablePathForExistingPath(installRoot);
                SaveOwnedShellIntegrationState(
                    installRoot,
                    stableRoot,
                    InstallOwnership.GetManagedDirectoryIdentity(installRoot),
                    installId);
                WriteProtocolRegistration(installRoot, stableRoot, installId);
                CreateShellCleanupDeploymentJournal(
                    installRoot,
                    installId,
                    deploymentOperationId);
                ShellIntegration.PrepareCleanup(
                    installRoot,
                    installRoot,
                    installId,
                    deploymentOperationId);
                CommitShellCleanupDeploymentJournal(installRoot);

                ShellIntegration.CleanupJournalWriteFailureInjectorForTest = phase =>
                    phase == ShellIntegrationCleanupPhase.Completed
                        ? new IOException("注入的 Completed journal 写入失败")
                        : null;
                ShellIntegrationCleanupResult first =
                    ShellIntegration.CompletePreparedCleanup(
                        installRoot,
                        installRoot,
                        installId,
                        deploymentOperationId);
                Assert(!first.Complete,
                    "Completed journal 写入失败被错误报告为完整成功。");
                AssertProtocolRegistrationMissing(
                    "Completed journal 写入失败前没有完成已授权资源清理。");
                Assert(!PortableStorage.IntegrationStateFileExists(),
                    "Completed journal 写入失败前没有清理 integration.json。");
                ShellIntegrationCleanupJournalRecord pending =
                    ShellIntegrationCleanupJournal.Read();
                Assert(pending != null &&
                    pending.Phase == ShellIntegrationCleanupPhase.Armed,
                    "Completed 写入失败后磁盘 journal 没有保持 Armed 以便幂等重试。");

                ShellIntegration.CleanupJournalWriteFailureInjectorForTest = null;
                Assert(ShellIntegration.RecoverPendingCleanup().Complete,
                    "解除 Completed 写入故障后没有幂等完成清理。");
                Assert(!ShellIntegrationCleanupJournal.Exists(),
                    "Completed 写入故障恢复后仍残留 cleanup journal。");
            });
        });
    }

    private static void TestCompletedJournalDeleteFailureOnlyRetriesJournal()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            RunWithIsolatedShellRegistry("completed-delete-failure", delegate
            {
                string caseRoot = NewCaseRoot("shell-completed-delete-failure");
                string installRoot = Path.Combine(caseRoot, "Codex");
                string firstInstallId = Guid.NewGuid().ToString("N");
                string secondInstallId = Guid.NewGuid().ToString("N");
                string deploymentOperationId = Guid.NewGuid().ToString("N");
                CreateMinimalCodex(
                    installRoot,
                    "1.0.0.0",
                    firstInstallId,
                    "completed-delete-failure");
                string stableRoot = NativeFileSystem.GetStablePathForExistingPath(installRoot);
                string rootIdentity = InstallOwnership.GetManagedDirectoryIdentity(installRoot);
                SaveOwnedShellIntegrationState(
                    installRoot,
                    stableRoot,
                    rootIdentity,
                    firstInstallId);
                WriteProtocolRegistration(installRoot, stableRoot, firstInstallId);
                CreateShellCleanupDeploymentJournal(
                    installRoot,
                    firstInstallId,
                    deploymentOperationId);
                ShellIntegration.PrepareCleanup(
                    installRoot,
                    installRoot,
                    firstInstallId,
                    deploymentOperationId);
                CommitShellCleanupDeploymentJournal(installRoot);

                ShellIntegration.CleanupFailureInjectorForTest = label =>
                    string.Equals(label, "integration-cleanup.json", StringComparison.Ordinal)
                        ? new IOException("注入的 Completed journal 删除失败")
                        : null;
                ShellIntegrationCleanupResult first =
                    ShellIntegration.CompletePreparedCleanup(
                        installRoot,
                        installRoot,
                        firstInstallId,
                        deploymentOperationId);
                Assert(!first.Complete,
                    "Completed journal 删除失败被错误报告为完整成功。");
                ShellIntegrationCleanupJournalRecord completed =
                    ShellIntegrationCleanupJournal.Read();
                Assert(completed != null &&
                    completed.Phase == ShellIntegrationCleanupPhase.Completed,
                    "最终 journal 删除失败后没有持久化 Completed。");

                SaveOwnedShellIntegrationState(
                    installRoot,
                    stableRoot,
                    rootIdentity,
                    secondInstallId);
                WriteProtocolRegistration(installRoot, stableRoot, secondInstallId);
                ShellIntegration.CleanupFailureInjectorForTest = null;
                Assert(ShellIntegration.RecoverPendingCleanup().Complete,
                    "解除最终 journal 删除故障后没有完成元数据清理。");
                using (RegistryKey protocol = Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\codex"))
                {
                    Assert(protocol != null && string.Equals(
                        protocol.GetValue("CodexPortableInstallId") as string,
                        secondInstallId,
                        StringComparison.OrdinalIgnoreCase),
                        "Completed 恢复重放了旧清理并删除新协议注册。");
                }
                IntegrationState state = PortableStorage.LoadIntegrationState();
                Assert(state != null && string.Equals(
                    state.InstallId,
                    secondInstallId,
                    StringComparison.OrdinalIgnoreCase),
                    "Completed 恢复重放了旧清理并删除新 integration.json。");
                Assert(!ShellIntegrationCleanupJournal.Exists(),
                    "Completed 恢复后仍残留 cleanup journal。");
            });
        });
    }

    private static void TestDamagedShellCleanupJournalFailsClosed()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            RunWithIsolatedShellRegistry("damaged-cleanup-journal", delegate
            {
                string caseRoot = NewCaseRoot("shell-damaged-cleanup-journal");
                string installRoot = Path.Combine(caseRoot, "Codex");
                string installId = Guid.NewGuid().ToString("N");
                string deploymentOperationId = Guid.NewGuid().ToString("N");
                CreateMinimalCodex(
                    installRoot,
                    "1.0.0.0",
                    installId,
                    "damaged-cleanup-journal");
                string stableRoot = NativeFileSystem.GetStablePathForExistingPath(installRoot);
                SaveOwnedShellIntegrationState(
                    installRoot,
                    stableRoot,
                    InstallOwnership.GetManagedDirectoryIdentity(installRoot),
                    installId);
                WriteProtocolRegistration(installRoot, stableRoot, installId);
                CreateShellCleanupDeploymentJournal(
                    installRoot,
                    installId,
                    deploymentOperationId);
                ShellIntegration.PrepareCleanup(
                    installRoot,
                    installRoot,
                    installId,
                    deploymentOperationId);

                string legal = File.ReadAllText(
                    ShellIntegrationCleanupJournal.FilePath,
                    Encoding.UTF8);
                string invalidPhase = legal.Replace(
                    "\"Phase\":1",
                    "\"Phase\":999");
                string invalidPurpose = legal.Replace(
                    "\"Purpose\":2",
                    "\"Purpose\":1");
                Assert(!string.Equals(legal, invalidPhase, StringComparison.Ordinal) &&
                    !string.Equals(legal, invalidPurpose, StringComparison.Ordinal),
                    "测试未能构造非法 cleanup journal payload。");
                foreach (string payload in new[]
                {
                    "{invalid-json",
                    invalidPhase,
                    invalidPurpose
                })
                {
                    File.WriteAllText(
                        ShellIntegrationCleanupJournal.FilePath,
                        payload,
                        new UTF8Encoding(false));
                    Exception failure = CaptureFailure(delegate
                    {
                        ShellIntegration.RecoverPendingCleanup();
                    });
                    Assert(failure is InvalidDataException,
                        "损坏 cleanup journal 没有 fail-closed：" + failure);
                    AssertProtocolRegistrationExists(
                        "损坏 cleanup journal 仍触发了协议删除。");
                    Assert(PortableStorage.IntegrationStateFileExists(),
                        "损坏 cleanup journal 仍触发了 integration.json 删除。");
                    Assert(File.ReadAllText(
                        ShellIntegrationCleanupJournal.FilePath,
                        Encoding.UTF8) == payload,
                        "损坏 cleanup journal 被自动改写或删除。");
                }

                File.WriteAllText(
                    ShellIntegrationCleanupJournal.FilePath,
                    legal,
                    new UTF8Encoding(false));
                ShellIntegration.CancelPreparedCleanup(
                    installRoot,
                    installId,
                    deploymentOperationId);
            });
        });
    }

    private static void TestShellCleanupPreservesReplacedShortcut()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            RunWithIsolatedShellRegistry("replaced-shortcut", delegate
            {
                string caseRoot = NewCaseRoot("shell-replaced-shortcut");
                string installRoot = Path.Combine(caseRoot, "Codex");
                string otherRoot = Path.Combine(caseRoot, "Other");
                string shortcutRoot = Path.Combine(caseRoot, "Shortcuts");
                string shortcutPath = Path.Combine(shortcutRoot, "Codex.lnk");
                string installId = Guid.NewGuid().ToString("N");
                string deploymentOperationId = Guid.NewGuid().ToString("N");
                CreateMinimalCodex(
                    installRoot,
                    "1.0.0.0",
                    installId,
                    "replaced-shortcut");
                Directory.CreateDirectory(otherRoot);
                Directory.CreateDirectory(shortcutRoot);
                ShellIntegration.ShortcutRootsProviderForTest = () =>
                    Tuple.Create(shortcutRoot, shortcutRoot);
                string executablePath = Path.Combine(installRoot, "app", "Codex.exe");
                string otherExecutable = Path.Combine(otherRoot, "Other.exe");
                File.WriteAllBytes(otherExecutable, new byte[] { 7, 8, 9 });
                ShortcutHelper.Create(
                    shortcutPath,
                    executablePath,
                    string.Empty,
                    Path.GetDirectoryName(executablePath),
                    executablePath,
                    "Codex",
                    ShellIntegration.AppUserModelId);
                string stableRoot = NativeFileSystem.GetStablePathForExistingPath(installRoot);
                SaveOwnedShellIntegrationState(
                    installRoot,
                    stableRoot,
                    InstallOwnership.GetManagedDirectoryIdentity(installRoot),
                    installId,
                    new[] { shortcutPath });
                WriteProtocolRegistration(installRoot, stableRoot, installId);
                CreateShellCleanupDeploymentJournal(
                    installRoot,
                    installId,
                    deploymentOperationId);
                ShellIntegration.PrepareCleanup(
                    installRoot,
                    installRoot,
                    installId,
                    deploymentOperationId);
                ShellIntegrationCleanupJournalRecord journal =
                    ShellIntegrationCleanupJournal.Read();
                    Assert(journal != null && journal.Shortcuts.Count == 1,
                        "准备清理时没有为受管快捷方式保存 receipt。");

                CommitShellCleanupDeploymentJournal(installRoot);
                ShellIntegration.ShortcutFinalDeleteObserverForTest = path =>
                {
                    if (PathsEqual(path, shortcutPath))
                    {
                        ShellIntegration.ShortcutFinalDeleteObserverForTest = null;
                        ShortcutHelper.Create(
                            shortcutPath,
                            otherExecutable,
                            string.Empty,
                            otherRoot,
                            otherExecutable,
                            "其他程序",
                            null);
                    }
                };
                ShellIntegrationCleanupResult result =
                    ShellIntegration.CompletePreparedCleanup(
                        installRoot,
                        installRoot,
                        installId,
                        deploymentOperationId);
                Assert(result.Complete,
                    "快捷方式被替换后其余 Shell 清理没有安全完成。");
                Assert(File.Exists(shortcutPath),
                    "旧快捷方式 receipt 误删了原位替换的新文件。");
                string target;
                string error;
                Assert(ShortcutHelper.TryGetTarget(shortcutPath, out target, out error) &&
                    PathsEqual(target, otherExecutable),
                    "原位替换的新快捷方式目标没有被保留：" + error);
                Assert(!ShellIntegrationCleanupJournal.Exists(),
                    "保留替换快捷方式后仍残留 cleanup journal。");
            });
        });
    }

    private static void TestPendingShellCleanupRootIsResolvedAndVisible()
    {
        WithPreservedShellCleanupStorage(delegate
        {
            RunWithIsolatedShellRegistry("pending-shell-root-status", delegate
            {
                string caseRoot = NewCaseRoot("pending-shell-root-status");
                string installRoot = Path.Combine(caseRoot, "Codex");
                string detachedRoot = Path.Combine(caseRoot, "Codex.detached");
                string installId = Guid.NewGuid().ToString("N");
                string deploymentOperationId = Guid.NewGuid().ToString("N");
                CreateMinimalCodex(
                    installRoot,
                    "1.0.0.0",
                    installId,
                    "pending-shell-root-status");
                string stableRoot = NativeFileSystem.GetStablePathForExistingPath(installRoot);
                SaveOwnedShellIntegrationState(
                    installRoot,
                    stableRoot,
                    InstallOwnership.GetManagedDirectoryIdentity(installRoot),
                    installId);
                WriteProtocolRegistration(installRoot, stableRoot, installId);
                CreateShellCleanupDeploymentJournal(
                    installRoot,
                    installId,
                    deploymentOperationId);
                ShellIntegration.PrepareCleanup(
                    installRoot,
                    installRoot,
                    installId,
                    deploymentOperationId);
                Directory.Move(installRoot, detachedRoot);
                CommitShellCleanupDeploymentJournal(installRoot);

                ShellIntegration.CleanupFailureInjectorForTest = label =>
                    string.Equals(label, "协议 codex", StringComparison.Ordinal)
                        ? new IOException("注入的持续协议清理失败")
                        : null;
                ShellIntegrationCleanupResult cleanup =
                    ShellIntegration.CompletePreparedCleanup(
                        installRoot,
                        detachedRoot,
                        installId,
                        deploymentOperationId);
                Assert(!cleanup.Complete, "注入故障后 Shell 清理错误报告为完成。");

                string resolved = InstallLocationResolver.ResolveInstallRoot(string.Empty, () => null);
                Assert(PathsEqual(resolved, installRoot),
                    "仅剩 Shell cleanup journal 时启动路径没有恢复原安装根。");
                using (CodexPortableService service = CreateService(new List<string>()))
                {
                    PortableLocalStatus status = service.GetLocalStatus(installRoot);
                    Assert(status.HasInstallRoot &&
                        status.PortableVersion == null &&
                        status.ShellIntegrationCleanupPending &&
                        string.IsNullOrWhiteSpace(status.Error),
                        "Shell 清理待办没有跨状态刷新持续显示。");
                }

                ShellIntegration.CleanupFailureInjectorForTest = null;
                Assert(ShellIntegration.RecoverPendingCleanup().Complete,
                    "解除持续故障后没有完成 Shell 清理恢复。");
            });
        });
    }

    private static void CreateShellCleanupDeploymentJournal(
        string installRoot,
        string installId,
        string operationId)
    {
        DeploymentJournal.Write(new DeploymentJournalRecord
        {
            OperationId = operationId,
            Operation = DeploymentOperationKind.Uninstall,
            Phase = DeploymentTransactionPhase.UninstallPrepared,
            InstallRoot = installRoot,
            InstallId = installId,
            HadCurrent = false,
            HadPrevious = false
        });
    }

    private static void CommitShellCleanupDeploymentJournal(string installRoot)
    {
        DeploymentJournalRecord journal = DeploymentJournal.Read(installRoot);
        Assert(journal != null, "Shell 清理测试缺少 deployment journal。");
        DeploymentJournalRecord candidate = DeploymentJournal.Clone(journal);
        candidate.Phase = DeploymentTransactionPhase.UninstallPayloadDetached;
        DeploymentJournal.Write(candidate);
    }

    private static void SaveOwnedShellIntegrationState(
        string recordedRoot,
        string physicalRoot,
        string rootIdentity,
        string installId,
        IEnumerable<string> shortcutPaths = null)
    {
        PortableStorage.SaveIntegrationState(new IntegrationState
        {
            InstallId = installId,
            InstallRoot = recordedRoot,
            PhysicalInstallRoot = physicalRoot,
            RootIdentity = rootIdentity,
            ExecutablePath = Path.Combine(recordedRoot, "app", "Codex.exe"),
            AppUserModelId = ShellIntegration.AppUserModelId,
            Protocols = new List<string> { "codex" },
            ProgIds = new List<string>(),
            Extensions = new List<string>(),
            ShortcutPaths = shortcutPaths == null
                ? new List<string>()
                : new List<string>(shortcutPaths),
            CleanupPending = false
        });
    }

    private static void WriteProtocolRegistration(
        string recordedRoot,
        string physicalRoot,
        string installId)
    {
        using (RegistryKey protocol = Registry.CurrentUser.CreateSubKey(
            @"Software\Classes\codex"))
        {
            protocol.SetValue("CodexPortableInstallRoot", recordedRoot);
            if (!string.IsNullOrWhiteSpace(physicalRoot))
            {
                protocol.SetValue("CodexPortablePhysicalInstallRoot", physicalRoot);
            }
            if (!string.IsNullOrWhiteSpace(installId))
            {
                protocol.SetValue("CodexPortableInstallId", installId);
            }
            using (RegistryKey command = protocol.CreateSubKey(@"shell\open\command"))
            {
                command.SetValue(
                    string.Empty,
                    "\"" + Path.Combine(recordedRoot, "app", "Codex.exe") + "\" \"%1\"");
            }
        }
    }

    private static void AssertProtocolRegistrationExists(string message)
    {
        using (RegistryKey protocol = Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\codex"))
        {
            Assert(protocol != null, message);
        }
    }

    private static void AssertProtocolRegistrationMissing(string message)
    {
        using (RegistryKey protocol = Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\codex"))
        {
            Assert(protocol == null, message);
        }
    }

    private static void WithPreservedShellCleanupStorage(Action action)
    {
        string statePath = PortableStorage.IntegrationStateFilePath;
        string journalPath = ShellIntegrationCleanupJournal.FilePath;
        byte[] previousState = File.Exists(statePath) ? File.ReadAllBytes(statePath) : null;
        byte[] previousJournal = File.Exists(journalPath) ? File.ReadAllBytes(journalPath) : null;
        try
        {
            RestoreOptionalFile(statePath, null);
            RestoreOptionalFile(journalPath, null);
            action();
        }
        finally
        {
            ShellIntegration.CleanupFailureInjectorForTest = null;
            ShellIntegration.CleanupJournalWriteFailureInjectorForTest = null;
            ShellIntegration.ShortcutRootsProviderForTest = null;
            ShellIntegration.ShortcutFinalDeleteObserverForTest = null;
            try
            {
                RestoreOptionalFile(statePath, previousState);
            }
            finally
            {
                RestoreOptionalFile(journalPath, previousJournal);
            }
        }
    }

    private static void RunWithIsolatedShellRegistry(string name, Action action)
    {
        string isolatedRegistryPath =
            @"Software\CodexPortableManagerRegression\" + name + "-" +
            Guid.NewGuid().ToString("N");
        RegistryKey overrideRoot = Registry.CurrentUser.CreateSubKey(isolatedRegistryPath);
        if (overrideRoot == null)
        {
            throw new InvalidOperationException("无法创建隔离注册表测试根。");
        }

        bool overridden = false;
        try
        {
            int overrideResult = RegOverridePredefKey(HkeyCurrentUser, overrideRoot.Handle);
            if (overrideResult != 0)
            {
                throw new InvalidOperationException(
                    "无法重定向 HKCU，Win32=" +
                    overrideResult.ToString(CultureInfo.InvariantCulture));
            }
            overridden = true;
            action();
        }
        finally
        {
            ShellIntegration.CleanupFailureInjectorForTest = null;
            ShellIntegration.CleanupJournalWriteFailureInjectorForTest = null;
            ShellIntegration.ShortcutRootsProviderForTest = null;
            ShellIntegration.ShortcutFinalDeleteObserverForTest = null;
            if (overridden)
            {
                RegOverridePredefKeyRaw(HkeyCurrentUser, IntPtr.Zero);
            }
            overrideRoot.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(isolatedRegistryPath, false);
        }
    }

    private static string CreateSubstDrive(string targetPath)
    {
        string[] existingDrives = Directory.GetLogicalDrives();
        for (char letter = 'Z'; letter >= 'S'; letter--)
        {
            if (letter == 'R')
            {
                continue;
            }
            string driveName = letter + ":";
            bool inUse = false;
            foreach (string existingDrive in existingDrives)
            {
                if (string.Equals(
                    existingDrive.TrimEnd(Path.DirectorySeparatorChar),
                    driveName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    inUse = true;
                    break;
                }
            }
            if (inUse)
            {
                continue;
            }

            RunSubst(driveName, targetPath, false);
            Assert(Directory.Exists(driveName + @"\"),
                "SUBST 映射创建后驱动器不可访问：" + driveName);
            return driveName;
        }
        throw new InvalidOperationException("没有可用于 Shell 集成回归的空闲 SUBST 驱动器号。");
    }

    private static void RemoveSubstDrive(string driveName)
    {
        RunSubst(driveName, null, true);
        Assert(!Directory.Exists(driveName + @"\"),
            "SUBST 映射删除后驱动器仍可访问：" + driveName);
    }

    private static void RunSubst(string driveName, string targetPath, bool remove)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "subst.exe"),
            Arguments = remove
                ? driveName + " /D"
                : driveName + " " + QuoteArgument(Path.GetFullPath(targetPath)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using (Process process = Process.Start(startInfo))
        {
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new IOException(
                    "SUBST 操作失败，退出码 " + process.ExitCode + "：" + output + error);
            }
        }
    }

    [DllImport("advapi32.dll")]
    private static extern int RegOverridePredefKey(
        IntPtr hKey,
        SafeRegistryHandle hNewKey);

    [DllImport("advapi32.dll", EntryPoint = "RegOverridePredefKey")]
    private static extern int RegOverridePredefKeyRaw(
        IntPtr hKey,
        IntPtr hNewKey);
}
}
