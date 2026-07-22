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
        private static string NormalizeDirectoryPrefix(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        private static bool IsPathUnderPrefix(string filePath, string normalizedRoot)
        {
            string fullPath = Path.GetFullPath(filePath);
            return fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        internal static string ValidateInstallRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("安装目录不能为空。", "path");
            }

            string fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string driveRoot = Path.GetPathRoot(fullPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullPath, driveRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("不能把磁盘根目录作为安装目录。", "path");
            }
            if (IsRemoteInstallRoot(fullPath))
            {
                throw new ArgumentException(
                    "Codex 便携版事务安装不支持 UNC 或映射网络盘；远程文件系统无法保证目录 File ID 在重连后保持稳定。",
                    "path");
            }
            if (File.Exists(fullPath))
            {
                throw new ArgumentException("安装目录不能是现有文件。", "path");
            }
            try
            {
                InstallOwnership.EnsureManagedDirectoryPath(fullPath, true);
            }
            catch (InvalidDataException exception)
            {
                throw new ArgumentException(exception.Message, "path", exception);
            }
            string stableInstallRoot;
            try
            {
                stableInstallRoot = NativeFileSystem.GetStablePathForPotentialPath(
                    fullPath);
            }
            catch (Exception exception)
            {
                throw new ArgumentException(
                    "无法解析安装目录的稳定物理位置：" + fullPath,
                    "path",
                    exception);
            }

            string[] managerStorageRoots =
            {
                PortableStorage.DataRoot,
                PortableStorage.CacheRoot,
                PortableStorage.LogsRoot
            };
            foreach (string storageRoot in managerStorageRoots)
            {
                string stableStorageRoot =
                    NativeFileSystem.GetStablePathForPotentialPath(storageRoot);
                if (DirectoryPathsOverlap(fullPath, storageRoot) ||
                    DirectoryPathsOverlap(stableInstallRoot, stableStorageRoot))
                {
                    throw new ArgumentException(
                        "Codex 安装目录不能位于管理器的数据、缓存或日志目录内，也不能包含这些目录。",
                        "path");
                }
            }

            string managerPath = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
            string installPrefix = NormalizeDirectoryPrefix(fullPath);
            string stableManagerPath =
                NativeFileSystem.GetStablePathForExistingPath(managerPath);
            string stableInstallPrefix = NormalizeDirectoryPrefix(stableInstallRoot);
            if (IsPathUnderPrefix(managerPath, installPrefix) ||
                IsPathUnderPrefix(stableManagerPath, stableInstallPrefix))
            {
                throw new ArgumentException("Codex 安装目录不能包含管理器自身，否则更新或卸载会删除管理器。", "path");
            }
            return fullPath;
        }

        private static bool IsRemoteInstallRoot(string fullPath)
        {
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return true;
            }
            string root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }
            try
            {
                Func<string, DriveType> provider =
                    InstallRootDriveTypeProviderForTest;
                DriveType driveType = provider == null
                    ? new DriveInfo(root).DriveType
                    : provider(root);
                return driveType == DriveType.Network;
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static bool DirectoryPathsOverlap(string firstPath, string secondPath)
        {
            string first = Path.GetFullPath(firstPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string second = Path.GetFullPath(secondPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string firstPrefix = first + Path.DirectorySeparatorChar;
            string secondPrefix = second + Path.DirectorySeparatorChar;
            return first.StartsWith(secondPrefix, StringComparison.OrdinalIgnoreCase) ||
                second.StartsWith(firstPrefix, StringComparison.OrdinalIgnoreCase);
        }

        internal static void MoveDirectoryWithRetry(string source, string destination)
        {
            Exception lastError = null;
            for (int attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    Directory.Move(source, destination);
                    return;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    Thread.Sleep(100);
                }
            }
            throw new IOException(
                "无法移动目录：" + source + " -> " + destination +
                (lastError == null ? string.Empty : "。最后错误：" + lastError.Message),
                lastError);
        }

        internal static void DeleteDirectorySafely(string path, string allowedParent)
        {
            DeleteDirectorySafely(path, allowedParent, null);
        }

        private static void DeleteDirectorySafely(
            string path,
            string allowedParent,
            string expectedDirectoryIdentity)
        {
            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            string parent = Path.GetFullPath(allowedParent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("拒绝清理允许范围之外的目录：" + fullPath);
            }
            Exception lastError = null;
            for (int attempt = 0; attempt < 50; attempt++)
            {
                NativePathKind pathKind = NativeFileSystem.GetPathKind(fullPath);
                if (pathKind == NativePathKind.Missing)
                {
                    return;
                }
                if (pathKind == NativePathKind.File ||
                    (pathKind == NativePathKind.ReparsePoint &&
                     expectedDirectoryIdentity != null))
                {
                    throw new IOException(
                        pathKind == NativePathKind.ReparsePoint
                            ? "带身份凭据的清理目录被重解析点替换：" + fullPath
                            : "待删除目录被普通文件占用：" + fullPath);
                }
                try
                {
                    NativeFileSystem.DeleteDirectoryRecursively(
                        fullPath,
                        expectedDirectoryIdentity);
                    return;
                }
                catch (InvalidDataException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    Thread.Sleep(100);
                }
            }
            throw new IOException(
                "无法删除目录：" + fullPath +
                (lastError == null
                    ? string.Empty
                    : "。最后错误：" + lastError.Message),
                lastError);
        }

        internal bool TryDeleteDirectory(string path, string allowedParent, string description)
        {
            return TryDeleteDirectory(path, allowedParent, description, null);
        }

        internal bool TryDeleteDirectory(
            string path,
            string allowedParent,
            string description,
            string expectedDirectoryIdentity)
        {
            try
            {
                DeleteDirectorySafely(
                    path,
                    allowedParent,
                    expectedDirectoryIdentity);
                return !Directory.Exists(path);
            }
            catch (Exception exception)
            {
                // 清理临时目录或过期备份失败不应覆盖安装、更新或回滚的原始结果。
                // 保留路径和异常，下一次启动可继续恢复或由用户人工处理。
                log("警告：无法清理" + description + "：" + path + "。" + exception.Message);
                return false;
            }
        }

        private bool TryDeleteDeploymentJournal(string installRoot, string description)
        {
            try
            {
                DeploymentJournal.Delete(installRoot);
                return !DeploymentJournal.Exists(installRoot);
            }
            catch (Exception exception)
            {
                log("警告：" + description + "的 journal 暂未删除：" + exception.Message);
                return false;
            }
        }

        internal void TryDeleteFile(string path, string description)
        {
            try
            {
                if (File.Exists(path))
                {
                    NativeFileSystem.DeleteFile(path);
                }
            }
            catch (Exception exception)
            {
                log("警告：无法清理" + description + "：" + path + "。" + exception.Message);
            }
        }

        internal void CleanupOwnedWorkDirectoriesBestEffort(
            string installRoot,
            string fallbackParent,
            TimeSpan maxAge)
        {
            HashSet<string> parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string volumeRoot = Path.GetPathRoot(installRoot);
            if (!string.IsNullOrWhiteSpace(volumeRoot))
            {
                parents.Add(Path.GetFullPath(volumeRoot));
            }
            if (!string.IsNullOrWhiteSpace(fallbackParent))
            {
                parents.Add(Path.GetFullPath(fallbackParent));
            }

            foreach (string parent in parents)
            {
                try
                {
                    StorageMaintenanceResult result = StorageMaintenance.CleanupOwnedWorkDirectories(
                        parent,
                        installRoot,
                        maxAge);
                    if (result.DeletedWorkDirectories > 0)
                    {
                        log(string.Format(
                            CultureInfo.InvariantCulture,
                            "崩溃工作目录维护完成：从“{0}”清理 {1} 个过期目录。",
                            parent,
                            result.DeletedWorkDirectories));
                    }
                    foreach (string warning in result.Warnings)
                    {
                        log("工作目录维护提示：" + warning);
                    }
                }
                catch (Exception exception)
                {
                    log("工作目录维护警告：无法扫描“" + parent + "”：" + exception.Message);
                }
            }
        }

    }
}
