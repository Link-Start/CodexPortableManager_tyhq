using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexPortableManager
{
    internal static class DeploymentCompletion
    {
        public static OperationProgress ForCurrentVersion(
            Version currentVersion,
            Version remoteVersion,
            DeploymentResult result)
        {
            if (currentVersion == null) throw new ArgumentNullException(nameof(currentVersion));
            if (remoteVersion == null) throw new ArgumentNullException(nameof(remoteVersion));
            if (result == null) throw new ArgumentNullException(nameof(result));

            List<string> details = new List<string> { "主体版本保持不变" };
            if (result.IntegrationRequested && result.IntegrationSucceeded)
            {
                details.Add("系统集成已刷新");
            }
            else if (!result.IntegrationSucceeded)
            {
                AddIntegrationWarning(details);
                details.Add("详情请查看日志");
            }

            return new OperationProgress(
                currentVersion == remoteVersion ? "当前便携版已经是最新版本" : "本地版本高于当前官方版本",
                100,
                JoinDetails(details));
        }

        public static OperationProgress ForInstalledVersion(Version remoteVersion, DeploymentResult result)
        {
            if (remoteVersion == null) throw new ArgumentNullException(nameof(remoteVersion));
            if (result == null) throw new ArgumentNullException(nameof(result));

            List<string> details = new List<string> { "版本 " + remoteVersion + " 已就绪" };
            if (!result.HasWarnings)
            {
                details.Add("可以直接启动");
            }
            else
            {
                if (!result.IntegrationSucceeded) AddIntegrationWarning(details);
                if (!result.CompatibilitySucceeded) AddCompatibilityWarning(details, result);
                AddMaintenanceWarning(details, result);
                if (!result.IntegrationSucceeded || !result.CompatibilitySucceeded)
                {
                    details.Add("详情请查看日志");
                }
            }

            string message = !result.IntegrationSucceeded
                ? "Codex 便携版主体安装完成，系统集成未完成"
                : !result.CompatibilitySucceeded
                    ? "Codex 便携版更新完成，部分功能设置等待适配"
                : (result.OldBackupCleanupPending
                    ? "Codex 便携版安装完成，存在待清理项目"
                    : "Codex 便携版安装完成");
            return new OperationProgress(message, 100, JoinDetails(details));
        }

        public static OperationProgress ForMigration(DeploymentResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            List<string> details = new List<string>
            {
                "官方桌面版已卸载",
                "便携版已验证并发起启动"
            };
            if (result.IntegrationSucceeded && result.IntegrationRequested)
            {
                details.Add("系统集成已切换到便携版");
            }
            if (!result.IntegrationSucceeded) AddIntegrationWarning(details);
            if (!result.CompatibilitySucceeded) AddCompatibilityWarning(details, result);
            AddMaintenanceWarning(details, result);
            if (!result.IntegrationSucceeded || !result.CompatibilitySucceeded)
            {
                details.Add("详情请查看日志");
            }

            string message = !result.IntegrationSucceeded
                ? "迁移主体完成，系统集成未完成"
                : !result.CompatibilitySucceeded
                    ? "迁移完成，部分功能设置等待适配"
                : (result.OldBackupCleanupPending
                    ? "迁移完成，存在待清理项目"
                    : "迁移完成，已切换到 Codex 便携版");
            return new OperationProgress(message, 100, JoinDetails(details));
        }

        public static OperationProgress ForRollback(DeploymentResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            List<string> details = new List<string> { "上一版本已恢复，回滚前版本已保留为新的 .previous" };
            if (!result.IntegrationSucceeded)
            {
                AddIntegrationWarning(details);
                details.Add("详情请查看日志");
            }

            return new OperationProgress(
                result.IntegrationSucceeded
                    ? "Codex 便携版已回滚"
                    : "Codex 便携版主体已回滚，系统集成未完成",
                100,
                JoinDetails(details));
        }

        private static void AddIntegrationWarning(ICollection<string> details)
        {
            details.Add("系统集成未能完整注册，可使用“修复启动入口”重试");
        }

        private static void AddCompatibilityWarning(ICollection<string> details, DeploymentResult result)
        {
            details.Add(result != null && result.Compatibility != null && result.Compatibility.HasPartialSuccess
                ? "部分兼容设置已应用；不支持的功能保留官方文件并等待适配"
                : "部分兼容设置未能适配新版本，已恢复官方程序文件并保留当前选择");
        }

        private static void AddMaintenanceWarning(ICollection<string> details, DeploymentResult result)
        {
            if (result.OldBackupCleanupPending)
            {
                details.Add("旧回滚备份暂未清理，下次启动将继续处理");
            }
        }

        private static string JoinDetails(IEnumerable<string> details)
        {
            return string.Join("；", details.ToArray()) + "。";
        }
    }
}
