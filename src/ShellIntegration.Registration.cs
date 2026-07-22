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
        private static IReadOnlyList<string> RegisterCore(
            PackageProfile profile,
            string expectedInstallRoot,
            string exePath,
            string iconPath,
            string managerPath)
        {
            if (profile == null)
            {
                throw new ArgumentNullException("profile");
            }

            string installRoot = NormalizeExpectedInstallRoot(expectedInstallRoot);
            InstallationRecord ownership = InstallOwnership.ReadInstallationRecord(installRoot);
            string installId = ownership.Identity.InstallId;
            string physicalInstallRoot = NormalizeExpectedInstallRoot(
                NativeFileSystem.GetStablePathForExistingPath(installRoot));
            string rootIdentity = InstallOwnership.GetManagedDirectoryIdentity(installRoot);
            string normalizedExePath = NormalizeRequiredAbsolutePath(exePath, "exePath");
            string normalizedIconPath = NormalizeRequiredAbsolutePath(iconPath, "iconPath");
            string normalizedManagerPath = NormalizeRequiredAbsolutePath(managerPath, "managerPath");
            if (!ShellOwnershipChecker.IsPathUnderRoot(normalizedExePath, installRoot))
            {
                throw new InvalidDataException("Codex 主程序不在预期安装目录内：" + normalizedExePath);
            }
            if (!ShellOwnershipChecker.IsPathUnderRoot(normalizedIconPath, installRoot))
            {
                throw new InvalidDataException("Codex 图标不在预期安装目录内：" + normalizedIconPath);
            }

            string appUserModelId = string.IsNullOrWhiteSpace(profile.AppUserModelId)
                ? AppUserModelId
                : profile.AppUserModelId.Trim();
            if (!IsSafeRegistryComponent(appUserModelId))
            {
                throw new InvalidDataException("AppUserModelID 包含非法字符：" + appUserModelId);
            }

            List<string> protocols = new List<string>();
            foreach (string protocol in profile.Protocols ?? new List<string>())
            {
                string value = (protocol ?? string.Empty).Trim();
                if (!IsSafeProtocol(value))
                {
                    throw new InvalidDataException("协议名不符合 Windows URI scheme 规则：" + value);
                }
                AddDistinct(protocols, value);
            }

            List<string> progIds = new List<string>();
            List<string> extensions = new List<string>();
            List<ShellAssociationRegistration> associations = new List<ShellAssociationRegistration>();
            foreach (FileAssociationProfile association in profile.FileAssociations ?? new List<FileAssociationProfile>())
            {
                if (association == null)
                {
                    continue;
                }

                string progId = "OpenAI.Codex." + SanitizeName(association.Name);
                if (!IsSafeRegistryComponent(progId))
                {
                    throw new InvalidDataException("文件关联 ProgID 包含非法字符：" + progId);
                }
                AddDistinct(progIds, progId);

                List<string> associationExtensions = new List<string>();
                foreach (string extension in association.Extensions ?? new List<string>())
                {
                    string value = (extension ?? string.Empty).Trim();
                    if (!IsSafeExtension(value))
                    {
                        throw new InvalidDataException("文件扩展名不安全：" + value);
                    }
                    AddDistinct(associationExtensions, value);
                    AddDistinct(extensions, value);
                }

                associations.Add(new ShellAssociationRegistration
                {
                    Description = "Codex " + (association.Name ?? "file"),
                    ProgId = progId,
                    Extensions = associationExtensions
                });
            }

            List<string> warnings = new List<string>();
            ShellIntegrationCleanupResult pendingCleanup = RecoverPendingCleanupCore();
            if (!pendingCleanup.Complete)
            {
                warnings.AddRange(pendingCleanup.Warnings);
                warnings.Add("已有系统集成清理事务尚未完成，本次注册已暂停以避免混合两套入口。");
                return warnings.AsReadOnly();
            }
            else
            {
                warnings.AddRange(pendingCleanup.Warnings);
            }

            IntegrationStateSnapshot existingSnapshot = ReadIntegrationStateSnapshot();
            IntegrationState existingState =
                existingSnapshot.Kind == IntegrationStateSnapshotKind.Valid
                ? existingSnapshot.State
                : null;
            string existingInstallRoot = TryResolveStateInstallRoot(existingState);
            if (existingState != null &&
                !StateBelongsToInstallation(
                    existingState,
                    installId,
                    installRoot,
                    physicalInstallRoot,
                    rootIdentity))
            {
                if (string.IsNullOrWhiteSpace(existingInstallRoot))
                {
                    warnings.Add("现有 integration.json 无法确定原安装身份，本次注册已暂停以避免覆盖未知入口。");
                    return warnings.AsReadOnly();
                }

                ShellIntegrationCleanupResult oldRootCleanup = RemoveWithResultCore(
                    existingInstallRoot,
                    TryGetValidInstallId(existingState),
                    existingInstallRoot);
                foreach (string warning in oldRootCleanup.Warnings)
                {
                    warnings.Add("清理旧安装根 " + existingInstallRoot + "：" + warning);
                }
                if (!oldRootCleanup.Complete)
                {
                    warnings.Add("旧安装根的系统集成尚未清理完成，本次注册已暂停以避免混合两套入口。");
                    return warnings.AsReadOnly();
                }
            }

            string workingDirectory = Path.GetDirectoryName(normalizedExePath);
            string startMenu;
            string desktop;
            GetShortcutRoots(out startMenu, out desktop);
            string startMenuShortcut = SelectPortableShortcutPath(
                Path.Combine(startMenu, "Codex.lnk"),
                Path.Combine(startMenu, "Codex Portable.lnk"),
                installRoot,
                warnings);
            string desktopShortcut = SelectPortableShortcutPath(
                Path.Combine(desktop, "Codex.lnk"),
                Path.Combine(desktop, "Codex Portable.lnk"),
                installRoot,
                warnings);

            IntegrationState state = new IntegrationState
            {
                InstallId = installId,
                InstallRoot = installRoot,
                PhysicalInstallRoot = physicalInstallRoot,
                RootIdentity = rootIdentity,
                ExecutablePath = normalizedExePath,
                AppUserModelId = appUserModelId,
                Protocols = protocols,
                ProgIds = progIds,
                Extensions = extensions,
                ShortcutPaths = new[] { startMenuShortcut, desktopShortcut }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList(),
                CleanupPending = false
            };

            try
            {
                ShellIntegrationCleanupJournalRecord cleanupJournal = PrepareCleanupCore(
                    installRoot,
                    installRoot,
                    installId,
                    ShellIntegrationCleanupPurpose.ImmediateCleanup,
                    null,
                    ShellIntegrationCleanupPhase.Armed,
                    warnings);
                ShellIntegrationCleanupResult currentRootCleanup = ExecuteCleanupJournalCore(cleanupJournal);
                warnings.AddRange(currentRootCleanup.Warnings);
                if (!currentRootCleanup.Complete)
                {
                    warnings.Add("当前安装身份的旧系统集成尚未清理完成，本次注册将在后续重试。");
                    return warnings.AsReadOnly();
                }
            }
            catch (Exception exception)
            {
                warnings.Add("准备旧系统集成清理事务失败：" + exception.Message);
                warnings.Add("未能持久化可恢复清理范围，本次注册未修改系统入口。");
                return warnings.AsReadOnly();
            }

            try
            {
                PortableStorage.SaveIntegrationState(state);
            }
            catch (Exception exception)
            {
                warnings.Add("保存系统集成注册意图失败：" + exception.Message);
                warnings.Add("未能保存可恢复注册状态，本次注册未修改系统入口。");
                return warnings.AsReadOnly();
            }

            if (!string.IsNullOrWhiteSpace(startMenuShortcut))
            {
                ShellRegistrationWriter.TryCreateShortcut(
                    warnings,
                    startMenuShortcut,
                    normalizedExePath,
                    workingDirectory,
                    normalizedIconPath,
                    "Codex",
                    appUserModelId);
            }
            if (!string.IsNullOrWhiteSpace(desktopShortcut))
            {
                ShellRegistrationWriter.TryCreateShortcut(
                    warnings,
                    desktopShortcut,
                    normalizedExePath,
                    workingDirectory,
                    normalizedIconPath,
                    "Codex",
                    appUserModelId);
            }

            foreach (string protocol in protocols)
            {
                TryRegister(warnings, "协议 " + protocol, delegate
                {
                    ShellRegistrationWriter.RegisterProtocol(
                        protocol,
                        normalizedExePath,
                        normalizedIconPath,
                        installRoot,
                        physicalInstallRoot,
                        installId);
                });
            }
            foreach (ShellAssociationRegistration association in associations)
            {
                bool progIdRegistered = TryRegister(warnings, "文件关联 " + association.ProgId, delegate
                {
                    ShellRegistrationWriter.RegisterProgId(
                        association.ProgId,
                        association.Description,
                        normalizedExePath,
                        normalizedIconPath,
                        installRoot,
                        physicalInstallRoot,
                        installId);
                });
                if (progIdRegistered)
                {
                    foreach (string extension in association.Extensions)
                    {
                        string capturedExtension = extension;
                        TryRegister(warnings, "文件扩展名 " + capturedExtension, delegate
                        {
                            ShellRegistrationWriter.RegisterOpenWith(capturedExtension, association.ProgId);
                        });
                    }
                }
            }

            TryRegister(warnings, "应用程序注册", delegate
            {
                ShellRegistrationWriter.RegisterApplication(
                    normalizedExePath,
                    normalizedIconPath,
                    extensions,
                    installRoot,
                    physicalInstallRoot,
                    installId);
            });
            TryRegister(warnings, "默认应用 Capabilities", delegate
            {
                ShellRegistrationWriter.RegisterCapabilities(
                    normalizedIconPath,
                    protocols,
                    associations,
                    installRoot,
                    physicalInstallRoot,
                    installId);
            });
            TryRegister(warnings, "通知标识", delegate
            {
                ShellRegistrationWriter.RegisterAppUserModel(
                    appUserModelId,
                    normalizedIconPath,
                    installRoot,
                    physicalInstallRoot,
                    installId);
            });
            TryRegister(warnings, "App Paths", delegate
            {
                ShellRegistrationWriter.RegisterAppPath(
                    Path.GetFileName(normalizedExePath),
                    normalizedExePath,
                    installRoot,
                    physicalInstallRoot,
                    installId);
                ShellRegistrationWriter.RegisterAppPath(
                    "CodexDesktop.exe",
                    normalizedExePath,
                    installRoot,
                    physicalInstallRoot,
                    installId);
            });

            // 管理器快捷方式不属于 Codex 安装根，失败时只记警告。
            ShellRegistrationWriter.TryCreateShortcut(
                warnings,
                Path.Combine(startMenu, "Codex Portable Manager.lnk"),
                normalizedManagerPath,
                Path.GetDirectoryName(normalizedManagerPath),
                normalizedManagerPath,
                "Codex Portable Manager");
            ShellRegistrationWriter.TryCreateShortcut(
                warnings,
                Path.Combine(desktop, "Codex Portable Manager.lnk"),
                normalizedManagerPath,
                Path.GetDirectoryName(normalizedManagerPath),
                normalizedManagerPath,
                "Codex Portable Manager");

            TryRegister(warnings, "Shell 刷新通知", delegate
            {
                NotifyShellChanged(normalizedIconPath, startMenuShortcut, desktopShortcut);
            });
            return warnings.AsReadOnly();
        }

        internal static string SelectPortableShortcutPath(
            string preferredPath,
            string fallbackPath,
            string installRoot,
            IList<string> warnings)
        {
            string preferred = NormalizeRequiredAbsolutePath(preferredPath, nameof(preferredPath));
            string fallback = NormalizeRequiredAbsolutePath(fallbackPath, nameof(fallbackPath));
            string normalizedRoot = NormalizeExpectedInstallRoot(installRoot);
            if (!DirectoryPathsEqual(Path.GetDirectoryName(preferred), Path.GetDirectoryName(fallback)))
            {
                throw new InvalidDataException("便携版快捷方式的首选和备用路径必须位于同一目录。");
            }

            if (ShortcutPathIsAvailableOrOwned(preferred, normalizedRoot, warnings))
            {
                return preferred;
            }
            if (ShortcutPathIsAvailableOrOwned(fallback, normalizedRoot, warnings))
            {
                warnings.Add("同名 Codex 快捷方式属于其他程序，已保留并改用：" + fallback);
                return fallback;
            }

            warnings.Add("Codex 与 Codex Portable 快捷方式均已被其他程序占用，本次未覆盖任何现有快捷方式。");
            return null;
        }

        private static bool ShortcutPathIsAvailableOrOwned(
            string shortcutPath,
            string installRoot,
            IList<string> warnings)
        {
            if (!File.Exists(shortcutPath))
            {
                return true;
            }

            string target;
            string error;
            if (!ShortcutHelper.TryGetTarget(shortcutPath, out target, out error))
            {
                warnings.Add("现有快捷方式无法确认归属，已保留（" + shortcutPath + "）：" + error);
                return false;
            }
            return ShellOwnershipChecker.IsPathUnderRoot(target, installRoot);
        }

        private static HashSet<string> GetPortableShortcutCandidates(
            IntegrationState state,
            IList<string> warnings)
        {
            string startMenu;
            string desktop;
            GetShortcutRoots(out startMenu, out desktop);
            HashSet<string> candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(startMenu, "Codex.lnk"),
                Path.Combine(startMenu, "Codex Portable.lnk"),
                Path.Combine(desktop, "Codex.lnk"),
                Path.Combine(desktop, "Codex Portable.lnk")
            };

            IEnumerable<string> stateShortcutPaths = state == null
                ? (IEnumerable<string>)new string[0]
                : state.ShortcutPaths ?? new List<string>();
            foreach (string value in stateShortcutPaths)
            {
                string normalized;
                if (ShellOwnershipChecker.TryNormalizeAbsolutePath(value, out normalized) &&
                    IsExpectedPortableShortcutPath(normalized, startMenu, desktop))
                {
                    candidates.Add(normalized);
                }
                else if (!string.IsNullOrWhiteSpace(value))
                {
                    warnings.Add("状态文件中的快捷方式路径不在受管范围，已忽略：" + value);
                }
            }
            return candidates;
        }

        private static bool IsExpectedPortableShortcutPath(
            string path,
            string startMenu,
            string desktop)
        {
            string name = Path.GetFileName(path);
            if (!string.Equals(name, "Codex.lnk", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "Codex Portable.lnk", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string parent = Path.GetDirectoryName(path);
            return DirectoryPathsEqual(parent, startMenu) ||
                DirectoryPathsEqual(parent, desktop);
        }

        private static void GetShortcutRoots(out string startMenu, out string desktop)
        {
            Func<Tuple<string, string>> provider = ShortcutRootsProviderForTest;
            Tuple<string, string> overridden = provider == null ? null : provider();
            if (overridden != null)
            {
                startMenu = NormalizeRequiredAbsolutePath(overridden.Item1, "startMenu");
                desktop = NormalizeRequiredAbsolutePath(overridden.Item2, "desktop");
                return;
            }
            startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs");
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        private static bool TryRegister(IList<string> warnings, string label, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception exception)
            {
                warnings.Add(label + "注册失败：" + exception.Message);
                return false;
            }
        }


    }
}
