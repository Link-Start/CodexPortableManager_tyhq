using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexPortableManager
{
    internal static class ShellRegistrationWriter
    {
        internal const string CapabilitiesPath = @"Software\OpenAI\CodexPortable\Capabilities";
        internal const string RegisteredApplicationsPath = @"Software\RegisteredApplications";
        internal const string PortableInstallIdValue = "CodexPortableInstallId";
        internal const string PortableInstallRootValue = "CodexPortableInstallRoot";
        internal const string PortablePhysicalInstallRootValue = "CodexPortablePhysicalInstallRoot";

        internal static void RegisterProtocol(
            string protocolName,
            string exePath,
            string iconPath,
            string installRoot,
            string physicalInstallRoot,
            string installId)
        {
            using (RegistryKey protocol = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + protocolName))
            {
                SetOwnershipMarkers(protocol, installRoot, physicalInstallRoot, installId);
                protocol.SetValue(string.Empty, "URL:Codex Protocol");
                protocol.SetValue("URL Protocol", string.Empty);
                using (RegistryKey icon = protocol.CreateSubKey("DefaultIcon"))
                {
                    icon.SetValue(string.Empty, FormatIconLocation(iconPath));
                }
                using (RegistryKey command = protocol.CreateSubKey(@"shell\open\command"))
                {
                    command.SetValue(string.Empty, Quote(exePath) + " \"%1\"");
                }
            }
        }

        internal static void RegisterProgId(
            string progId,
            string description,
            string exePath,
            string iconPath,
            string installRoot,
            string physicalInstallRoot,
            string installId)
        {
            using (RegistryKey root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + progId))
            {
                SetOwnershipMarkers(root, installRoot, physicalInstallRoot, installId);
                root.SetValue(string.Empty, description);
                using (RegistryKey icon = root.CreateSubKey("DefaultIcon"))
                {
                    icon.SetValue(string.Empty, FormatIconLocation(iconPath));
                }
                using (RegistryKey command = root.CreateSubKey(@"shell\open\command"))
                {
                    command.SetValue(string.Empty, Quote(exePath) + " \"%1\"");
                }
            }
        }

        internal static void RegisterOpenWith(string extension, string progId)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\" + extension + @"\OpenWithProgids"))
            {
                key.SetValue(progId, new byte[0], RegistryValueKind.None);
            }
        }

        internal static void RegisterApplication(
            string exePath,
            string iconPath,
            IEnumerable<string> extensions,
            string installRoot,
            string physicalInstallRoot,
            string installId)
        {
            using (RegistryKey app = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\Applications\" + Path.GetFileName(exePath)))
            {
                SetOwnershipMarkers(app, installRoot, physicalInstallRoot, installId);
                app.SetValue("FriendlyAppName", "Codex");
                using (RegistryKey icon = app.CreateSubKey("DefaultIcon"))
                {
                    icon.SetValue(string.Empty, FormatIconLocation(iconPath));
                }
                using (RegistryKey command = app.CreateSubKey(@"shell\open\command"))
                {
                    command.SetValue(string.Empty, Quote(exePath) + " \"%1\"");
                }
                using (RegistryKey types = app.CreateSubKey("SupportedTypes"))
                {
                    foreach (string extension in extensions.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        types.SetValue(extension, string.Empty);
                    }
                }
            }
        }

        internal static void RegisterCapabilities(
            string iconPath,
            IEnumerable<string> protocols,
            IEnumerable<ShellAssociationRegistration> associations,
            string installRoot,
            string physicalInstallRoot,
            string installId)
        {
            using (RegistryKey capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
            {
                SetOwnershipMarkers(capabilities, installRoot, physicalInstallRoot, installId);
                capabilities.SetValue("ApplicationName", "Codex");
                capabilities.SetValue("ApplicationDescription", "OpenAI Codex Desktop");
                capabilities.SetValue("ApplicationIcon", FormatIconLocation(iconPath));
                using (RegistryKey files = capabilities.CreateSubKey("FileAssociations"))
                {
                    foreach (ShellAssociationRegistration association in associations)
                    {
                        foreach (string extension in association.Extensions)
                        {
                            files.SetValue(extension, association.ProgId);
                        }
                    }
                }
                using (RegistryKey urls = capabilities.CreateSubKey("URLAssociations"))
                {
                    foreach (string protocol in protocols)
                    {
                        urls.SetValue(protocol, protocol);
                    }
                }
            }
            using (RegistryKey registered = Registry.CurrentUser.CreateSubKey(RegisteredApplicationsPath))
            {
                registered.SetValue("Codex", CapabilitiesPath);
            }
        }

        internal static void RegisterAppUserModel(
            string appId,
            string iconPath,
            string installRoot,
            string physicalInstallRoot,
            string installId)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\AppUserModelId\" + appId))
            {
                SetOwnershipMarkers(key, installRoot, physicalInstallRoot, installId);
                key.SetValue("DisplayName", "Codex");
                key.SetValue("IconUri", FormatIconLocation(iconPath));
            }
        }

        internal static void RegisterAppPath(
            string name,
            string exePath,
            string installRoot,
            string physicalInstallRoot,
            string installId)
        {
            if (!ShellResourceNameRules.IsSafeRegistryComponent(name))
            {
                throw new InvalidDataException("App Paths 名称包含非法字符：" + name);
            }
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + name))
            {
                SetOwnershipMarkers(key, installRoot, physicalInstallRoot, installId);
                key.SetValue(string.Empty, exePath);
                key.SetValue("Path", Path.GetDirectoryName(exePath));
            }
        }

        internal static void TryCreateShortcut(
            IList<string> warnings,
            string shortcutPath,
            string target,
            string workingDirectory,
            string iconPath,
            string description,
            string appUserModelId = null)
        {
            try
            {
                ShortcutHelper.Create(
                    shortcutPath,
                    target,
                    string.Empty,
                    workingDirectory,
                    iconPath,
                    description,
                    appUserModelId);
            }
            catch (Exception exception)
            {
                warnings.Add("创建快捷方式失败（" + shortcutPath + "）：" + exception.Message);
            }
        }

        internal static void SetOwnershipMarkers(
            RegistryKey key,
            string installRoot,
            string physicalInstallRoot,
            string installId)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            Guid parsedInstallId;
            if (!Guid.TryParseExact(installId, "N", out parsedInstallId))
            {
                throw new InvalidDataException("Shell 注册表安装 ID 格式无效。");
            }
            key.SetValue(PortableInstallIdValue, installId);
            key.SetValue(PortableInstallRootValue, installRoot);
            if (!string.IsNullOrWhiteSpace(physicalInstallRoot))
            {
                key.SetValue(PortablePhysicalInstallRootValue, physicalInstallRoot);
            }
            else
            {
                key.DeleteValue(PortablePhysicalInstallRootValue, false);
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        private static string FormatIconLocation(string iconPath)
        {
            // IconUri/ApplicationIcon 接受“绝对路径,资源索引”，保持与既有 Windows 注册格式一致。
            return iconPath + ",0";
        }

    }

    internal sealed class ShellAssociationRegistration
    {
        public string Description { get; set; }
        public string ProgId { get; set; }
        public List<string> Extensions { get; set; }
    }


}
