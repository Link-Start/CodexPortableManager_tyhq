using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace CodexPortableManager
{
    internal enum ShellRegistryOwnership
    {
        NotOwned = 0,
        Owned = 1,
        Unknown = 2
    }

    internal static class ShellOwnershipChecker
    {
        internal static ShellRegistryOwnership GetRegistryCommandTreeOwnership(
            string path,
            ShellIntegrationCleanupJournalRecord journal)
        {
            using (RegistryKey root = Registry.CurrentUser.OpenSubKey(path))
            {
                if (root == null)
                {
                    return ShellRegistryOwnership.NotOwned;
                }
                ShellRegistryOwnership markerOwnership;
                bool hasInstallIdMarker = TryGetInstallIdMarkerOwnership(
                    root,
                    journal,
                    out markerOwnership);
                if (hasInstallIdMarker && markerOwnership != ShellRegistryOwnership.Owned)
                {
                    return markerOwnership;
                }
                using (RegistryKey command = root.OpenSubKey(@"shell\open\command"))
                {
                    string value = command == null ? null : command.GetValue(string.Empty) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        string executablePath;
                        return TryGetCommandExecutable(value, out executablePath) &&
                            PathBelongsToJournal(executablePath, journal)
                            ? ShellRegistryOwnership.Owned
                            : ShellRegistryOwnership.NotOwned;
                    }
                }
                return ShellRegistryOwnership.NotOwned;
            }
        }

        internal static bool RegistryKeyExists(string path)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path))
            {
                return key != null;
            }
        }

        internal static ShellRegistryOwnership GetRegistryPathEntryOwnership(
            string path,
            ShellIntegrationCleanupJournalRecord journal)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path))
            {
                if (key == null)
                {
                    return ShellRegistryOwnership.NotOwned;
                }
                ShellRegistryOwnership markerOwnership;
                bool hasInstallIdMarker = TryGetInstallIdMarkerOwnership(
                    key,
                    journal,
                    out markerOwnership);
                if (hasInstallIdMarker && markerOwnership != ShellRegistryOwnership.Owned)
                {
                    return markerOwnership;
                }
                string value = key.GetValue(string.Empty) as string;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return PathBelongsToJournal(value, journal)
                        ? ShellRegistryOwnership.Owned
                        : ShellRegistryOwnership.NotOwned;
                }
                return ShellRegistryOwnership.NotOwned;
            }
        }

        internal static ShellRegistryOwnership GetRegistryResourceTreeOwnership(
            string path,
            string valueName,
            ShellIntegrationCleanupJournalRecord journal)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path))
            {
                if (key == null)
                {
                    return ShellRegistryOwnership.NotOwned;
                }
                ShellRegistryOwnership markerOwnership;
                bool hasInstallIdMarker = TryGetInstallIdMarkerOwnership(
                    key,
                    journal,
                    out markerOwnership);
                if (hasInstallIdMarker && markerOwnership != ShellRegistryOwnership.Owned)
                {
                    return markerOwnership;
                }

                string resource = key.GetValue(valueName) as string;
                if (!string.IsNullOrWhiteSpace(resource))
                {
                    string resourcePath;
                    return TryGetResourcePath(resource, out resourcePath) &&
                        PathBelongsToJournal(resourcePath, journal)
                        ? ShellRegistryOwnership.Owned
                        : ShellRegistryOwnership.NotOwned;
                }
                return ShellRegistryOwnership.NotOwned;
            }
        }

        private static bool TryGetInstallIdMarkerOwnership(
            RegistryKey key,
            ShellIntegrationCleanupJournalRecord journal,
            out ShellRegistryOwnership ownership)
        {
            object markerValue = key.GetValue(ShellRegistrationWriter.PortableInstallIdValue);
            if (markerValue == null)
            {
                ownership = ShellRegistryOwnership.NotOwned;
                return false;
            }
            string marker = markerValue as string;
            Guid parsedMarker;
            if (!Guid.TryParseExact(marker, "N", out parsedMarker))
            {
                ownership = ShellRegistryOwnership.Unknown;
                return true;
            }
            ownership = string.Equals(marker, journal.InstallId, StringComparison.OrdinalIgnoreCase)
                ? ShellRegistryOwnership.Owned
                : ShellRegistryOwnership.NotOwned;
            return true;
        }

        private static bool TryGetCommandExecutable(string commandLine, out string executablePath)
        {
            executablePath = null;
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                return false;
            }

            int argumentCount;
            IntPtr arguments = CommandLineToArgvW(commandLine.Trim(), out argumentCount);
            if (arguments == IntPtr.Zero || argumentCount < 1)
            {
                return false;
            }
            try
            {
                IntPtr firstArgument = Marshal.ReadIntPtr(arguments);
                string value = Marshal.PtrToStringUni(firstArgument);
                string normalized;
                if (!TryNormalizeAbsolutePath(value, out normalized))
                {
                    return false;
                }
                executablePath = normalized;
                return true;
            }
            finally
            {
                LocalFree(arguments);
            }
        }

        private static bool TryGetResourcePath(string resource, out string resourcePath)
        {
            resourcePath = null;
            if (string.IsNullOrWhiteSpace(resource))
            {
                return false;
            }

            string value = resource.Trim();
            string path;
            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                int closingQuote = value.IndexOf('"', 1);
                if (closingQuote <= 1)
                {
                    return false;
                }
                path = value.Substring(1, closingQuote - 1);
            }
            else
            {
                path = value;
                int comma = value.LastIndexOf(',');
                int iconIndex;
                if (comma > 0 && int.TryParse(
                    value.Substring(comma + 1).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out iconIndex))
                {
                    path = value.Substring(0, comma).Trim();
                }
            }

            string normalized;
            if (!TryNormalizeAbsolutePath(path, out normalized))
            {
                return false;
            }
            resourcePath = normalized;
            return true;
        }

        internal static bool PathBelongsToJournal(
            string path,
            ShellIntegrationCleanupJournalRecord journal)
        {
            string normalizedPath;
            if (!TryNormalizeAbsolutePath(path, out normalizedPath))
            {
                return false;
            }
            IEnumerable<string> aliases = (journal.RootAliases ?? new List<string>())
                .Concat(new[] { journal.RegistrationRoot, journal.PhysicalRoot });
            if (aliases.Any(alias =>
                !string.IsNullOrWhiteSpace(alias) && IsPathUnderRoot(normalizedPath, alias)))
            {
                return true;
            }
            if ((File.Exists(normalizedPath) || Directory.Exists(normalizedPath)) &&
                !string.IsNullOrWhiteSpace(journal.PhysicalRoot))
            {
                try
                {
                    string stablePath = NativeFileSystem.GetStablePathForExistingPath(normalizedPath);
                    return IsPathUnderRoot(stablePath, journal.PhysicalRoot);
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        internal static bool TryNormalizeAbsolutePath(string path, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
            try
            {
                string value = Environment.ExpandEnvironmentVariables(path.Trim());
                if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    value = @"\\" + value.Substring(8);
                }
                else if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                {
                    value = value.Substring(4);
                }
                if (!Path.IsPathRooted(value))
                {
                    return false;
                }
                string root = Path.GetPathRoot(value);
                if (string.IsNullOrWhiteSpace(root) || root.EndsWith(":", StringComparison.Ordinal))
                {
                    // C:relative.exe 属于驱动器相对路径，不能用作可信目标。
                    return false;
                }
                normalized = Path.GetFullPath(value)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return normalized.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsPathUnderRoot(string path, string normalizedInstallRoot)
        {
            string normalizedPath;
            string normalizedRoot;
            if (!TryNormalizeAbsolutePath(path, out normalizedPath) ||
                !TryNormalizeAbsolutePath(normalizedInstallRoot, out normalizedRoot))
            {
                return false;
            }
            string prefix = normalizedRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!(File.Exists(normalizedPath) || Directory.Exists(normalizedPath)) ||
                !Directory.Exists(normalizedRoot))
            {
                return false;
            }
            try
            {
                string stablePath = NativeFileSystem.GetStablePathForExistingPath(normalizedPath);
                string stableRoot = NativeFileSystem.GetStablePathForExistingPath(normalizedRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                return stablePath.StartsWith(stableRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static void ThrowIfOwnershipUnknown(ShellRegistryOwnership ownership, string path)
        {
            if (ownership == ShellRegistryOwnership.Unknown)
            {
                throw new InvalidDataException(
                    "注册表归属标记损坏，已保留现场而未猜测清理：" + path);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(
            [MarshalAs(UnmanagedType.LPWStr)] string commandLine,
            out int argumentCount);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

    }
}
