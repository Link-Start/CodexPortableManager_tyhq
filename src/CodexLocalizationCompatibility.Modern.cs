using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexPortableManager
{
    internal static partial class CodexLocalizationCompatibility
    {
        internal const string LocaleMenuRecipeId = "localization.native-menu-locale";
        internal const string LocaleMenuMarkerKey = "codexPortableManager.localization.menuRecipe";
        private const string LocaleMenuPath = "native-menu-locales/zh-CN.json";

        private static readonly KeyValuePair<string, string>[] LocaleMenuTranslations =
        {
            Pair("codex.commandMenuTitle.newWindow", "新窗口"),
            Pair("codex.commandMenuTitle.newThread", "新任务"),
            Pair("codex.commandMenuTitle.newProjectlessTask", "无项目任务"),
            Pair("codex.commandMenuTitle.openFolder", "打开目录"),
            Pair("codex.commandMenuTitle.closeWindow", "关闭"),
            Pair("codex.commandMenuTitle.settings", "设置"),
            Pair("codex.commandMenuTitle.toggleSidebar", "侧边栏"),
            Pair("codex.commandMenuTitle.toggleBottomPanel", "底部面板"),
            Pair("codex.commandMenuTitle.togglePinnedSummary", "固定摘要"),
            Pair("codex.commandMenuTitle.toggleTerminal", "打开终端"),
            Pair("codex.commandMenuTitle.toggleFileTreePanel", "文件树"),
            Pair("codex.commandMenuTitle.toggleBrowserPanel", "侧面板"),
            Pair("codex.commandMenuTitle.openBrowserTab", "浏览器标签"),
            Pair("codex.commandMenuTitle.focusBrowserAddressBar", "聚焦地址栏"),
            Pair("codex.commandMenuTitle.reloadBrowserPage", "刷新浏览器"),
            Pair("codex.commandMenuTitle.findInThread", "查找"),
            Pair("codex.commandMenuTitle.previousThread", "上一任务"),
            Pair("codex.commandMenuTitle.nextThread", "下一任务"),
            Pair("codex.commandMenuTitle.navigateBack", "后退"),
            Pair("codex.commandMenuTitle.navigateForward", "前进"),
            Pair("codex.commandMenuTitle.archiveThread", "归档任务"),
            Pair("codex.commandMenuTitle.closeTab", "关闭标签页"),
            Pair("codex.commandMenuTitle.composer.startDictation", "听写"),
            Pair("codex.commandMenuTitle.copyConversationPath", "复制对话路径"),
            Pair("codex.commandMenuTitle.copyDeeplink", "复制深层链接"),
            Pair("codex.commandMenuTitle.copySessionId", "复制会话 ID"),
            Pair("codex.commandMenuTitle.copyWorkingDirectory", "复制工作目录"),
            Pair("codex.commandMenuTitle.hardReloadBrowserPage", "强制刷新浏览器页面"),
            Pair("codex.commandMenuTitle.openAvatarOverlay", "显示宠物"),
            Pair("codex.commandMenuTitle.openCommandMenu", "打开命令菜单"),
            Pair("codex.commandMenuTitle.openProcessManager", "进程管理器"),
            Pair("codex.commandMenuTitle.openThreadInNewWindow", "在新窗口中打开"),
            Pair("codex.commandMenuTitle.renameThread", "重命名任务"),
            Pair("codex.commandMenuTitle.searchChats", "搜索对话..."),
            Pair("codex.commandMenuTitle.searchFiles", "搜索文件..."),
            Pair("codex.commandMenuTitle.showKeyboardShortcuts", "键盘快捷键"),
            Pair("codex.commandMenuTitle.thread1", "转到任务 1"),
            Pair("codex.commandMenuTitle.thread2", "转到任务 2"),
            Pair("codex.commandMenuTitle.thread3", "转到任务 3"),
            Pair("codex.commandMenuTitle.thread4", "转到任务 4"),
            Pair("codex.commandMenuTitle.thread5", "转到任务 5"),
            Pair("codex.commandMenuTitle.thread6", "转到任务 6"),
            Pair("codex.commandMenuTitle.thread7", "转到任务 7"),
            Pair("codex.commandMenuTitle.thread8", "转到任务 8"),
            Pair("codex.commandMenuTitle.thread9", "转到任务 9"),
            Pair("codex.commandMenuTitle.toggleReviewPanel", "审查面板"),
            Pair("codex.commandMenuTitle.toggleThreadPin", "固定/取消固定任务"),
            Pair("codex.commandMenuTitle.toggleTraceRecording", "开始跟踪记录"),
            Pair("electron.appMenu.help.systemStatus", "系统状态"),
            Pair("trayMenu.openApp", "打开 {appName}"),
            Pair("trayMenu.newChat", "新建任务"),
            Pair("trayMenu.pinnedThreads", "已固定"),
            Pair("trayMenu.runningThreads", "运行中"),
            Pair("trayMenu.recentThreads", "最近"),
            Pair("trayMenu.unreadThreads", "未读"),
            Pair("trayMenu.usage", "使用情况"),
            Pair("trayMenu.more", "更多"),
            Pair("trayMenu.projectlessThreads", "对话")
        };

        internal static CompatibilityFeatureChange Plan(
            AsarSession session,
            bool chineseMenusEnabled,
            bool englishReasoningEnabled,
            bool menuActive,
            bool reasoningActive,
            Action<string> log)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            LocalizationComponentChange menu = null;
            LocalizationComponentChange reasoning = null;
            bool fatal = false;
            try
            {
                session.RunStagingTransaction(delegate
                {
                    menu = menuActive
                        ? PlanLocaleMenu(session, chineseMenusEnabled)
                        : LocalizationComponentChange.NotManaged();
                    reasoning = reasoningActive
                        ? PlanReasoning(session, englishReasoningEnabled)
                        : LocalizationComponentChange.NotManaged();
                    if (menu.IsFatal || reasoning.IsFatal)
                    {
                        fatal = true;
                        throw new LocalizationPlanningException();
                    }
                });
            }
            catch (LocalizationPlanningException)
            {
                if (menu != null) menu.MarkRolledBack();
                if (reasoning != null) reasoning.MarkRolledBack();
            }
            catch (Exception exception)
            {
                SafeLog(log, "警告：Codex 界面语言变换无法完整生成，已保留完整 app.asar。原因：" + exception.Message);
                return CreatePlanFailure(
                    chineseMenusEnabled,
                    englishReasoningEnabled,
                    menuActive,
                    reasoningActive,
                    "Unknown",
                    CompatibilityFeatureStatus.Failed,
                    exception.Message);
            }

            CompatibilityFeatureChange combined = CombineComponents(
                menu ?? LocalizationComponentChange.NotManaged(),
                reasoning ?? LocalizationComponentChange.NotManaged(),
                menuActive,
                reasoningActive);
            if (fatal)
            {
                combined.Succeeded = false;
                combined.Changed = false;
                combined.Status = CompatibilityFeatureStatus.Failed;
            }
            if (!combined.Succeeded)
            {
                SafeLog(log, "警告：部分界面语言能力与当前版本不兼容；未支持的组件保持原状，其他已验证组件可独立提交。" +
                    (string.IsNullOrWhiteSpace(combined.Error) ? string.Empty : " 原因：" + combined.Error));
            }
            else if (!string.IsNullOrWhiteSpace(combined.Error))
            {
                SafeLog(log, "兼容提示：已应用可验证的界面语言部分，未匹配部分保持官方状态。" +
                    " 原因：" + combined.Error);
            }
            return combined;
        }

        internal static CompatibilityFeatureChange Inspect(AsarSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            LocalizationComponentChange menu = InspectLocaleMenuForStatus(session);
            LocalizationComponentChange reasoning = InspectReasoningForStatus(session);
            return CombineComponents(menu, reasoning, true, true);
        }

        private static LocalizationComponentChange PlanLocaleMenu(AsarSession session, bool enabled)
        {
            LocaleMenuInspection locale = InspectLocaleMenu(session);
            NativeMenuScriptInspection script = InspectNativeMenuScript(session);
            string state = GetCompleteMenuState(locale, script);
            if (state == "Mixed")
            {
                return LocalizationComponentChange.Failed(
                    "Mixed",
                    GetCompleteMenuError(locale, script) ?? "原生菜单受管变换处于混合状态。");
            }

            if (enabled)
            {
                bool localeSupported = locale.State != "Unsupported";
                bool scriptSupported = script.SupportedComponentCount > 0;
                if (!localeSupported && !scriptSupported)
                {
                    return LocalizationComponentChange.Unsupported(
                        state,
                        GetCompleteMenuError(locale, script) ?? "当前版本没有可验证的原生菜单入口。");
                }
                bool localeSatisfied = !localeSupported ||
                    (IsLocaleMenuResourceComplete(locale) && !locale.NeedsRewrite);
                bool scriptSatisfied = !scriptSupported || script.AllSupportedComponentsPatched;
                string compatibilityError = GetCompleteMenuError(locale, script);
                string compatibilityNote = string.IsNullOrWhiteSpace(compatibilityError)
                    ? null
                    : "已应用可验证的中文菜单部分，未匹配部分保持官方状态：" +
                        compatibilityError;
                if (localeSatisfied && scriptSatisfied)
                {
                    LocalizationComponentChange satisfied =
                        LocalizationComponentChange.Satisfied("Patched", LocaleMenuRecipeId);
                    satisfied.Error = compatibilityNote;
                    return satisfied;
                }

                if (localeSupported &&
                    (locale.NeedsRewrite || !IsLocaleMenuResourceComplete(locale)))
                {
                    session.StageEntry(locale.Entry, TransformLocaleMenu(locale, true));
                }
                if (scriptSupported && !script.AllSupportedComponentsPatched)
                {
                    session.StageEntry(script.Entry, TransformNativeMenuScript(script, true));
                }
                return new LocalizationComponentChange
                {
                    Succeeded = true,
                    Changed = true,
                    Before = state,
                    After = "Patched",
                    Status = CompatibilityFeatureStatus.Applied,
                    RecipeId = LocaleMenuRecipeId,
                    Error = compatibilityNote,
                    Verify = verified => VerifySupportedMenu(
                        verified,
                        true,
                        localeSupported,
                        script)
                };
            }

            bool localeManaged = locale.InsertedKeys.Length > 0;
            bool scriptManaged = script.HasManagedMarker;
            if (!localeManaged && !scriptManaged)
            {
                return LocalizationComponentChange.Satisfied("Official", LocaleMenuRecipeId);
            }
            if (localeManaged)
            {
                session.StageEntry(locale.Entry, TransformLocaleMenu(locale, false));
            }
            if (scriptManaged)
            {
                session.StageEntry(script.Entry, TransformNativeMenuScript(script, false));
            }
            return new LocalizationComponentChange
            {
                Succeeded = true,
                Changed = true,
                Before = state,
                After = "Official",
                Status = CompatibilityFeatureStatus.Applied,
                RecipeId = LocaleMenuRecipeId,
                Verify = verified => VerifySupportedMenu(
                    verified,
                    false,
                    localeManaged,
                    script)
            };
        }

        private static LocalizationComponentChange PlanReasoning(AsarSession session, bool enabled)
        {
            ReasoningInspection inspection = InspectReasoning(session);
            if (inspection.State == CompatibilityPatchState.Unsupported)
            {
                return enabled || inspection.ManagedMarkerPresent
                    ? LocalizationComponentChange.Unsupported(
                        CompatibilityPatchState.Unsupported.ToString(),
                        inspection.Error ?? "当前版本没有可验证的简体中文推理语言资源。")
                    : LocalizationComponentChange.Unmanaged();
            }
            if (inspection.State == CompatibilityPatchState.Mixed || inspection.Error != null)
            {
                return LocalizationComponentChange.Failed(
                    inspection.State.ToString(),
                    inspection.Error ?? "专业参数语言补丁处于混合状态。");
            }

            string desired = enabled
                ? CompatibilityPatchState.Patched.ToString()
                : CompatibilityPatchState.Official.ToString();
            if (inspection.State.ToString() == desired)
            {
                return LocalizationComponentChange.Satisfied(inspection.State.ToString(), RecipeId);
            }

            byte[] changed = TransformReasoning(inspection.Data, enabled);
            session.StageEntry(inspection.Entry, changed);
            return new LocalizationComponentChange
            {
                Succeeded = true,
                Changed = true,
                Before = inspection.State.ToString(),
                After = desired,
                Status = CompatibilityFeatureStatus.Applied,
                RecipeId = RecipeId,
                Verify = verified => VerifyReasoning(verified, enabled)
            };
        }

        private static LocalizationComponentChange InspectLocaleMenuForStatus(AsarSession session)
        {
            LocaleMenuInspection locale = InspectLocaleMenu(session);
            NativeMenuScriptInspection script = InspectNativeMenuScript(session);
            string state = GetCompleteMenuState(locale, script);
            if (state == "Unsupported") return LocalizationComponentChange.Unmanaged();
            if (state == "Mixed")
            {
                return LocalizationComponentChange.Failed(
                    "Mixed",
                    GetCompleteMenuError(locale, script) ?? "原生菜单受管变换处于混合状态。");
            }
            string observedState = state == "Patched" &&
                locale.NeedsRewrite
                    ? "PatchedRefreshRequired"
                    : state;
            LocalizationComponentChange satisfied =
                LocalizationComponentChange.Satisfied(observedState, LocaleMenuRecipeId);
            satisfied.Error = GetCompleteMenuError(locale, script);
            return satisfied;
        }

        private static string GetCompleteMenuState(
            LocaleMenuInspection locale,
            NativeMenuScriptInspection script)
        {
            if (locale.State == "Mixed" ||
                locale.State == "Partial" ||
                script.State == "Mixed")
            {
                return "Mixed";
            }
            bool localeSupported = locale.State != "Unsupported";
            bool scriptSupported = script.SupportedComponentCount > 0;
            bool localeComplete = !localeSupported || IsLocaleMenuResourceComplete(locale);
            bool scriptComplete = !scriptSupported || script.AllSupportedComponentsPatched;
            bool managed = locale.InsertedKeys.Length > 0 || script.HasManagedMarker;
            if (managed && localeComplete && scriptComplete) return "Patched";
            if (!managed && !localeSupported && !scriptSupported) return "Unsupported";
            bool scriptOfficial = !scriptSupported || script.AllSupportedComponentsOfficial;
            if (!managed && scriptOfficial) return "Official";
            if (locale.InsertedKeys.Length > 0 ||
                script.HasManagedMarker ||
                script.State == "Partial") return "Partial";
            if (locale.State == "Unsupported" ||
                script.SupportedComponentCount == 0) return "Unsupported";
            return "Official";
        }

        private static bool IsLocaleMenuResourceComplete(LocaleMenuInspection inspection)
        {
            return inspection.State == "Patched" || inspection.State == "NativeSupported";
        }

        private static string GetCompleteMenuError(
            LocaleMenuInspection locale,
            NativeMenuScriptInspection script)
        {
            string[] errors = new[] { locale.Error, script.Error }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            return errors.Length == 0 ? null : string.Join("；", errors);
        }

        private static LocalizationComponentChange InspectReasoningForStatus(AsarSession session)
        {
            ReasoningInspection inspection = InspectReasoning(session);
            if (inspection.State == CompatibilityPatchState.Unsupported && !inspection.ManagedMarkerPresent)
            {
                return LocalizationComponentChange.Unmanaged();
            }
            if (inspection.State == CompatibilityPatchState.Unsupported)
            {
                return LocalizationComponentChange.Unsupported(
                    inspection.State.ToString(),
                    inspection.Error ?? "无法识别已应用的专业参数语言补丁。");
            }
            if (inspection.State == CompatibilityPatchState.Mixed || inspection.Error != null)
            {
                return LocalizationComponentChange.Failed(
                    inspection.State.ToString(),
                    inspection.Error ?? "专业参数语言补丁处于混合状态。");
            }
            return LocalizationComponentChange.Satisfied(inspection.State.ToString(), RecipeId);
        }

        private static CompatibilityFeatureChange CombineComponents(
            LocalizationComponentChange menu,
            LocalizationComponentChange reasoning,
            bool menuActive,
            bool reasoningActive)
        {
            bool unsupported = menu.Status == CompatibilityFeatureStatus.Unsupported ||
                reasoning.Status == CompatibilityFeatureStatus.Unsupported;
            bool failed = menu.IsFatal || reasoning.IsFatal;
            bool changed = menu.Changed || reasoning.Changed;
            CompatibilityFeatureStatus status = failed
                ? CompatibilityFeatureStatus.Failed
                : unsupported
                    ? CompatibilityFeatureStatus.Unsupported
                    : changed
                        ? CompatibilityFeatureStatus.Applied
                        : menu.Status == CompatibilityFeatureStatus.NotRequired ||
                            reasoning.Status == CompatibilityFeatureStatus.NotRequired
                            ? CompatibilityFeatureStatus.NotRequired
                            : CompatibilityFeatureStatus.AlreadySatisfied;
            string error = string.Join("；", new[] { menu.Error, reasoning.Error }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray());
            return new CompatibilityFeatureChange
            {
                Succeeded = !failed && !unsupported,
                Changed = changed,
                Before = "Menus=" + (menuActive ? menu.Before : "NotManaged") +
                    ";Reasoning=" + (reasoningActive ? reasoning.Before : "NotManaged"),
                Desired = "Menus=" + (menuActive ? menu.After : "NotManaged") +
                    ";Reasoning=" + (reasoningActive ? reasoning.After : "NotManaged"),
                After = "Menus=" + (menuActive ? menu.After : "NotManaged") +
                    ";Reasoning=" + (reasoningActive ? reasoning.After : "NotManaged"),
                Status = status,
                Error = string.IsNullOrWhiteSpace(error) ? null : error,
                RecipeId = RecipeId,
                CompletionMessage = changed
                    ? "已按独立组件应用 Codex 界面语言设置。"
                    : "界面语言设置已经达到当前版本可支持的目标状态。",
                Verify = changed
                    ? (Action<AsarSession>)(verified =>
                    {
                        if (menu.Changed && menu.Verify != null) menu.Verify(verified);
                        if (reasoning.Changed && reasoning.Verify != null) reasoning.Verify(verified);
                    })
                    : null
            };
        }

        private static CompatibilityFeatureChange CreatePlanFailure(
            bool chineseMenusEnabled,
            bool englishReasoningEnabled,
            bool menuActive,
            bool reasoningActive,
            string before,
            CompatibilityFeatureStatus status,
            string error)
        {
            return new CompatibilityFeatureChange
            {
                Succeeded = false,
                Changed = false,
                Before = before,
                Desired = "Menus=" + (menuActive
                        ? chineseMenusEnabled ? "Patched" : "Official"
                        : "NotManaged") +
                    ";Reasoning=" + (reasoningActive
                        ? englishReasoningEnabled ? "Patched" : "Official"
                        : "NotManaged"),
                After = before,
                Status = status,
                Error = error,
                RecipeId = RecipeId
            };
        }

        private static LocaleMenuInspection InspectLocaleMenu(AsarSession session)
        {
            AsarArchiveEntry entry = session.Entries.SingleOrDefault(value =>
                string.Equals(value.Path, LocaleMenuPath, StringComparison.Ordinal));
            if (entry == null) return LocaleMenuInspection.Unsupported();

            byte[] data;
            try { data = session.GetEntryData(entry); }
            catch (Exception exception) { return LocaleMenuInspection.Unsupported(exception.Message); }

            string text = Encoding.UTF8.GetString(data);
            Dictionary<string, object> values;
            try
            {
                values = CreateJsonSerializer().DeserializeObject(text) as Dictionary<string, object>;
                if (values == null) throw new InvalidDataException("中文菜单资源根节点不是 JSON 对象。");
            }
            catch (Exception exception)
            {
                return LocaleMenuInspection.Mixed(entry, data, exception.Message);
            }

            LocaleMenuConsumerSet consumers;
            try { consumers = DiscoverLocaleMenuConsumers(session); }
            catch (Exception exception)
            {
                return LocaleMenuInspection.Unsupported(exception.Message);
            }
            string[] consumerIds = consumers.SupportedIds;
            if (consumerIds.Length == 0)
            {
                return LocaleMenuInspection.Unsupported(
                    "当前版本没有本工具可翻译的原生菜单语言消费者。" +
                    BuildUnknownConsumerNote(consumers.UnknownIds));
            }
            string consumerNote = BuildUnknownConsumerNote(consumers.UnknownIds);
            Dictionary<string, string> translations = LocaleMenuTranslations.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);

            object markerObject;
            if (!values.TryGetValue(LocaleMenuMarkerKey, out markerObject))
            {
                bool allPresent = consumerIds.All(id => HasNonEmptyString(values, id));
                return new LocaleMenuInspection
                {
                    Entry = entry,
                    Data = data,
                    Text = text,
                    State = allPresent ? "NativeSupported" : "Official",
                    ConsumerIds = consumerIds,
                    UnknownConsumerIds = consumers.UnknownIds,
                    Error = consumerNote
                };
            }

            string marker = markerObject as string;
            string managedRecipeId = ResolveManagedLocaleRecipeId(marker);
            if (managedRecipeId == null)
            {
                return LocaleMenuInspection.Mixed(entry, data, "原生中文菜单资源包含未知的管理器标记。");
            }
            string[] inserted = marker.Substring(managedRecipeId.Length + 1)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (inserted.Length == 0 || inserted.Distinct(StringComparer.Ordinal).Count() != inserted.Length)
            {
                return LocaleMenuInspection.Mixed(entry, data, "原生中文菜单资源的插入键记录无效。");
            }
            foreach (string key in inserted)
            {
                object value;
                string expected;
                if (!translations.TryGetValue(key, out expected) ||
                    !values.TryGetValue(key, out value) ||
                    !string.Equals(value as string, expected, StringComparison.Ordinal))
                {
                    return LocaleMenuInspection.Mixed(entry, data, "原生中文菜单资源的受管翻译键已被修改。");
                }
            }
            string suffix = BuildLocaleMenuSuffix(inserted, managedRecipeId);
            int close = FindJsonObjectClose(text);
            if (close < 0 || !text.Substring(0, close).EndsWith(suffix, StringComparison.Ordinal))
            {
                return LocaleMenuInspection.Mixed(entry, data, "原生中文菜单资源的可逆后缀不完整。");
            }
            bool allCurrentTranslationsPresent = consumerIds.All(id =>
                HasNonEmptyString(values, id));
            return new LocaleMenuInspection
            {
                Entry = entry,
                Data = data,
                Text = text,
                State = "Patched",
                InsertedKeys = inserted,
                ManagedRecipeId = managedRecipeId,
                ConsumerIds = consumerIds,
                UnknownConsumerIds = consumers.UnknownIds,
                Error = consumerNote,
                NeedsRewrite = !string.Equals(
                    managedRecipeId,
                    LocaleMenuRecipeId,
                    StringComparison.Ordinal) ||
                    !allCurrentTranslationsPresent ||
                    inserted.Any(key => !consumerIds.Contains(key, StringComparer.Ordinal))
            };
        }

        private static LocaleMenuConsumerSet DiscoverLocaleMenuConsumers(
            AsarSession session)
        {
            AsarArchiveEntry entry = null;
            try
            {
                entry = session.FindUniqueEntry(
                    value => value.Path.StartsWith(".vite/build/", StringComparison.Ordinal) &&
                        value.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase),
                    data => AsarSession.ContainsAscii(data, "native-menu-locales") &&
                        AsarSession.ContainsAscii(data, "menuTitleIntlId"),
                    "原生菜单语言消费者");
            }
            catch
            {
                // 资源翻译可以独立于主脚本变换工作；主脚本定位失败时仍先
                // 尝试 snapshot worker，完全没有消费者才扩大扫描。
            }
            HashSet<string> known = new HashSet<string>(
                LocaleMenuTranslations.Select(pair => pair.Key),
                StringComparer.Ordinal);
            HashSet<string> actual = new HashSet<string>(StringComparer.Ordinal);
            Action<AsarArchiveEntry, byte[]> collect = delegate(
                AsarArchiveEntry value,
                byte[] data)
            {
                string text = Encoding.UTF8.GetString(data);
                foreach (Match match in Regex.Matches(
                    text,
                    @"(?:codex\.commandMenuTitle|electron\.appMenu|trayMenu)\.[A-Za-z0-9.]+",
                    RegexOptions.CultureInvariant))
                {
                    string id = match.Value;
                    bool quoted = match.Index > 0 &&
                        match.Index + match.Length < text.Length &&
                        IsMatchingJavaScriptQuote(
                            text[match.Index - 1],
                            text[match.Index + match.Length]);
                    if (known.Contains(id) ||
                        !id.StartsWith("trayMenu.", StringComparison.Ordinal) ||
                        quoted)
                    {
                        actual.Add(id);
                    }
                }
            };
            session.ScanEntries(
                value => object.ReferenceEquals(value, entry) ||
                    value.Path.EndsWith(
                        "/child-process-snapshot-worker.js",
                        StringComparison.Ordinal),
                collect);
            if (!actual.Any(value => value.StartsWith(
                "codex.commandMenuTitle.",
                StringComparison.Ordinal)))
            {
                session.ScanEntries(
                    value => value.Path.StartsWith(".vite/build/", StringComparison.Ordinal) &&
                        value.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) &&
                        !object.ReferenceEquals(value, entry) &&
                        !value.Path.EndsWith(
                            "/child-process-snapshot-worker.js",
                            StringComparison.Ordinal),
                    collect);
            }
            return new LocaleMenuConsumerSet
            {
                SupportedIds = actual.Where(known.Contains)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                UnknownIds = actual.Where(value => !known.Contains(value))
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray()
            };
        }

        private static bool IsMatchingJavaScriptQuote(char opening, char closing)
        {
            return opening == closing &&
                (opening == '\'' || opening == '"' || opening == '`');
        }

        private static string BuildUnknownConsumerNote(string[] unknownIds)
        {
            if (unknownIds == null || unknownIds.Length == 0) return null;
            string[] shown = unknownIds.Take(6).ToArray();
            return "发现 " + unknownIds.Length +
                " 个暂无中文翻译的原生菜单键，已保持官方状态：" +
                string.Join("、", shown) +
                (shown.Length == unknownIds.Length ? "。" : " 等。 ");
        }

        private static string ResolveManagedLocaleRecipeId(string marker)
        {
            if (string.IsNullOrWhiteSpace(marker)) return null;
            int separator = marker.IndexOf('|');
            if (separator <= 0) return null;
            string recipeId = marker.Substring(0, separator);
            return string.Equals(recipeId, LocaleMenuRecipeId, StringComparison.Ordinal)
                ? recipeId
                : null;
        }

        private static byte[] TransformLocaleMenu(LocaleMenuInspection inspection, bool enabled)
        {
            if (inspection == null || inspection.Entry == null || inspection.Text == null)
            {
                throw new InvalidDataException("原生中文菜单资源不可用。");
            }
            string baseText = inspection.Text;
            if (inspection.InsertedKeys.Length > 0)
            {
                string existingSuffix = BuildLocaleMenuSuffix(
                    inspection.InsertedKeys,
                    inspection.ManagedRecipeId);
                int existingClose = FindJsonObjectClose(baseText);
                if (existingClose < 0 ||
                    !baseText.Substring(0, existingClose).EndsWith(
                        existingSuffix,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("原生中文菜单资源的受管后缀无法安全恢复。");
                }
                baseText = baseText.Substring(
                    0,
                    existingClose - existingSuffix.Length) +
                    baseText.Substring(existingClose);
            }

            string changed;
            if (enabled)
            {
                Dictionary<string, object> values = CreateJsonSerializer()
                    .DeserializeObject(baseText) as Dictionary<string, object>;
                if (values == null) throw new InvalidDataException("中文菜单资源根节点不是 JSON 对象。");
                string[] inserted = LocaleMenuTranslations
                    .Where(pair => inspection.ConsumerIds.Contains(pair.Key) &&
                        !HasNonEmptyString(values, pair.Key))
                    .Select(pair => pair.Key)
                    .ToArray();
                if (inserted.Length == 0) throw new InvalidDataException("官方中文菜单资源已经完整，无需写入管理器标记。");
                int close = FindJsonObjectClose(baseText);
                if (close < 0) throw new InvalidDataException("中文菜单资源缺少 JSON 对象结尾。");
                changed = baseText.Substring(0, close) +
                    BuildLocaleMenuSuffix(inserted) +
                    baseText.Substring(close);
            }
            else
            {
                changed = baseText;
            }
            // 转换后必须仍是结构化 JSON，状态也必须由实际内容重新判定。
            if (!(CreateJsonSerializer().DeserializeObject(changed) is Dictionary<string, object>))
            {
                throw new InvalidDataException("原生中文菜单资源变换后不是 JSON 对象。");
            }
            return Encoding.UTF8.GetBytes(changed);
        }

        private static string BuildLocaleMenuSuffix(IEnumerable<string> insertedKeys)
        {
            return BuildLocaleMenuSuffix(insertedKeys, LocaleMenuRecipeId);
        }

        private static string BuildLocaleMenuSuffix(
            IEnumerable<string> insertedKeys,
            string recipeId)
        {
            string[] keys = (insertedKeys ?? Enumerable.Empty<string>()).ToArray();
            Dictionary<string, string> translations = LocaleMenuTranslations.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            StringBuilder suffix = new StringBuilder();
            foreach (string key in keys)
            {
                string value;
                if (!translations.TryGetValue(key, out value))
                {
                    throw new InvalidDataException("原生中文菜单资源包含未知插入键：" + key);
                }
                suffix.Append(',')
                    .Append(SerializeJsonString(key))
                    .Append(':')
                    .Append(SerializeJsonString(value));
            }
            if (string.IsNullOrWhiteSpace(recipeId))
            {
                throw new InvalidDataException("中文菜单资源配方标识不能为空。");
            }
            string marker = recipeId + "|" + string.Join(",", keys);
            suffix.Append(',')
                .Append(SerializeJsonString(LocaleMenuMarkerKey))
                .Append(':')
                .Append(SerializeJsonString(marker));
            return suffix.ToString();
        }

        private static int FindJsonObjectClose(string text)
        {
            if (text == null) return -1;
            for (int index = text.Length - 1; index >= 0; index--)
            {
                if (char.IsWhiteSpace(text[index])) continue;
                return text[index] == '}' ? index : -1;
            }
            return -1;
        }

        private static string SerializeJsonString(string value)
        {
            return CreateJsonSerializer().Serialize(value);
        }

        private static JavaScriptSerializer CreateJsonSerializer()
        {
            return new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue,
                RecursionLimit = 256
            };
        }

        private static bool HasNonEmptyString(
            IDictionary<string, object> values,
            string key)
        {
            object value;
            return values != null &&
                !string.IsNullOrWhiteSpace(key) &&
                values.TryGetValue(key, out value) &&
                value is string &&
                !string.IsNullOrWhiteSpace((string)value);
        }

        private static ReasoningInspection InspectReasoning(AsarSession session)
        {
            bool markerPresent = HasReasoningMarker(session);
            try
            {
                AsarArchiveEntry entry;
                try
                {
                    entry = session.FindUniqueEntry(
                        value => value.Path.StartsWith("webview/assets/zh-CN-", StringComparison.Ordinal) &&
                            value.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase),
                        data => AsarSession.ContainsAscii(data, ReasoningFamilyMarker),
                        "简体中文语言资源");
                }
                catch
                {
                    // 语言 chunk 的 hash 或文件前缀可能变化；只有快路径失败时
                    // 才在 webview 资源中按稳定的专业参数键族重新找唯一条目。
                    entry = session.FindUniqueEntry(
                        IsReasoningAssetEntry,
                        data => AsarSession.ContainsAscii(data, ReasoningFamilyMarker),
                        "专业参数语言键族");
                }
                byte[] data = session.GetEntryData(entry);
                markerPresent = markerPresent || HasReasoningMarker(data);
                return new ReasoningInspection
                {
                    Entry = entry,
                    Data = data,
                    State = GetReasoningState(data),
                    ManagedMarkerPresent = markerPresent
                };
            }
            catch (Exception exception)
            {
                return new ReasoningInspection
                {
                    State = CompatibilityPatchState.Unsupported,
                    ManagedMarkerPresent = markerPresent,
                    Error = exception.Message
                };
            }
        }

        private static void VerifySupportedMenu(
            AsarSession session,
            bool enabled,
            bool verifyLocale,
            NativeMenuScriptInspection expectedScript)
        {
            LocaleMenuInspection locale = InspectLocaleMenu(session);
            NativeMenuScriptInspection script = InspectNativeMenuScript(session);
            if (enabled)
            {
                if (verifyLocale && !IsLocaleMenuResourceComplete(locale))
                {
                    throw new InvalidDataException("原生中文菜单资源写入后状态验证失败。");
                }
                if (expectedScript != null)
                {
                    VerifyNativeMenuScript(script, expectedScript, true);
                }
                return;
            }
            if (verifyLocale && locale.InsertedKeys.Length > 0)
            {
                throw new InvalidDataException("关闭原生中文菜单后仍存在受管资源键。");
            }
            if (expectedScript != null)
            {
                VerifyNativeMenuScript(script, expectedScript, false);
            }
        }

        private static void VerifyReasoning(AsarSession session, bool enabled)
        {
            ReasoningInspection inspection = InspectReasoning(session);
            CompatibilityPatchState expected = enabled
                ? CompatibilityPatchState.Patched
                : CompatibilityPatchState.Official;
            if (inspection.Error != null || inspection.State != expected)
            {
                throw new InvalidDataException("专业参数语言补丁写入后状态验证失败。");
            }
        }

        private static bool HasReasoningMarker(AsarSession session)
        {
            int count = 0;
            session.ScanEntries(
                IsLegacyReasoningAssetEntry,
                delegate(AsarArchiveEntry entry, byte[] data)
                {
                    string text = Encoding.UTF8.GetString(data);
                    count += ReasoningKeyFamilyRegex.Matches(text)
                        .Cast<System.Text.RegularExpressions.Match>()
                        .Count(match => string.Equals(
                            match.Groups["ending"].Value,
                            "_",
                            StringComparison.Ordinal));
                });
            return count > 0;
        }

        private static bool HasReasoningMarker(byte[] data)
        {
            if (data == null) return false;
            string text = Encoding.UTF8.GetString(data);
            return ReasoningKeyFamilyRegex.Matches(text)
                .Cast<System.Text.RegularExpressions.Match>()
                .Any(match => string.Equals(
                    match.Groups["ending"].Value,
                    "_",
                    StringComparison.Ordinal));
        }

        private static bool IsLegacyReasoningAssetEntry(AsarArchiveEntry entry)
        {
            return IsReasoningAssetEntry(entry) &&
                entry.Path.StartsWith("webview/assets/zh-CN-", StringComparison.Ordinal);
        }

        private static bool IsReasoningAssetEntry(AsarArchiveEntry entry)
        {
            return entry != null &&
                entry.Path.StartsWith("webview/assets/", StringComparison.Ordinal) &&
                entry.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class LocaleMenuInspection
        {
            internal AsarArchiveEntry Entry;
            internal byte[] Data;
            internal string Text;
            internal string State;
            internal string[] InsertedKeys = new string[0];
            internal string ManagedRecipeId;
            internal string[] ConsumerIds = new string[0];
            internal string[] UnknownConsumerIds = new string[0];
            internal string Error;
            internal bool NeedsRewrite;

            internal static LocaleMenuInspection Unsupported(string error = null)
            {
                return new LocaleMenuInspection { State = "Unsupported", Error = error };
            }

            internal static LocaleMenuInspection Mixed(
                AsarArchiveEntry entry,
                byte[] data,
                string error)
            {
                return new LocaleMenuInspection
                {
                    Entry = entry,
                    Data = data,
                    Text = data == null ? null : Encoding.UTF8.GetString(data),
                    State = "Mixed",
                    Error = error
                };
            }
        }

        private sealed class LocaleMenuConsumerSet
        {
            internal string[] SupportedIds = new string[0];
            internal string[] UnknownIds = new string[0];
        }

        private sealed class ReasoningInspection
        {
            internal AsarArchiveEntry Entry;
            internal byte[] Data;
            internal CompatibilityPatchState State;
            internal bool ManagedMarkerPresent;
            internal string Error;
        }

        private sealed class LocalizationComponentChange
        {
            internal bool Succeeded;
            internal bool Changed;
            internal string Before;
            internal string After;
            internal CompatibilityFeatureStatus Status;
            internal string Error;
            internal string RecipeId;
            internal Action<AsarSession> Verify;

            internal bool IsFatal
            {
                get { return Status == CompatibilityFeatureStatus.Failed; }
            }

            internal void MarkRolledBack()
            {
                if (!Changed) return;
                Changed = false;
                After = Before;
            }

            internal static LocalizationComponentChange Satisfied(string state, string recipeId)
            {
                return new LocalizationComponentChange
                {
                    Succeeded = true,
                    Before = state,
                    After = state,
                    Status = CompatibilityFeatureStatus.AlreadySatisfied,
                    RecipeId = recipeId
                };
            }

            internal static LocalizationComponentChange NotManaged()
            {
                return new LocalizationComponentChange
                {
                    Succeeded = true,
                    Before = "NotManaged",
                    After = "NotManaged",
                    Status = CompatibilityFeatureStatus.AlreadySatisfied,
                    RecipeId = CodexLocalizationCompatibility.RecipeId
                };
            }

            internal static LocalizationComponentChange Unmanaged()
            {
                return new LocalizationComponentChange
                {
                    Succeeded = true,
                    Before = "UnmanagedOrOfficial",
                    After = "UnmanagedOrOfficial",
                    Status = CompatibilityFeatureStatus.AlreadySatisfied,
                    RecipeId = CodexLocalizationCompatibility.RecipeId
                };
            }

            internal static LocalizationComponentChange Unsupported(string state, string error)
            {
                return new LocalizationComponentChange
                {
                    Succeeded = false,
                    Before = state,
                    After = state,
                    Status = CompatibilityFeatureStatus.Unsupported,
                    Error = error,
                    RecipeId = CodexLocalizationCompatibility.RecipeId
                };
            }

            internal static LocalizationComponentChange Failed(string state, string error)
            {
                return new LocalizationComponentChange
                {
                    Succeeded = false,
                    Before = state,
                    After = state,
                    Status = CompatibilityFeatureStatus.Failed,
                    Error = error,
                    RecipeId = CodexLocalizationCompatibility.RecipeId
                };
            }
        }

        private sealed class LocalizationPlanningException : Exception
        {
        }
    }
}
