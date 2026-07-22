using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace CodexPortableManager
{
    internal sealed class ShellIntegrationCoordinator
    {
        private readonly Action<string> log;

        public ShellIntegrationCoordinator(Action<string> logAction)
        {
            log = logAction ?? delegate { };
        }

        public IReadOnlyList<string> Create(string installRoot)
        {
            PackageProfile profile = PackageProfileReader.Read(installRoot);
            string executablePath = PackageProfileReader.GetExecutablePath(installRoot, profile);
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException("未找到 Codex 桌面程序。", executablePath);
            }
            string managerPath = Process.GetCurrentProcess().MainModule.FileName;
            string iconPath = Path.Combine(installRoot, "Codex.ico");
            if (!File.Exists(iconPath))
            {
                iconPath = executablePath;
                log("未找到独立 Codex.ico，系统集成将使用主程序自身图标。");
            }
            List<string> warnings = new List<string>();
            try
            {
                warnings.AddRange(ShellIntegration.Register(
                    profile,
                    installRoot,
                    executablePath,
                    iconPath,
                    managerPath));
            }
            catch (Exception exception)
            {
                warnings.Add("系统集成注册未完成：" + exception.Message);
            }
            foreach (string warning in warnings)
            {
                log("系统集成注册警告：" + warning);
            }
            log(warnings.Count == 0
                ? "快捷方式、codex://、文件关联和通知标识已指向便携版。"
                : "Codex 主程序已就绪，但部分系统集成未完成；可稍后使用“修复启动入口”重试。");
            return warnings.AsReadOnly();
        }

        public void Remove(string installRoot)
        {
            RemoveWithResult(installRoot);
        }

        public ShellIntegrationCleanupResult RemoveWithResult(string installRoot)
        {
            ShellIntegrationCleanupResult result = ShellIntegration.RemoveWithResult(installRoot);
            LogCleanupResult(result);
            return result;
        }

        public void PrepareCleanup(
            string registrationRoot,
            string sourceRoot,
            string installId,
            string deploymentOperationId)
        {
            IReadOnlyList<string> warnings = ShellIntegration.PrepareCleanup(
                registrationRoot,
                sourceRoot,
                installId,
                deploymentOperationId);
            foreach (string warning in warnings)
            {
                log("系统集成清理准备警告：" + warning);
            }
        }

        public ShellIntegrationCleanupResult CompletePreparedCleanup(
            string registrationRoot,
            string sourceRoot,
            string installId,
            string deploymentOperationId)
        {
            ShellIntegrationCleanupResult result = ShellIntegration.CompletePreparedCleanup(
                registrationRoot,
                sourceRoot,
                installId,
                deploymentOperationId);
            LogCleanupResult(result);
            return result;
        }

        public void CancelPreparedCleanup(
            string registrationRoot,
            string installId,
            string deploymentOperationId)
        {
            ShellIntegration.CancelPreparedCleanup(
                registrationRoot,
                installId,
                deploymentOperationId);
        }

        private void LogCleanupResult(ShellIntegrationCleanupResult result)
        {
            foreach (string warning in result.Warnings)
            {
                log("系统集成清理警告：" + warning);
            }
            if (!result.Complete)
            {
                log("系统集成仍有待清理项目；程序目录卸载不回滚，下次启动将自动重试。");
            }
        }

        public ShellIntegrationCleanupResult RecoverPendingCleanup()
        {
            ShellIntegrationCleanupResult result = ShellIntegration.RecoverPendingCleanup();
            foreach (string warning in result.Warnings)
            {
                log("系统集成恢复清理警告：" + warning);
            }
            if (result.Complete && result.Warnings.Count > 0)
            {
                log("上次遗留的系统集成清理已经完成。");
            }
            return result;
        }
    }
}
