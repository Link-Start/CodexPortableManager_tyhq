using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPortableManager
{
internal static partial class RegressionTestRunner
{
    private static void TestPortableStorageMigration()
    {
        string caseRoot = NewCaseRoot("portable-storage-migration");
        // 旧 LocalAppData 必须与管理器 BaseDirectory 互不包含，否则应由生产代码拒绝迁移。
        string fakeProfileContainer = Path.Combine(
            Path.GetTempPath(),
            "CodexPortableManager-regression-profile-" + Guid.NewGuid().ToString("N"));
        string fakeProfile = Path.Combine(fakeProfileContainer, "profile");
        string fakeLocalAppData = Path.Combine(fakeProfile, "AppData", "Local");
        string legacyRoot = Path.Combine(fakeLocalAppData, "CodexPortableManager");
        string legacyCache = Path.Combine(legacyRoot, "cache");
        string destinationData = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        string destinationCache = Path.Combine(destinationData, "cache");
        Directory.CreateDirectory(fakeLocalAppData);
        if (Directory.Exists(destinationData))
        {
            Directory.Delete(destinationData, true);
        }

        string previousLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        string previousUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", fakeLocalAppData);
            Environment.SetEnvironmentVariable("USERPROFILE", fakeProfile);
            string resolvedLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            Assert(string.Equals(Path.GetFullPath(resolvedLocalAppData), Path.GetFullPath(fakeLocalAppData), StringComparison.OrdinalIgnoreCase),
                "测试进程无法把 LocalApplicationData 隔离到 %TEMP%，已停止迁移测试：" + resolvedLocalAppData);

            Directory.CreateDirectory(legacyCache);
            string nonCacheSentinel = Path.Combine(legacyRoot, "keep-user-file.txt");
            string sourcePath = Path.Combine(legacyCache, "package-success.msix");
            byte[] sourceContent = Encoding.UTF8.GetBytes("verified-cache-content");
            File.WriteAllText(nonCacheSentinel, "非缓存文件必须保留", Encoding.UTF8);
            File.WriteAllBytes(sourcePath, sourceContent);

            RunLegacyMigration();

            string destinationPath = Path.Combine(destinationCache, Path.GetFileName(sourcePath));
            Assert(!File.Exists(sourcePath), "校验成功的旧缓存源文件没有删除。");
            Assert(File.Exists(destinationPath), "校验成功的缓存文件没有发布到新目录。");
            Assert(BytesEqual(File.ReadAllBytes(destinationPath), sourceContent), "迁移后的缓存文件内容不一致。");
            Assert(File.Exists(nonCacheSentinel), "旧缓存根目录中的非缓存文件被错误删除。");
            Assert(Directory.Exists(legacyRoot), "包含非缓存文件的旧根目录不应被整体删除。");

            Directory.CreateDirectory(legacyCache);
            string conflictSource = Path.Combine(legacyCache, "package-conflict.msix");
            string conflictDestination = Path.Combine(destinationCache, "package-conflict.msix");
            byte[] sourceConflictContent = Encoding.UTF8.GetBytes("source-version");
            byte[] destinationConflictContent = Encoding.UTF8.GetBytes("destination-version");
            File.WriteAllBytes(conflictSource, sourceConflictContent);
            File.WriteAllBytes(conflictDestination, destinationConflictContent);

            Exception conflict = null;
            try
            {
                RunLegacyMigration();
            }
            catch (Exception exception)
            {
                conflict = Unwrap(exception);
            }

            Assert(conflict is IOException, "同名不同内容缓存应拒绝迁移，实际异常：" + (conflict == null ? "无" : conflict.ToString()));
            Assert(File.Exists(conflictSource), "冲突时旧缓存源文件被错误删除。");
            Assert(File.Exists(conflictDestination), "冲突时新缓存目标文件被错误删除。");
            Assert(BytesEqual(File.ReadAllBytes(conflictSource), sourceConflictContent), "冲突时旧缓存源内容被修改。");
            Assert(BytesEqual(File.ReadAllBytes(conflictDestination), destinationConflictContent), "冲突时新缓存目标内容被覆盖。");
            Assert(File.Exists(nonCacheSentinel), "冲突处理错误删除了旧根目录中的非缓存文件。");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocalAppData);
            Environment.SetEnvironmentVariable("USERPROFILE", previousUserProfile);
            if (Directory.Exists(fakeProfileContainer))
            {
                Directory.Delete(fakeProfileContainer, true);
            }
        }
    }

    private static void TestFatalExceptionLogging()
    {
        string caseRoot = NewCaseRoot("fatal-exception-logging");
        string logsRoot = Path.Combine(caseRoot, "logs");
        InvalidOperationException expected = new InvalidOperationException("fatal-log-sentinel");
        string logPath = PortableStorage.RecordFatalException("回归测试", expected, logsRoot);

        Assert(!string.IsNullOrWhiteSpace(logPath), "致命异常日志路径为空。");
        Assert(File.Exists(logPath), "致命异常日志未写入磁盘。");
        Assert(
            string.Equals(Path.GetDirectoryName(logPath), Path.GetFullPath(logsRoot), StringComparison.OrdinalIgnoreCase),
            "致命异常日志写入了非预期目录。");
        string content = File.ReadAllText(logPath, Encoding.UTF8);
        Assert(content.Contains("来源：回归测试"), "致命异常日志缺少来源。");
        Assert(content.Contains(typeof(InvalidOperationException).FullName), "致命异常日志缺少异常类型。");
        Assert(content.Contains("fatal-log-sentinel"), "致命异常日志缺少异常消息。");
    }

    private static void TestStartupMaintenanceRemainsBestEffort()
    {
        List<string> events = new List<string>();
        List<string> logs = new List<string>();

        CodexPortableService.RunStartupMaintenanceBestEffort(
            delegate
            {
                events.Add("shell");
                throw new IOException("测试 Shell 恢复失败");
            },
            delegate
            {
                events.Add("storage");
                throw new IOException("测试存储维护失败");
            },
            logs.Add);

        Assert(events.SequenceEqual(new[] { "shell", "storage" }),
            "Shell 恢复失败后没有继续执行存储维护。");
        Assert(logs.Count == 2 &&
            logs[0].IndexOf("后续操作重试", StringComparison.Ordinal) >= 0 &&
            logs[1].IndexOf("维护管理器存储失败", StringComparison.Ordinal) >= 0,
            "启动维护失败没有分别降级并记录。");
    }

    private static void TestStorageMaintenancePolicy()
    {
        string caseRoot = NewCaseRoot("storage-maintenance-policy");
        string cacheRoot = Path.Combine(caseRoot, "cache");
        string logsRoot = Path.Combine(caseRoot, "logs");
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(logsRoot);
        DateTime utcNow = new DateTime(2026, 7, 11, 4, 0, 0, DateTimeKind.Utc);

        string package1 = CreateTimedFile(cacheRoot, "OpenAI.Codex_1.0.0.0_x64.msix", "package-1", utcNow.AddHours(-1));
        string package2 = CreateTimedFile(cacheRoot, "OpenAI.Codex_2.0.0.0_x64.msix", "package-2", utcNow.AddHours(-4));
        string package3 = CreateTimedFile(cacheRoot, "OpenAI.Codex_3.0.0.0_x64.msix", "package-3", utcNow.AddHours(-3));
        string package4 = CreateTimedFile(cacheRoot, "OpenAI.Codex_4.0.0.0_x64.msix", "package-4", utcNow.AddHours(-2));
        string armPackage1 = CreateTimedFile(cacheRoot, "OpenAI.Codex_1.0.0.0_arm64.msix", "arm-package-1", utcNow.AddHours(-1));
        string armPackage2 = CreateTimedFile(cacheRoot, "OpenAI.Codex_2.0.0.0_arm64.msix", "arm-package-2", utcNow.AddHours(-3));
        string armPackage3 = CreateTimedFile(cacheRoot, "OpenAI.Codex_3.0.0.0_arm64.msix", "arm-package-3", utcNow.AddHours(-2));
        string unknownCache = CreateTimedFile(cacheRoot, "user-cache.bin", "unknown", utcNow.AddYears(-1));
        string staleDownload = CreateTimedFile(
            cacheRoot,
            "OpenAI.Codex_5.0.0.0_x64.msix.download-" + Guid.NewGuid().ToString("N") + ".msix",
            "stale-download",
            utcNow.AddDays(-2));
        string recentDownload = CreateTimedFile(
            cacheRoot,
            "OpenAI.Codex_5.0.0.0_x64.msix.download-" + Guid.NewGuid().ToString("N") + ".msix",
            "recent-download",
            utcNow.AddHours(-2));
        string staleLegacyMaterialize = CreateTimedFile(
            cacheRoot,
            "OpenAI.Codex_5.0.0.0_x64.msix.materialize-" + Guid.NewGuid().ToString("N") + ".msix",
            "stale-legacy-materialize",
            utcNow.AddDays(-2));
        string staleMaterialize = CreateTimedFile(
            cacheRoot,
            ".materialize-" + Guid.NewGuid().ToString("N") + ".msix",
            "stale-materialize",
            utcNow.AddDays(-2));
        string recentMaterialize = CreateTimedFile(
            cacheRoot,
            ".materialize-" + Guid.NewGuid().ToString("N") + ".msix",
            "recent-materialize",
            utcNow.AddHours(-2));

        string invalidNewest = CreateTimedFile(
            cacheRoot,
            "OpenAI.Codex_4.0.0.0_x64.msix.invalid-" + Guid.NewGuid().ToString("N"),
            "invalid-newest",
            utcNow.AddDays(-1));
        string invalidSecond = CreateTimedFile(
            cacheRoot,
            "OpenAI.Codex_3.0.0.0_x64.msix.invalid-" + Guid.NewGuid().ToString("N"),
            "invalid-second",
            utcNow.AddDays(-2));
        string invalidExpired = CreateTimedFile(
            cacheRoot,
            "OpenAI.Codex_2.0.0.0_x64.msix.invalid-" + Guid.NewGuid().ToString("N"),
            "invalid-expired",
            utcNow.AddDays(-10));

        string oldLog = CreateTimedFile(logsRoot, "session-20260501-120000.log", "old-log!", utcNow.AddDays(-40));
        string recentOldest = CreateTimedFile(logsRoot, "session-20260708-120000.log", "12345678", utcNow.AddDays(-3));
        string recentMiddle = CreateTimedFile(logsRoot, "session-20260709-120000.log", "abcdefgh", utcNow.AddDays(-2));
        string recentNewest = CreateTimedFile(
            logsRoot,
            "storage-load-error-20260711-123456789-" + Guid.NewGuid().ToString("N") + ".log",
            "ABCDEFGH",
            utcNow.AddDays(-1));
        string expiredFatalLog = CreateTimedFile(
            logsRoot,
            "fatal-error-20260501-120000000-" + Guid.NewGuid().ToString("N") + ".log",
            "fatal-log",
            utcNow.AddDays(-40));
        string unknownLog = CreateTimedFile(logsRoot, "keep-user.log", "unknown-log", utcNow.AddYears(-1));

        WithIsolatedLocalAppData("storage-maintenance-profile", delegate
        {
            StorageMaintenanceResult result = StorageMaintenance.Run(
                cacheRoot,
                logsRoot,
                utcNow,
                2,
                1,
                TimeSpan.FromDays(7),
                TimeSpan.FromDays(30),
                8L);

            Assert(!File.Exists(package1) && !File.Exists(package2), "较旧的正式缓存包没有按版本清理。");
            Assert(File.Exists(package3) && File.Exists(package4), "最高两个正式缓存包未被保留。");
            Assert(!File.Exists(armPackage1), "arm64 较旧正式缓存包没有被清理。");
            Assert(File.Exists(armPackage2) && File.Exists(armPackage3), "arm64 最高两个正式缓存包未独立保留。");
            Assert(File.Exists(unknownCache), "未知缓存文件被错误清理。");
            Assert(!File.Exists(staleDownload), "过期下载临时文件没有清理。");
            Assert(File.Exists(recentDownload), "仍在保留期内的下载临时文件被错误清理。");
            Assert(!File.Exists(staleLegacyMaterialize) && !File.Exists(staleMaterialize),
                "新旧格式的过期增量物化临时文件没有全部清理。");
            Assert(File.Exists(recentMaterialize), "仍在保留期内的增量物化临时文件被错误清理。");
            Assert(File.Exists(invalidNewest), "invalid 保留策略没有保留最新一份。");
            Assert(!File.Exists(invalidSecond) && !File.Exists(invalidExpired), "invalid 数量或期限策略没有生效。");

            Assert(!File.Exists(oldLog), "超过日志保留期的受管日志没有清理。");
            Assert(!File.Exists(expiredFatalLog), "超过日志保留期的致命异常日志没有清理。");
            Assert(!File.Exists(recentOldest) && !File.Exists(recentMiddle), "日志总量上限没有优先清理较旧日志。");
            Assert(File.Exists(recentNewest), "日志总量策略错误删除了最新受管日志。");
            Assert(File.Exists(unknownLog), "未知日志文件被错误清理。");

            Assert(result.DeletedPackageFiles == 3, "正式包删除计数不正确。");
            Assert(result.DeletedInvalidFiles == 2, "invalid 删除计数不正确。");
            Assert(result.DeletedDownloadFiles == 3, "下载与物化临时文件删除计数不正确。");
            Assert(result.DeletedLogFiles == 4, "日志删除计数不正确。");
            Assert(result.ReclaimedBytes > 0, "回收字节数没有累计。");
        });
    }

    private static void TestOwnedWorkDirectoryMaintenance()
    {
        string caseRoot = NewCaseRoot("owned-work-maintenance");
        string parentRoot = Path.Combine(caseRoot, "work-parent");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string otherInstallRoot = Path.Combine(caseRoot, "OtherCodexDesktop");
        Directory.CreateDirectory(parentRoot);

        string expiredOwned = CreateWorkDirectory(parentRoot, installRoot, "expired");
        string freshOwned = CreateWorkDirectory(parentRoot, installRoot, "fresh");
        string wrongOwnerRoot = CreateWorkDirectory(parentRoot, otherInstallRoot, "wrong-root");
        string unknown = Path.Combine(parentRoot, ".cpm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "sentinel.txt"), "unknown", Encoding.UTF8);
        string invalidName = Path.Combine(parentRoot, ".cpm-not-a-guid");
        Directory.CreateDirectory(invalidName);
        File.WriteAllText(Path.Combine(invalidName, "sentinel.txt"), "invalid-name", Encoding.UTF8);

        DateTime utcNow = DateTime.UtcNow;
        RewriteWorkMarkerCreatedUtc(expiredOwned, utcNow.AddDays(-10));
        RewriteWorkMarkerCreatedUtc(freshOwned, utcNow.AddHours(-1));
        RewriteWorkMarkerCreatedUtc(wrongOwnerRoot, utcNow.AddDays(-10));

        StorageMaintenanceResult result = StorageMaintenance.CleanupOwnedWorkDirectories(
            parentRoot,
            installRoot,
            TimeSpan.FromDays(1),
            utcNow);

        Assert(!Directory.Exists(expiredOwned), "过期且归属匹配的工作目录没有清理。");
        Assert(Directory.Exists(freshOwned), "未超过期限的自有工作目录被错误清理。");
        Assert(Directory.Exists(wrongOwnerRoot), "属于其他安装根的工作目录被错误清理。");
        Assert(Directory.Exists(unknown), "无 marker 的未知工作目录被错误清理。");
        Assert(Directory.Exists(invalidName), "名称不符合规则的目录被错误清理。");
        Assert(result.DeletedWorkDirectories == 1, "工作目录删除计数不正确。");
    }

    private static void TestOwnedWorkDirectoryRejectsIdentityReplacement()
    {
        string caseRoot = NewCaseRoot("owned-work-identity-replacement");
        string parentRoot = Path.Combine(caseRoot, "work-parent");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        Directory.CreateDirectory(parentRoot);
        string workRoot = CreateWorkDirectory(parentRoot, installRoot, "original");
        RewriteWorkMarkerCreatedUtc(workRoot, DateTime.UtcNow.AddDays(-10));
        byte[] originalMarker = File.ReadAllBytes(
            Path.Combine(workRoot, StorageMaintenance.WorkMarkerFileName));
        string originalRoot = workRoot + ".original";
        Directory.Move(workRoot, originalRoot);

        Directory.CreateDirectory(workRoot);
        File.WriteAllBytes(
            Path.Combine(workRoot, StorageMaintenance.WorkMarkerFileName),
            originalMarker);
        string replacementSentinel = Path.Combine(workRoot, "replacement.txt");
        File.WriteAllText(replacementSentinel, "替换目录必须保留", Encoding.UTF8);

        StorageMaintenanceResult result = StorageMaintenance.CleanupOwnedWorkDirectories(
            parentRoot,
            installRoot,
            TimeSpan.FromDays(1),
            DateTime.UtcNow);

        Assert(result.DeletedWorkDirectories == 0,
            "身份不匹配的替换工作目录被计入已删除数量。");
        Assert(File.Exists(replacementSentinel) &&
            File.ReadAllText(replacementSentinel, Encoding.UTF8) == "替换目录必须保留",
            "身份不匹配的替换工作目录被错误删除或修改。");
        Assert(result.Warnings.Any(value =>
                value.IndexOf("身份", StringComparison.Ordinal) >= 0),
            "工作目录身份不匹配时没有记录明确警告。");
    }

    private static void TestIntegrationStateSerialization()
    {
        string caseRoot = NewCaseRoot("integration-state-serialization");
        string installRoot = Path.Combine(caseRoot, "CodexDesktopA");
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "1.0.0.0", installId, "integration-a");
        string executablePath = Path.Combine(installRoot, "app", "Codex.exe");
        string physicalRoot = NativeFileSystem.GetStablePathForExistingPath(installRoot);
        string rootIdentity = InstallOwnership.GetManagedDirectoryIdentity(installRoot);
        string statePath = PortableStorage.IntegrationStateFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(statePath));
        if (File.Exists(statePath)) File.Delete(statePath);

        IntegrationState state = new IntegrationState
        {
            InstallId = installId,
            InstallRoot = installRoot,
            PhysicalInstallRoot = physicalRoot,
            RootIdentity = rootIdentity,
            ExecutablePath = executablePath,
            AppUserModelId = "com.openai.codex",
            Protocols = new List<string> { "codex" },
            ProgIds = new List<string> { "OpenAI.Codex.Test" },
            Extensions = new List<string> { ".test" },
            ShortcutPaths = new List<string> { Path.Combine(caseRoot, "Codex Portable.lnk") },
            CleanupPending = true
        };

        PortableStorage.SaveIntegrationState(state);
        string serialized = File.ReadAllText(statePath, Encoding.UTF8);
        Assert(serialized.IndexOf("\"InstallRoot\"", StringComparison.Ordinal) >= 0,
            "新 IntegrationState 没有序列化 InstallRoot。");
        Assert(serialized.IndexOf("\"InstallId\"", StringComparison.Ordinal) >= 0 &&
            serialized.IndexOf("\"PhysicalInstallRoot\"", StringComparison.Ordinal) >= 0 &&
            serialized.IndexOf("\"RootIdentity\"", StringComparison.Ordinal) >= 0,
            "新 IntegrationState 没有序列化安装 ID 或物理身份。");
        IntegrationState loaded = PortableStorage.LoadIntegrationState();
        Assert(PathsEqual(loaded.InstallRoot, installRoot),
            "IntegrationState.InstallRoot 往返序列化不一致。");
        Assert(string.Equals(loaded.InstallId, installId, StringComparison.OrdinalIgnoreCase) &&
            PathsEqual(loaded.PhysicalInstallRoot, physicalRoot) &&
            string.Equals(loaded.RootIdentity, rootIdentity, StringComparison.OrdinalIgnoreCase),
            "IntegrationState 的安装 ID 或物理身份往返不一致。");
        Assert(loaded.CleanupPending &&
            loaded.ShortcutPaths != null &&
            loaded.ShortcutPaths.Count == 1,
            "IntegrationState 的待清理状态或快捷方式范围往返序列化不一致。");

    }

    private static void TestPortableRegistryDiscovery()
    {
        string caseRoot = NewCaseRoot("portable-registry-discovery");
        string firstRoot = Path.Combine(caseRoot, "CodexDesktopA");
        string secondRoot = Path.Combine(caseRoot, "CodexDesktopB");
        string incompleteRoot = Path.Combine(caseRoot, "CodexDesktopIncomplete");
        string unownedRoot = Path.Combine(caseRoot, "CodexDesktopUnowned");
        CreateRunnableCodex(firstRoot, "1.0.0.0", Guid.NewGuid().ToString("N"), "registry-a");
        CreateRunnableCodex(secondRoot, "2.0.0.0", Guid.NewGuid().ToString("N"), "registry-b");
        CreateMinimalCodex(incompleteRoot, "1.0.0.0", Guid.NewGuid().ToString("N"), "registry-incomplete");
        CreateRunnableCodex(unownedRoot, "1.0.0.0", Guid.NewGuid().ToString("N"), "registry-unowned");
        File.Delete(Path.Combine(unownedRoot, ".codex-portable-manager.json"));

        string registryRoot = @"Software\OpenAI\CodexPortableManager.Tests." + Guid.NewGuid().ToString("N");
        string firstPath = registryRoot + @"\First";
        string duplicatePath = registryRoot + @"\Duplicate";
        string secondPath = registryRoot + @"\Second";
        string incompletePath = registryRoot + @"\Incomplete";
        string unownedPath = registryRoot + @"\Unowned";
        string missingPath = registryRoot + @"\Missing";
        try
        {
            SetPortableRegistryMarker(firstPath, firstRoot);
            SetPortableRegistryMarker(duplicatePath, firstRoot + Path.DirectorySeparatorChar);
            SetPortableRegistryMarker(secondPath, secondRoot);
            SetPortableRegistryMarker(incompletePath, incompleteRoot);
            SetPortableRegistryMarker(unownedPath, unownedRoot);
            SetPortableRegistryMarker(missingPath, Path.Combine(caseRoot, "does-not-exist"));

            string discovered = ShellIntegration.TryDiscoverPortableInstallRoot(
                new[] { firstPath, duplicatePath, incompletePath, unownedPath, missingPath });
            Assert(PathsEqual(discovered, firstRoot), "唯一有效便携目录没有从注册表恢复。实际：" + discovered);

            string ambiguous = ShellIntegration.TryDiscoverPortableInstallRoot(
                new[] { firstPath, secondPath });
            Assert(string.IsNullOrWhiteSpace(ambiguous), "多个有效便携目录冲突时不应静默选择。实际：" + ambiguous);

            string userDataRoot = PortableStorage.UserDataRoot;
            string configPath = Path.Combine(userDataRoot, "config.json");
            Directory.CreateDirectory(userDataRoot);
            string ordinaryRoot = Path.Combine(caseRoot, "ordinary-selected-directory");
            Directory.CreateDirectory(ordinaryRoot);
            File.WriteAllText(
                configPath,
                "{\"InstallRoot\":\"" + JsonEscape(ordinaryRoot) +
                "\",\"IgnoredField\":\"ignored\"}",
                new UTF8Encoding(false));
            string restored = InstallLocationResolver.ResolveInstallRoot(ordinaryRoot, () => firstRoot);
            Assert(PathsEqual(restored, firstRoot), "无效的手动目录记录没有回退到注册表结果。实际：" + restored);
            Assert(File.Exists(configPath), "注册表恢复结果没有写回 config.json。");
            string savedConfig = File.ReadAllText(configPath, Encoding.UTF8);
            Assert(savedConfig.IndexOf(firstRoot.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase) >= 0,
                "注册表恢复结果没有成为成功目录记录。");
            ManagerSettings loadedSettings = PortableStorage.LoadSettings();
            Assert(PathsEqual(loadedSettings.InstallRoot, firstRoot),
                "替换无效目录记录后没有保存唯一有效便携目录。");
            savedConfig = File.ReadAllText(configPath, Encoding.UTF8);
            Assert(savedConfig.IndexOf("IgnoredField", StringComparison.Ordinal) < 0,
                "替换无效目录记录后仍保留非当前配置字段。");

            string configured = InstallLocationResolver.ResolveInstallRoot(firstRoot, () => secondRoot);
            Assert(PathsEqual(configured, firstRoot), "有效的成功目录记录没有优先于注册表。实际：" + configured);

            Exception invalidSave = null;
            try
            {
                InstallLocationResolver.SaveConfirmedInstallRoot(ordinaryRoot);
            }
            catch (Exception exception)
            {
                invalidSave = Unwrap(exception);
            }
            Assert(invalidSave is InvalidOperationException,
                "普通浏览目录被错误保存为成功目录。实际异常：" + (invalidSave == null ? "无" : invalidSave.ToString()));

            File.Delete(Path.Combine(firstRoot, "app", "resources", "app.asar"));
            string fallback = InstallLocationResolver.ResolveInstallRoot(firstRoot, () => secondRoot);
            Assert(PathsEqual(fallback, secondRoot), "成功目录失效后没有回退到注册表。实际：" + fallback);

            File.Delete(Path.Combine(secondRoot, "app", "resources", "app.asar"));
            string empty = InstallLocationResolver.ResolveInstallRoot(secondRoot, () => null);
            Assert(string.IsNullOrWhiteSpace(empty), "成功记录和注册表都无效时没有返回空目录。实际：" + empty);
            savedConfig = File.ReadAllText(configPath, Encoding.UTF8);
            Assert(savedConfig.IndexOf(firstRoot, StringComparison.OrdinalIgnoreCase) < 0 &&
                savedConfig.IndexOf(secondRoot, StringComparison.OrdinalIgnoreCase) < 0,
                "所有候选失效后配置仍保留旧成功目录。" );
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registryRoot, false);
        }
    }

    private static void TestPortableStorageConfigTransaction()
    {
        string caseRoot = NewCaseRoot("portable-storage-config-transaction");
        string userDataRoot = PortableStorage.UserDataRoot;
        string configPath = Path.Combine(userDataRoot, "config.json");
        byte[] previousConfig = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
        Process installRootChild = null;

        try
        {
            string initialRoot = Path.Combine(caseRoot, "initial-root");
            string updatedRoot = Path.Combine(caseRoot, "updated-root");
            PortableStorage.SaveRecordedInstallRoot(initialRoot);

            string normalizedConfigPath = CrossProcessFileLock.NormalizePathKey(configPath);
            string installReadyPath = Path.Combine(caseRoot, "install-root-ready.txt");
            string harnessPath = Process.GetCurrentProcess().MainModule.FileName;

            using (HeldFileLock storageLock = CrossProcessFileLock.Acquire(
                "storage",
                "storage-path|" + normalizedConfigPath,
                TimeSpan.FromSeconds(5),
                stream => new HeldFileLock(stream),
                "测试未能获得配置文件锁。"))
            {
                installRootChild = StartConfigSaveChild(
                    harnessPath,
                    "install-root",
                    updatedRoot,
                    installReadyPath);

                WaitForChildReady(installRootChild, installReadyPath, "安装根保存子进程");
                Thread.Sleep(1000);
                Assert(!installRootChild.HasExited,
                    "配置保存子进程没有在父进程持锁期间等待共享文件锁。");
            }

            Assert(installRootChild.WaitForExit(8000), "安装根保存子进程未正常退出。");
            Assert(installRootChild.ExitCode == 0,
                "安装根保存子进程退出码异常：" + installRootChild.ExitCode.ToString(CultureInfo.InvariantCulture));

            ManagerSettings loaded = PortableStorage.LoadSettings();
            Assert(PathsEqual(loaded.InstallRoot, updatedRoot),
                "跨进程配置事务没有保存较新的安装根。");
        }
        finally
        {
            DisposeChildProcess(installRootChild);
            Directory.CreateDirectory(userDataRoot);
            if (previousConfig == null)
            {
                if (File.Exists(configPath)) File.Delete(configPath);
            }
            else
            {
                File.WriteAllBytes(configPath, previousConfig);
            }
        }
    }

    private static void TestConditionalRecordedInstallRootClear()
    {
        string caseRoot = NewCaseRoot("conditional-install-root-clear");
        string userDataRoot = PortableStorage.UserDataRoot;
        string configPath = Path.Combine(userDataRoot, "config.json");
        byte[] previousConfig = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
        try
        {
            string recordedRoot = Path.Combine(caseRoot, "recorded");
            string unrelatedRoot = Path.Combine(caseRoot, "previous-only");
            PortableStorage.SaveRecordedInstallRoot(recordedRoot);

            Assert(!PortableStorage.ClearRecordedInstallRootIfMatches(unrelatedRoot),
                "清理 previous-only 目标时错误清除了另一便携版的成功目录记录。");
            Assert(PathsEqual(PortableStorage.LoadSettings().InstallRoot, recordedRoot),
                "不匹配的条件清理改写了成功目录记录。");

            Assert(PortableStorage.ClearRecordedInstallRootIfMatches(recordedRoot),
                "匹配当前便携版路径时没有清除成功目录记录。");
            Assert(string.IsNullOrWhiteSpace(PortableStorage.LoadSettings().InstallRoot),
                "匹配清理完成后配置仍保留旧成功目录。");
        }
        finally
        {
            Directory.CreateDirectory(userDataRoot);
            if (previousConfig == null)
            {
                if (File.Exists(configPath)) File.Delete(configPath);
            }
            else
            {
                File.WriteAllBytes(configPath, previousConfig);
            }
        }
    }

    private static void TestPortableStorageScopePartitioning()
    {
        string dataRoot = PortableStorage.DataRoot;
        string userDataRoot = PortableStorage.UserDataRoot;
        string logsRoot = PortableStorage.LogsRoot;
        string sharedLocksRoot = PortableStorage.SharedLocksRoot;

        string firstUserRoot = PortableStorage.GetUserDataRoot(dataRoot, "S-1-5-21-1001");
        string secondUserRoot = PortableStorage.GetUserDataRoot(dataRoot, "S-1-5-21-1002");
        Assert(!PathsEqual(firstUserRoot, secondUserRoot), "不同 SID 被映射到了同一个用户状态目录。");
        Assert(IsPathUnderRoot(userDataRoot, Path.Combine(dataRoot, "users")), "当前用户状态没有位于 data/users 下。");
        Assert(IsPathUnderRoot(logsRoot, userDataRoot), "用户日志没有位于当前用户状态目录下。");
        Assert(PathsEqual(sharedLocksRoot, Path.Combine(dataRoot, "locks")), "共享锁目录没有位于 data/locks。");

        string originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        string firstLockPath;
        string secondLockPath;
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(suiteRoot, "profile-a"));
            firstLockPath = GetCrossProcessLockPath("cache", "scope-test-key");
            Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(suiteRoot, "profile-b"));
            secondLockPath = GetCrossProcessLockPath("cache", "scope-test-key");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
        }

        Assert(PathsEqual(firstLockPath, secondLockPath), "共享缓存锁路径仍随当前用户 LocalAppData 变化。");
        Assert(IsPathUnderRoot(firstLockPath, sharedLocksRoot), "共享缓存锁没有位于共享锁目录下。");
    }

    private static Process StartConfigSaveChild(
        string harnessPath,
        string operation,
        string value,
        string readyPath)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = harnessPath,
            Arguments = string.Join(" ", new[]
            {
                QuoteArgument("--save-config-part"),
                QuoteArgument(managerPath),
                QuoteArgument(operation),
                QuoteArgument(value),
                QuoteArgument(readyPath)
            }),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return Process.Start(startInfo);
    }

    private static void WaitForChildReady(Process child, string readyPath, string description)
    {
        Stopwatch readyWait = Stopwatch.StartNew();
        while (!File.Exists(readyPath) && !child.HasExited && readyWait.Elapsed < TimeSpan.FromSeconds(8))
        {
            Thread.Sleep(25);
        }
        Assert(File.Exists(readyPath),
            description + "未在超时内就绪，退出码：" +
            (child.HasExited ? child.ExitCode.ToString(CultureInfo.InvariantCulture) : "仍在运行"));
    }

    private static void DisposeChildProcess(Process child)
    {
        if (child == null)
        {
            return;
        }
        try
        {
            if (!child.HasExited && !child.WaitForExit(3000))
            {
                child.Kill();
                child.WaitForExit(3000);
            }
        }
        finally
        {
            child.Dispose();
        }
    }
}
}
