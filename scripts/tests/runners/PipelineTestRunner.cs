using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CodexPortableManager
{
    internal static class PipelineTestRunner
    {
        public static int Run(string reportPath, string installRoot)
        {
            List<string> log = new List<string>();
            try
            {
                using (CodexPortableService service = new CodexPortableService(message => log.Add(message)))
                {
                    IProgress<OperationProgress> progress = new DirectProgress<OperationProgress>(value => log.Add(value.Message));
                    service.InstallOrUpdateAsync(
                        installRoot,
                        true,
                        progress,
                        new OperationPauseToken(null),
                        CancellationToken.None,
                        false).GetAwaiter().GetResult();
                    Version version = service.GetPortableVersion(installRoot);
                    PackageProfile profile = PackageProfileReader.Read(installRoot);
                    string exePath = PackageProfileReader.GetExecutablePath(installRoot, profile);
                    if (version == null || !File.Exists(exePath))
                    {
                        throw new InvalidDataException("管线完成后缺少版本清单或清单声明的主程序。"
                        );
                    }

                    string resourcesRoot = Path.Combine(installRoot, "app", "resources");
                    string runtimeIcon = Path.Combine(resourcesRoot, "icon-chatgpt.ico");
                    string trayIcon = Path.Combine(resourcesRoot, "chatgpt-tray-light.ico");
                    string currentIcon = Path.Combine(resourcesRoot, "icon.ico");
                    string officialIcon = File.Exists(trayIcon) ? trayIcon : currentIcon;
                    string stableIcon = Path.Combine(installRoot, "Codex.ico");
                    string[] requiredFiles =
                    {
                        Path.Combine(resourcesRoot, "app.asar"),
                        Path.Combine(resourcesRoot, "codex.exe"),
                        officialIcon,
                        stableIcon
                    };
                    foreach (string requiredFile in requiredFiles)
                    {
                        if (!File.Exists(requiredFile) || new FileInfo(requiredFile).Length == 0)
                        {
                            throw new InvalidDataException("管线完成后缺少关键文件：" + requiredFile);
                        }
                    }
                    if (!string.Equals(ComputeHash(stableIcon), ComputeHash(officialIcon), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("独立 Codex.ico 没有与官方图标保持一致。");
                    }
                    bool runtimeIconMatches = !File.Exists(runtimeIcon) ||
                        string.Equals(ComputeHash(runtimeIcon), ComputeHash(officialIcon), StringComparison.OrdinalIgnoreCase);
                    if (!runtimeIconMatches)
                    {
                        throw new InvalidDataException("运行时窗口图标没有与托盘图标保持一致。");
                    }
                    if (!IconResourcePatcher.HaveSameIconsFromIco(stableIcon, exePath))
                    {
                        throw new InvalidDataException("主程序的多尺寸图标资源没有正确更新。");
                    }
                    bool modelCatalogEnabled = false;
                    try
                    {
                        modelCatalogEnabled = ModelCatalogCompatibility.IsEnabled(exePath);
                    }
                    catch (Exception exception)
                    {
                        log.Add("模型 catalog 兼容层因官方指纹变化安全降级：" + exception.Message);
                    }
                    bool sandboxCompatibilityEnabled = false;
                    try
                    {
                        sandboxCompatibilityEnabled = SandboxCompatibility.IsEnabled(exePath);
                    }
                    catch (Exception exception)
                    {
                        log.Add("Windows 沙箱兼容层因官方 helper 结构变化安全降级：" + exception.Message);
                    }

                    StringBuilder report = new StringBuilder();
                    report.AppendLine("RESULT=PASS");
                    report.AppendLine("VERSION=" + version);
                    report.AppendLine("EXE=" + exePath);
                    report.AppendLine("RUNTIME_ICON_MATCH=" + runtimeIconMatches);
                    report.AppendLine("EXE_ICON_MATCH=True");
                    report.AppendLine("MODEL_CATALOG_UNLOCKED=" + modelCatalogEnabled);
                    report.AppendLine("SANDBOX_COMPATIBILITY=" + sandboxCompatibilityEnabled);
                    report.AppendLine("LOG=" + string.Join(" | ", log.ToArray()));
                    File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(true));
                }
                return 0;
            }
            catch (Exception exception)
            {
                File.WriteAllText(reportPath, "RESULT=FAIL" + Environment.NewLine + exception, new UTF8Encoding(true));
                return 1;
            }
        }


        private static string ComputeHash(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }
    }
}
