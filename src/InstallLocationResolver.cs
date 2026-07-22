using System;
using System.IO;

namespace CodexPortableManager
{
    internal static class InstallLocationResolver
    {
        private const string DefaultInstallDirectoryName = "Codex";
        private const int MaximumGeneratedDirectoryIndex = 1000;

        public static string ResolveInstallRoot(string recordedInstallRoot)
        {
            return ResolveInstallRoot(recordedInstallRoot, ShellIntegration.TryDiscoverPortableInstallRoot);
        }

        internal static string ResolveInstallRoot(
            string recordedInstallRoot,
            Func<string> discoverPortableInstallRoot)
        {
            string validatedRoot;
            if (TryValidateInstallRoot(recordedInstallRoot, out validatedRoot))
            {
                return validatedRoot;
            }
            if (TryResolvePendingDeploymentRoot(recordedInstallRoot, out validatedRoot))
            {
                return validatedRoot;
            }

            string pendingShellCleanupRoot = null;
            try
            {
                pendingShellCleanupRoot = ShellIntegration.TryGetPendingCleanupRoot();
            }
            catch
            {
                // 损坏的 Shell journal 由启动维护记录；路径解析不猜测其内容。
            }
            if (TryResolvePendingShellCleanupRoot(pendingShellCleanupRoot, out validatedRoot))
            {
                return validatedRoot;
            }

            string discovered = discoverPortableInstallRoot == null
                ? null
                : discoverPortableInstallRoot();
            if (TryValidateInstallRoot(discovered, out validatedRoot))
            {
                PortableStorage.SaveRecordedInstallRoot(validatedRoot);
                return validatedRoot;
            }

            if (!string.IsNullOrWhiteSpace(recordedInstallRoot))
            {
                PortableStorage.ClearRecordedInstallRoot();
            }
            return string.Empty;
        }

        public static void SaveConfirmedInstallRoot(string installRoot)
        {
            string validatedRoot;
            if (!TryValidateInstallRoot(installRoot, out validatedRoot))
            {
                throw new InvalidOperationException("只能记录已经检测为完整可运行的 Codex 便携版目录。");
            }
            PortableStorage.SaveRecordedInstallRoot(validatedRoot);
        }

        public static string ResolveInstallDestination(string selectedPath)
        {
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                throw new ArgumentException("便携版目标位置不能为空。", nameof(selectedPath));
            }

            string selectedRoot = NormalizeDirectoryPath(selectedPath);
            string validatedSelectedRoot = null;
            ArgumentException selectedValidationError = null;
            try
            {
                validatedSelectedRoot = DeploymentEngine.ValidateInstallRoot(selectedRoot);
            }
            catch (ArgumentException exception)
            {
                selectedValidationError = exception;
            }

            if (validatedSelectedRoot != null && CanUseAsInstallRoot(validatedSelectedRoot))
            {
                return validatedSelectedRoot;
            }
            if (!Directory.Exists(selectedRoot))
            {
                if (selectedValidationError != null)
                {
                    throw selectedValidationError;
                }
                throw new DirectoryNotFoundException("便携版目标位置不存在：" + selectedRoot);
            }

            string candidateParent = selectedRoot;
            int firstIndex = 1;
            string selectedName = Path.GetFileName(selectedRoot);
            if (string.Equals(selectedName, DefaultInstallDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                DirectoryInfo parent = Directory.GetParent(selectedRoot);
                if (parent != null)
                {
                    candidateParent = parent.FullName;
                    firstIndex = 2;
                }
            }

            for (int index = firstIndex; index <= MaximumGeneratedDirectoryIndex; index++)
            {
                string directoryName = index == 1
                    ? DefaultInstallDirectoryName
                    : DefaultInstallDirectoryName + "-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string candidate = Path.Combine(candidateParent, directoryName);
                if (File.Exists(candidate))
                {
                    continue;
                }

                string validatedCandidate;
                try
                {
                    validatedCandidate = DeploymentEngine.ValidateInstallRoot(candidate);
                }
                catch (ArgumentException)
                {
                    if (Directory.Exists(candidate))
                    {
                        continue;
                    }
                    throw;
                }

                if (CanUseAsInstallRoot(validatedCandidate))
                {
                    return validatedCandidate;
                }
            }

            throw new IOException(
                "目标位置可用的 Codex 到 Codex-" + MaximumGeneratedDirectoryIndex + " 目录均已被其他内容占用：" + candidateParent);
        }

        private static bool CanUseAsInstallRoot(string installRoot)
        {
            if (!Directory.Exists(installRoot))
            {
                return true;
            }
            if (InstallOwnership.IsDirectoryEmpty(installRoot) || InstallOwnership.HasOwnershipMarker(installRoot))
            {
                return true;
            }

            PackageProfile profile;
            string validationError;
            return InstallOwnership.TryValidateCodexPayload(installRoot, out profile, out validationError);
        }

        private static string NormalizeDirectoryPath(string path)
        {
            string fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
            string rootPath = Path.GetPathRoot(fullPath);
            while (fullPath.Length > rootPath.Length &&
                (fullPath[fullPath.Length - 1] == Path.DirectorySeparatorChar ||
                 fullPath[fullPath.Length - 1] == Path.AltDirectorySeparatorChar))
            {
                fullPath = fullPath.Substring(0, fullPath.Length - 1);
            }
            return fullPath;
        }

        private static bool TryValidateInstallRoot(string installRoot, out string validatedRoot)
        {
            validatedRoot = null;
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                return false;
            }

            try
            {
                string normalized = DeploymentEngine.ValidateInstallRoot(installRoot);
                PackageProfile profile;
                string validationError;
                if (!InstallOwnership.TryValidateRunnableCodexPayload(normalized, out profile, out validationError))
                {
                    return false;
                }
                validatedRoot = normalized;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolvePendingDeploymentRoot(
            string installRoot,
            out string validatedRoot)
        {
            validatedRoot = null;
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                return false;
            }
            try
            {
                string normalized = DeploymentEngine.ValidateInstallRoot(installRoot);
                if (!DeploymentJournal.Exists(normalized))
                {
                    return false;
                }
                validatedRoot = normalized;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolvePendingShellCleanupRoot(
            string installRoot,
            out string validatedRoot)
        {
            validatedRoot = null;
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                return false;
            }
            try
            {
                validatedRoot = DeploymentEngine.ValidateInstallRoot(installRoot);
                return true;
            }
            catch
            {
                validatedRoot = null;
                return false;
            }
        }
    }
}
