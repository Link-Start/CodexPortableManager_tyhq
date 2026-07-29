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
    private static void TestRejectsUnownedDirectory()
    {
        string caseRoot = NewCaseRoot("unowned-uninstall");
        string installRoot = Path.Combine(caseRoot, "not-codex");
        Directory.CreateDirectory(installRoot);
        string sentinel = Path.Combine(installRoot, "do-not-delete.txt");
        File.WriteAllText(sentinel, "用户文件必须保留", Encoding.UTF8);

        List<string> logs = new List<string>();
        CodexPortableService service = CreateService(logs);
        Exception failure = null;
        try
        {
            service.UninstallPortable(installRoot);
        }
        catch (Exception exception)
        {
            failure = Unwrap(exception);
        }
        finally
        {
            service.Dispose();
        }

        Assert(failure != null, "卸载未拥有目录时应当抛出异常。");
        Assert(failure.Message.IndexOf("拒绝", StringComparison.Ordinal) >= 0,
            "卸载拒绝异常缺少明确的安全提示：" + failure.Message);
        Assert(Directory.Exists(installRoot), "未拥有目录被错误删除。");
        Assert(File.Exists(sentinel), "未拥有目录中的哨兵文件被错误删除。");
        Assert(File.ReadAllText(sentinel, Encoding.UTF8) == "用户文件必须保留", "哨兵文件内容被修改。");
    }

    private static void TestInstallRootRejectsManagerStorageOverlap()
    {
        string dataRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        string cacheRoot = Path.Combine(dataRoot, "cache");
        string logsRoot = Path.Combine(dataRoot, "logs");
        string[] rejectedRoots =
        {
            dataRoot,
            cacheRoot,
            logsRoot,
            Path.Combine(cacheRoot, "portable"),
            Path.Combine(logsRoot, "portable"),
            Directory.GetParent(dataRoot).FullName
        };

        foreach (string rejectedRoot in rejectedRoots)
        {
            Exception failure = null;
            try
            {
                DeploymentEngine.ValidateInstallRoot(rejectedRoot);
            }
            catch (Exception exception)
            {
                failure = Unwrap(exception);
            }

            Assert(failure is ArgumentException,
                "与管理器存储树重叠的安装目录没有被拒绝：" + rejectedRoot);
            Assert(failure.Message.IndexOf("数据、缓存或日志目录", StringComparison.Ordinal) >= 0,
                "存储树重叠没有返回明确错误：" + rejectedRoot + "；" + failure.Message);
        }

        string nonOverlappingRoot = dataRoot + "-portable";
        string normalized = DeploymentEngine.ValidateInstallRoot(nonOverlappingRoot);
        Assert(PathsEqual(normalized, nonOverlappingRoot),
            "仅名称前缀相似但不重叠的安装目录被错误拒绝。实际：" + normalized);

        string substDrive = null;
        try
        {
            string physicalBase = NativeFileSystem.GetStablePathForExistingPath(
                AppDomain.CurrentDomain.BaseDirectory);
            substDrive = CreateSubstDrive(physicalBase);
            string aliasedStorageRoot = Path.Combine(substDrive + "\\", "data", "portable");
            Exception aliasFailure = CaptureFailure(delegate
            {
                DeploymentEngine.ValidateInstallRoot(aliasedStorageRoot);
            });
            Assert(aliasFailure is ArgumentException &&
                aliasFailure.Message.IndexOf("数据、缓存或日志目录", StringComparison.Ordinal) >= 0,
                "SUBST 物理别名绕过了管理器存储树重叠检查。");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(substDrive))
            {
                RemoveSubstDrive(substDrive);
            }
        }
    }

    private static void TestInstallRootRejectsRemotePaths()
    {
        string remoteRoot = @"\\server\share\CodexDesktop";
        Exception failure = CaptureFailure(delegate
        {
            DeploymentEngine.ValidateInstallRoot(remoteRoot);
        });
        Assert(failure is ArgumentException,
            "UNC 事务安装路径没有被拒绝。");
        Assert(failure.Message.IndexOf(
            "网络盘",
            StringComparison.Ordinal) >= 0,
            "远程安装拒绝没有返回明确的 File ID 持久性提示：" +
            failure.Message);

        string mappedRoot = Path.Combine(
            Path.GetPathRoot(Environment.SystemDirectory),
            "MappedCodexDesktop");
        try
        {
            DeploymentEngine.InstallRootDriveTypeProviderForTest = root =>
                DriveType.Network;
            Exception mappedFailure = CaptureFailure(delegate
            {
                DeploymentEngine.ValidateInstallRoot(mappedRoot);
            });
            Assert(mappedFailure is ArgumentException &&
                mappedFailure.Message.IndexOf("网络盘", StringComparison.Ordinal) >= 0,
                "映射网络盘事务安装路径没有被拒绝。");

            using (CodexPortableService service = CreateService(new List<string>()))
            {
                PortableLocalStatus status = service.GetLocalStatus(mappedRoot);
                Assert(!string.IsNullOrWhiteSpace(status.Error) &&
                    status.PortableVersion == null,
                    "状态入口仍把映射网络盘显示为可操作的便携安装。");
            }
        }
        finally
        {
            DeploymentEngine.InstallRootDriveTypeProviderForTest = null;
        }
    }

    private static void TestTopLevelJunctionDeletion()
    {
        string caseRoot = NewCaseRoot("junction-delete");
        string linkParent = Path.Combine(caseRoot, "allowed-parent");
        string targetRoot = Path.Combine(caseRoot, "outside-target");
        string linkRoot = Path.Combine(linkParent, "CodexDesktop");
        Directory.CreateDirectory(linkParent);
        Directory.CreateDirectory(targetRoot);
        string sentinel = Path.Combine(targetRoot, "outside-sentinel.txt");
        File.WriteAllText(sentinel, "junction 目标不得删除", Encoding.UTF8);
        CreateJunction(linkRoot, targetRoot);

        Assert((File.GetAttributes(linkRoot) & FileAttributes.ReparsePoint) != 0, "测试前置失败：创建的路径不是重解析点。");

        DeploymentEngine.DeleteDirectorySafely(linkRoot, linkParent);

        Assert(!Directory.Exists(linkRoot), "顶层 junction 链接本身没有被删除。");
        Assert(Directory.Exists(targetRoot), "顶层 junction 的目标目录被错误删除。");
        Assert(File.Exists(sentinel), "顶层 junction 的目标哨兵被错误删除。");
        Assert(File.ReadAllText(sentinel, Encoding.UTF8) == "junction 目标不得删除", "目标哨兵内容被修改。");
    }

    private static void TestManagedRootRejectsTopLevelJunction()
    {
        string caseRoot = NewCaseRoot("managed-junction-root");
        string targetRoot = Path.Combine(caseRoot, "payload");
        string linkRoot = Path.Combine(caseRoot, "CodexDesktop");
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(targetRoot, "1.2.3.4", installId, "junction-target");
        string sentinel = Path.Combine(targetRoot, "outside-sentinel.txt");
        File.WriteAllText(sentinel, "受管 payload 不得被间接操作", Encoding.UTF8);
        CreateJunction(linkRoot, targetRoot);

        Exception validationFailure = CaptureFailure(delegate
        {
            DeploymentEngine.ValidateInstallRoot(linkRoot);
        });
        Assert(validationFailure is ArgumentException, "最终安装根 junction 应在路径验证阶段被拒绝。");
        Assert(validationFailure.Message.IndexOf("重解析点", StringComparison.Ordinal) >= 0,
            "最终安装根 junction 的拒绝错误不明确：" + validationFailure.Message);

        Exception ownershipFailure = CaptureFailure(delegate
        {
            InstallOwnership.EnsureOwnedInstallation(linkRoot, installId, null, delegate { });
        });
        Assert(ownershipFailure is InvalidOperationException, "所有权验证不得沿最终安装根 junction 读取 payload。");
        Assert(ownershipFailure.Message.IndexOf("重解析点", StringComparison.Ordinal) >= 0,
            "所有权验证的 junction 拒绝错误不明确：" + ownershipFailure.Message);

        CodexPortableService service = CreateService(new List<string>());
        Exception uninstallFailure;
        try
        {
            uninstallFailure = CaptureFailure(delegate
            {
                service.UninstallPortable(linkRoot);
            });
        }
        finally
        {
            service.Dispose();
        }
        Assert(uninstallFailure is ArgumentException, "完整卸载不得接受最终安装根 junction。");
        Assert(Directory.Exists(linkRoot), "拒绝卸载后 junction 链接不应被删除。");
        Assert(Directory.Exists(targetRoot), "拒绝卸载后 junction 目标不应被删除。");
        Assert(File.Exists(sentinel), "拒绝卸载后 payload 哨兵不应被删除。");
    }

    private static void TestInstallRootRejectsJunctionAncestor()
    {
        string caseRoot = NewCaseRoot("junction-ancestor-root");
        string physicalParent = Path.Combine(caseRoot, "physical-parent");
        string parentAlias = Path.Combine(caseRoot, "parent-alias");
        string installRoot = Path.Combine(parentAlias, "not-created", "CodexDesktop");
        Directory.CreateDirectory(physicalParent);
        CreateJunction(parentAlias, physicalParent);

        Exception failure = CaptureFailure(delegate
        {
            DeploymentEngine.ValidateInstallRoot(installRoot);
        });

        Assert(failure is ArgumentException, "带 junction 祖先的安装根应被拒绝。");
        Assert(failure.Message.IndexOf("重解析点祖先", StringComparison.Ordinal) >= 0,
            "junction 祖先的拒绝错误不明确：" + failure.Message);
        Assert(!Directory.Exists(Path.Combine(physicalParent, "not-created")),
            "路径验证不应沿 junction 祖先创建安装目录。");
    }

    private static void TestInstallDestinationResolution()
    {
        string caseRoot = NewCaseRoot("install-destination");
        string emptyRoot = Path.Combine(caseRoot, "empty");
        Directory.CreateDirectory(emptyRoot);
        string resolved = InstallLocationResolver.ResolveInstallDestination(emptyRoot);
        Assert(PathsEqual(resolved, emptyRoot), "空目录不应额外创建 Codex 子目录。实际：" + resolved);

        string occupiedParent = Path.Combine(caseRoot, "occupied-parent");
        Directory.CreateDirectory(occupiedParent);
        File.WriteAllText(Path.Combine(occupiedParent, "existing.txt"), "保留", Encoding.UTF8);
        string expectedCodex = Path.Combine(occupiedParent, "Codex");
        resolved = InstallLocationResolver.ResolveInstallDestination(occupiedParent);
        Assert(PathsEqual(resolved, expectedCodex), "非空目录没有解析到 Codex 子目录。实际：" + resolved);
        Assert(!Directory.Exists(expectedCodex), "解析安装目标时不应提前创建 Codex 子目录。");

        Directory.CreateDirectory(expectedCodex);
        File.WriteAllText(Path.Combine(expectedCodex, "occupied.txt"), "占用", Encoding.UTF8);
        string expectedCodex2 = Path.Combine(occupiedParent, "Codex-2");
        CreateMinimalCodex(expectedCodex2, "1.2.3.4", Guid.NewGuid().ToString("N"), "existing-codex");
        resolved = InstallLocationResolver.ResolveInstallDestination(occupiedParent);
        Assert(PathsEqual(resolved, expectedCodex2), "Codex 被占用时没有复用已有合法 Codex-2 安装。实际：" + resolved);

        resolved = InstallLocationResolver.ResolveInstallDestination(expectedCodex);
        Assert(PathsEqual(resolved, expectedCodex2), "直接选择被占用的 Codex 目录时没有选择同级 Codex-2。实际：" + resolved);

        string existingInstall = Path.Combine(caseRoot, "existing-install");
        CreateMinimalCodex(existingInstall, "2.0.0.0", Guid.NewGuid().ToString("N"), "direct-install");
        resolved = InstallLocationResolver.ResolveInstallDestination(existingInstall);
        Assert(PathsEqual(resolved, existingInstall), "已有合法 Codex 安装不应被重定向到子目录。实际：" + resolved);
    }

    private static void TestEmptyDirectoryDeletionNeverRecurses()
    {
        string caseRoot = NewCaseRoot("empty-delete-never-recurses");
        string target = Path.Combine(caseRoot, "target");
        Directory.CreateDirectory(target);
        NativeFileSystem.DeleteEmptyDirectory(target);
        Assert(!Directory.Exists(target), "空目录没有被句柄式空目录删除清理。");

        Directory.CreateDirectory(target);
        string sentinel = Path.Combine(target, "late-entry.txt");
        File.WriteAllText(sentinel, "检查后进入的文件必须保留", Encoding.UTF8);
        Exception failure = CaptureFailure(delegate
        {
            NativeFileSystem.DeleteEmptyDirectory(target);
        });

        Assert(failure is IOException,
            "非空目录没有被空目录删除原语拒绝。实际异常：" +
            (failure == null ? "无" : failure.ToString()));
        Assert(File.Exists(sentinel), "空目录删除错误递归清理了后来进入的文件。");
    }

    private static void TestFileDeletionRejectsJunctionAncestor()
    {
        string caseRoot = NewCaseRoot("file-delete-junction-ancestor");
        string physicalRoot = Path.Combine(caseRoot, "physical");
        string aliasRoot = Path.Combine(caseRoot, "alias");
        Directory.CreateDirectory(physicalRoot);
        string sentinel = Path.Combine(physicalRoot, "sentinel.txt");
        File.WriteAllText(sentinel, "junction 目标文件必须保留", Encoding.UTF8);
        CreateJunction(aliasRoot, physicalRoot);

        Exception failure = CaptureFailure(delegate
        {
            NativeFileSystem.DeleteFile(Path.Combine(aliasRoot, "sentinel.txt"));
        });

        Assert(failure is IOException,
            "普通文件删除没有拒绝 junction 祖先。实际异常：" +
            (failure == null ? "无" : failure.ToString()));
        Assert(File.Exists(sentinel), "普通文件删除越过 junction 删除了目标文件。");
    }

    private static void TestFileDeletionRejectsIdentityReplacement()
    {
        string caseRoot = NewCaseRoot("file-delete-identity-replacement");
        string target = Path.Combine(caseRoot, "target.json");
        string original = Path.Combine(caseRoot, "original.json");
        File.WriteAllText(target, "original", Encoding.UTF8);
        string identity = NativeFileSystem.GetPersistentFileIdentity(target);
        File.Move(target, original);
        File.WriteAllText(target, "replacement", Encoding.UTF8);

        Exception failure = CaptureFailure(delegate
        {
            NativeFileSystem.DeleteFile(target, identity);
        });

        Assert(failure is InvalidDataException,
            "普通文件删除没有拒绝 File ID 不匹配的替换文件。实际异常：" +
            (failure == null ? "无" : failure.ToString()));
        Assert(File.Exists(target) && File.ReadAllText(target, Encoding.UTF8) == "replacement",
            "File ID 不匹配的替换文件被错误删除或改写。");
        Assert(File.Exists(original), "原始文件在身份替换测试中被错误删除。");
    }

    private static void TestDenseDirectoryDeletion()
    {
        Assert(!NativeFileSystem.DeleteChildUsesListDirectoryAccessForTest(false, false) &&
            NativeFileSystem.DeleteChildUsesListDirectoryAccessForTest(true, false) &&
            !NativeFileSystem.DeleteChildUsesListDirectoryAccessForTest(true, true),
            "安全删除为普通文件或目录重解析点错误申请了内容读取权限。");
        string caseRoot = NewCaseRoot("dense-delete");
        string allowedParent = Path.Combine(caseRoot, "allowed-parent");
        string denseRoot = Path.Combine(allowedParent, "dense");
        Directory.CreateDirectory(denseRoot);
        for (int index = 0; index < 1500; index++)
        {
            File.WriteAllText(
                Path.Combine(denseRoot, "entry-" + index.ToString("D4", CultureInfo.InvariantCulture) + ".js"),
                "x",
                Encoding.ASCII);
        }
        string readOnlyFile = Path.Combine(denseRoot, "entry-0000.js");
        File.SetAttributes(readOnlyFile, File.GetAttributes(readOnlyFile) | FileAttributes.ReadOnly);

        Stopwatch stopwatch = Stopwatch.StartNew();
        DeploymentEngine.DeleteDirectorySafely(denseRoot, allowedParent);
        stopwatch.Stop();

        Assert(!Directory.Exists(denseRoot), "密集小文件目录没有被完整删除。");
        Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            "密集小文件目录删除耗时异常：" + stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " 秒。");
    }

    private static void TestNativeDirectoryDeletionRejectsIdentityReplacement()
    {
        string caseRoot = NewCaseRoot("native-delete-identity-replacement");
        string cleanupRoot = Path.Combine(caseRoot, "cleanup-root");
        string originalRoot = Path.Combine(caseRoot, "original-root");
        Directory.CreateDirectory(cleanupRoot);
        string expectedIdentity = InstallOwnership.GetManagedDirectoryIdentity(cleanupRoot);
        File.WriteAllText(
            Path.Combine(cleanupRoot, "original.txt"),
            "原始清理目录",
            Encoding.UTF8);

        Directory.Move(cleanupRoot, originalRoot);
        Directory.CreateDirectory(cleanupRoot);
        string replacementSentinel = Path.Combine(cleanupRoot, "replacement-sentinel.txt");
        File.WriteAllText(replacementSentinel, "替换目录必须保留", Encoding.UTF8);

        Exception failure = CaptureFailure(delegate
        {
            NativeFileSystem.DeleteDirectoryRecursively(cleanupRoot, expectedIdentity);
        });

        Assert(failure is InvalidDataException,
            "最终删除句柄没有拒绝身份不匹配的替换目录。");
        Assert(Directory.Exists(cleanupRoot),
            "身份不匹配的替换目录被错误删除。");
        Assert(File.Exists(replacementSentinel),
            "身份不匹配的替换目录哨兵被错误删除。");
        Assert(File.ReadAllText(replacementSentinel, Encoding.UTF8) == "替换目录必须保留",
            "身份不匹配的替换目录哨兵内容被修改。");
        Assert(Directory.Exists(originalRoot),
            "移走的原始清理目录不应被最终路径身份复验修改。");
    }

    private static void TestNativeDirectoryDeletionRejectsReceiptJunctionReplacement()
    {
        string caseRoot = NewCaseRoot("native-delete-junction-replacement");
        string cleanupRoot = Path.Combine(caseRoot, "cleanup-root");
        string originalRoot = Path.Combine(caseRoot, "original-root");
        string junctionTarget = Path.Combine(caseRoot, "junction-target");
        Directory.CreateDirectory(cleanupRoot);
        Directory.CreateDirectory(junctionTarget);
        string expectedIdentity = InstallOwnership.GetManagedDirectoryIdentity(cleanupRoot);
        string targetSentinel = Path.Combine(junctionTarget, "target-sentinel.txt");
        File.WriteAllText(targetSentinel, "junction 目标必须保留", Encoding.UTF8);

        Directory.Move(cleanupRoot, originalRoot);
        CreateJunction(cleanupRoot, junctionTarget);
        Exception failure = CaptureFailure(delegate
        {
            NativeFileSystem.DeleteDirectoryRecursively(cleanupRoot, expectedIdentity);
        });

        Assert(failure is InvalidDataException,
            "最终删除句柄没有拒绝带 receipt 的替换 junction。");
        Assert(Directory.Exists(cleanupRoot),
            "带 receipt 的替换 junction 被错误删除。");
        Assert(File.Exists(targetSentinel) &&
            File.ReadAllText(targetSentinel, Encoding.UTF8) == "junction 目标必须保留",
            "替换 junction 的目标内容被修改。");
        Assert(Directory.Exists(originalRoot),
            "原始 receipt 目录被替换测试意外修改。");
    }

    private static void TestManagedDirectoryIdentityUsesPersistentFileId()
    {
        string caseRoot = NewCaseRoot("managed-directory-identity");
        string directory = Path.Combine(caseRoot, "identity-root");
        Directory.CreateDirectory(directory);
        string identity = InstallOwnership.GetManagedDirectoryIdentity(directory);
        Assert(identity.StartsWith("directory-identity|", StringComparison.Ordinal) &&
            InstallOwnership.IsManagedDirectoryIdentity(identity),
            "目录清理身份格式无效：" + identity);
        Assert(!InstallOwnership.IsManagedDirectoryIdentity(
            "directory-identity|0000000000000000|00000000000000000000000000000000"),
            "目录清理身份接受了全零 File ID。");
        Assert(!InstallOwnership.IsManagedDirectoryIdentity(
            "directory-identity|0000000000000000|00000000000000000000000000000001"),
            "目录清理身份接受了零卷序列号。");
        Assert(!InstallOwnership.IsManagedDirectoryIdentity(
            "directory-identity|0000000000000001|00000000000000000000000000000000"),
            "目录清理身份接受了零 File ID。");
        Assert(InstallOwnership.ManagedDirectoryIdentitiesEqual(identity, identity),
            "同一目录身份没有被识别为同一对象。");
        Assert(!InstallOwnership.ManagedDirectoryIdentitiesEqual(
            "directory-identity|0000000000000001|00000000000000000000000000000001",
            "directory-identity|0000000000000002|00000000000000000000000000000001"),
            "不同卷上的目录身份被误判为同一对象。");
    }

    private static void TestDeploymentCleanupReceiptArmsAfterMove()
    {
        string caseRoot = NewCaseRoot("cleanup-receipt-arm-after-move");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = previousRoot + ".transaction-old";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(
            previousRoot,
            "1.0.0.0",
            installId,
            "cleanup-receipt-source");

        DeploymentJournalRecord journal = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Update,
            Phase = DeploymentTransactionPhase.UpdatePrepared,
            InstallRoot = installRoot,
            InstallId = installId,
            HadCurrent = false,
            HadPrevious = true
        };
        journal.UpdateOldPreviousCleanup =
            DeploymentJournal.CreatePreparedCleanupReceipt(
                journal,
                previousRoot,
                transactionRoot);
        DeploymentJournal.Write(journal);
        Assert(journal.UpdateOldPreviousCleanup.Phase ==
            DeploymentCleanupReceiptPhase.Prepared &&
            string.IsNullOrWhiteSpace(
                journal.UpdateOldPreviousCleanup.DirectoryIdentity),
            "目录移动前的 cleanup receipt 没有保持 Prepared。");
        Assert(InstallOwnership.IsManagedDirectoryIdentity(
            journal.UpdateOldPreviousCleanup.SourceDirectoryIdentity),
            "Prepared cleanup receipt 没有绑定来源目录身份。");

        bool identityReadAfterMove = false;
        try
        {
            DeploymentJournal.CleanupReceiptIdentityProviderForTest =
                delegate(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path)
                {
                    identityReadAfterMove =
                        !Directory.Exists(previousRoot) &&
                        Directory.Exists(transactionRoot) &&
                        PathsEqual(path, transactionRoot);
                    return InstallOwnership.GetManagedDirectoryIdentity(
                        handle,
                        path);
                };
            using (Microsoft.Win32.SafeHandles.SafeFileHandle handle =
                InstallOwnership.OpenManagedDirectoryHandle(previousRoot))
            {
                DeploymentEngine.MoveDirectoryWithRetry(
                    previousRoot,
                    transactionRoot);
                DeploymentJournalRecord candidate =
                    DeploymentJournal.Clone(journal);
                candidate.UpdateOldPreviousCleanup =
                    DeploymentJournal.ArmCleanupReceipt(
                        candidate,
                        candidate.UpdateOldPreviousCleanup,
                        handle,
                        transactionRoot);
                candidate.Phase =
                    DeploymentTransactionPhase.UpdateOldPreviousDetached;
                DeploymentJournal.Write(candidate);
            }
        }
        finally
        {
            DeploymentJournal.CleanupReceiptIdentityProviderForTest = null;
        }

        DeploymentJournalRecord persisted = DeploymentJournal.Read(installRoot);
        Assert(identityReadAfterMove,
            "cleanup receipt 在目录移动完成前读取了最终身份。");
        Assert(persisted.UpdateOldPreviousCleanup.Phase ==
            DeploymentCleanupReceiptPhase.Armed,
            "目录移动完成后 cleanup receipt 没有进入 Armed。");
        Assert(string.Equals(
            persisted.UpdateOldPreviousCleanup.DirectoryIdentity,
            InstallOwnership.GetManagedDirectoryIdentity(transactionRoot),
            StringComparison.OrdinalIgnoreCase),
            "cleanup receipt 没有持久化最终 transaction 路径的身份。");

        RecoverDeployment(installRoot);
        AssertVersionAt(
            previousRoot,
            "cleanup-receipt-source",
            "1.0.0.0");
        Assert(!Directory.Exists(transactionRoot) &&
            !DeploymentJournal.Exists(installRoot),
            "Prepared 到 Armed 回归结束后没有恢复原拓扑。");
    }

    private static void TestPreparedCleanupReceiptRejectsReplacedSource()
    {
        string caseRoot = NewCaseRoot("prepared-replaced-source");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string originalRoot = previousRoot + ".original";
        string transactionRoot = previousRoot + ".transaction-old";
        string sentinel = Path.Combine(transactionRoot, "replacement.txt");
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(
            previousRoot,
            "1.0.0.0",
            installId,
            "prepared-original-source");

        DeploymentJournalRecord journal = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Update,
            Phase = DeploymentTransactionPhase.UpdatePrepared,
            InstallRoot = installRoot,
            InstallId = installId,
            HadCurrent = false,
            HadPrevious = true
        };
        using (Microsoft.Win32.SafeHandles.SafeFileHandle sourceHandle =
            InstallOwnership.OpenManagedDirectoryHandle(previousRoot))
        {
            journal.UpdateOldPreviousCleanup =
                DeploymentJournal.CreatePreparedCleanupReceipt(
                    journal,
                    sourceHandle,
                    previousRoot,
                    transactionRoot);
            DeploymentJournal.Write(journal);

            Directory.Move(previousRoot, originalRoot);
            Directory.CreateDirectory(previousRoot);
            File.WriteAllText(
                Path.Combine(previousRoot, "replacement.txt"),
                "替换目录必须保留",
                Encoding.UTF8);
            Directory.Move(previousRoot, transactionRoot);

            DeploymentJournalRecord candidate =
                DeploymentJournal.Clone(journal);
            Exception failure = CaptureFailure(delegate
            {
                candidate.UpdateOldPreviousCleanup =
                    DeploymentJournal.ArmCleanupReceipt(
                        candidate,
                        candidate.UpdateOldPreviousCleanup,
                        sourceHandle,
                        transactionRoot);
            });
            Assert(failure is InvalidDataException,
                "Prepared 后被替换的来源目录仍进入了 Armed。");
        }

        Assert(File.Exists(sentinel) &&
            File.ReadAllText(sentinel, Encoding.UTF8) == "替换目录必须保留",
            "来源替换测试修改或删除了替换目录。");
        Assert(Directory.Exists(originalRoot),
            "来源替换测试修改了原始受管目录。");
        DeploymentJournalRecord persisted = DeploymentJournal.Read(installRoot);
        Assert(persisted.UpdateOldPreviousCleanup.Phase ==
            DeploymentCleanupReceiptPhase.Prepared,
            "来源替换被拒绝后磁盘 receipt 不再保持 Prepared。");
    }

    private static void TestPreparedCleanupMoveWindowsRollBack()
    {
        string updateCaseRoot = NewCaseRoot("prepared-update-move-window");
        string updateInstallRoot = Path.Combine(updateCaseRoot, "CodexDesktop");
        string updatePreviousRoot = updateInstallRoot + ".previous";
        string updateTransactionRoot = updatePreviousRoot + ".transaction-old";
        string updateInstallId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(
            updateInstallRoot,
            "2.0.0.0",
            updateInstallId,
            "prepared-update-current");
        CreateMinimalCodex(
            updatePreviousRoot,
            "1.0.0.0",
            updateInstallId,
            "prepared-update-previous");
        DeploymentJournalRecord updateJournal = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Update,
            Phase = DeploymentTransactionPhase.UpdatePrepared,
            InstallRoot = updateInstallRoot,
            InstallId = updateInstallId,
            HadCurrent = true,
            HadPrevious = true
        };
        updateJournal.UpdateOldPreviousCleanup =
            DeploymentJournal.CreatePreparedCleanupReceipt(
                updateJournal,
                updatePreviousRoot,
                updateTransactionRoot);
        DeploymentJournal.Write(updateJournal);
        Directory.Move(updatePreviousRoot, updateTransactionRoot);

        RecoverDeployment(updateInstallRoot);
        AssertVersionAt(
            updateInstallRoot,
            "prepared-update-current",
            "2.0.0.0");
        AssertVersionAt(
            updatePreviousRoot,
            "prepared-update-previous",
            "1.0.0.0");
        Assert(!Directory.Exists(updateTransactionRoot) &&
            !DeploymentJournal.Exists(updateInstallRoot),
            "update move 后 Armed 写入前的窗口没有回滚。");

        string uninstallCaseRoot = NewCaseRoot("prepared-uninstall-move-window");
        string uninstallRoot = Path.Combine(uninstallCaseRoot, "CodexDesktop");
        string uninstallPreviousRoot = uninstallRoot + ".previous";
        string previousTombstone = uninstallRoot + ".uninstall-previous";
        string uninstallInstallId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(
            uninstallRoot,
            "2.0.0.0",
            uninstallInstallId,
            "prepared-uninstall-current");
        CreateMinimalCodex(
            uninstallPreviousRoot,
            "1.0.0.0",
            uninstallInstallId,
            "prepared-uninstall-previous");
        DeploymentJournalRecord uninstallJournal = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Uninstall,
            Phase = DeploymentTransactionPhase.UninstallPrepared,
            InstallRoot = uninstallRoot,
            InstallId = uninstallInstallId,
            HadCurrent = true,
            HadPrevious = true
        };
        uninstallJournal.UninstallCurrentCleanup =
            DeploymentJournal.CreatePreparedCleanupReceipt(
                uninstallJournal,
                uninstallRoot,
                uninstallRoot + ".uninstall-current");
        uninstallJournal.UninstallPreviousCleanup =
            DeploymentJournal.CreatePreparedCleanupReceipt(
                uninstallJournal,
                uninstallPreviousRoot,
                previousTombstone);
        DeploymentJournal.Write(uninstallJournal);
        Directory.Move(uninstallPreviousRoot, previousTombstone);

        RecoverDeployment(uninstallRoot);
        AssertVersionAt(
            uninstallRoot,
            "prepared-uninstall-current",
            "2.0.0.0");
        AssertVersionAt(
            uninstallPreviousRoot,
            "prepared-uninstall-previous",
            "1.0.0.0");
        Assert(!Directory.Exists(previousTombstone) &&
            !DeploymentJournal.Exists(uninstallRoot),
            "uninstall previous move 后 Armed 写入前的窗口没有回滚。");
    }

    private static void TestPreparedRecoveryRejectsSameInstallIdReplacement()
    {
        string updateCaseRoot = NewCaseRoot("prepared-update-recovery-replacement");
        string updateRoot = Path.Combine(updateCaseRoot, "CodexDesktop");
        string updatePrevious = updateRoot + ".previous";
        string updateTransaction = updatePrevious + ".transaction-old";
        string updateOriginal = updateTransaction + ".original";
        string updateInstallId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(
            updateRoot,
            "2.0.0.0",
            updateInstallId,
            "prepared-recovery-current");
        CreateMinimalCodex(
            updatePrevious,
            "1.0.0.0",
            updateInstallId,
            "prepared-recovery-original-previous");
        DeploymentJournalRecord updateJournal = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Update,
            Phase = DeploymentTransactionPhase.UpdatePrepared,
            InstallRoot = updateRoot,
            InstallId = updateInstallId,
            HadCurrent = true,
            HadPrevious = true
        };
        updateJournal.UpdateOldPreviousCleanup =
            DeploymentJournal.CreatePreparedCleanupReceipt(
                updateJournal,
                updatePrevious,
                updateTransaction);
        DeploymentJournal.Write(updateJournal);
        Directory.Move(updatePrevious, updateTransaction);
        Directory.Move(updateTransaction, updateOriginal);
        CreateMinimalCodex(
            updateTransaction,
            "9.0.0.0",
            updateInstallId,
            "prepared-recovery-replacement-previous");
        File.Delete(InstallOwnership.GetMarkerPath(updateTransaction));
        File.Move(
            InstallOwnership.GetMarkerPath(updateOriginal),
            InstallOwnership.GetMarkerPath(updateTransaction));

        Exception updateFailure = CaptureFailure(delegate
        {
            RecoverDeployment(updateRoot);
        });
        Assert(updateFailure is InvalidDataException,
            "update Prepared 恢复接受了持有原 marker 的替换 transaction-old。");
        AssertVersionAt(
            updateTransaction,
            "prepared-recovery-replacement-previous",
            "9.0.0.0");
        AssertVersionAt(
            updateOriginal,
            "prepared-recovery-original-previous",
            "1.0.0.0");
        Assert(!Directory.Exists(updatePrevious) &&
            DeploymentJournal.Exists(updateRoot),
            "拒绝 update 替换目录后仍改写了拓扑或删除 journal。");

        string uninstallCaseRoot = NewCaseRoot("prepared-uninstall-recovery-replacement");
        string uninstallRoot = Path.Combine(uninstallCaseRoot, "CodexDesktop");
        string currentTombstone = uninstallRoot + ".uninstall-current";
        string currentOriginal = currentTombstone + ".original";
        string uninstallInstallId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(
            uninstallRoot,
            "2.0.0.0",
            uninstallInstallId,
            "prepared-uninstall-original-current");
        DeploymentJournalRecord uninstallJournal = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Uninstall,
            Phase = DeploymentTransactionPhase.UninstallPrepared,
            InstallRoot = uninstallRoot,
            InstallId = uninstallInstallId,
            HadCurrent = true,
            HadPrevious = false
        };
        uninstallJournal.UninstallCurrentCleanup =
            DeploymentJournal.CreatePreparedCleanupReceipt(
                uninstallJournal,
                uninstallRoot,
                currentTombstone);
        DeploymentJournal.Write(uninstallJournal);
        Directory.Move(uninstallRoot, currentTombstone);
        Directory.Move(currentTombstone, currentOriginal);
        CreateMinimalCodex(
            currentTombstone,
            "9.0.0.0",
            uninstallInstallId,
            "prepared-uninstall-replacement-current");
        File.Delete(InstallOwnership.GetMarkerPath(currentTombstone));
        File.Move(
            InstallOwnership.GetMarkerPath(currentOriginal),
            InstallOwnership.GetMarkerPath(currentTombstone));

        Exception uninstallFailure = CaptureFailure(delegate
        {
            RecoverDeployment(uninstallRoot);
        });
        Assert(uninstallFailure is InvalidDataException,
            "uninstall Prepared 恢复接受了持有原 marker 的替换 tombstone。");
        AssertVersionAt(
            currentTombstone,
            "prepared-uninstall-replacement-current",
            "9.0.0.0");
        AssertVersionAt(
            currentOriginal,
            "prepared-uninstall-original-current",
            "2.0.0.0");
        Assert(!Directory.Exists(uninstallRoot) &&
            DeploymentJournal.Exists(uninstallRoot),
            "拒绝 uninstall 替换目录后仍改写了拓扑或删除 journal。");
    }

    private static void TestDeploymentJournalRejectsCleanupReceiptPhaseMismatch()
    {
        string caseRoot = NewCaseRoot("deployment-phase-mismatch");
        string installId = Guid.NewGuid().ToString("N");

        string firstRoot = Path.Combine(caseRoot, "First");
        string firstPrevious = firstRoot + ".previous";
        CreateMinimalCodex(firstPrevious, "1.0.0.0", installId, "armed-too-early");
        DeploymentJournalRecord armedTooEarly = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Update,
            Phase = DeploymentTransactionPhase.UpdatePrepared,
            InstallRoot = firstRoot,
            InstallId = installId,
            HadPrevious = true
        };
        armedTooEarly.UpdateOldPreviousCleanup =
            DeploymentJournal.CreateCleanupReceipt(
                armedTooEarly,
                firstPrevious,
                firstPrevious + ".transaction-old");
        Assert(CaptureFailure(delegate
        {
            DeploymentJournal.Write(armedTooEarly);
        }) is InvalidDataException,
            "UpdatePrepared 接受了提前 Armed 的 receipt。");

        string secondRoot = Path.Combine(caseRoot, "Second");
        string secondTransaction = secondRoot + ".previous.transaction-old";
        CreateMinimalCodex(secondTransaction, "1.0.0.0", installId, "prepared-too-late");
        DeploymentJournalRecord preparedTooLate = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Update,
            Phase = DeploymentTransactionPhase.UpdateOldPreviousDetached,
            InstallRoot = secondRoot,
            InstallId = installId,
            HadPrevious = true
        };
        preparedTooLate.UpdateOldPreviousCleanup =
            DeploymentJournal.CreatePreparedCleanupReceipt(
                preparedTooLate,
                secondTransaction,
                secondTransaction);
        Assert(CaptureFailure(delegate
        {
            DeploymentJournal.Write(preparedTooLate);
        }) is InvalidDataException,
            "UpdateOldPreviousDetached 接受了仍为 Prepared 的 receipt。");

        string thirdRoot = Path.Combine(caseRoot, "Third");
        string thirdTombstone = thirdRoot + ".uninstall-current";
        CreateMinimalCodex(thirdTombstone, "1.0.0.0", installId, "uninstall-prepared-too-late");
        DeploymentJournalRecord uninstallPreparedTooLate = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Uninstall,
            Phase = DeploymentTransactionPhase.UninstallPayloadDetached,
            InstallRoot = thirdRoot,
            InstallId = installId,
            HadCurrent = true
        };
        uninstallPreparedTooLate.UninstallCurrentCleanup =
            DeploymentJournal.CreatePreparedCleanupReceipt(
                uninstallPreparedTooLate,
                thirdTombstone,
                thirdTombstone);
        Assert(CaptureFailure(delegate
        {
            DeploymentJournal.Write(uninstallPreparedTooLate);
        }) is InvalidDataException,
            "UninstallPayloadDetached 接受了仍为 Prepared 的 receipt。");

    }

    private static void TestDeploymentJournalRejectsMissingOrCoercedFields()
    {
        string caseRoot = NewCaseRoot("deployment-journal-strict-fields");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        DeploymentJournalRecord record = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Update,
            Phase = DeploymentTransactionPhase.UpdatePrepared,
            InstallRoot = installRoot,
            InstallId = Guid.NewGuid().ToString("N"),
            HadCurrent = false,
            HadPrevious = false,
            CreateIntegration = false
        };
        string[] requiredBooleanFields =
        {
            "HadCurrent",
            "HadPrevious",
            "CreateIntegration",
            "UninstallCurrentCleanupCompleted",
            "UninstallPreviousCleanupCompleted"
        };
        System.Web.Script.Serialization.JavaScriptSerializer serializer =
            new System.Web.Script.Serialization.JavaScriptSerializer();
        foreach (string field in requiredBooleanFields)
        {
            DeploymentJournal.Write(record);
            Dictionary<string, object> fields = serializer.Deserialize<Dictionary<string, object>>(
                File.ReadAllText(DeploymentJournal.GetPath(installRoot), Encoding.UTF8));
            fields.Remove(field);
            File.WriteAllText(
                DeploymentJournal.GetPath(installRoot),
                serializer.Serialize(fields),
                new UTF8Encoding(false));
            Assert(CaptureFailure(delegate
            {
                DeploymentJournal.Read(installRoot);
            }) is InvalidDataException,
                "部署 journal 接受了缺失的关键布尔字段：" + field);
        }

        DeploymentJournal.Write(record);
        Dictionary<string, object> coerced = serializer.Deserialize<Dictionary<string, object>>(
            File.ReadAllText(DeploymentJournal.GetPath(installRoot), Encoding.UTF8));
        coerced["HadCurrent"] = "false";
        File.WriteAllText(
            DeploymentJournal.GetPath(installRoot),
            serializer.Serialize(coerced),
            new UTF8Encoding(false));
        Assert(CaptureFailure(delegate
        {
            DeploymentJournal.Read(installRoot);
        }) is InvalidDataException,
            "部署 journal 接受了字符串形式的关键布尔字段。");
    }

    private static void TestRollbackTargetSelection()
    {
        string caseRoot = NewCaseRoot("rollback-target-selection");
        string cacheRoot = Path.Combine(caseRoot, "cache");
        Directory.CreateDirectory(cacheRoot);
        string cachedLower = CacheFileLock.GetPackagePath(
            cacheRoot,
            CodexMicrosoftStoreSource.PackageName,
            "26.707.8168.0",
            "x64");
        File.WriteAllBytes(cachedLower, new byte[] { 1 });
        string wrongArchitecture = CacheFileLock.GetPackagePath(
            cacheRoot,
            CodexMicrosoftStoreSource.PackageName,
            "26.715.2000.0",
            "arm64");
        File.WriteAllBytes(wrongArchitecture, new byte[] { 2 });

        RollbackPackageTarget previousLower = RollbackPackageSelector.Select(
            cacheRoot,
            new Version(26, 715, 4045, 0),
            new Version(26, 715, 2305, 0),
            "x64");
        Assert(previousLower != null &&
            previousLower.Kind == RollbackTargetKind.PreviousDirectory &&
            previousLower.Version == new Version(26, 715, 2305, 0),
            "较低的 .previous 没有优先于更早的缓存包。");

        RollbackPackageTarget cached = RollbackPackageSelector.Select(
            cacheRoot,
            new Version(26, 715, 2305, 0),
            new Version(26, 715, 4045, 0),
            "x64");
        Assert(cached != null &&
            cached.Kind == RollbackTargetKind.CachedPackage &&
            cached.Version == new Version(26, 707, 8168, 0) &&
            PathsEqual(cached.Path, cachedLower),
            ".previous 较新时没有选择缓存中最高的同架构低版本。");

        RollbackPackageTarget cachedWithoutPrevious = RollbackPackageSelector.Select(
            cacheRoot,
            new Version(26, 715, 2305, 0),
            null,
            "x64");
        Assert(cachedWithoutPrevious != null &&
            cachedWithoutPrevious.Kind == RollbackTargetKind.CachedPackage,
            "缺少 .previous 时没有开放缓存低版本回滚。");

        File.Delete(cachedLower);
        RollbackPackageTarget newerPreviousFallback = RollbackPackageSelector.Select(
            cacheRoot,
            new Version(26, 715, 2305, 0),
            new Version(26, 715, 4045, 0),
            "x64");
        Assert(newerPreviousFallback != null &&
            newerPreviousFallback.Kind == RollbackTargetKind.PreviousDirectory &&
            newerPreviousFallback.Version == new Version(26, 715, 4045, 0),
            "没有缓存低版本时破坏了原来的双向 .previous 切换。");
    }

    private static void TestRollbackPreflightFailureKeepsBothVersions()
    {
        string caseRoot = NewCaseRoot("rollback-preflight");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "2.0.0.0", installId, "current-original");
        CreateMinimalCodex(previousRoot, "1.0.0.0", installId, "previous-original");

        CodexPortableService service = CreateService(new List<string>());
        Exception failure = null;
        try
        {
            service.Rollback(installRoot, false, null);
        }
        catch (Exception exception)
        {
            failure = Unwrap(exception);
        }
        finally
        {
            service.Dispose();
        }

        bool rejectedBeforeSwap = failure is FileNotFoundException ||
            (failure is InvalidOperationException &&
             failure.Message.IndexOf("没有可回滚的上一版本", StringComparison.Ordinal) >= 0);
        Assert(rejectedBeforeSwap, "缺少 app.asar 应在切换目录前被判定为不可回滚，实际异常：" + (failure == null ? "无" : failure.ToString()));
        AssertVersionAt(installRoot, "current-original", "2.0.0.0");
        AssertVersionAt(previousRoot, "previous-original", "1.0.0.0");
        Assert(!Directory.Exists(installRoot + ".rollback-transaction"), "预检失败后不应留下 rollback-transaction。");
        Assert(!File.Exists(installRoot + ".rollback-recovery.state"), "预检失败后不应留下恢复状态文件。");
    }

    private static void TestUpdateRecoveryCurrentAndTransaction()
    {
        string caseRoot = NewCaseRoot("update-current-transaction");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = previousRoot + ".transaction-old";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "2.0.0.0", installId, "current-install");
        CreateMinimalCodex(transactionRoot, "1.0.0.0", installId, "detached-previous");

        RecoverDeployment(installRoot);

        AssertVersionAt(installRoot, "current-install", "2.0.0.0");
        AssertVersionAt(previousRoot, "detached-previous", "1.0.0.0");
        Assert(!Directory.Exists(transactionRoot), "更新事务目录应已消费。");
    }

    private static void TestUpdateRecoveryPreviousAndTransaction()
    {
        string caseRoot = NewCaseRoot("update-previous-transaction");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = previousRoot + ".transaction-old";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(previousRoot, "2.0.0.0", installId, "pre-update-current");
        CreateMinimalCodex(transactionRoot, "1.0.0.0", installId, "pre-update-previous");

        RecoverDeployment(installRoot);

        AssertVersionAt(installRoot, "pre-update-current", "2.0.0.0");
        AssertVersionAt(previousRoot, "pre-update-previous", "1.0.0.0");
        Assert(!Directory.Exists(transactionRoot), "更新事务目录应已消费。");
    }

    private static void TestUpdateRecoveryCommittedTopology()
    {
        string caseRoot = NewCaseRoot("update-committed");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = previousRoot + ".transaction-old";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "3.0.0.0", installId, "new-current");
        CreateMinimalCodex(previousRoot, "2.0.0.0", installId, "new-previous");
        CreateMinimalCodex(transactionRoot, "1.0.0.0", installId, "obsolete-previous");

        RecoverDeployment(installRoot);

        AssertVersionAt(installRoot, "new-current", "3.0.0.0");
        AssertVersionAt(previousRoot, "new-previous", "2.0.0.0");
        Assert(!Directory.Exists(transactionRoot), "已提交更新的旧回滚备份应被清理。");
    }

    private static void TestUpdateRecoverySoleTransaction()
    {
        string caseRoot = NewCaseRoot("update-sole-transaction");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string transactionRoot = installRoot + ".previous.transaction-old";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(transactionRoot, "1.0.0.0", installId, "sole-survivor");

        RecoverDeployment(installRoot);

        AssertVersionAt(installRoot, "sole-survivor", "1.0.0.0");
        Assert(!Directory.Exists(installRoot + ".previous"), "只有一个幸存版本时不应把它留作不可启动的 previous。");
        Assert(!Directory.Exists(transactionRoot), "更新事务目录应已消费。");
    }

    private static void TestPreviousOnlyNormalizationBeforeUpdate()
    {
        string caseRoot = NewCaseRoot("update-previous-only-normalization");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(previousRoot, "2.0.0.0", installId, "only-surviving-version");

        using (DeploymentEngineScope scope = new DeploymentEngineScope(new List<string>()))
        {
            scope.Engine.PrepareInstallTopology(installRoot, null);
        }

        AssertVersionAt(installRoot, "only-surviving-version", "2.0.0.0");
        Assert(!Directory.Exists(previousRoot), "previous-only 规范化后旧路径仍然存在。");
    }

    private static void TestUpdateJournalRecoveryBeforeCommit()
    {
        string caseRoot = NewCaseRoot("update-journal-before-commit");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = previousRoot + ".transaction-old";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "2.0.0.0", installId, "current-before-update");
        CreateMinimalCodex(transactionRoot, "1.0.0.0", installId, "previous-before-update");
        CreateDeploymentJournal(
            "Update",
            "UpdateOldPreviousDetached",
            installRoot,
            installId,
            true,
            true);

        RecoverDeployment(installRoot);

        AssertVersionAt(installRoot, "current-before-update", "2.0.0.0");
        AssertVersionAt(previousRoot, "previous-before-update", "1.0.0.0");
        Assert(!Directory.Exists(transactionRoot), "提交前更新恢复后仍残留 transaction-old。");
        Assert(!File.Exists(installRoot + ".deployment-journal.json"), "提交前更新恢复后仍残留 journal。");
    }

    private static void TestUpdateJournalRecoveryAfterActivation()
    {
        string caseRoot = NewCaseRoot("update-journal-after-activation");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = previousRoot + ".transaction-old";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "3.0.0.0", installId, "new-current");
        CreateMinimalCodex(previousRoot, "2.0.0.0", installId, "old-current");
        CreateMinimalCodex(transactionRoot, "1.0.0.0", installId, "old-previous");
        CreateDeploymentJournal(
            "Update",
            "UpdateCurrentDetached",
            installRoot,
            installId,
            true,
            true);

        RecoverDeployment(installRoot);

        AssertVersionAt(installRoot, "new-current", "3.0.0.0");
        AssertVersionAt(previousRoot, "old-current", "2.0.0.0");
        Assert(!Directory.Exists(transactionRoot), "已激活更新恢复后没有清理旧 previous。");
        Assert(!File.Exists(installRoot + ".deployment-journal.json"), "已激活更新恢复后仍残留 journal。");
    }

    private static void TestFirstInstallPreparedJournalWithoutPayloadClears()
    {
        string caseRoot = NewCaseRoot("first-install-empty-journal");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        DeploymentJournal.Write(new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Update,
            Phase = DeploymentTransactionPhase.UpdatePrepared,
            InstallRoot = installRoot,
            InstallId = Guid.NewGuid().ToString("N"),
            HadCurrent = false,
            HadPrevious = false
        });

        RecoverDeployment(installRoot);

        Assert(!DeploymentJournal.Exists(installRoot) &&
            !Directory.Exists(installRoot) &&
            !Directory.Exists(installRoot + ".previous"),
            "首次安装在 payload 激活前留下的空 journal 没有安全清理。");
    }

    private static void TestDeploymentJournalRejectsUndefinedPhase()
    {
        string caseRoot = NewCaseRoot("deployment-journal-undefined-phase");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        DeploymentJournalRecord journal = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Update,
            Phase = (DeploymentTransactionPhase)15,
            InstallRoot = installRoot,
            InstallId = Guid.NewGuid().ToString("N"),
            HadCurrent = false,
            HadPrevious = false
        };

        Exception failure = CaptureFailure(delegate
        {
            DeploymentJournal.Write(journal);
        });
        Assert(failure is InvalidDataException,
            "部署 journal 接受了数值区间内未定义的事务阶段。");
        Assert(!File.Exists(DeploymentJournal.GetPath(installRoot)),
            "拒绝未定义阶段后仍写出了部署 journal。");
    }

    private static void TestUpdateFailureAfterCommitCompletesForward()
    {
        string caseRoot = NewCaseRoot("update-failure-after-commit");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = previousRoot + ".transaction-old";
        string workRoot = Path.Combine(caseRoot, ".cpm-fault-injection");
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "3.0.0.0", installId, "new-current");
        CreateMinimalCodex(previousRoot, "2.0.0.0", installId, "old-current");
        CreateMinimalCodex(transactionRoot, "1.0.0.0", installId, "obsolete-previous");
        Directory.CreateDirectory(workRoot);

        DeploymentJournalRecord journal = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Update,
            Phase = DeploymentTransactionPhase.UpdatePayloadActivated,
            InstallRoot = installRoot,
            InstallId = installId,
            HadCurrent = true,
            HadPrevious = true
        };
        journal.UpdateOldPreviousCleanup = DeploymentJournal.CreateCleanupReceipt(
            journal,
            transactionRoot,
            transactionRoot);
        DeploymentJournal.Write(journal);

        bool committed;
        List<string> logs = new List<string>();
        using (DeploymentEngineScope scope = new DeploymentEngineScope(logs))
        {
            committed = scope.Engine.RecoverFailedUpdateSwitch(
                journal,
                caseRoot,
                workRoot,
                true,
                true,
                true);
        }

        Assert(committed, "激活后的故障恢复没有识别更新提交点。");
        AssertVersionAt(installRoot, "new-current", "3.0.0.0");
        AssertVersionAt(previousRoot, "old-current", "2.0.0.0");
        Assert(!Directory.Exists(transactionRoot), "提交后向前恢复没有清理旧 previous：" + string.Join(" | ", logs.ToArray()));
        Assert(!Directory.Exists(Path.Combine(workRoot, "failed-new")), "提交后的新版本被错误移入临时工作目录。");
        Assert(!File.Exists(installRoot + ".deployment-journal.json"), "提交后向前恢复仍残留 journal。");
    }

    private static void TestUpdateCleanupReceiptSurvivesPartialDeletion()
    {
        string caseRoot = NewCaseRoot("update-cleanup-partial");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = previousRoot + ".transaction-old";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "3.0.0.0", installId, "new-current");
        CreateMinimalCodex(previousRoot, "2.0.0.0", installId, "old-current");
        CreateMinimalCodex(transactionRoot, "1.0.0.0", installId, "obsolete-previous");

        DeploymentJournalRecord journal = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Update,
            Phase = DeploymentTransactionPhase.UpdateExternalStateUpdated,
            InstallRoot = installRoot,
            InstallId = installId,
            HadCurrent = true,
            HadPrevious = true
        };
        journal.UpdateOldPreviousCleanup = DeploymentJournal.CreateCleanupReceipt(
            journal,
            transactionRoot,
            transactionRoot);
        DeploymentJournal.Write(journal);
        DeleteCleanupOwnershipEvidence(transactionRoot);

        RecoverDeployment(installRoot);

        AssertVersionAt(installRoot, "new-current", "3.0.0.0");
        AssertVersionAt(previousRoot, "old-current", "2.0.0.0");
        Assert(!Directory.Exists(transactionRoot), "部分删除后的 transaction-old 没有继续清理。");
        Assert(!File.Exists(installRoot + ".deployment-journal.json"), "更新清理完成后仍残留 journal。");
    }

    private static void TestUpdateCleanupReceiptRejectsReplacement()
    {
        string caseRoot = NewCaseRoot("update-cleanup-replaced");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = previousRoot + ".transaction-old";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "3.0.0.0", installId, "new-current");
        CreateMinimalCodex(previousRoot, "2.0.0.0", installId, "old-current");
        CreateMinimalCodex(transactionRoot, "1.0.0.0", installId, "original-cleanup-root");

        DeploymentJournalRecord journal = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Update,
            Phase = DeploymentTransactionPhase.UpdateExternalStateUpdated,
            InstallRoot = installRoot,
            InstallId = installId,
            HadCurrent = true,
            HadPrevious = true
        };
        journal.UpdateOldPreviousCleanup = DeploymentJournal.CreateCleanupReceipt(
            journal,
            transactionRoot,
            transactionRoot);
        DeploymentJournal.Write(journal);

        Directory.Delete(transactionRoot, true);
        CreateMinimalCodex(transactionRoot, "1.0.0.0", installId, "replacement-root");

        Exception failure = CaptureFailure(delegate { RecoverDeployment(installRoot); });

        Assert(failure is InvalidDataException, "替换清理目录没有被 receipt 文件 ID 拒绝。");
        AssertVersionAt(transactionRoot, "replacement-root", "1.0.0.0");
        Assert(File.Exists(installRoot + ".deployment-journal.json"), "拒绝替换目录后不应删除 journal。");
    }

    private static void TestCommittedUpdateCleanupPendingKeepsCurrentStatus()
    {
        string caseRoot = NewCaseRoot("update-cleanup-pending-status");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = previousRoot + ".transaction-old";
        string installId = Guid.NewGuid().ToString("N");
        CreateRunnableCodex(installRoot, "3.0.0.0", installId, "new-current");
        CreateRunnableCodex(previousRoot, "2.0.0.0", installId, "old-current");
        CreateRunnableCodex(transactionRoot, "1.0.0.0", installId, "obsolete-previous");

        DeploymentJournalRecord journal = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Update,
            Phase = DeploymentTransactionPhase.UpdateExternalStateUpdated,
            InstallRoot = installRoot,
            InstallId = installId,
            HadCurrent = true,
            HadPrevious = true
        };
        journal.UpdateOldPreviousCleanup = DeploymentJournal.CreateCleanupReceipt(
            journal,
            transactionRoot,
            transactionRoot);
        DeploymentJournal.Write(journal);

        List<string> logs = new List<string>();
        using (CodexPortableService service = CreateService(logs))
        {
            PortableLocalStatus pending;
            using (FileStream held = new FileStream(
                Path.Combine(transactionRoot, "identity.txt"),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                pending = service.GetLocalStatus(installRoot);
            }

            Assert(pending.Error == null &&
                pending.PortableVersion == new Version(3, 0, 0, 0),
                "旧备份清理失败后没有继续返回有效当前版本：" + pending.Error);
            Assert(pending.OldBackupCleanupPending &&
                !pending.UninstallDirectoryCleanupPending,
                "已提交更新的清理待办没有进入结构化本地状态。");
            Assert(Directory.Exists(transactionRoot) &&
                File.Exists(DeploymentJournal.GetPath(installRoot)),
                "清理失败后没有保留可重试的旧备份和 journal。");

            PortableLocalStatus stillPending = service.GetLocalStatus(installRoot);
            Assert(stillPending.Error == null &&
                stillPending.PortableVersion == new Version(3, 0, 0, 0) &&
                stillPending.OldBackupCleanupPending &&
                Directory.Exists(transactionRoot) &&
                DeploymentJournal.Exists(installRoot),
                "普通状态读取在释放占用后同步执行了已提交清理。");

            Assert(service.CompletePendingDeploymentCleanup(installRoot),
                "显式后台恢复入口没有完成已提交更新清理：" +
                string.Join(" | ", logs.ToArray()));
            PortableLocalStatus completed = service.GetLocalStatus(installRoot);
            Assert(completed.Error == null &&
                completed.PortableVersion == new Version(3, 0, 0, 0) &&
                !completed.OldBackupCleanupPending,
                "后台清理后没有保持当前版本可用：" +
                completed.Error + "；" + string.Join(" | ", logs.ToArray()));
        }

        Assert(!Directory.Exists(transactionRoot), "后台恢复后旧回滚备份仍未清理。");
        Assert(!File.Exists(DeploymentJournal.GetPath(installRoot)), "清理完成后仍残留更新 journal。");
    }

    private static void TestRollbackRecoveryPreviousAndTransaction()
    {
        string caseRoot = NewCaseRoot("rollback-previous-transaction");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = installRoot + ".rollback-transaction";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(previousRoot, "1.0.0.0", installId, "rollback-target");
        CreateMinimalCodex(transactionRoot, "2.0.0.0", installId, "original-current");

        RecoverDeployment(installRoot);

        AssertVersionAt(installRoot, "rollback-target", "1.0.0.0");
        AssertVersionAt(previousRoot, "original-current", "2.0.0.0");
        Assert(!Directory.Exists(transactionRoot), "回滚事务目录应已消费。");
    }

    private static void TestRollbackRecoveryCurrentAndTransaction()
    {
        string caseRoot = NewCaseRoot("rollback-current-transaction");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = installRoot + ".rollback-transaction";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "1.0.0.0", installId, "rollback-target");
        CreateMinimalCodex(transactionRoot, "2.0.0.0", installId, "original-current");

        RecoverDeployment(installRoot);

        AssertVersionAt(installRoot, "rollback-target", "1.0.0.0");
        AssertVersionAt(previousRoot, "original-current", "2.0.0.0");
        Assert(!Directory.Exists(transactionRoot), "回滚事务目录应已消费。");
    }

    private static void TestRollbackRecoverySoleTransaction()
    {
        string caseRoot = NewCaseRoot("rollback-sole-transaction");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string transactionRoot = installRoot + ".rollback-transaction";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(transactionRoot, "2.0.0.0", installId, "sole-current");

        RecoverDeployment(installRoot);

        AssertVersionAt(installRoot, "sole-current", "2.0.0.0");
        Assert(!Directory.Exists(transactionRoot), "回滚事务目录应已消费。");
    }

    private static void TestRollbackRecoveryRejectsAmbiguousTopology()
    {
        string caseRoot = NewCaseRoot("rollback-ambiguous");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = installRoot + ".rollback-transaction";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "3.0.0.0", installId, "ambiguous-current");
        CreateMinimalCodex(previousRoot, "2.0.0.0", installId, "ambiguous-previous");
        CreateMinimalCodex(transactionRoot, "1.0.0.0", installId, "ambiguous-transaction");

        Exception failure = null;
        try
        {
            RecoverDeployment(installRoot);
        }
        catch (Exception exception)
        {
            failure = Unwrap(exception);
        }

        Assert(failure is IOException, "三目录回滚拓扑应拒绝自动猜测。");
        AssertVersionAt(installRoot, "ambiguous-current", "3.0.0.0");
        AssertVersionAt(previousRoot, "ambiguous-previous", "2.0.0.0");
        AssertVersionAt(transactionRoot, "ambiguous-transaction", "1.0.0.0");
    }

    private static void TestRollbackReversalCurrentMoved()
    {
        string caseRoot = NewCaseRoot("rollback-reversal-current-moved");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = installRoot + ".rollback-transaction";
        string statePath = installRoot + ".rollback-recovery.state";
        string installId = Guid.NewGuid().ToString("N");

        // 原 current 已移动到 transaction，原 previous 尚未移动。
        CreateMinimalCodex(previousRoot, "1.0.0.0", installId, "original-previous");
        CreateMinimalCodex(transactionRoot, "2.0.0.0", installId, "original-current");
        File.WriteAllText(statePath, "restore-current-moved\r\n", Encoding.ASCII);

        RecoverDeployment(installRoot);

        AssertRollbackOriginalTopology(installRoot, previousRoot, transactionRoot, statePath);
    }

    private static void TestRollbackReversalPreviousMoved()
    {
        string caseRoot = NewCaseRoot("rollback-reversal-previous-moved");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = installRoot + ".rollback-transaction";
        string statePath = installRoot + ".rollback-recovery.state";
        string installId = Guid.NewGuid().ToString("N");

        // 原 previous 已成为 current，原 current 仍在 transaction。
        CreateMinimalCodex(installRoot, "1.0.0.0", installId, "original-previous");
        CreateMinimalCodex(transactionRoot, "2.0.0.0", installId, "original-current");
        File.WriteAllText(statePath, "restore-previous-moved-step1\r\n", Encoding.ASCII);

        RecoverDeployment(installRoot);

        AssertRollbackOriginalTopology(installRoot, previousRoot, transactionRoot, statePath);
    }

    private static void TestRollbackReversalCompletedSwap()
    {
        string caseRoot = NewCaseRoot("rollback-reversal-completed-swap");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = installRoot + ".rollback-transaction";
        string statePath = installRoot + ".rollback-recovery.state";
        string installId = Guid.NewGuid().ToString("N");

        // 目录交换已经完成：原 previous 在 current，原 current 在 previous。
        CreateMinimalCodex(installRoot, "1.0.0.0", installId, "original-previous");
        CreateMinimalCodex(previousRoot, "2.0.0.0", installId, "original-current");
        File.WriteAllText(statePath, "restore-swapped-step1\r\n", Encoding.ASCII);

        RecoverDeployment(installRoot);

        AssertRollbackOriginalTopology(installRoot, previousRoot, transactionRoot, statePath);
    }

    private static void TestRollbackJournalRecoveryAfterCurrentDetached()
    {
        string caseRoot = NewCaseRoot("rollback-journal-current-detached");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = installRoot + ".rollback-transaction";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(previousRoot, "1.0.0.0", installId, "rollback-target");
        CreateMinimalCodex(transactionRoot, "2.0.0.0", installId, "rollback-original");
        CreateDeploymentJournal(
            "Rollback",
            "RollbackPrepared",
            installRoot,
            installId,
            true,
            true);

        RecoverDeployment(installRoot);

        AssertVersionAt(installRoot, "rollback-target", "1.0.0.0");
        AssertVersionAt(previousRoot, "rollback-original", "2.0.0.0");
        Assert(!Directory.Exists(transactionRoot), "journal 回滚完成后仍残留 transaction。");
        Assert(!File.Exists(installRoot + ".deployment-journal.json"), "journal 回滚完成后仍残留状态文件。");
    }

    private static void TestRollbackJournalRestorationFromSwapped()
    {
        string caseRoot = NewCaseRoot("rollback-journal-restore-swapped");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = installRoot + ".rollback-transaction";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "1.0.0.0", installId, "rollback-target");
        CreateMinimalCodex(previousRoot, "2.0.0.0", installId, "rollback-original");
        CreateDeploymentJournal(
            "Rollback",
            "RollbackRestoreSwapped",
            installRoot,
            installId,
            true,
            true);

        RecoverDeployment(installRoot);

        AssertVersionAt(installRoot, "rollback-original", "2.0.0.0");
        AssertVersionAt(previousRoot, "rollback-target", "1.0.0.0");
        Assert(!Directory.Exists(transactionRoot), "journal 反向恢复后仍残留 transaction。");
        Assert(!File.Exists(installRoot + ".deployment-journal.json"), "journal 反向恢复后仍残留状态文件。");
    }

    private static void TestUnmanagedAdoptionRequiresExplicitApproval()
    {
        string caseRoot = NewCaseRoot("unmanaged-adoption-approval");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        CreateMinimalCodex(installRoot, "1.0.0.0", Guid.NewGuid().ToString("N"), "unmanaged-install");
        string markerPath = Path.Combine(installRoot, ".codex-portable-manager.json");
        File.Delete(markerPath);

        CodexPortableService service = CreateService(new List<string>());
        try
        {
            Exception rejected = CaptureFailure(delegate
            {
                service.UninstallPortable(installRoot);
            });
            Assert(rejected is InvalidOperationException, "未批准的无标记目录接管没有被拒绝。");
            Assert(Directory.Exists(installRoot) && !File.Exists(markerPath), "拒绝接管时无标记目录或 marker 被修改。");

            LegacyAdoptionApproval approval = LegacyAdoptionApproval.Create(installRoot);
            service.UninstallPortable(installRoot, approval);
            Assert(!Directory.Exists(installRoot), "显式批准后无标记目录没有完成卸载。");
            Assert(!File.Exists(installRoot + ".deployment-journal.json"), "成功卸载后仍残留 journal。");
        }
        finally
        {
            service.Dispose();
        }
    }

    private static void TestUninstallRecoveryBeforeCommit()
    {
        string caseRoot = NewCaseRoot("uninstall-recovery-before-commit");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string previousTombstone = installRoot + ".uninstall-previous";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "2.0.0.0", installId, "current-before-uninstall");
        CreateMinimalCodex(previousRoot, "1.0.0.0", installId, "previous-before-uninstall");
        Directory.Move(previousRoot, previousTombstone);
        CreateUninstallJournal(
            installRoot,
            installId,
            "UninstallPreviousDetached",
            true,
            true);

        RecoverDeployment(installRoot);

        AssertVersionAt(installRoot, "current-before-uninstall", "2.0.0.0");
        AssertVersionAt(previousRoot, "previous-before-uninstall", "1.0.0.0");
        Assert(!Directory.Exists(previousTombstone), "提交前恢复后仍残留 previous tombstone。");
        Assert(!File.Exists(installRoot + ".deployment-journal.json"), "提交前恢复后仍残留 journal。");
    }

    private static void TestUninstallRecoveryAfterMoveBeforeCommitWriteRollsBack()
    {
        string caseRoot = NewCaseRoot("uninstall-recovery-after-commit");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string currentTombstone = installRoot + ".uninstall-current";
        string previousTombstone = installRoot + ".uninstall-previous";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "2.0.0.0", installId, "committed-current");
        CreateMinimalCodex(previousRoot, "1.0.0.0", installId, "committed-previous");
        Directory.Move(previousRoot, previousTombstone);
        Directory.Move(installRoot, currentTombstone);
        CreateUninstallJournal(
            installRoot,
            installId,
            "UninstallPreviousDetached",
            true,
            true);

        DeploymentJournalRecord pending = DeploymentJournal.Read(installRoot);
        Assert(pending.UninstallPreviousCleanup.Phase ==
            DeploymentCleanupReceiptPhase.Armed &&
            pending.UninstallCurrentCleanup.Phase ==
                DeploymentCleanupReceiptPhase.Prepared,
            "提交阶段未落盘时卸载 receipt 没有保持 previous Armed/current Prepared。" );

        RecoverDeployment(installRoot);

        AssertVersionAt(installRoot, "committed-current", "2.0.0.0");
        AssertVersionAt(previousRoot, "committed-previous", "1.0.0.0");
        Assert(!Directory.Exists(currentTombstone) &&
            !Directory.Exists(previousTombstone),
            "提交阶段未落盘的卸载恢复后仍残留 tombstone。");
        Assert(!File.Exists(installRoot + ".deployment-journal.json"),
            "提交阶段未落盘的卸载回滚后仍残留 journal。");
    }

    private static void TestUninstallCleanupReceiptSurvivesPartialDeletion()
    {
        string caseRoot = NewCaseRoot("uninstall-cleanup-partial");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string currentTombstone = installRoot + ".uninstall-current";
        string previousTombstone = installRoot + ".uninstall-previous";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "2.0.0.0", installId, "uninstall-current");
        CreateMinimalCodex(previousRoot, "1.0.0.0", installId, "uninstall-previous");

        DeploymentJournalRecord journal = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Uninstall,
            Phase = DeploymentTransactionPhase.UninstallExternalStateCleaned,
            InstallRoot = installRoot,
            InstallId = installId,
            HadCurrent = true,
            HadPrevious = true
        };
        journal.UninstallCurrentCleanup = DeploymentJournal.CreateCleanupReceipt(
            journal,
            installRoot,
            currentTombstone);
        journal.UninstallPreviousCleanup = DeploymentJournal.CreateCleanupReceipt(
            journal,
            previousRoot,
            previousTombstone);
        DeploymentJournal.Write(journal);
        Directory.Move(previousRoot, previousTombstone);
        Directory.Move(installRoot, currentTombstone);
        DeleteCleanupOwnershipEvidence(currentTombstone);
        DeleteCleanupOwnershipEvidence(previousTombstone);

        RecoverDeployment(installRoot);

        Assert(!Directory.Exists(installRoot) && !Directory.Exists(previousRoot), "已提交卸载错误恢复了正式槽位。");
        Assert(!Directory.Exists(currentTombstone) && !Directory.Exists(previousTombstone), "部分删除后的 tombstone 没有继续清理。");
        Assert(!File.Exists(installRoot + ".deployment-journal.json"), "卸载清理完成后仍残留 journal。");
    }

    private static void TestMissingCleanupReceiptNeverDeletesAppearingDirectory()
    {
        string caseRoot = NewCaseRoot("missing-receipt-appearing-directory");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string currentTombstone = installRoot + ".uninstall-current";
        string unexpectedPreviousTombstone = installRoot + ".uninstall-previous";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(
            currentTombstone,
            "2.0.0.0",
            installId,
            "missing-receipt-current");
        DeploymentJournalRecord journal = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = DeploymentOperationKind.Uninstall,
            Phase = DeploymentTransactionPhase.UninstallExternalStateCleaned,
            InstallRoot = installRoot,
            InstallId = installId,
            HadCurrent = true,
            HadPrevious = false
        };
        journal.UninstallCurrentCleanup = DeploymentJournal.CreateCleanupReceipt(
            journal,
            currentTombstone,
            currentTombstone);
        DeploymentJournal.Write(journal);

        string sentinel = Path.Combine(unexpectedPreviousTombstone, "sentinel.txt");
        try
        {
            DeploymentEngine.MissingCleanupReceiptObservedForTest = path =>
            {
                if (PathsEqual(path, unexpectedPreviousTombstone))
                {
                    Directory.CreateDirectory(unexpectedPreviousTombstone);
                    File.WriteAllText(sentinel, "必须保留", Encoding.UTF8);
                }
            };
            RecoverDeployment(installRoot);
        }
        finally
        {
            DeploymentEngine.MissingCleanupReceiptObservedForTest = null;
        }

        Assert(File.Exists(sentinel),
            "无 receipt 的清理槽删除了首次缺失探测后出现的新目录。");
        Assert(!DeploymentJournal.Exists(installRoot),
            "已授权 tombstone 清理完成后仍残留 deployment journal。");
    }

    private static void TestPreviousOnlyUninstall()
    {
        string caseRoot = NewCaseRoot("previous-only-uninstall");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        CreateMinimalCodex(previousRoot, "1.0.0.0", Guid.NewGuid().ToString("N"), "previous-only");

        CodexPortableService service = CreateService(new List<string>());
        try
        {
            service.UninstallPortable(installRoot);
        }
        finally
        {
            service.Dispose();
        }

        Assert(!Directory.Exists(previousRoot), "previous-only 卸载后回滚槽位仍然存在。");
        Assert(!Directory.Exists(installRoot + ".uninstall-previous"), "previous-only 卸载后仍残留 tombstone。");
        Assert(!File.Exists(installRoot + ".deployment-journal.json"), "previous-only 卸载后仍残留 journal。");
    }

    private static void TestDeferredUninstallDetachesBeforeCleanup()
    {
        string caseRoot = NewCaseRoot("deferred-uninstall");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string currentTombstone = installRoot + ".uninstall-current";
        string previousTombstone = installRoot + ".uninstall-previous";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "2.0.0.0", installId, "deferred-current");
        CreateMinimalCodex(previousRoot, "1.0.0.0", installId, "deferred-previous");

        List<string> logs = new List<string>();
        using (CodexPortableService service = CreateService(logs))
        {
            UninstallResult result = service.DetachPortableForUninstall(
                installRoot,
                null);

            Assert(result.DirectoryCleanupPending,
                "逻辑卸载没有报告独立后台目录清理待办。");
            Assert(!Directory.Exists(installRoot) && !Directory.Exists(previousRoot),
                "逻辑卸载返回时 current/previous 仍占用活动槽。");
            Assert(Directory.Exists(currentTombstone) && Directory.Exists(previousTombstone),
                "逻辑卸载在后台清理前提前丢失了 tombstone。");
            Assert(DeploymentJournal.Exists(installRoot),
                "逻辑卸载没有保留可恢复的 deployment journal。");

            bool complete = service.CompletePendingUninstallCleanup(installRoot);
            Assert(complete,
                "显式恢复没有完成卸载后台清理：" +
                string.Join(" | ", logs.ToArray()));
        }

        Assert(!Directory.Exists(currentTombstone) && !Directory.Exists(previousTombstone),
            "卸载后台清理后仍残留 tombstone。");
        Assert(!DeploymentJournal.Exists(installRoot),
            "卸载后台清理后仍残留 deployment journal。");
    }

    private static void TestUninstallCleanupWorkerProcess()
    {
        string caseRoot = NewCaseRoot("uninstall-cleanup-worker");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string currentTombstone = installRoot + ".uninstall-current";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "2.0.0.0", installId, "worker-current");

        List<string> logs = new List<string>();
        Task<int> cleanupTask;
        using (CodexPortableService service = CreateService(logs))
        {
            UninstallResult result = service.DetachPortableForUninstall(
                installRoot,
                null);
            Assert(result.DirectoryCleanupPending && Directory.Exists(currentTombstone),
                "后台清理进程测试没有准备好已提交的卸载事务。");
            cleanupTask = service.StartUninstallCleanupAsync(installRoot);
            Assert(cleanupTask.Wait(30000),
                "独立卸载清理进程在 30 秒内没有退出。");
        }

        int exitCode = cleanupTask.GetAwaiter().GetResult();
        Assert(exitCode == 0,
            "独立卸载清理进程返回异常退出代码：" + exitCode + "。日志：" +
            string.Join(" | ", logs.ToArray()));

        Assert(!Directory.Exists(currentTombstone),
            "独立卸载清理进程退出后仍残留 current tombstone。");
        Assert(!DeploymentJournal.Exists(installRoot),
            "独立卸载清理进程退出后仍残留 deployment journal。");
    }

    private static void TestPostDeploymentCleanupWorkerProcess()
    {
        string caseRoot = NewCaseRoot("post-deployment-cleanup-worker");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string previousRoot = installRoot + ".previous";
        string transactionRoot = previousRoot + ".transaction-old";
        string installId = Guid.NewGuid().ToString("N");
        CreateMinimalCodex(installRoot, "3.0.0.0", installId, "worker-current");
        CreateMinimalCodex(previousRoot, "2.0.0.0", installId, "worker-previous");
        CreateMinimalCodex(transactionRoot, "1.0.0.0", installId, "worker-obsolete");
        CreateDeploymentJournal(
            "Update",
            "UpdateExternalStateUpdated",
            installRoot,
            installId,
            true,
            true);

        string testRunnerPath = Assembly.GetExecutingAssembly().Location;
        using (OperationFileLock held = OperationFileLock.Acquire(installRoot))
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = testRunnerPath,
                Arguments = "--regression-child --start-post-deployment-cleanup-and-exit " +
                    QuoteArgument(managerPath) + " " + QuoteArgument(installRoot),
                WorkingDirectory = Path.GetDirectoryName(testRunnerPath),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (Process launcher = Process.Start(startInfo))
            {
                Assert(launcher != null, "无法启动部署后清理派发父进程。");
                Assert(launcher.WaitForExit(10000),
                    "部署后清理派发父进程在 10 秒内没有退出。");
                Assert(launcher.ExitCode == 0,
                    "部署后清理派发父进程异常退出：" + launcher.ExitCode + "。");
                Assert(Directory.Exists(transactionRoot) && DeploymentJournal.Exists(installRoot),
                    "父进程退出前，受操作锁阻塞的清理子进程错误完成了事务。");
            }
        }

        Stopwatch cleanupWait = Stopwatch.StartNew();
        while (DeploymentJournal.Exists(installRoot) &&
            cleanupWait.Elapsed < TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(50);
        }
        AssertVersionAt(installRoot, "worker-current", "3.0.0.0");
        AssertVersionAt(previousRoot, "worker-previous", "2.0.0.0");
        Assert(!Directory.Exists(transactionRoot),
            "派发父进程退出后，独立清理进程仍残留旧回滚事务目录。");
        Assert(!DeploymentJournal.Exists(installRoot),
            "派发父进程退出后，独立清理进程仍残留 deployment journal。");
    }

    private static void CreateUninstallJournal(
        string installRoot,
        string installId,
        string phaseName,
        bool hadCurrent,
        bool hadPrevious)
    {
        CreateDeploymentJournal(
            "Uninstall",
            phaseName,
            installRoot,
            installId,
            hadCurrent,
            hadPrevious);
    }

    private static void CreateDeploymentJournal(
        string operationName,
        string phaseName,
        string installRoot,
        string installId,
        bool hadCurrent,
        bool hadPrevious)
    {
        DeploymentJournalRecord record = new DeploymentJournalRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = (DeploymentOperationKind)Enum.Parse(typeof(DeploymentOperationKind), operationName),
            Phase = (DeploymentTransactionPhase)Enum.Parse(typeof(DeploymentTransactionPhase), phaseName),
            InstallRoot = installRoot,
            InstallId = installId,
            HadCurrent = hadCurrent,
            HadPrevious = hadPrevious
        };
        if (record.Operation == DeploymentOperationKind.Update && hadPrevious)
        {
            string cleanupRoot = installRoot + ".previous.transaction-old";
            string sourceRoot = Directory.Exists(cleanupRoot)
                ? cleanupRoot
                : installRoot + ".previous";
            record.UpdateOldPreviousCleanup = DeploymentJournal.CreateCleanupReceipt(
                record,
                sourceRoot,
                cleanupRoot);
        }
        else if (record.Operation == DeploymentOperationKind.Uninstall)
        {
            if (hadCurrent)
            {
                string cleanupRoot = installRoot + ".uninstall-current";
                record.UninstallCurrentCleanup =
                    record.Phase >= DeploymentTransactionPhase.UninstallPayloadDetached
                        ? DeploymentJournal.CreateCleanupReceipt(
                            record,
                            cleanupRoot,
                            cleanupRoot)
                        : DeploymentJournal.CreatePreparedCleanupReceipt(
                            record,
                            Directory.Exists(installRoot)
                                ? installRoot
                                : cleanupRoot,
                            cleanupRoot);
            }
            if (hadPrevious)
            {
                string cleanupRoot = installRoot + ".uninstall-previous";
                record.UninstallPreviousCleanup =
                    record.Phase >= DeploymentTransactionPhase.UninstallPreviousDetached
                        ? DeploymentJournal.CreateCleanupReceipt(
                            record,
                            cleanupRoot,
                            cleanupRoot)
                        : DeploymentJournal.CreatePreparedCleanupReceipt(
                            record,
                            Directory.Exists(installRoot + ".previous")
                                ? installRoot + ".previous"
                                : cleanupRoot,
                            cleanupRoot);
            }
        }
        DeploymentJournal.Write(record);
    }

    private static void DeleteCleanupOwnershipEvidence(string root)
    {
        string[] paths =
        {
            Path.Combine(root, ".codex-portable-manager.json"),
            Path.Combine(root, "AppxManifest.xml"),
            Path.Combine(root, "app", "Codex.exe")
        };
        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void AssertRollbackOriginalTopology(
        string installRoot,
        string previousRoot,
        string transactionRoot,
        string statePath)
    {
        AssertVersionAt(installRoot, "original-current", "2.0.0.0");
        AssertVersionAt(previousRoot, "original-previous", "1.0.0.0");
        Assert(!Directory.Exists(transactionRoot), "反向恢复后不应残留 rollback-transaction。");
        Assert(!File.Exists(statePath), "反向恢复完成后应删除恢复状态文件。");
    }
}
}
