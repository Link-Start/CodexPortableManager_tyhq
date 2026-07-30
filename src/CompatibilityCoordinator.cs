using System;
using System.IO;
using System.Linq;

namespace CodexPortableManager
{
    internal sealed class CompatibilityCoordinator
    {
        internal const string SandboxRecipeId = "sandbox.account-environment";
        private readonly Action<string> log;

        public CompatibilityCoordinator(Action<string> logAction)
        {
            log = logAction ?? delegate { };
        }

        public CompatibilityResult Apply(string executablePath, CompatibilityOptions compatibility)
        {
            return ApplyInternal(executablePath, compatibility, false);
        }

        internal CompatibilityResult ApplyOfficialStaging(
            string executablePath,
            CompatibilityOptions compatibility)
        {
            return ApplyInternal(executablePath, compatibility, true);
        }

        private CompatibilityResult ApplyInternal(
            string executablePath,
            CompatibilityOptions compatibility,
            bool defaultUnsupportedToDisabled)
        {
            return CompatibilityAnalysisMemory.Run(
                () => ApplyInternalCore(
                    executablePath,
                    compatibility,
                    defaultUnsupportedToDisabled),
                log);
        }

        private CompatibilityResult ApplyInternalCore(
            string executablePath,
            CompatibilityOptions compatibility,
            bool defaultUnsupportedToDisabled)
        {
            if (compatibility == null) throw new ArgumentNullException(nameof(compatibility));

            bool manageSandbox = compatibility.ManageSandboxCompatibility;
            string sandboxReason = string.Empty;
            bool sandboxRequired = manageSandbox &&
                compatibility.SandboxCompatibilityEnabled &&
                SandboxCompatibility.NeedsCompatibilityFix(out sandboxReason);
            bool sandboxTargetEnabled = compatibility.SandboxCompatibilityEnabled && sandboxRequired;
            if (manageSandbox && compatibility.SandboxCompatibilityEnabled)
            {
                log(sandboxRequired
                    ? "检测到 Windows 沙箱账户名解析冲突，将启用环境修正。" + sandboxReason
                    : "当前账户不需要 Windows 沙箱账户名环境修正，将保持官方脚本。" + sandboxReason);
            }

            CompatibilityOptions effective = new CompatibilityOptions(
                sandboxTargetEnabled,
                compatibility.UnlockModelCatalogEnabled,
                compatibility.SupplementChineseUiEnabled,
                compatibility.EnglishTechnicalParametersEnabled,
                compatibility.ManageSandboxCompatibility,
                compatibility.ManageModelCatalog,
                compatibility.ManageLocalization);
            CompatibilityPlanResult asar = new CompatibilityPlan(log).Apply(
                executablePath,
                effective,
                defaultUnsupportedToDisabled);
            string sandboxDesired = !manageSandbox
                ? "NotManaged"
                : compatibility.SandboxCompatibilityEnabled
                    ? sandboxRequired ? "Enabled" : "NotRequired"
                    : "Disabled";
            CompatibilityFeatureResult sandbox = BuildPlanFeature(
                asar.SandboxChange,
                asar.SandboxSucceeded,
                "SandboxCompatibility",
                "Windows 沙箱兼容",
                sandboxDesired,
                SandboxRecipeId);
            if (manageSandbox && sandboxDesired == "NotRequired" && asar.SandboxSucceeded)
            {
                sandbox.Desired = "NotRequired";
                sandbox.Status = CompatibilityFeatureStatus.NotRequired;
            }
            CompatibilityResult result = new CompatibilityResult
            {
                ModelCatalogSucceeded = asar.ModelCatalogSucceeded,
                SandboxSucceeded = asar.SandboxSucceeded,
                LocalizationSucceeded = asar.LocalizationSucceeded,
                ModelCatalog = BuildPlanFeature(
                    asar.ModelCatalogChange,
                    asar.ModelCatalogSucceeded,
                    "ModelCatalog",
                    "模型目录",
                    compatibility.ManageModelCatalog
                        ? compatibility.UnlockModelCatalogEnabled ? "Patched" : "Official"
                        : "NotManaged",
                    ModelCatalogCompatibility.RecipeId),
                Sandbox = sandbox,
                Localization = BuildPlanFeature(
                    asar.LocalizationChange,
                    asar.LocalizationSucceeded,
                    "Localization",
                    "界面语言",
                    compatibility.ManageLocalization
                        ? "Menus=" + (compatibility.SupplementChineseUiEnabled ? "Patched" : "Official") +
                          ";Reasoning=" + (compatibility.EnglishTechnicalParametersEnabled ? "Patched" : "Official")
                        : "Menus=NotManaged;Reasoning=NotManaged",
                    CodexLocalizationCompatibility.RecipeId)
            };
            if (!result.AllSucceeded)
            {
                log("兼容设置警告：以下功能未能应用：" + string.Join("、", result.FailedFeatures.ToArray()) + "。主体程序保持可用。");
            }
            return result;
        }

