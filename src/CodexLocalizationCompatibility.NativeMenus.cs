using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexPortableManager
{
    internal static partial class CodexLocalizationCompatibility
    {
        internal const string NativeMenuManagedPrefix =
            "/*codex-portable-manager:native-menu:";
        internal const string NativeMenuScriptMarker =
            "/*codex-portable-manager:native-menu:labels*/";
        internal const string NativeTrayExitMarker =
            "/*codex-portable-manager:native-menu:tray-exit*/";
        internal const string NativeTrayLabelsMarker =
            "/*codex-portable-manager:native-menu:tray-labels*/";
        internal const string NativeMenuSettingsStoreMarker =
            "/*codex-portable-manager:native-menu:settings-store*/";
        internal const string NativeMenuLocaleRefreshMarker =
            "/*codex-portable-manager:native-menu:locale-refresh*/";
        internal const string NativeTraceResolverMarker =
            "/*codex-portable-manager:native-menu:trace-label*/";

        private const string ManagedSettingsStoreVariable = "CPMSettingsStore";
        private const string ManagedSettingsStoreValueVariable = "CPMSettingsStoreValue";

        private const string TraceStartOfficial = "Start Performance Trace";
        private const string TraceStartPatched = "开始性能跟踪";
        private const string TraceStopOfficial = "Stop Performance Trace";
        private const string TraceStopPatched = "停止性能跟踪";

        private static readonly KeyValuePair<string, string>[] NativeMenuLabelTranslations =
        {
            Pair("File", "文件"),
            Pair("Edit", "编辑"),
            Pair("View", "视图"),
            Pair("Window", "窗口"),
            Pair("Help", "帮助"),
            Pair("Log Out", "退出登录"),
            Pair("Exit", "退出"),
            Pair("Undo", "撤销"),
            Pair("Redo", "重做"),
            Pair("Cut", "剪切"),
            Pair("Copy", "复制"),
            Pair("Paste", "粘贴"),
            Pair("Paste and Match Style", "粘贴并匹配样式"),
            Pair("Delete", "删除"),
            Pair("Select All", "全选"),
            Pair("Zoom In", "放大"),
            Pair("Zoom Out", "缩小"),
            Pair("Actual Size", "实际大小"),
            Pair("Toggle Full Screen", "切换全屏"),
            Pair("Toggle Developer Tools", "开发者工具"),
            Pair("Browser Back", "浏览器后退"),
            Pair("Browser Forward", "浏览器前进"),
            Pair("About ChatGPT", "关于 ChatGPT"),
            Pair("About Codex", "关于 Codex"),
            Pair("Documentation", "文档"),
            Pair("What's New", "更新日志"),
            Pair("Troubleshooting", "故障排除"),
            Pair("System Status", "系统状态"),
            Pair("Send Feedback", "发送反馈")
        };

        // Codex 动态菜单使用 locale JSON 的 defaultMessage；记录英文原文后才能在同一进程内从中文切回英语。
        private static readonly KeyValuePair<string, string>[] CodexMenuLabelTranslations =
        {
            Pair("Archive chat", "归档任务"),
            Pair("Close Tab", "关闭标签页"),
            Pair("Close", "关闭"),
            Pair("Dictation", "听写"),
            Pair("Copy conversation path", "复制对话路径"),
            Pair("Copy deeplink", "复制深层链接"),
            Pair("Copy session id", "复制会话 ID"),
            Pair("Copy working directory", "复制工作目录"),
            Pair("Find", "查找"),
            Pair("Focus Browser Address Bar", "聚焦地址栏"),
            Pair("Force Reload Browser Page", "强制刷新浏览器页面"),
            Pair("Back", "后退"),
            Pair("Forward", "前进"),
            Pair("New standalone chat", "无项目任务"),
            Pair("New Chat", "新任务"),
            Pair("New Task", "新任务"),
            Pair("New task", "新任务"),
            Pair("New Window", "新窗口"),
            Pair("Next Chat", "下一任务"),
            Pair("Show pet", "显示宠物"),
            Pair("Open Browser Tab", "浏览器标签"),
            Pair("Open command menu", "打开命令菜单"),
            Pair("Open Folder…", "打开目录"),
            Pair("Open Folder", "打开目录"),
            Pair("Process Manager", "进程管理器"),
            Pair("Open in New Window", "在新窗口中打开"),
            Pair("Previous Chat", "上一任务"),
            Pair("Reload Browser Page", "刷新浏览器"),
            Pair("Rename chat", "重命名任务"),
            Pair("Search Chats…", "搜索对话..."),
            Pair("Search Chats...", "搜索对话..."),
            Pair("Search Files…", "搜索文件..."),
            Pair("Search Files...", "搜索文件..."),
            Pair("Settings…", "设置"),
            Pair("Settings", "设置"),
            Pair("Keyboard Shortcuts", "键盘快捷键"),
            Pair("Go to Chat 1", "转到任务 1"),
            Pair("Go to Chat 2", "转到任务 2"),
            Pair("Go to Chat 3", "转到任务 3"),
            Pair("Go to Chat 4", "转到任务 4"),
            Pair("Go to Chat 5", "转到任务 5"),
            Pair("Go to Chat 6", "转到任务 6"),
            Pair("Go to Chat 7", "转到任务 7"),
            Pair("Go to Chat 8", "转到任务 8"),
            Pair("Go to Chat 9", "转到任务 9"),
            Pair("Toggle Bottom Panel", "底部面板"),
            Pair("Toggle Browser Panel", "侧面板"),
            Pair("Toggle File Tree", "文件树"),
            Pair("Toggle Pinned Summary", "固定摘要"),
            Pair("Toggle Review Panel", "审查面板"),
            Pair("Toggle Sidebar", "侧边栏"),
            Pair("Open Terminal", "打开终端"),
            Pair("Pin/unpin chat", "固定/取消固定任务"),
            Pair("Start Trace Recording", "开始跟踪记录"),
            Pair("System Status", "系统状态"),
            Pair("Open {appName}", "打开 {appName}"),
            Pair("Pinned", "已固定"),
            Pair("Running", "运行中"),
            Pair("Recent", "最近"),
            Pair("Unread", "未读"),
            Pair("Usage", "使用情况"),
            Pair("More", "更多"),
            Pair("Chats", "对话")
        };

        // 托盘菜单持有的 nativeIntl 可能在运行时切换语言后保留旧 locale，静态标签必须按当前设置重算。
        private static readonly KeyValuePair<string, string>[] TrayMenuLabelTranslations =
        {
            Pair("Open {appName}", "打开 {appName}"),
            Pair("New Chat", "新建任务"),
            Pair("Pinned", "已固定"),
            Pair("Running", "运行中"),
            Pair("Recent", "最近"),
            Pair("Unread", "未读"),
            Pair("Usage", "使用情况"),
            Pair("More", "更多"),
            Pair("Chats", "对话")
        };

        private static NativeMenuScriptInspection InspectNativeMenuScript(AsarSession session)
        {
            AsarArchiveEntry entry;
            try
            {
                entry = session.FindUniqueEntry(
                    IsDesktopBuildScript,
                    data => AsarSession.ContainsAscii(data, "native-menu-locales") &&
                        AsarSession.ContainsAscii(data, "menuTitleIntlId"),
                    "原生菜单主进程脚本");
            }
            catch (Exception exception)
            {
                try
                {
                    // 主进程 chunk 的名称或菜单资源引用可能随 bundler 改变；
                    // 失败时按 setApplicationMenu/受管标记扩大一次候选发现。
                    entry = session.FindUniqueEntry(
                        IsDesktopBuildScript,
                        data => AsarSession.ContainsAscii(data, "setApplicationMenu") ||
                            AsarSession.ContainsAscii(data, NativeMenuManagedPrefix),
                        "原生菜单语义入口");
                }
                catch (Exception fallbackException)
                {
                    string unexpectedEntry;
                    if (HasNativeMenuScriptMarkerOutsideEntry(
                        session,
                        null,
                        out unexpectedEntry))
                    {
                        return NativeMenuScriptInspection.Mixed(
                            null,
                            null,
                            "原生菜单受管标记存在，但唯一主脚本入口无法定位；标记条目=" +
                                unexpectedEntry + "。");
                    }
                    return NativeMenuScriptInspection.Unsupported(
                        fallbackException.Message + "；快路径=" + exception.Message);
                }
            }

            byte[] data;
            try { data = session.GetEntryData(entry); }
            catch (Exception exception)
            {
                return NativeMenuScriptInspection.Unsupported(exception.Message);
            }
            string externalMarkerEntry;
            if (HasNativeMenuScriptMarkerOutsideEntry(
                session,
                entry,
                out externalMarkerEntry))
            {
                return NativeMenuScriptInspection.Mixed(
                    entry,
                    data,
                    "原生菜单受管标记出现在非菜单主脚本条目：" +
                        externalMarkerEntry + "。");
            }
            return AnalyzeNativeMenuScript(entry, data);
        }

        private static bool IsDesktopBuildScript(AsarArchiveEntry entry)
        {
            return entry != null &&
                entry.Path.StartsWith(".vite/build/", StringComparison.Ordinal) &&
                entry.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasNativeMenuScriptMarkerOutsideEntry(
            AsarSession session,
            AsarArchiveEntry expectedEntry,
            out string firstEntry)
        {
            string foundEntry = null;
            IDictionary<string, int> totalCounts = session.CountCurrentPatterns(
                new[] { NativeMenuManagedPrefix });
            byte[] expectedData = expectedEntry == null
                ? null
                : session.GetEntryData(expectedEntry);
            bool countsMatch = expectedEntry != null &&
                totalCounts[NativeMenuManagedPrefix] ==
                    AsarSession.CountAscii(expectedData, NativeMenuManagedPrefix);
            if (countsMatch)
            {
                firstEntry = null;
                return false;
            }
            if (expectedEntry == null && totalCounts[NativeMenuManagedPrefix] == 0)
            {
                firstEntry = null;
                return false;
            }

            session.ScanEntries(
                entry => expectedEntry == null || !object.ReferenceEquals(entry, expectedEntry),
                delegate(AsarArchiveEntry entry, byte[] data)
                {
                    if (foundEntry != null) return;
                    if (AsarSession.ContainsAscii(data, NativeMenuManagedPrefix))
                    {
                        foundEntry = entry.Path;
                    }
                });
            firstEntry = foundEntry;
            return foundEntry != null;
        }

        private static NativeMenuScriptInspection AnalyzeNativeMenuScript(
            AsarArchiveEntry entry,
            byte[] data)
        {
            string text = Encoding.UTF8.GetString(data);
            int applicationMarkerCount = CountOccurrences(text, NativeMenuScriptMarker);
            int trayLabelsMarkerCount = CountOccurrences(text, NativeTrayLabelsMarker);
            int trayMarkerCount = CountOccurrences(text, NativeTrayExitMarker);
            int traceResolverMarkerCount = CountOccurrences(text, NativeTraceResolverMarker);
            int settingsStoreMarkerCount = CountOccurrences(
                text,
                NativeMenuSettingsStoreMarker);
            int localeRefreshMarkerCount = CountOccurrences(
                text,
                NativeMenuLocaleRefreshMarker);
            int knownMarkerCount = applicationMarkerCount +
                trayLabelsMarkerCount +
                trayMarkerCount +
                traceResolverMarkerCount +
                settingsStoreMarkerCount +
                localeRefreshMarkerCount;
            int managedPrefixCount = CountOccurrences(text, NativeMenuManagedPrefix);
            bool scriptMarkerPresent = managedPrefixCount > 0;
            if (managedPrefixCount != knownMarkerCount)
            {
                return NativeMenuScriptInspection.Mixed(
                    entry,
                    data,
                    "原生菜单脚本包含未知或不完整的受管标记。");
            }

            JavaScriptSemanticDocument document;
            try { document = JavaScriptSemanticDocument.Parse(text); }
            catch (Exception exception)
            {
                return scriptMarkerPresent
                    ? NativeMenuScriptInspection.Mixed(entry, data, exception.Message)
                    : NativeMenuScriptInspection.Unsupported(exception.Message);
            }
            string applicationMenuError;
            NativeMenuCommitSpan applicationMenu = FindApplicationMenuCommit(
                document,
                out applicationMenuError);
            string localeSettingExpression = FindLocaleSettingExpression(document);
            NativeTrayQuitSpan trayQuit = FindNativeTrayQuit(
                text,
                localeSettingExpression,
                document);
            string electronVariable = applicationMenu == null
                ? trayQuit == null ? null : trayQuit.ElectronVariable
                : applicationMenu.ElectronVariable;
            NativeMenuPlumbingInspection plumbing = InspectNativeMenuPlumbing(text, document);
            if (plumbing.State == "Mixed")
            {
                return NativeMenuScriptInspection.Mixed(
                    entry,
                    data,
                    plumbing.Error ?? "应用语言设置联动层处于混合状态。");
            }
            bool componentMarkerPresent = applicationMarkerCount > 0 ||
                trayLabelsMarkerCount > 0 ||
                trayMarkerCount > 0 ||
                traceResolverMarkerCount > 0;
            bool plumbingSupported = plumbing.State == "Official" ||
                plumbing.State == "Patched";
            bool commonPrerequisites = !string.IsNullOrWhiteSpace(localeSettingExpression) &&
                plumbingSupported;
            if (componentMarkerPresent && !commonPrerequisites)
            {
                return NativeMenuScriptInspection.Mixed(
                    entry,
                    data,
                    plumbing.Error ?? "受管菜单组件缺少可验证的语言设置联动入口。");
            }
            if (plumbing.State == "Patched" && !componentMarkerPresent)
            {
                return NativeMenuScriptInspection.Mixed(
                    entry,
                    data,
                    "菜单语言设置联动标记存在，但没有对应的受管菜单组件。");
            }

            if (applicationMarkerCount > 1)
            {
                return NativeMenuScriptInspection.Mixed(entry, data, "顶部菜单受管标记出现多次。");
            }
            bool applicationSupported = applicationMenu != null && commonPrerequisites;
            bool applicationOfficial = applicationSupported && applicationMarkerCount == 0;
            bool applicationPatched = false;
            if (applicationMarkerCount == 1)
            {
                if (!applicationSupported)
                {
                    return NativeMenuScriptInspection.Mixed(
                        entry,
                        data,
                        "顶部菜单受管标记缺少唯一构造锚点。");
                }
                string applicationInjection = BuildApplicationMenuInjection(
                    applicationMenu.MenuVariable,
                    electronVariable,
                    localeSettingExpression);
                applicationPatched = applicationMenu.Start >= applicationInjection.Length &&
                    string.Equals(
                        text.Substring(
                            applicationMenu.Start - applicationInjection.Length,
                            applicationInjection.Length),
                        applicationInjection,
                        StringComparison.Ordinal);
                if (!applicationPatched)
                {
                    return NativeMenuScriptInspection.Mixed(
                        entry,
                        data,
                        "顶部菜单受管标记无法对应到可精确恢复的注入区间。");
                }
            }

            JavaScriptMethodSpan trayMenuMethod = FindNativeTrayMenuMethod(text, document);
            bool trayLabelsSupported = trayMenuMethod != null &&
                !string.IsNullOrWhiteSpace(electronVariable) &&
                commonPrerequisites;
            int trayOfficialCalls = trayMenuMethod == null
                ? 0
                : CountOccurrences(trayMenuMethod.Body, "this.nativeIntl.formatMessage(");
            int trayManagedCalls = trayMenuMethod == null
                ? 0
                : CountOccurrences(trayMenuMethod.Body, "CPMTrayFormat(");
            if (trayLabelsMarkerCount > 1 ||
                (trayLabelsMarkerCount == 0 && trayManagedCalls > 0))
            {
                return NativeMenuScriptInspection.Mixed(
                    entry,
                    data,
                    "托盘标签受管调用或标记处于混合状态。");
            }
            bool trayLabelsOfficial = trayLabelsSupported &&
                trayLabelsMarkerCount == 0 &&
                trayOfficialCalls > 0 &&
                trayManagedCalls == 0;
            bool trayLabelsPatched = false;
            if (trayLabelsMarkerCount == 1)
            {
                if (!trayLabelsSupported)
                {
                    return NativeMenuScriptInspection.Mixed(
                        entry,
                        data,
                        "托盘标签受管标记缺少唯一菜单方法。");
                }
                string trayLabelsInjection = BuildNativeTrayLabelsInjection(electronVariable);
                string managedTrayBody = trayMenuMethod.Body.StartsWith(
                        trayLabelsInjection,
                        StringComparison.Ordinal)
                    ? trayMenuMethod.Body.Substring(trayLabelsInjection.Length)
                    : null;
                trayLabelsPatched = managedTrayBody != null &&
                    CountOccurrences(managedTrayBody, "CPMTrayFormat(") > 0 &&
                    CountOccurrences(
                        managedTrayBody,
                        "this.nativeIntl.formatMessage(") == 0;
                if (!trayLabelsPatched)
                {
                    return NativeMenuScriptInspection.Mixed(
                        entry,
                        data,
                        "托盘标签受管前缀无法精确恢复。");
                }
            }

            if (trayMarkerCount > 1)
            {
                return NativeMenuScriptInspection.Mixed(entry, data, "托盘退出受管标记出现多次。");
            }
            bool trayExitSupported = trayQuit != null && commonPrerequisites;
            bool trayOfficial = trayExitSupported && trayMarkerCount == 0 && !trayQuit.Patched;
            bool trayPatched = trayExitSupported && trayMarkerCount == 1 && trayQuit.Patched;
            if (trayMarkerCount == 1 && !trayPatched)
            {
                return NativeMenuScriptInspection.Mixed(
                    entry,
                    data,
                    "托盘退出受管标记无法对应到可精确恢复的表达式。");
            }

            int traceStartOfficial = CountOccurrences(text, "`" + TraceStartOfficial + "`");
            int traceStartPatched = CountOccurrences(text, "`" + TraceStartPatched + "`");
            int traceStopOfficial = CountOccurrences(text, "`" + TraceStopOfficial + "`");
            int traceStopPatched = CountOccurrences(text, "`" + TraceStopPatched + "`");
            NativeTraceResolverSpan traceResolver = FindTraceLabelResolver(
                text,
                electronVariable,
                localeSettingExpression,
                document);
            if (traceResolverMarkerCount > 1)
            {
                return NativeMenuScriptInspection.Mixed(entry, data, "性能跟踪受管标记出现多次。");
            }
            bool traceLabelsSupported = traceResolver != null &&
                !string.IsNullOrWhiteSpace(electronVariable) &&
                commonPrerequisites;
            bool traceLiteralsOfficial = traceStartOfficial == 1 &&
                traceStopOfficial == 1 &&
                traceStartPatched == 0 &&
                traceStopPatched == 0;
            bool traceOfficial = traceLabelsSupported &&
                traceLiteralsOfficial &&
                traceResolverMarkerCount == 0 &&
                !traceResolver.Patched;
            bool tracePatched = traceLabelsSupported &&
                traceLiteralsOfficial &&
                traceResolverMarkerCount == 1 &&
                traceResolver.Patched;
            if (traceResolverMarkerCount == 1 && !tracePatched)
            {
                return NativeMenuScriptInspection.Mixed(
                    entry,
                    data,
                    "性能跟踪受管标记无法对应到可精确恢复的表达式。");
            }

            List<string> unsupported = new List<string>();
            if (!applicationSupported || (!applicationOfficial && !applicationPatched))
            {
                unsupported.Add("顶部菜单" +
                    (string.IsNullOrWhiteSpace(applicationMenuError)
                        ? string.Empty
                        : "（" + applicationMenuError + "）"));
                applicationSupported = false;
            }
            if (!trayLabelsSupported || (!trayLabelsOfficial && !trayLabelsPatched))
            {
                unsupported.Add("托盘静态标签");
                trayLabelsSupported = false;
            }
            if (!trayExitSupported || (!trayOfficial && !trayPatched))
            {
                unsupported.Add("托盘退出文本");
                trayExitSupported = false;
            }
            if (!traceLabelsSupported || (!traceOfficial && !tracePatched))
            {
                unsupported.Add("性能跟踪文本");
                traceLabelsSupported = false;
            }
            int supportedCount = new[]
            {
                applicationSupported,
                trayLabelsSupported,
                trayExitSupported,
                traceLabelsSupported
            }.Count(value => value);
            int patchedCount = new[]
            {
                applicationPatched,
                trayLabelsPatched,
                trayPatched,
                tracePatched
            }.Count(value => value);
            int officialCount = new[]
            {
                applicationOfficial,
                trayLabelsOfficial,
                trayOfficial,
                traceOfficial
            }.Count(value => value);
            if (supportedCount == 0)
            {
                string error = "当前版本没有可验证的原生菜单脚本组件。" +
                    BuildUnsupportedNativeMenuComponentNote(
                        unsupported,
                        plumbing,
                        localeSettingExpression);
                return scriptMarkerPresent
                    ? NativeMenuScriptInspection.Mixed(entry, data, error)
                    : NativeMenuScriptInspection.Unsupported(error);
            }

            bool allPatched = patchedCount == supportedCount;
            bool allOfficial = officialCount == supportedCount;
            string state = unsupported.Count == 0 && allPatched
                ? "Patched"
                : unsupported.Count == 0 && allOfficial
                    ? "Official"
                    : "Partial";
            string partialNote = BuildUnsupportedNativeMenuComponentNote(
                unsupported,
                plumbing,
                localeSettingExpression);
            return new NativeMenuScriptInspection
            {
                Entry = entry,
                Data = data,
                Text = text,
                State = state,
                ApplicationMenuSupported = applicationSupported,
                ApplicationMenuOfficial = applicationOfficial,
                ApplicationMenuPatched = applicationPatched,
                TrayLabelsSupported = trayLabelsSupported,
                TrayLabelsOfficial = trayLabelsOfficial,
                TrayLabelsPatched = trayLabelsPatched,
                TrayExitSupported = trayExitSupported,
                TrayExitOfficial = trayOfficial,
                TrayExitPatched = trayPatched,
                TraceLabelsSupported = traceLabelsSupported,
                TraceLabelsOfficial = traceOfficial,
                TraceLabelsPatched = tracePatched,
                ElectronVariable = electronVariable,
                LocaleSettingExpression = localeSettingExpression,
                Plumbing = plumbing,
                HasManagedMarker = scriptMarkerPresent,
                SupportedComponentCount = supportedCount,
                AllSupportedComponentsPatched = allPatched,
                AllSupportedComponentsOfficial = allOfficial,
                Error = string.IsNullOrWhiteSpace(partialNote) ? null : partialNote
            };
        }

        private static string BuildUnsupportedNativeMenuComponentNote(
            IEnumerable<string> unsupported,
            NativeMenuPlumbingInspection plumbing,
            string localeSettingExpression)
        {
            List<string> reasons = new List<string>();
            string[] components = (unsupported ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (components.Length > 0)
            {
                reasons.Add("已跳过无法唯一验证的脚本组件：" +
                    string.Join("、", components) + "。");
            }
            if (string.IsNullOrWhiteSpace(localeSettingExpression))
            {
                reasons.Add("没有唯一的应用语言设置读取入口。");
            }
            if (plumbing == null || plumbing.State == "Unsupported")
            {
                reasons.Add(plumbing == null || string.IsNullOrWhiteSpace(plumbing.Error)
                    ? "没有可验证的菜单语言设置联动入口。"
                    : plumbing.Error);
            }
            return string.Join("", reasons.ToArray());
        }

        private static NativeMenuCommitSpan FindApplicationMenuCommit(string text)
        {
            string ignored;
            return FindApplicationMenuCommit(text, out ignored);
        }

        private static NativeMenuCommitSpan FindApplicationMenuCommit(
            string text,
            out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "原生菜单主进程脚本为空。";
                return null;
            }
            JavaScriptSemanticDocument document;
            try { document = JavaScriptSemanticDocument.Parse(text); }
            catch (Exception exception)
            {
                error = exception.Message;
                return null;
            }

            return FindApplicationMenuCommit(document, out error);
        }

        private static NativeMenuCommitSpan FindApplicationMenuCommit(
            JavaScriptSemanticDocument document,
            out string error)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            error = null;
            List<NativeMenuCommitSpan> matches = new List<NativeMenuCommitSpan>();
            List<string> rejected = new List<string>();
            int semanticCandidates = 0;
            foreach (JavaScriptNodeRecord record in document.Records)
            {
                Esprima.Ast.CallExpression call = record.Node as Esprima.Ast.CallExpression;
                if (call == null || call.Arguments.Count != 1) continue;
                string[] chain = JavaScriptSemanticDocument.GetMemberChain(call.Callee);
                if (chain.Length < 3 ||
                    !string.Equals(chain[chain.Length - 2], "Menu", StringComparison.Ordinal) ||
                    !string.Equals(
                        chain[chain.Length - 1],
                        "setApplicationMenu",
                        StringComparison.Ordinal)) continue;
                semanticCandidates++;
                string menuVariable = JavaScriptSemanticDocument.IdentifierName(call.Arguments[0]);
                if (string.IsNullOrWhiteSpace(menuVariable))
                {
                    rejected.Add("提交参数不是菜单变量");
                    continue;
                }

                JavaScriptNodeRecord scope = record.FindAncestor(node =>
                    IsFunctionNode(node)) ?? document.RecordFor(document.Root);
                IEnumerable<JavaScriptNodeRecord> context = document.Descendants(scope.Node);
                string dataFlowError;
                if (!HasApplicationMenuDataFlow(
                    context,
                    menuVariable,
                    string.Join(".", chain.Take(chain.Length - 2).ToArray()),
                    call.Range.Start,
                    scope,
                    out dataFlowError))
                {
                    rejected.Add(menuVariable + "：" + dataFlowError);
                    continue;
                }

                matches.Add(new NativeMenuCommitSpan
                {
                    Start = call.Range.Start,
                    ElectronVariable = string.Join(
                        ".",
                        chain.Take(chain.Length - 2).ToArray()),
                    MenuVariable = menuVariable
                });
            }
            if (matches.Count == 1) return matches[0];
            error = matches.Count > 1
                ? "顶部菜单提交数据流不唯一，匹配 " + matches.Count + " 项。"
                : "没有找到可验证的顶部菜单提交数据流；setApplicationMenu 候选=" +
                    semanticCandidates +
                    (rejected.Count == 0 ? string.Empty : "；" + string.Join("；", rejected.ToArray())) +
                    "。";
            return null;
        }

        private static bool HasApplicationMenuDataFlow(
            IEnumerable<JavaScriptNodeRecord> records,
            string menuVariable,
            string electronVariable,
            int commitStart,
            JavaScriptNodeRecord scope,
            out string error)
        {
            error = null;
            JavaScriptNodeRecord[] context = records
                .Where(value => value.Node.Range.Start < commitStart &&
                    IsOwnedByFunction(value, scope))
                .ToArray();
            int builders = context.Count(record => IsApplicationMenuBuild(
                record.Node,
                menuVariable,
                electronVariable));
            if (builders != 1)
            {
                error = "buildFromTemplate 构建链数量=" + builders + "。";
                return false;
            }

            int writes = context.Count(record => IsIdentifierWrite(
                record.Node,
                menuVariable));
            if (writes != 1)
            {
                error = "菜单变量写入链数量=" + writes + "。";
                return false;
            }

            bool hasLookup = context.Any(value =>
            {
                Esprima.Ast.CallExpression candidate = value.Node as Esprima.Ast.CallExpression;
                if (candidate == null) return false;
                string[] member = JavaScriptSemanticDocument.GetMemberChain(candidate.Callee);
                return member.Length >= 2 &&
                    string.Equals(member[0], menuVariable, StringComparison.Ordinal) &&
                    string.Equals(
                        member[member.Length - 1],
                        "getMenuItemById",
                        StringComparison.Ordinal);
            });
            if (!hasLookup) error = "同一菜单变量缺少 getMenuItemById 访问链。";
            return hasLookup;
        }

        private static bool IsOwnedByFunction(
            JavaScriptNodeRecord record,
            JavaScriptNodeRecord expectedOwner)
        {
            if (record == null || expectedOwner == null) return false;
            JavaScriptNodeRecord owner = record.FindAncestor(IsFunctionNode);
            if (IsFunctionNode(expectedOwner.Node))
            {
                return owner != null && object.ReferenceEquals(owner.Node, expectedOwner.Node);
            }
            return owner == null;
        }

        private static bool IsIdentifierWrite(Esprima.Ast.Node node, string name)
        {
            Esprima.Ast.VariableDeclarator declaration = node as Esprima.Ast.VariableDeclarator;
            if (declaration != null)
            {
                return string.Equals(
                    JavaScriptSemanticDocument.IdentifierName(declaration.Id),
                    name,
                    StringComparison.Ordinal);
            }
            Esprima.Ast.AssignmentExpression assignment = node as Esprima.Ast.AssignmentExpression;
            return assignment != null && string.Equals(
                JavaScriptSemanticDocument.IdentifierName(assignment.Left),
                name,
                StringComparison.Ordinal);
        }

        private static bool IsApplicationMenuBuild(
            Esprima.Ast.Node node,
            string menuVariable,
            string electronVariable)
        {
            Esprima.Ast.Expression initializer = null;
            Esprima.Ast.VariableDeclarator declaration = node as Esprima.Ast.VariableDeclarator;
            if (declaration != null && string.Equals(
                JavaScriptSemanticDocument.IdentifierName(declaration.Id),
                menuVariable,
                StringComparison.Ordinal))
            {
                initializer = declaration.Init;
            }

            Esprima.Ast.AssignmentExpression assignment = node as Esprima.Ast.AssignmentExpression;
            if (assignment != null && string.Equals(
                JavaScriptSemanticDocument.IdentifierName(assignment.Left),
                menuVariable,
                StringComparison.Ordinal))
            {
                initializer = assignment.Right;
            }

            Esprima.Ast.CallExpression call = initializer as Esprima.Ast.CallExpression;
            if (call == null || call.Arguments.Count != 1) return false;
            string[] chain = JavaScriptSemanticDocument.GetMemberChain(call.Callee);
            return chain.Length >= 3 &&
                string.Equals(
                    chain[chain.Length - 2],
                    "Menu",
                    StringComparison.Ordinal) &&
                string.Equals(
                    chain[chain.Length - 1],
                    "buildFromTemplate",
                    StringComparison.Ordinal) &&
                string.Equals(
                    string.Join(".", chain.Take(chain.Length - 2).ToArray()),
                    electronVariable,
                    StringComparison.Ordinal);
        }

        private static bool HasObjectProperty(
            IEnumerable<JavaScriptNodeRecord> records,
            string key,
            string value)
        {
            return records.Any(record =>
            {
                Esprima.Ast.Property property = record.Node as Esprima.Ast.Property;
                return property != null &&
                    string.Equals(
                        JavaScriptSemanticDocument.PropertyName(property.Key),
                        key,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        JavaScriptSemanticDocument.StringValue(property.Value),
                        value,
                        StringComparison.Ordinal);
            });
        }

        private static string BuildApplicationMenuInjection(
            string menuVariable,
            string electronVariable,
            string localeSettingExpression)
        {
            KeyValuePair<string, string>[] translationPairs = NativeMenuLabelTranslations
                .Concat(CodexMenuLabelTranslations)
                .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToArray();
            string translationJson = "{" + string.Join(
                ",",
                translationPairs.Select(pair =>
                    SerializeJsonString(pair.Key) + ":" + SerializeJsonString(pair.Value)).ToArray()) + "}";
            string fallbackJson = "{" + string.Join(
                ",",
                translationPairs.Select(pair =>
                    SerializeJsonString(pair.Value) + ":" + SerializeJsonString(pair.Key)).ToArray()) + "}";
            string chinese = BuildChineseLocaleTest(electronVariable, localeSettingExpression);
            return "((CPMMenu,CPMTranslations,CPMFallbacks,CPMChinese)=>{" +
                "let CPMLabelState=globalThis.__codexPortableManagerMenuLabels;" +
                "if(!CPMLabelState)CPMLabelState=globalThis.__codexPortableManagerMenuLabels=new WeakMap();" +
                "let CPMTranslateMenu=CPMCurrentMenu=>{" +
                "for(let CPMMenuItem of CPMCurrentMenu.items??[]){" +
                "if(CPMMenuItem&&typeof CPMMenuItem.label===\"string\"){" +
                "let CPMOriginalLabel=CPMLabelState.get(CPMMenuItem);" +
                "if(CPMOriginalLabel===void 0){CPMOriginalLabel=CPMFallbacks[CPMMenuItem.label]??CPMMenuItem.label;" +
                "CPMLabelState.set(CPMMenuItem,CPMOriginalLabel)}" +
                "CPMMenuItem.label=CPMChinese?(CPMTranslations[CPMOriginalLabel]??CPMOriginalLabel):CPMOriginalLabel}" +
                "CPMMenuItem.submenu&&CPMTranslateMenu(CPMMenuItem.submenu)}" +
                "};" +
                "CPMTranslateMenu(CPMMenu)})(" +
                menuVariable + "," + translationJson + "," + fallbackJson + "," + chinese + ")" +
                NativeMenuScriptMarker + ",";
        }

        private static string BuildChineseLocaleTest(
            string electronVariable,
            string localeSettingExpression)
        {
            return "/^zh(?:-|_|$)/i.test(" + ManagedSettingsStoreVariable +
                "?.getEffective(" + localeSettingExpression + ")??" +
                electronVariable + ".app.getLocale())";
        }

        private static string BuildNativeTrayLabelsInjection(
            string electronVariable)
        {
            string translationJson = "{" + string.Join(
                ",",
                TrayMenuLabelTranslations.Select(pair =>
                    SerializeJsonString(pair.Key) + ":" +
                    SerializeJsonString(pair.Value)).ToArray()) + "}";
            return "let CPMTrayChinese=" +
                "/^zh(?:-|_|$)/i.test(" + ManagedSettingsStoreVariable +
                "?.getEffective(`localeOverride`)??" + electronVariable +
                ".app.getLocale())" +
                ",CPMTrayTranslations=" + translationJson +
                ",CPMTrayFormat=CPMTrayMessage=>{" +
                "let CPMTrayDefault=String(CPMTrayMessage.defaultMessage??``)," +
                "CPMTrayTemplate=CPMTrayChinese?(CPMTrayTranslations[CPMTrayDefault]??" +
                "this.nativeIntl.formatMessage(CPMTrayMessage)):CPMTrayDefault;" +
                "return CPMTrayTemplate.replace(/\\{([A-Za-z_$][A-Za-z0-9_$]*)\\}/g," +
                "(CPMWhole,CPMKey)=>String(CPMTrayMessage.values?.[CPMKey]??CPMWhole))}" +
                NativeTrayLabelsMarker + ";";
        }

        private static JavaScriptMethodSpan FindNativeTrayMenuMethod(string text)
        {
            try
            {
                JavaScriptSemanticDocument document = JavaScriptSemanticDocument.Parse(text ?? string.Empty);
                return FindNativeTrayMenuMethod(text, document);
            }
            catch { return null; }
        }

        private static JavaScriptMethodSpan FindNativeTrayMenuMethod(
            string text,
            JavaScriptSemanticDocument document)
        {
            try
            {
                Esprima.Ast.MethodDefinition[] methods = document.Records
                    .Select(value => value.Node as Esprima.Ast.MethodDefinition)
                    .Where(value => value != null && string.Equals(
                        JavaScriptSemanticDocument.PropertyName(value.Key),
                        "getNativeTrayMenuItems",
                        StringComparison.Ordinal))
                    .ToArray();
                if (methods.Length == 1)
                {
                    Esprima.Ast.BlockStatement bodyNode = methods[0].Value.Body;
                    IEnumerable<JavaScriptNodeRecord> bodyRecords = document.Descendants(bodyNode);
                    bool hasTrayFormatting = bodyRecords.Any(value =>
                        value.Node is Esprima.Ast.CallExpression &&
                        JavaScriptSemanticDocument.MemberChainEndsWith(
                            ((Esprima.Ast.CallExpression)value.Node).Callee,
                            "formatMessage"));
                    // 菜单项本身可以增删；方法名和 nativeIntl 格式化调用
                    // 已经构成稳定语义，不再把某两个具体条目当作必需锚点。
                    if (hasTrayFormatting)
                    {
                        int semanticBodyStart = bodyNode.Range.Start + 1;
                        int semanticBodyLength = bodyNode.Range.End - semanticBodyStart - 1;
                        return new JavaScriptMethodSpan
                        {
                            BodyStart = semanticBodyStart,
                            BodyLength = semanticBodyLength,
                            Body = text.Substring(semanticBodyStart, semanticBodyLength)
                        };
                    }
                }
            }
            catch { return null; }
            return null;
        }

        private static string TransformNativeTrayLabels(
            string text,
            string electronVariable,
            bool enabled)
        {
            JavaScriptMethodSpan method = FindNativeTrayMenuMethod(text);
            if (method == null)
            {
                throw new InvalidDataException("托盘菜单构造入口不唯一或结构不完整。");
            }
            const string officialCall = "this.nativeIntl.formatMessage(";
            const string managedCall = "CPMTrayFormat(";
            string injection = BuildNativeTrayLabelsInjection(
                electronVariable);
            string transformedBody;
            if (enabled)
            {
                if (method.Body.IndexOf(
                        NativeTrayLabelsMarker,
                        StringComparison.Ordinal) >= 0 ||
                    CountOccurrences(method.Body, managedCall) > 0 ||
                    CountOccurrences(method.Body, officialCall) == 0)
                {
                    throw new InvalidDataException("托盘菜单静态标签不是可启用的官方结构。");
                }
                transformedBody = injection + method.Body.Replace(officialCall, managedCall);
            }
            else
            {
                if (!method.Body.StartsWith(injection, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("托盘菜单静态标签受管前缀无法精确恢复。");
                }
                string managedBody = method.Body.Substring(injection.Length);
                if (CountOccurrences(managedBody, managedCall) == 0 ||
                    CountOccurrences(managedBody, officialCall) > 0)
                {
                    throw new InvalidDataException("托盘菜单静态标签受管调用处于混合状态。");
                }
                transformedBody = managedBody.Replace(managedCall, officialCall);
            }
            return text.Substring(0, method.BodyStart) +
                transformedBody +
                text.Substring(method.BodyStart + method.BodyLength);
        }

        private static string BuildLocaleAwareTrayExpression(
            string electronVariable,
            string localeSettingExpression,
            string officialExpression)
        {
            return BuildChineseLocaleTest(electronVariable, localeSettingExpression) + "?`退出`:(" +
                officialExpression + ")";
        }

        private static NativeTrayQuitSpan FindNativeTrayQuit(
            string text,
            string localeSettingExpression)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            JavaScriptSemanticDocument document;
            try { document = JavaScriptSemanticDocument.Parse(text); }
            catch { return null; }
            return FindNativeTrayQuit(text, localeSettingExpression, document);
        }

        private static NativeTrayQuitSpan FindNativeTrayQuit(
            string text,
            string localeSettingExpression,
            JavaScriptSemanticDocument document)
        {
            List<NativeTrayQuitSpan> candidates = new List<NativeTrayQuitSpan>();
            foreach (JavaScriptNodeRecord callRecord in document.Records)
            {
                Esprima.Ast.CallExpression call = callRecord.Node as Esprima.Ast.CallExpression;
                if (call == null || !JavaScriptSemanticDocument.MemberChainEndsWith(
                        call.Callee,
                        "Menu",
                        "buildFromTemplate") ||
                    !HasObjectProperty(document.Descendants(call), "role", "quit")) continue;
                string[] chain = JavaScriptSemanticDocument.GetMemberChain(call.Callee);
                if (chain.Length < 3) continue;
                string electronVariable = string.Join(
                    ".",
                    chain.Take(chain.Length - 2).ToArray());

                JavaScriptNodeRecord declarationRecord = callRecord.FindAncestor(node =>
                    node is Esprima.Ast.VariableDeclarator);
                Esprima.Ast.VariableDeclarator declaration = declarationRecord == null
                    ? null
                    : declarationRecord.Node as Esprima.Ast.VariableDeclarator;
                string menuVariable = declaration == null
                    ? null
                    : JavaScriptSemanticDocument.IdentifierName(declaration.Id);
                JavaScriptNodeRecord functionRecord = callRecord.FindAncestor(IsFunctionNode);
                string argumentVariable = GetSingleFunctionParameter(functionRecord == null
                    ? null
                    : functionRecord.Node);
                if (menuVariable == null || argumentVariable == null || functionRecord == null) continue;

                Esprima.Ast.ReturnStatement[] returns = document.Descendants(functionRecord.Node)
                    .Where(value =>
                    {
                        if (!(value.Node is Esprima.Ast.ReturnStatement)) return false;
                        JavaScriptNodeRecord owner = value.FindAncestor(IsFunctionNode);
                        return owner != null && object.ReferenceEquals(owner.Node, functionRecord.Node);
                    })
                    .Select(value => (Esprima.Ast.ReturnStatement)value.Node)
                    .Where(value => value.Argument != null)
                    .ToArray();
                foreach (Esprima.Ast.ReturnStatement returned in returns)
                {
                    string expression = document.Slice(returned.Argument);
                    bool markerAfter = returned.Argument.Range.End + NativeTrayExitMarker.Length <= text.Length &&
                        string.Equals(
                            text.Substring(returned.Argument.Range.End, NativeTrayExitMarker.Length),
                            NativeTrayExitMarker,
                            StringComparison.Ordinal);
                    if (!markerAfter)
                    {
                        if (expression.IndexOf(menuVariable, StringComparison.Ordinal) < 0 ||
                            expression.IndexOf(argumentVariable, StringComparison.Ordinal) < 0) continue;
                        candidates.Add(new NativeTrayQuitSpan
                        {
                            ExpressionStart = returned.Argument.Range.Start,
                            ExpressionLength = returned.Argument.Range.End - returned.Argument.Range.Start,
                            ElectronVariable = electronVariable,
                            OfficialExpression = expression,
                            Patched = false
                        });
                        continue;
                    }

                    Esprima.Ast.ConditionalExpression conditional =
                        returned.Argument as Esprima.Ast.ConditionalExpression;
                    if (conditional == null ||
                        !string.Equals(
                            JavaScriptSemanticDocument.StringValue(conditional.Consequent),
                            "退出",
                            StringComparison.Ordinal) ||
                        string.IsNullOrWhiteSpace(localeSettingExpression) ||
                        !string.Equals(
                            document.Slice(conditional.Test),
                            BuildChineseLocaleTest(electronVariable, localeSettingExpression),
                            StringComparison.Ordinal)) continue;
                    string original = document.Slice(conditional.Alternate);
                    if (original.IndexOf(menuVariable, StringComparison.Ordinal) < 0 ||
                        original.IndexOf(argumentVariable, StringComparison.Ordinal) < 0) continue;
                    candidates.Add(new NativeTrayQuitSpan
                    {
                        ExpressionStart = returned.Argument.Range.Start,
                        ExpressionLength = returned.Argument.Range.End - returned.Argument.Range.Start +
                            NativeTrayExitMarker.Length,
                        ElectronVariable = electronVariable,
                        OfficialExpression = original,
                        Patched = true
                    });
                }
            }
            return candidates.Count == 1 ? candidates[0] : null;
        }

        private static bool IsFunctionNode(Esprima.Ast.Node node)
        {
            return node is Esprima.Ast.FunctionDeclaration ||
                node is Esprima.Ast.FunctionExpression ||
                node is Esprima.Ast.ArrowFunctionExpression;
        }

        private static string GetSingleFunctionParameter(Esprima.Ast.Node node)
        {
            Esprima.Ast.FunctionDeclaration declaration = node as Esprima.Ast.FunctionDeclaration;
            if (declaration != null && declaration.Params.Count >= 1)
            {
                return GetFunctionParameterName(declaration.Params[0]);
            }
            Esprima.Ast.FunctionExpression expression = node as Esprima.Ast.FunctionExpression;
            if (expression != null && expression.Params.Count >= 1)
            {
                return GetFunctionParameterName(expression.Params[0]);
            }
            Esprima.Ast.ArrowFunctionExpression arrow = node as Esprima.Ast.ArrowFunctionExpression;
            if (arrow != null && arrow.Params.Count >= 1)
            {
                return GetFunctionParameterName(arrow.Params[0]);
            }
            return null;
        }

        private static string GetFunctionParameterName(Esprima.Ast.Node parameter)
        {
            string identifier = JavaScriptSemanticDocument.IdentifierName(parameter);
            if (identifier != null) return identifier;
            Esprima.Ast.AssignmentPattern assignment =
                parameter as Esprima.Ast.AssignmentPattern;
            if (assignment != null)
            {
                return JavaScriptSemanticDocument.IdentifierName(assignment.Left);
            }
            Esprima.Ast.RestElement rest = parameter as Esprima.Ast.RestElement;
            return rest == null
                ? null
                : JavaScriptSemanticDocument.IdentifierName(rest.Argument);
        }

        private static Esprima.Ast.Node GetFirstFunctionParameter(Esprima.Ast.Node function)
        {
            Esprima.Ast.FunctionDeclaration declaration =
                function as Esprima.Ast.FunctionDeclaration;
            if (declaration != null && declaration.Params.Count > 0) return declaration.Params[0];
            Esprima.Ast.FunctionExpression expression =
                function as Esprima.Ast.FunctionExpression;
            if (expression != null && expression.Params.Count > 0) return expression.Params[0];
            Esprima.Ast.ArrowFunctionExpression arrow =
                function as Esprima.Ast.ArrowFunctionExpression;
            return arrow != null && arrow.Params.Count > 0 ? arrow.Params[0] : null;
        }

        private static Esprima.Ast.BlockStatement GetFunctionBody(Esprima.Ast.Node function)
        {
            Esprima.Ast.FunctionDeclaration declaration =
                function as Esprima.Ast.FunctionDeclaration;
            if (declaration != null) return declaration.Body;
            Esprima.Ast.FunctionExpression expression =
                function as Esprima.Ast.FunctionExpression;
            if (expression != null) return expression.Body;
            Esprima.Ast.ArrowFunctionExpression arrow =
                function as Esprima.Ast.ArrowFunctionExpression;
            return arrow == null ? null : arrow.Body as Esprima.Ast.BlockStatement;
        }

        private static string GetFunctionBindingName(
            JavaScriptNodeRecord record,
            Esprima.Ast.Node function)
        {
            Esprima.Ast.FunctionDeclaration declaration =
                function as Esprima.Ast.FunctionDeclaration;
            if (declaration != null && declaration.Id != null) return declaration.Id.Name;
            Esprima.Ast.FunctionExpression expression =
                function as Esprima.Ast.FunctionExpression;
            if (expression != null && expression.Id != null) return expression.Id.Name;

            JavaScriptNodeRecord binding = record.FindAncestor(node =>
                node is Esprima.Ast.VariableDeclarator ||
                node is Esprima.Ast.AssignmentExpression);
            Esprima.Ast.VariableDeclarator declarationBinding = binding == null
                ? null
                : binding.Node as Esprima.Ast.VariableDeclarator;
            if (declarationBinding != null &&
                object.ReferenceEquals(declarationBinding.Init, function))
            {
                return JavaScriptSemanticDocument.IdentifierName(declarationBinding.Id);
            }
            Esprima.Ast.AssignmentExpression assignment = binding == null
                ? null
                : binding.Node as Esprima.Ast.AssignmentExpression;
            return assignment != null && object.ReferenceEquals(assignment.Right, function)
                ? JavaScriptSemanticDocument.IdentifierName(assignment.Left)
                : null;
        }

        private static int GetFunctionBodyInsertionIndex(Esprima.Ast.BlockStatement body)
        {
            int index = body.Range.Start + 1;
            foreach (Esprima.Ast.Node child in body.ChildNodes)
            {
                Esprima.Ast.ExpressionStatement statement =
                    child as Esprima.Ast.ExpressionStatement;
                if (statement == null ||
                    JavaScriptSemanticDocument.StringValue(statement.Expression) == null)
                {
                    break;
                }
                index = statement.Range.End;
            }
            return index;
        }

        private static NativeMenuFactoryCandidate TryCreateNativeMenuFactory(
            JavaScriptSemanticDocument document,
            JavaScriptNodeRecord record)
        {
            if (record == null || !IsFunctionNode(record.Node)) return null;
            Esprima.Ast.ObjectPattern pattern =
                GetFirstFunctionParameter(record.Node) as Esprima.Ast.ObjectPattern;
            Esprima.Ast.BlockStatement body = GetFunctionBody(record.Node);
            if (pattern == null || body == null ||
                !HasDirectProperty(pattern, "buildFlavor") ||
                !HasDirectProperty(pattern, "globalState") ||
                !HasDirectProperty(pattern, "appVersion") ||
                HasDirectProperty(pattern, "settingsStore") ||
                pattern.ChildNodes.Any(child => child is Esprima.Ast.RestElement))
            {
                return null;
            }
            string name = GetFunctionBindingName(record, record.Node);
            if (string.IsNullOrWhiteSpace(name)) return null;
            JavaScriptNodeRecord declaration = record.FindAncestor(node =>
                node is Esprima.Ast.VariableDeclaration ||
                node is Esprima.Ast.ExpressionStatement);
            return new NativeMenuFactoryCandidate
            {
                Function = record.Node,
                Pattern = pattern,
                Body = body,
                Name = name,
                DeclarationInsertionIndex = declaration == null
                    ? record.Node.Range.Start
                    : declaration.Node.Range.Start,
                BodyInsertionIndex = GetFunctionBodyInsertionIndex(body)
            };
        }

        private static bool HasRefreshApplicationMenu(
            JavaScriptSemanticDocument document,
            Esprima.Ast.MethodDefinition effect)
        {
            if (effect == null || effect.Value == null || effect.Value.Body == null) return false;
            if (document.Descendants(effect.Value.Body).Any(record =>
                record.Node is Esprima.Ast.CallExpression &&
                JavaScriptSemanticDocument.MemberChainEndsWith(
                    ((Esprima.Ast.CallExpression)record.Node).Callee,
                    "refreshApplicationMenu")))
            {
                return true;
            }
            JavaScriptNodeRecord effectRecord = document.RecordFor(effect);
            JavaScriptNodeRecord owner = effectRecord == null
                ? null
                : effectRecord.FindAncestor(node =>
                    node is Esprima.Ast.ClassBody || node is Esprima.Ast.ObjectExpression);
            IEnumerable<JavaScriptNodeRecord> candidates = owner == null
                ? document.Records
                : document.Descendants(owner.Node);
            return candidates.Any(record =>
            {
                Esprima.Ast.MethodDefinition method = record.Node as Esprima.Ast.MethodDefinition;
                if (method == null || !string.Equals(
                    JavaScriptSemanticDocument.PropertyName(method.Key),
                    "refreshApplicationMenu",
                    StringComparison.Ordinal)) return false;
                if (owner == null) return true;
                JavaScriptNodeRecord methodOwner = record.FindAncestor(node =>
                    node is Esprima.Ast.ClassBody || node is Esprima.Ast.ObjectExpression);
                return methodOwner != null && object.ReferenceEquals(methodOwner.Node, owner.Node);
            }) ||
                // 旧版设置类没有单独暴露 refreshApplicationMenu，但其唯一的
                // applySettingSideEffects 仍是稳定入口；保留该兼容后备。
                document.Descendants(effect.Value.Body).Any(record =>
                    record.Node is Esprima.Ast.MemberExpression &&
                    JavaScriptSemanticDocument.MemberChainEndsWith(
                        (Esprima.Ast.Expression)record.Node,
                        "DOCK_ICON_PREFERENCE")) &&
                document.Descendants(effect.Value.Body).Any(record =>
                    record.Node is Esprima.Ast.CallExpression &&
                    JavaScriptSemanticDocument.MemberChainEndsWith(
                        ((Esprima.Ast.CallExpression)record.Node).Callee,
                        "updateDockIcon"));
        }

        private static string BuildLocaleAwareTraceExpression(
            string officialExpression,
            string electronVariable,
            string localeSettingExpression)
        {
            const string label = "CPMTraceLabel";
            return "(" + label + "=>" + BuildChineseLocaleTest(electronVariable, localeSettingExpression) +
                "?{" + SerializeJsonString(TraceStartOfficial) + ":" +
                SerializeJsonString(TraceStartPatched) + "," +
                SerializeJsonString(TraceStopOfficial) + ":" +
                SerializeJsonString(TraceStopPatched) + "}[" + label + "]??" + label + ":" +
                label + ")(" + officialExpression + ")";
        }

        private static NativeTraceResolverSpan FindTraceLabelResolver(
            string text,
            string electronVariable,
            string localeSettingExpression)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(electronVariable) ||
                string.IsNullOrWhiteSpace(localeSettingExpression)) return null;
            JavaScriptSemanticDocument document;
            try { document = JavaScriptSemanticDocument.Parse(text); }
            catch { return null; }
            return FindTraceLabelResolver(
                text,
                electronVariable,
                localeSettingExpression,
                document);
        }

        private static NativeTraceResolverSpan FindTraceLabelResolver(
            string text,
            string electronVariable,
            string localeSettingExpression,
            JavaScriptSemanticDocument document)
        {
            List<NativeTraceResolverSpan> candidates = new List<NativeTraceResolverSpan>();
            foreach (JavaScriptNodeRecord record in document.Records)
            {
                Esprima.Ast.ReturnStatement returned = record.Node as Esprima.Ast.ReturnStatement;
                if (returned == null || returned.Argument == null) continue;
                JavaScriptNodeRecord function = record.FindAncestor(IsFunctionNode);
                string stateVariable = GetSingleFunctionParameter(function == null ? null : function.Node);
                if (stateVariable == null) continue;

                bool markerAfter = returned.Argument.Range.End + NativeTraceResolverMarker.Length <= text.Length &&
                    string.Equals(
                        text.Substring(returned.Argument.Range.End, NativeTraceResolverMarker.Length),
                        NativeTraceResolverMarker,
                        StringComparison.Ordinal);
                if (!markerAfter)
                {
                    if (!IsTraceStateExpression(document, returned.Argument, stateVariable)) continue;
                    candidates.Add(new NativeTraceResolverSpan
                    {
                        ExpressionStart = returned.Argument.Range.Start,
                        ExpressionLength = returned.Argument.Range.End - returned.Argument.Range.Start,
                        OfficialExpression = document.Slice(returned.Argument),
                        Patched = false
                    });
                    continue;
                }

                Esprima.Ast.CallExpression managed = returned.Argument as Esprima.Ast.CallExpression;
                Esprima.Ast.ArrowFunctionExpression wrapper = managed == null
                    ? null
                    : managed.Callee as Esprima.Ast.ArrowFunctionExpression;
                Esprima.Ast.Expression original = managed == null || managed.Arguments.Count != 1
                    ? null
                    : managed.Arguments[0] as Esprima.Ast.Expression;
                if (wrapper == null || original == null ||
                    !IsTraceStateExpression(document, original, stateVariable)) continue;
                string official = document.Slice(original);
                if (!string.Equals(
                    document.Slice(managed),
                    BuildLocaleAwareTraceExpression(
                        official,
                        electronVariable,
                        localeSettingExpression),
                    StringComparison.Ordinal)) continue;
                candidates.Add(new NativeTraceResolverSpan
                {
                    ExpressionStart = returned.Argument.Range.Start,
                    ExpressionLength = returned.Argument.Range.End - returned.Argument.Range.Start +
                        NativeTraceResolverMarker.Length,
                    OfficialExpression = official,
                    Patched = true
                });
            }
            return candidates.Count == 1 ? candidates[0] : null;
        }

        private static bool IsTraceStateExpression(
            JavaScriptSemanticDocument document,
            Esprima.Ast.Expression expression,
            string stateVariable)
        {
            JavaScriptNodeRecord expressionRecord = document.RecordFor(expression);
            if (expressionRecord == null) return false;
            JavaScriptNodeRecord[] descendants = document.Descendants(expression).ToArray();
            HashSet<string> states = new HashSet<string>(
                descendants.Select(value => JavaScriptSemanticDocument.StringValue(value.Node))
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.Ordinal);
            bool readsState = descendants.Any(value => string.Equals(
                JavaScriptSemanticDocument.IdentifierName(value.Node),
                stateVariable,
                StringComparison.Ordinal));
            int secondaryStates = new[]
            {
                "awaiting-start-confirmation", "saving", "awaiting-upload-details", "uploading"
            }.Count(states.Contains);
            return readsState && states.Contains("recording") && secondaryStates >= 1;
        }

        private static string FindLocaleSettingExpression(string text)
        {
            try
            {
                JavaScriptSemanticDocument document = JavaScriptSemanticDocument.Parse(text ?? string.Empty);
                return FindLocaleSettingExpression(document);
            }
            catch { return null; }
        }

        private static string FindLocaleSettingExpression(
            JavaScriptSemanticDocument document)
        {
            try
            {
                string[] semanticExpressions = document.Records
                    .Select(value => value.Node as Esprima.Ast.CallExpression)
                    .Where(value => value != null && value.Arguments.Count == 1 &&
                        JavaScriptSemanticDocument.MemberChainEndsWith(
                            value.Callee,
                            "getEffective") &&
                        value.Arguments[0] is Esprima.Ast.Expression &&
                        JavaScriptSemanticDocument.MemberChainEndsWith(
                            (Esprima.Ast.Expression)value.Arguments[0],
                            "localeOverride",
                            "key"))
                    .Select(value => document.Slice(value.Arguments[0]))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (semanticExpressions.Length == 1) return semanticExpressions[0];
            }
            catch { return null; }
            return null;
        }

        private static NativeMenuPlumbingInspection InspectNativeMenuPlumbing(string text)
        {
            return InspectSemanticNativeMenuPlumbing(text);
        }

        private static NativeMenuPlumbingInspection InspectNativeMenuPlumbing(
            string text,
            JavaScriptSemanticDocument document)
        {
            return InspectSemanticNativeMenuPlumbing(text, document);
        }

        private static NativeMenuPlumbingInspection InspectSemanticNativeMenuPlumbing(string text)
        {
            JavaScriptSemanticDocument document = null;
            if (!string.IsNullOrWhiteSpace(text) &&
                CountOccurrences(text, NativeMenuSettingsStoreMarker) == 0 &&
                CountOccurrences(text, NativeMenuLocaleRefreshMarker) == 0)
            {
                try { document = JavaScriptSemanticDocument.Parse(text); }
                catch (Exception exception)
                {
                    return NativeMenuPlumbingInspection.Unsupported(exception.Message);
                }
            }
            return InspectSemanticNativeMenuPlumbing(text, document);
        }

        private static NativeMenuPlumbingInspection InspectSemanticNativeMenuPlumbing(
            string text,
            JavaScriptSemanticDocument document)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return NativeMenuPlumbingInspection.Unsupported("原生菜单脚本为空。");
            }
            string globalDeclaration = "let " + ManagedSettingsStoreVariable +
                NativeMenuSettingsStoreMarker + ";";
            string assignment = ManagedSettingsStoreVariable + "=" +
                ManagedSettingsStoreValueVariable + NativeMenuSettingsStoreMarker + ";";
            int settingsMarkers = CountOccurrences(text, NativeMenuSettingsStoreMarker);
            int refreshMarkers = CountOccurrences(text, NativeMenuLocaleRefreshMarker);
            if (settingsMarkers > 0 || refreshMarkers > 0)
            {
                if (settingsMarkers != 4 || refreshMarkers != 1 ||
                    CountOccurrences(text, globalDeclaration) != 1 ||
                    CountOccurrences(text, assignment) != 1 ||
                    CountOccurrences(
                        text,
                        ",settingsStore:" + ManagedSettingsStoreValueVariable +
                            NativeMenuSettingsStoreMarker) != 1)
                {
                    return NativeMenuPlumbingInspection.Mixed(
                        "菜单语言设置语义配方标记不完整。");
                }
                string restored = text;
                try
                {
                    restored = RestoreSemanticNativeMenuPlumbing(text);
                }
                catch (Exception exception)
                {
                    return NativeMenuPlumbingInspection.Mixed(exception.Message);
                }
                NativeMenuPlumbingInspection official = InspectSemanticNativeMenuPlumbing(restored);
                if (!string.Equals(official.State, "Official", StringComparison.Ordinal))
                {
                    return NativeMenuPlumbingInspection.Mixed(
                        "菜单语言设置语义配方无法恢复为可验证的官方结构。");
                }
                official.State = "Patched";
                return official;
            }

            if (document == null)
            {
                return NativeMenuPlumbingInspection.Unsupported(
                    "菜单语言设置分析缺少 JavaScript 语义文档。");
            }

            if (Regex.IsMatch(
                    text,
                    @"\b(?:let|const|var|function|class)\s+(?:" +
                        ManagedSettingsStoreVariable + "|" +
                        ManagedSettingsStoreValueVariable + @")\b",
                    RegexOptions.CultureInvariant))
            {
                return NativeMenuPlumbingInspection.Unsupported(
                    "菜单脚本已占用管理器保留的 settings store 标识符。");
            }

            NativeMenuFactoryCandidate[] factories = document.Records
                .Select(record => TryCreateNativeMenuFactory(document, record))
                .Where(value => value != null)
                .ToArray();
            if (factories.Length != 1)
            {
                return NativeMenuPlumbingInspection.Unsupported(
                    "当前版本没有唯一的菜单管理器解构参数入口。");
            }
            NativeMenuFactoryCandidate factory = factories[0];
            string factoryName = factory.Name;
            Esprima.Ast.ObjectPattern pattern = factory.Pattern;

            List<Tuple<Esprima.Ast.CallExpression, Esprima.Ast.ObjectExpression, string>> invocations =
                new List<Tuple<Esprima.Ast.CallExpression, Esprima.Ast.ObjectExpression, string>>();
            foreach (Esprima.Ast.CallExpression call in document.Records
                .Select(value => value.Node as Esprima.Ast.CallExpression)
                .Where(value => value != null && value.Arguments.Count > 0 &&
                    string.Equals(
                        JavaScriptSemanticDocument.IdentifierName(value.Callee),
                        factoryName,
                        StringComparison.Ordinal)))
            {
                Esprima.Ast.ObjectExpression argument = call.Arguments[0] as Esprima.Ast.ObjectExpression;
                Esprima.Ast.Property global = FindDirectProperty(argument, "globalState");
                Esprima.Ast.Expression globalValue = global == null
                    ? null
                    : global.Value as Esprima.Ast.Expression;
                string[] chain = globalValue == null
                    ? new string[0]
                    : JavaScriptSemanticDocument.GetMemberChain(globalValue);
                if (argument == null || !HasDirectProperty(argument, "buildFlavor") ||
                    !HasDirectProperty(argument, "appVersion") ||
                    HasDirectProperty(argument, "settingsStore") || chain.Length < 2 ||
                    !string.Equals(
                        chain[chain.Length - 1],
                        "globalState",
                        StringComparison.Ordinal)) continue;
                invocations.Add(Tuple.Create(
                    call,
                    argument,
                    string.Join(".", chain.Take(chain.Length - 1).ToArray())));
            }
            if (invocations.Count != 1)
            {
                return NativeMenuPlumbingInspection.Unsupported(
                    "当前版本没有唯一的菜单管理器创建入口。");
            }

            Esprima.Ast.MethodDefinition[] effects = document.Records
                .Select(value => value.Node as Esprima.Ast.MethodDefinition)
                .Where(value => value != null && string.Equals(
                    JavaScriptSemanticDocument.PropertyName(value.Key),
                    "applySettingSideEffects",
                    StringComparison.Ordinal) &&
                    value.Value.Params.Count >= 1 &&
                    GetSingleFunctionParameter(value.Value) != null &&
                    HasRefreshApplicationMenu(document, value))
                .ToArray();
            if (effects.Length != 1)
            {
                return NativeMenuPlumbingInspection.Unsupported(
                    "当前版本没有唯一的语言设置副作用入口。");
            }
            string keyVariable = GetSingleFunctionParameter(effects[0].Value);
            if (keyVariable == null)
            {
                return NativeMenuPlumbingInspection.Unsupported("语言设置键参数不是简单标识符。");
            }

            string context = invocations[0].Item3;
            return new NativeMenuPlumbingInspection
            {
                State = "Official",
                SemanticEdits = new[]
                {
                    new JavaScriptInsertion(factory.DeclarationInsertionIndex, globalDeclaration),
                    new JavaScriptInsertion(
                        pattern.Range.End - 1,
                        ",settingsStore:" + ManagedSettingsStoreValueVariable +
                            NativeMenuSettingsStoreMarker),
                    new JavaScriptInsertion(factory.BodyInsertionIndex, assignment),
                    new JavaScriptInsertion(
                        invocations[0].Item2.Range.End - 1,
                        ",settingsStore:" + context + ".settingsStore" +
                            NativeMenuSettingsStoreMarker),
                    new JavaScriptInsertion(
                        effects[0].Value.Body.Range.End - 1,
                        ";" + keyVariable + "===`localeOverride`&&this.refreshApplicationMenu()" +
                            NativeMenuLocaleRefreshMarker + ";")
                }
            };
        }

        private static bool HasDirectProperty(Esprima.Ast.Node node, string name)
        {
            return FindDirectProperty(node, name) != null;
        }

        private static string RestoreSemanticNativeMenuPlumbing(string text)
        {
            string restored = text;
            string globalDeclaration = "let " + ManagedSettingsStoreVariable +
                NativeMenuSettingsStoreMarker + ";";
            string assignment = ManagedSettingsStoreVariable + "=" +
                ManagedSettingsStoreValueVariable + NativeMenuSettingsStoreMarker + ";";
            restored = ReplaceOnce(restored, globalDeclaration, string.Empty);
            restored = ReplaceOnce(restored, assignment, string.Empty);
            restored = ReplaceOnce(
                restored,
                ",settingsStore:" + ManagedSettingsStoreValueVariable +
                    NativeMenuSettingsStoreMarker,
                string.Empty);
            Match[] invocationProperties = Regex.Matches(
                restored,
                @",settingsStore:(?<context>[A-Za-z_$][A-Za-z0-9_$]*)\.settingsStore" +
                    Regex.Escape(NativeMenuSettingsStoreMarker),
                RegexOptions.CultureInvariant).Cast<Match>().ToArray();
            if (invocationProperties.Length != 1)
            {
                throw new InvalidDataException("菜单管理器 settingsStore 调用参数不唯一。");
            }
            restored = restored.Substring(0, invocationProperties[0].Index) +
                restored.Substring(invocationProperties[0].Index + invocationProperties[0].Length);
            Match[] refreshes = Regex.Matches(
                restored,
                @";?(?<key>[A-Za-z_$][A-Za-z0-9_$]*)===`localeOverride`&&" +
                @"this\.refreshApplicationMenu\(\)" +
                Regex.Escape(NativeMenuLocaleRefreshMarker) + @";?",
                RegexOptions.CultureInvariant).Cast<Match>().ToArray();
            if (refreshes.Length != 1)
            {
                throw new InvalidDataException("语言设置刷新受管表达式不唯一。");
            }
            return restored.Substring(0, refreshes[0].Index) +
                restored.Substring(refreshes[0].Index + refreshes[0].Length);
        }

        private static Esprima.Ast.Property FindDirectProperty(Esprima.Ast.Node node, string name)
        {
            if (node == null) return null;
            return node.ChildNodes
                .Select(value => value as Esprima.Ast.Property)
                .FirstOrDefault(value => value != null && string.Equals(
                    JavaScriptSemanticDocument.PropertyName(value.Key),
                    name,
                    StringComparison.Ordinal));
        }

        private static string TransformNativeMenuPlumbing(
            string text,
            NativeMenuPlumbingInspection plumbing,
            bool enabled)
        {
            if (plumbing == null)
            {
                throw new InvalidDataException("原生菜单语言设置联动层不可用。");
            }
            bool currentlyEnabled = string.Equals(
                plumbing.State,
                "Patched",
                StringComparison.Ordinal);
            if (currentlyEnabled == enabled) return text;

            if (!enabled)
            {
                return RestoreSemanticNativeMenuPlumbing(text);
            }
            if (plumbing.SemanticEdits == null || plumbing.SemanticEdits.Length == 0)
            {
                throw new InvalidDataException("菜单语言设置语义配方缺少插入计划。");
            }
            foreach (JavaScriptInsertion insertion in plumbing.SemanticEdits
                .OrderByDescending(value => value.Index))
            {
                text = text.Substring(0, insertion.Index) + insertion.Text +
                    text.Substring(insertion.Index);
            }
            return text;
        }

        private static byte[] TransformNativeMenuScript(
            NativeMenuScriptInspection inspection,
            bool enabled)
        {
            if (inspection == null || inspection.Entry == null || inspection.Text == null)
            {
                throw new InvalidDataException("原生菜单主进程脚本不可用。");
            }
            string text = inspection.Text;
            if (enabled)
            {
                if (inspection.ApplicationMenuSupported &&
                    !inspection.ApplicationMenuPatched)
                {
                    NativeMenuCommitSpan match = FindApplicationMenuCommit(text);
                    if (match == null) throw new InvalidDataException("顶部菜单提交锚点不唯一。");
                    string injection = BuildApplicationMenuInjection(
                        match.MenuVariable,
                        match.ElectronVariable,
                        inspection.LocaleSettingExpression);
                    text = text.Substring(0, match.Start) + injection + text.Substring(match.Start);
                }
                if (inspection.TrayLabelsSupported &&
                    !inspection.TrayLabelsPatched)
                {
                    text = TransformNativeTrayLabels(
                        text,
                        inspection.ElectronVariable,
                        true);
                }
                if (inspection.TrayExitSupported &&
                    !inspection.TrayExitPatched)
                {
                    NativeTrayQuitSpan tray = FindNativeTrayQuit(
                        text,
                        inspection.LocaleSettingExpression);
                    if (tray == null || tray.Patched)
                    {
                        throw new InvalidDataException("托盘退出菜单语义锚点不唯一。");
                    }
                    string replacement = BuildLocaleAwareTrayExpression(
                        tray.ElectronVariable,
                        inspection.LocaleSettingExpression,
                        tray.OfficialExpression) + NativeTrayExitMarker;
                    text = text.Substring(0, tray.ExpressionStart) + replacement +
                        text.Substring(tray.ExpressionStart + tray.ExpressionLength);
                }
                if (inspection.TraceLabelsSupported &&
                    !inspection.TraceLabelsPatched)
                {
                    NativeTraceResolverSpan resolver = FindTraceLabelResolver(
                        text,
                        inspection.ElectronVariable,
                        inspection.LocaleSettingExpression);
                    if (resolver == null || resolver.Patched)
                    {
                        throw new InvalidDataException("性能跟踪菜单语言入口不唯一。");
                    }
                    string replacement = BuildLocaleAwareTraceExpression(
                        resolver.OfficialExpression,
                        inspection.ElectronVariable,
                        inspection.LocaleSettingExpression) + NativeTraceResolverMarker;
                    text = text.Substring(0, resolver.ExpressionStart) + replacement +
                        text.Substring(resolver.ExpressionStart + resolver.ExpressionLength);
                }
                if (inspection.SupportedComponentCount > 0)
                {
                    text = TransformNativeMenuPlumbing(
                        text,
                        InspectNativeMenuPlumbing(text),
                        true);
                }
            }
            else
            {
                if (inspection.ApplicationMenuPatched)
                {
                    NativeMenuCommitSpan match = FindApplicationMenuCommit(text);
                    if (match == null) throw new InvalidDataException("顶部菜单提交锚点不唯一。");
                    string injection = BuildApplicationMenuInjection(
                        match.MenuVariable,
                        match.ElectronVariable,
                        inspection.LocaleSettingExpression);
                    int injectionIndex = match.Start - injection.Length;
                    if (injectionIndex < 0 ||
                        !string.Equals(
                            text.Substring(injectionIndex, injection.Length),
                            injection,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("顶部菜单受管变换无法精确恢复。");
                    }
                    text = text.Substring(0, injectionIndex) +
                            text.Substring(match.Start);
                }
                if (inspection.TrayLabelsPatched)
                {
                    text = TransformNativeTrayLabels(
                        text,
                        inspection.ElectronVariable,
                        false);
                }
                if (inspection.TrayExitPatched)
                {
                    NativeTrayQuitSpan tray = FindNativeTrayQuit(
                        text,
                        inspection.LocaleSettingExpression);
                    if (tray == null || !tray.Patched)
                    {
                        throw new InvalidDataException("托盘退出受管变换无法精确恢复。");
                    }
                    text = text.Substring(0, tray.ExpressionStart) + tray.OfficialExpression +
                        text.Substring(tray.ExpressionStart + tray.ExpressionLength);
                }
                if (inspection.TraceLabelsPatched)
                {
                    NativeTraceResolverSpan resolver = FindTraceLabelResolver(
                        text,
                        inspection.ElectronVariable,
                        inspection.LocaleSettingExpression);
                    if (resolver == null || !resolver.Patched)
                    {
                        throw new InvalidDataException("性能跟踪受管语言入口无法精确恢复。");
                    }
                    text = text.Substring(0, resolver.ExpressionStart) + resolver.OfficialExpression +
                        text.Substring(resolver.ExpressionStart + resolver.ExpressionLength);
                }
                if (inspection.Plumbing != null &&
                    string.Equals(
                        inspection.Plumbing.State,
                        "Patched",
                        StringComparison.Ordinal))
                {
                    text = TransformNativeMenuPlumbing(text, inspection.Plumbing, false);
                }
            }

            byte[] transformed = Encoding.UTF8.GetBytes(text);
            NativeMenuScriptInspection verified = AnalyzeNativeMenuScript(
                inspection.Entry,
                transformed);
            VerifyNativeMenuScript(verified, inspection, enabled);
            return transformed;
        }

        private static void VerifyNativeMenuScript(
            NativeMenuScriptInspection actual,
            NativeMenuScriptInspection expected,
            bool enabled)
        {
            if (expected == null ||
                (expected.SupportedComponentCount == 0 && !expected.HasManagedMarker))
            {
                return;
            }
            if (actual == null ||
                string.Equals(actual.State, "Mixed", StringComparison.Ordinal) ||
                string.Equals(actual.State, "Unsupported", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "原生菜单主进程脚本变换后无法验证：" +
                    (actual == null || string.IsNullOrWhiteSpace(actual.Error)
                        ? "状态不可用。"
                        : actual.Error));
            }

            bool componentsMatch = enabled
                ? (!expected.ApplicationMenuSupported || actual.ApplicationMenuPatched) &&
                    (!expected.TrayLabelsSupported || actual.TrayLabelsPatched) &&
                    (!expected.TrayExitSupported || actual.TrayExitPatched) &&
                    (!expected.TraceLabelsSupported || actual.TraceLabelsPatched) &&
                    actual.Plumbing != null &&
                    string.Equals(actual.Plumbing.State, "Patched", StringComparison.Ordinal)
                : (!expected.ApplicationMenuPatched || actual.ApplicationMenuOfficial) &&
                    (!expected.TrayLabelsPatched || actual.TrayLabelsOfficial) &&
                    (!expected.TrayExitPatched || actual.TrayExitOfficial) &&
                    (!expected.TraceLabelsPatched || actual.TraceLabelsOfficial) &&
                    !actual.HasManagedMarker &&
                    (expected.Plumbing == null ||
                        !string.Equals(expected.Plumbing.State, "Patched", StringComparison.Ordinal) ||
                        actual.Plumbing != null &&
                        string.Equals(actual.Plumbing.State, "Official", StringComparison.Ordinal));
            if (!componentsMatch)
            {
                throw new InvalidDataException(
                    "原生菜单主进程脚本组件变换后状态验证失败：" +
                    "application=" + actual.ApplicationMenuOfficial + "/" + actual.ApplicationMenuPatched +
                    "，trayLabels=" + actual.TrayLabelsOfficial + "/" + actual.TrayLabelsPatched +
                    "，trayExit=" + actual.TrayExitOfficial + "/" + actual.TrayExitPatched +
                    "，trace=" + actual.TraceLabelsOfficial + "/" + actual.TraceLabelsPatched + "。");
            }
        }

        private sealed class NativeMenuScriptInspection
        {
            internal AsarArchiveEntry Entry;
            internal byte[] Data;
            internal string Text;
            internal string State;
            internal bool ApplicationMenuSupported;
            internal bool ApplicationMenuOfficial;
            internal bool ApplicationMenuPatched;
            internal bool TrayLabelsSupported;
            internal bool TrayLabelsOfficial;
            internal bool TrayLabelsPatched;
            internal bool TrayExitSupported;
            internal bool TrayExitOfficial;
            internal bool TrayExitPatched;
            internal bool TraceLabelsSupported;
            internal bool TraceLabelsOfficial;
            internal bool TraceLabelsPatched;
            internal string ElectronVariable;
            internal string LocaleSettingExpression;
            internal NativeMenuPlumbingInspection Plumbing;
            internal bool HasManagedMarker;
            internal int SupportedComponentCount;
            internal bool AllSupportedComponentsPatched;
            internal bool AllSupportedComponentsOfficial;
            internal string Error;

            internal static NativeMenuScriptInspection Unsupported(string error)
            {
                return new NativeMenuScriptInspection { State = "Unsupported", Error = error };
            }

            internal static NativeMenuScriptInspection Mixed(
                AsarArchiveEntry entry,
                byte[] data,
                string error)
            {
                return new NativeMenuScriptInspection
                {
                    Entry = entry,
                    Data = data,
                    Text = data == null ? null : Encoding.UTF8.GetString(data),
                    State = "Mixed",
                    HasManagedMarker = true,
                    Error = error
                };
            }
        }

        private sealed class JavaScriptMethodSpan
        {
            internal int BodyStart;
            internal int BodyLength;
            internal string Body;
        }

        private sealed class NativeMenuCommitSpan
        {
            internal int Start;
            internal string ElectronVariable;
            internal string MenuVariable;
        }

        private sealed class NativeTrayQuitSpan
        {
            internal int ExpressionStart;
            internal int ExpressionLength;
            internal string ElectronVariable;
            internal string OfficialExpression;
            internal bool Patched;
        }

        private sealed class NativeTraceResolverSpan
        {
            internal int ExpressionStart;
            internal int ExpressionLength;
            internal string OfficialExpression;
            internal bool Patched;
        }

        private sealed class NativeMenuFactoryCandidate
        {
            internal Esprima.Ast.Node Function;
            internal Esprima.Ast.ObjectPattern Pattern;
            internal Esprima.Ast.BlockStatement Body;
            internal string Name;
            internal int DeclarationInsertionIndex;
            internal int BodyInsertionIndex;
        }

        private sealed class NativeMenuPlumbingInspection
        {
            internal string State;
            internal JavaScriptInsertion[] SemanticEdits;
            internal string Error;

            internal static NativeMenuPlumbingInspection Unsupported(string error)
            {
                return new NativeMenuPlumbingInspection
                {
                    State = "Unsupported",
                    Error = error
                };
            }

            internal static NativeMenuPlumbingInspection Mixed(string error)
            {
                return new NativeMenuPlumbingInspection
                {
                    State = "Mixed",
                    Error = error
                };
            }
        }

        private sealed class JavaScriptInsertion
        {
            internal JavaScriptInsertion(int index, string text)
            {
                Index = index;
                Text = text;
            }

            internal int Index;
            internal string Text;
        }
    }
}
