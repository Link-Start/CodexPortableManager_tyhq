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
        private static void AddProfileCandidates(
            string installRoot,
            ISet<string> protocols,
            ISet<string> progIds,
            ISet<string> extensions,
            ISet<string> executableNames,
            ISet<string> appUserModelIds)
        {
            try
            {
                PackageProfile profile = PackageProfileReader.Read(installRoot);
                string executablePath = PackageProfileReader.GetExecutablePath(installRoot, profile);
                string executableName = Path.GetFileName(executablePath);
                if (IsSafeRegistryComponent(executableName))
                {
                    executableNames.Add(executableName);
                }
                if (IsSafeRegistryComponent(profile.AppUserModelId))
                {
                    appUserModelIds.Add(profile.AppUserModelId.Trim());
                }
                foreach (string protocol in profile.Protocols ?? new List<string>())
                {
                    if (IsSafeProtocol(protocol))
                    {
                        protocols.Add(protocol.Trim());
                    }
                }
                foreach (FileAssociationProfile association in profile.FileAssociations ?? new List<FileAssociationProfile>())
                {
                    if (association == null)
                    {
                        continue;
                    }
                    string progId = "OpenAI.Codex." + SanitizeName(association.Name);
                    if (IsSafeRegistryComponent(progId))
                    {
                        progIds.Add(progId);
                    }
                    foreach (string extension in association.Extensions ?? new List<string>())
                    {
                        if (IsSafeExtension(extension))
                        {
                            extensions.Add(extension.Trim());
                        }
                    }
                }
            }
            catch
            {
                // 清单缺失或损坏时继续使用状态文件和固定已知标识。
            }
        }

        internal static string TryResolveStateInstallRoot(IntegrationState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.InstallRoot))
            {
                return null;
            }
            try
            {
                return NormalizeExpectedInstallRoot(state.InstallRoot);
            }
            catch
            {
                return null;
            }
        }

        private static void AddSafeProtocolCandidates(
            ISet<string> destination,
            IEnumerable<string> values,
            IList<string> warnings,
            string source)
        {
            foreach (string value in values ?? new List<string>())
            {
                if (IsSafeProtocol(value))
                {
                    destination.Add(value.Trim());
                }
                else if (!string.IsNullOrWhiteSpace(value))
                {
                    warnings.Add(source + "中的非法协议名已忽略：" + value);
                }
            }
        }

        private static void AddSafeRegistryCandidates(
            ISet<string> destination,
            IEnumerable<string> values,
            IList<string> warnings,
            string source)
        {
            foreach (string value in values ?? new List<string>())
            {
                if (IsSafeRegistryComponent(value))
                {
                    destination.Add(value.Trim());
                }
                else if (!string.IsNullOrWhiteSpace(value))
                {
                    warnings.Add(source + " 中的非法名称已忽略：" + value);
                }
            }
        }

        private static void AddSafeExtensionCandidates(
            ISet<string> destination,
            IEnumerable<string> values,
            IList<string> warnings,
            string source)
        {
            foreach (string value in values ?? new List<string>())
            {
                if (IsSafeExtension(value))
                {
                    destination.Add(value.Trim());
                }
                else if (!string.IsNullOrWhiteSpace(value))
                {
                    warnings.Add(source + "中的非法扩展名已忽略：" + value);
                }
            }
        }

        private static OperationFileLock AcquireShellIntegrationLock()
        {
            return OperationFileLock.AcquireResource(
                "shell-integration|" + PortableStorage.CurrentUserStorageKey,
                ShellIntegrationResourceName);
        }

        private static void DeleteRegistryValue(string path, string name)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path, true))
            {
                if (key != null)
                {
                    key.DeleteValue(name, false);
                }
            }
        }

        private static string SanitizeName(string value)
        {
            StringBuilder result = new StringBuilder();
            bool capitalize = true;
            foreach (char character in value ?? "file")
            {
                if (!char.IsLetterOrDigit(character))
                {
                    capitalize = true;
                    continue;
                }
                result.Append(capitalize ? char.ToUpperInvariant(character) : character);
                capitalize = false;
            }
            return result.Length == 0 ? "File" : result.ToString();
        }

        private static bool IsSafeProtocol(string value)
        {
            return ShellResourceNameRules.IsSafeProtocol(value);
        }

        private static bool IsSafeExtension(string value)
        {
            return ShellResourceNameRules.IsSafeExtension(value);
        }

        private static bool IsSafeRegistryComponent(string value)
        {
            return ShellResourceNameRules.IsSafeRegistryComponent(value);
        }

        private static void AddDistinct(ICollection<string> values, string value)
        {
            if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(value);
            }
        }

        private static string NormalizeExpectedInstallRoot(string path)
        {
            string normalized = NormalizeRequiredAbsolutePath(path, "expectedInstallRoot")
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string pathRoot = Path.GetPathRoot(normalized);
            if (string.Equals(
                normalized,
                (pathRoot ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "不能把磁盘根目录作为系统集成清理边界。",
                    "expectedInstallRoot");
            }
            return normalized;
        }

        private static string NormalizeRequiredAbsolutePath(string path, string parameterName)
        {
            string normalized;
            if (!ShellOwnershipChecker.TryNormalizeAbsolutePath(path, out normalized))
            {
                throw new ArgumentException("路径必须是有效的绝对路径：" + path, parameterName);
            }
            return normalized;
        }

        private static bool DirectoryPathsEqual(string first, string second)
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
            if (!Directory.Exists(normalizedFirst) || !Directory.Exists(normalizedSecond))
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

        private static void NotifyShellChanged(params string[] updatedPaths)
        {
            foreach (string path in updatedPaths ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    SHChangeNotifyPath(
                        ShellChangeUpdateItem,
                        ShellNotifyPathWFlushNoWait,
                        path,
                        IntPtr.Zero);
                }
            }
            SHChangeNotify(
                ShellChangeAssociationsChanged,
                ShellNotifyIdListFlushNoWait,
                IntPtr.Zero,
                IntPtr.Zero);
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(
            uint eventId,
            uint flags,
            IntPtr item1,
            IntPtr item2);

        [DllImport(
            "shell32.dll",
            CharSet = CharSet.Unicode,
            EntryPoint = "SHChangeNotify",
            ExactSpelling = true)]
        private static extern void SHChangeNotifyPath(
            uint eventId,
            uint flags,
            string item1,
            IntPtr item2);


    }
}