        private static CompatibilityFeatureResult BuildPlanFeature(
            CompatibilityFeatureChange change,
            bool succeeded,
            string featureId,
            string displayName,
            string desired,
            string recipeId)
        {
            if (change != null)
            {
                CompatibilityFeatureResult feature = change.ToFeatureResult(
                    featureId,
                    displayName,
                    desired,
                    recipeId);
                if (!succeeded && feature.Status != CompatibilityFeatureStatus.Unsupported)
                {
                    feature.Status = CompatibilityFeatureStatus.Failed;
                }
                return feature;
            }

            return new CompatibilityFeatureResult
            {
                FeatureId = featureId,
                DisplayName = displayName,
                Before = succeeded ? desired : "Unknown",
                Desired = desired,
                After = succeeded ? desired : "Unknown",
                Changed = false,
                Status = succeeded
                    ? CompatibilityFeatureStatus.AlreadySatisfied
                    : CompatibilityFeatureStatus.Failed,
                Error = succeeded ? null : "功能分析未完成；请查看前序日志。",
                RecipeId = recipeId
            };
        }

        public void ApplyVisual(string installRoot, string executablePath)
        {
            string resourcesRoot = Path.Combine(Path.GetDirectoryName(executablePath), "resources");
            string trayIcon = Path.Combine(resourcesRoot, "chatgpt-tray-light.ico");
            string windowIcon = Path.Combine(resourcesRoot, "icon-chatgpt.ico");
            if (!File.Exists(trayIcon))
            {
                string currentIcon = Path.Combine(resourcesRoot, "icon.ico");
                if (!File.Exists(currentIcon))
                {
                    log("视觉兼容警告：未找到官方 ICO，已保留官方程序文件并继续安装。");
                    return;
                }
                trayIcon = currentIcon;
            }

            string stableIcon = Path.Combine(installRoot, "Codex.ico");
            try
            {
                CopyIcoAtomically(trayIcon, stableIcon);
            }
            catch (Exception exception)
            {
                log("视觉兼容警告：无法生成独立 Codex.ico，Shell 集成将回退使用主程序图标。" + exception.Message);
                return;
            }

            if (File.Exists(windowIcon))
            {
                try
                {
                    CopyIcoAtomically(trayIcon, windowIcon);
                }
                catch (Exception exception)
                {
                    log("视觉兼容警告：无法替换窗口图标，已保留官方窗口图标并继续安装。" + exception.Message);
                }
            }
            else
            {
                log("视觉兼容警告：新版程序包没有旧版窗口 ICO 路径，已跳过窗口图标修改。");
            }

            try
            {
                IconResourcePatcher.CopyIconsFromIco(stableIcon, executablePath);
                log("Codex 独立图标、窗口图标和 EXE 图标已尽可能与官方托盘图标统一。");
            }
            catch (Exception exception)
            {
                log("视觉兼容警告：主程序图标资源格式发生变化，已保留官方 EXE 并继续安装。" + exception.Message);
            }
        }

        private static void CopyIcoAtomically(string sourcePath, string destinationPath)
        {
            string source = Path.GetFullPath(sourcePath);
            string destination = Path.GetFullPath(destinationPath);
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("图标来源不能是重解析点：" + source);
            }

            string temporary = destination + ".icon-new-" + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(true);
                }
                IconResourcePatcher.ValidateIco(temporary);
                if (File.Exists(destination)) File.Replace(temporary, destination, null, true);
                else File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) NativeFileSystem.DeleteFile(temporary);
            }
        }
    }
}
