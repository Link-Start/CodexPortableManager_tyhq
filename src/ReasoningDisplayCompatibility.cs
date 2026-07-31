using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Esprima.Ast;

namespace CodexPortableManager
{
    internal static class ReasoningDisplayCompatibility
    {
        internal const string FeatureId = "ReasoningDisplay";
        internal const string RecipeId = "reasoning-display.raw-content-expanded.v2";
        internal const string ContentMarker =
            "/*codex-portable-manager:reasoning-display-content*/";
        internal const string LayoutMarker =
            "/*codex-portable-manager:reasoning-display-layout*/";
        internal const string ExpansionMarker =
            "/*codex-portable-manager:reasoning-display-expanded*/";

        private const string ReasoningMarkdownKey = "reasoning-markdown";
        private const string FixedReasoningHeight = "8.75rem";
        private const string RawContentSentinel =
            "<!--codex-portable-manager:reasoning-display-raw-->";
        private const string LayoutInsertion =
            LayoutMarker + "disableMaxHeight:!0,";

        internal static IEnumerable<string> ManagedMarkers
        {
            get { return new[] { ContentMarker, LayoutMarker, ExpansionMarker }; }
        }

        public static bool TryConfigure(
            string executablePath,
            bool enabled,
            Action<string> log)
        {
            return new CompatibilityPlan(log)
                .ApplyReasoningDisplay(executablePath, enabled);
        }

        public static bool IsEnabled(string executablePath)
        {
            using (AsarSession session = AsarSession.Open(
                AsarSession.GetAsarPath(executablePath)))
            {
                ReasoningDisplayAnalysis analysis = Analyze(session);
                if (analysis.State == CompatibilityPatchState.Mixed)
                {
                    throw new InvalidDataException("模型推理显示补丁处于混合状态。");
                }
                if (analysis.State == CompatibilityPatchState.Unsupported)
                {
                    throw new InvalidDataException(
                        "当前 Codex 版本不包含受支持的模型推理显示入口。");
                }
                return analysis.State == CompatibilityPatchState.Patched;
            }
        }

        internal static CompatibilityFeatureChange Inspect(AsarSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            ReasoningDisplayAnalysis analysis = Analyze(session);
            if (analysis.State == CompatibilityPatchState.Official ||
                analysis.State == CompatibilityPatchState.Patched)
            {
                string observedState = analysis.NeedsUpgrade
                    ? "PatchedRefreshRequired"
                    : analysis.State.ToString();
                return new CompatibilityFeatureChange
                {
                    Succeeded = true,
                    Changed = false,
                    Before = observedState,
                    Desired = observedState,
                    After = observedState,
                    Status = CompatibilityFeatureStatus.AlreadySatisfied,
                    RecipeId = RecipeId
                };
            }

            string state = analysis.State == CompatibilityPatchState.Unsupported &&
                !analysis.HasManagedMarker
                ? CompatibilityPatchState.Official.ToString()
                : analysis.State.ToString();
            return new CompatibilityFeatureChange
            {
                Succeeded = false,
                Changed = false,
                Before = state,
                Desired = state,
                After = state,
                Status = analysis.State == CompatibilityPatchState.Unsupported
                    ? CompatibilityFeatureStatus.Unsupported
                    : CompatibilityFeatureStatus.Failed,
                Error = analysis.Error ??
                    "模型推理显示补丁处于混合或未知受管状态。",
                RecipeId = RecipeId
            };
        }

        internal static CompatibilityFeatureChange Plan(
            AsarSession session,
            bool enabled,
            Action<string> log)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            ReasoningDisplayAnalysis analysis = Analyze(session);
            CompatibilityPatchState desired = enabled
                ? CompatibilityPatchState.Patched
                : CompatibilityPatchState.Official;
            if (analysis.State == desired && !(enabled && analysis.NeedsUpgrade))
            {
                return new CompatibilityFeatureChange
                {
                    Succeeded = true,
                    Changed = false,
                    Before = desired.ToString(),
                    Desired = desired.ToString(),
                    After = desired.ToString(),
                    Status = CompatibilityFeatureStatus.AlreadySatisfied,
                    RecipeId = RecipeId
                };
            }

            bool canUpgrade = enabled && analysis.State == CompatibilityPatchState.Patched &&
                analysis.NeedsUpgrade;
            CompatibilityPatchState expected = enabled
                ? CompatibilityPatchState.Official
                : CompatibilityPatchState.Patched;
            if ((!canUpgrade && analysis.State != expected) ||
                analysis.ContentCandidate == null ||
                analysis.LayoutCandidate == null)
            {
                SafeLog(log,
                    "警告：模型推理显示语义定位失败，已保留完整 app.asar。原因：" +
                    analysis.Error);
                string safeState = analysis.State == CompatibilityPatchState.Unsupported &&
                    !analysis.HasManagedMarker
                    ? CompatibilityPatchState.Official.ToString()
                    : analysis.State.ToString();
                return new CompatibilityFeatureChange
                {
                    Succeeded = false,
                    Changed = false,
                    Before = safeState,
                    Desired = desired.ToString(),
                    After = safeState,
                    Status = analysis.State == CompatibilityPatchState.Unsupported
                        ? CompatibilityFeatureStatus.Unsupported
                        : CompatibilityFeatureStatus.Failed,
                    Error = analysis.Error,
                    RecipeId = RecipeId
                };
            }

            session.RunStagingTransaction(() => StageTransformation(
                session,
                analysis,
                enabled));
            return new CompatibilityFeatureChange
            {
                Succeeded = true,
                Changed = true,
                Before = analysis.NeedsUpgrade
                    ? "PatchedRefreshRequired"
                    : analysis.State.ToString(),
                Desired = desired.ToString(),
                After = desired.ToString(),
                Status = CompatibilityFeatureStatus.Applied,
                RecipeId = RecipeId,
                CompletionMessage = enabled
                    ? "已优先显示并默认展开模型原始推理内容，同时取消推理卡片内部固定高度。"
                    : "已恢复 Codex 官方推理摘要映射、默认折叠和卡片高度设置。",
                Verify = verified =>
                {
                    ReasoningDisplayAnalysis checkedAnalysis = Analyze(verified);
                    if (checkedAnalysis.State != desired || checkedAnalysis.Error != null)
                    {
                        throw new InvalidDataException(
                            "app.asar 模型推理显示变换完成后验证失败：" +
                            checkedAnalysis.Error);
                    }
                }
            };
        }

        internal static void LogUnavailable(
            Action<string> log,
            bool enabled,
            Exception exception)
        {
            SafeLog(
                log,
                (enabled
                    ? "警告：当前 Codex 版本的模型推理显示语义与本工具不兼容，已保留官方 app.asar。原因："
                    : "警告：模型推理显示补丁无法安全恢复，已保留当前完整 app.asar。原因：") +
                (exception == null ? "未知错误。" : exception.Message));
        }

        private static ReasoningDisplayAnalysis Analyze(AsarSession session)
        {
            List<ReasoningContentCandidate> officialContent =
                new List<ReasoningContentCandidate>();
            List<ReasoningContentCandidate> patchedContent =
                new List<ReasoningContentCandidate>();
            List<ReasoningContentCandidate> legacyContent =
                new List<ReasoningContentCandidate>();
            List<ReasoningLayoutCandidate> officialLayout =
                new List<ReasoningLayoutCandidate>();
            List<ReasoningLayoutCandidate> patchedLayout =
                new List<ReasoningLayoutCandidate>();
            List<ReasoningLayoutCandidate> legacyLayout =
                new List<ReasoningLayoutCandidate>();
            List<string> parseErrors = new List<string>();
            IDictionary<string, int> markerCounts = session.CountCurrentPatterns(
                ManagedMarkers);
            int contentMarkers = GetCount(markerCounts, ContentMarker);
            int layoutMarkers = GetCount(markerCounts, LayoutMarker);
            int expansionMarkers = GetCount(markerCounts, ExpansionMarker);

            session.ScanEntries(
                IsReasoningAssetEntry,
                (entry, data) =>
                {
                    string text = Encoding.UTF8.GetString(data);
                    int entryContentMarkers = Count(text, ContentMarker);
                    int entryLayoutMarkers = Count(text, LayoutMarker);
                    int entryExpansionMarkers = Count(text, ExpansionMarker);
                    bool mayContainContent = entryContentMarkers > 0 ||
                        ContainsReasoningCase(text) &&
                        text.IndexOf(".summary", StringComparison.Ordinal) >= 0 &&
                        text.IndexOf(".push", StringComparison.Ordinal) >= 0;
                    bool mayContainLayout = entryLayoutMarkers > 0 ||
                        entryExpansionMarkers > 0 ||
                        text.IndexOf(ReasoningMarkdownKey, StringComparison.Ordinal) >= 0 &&
                        text.IndexOf(FixedReasoningHeight, StringComparison.Ordinal) >= 0;
                    if (!mayContainContent && !mayContainLayout) return;

                    JavaScriptSemanticDocument document;
                    try
                    {
                        document = JavaScriptSemanticDocument.Parse(text);
                    }
                    catch (Exception exception)
                    {
                        parseErrors.Add(entry.Path + "：" + exception.Message);
                        return;
                    }

                    if (mayContainContent)
                    {
                        FindContentCandidates(
                            entry,
                            data,
                            document,
                            officialContent,
                            patchedContent,
                            legacyContent);
                    }
                    if (mayContainLayout)
                    {
                        FindLayoutCandidates(
                            entry,
                            data,
                            document,
                            officialLayout,
                            patchedLayout,
                            legacyLayout);
                    }
                });

            ReasoningDisplayAnalysis result = new ReasoningDisplayAnalysis
            {
                HasManagedMarker = contentMarkers > 0 || layoutMarkers > 0 ||
                    expansionMarkers > 0
            };
            if (parseErrors.Count > 0)
            {
                result.State = result.HasManagedMarker
                    ? CompatibilityPatchState.Mixed
                    : CompatibilityPatchState.Unsupported;
                result.Error = "候选脚本无法完成 JavaScript 语义解析：" +
                    string.Join("；", parseErrors.Take(3).ToArray());
                return result;
            }
            if (contentMarkers > 1 || layoutMarkers > 1 || expansionMarkers > 1)
            {
                result.State = CompatibilityPatchState.Mixed;
                result.Error = "模型推理显示受管标记数量异常：content=" +
                    contentMarkers + "，layout=" + layoutMarkers +
                    "，expanded=" + expansionMarkers + "。";
                return result;
            }

            bool official = contentMarkers == 0 && layoutMarkers == 0 &&
                expansionMarkers == 0 && officialContent.Count == 1 &&
                patchedContent.Count == 0 && legacyContent.Count == 0 &&
                officialLayout.Count == 1 && patchedLayout.Count == 0 &&
                legacyLayout.Count == 0;
            if (official)
            {
                result.State = CompatibilityPatchState.Official;
                result.ContentCandidate = officialContent.Single();
                result.LayoutCandidate = officialLayout.Single();
                return result;
            }

            bool patched = contentMarkers == 1 && layoutMarkers == 1 &&
                expansionMarkers == 1 && officialContent.Count == 0 &&
                patchedContent.Count == 1 && legacyContent.Count == 0 &&
                officialLayout.Count == 0 && patchedLayout.Count == 1 &&
                legacyLayout.Count == 0;
            if (patched)
            {
                result.State = CompatibilityPatchState.Patched;
                result.ContentCandidate = patchedContent.Single();
                result.LayoutCandidate = patchedLayout.Single();
                return result;
            }

            bool legacy = contentMarkers == 1 && layoutMarkers == 1 &&
                expansionMarkers == 0 && officialContent.Count == 0 &&
                patchedContent.Count == 0 && legacyContent.Count == 1 &&
                officialLayout.Count == 0 && patchedLayout.Count == 0 &&
                legacyLayout.Count == 1;
            if (legacy)
            {
                result.State = CompatibilityPatchState.Patched;
                result.ContentCandidate = legacyContent.Single();
                result.LayoutCandidate = legacyLayout.Single();
                result.NeedsUpgrade = true;
                return result;
            }

            bool unsupported = !result.HasManagedMarker &&
                (officialContent.Count != 1 || officialLayout.Count != 1);
            result.State = unsupported
                ? CompatibilityPatchState.Unsupported
                : CompatibilityPatchState.Mixed;
            result.Error = "模型推理显示组件状态不完整：content（官方候选=" +
                officialContent.Count + "，当前候选=" + patchedContent.Count +
                "，旧候选=" + legacyContent.Count + "，标记=" + contentMarkers +
                "）；layout（官方候选=" + officialLayout.Count +
                "，当前候选=" + patchedLayout.Count + "，旧候选=" +
                legacyLayout.Count + "，布局标记=" + layoutMarkers +
                "，展开标记=" + expansionMarkers + "）。";
            return result;
        }

        private static void FindContentCandidates(
            AsarArchiveEntry entry,
            byte[] data,
            JavaScriptSemanticDocument document,
            ICollection<ReasoningContentCandidate> official,
            ICollection<ReasoningContentCandidate> patched,
            ICollection<ReasoningContentCandidate> legacy)
        {
            foreach (SwitchCase branch in document.Records
                .Select(record => record.Node as SwitchCase)
                .Where(value => value != null && string.Equals(
                    JavaScriptSemanticDocument.StringValue(value.Test),
                    "reasoning",
                    StringComparison.Ordinal)))
            {
                IReadOnlyList<JavaScriptNodeRecord> descendants =
                    document.Descendants(branch).ToList();
                foreach (VariableDeclarator declaration in descendants
                    .Select(record => record.Node as VariableDeclarator)
                    .Where(value => value != null))
                {
                    string resultVariable =
                        JavaScriptSemanticDocument.IdentifierName(declaration.Id);
                    if (string.IsNullOrWhiteSpace(resultVariable)) continue;
                    Expression summaryTarget;
                    string originalExpression;
                    int start;
                    int length;
                    ReasoningCandidateKind kind;
                    if (!TryReadReasoningInitializer(
                        document,
                        declaration.Init,
                        out summaryTarget,
                        out originalExpression,
                        out start,
                        out length,
                        out kind))
                    {
                        continue;
                    }
                    if (!HasReasoningPush(descendants, resultVariable)) continue;

                    ReasoningContentCandidate candidate =
                        new ReasoningContentCandidate
                        {
                            Entry = entry,
                            Data = data,
                            Text = document.Source,
                            Start = start,
                            Length = length,
                            ItemExpression = document.Slice(summaryTarget),
                            OfficialExpression = originalExpression,
                            Kind = kind
                        };
                    if (kind == ReasoningCandidateKind.Patched) patched.Add(candidate);
                    else if (kind == ReasoningCandidateKind.Legacy) legacy.Add(candidate);
                    else official.Add(candidate);
                }
            }
        }

        private static bool TryReadReasoningInitializer(
            JavaScriptSemanticDocument document,
            Expression initializer,
            out Expression summaryTarget,
            out string officialExpression,
            out int start,
            out int length,
            out ReasoningCandidateKind kind)
        {
            summaryTarget = null;
            officialExpression = null;
            start = 0;
            length = 0;
            kind = ReasoningCandidateKind.Official;
            CallExpression officialCall = initializer as CallExpression;
            if (TryGetSummaryCall(officialCall, out summaryTarget))
            {
                officialExpression = document.Slice(officialCall);
                start = officialCall.Range.Start;
                length = officialCall.Range.End - officialCall.Range.Start;
                int officialMarkerStart = start - ContentMarker.Length;
                return officialMarkerStart < 0 || !string.Equals(
                    document.Source.Substring(
                        officialMarkerStart,
                        ContentMarker.Length),
                    ContentMarker,
                    StringComparison.Ordinal);
            }

            ConditionalExpression conditional = initializer as ConditionalExpression;
            if (conditional == null ||
                !TryGetSummaryCall(conditional.Alternate as CallExpression, out summaryTarget))
            {
                return false;
            }
            int markerStart = conditional.Range.Start - ContentMarker.Length;
            if (markerStart < 0 || !string.Equals(
                document.Source.Substring(markerStart, ContentMarker.Length),
                ContentMarker,
                StringComparison.Ordinal))
            {
                return false;
            }
            officialExpression = document.Slice(conditional.Alternate);
            string itemExpression = document.Slice(summaryTarget);
            string expected = BuildContentReplacement(itemExpression, officialExpression);
            string legacy = BuildLegacyContentReplacement(
                itemExpression,
                officialExpression);
            int managedLength = conditional.Range.End - markerStart;
            string managed = document.Source.Substring(markerStart, managedLength);
            if (managedLength == expected.Length && string.Equals(
                managed,
                expected,
                StringComparison.Ordinal))
            {
                kind = ReasoningCandidateKind.Patched;
            }
            else if (managedLength == legacy.Length && string.Equals(
                managed,
                legacy,
                StringComparison.Ordinal))
            {
                kind = ReasoningCandidateKind.Legacy;
            }
            else
            {
                return false;
            }
            start = markerStart;
            length = managedLength;
            return true;
        }

        private static bool TryGetSummaryCall(
            CallExpression call,
            out Expression target)
        {
            target = null;
            if (call == null || call.Arguments.Count != 1) return false;
            Expression memberTarget;
            string property;
            if (!JavaScriptSemanticDocument.TryGetMember(
                call.Arguments[0] as Expression,
                out memberTarget,
                out property) ||
                !string.Equals(property, "summary", StringComparison.Ordinal) ||
                !(memberTarget is Identifier))
            {
                return false;
            }
            target = memberTarget;
            return true;
        }

        private static bool HasReasoningPush(
            IEnumerable<JavaScriptNodeRecord> descendants,
            string resultVariable)
        {
            List<JavaScriptNodeRecord> records = descendants.ToList();
            HashSet<string> reasoningObjects = new HashSet<string>(StringComparer.Ordinal);
            foreach (VariableDeclarator declaration in records
                .Select(record => record.Node as VariableDeclarator)
                .Where(value => value != null && value.Init is ObjectExpression))
            {
                if (IsReasoningDisplayObject(
                    declaration.Init as ObjectExpression,
                    resultVariable))
                {
                    string name = JavaScriptSemanticDocument.IdentifierName(declaration.Id);
                    if (!string.IsNullOrWhiteSpace(name)) reasoningObjects.Add(name);
                }
            }

            foreach (CallExpression call in records
                .Select(record => record.Node as CallExpression)
                .Where(value => value != null && value.Arguments.Count == 1 &&
                    JavaScriptSemanticDocument.MemberChainEndsWith(
                        value.Callee,
                        "push")))
            {
                ObjectExpression value = call.Arguments[0] as ObjectExpression;
                if (IsReasoningDisplayObject(value, resultVariable) ||
                    reasoningObjects.Contains(
                        JavaScriptSemanticDocument.IdentifierName(call.Arguments[0]) ??
                        string.Empty))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsReasoningDisplayObject(
            ObjectExpression value,
            string resultVariable)
        {
            Property type = FindDirectProperty(value, "type");
            Property content = FindDirectProperty(value, "content");
            return value != null &&
                type != null &&
                content != null &&
                FindDirectProperty(value, "completed") != null &&
                string.Equals(
                    JavaScriptSemanticDocument.StringValue(type.Value),
                    "reasoning",
                    StringComparison.Ordinal) &&
                string.Equals(
                    JavaScriptSemanticDocument.IdentifierName(content.Value),
                    resultVariable,
                    StringComparison.Ordinal);
        }

        private static void FindLayoutCandidates(
            AsarArchiveEntry entry,
            byte[] data,
            JavaScriptSemanticDocument document,
            ICollection<ReasoningLayoutCandidate> official,
            ICollection<ReasoningLayoutCandidate> patched,
            ICollection<ReasoningLayoutCandidate> legacy)
        {
            foreach (ObjectExpression value in document.Records
                .Select(record => record.Node as ObjectExpression)
                .Where(node => node != null && IsReasoningLayoutObject(document, node)))
            {
                int insertionStart = value.Range.Start + 1;
                bool hasManagedInsertion = insertionStart + LayoutInsertion.Length <=
                    document.Source.Length && string.Equals(
                        document.Source.Substring(
                            insertionStart,
                            LayoutInsertion.Length),
                         LayoutInsertion,
                         StringComparison.Ordinal);
                Property disable = FindDirectProperty(value, "disableMaxHeight");
                int expansionStart;
                int expansionLength;
                string itemExpression;
                string officialExpansionExpression;
                ReasoningCandidateKind expansionKind;
                if (!TryFindExpansionInitializer(
                    document,
                    value,
                    out expansionStart,
                    out expansionLength,
                    out itemExpression,
                    out officialExpansionExpression,
                    out expansionKind))
                {
                    continue;
                }

                ReasoningCandidateKind layoutKind;
                if (disable == null && !hasManagedInsertion)
                {
                    layoutKind = ReasoningCandidateKind.Official;
                }
                else if (disable != null && hasManagedInsertion)
                {
                    layoutKind = ReasoningCandidateKind.Patched;
                }
                else
                {
                    continue;
                }

                ReasoningCandidateKind candidateKind;
                if (layoutKind == ReasoningCandidateKind.Official &&
                    expansionKind == ReasoningCandidateKind.Official)
                {
                    candidateKind = ReasoningCandidateKind.Official;
                }
                else if (layoutKind == ReasoningCandidateKind.Patched &&
                    expansionKind == ReasoningCandidateKind.Patched)
                {
                    candidateKind = ReasoningCandidateKind.Patched;
                }
                else if (layoutKind == ReasoningCandidateKind.Patched &&
                    expansionKind == ReasoningCandidateKind.Official)
                {
                    candidateKind = ReasoningCandidateKind.Legacy;
                }
                else
                {
                    continue;
                }

                ReasoningLayoutCandidate candidate = new ReasoningLayoutCandidate
                {
                    Entry = entry,
                    Data = data,
                    Text = document.Source,
                    Start = insertionStart,
                    Length = layoutKind == ReasoningCandidateKind.Patched
                        ? LayoutInsertion.Length
                        : 0,
                    ExpansionStart = expansionStart,
                    ExpansionLength = expansionLength,
                    ItemExpression = itemExpression,
                    OfficialExpansionExpression = officialExpansionExpression,
                    Kind = candidateKind
                };
                if (candidateKind == ReasoningCandidateKind.Patched) patched.Add(candidate);
                else if (candidateKind == ReasoningCandidateKind.Legacy) legacy.Add(candidate);
                else official.Add(candidate);
            }
        }

        private static bool TryFindExpansionInitializer(
            JavaScriptSemanticDocument document,
            ObjectExpression layout,
            out int start,
            out int length,
            out string itemExpression,
            out string officialExpression,
            out ReasoningCandidateKind kind)
        {
            start = 0;
            length = 0;
            itemExpression = null;
            officialExpression = null;
            kind = ReasoningCandidateKind.Official;
            JavaScriptNodeRecord layoutRecord = document.RecordFor(layout);
            JavaScriptNodeRecord functionRecord = layoutRecord == null
                ? null
                : layoutRecord.FindAncestor(IsFunctionNode);
            if (functionRecord == null) return false;

            string[] itemBindings = document.Descendants(functionRecord.Node)
                .Where(record => ReferenceEquals(
                    NearestFunction(record),
                    functionRecord))
                .Select(record => record.Node as Property)
                .Where(property => property != null &&
                    string.Equals(
                        JavaScriptSemanticDocument.PropertyName(property.Key),
                        "item",
                        StringComparison.Ordinal) &&
                    property.Value is Identifier &&
                    document.RecordFor(property).FindAncestor(node =>
                        node is ObjectPattern) != null)
                .Select(property =>
                    JavaScriptSemanticDocument.IdentifierName(property.Value))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (itemBindings.Length != 1) return false;
            itemExpression = itemBindings[0];

            List<VariableDeclarator> stateDeclarations = document
                .Descendants(functionRecord.Node)
                .Where(record => ReferenceEquals(
                    NearestFunction(record),
                    functionRecord))
                .Select(record => record.Node as VariableDeclarator)
                .Where(declaration => declaration != null &&
                    declaration.Id is ArrayPattern &&
                    IsUseStateCall(document, declaration.Init as CallExpression))
                .ToList();
            if (stateDeclarations.Count != 1) return false;

            CallExpression call = stateDeclarations[0].Init as CallExpression;
            if (call == null || call.Arguments.Count != 1) return false;
            Expression argument = call.Arguments[0] as Expression;
            if (IsFalseExpression(document, argument))
            {
                officialExpression = document.Slice(argument);
                start = argument.Range.Start;
                length = argument.Range.End - argument.Range.Start;
                return true;
            }

            ArrowFunctionExpression arrow = argument as ArrowFunctionExpression;
            LogicalExpression body = arrow == null
                ? null
                : arrow.Body as LogicalExpression;
            if (body == null || body.Operator != BinaryOperator.LogicalOr ||
                !IsFalseExpression(document, body.Right as Expression))
            {
                return false;
            }
            int markerStart = arrow.Range.Start - ExpansionMarker.Length;
            if (markerStart < 0 || !string.Equals(
                document.Source.Substring(markerStart, ExpansionMarker.Length),
                ExpansionMarker,
                StringComparison.Ordinal))
            {
                return false;
            }
            officialExpression = document.Slice(body.Right);
            string expected = BuildExpansionReplacement(
                itemExpression,
                officialExpression);
            int managedLength = arrow.Range.End - markerStart;
            if (managedLength != expected.Length || !string.Equals(
                document.Source.Substring(markerStart, managedLength),
                expected,
                StringComparison.Ordinal))
            {
                return false;
            }
            start = markerStart;
            length = managedLength;
            kind = ReasoningCandidateKind.Patched;
            return true;
        }

        private static JavaScriptNodeRecord NearestFunction(
            JavaScriptNodeRecord record)
        {
            return record == null ? null : record.FindAncestor(IsFunctionNode);
        }

        private static bool IsFunctionNode(Node node)
        {
            return node is FunctionDeclaration ||
                node is FunctionExpression ||
                node is ArrowFunctionExpression;
        }

        private static bool IsUseStateCall(
            JavaScriptSemanticDocument document,
            CallExpression call)
        {
            if (call == null || call.Arguments.Count != 1) return false;
            IEnumerable<Node> calleeNodes = new[] { call.Callee as Node }
                .Concat(document.Descendants(call.Callee)
                    .Select(record => record.Node));
            return calleeNodes
                .Select(node => node as MemberExpression)
                .Where(member => member != null)
                .Any(member => string.Equals(
                    JavaScriptSemanticDocument.PropertyName(member.Property),
                    "useState",
                    StringComparison.Ordinal));
        }

        private static bool IsFalseExpression(
            JavaScriptSemanticDocument document,
            Expression expression)
        {
            Literal literal = expression as Literal;
            if (literal != null && literal.BooleanValue == false) return true;
            UnaryExpression unary = expression as UnaryExpression;
            Literal operand = unary == null ? null : unary.Argument as Literal;
            return unary != null && unary.Operator == UnaryOperator.LogicalNot &&
                operand != null && string.Equals(
                    document.Slice(expression),
                    "!1",
                    StringComparison.Ordinal);
        }

        private static bool IsReasoningLayoutObject(
            JavaScriptSemanticDocument document,
            ObjectExpression value)
        {
            Property items = FindDirectProperty(value, "items");
            Property heights = FindDirectProperty(value, "maxHeightByState");
            Property viewState = FindDirectProperty(value, "viewState");
            if (items == null ||
                heights == null ||
                FindDirectProperty(value, "autoScrollToBottom") == null ||
                !string.Equals(
                    JavaScriptSemanticDocument.StringValue(viewState == null
                        ? null
                        : viewState.Value),
                    "expanded",
                    StringComparison.Ordinal))
            {
                return false;
            }
            ObjectExpression heightObject = heights.Value as ObjectExpression;
            if (!HasStringProperty(heightObject, "preview", FixedReasoningHeight) ||
                !HasStringProperty(heightObject, "expanded", FixedReasoningHeight) ||
                !HasStringProperty(heightObject, "collapsed", "0px"))
            {
                return false;
            }
            return document.Descendants(items.Value).Any(record =>
            {
                Property property = record.Node as Property;
                return property != null &&
                    string.Equals(
                        JavaScriptSemanticDocument.PropertyName(property.Key),
                        "key",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        JavaScriptSemanticDocument.StringValue(property.Value),
                        ReasoningMarkdownKey,
                        StringComparison.Ordinal);
            });
        }

        private static bool HasStringProperty(
            ObjectExpression value,
            string name,
            string expected)
        {
            Property property = FindDirectProperty(value, name);
            return property != null && string.Equals(
                JavaScriptSemanticDocument.StringValue(property.Value),
                expected,
                StringComparison.Ordinal);
        }

        private static Property FindDirectProperty(Node node, string name)
        {
            if (node == null) return null;
            return node.ChildNodes
                .Select(value => value as Property)
                .FirstOrDefault(value => value != null && string.Equals(
                    JavaScriptSemanticDocument.PropertyName(value.Key),
                    name,
                    StringComparison.Ordinal));
        }

        private static void StageTransformation(
            AsarSession session,
            ReasoningDisplayAnalysis analysis,
            bool enabled)
        {
            ReasoningContentCandidate content = analysis.ContentCandidate;
            ReasoningLayoutCandidate layout = analysis.LayoutCandidate;
            List<ReasoningEdit> edits = new List<ReasoningEdit>
            {
                new ReasoningEdit
                {
                    Entry = content.Entry,
                    Data = content.Data,
                    Text = content.Text,
                    Start = content.Start,
                    Length = content.Length,
                    Replacement = enabled
                        ? BuildContentReplacement(
                            content.ItemExpression,
                            content.OfficialExpression)
                        : content.OfficialExpression
                },
                new ReasoningEdit
                {
                    Entry = layout.Entry,
                    Data = layout.Data,
                    Text = layout.Text,
                    Start = layout.Start,
                    Length = layout.Length,
                    Replacement = enabled ? LayoutInsertion : string.Empty
                },
                new ReasoningEdit
                {
                    Entry = layout.Entry,
                    Data = layout.Data,
                    Text = layout.Text,
                    Start = layout.ExpansionStart,
                    Length = layout.ExpansionLength,
                    Replacement = enabled
                        ? BuildExpansionReplacement(
                            layout.ItemExpression,
                            layout.OfficialExpansionExpression)
                        : layout.OfficialExpansionExpression
                }
            };

            foreach (IGrouping<AsarArchiveEntry, ReasoningEdit> group in
                edits.GroupBy(edit => edit.Entry))
            {
                ReasoningEdit first = group.First();
                string transformed = first.Text;
                int previousStart = transformed.Length + 1;
                foreach (ReasoningEdit edit in group.OrderByDescending(value => value.Start))
                {
                    if (!ReferenceEquals(edit.Data, first.Data) ||
                        !string.Equals(edit.Text, first.Text, StringComparison.Ordinal) ||
                        edit.Start < 0 ||
                        edit.Length < 0 ||
                        edit.Start + edit.Length > transformed.Length ||
                        edit.Start + edit.Length > previousStart)
                    {
                        throw new InvalidDataException(
                            "模型推理显示变换包含重叠或失效的源码区间。");
                    }
                    transformed = transformed.Substring(0, edit.Start) +
                        edit.Replacement +
                        transformed.Substring(edit.Start + edit.Length);
                    previousStart = edit.Start;
                }
                session.StageEntry(group.Key, Encoding.UTF8.GetBytes(transformed));
            }
        }

        private static string BuildContentReplacement(
            string itemExpression,
            string officialExpression)
        {
            return ContentMarker +
                itemExpression + ".content&&" +
                itemExpression + ".content.some(e=>e.trim().length>0)?" +
                "`" + RawContentSentinel + "\\n`+" +
                itemExpression + ".content.join(`\\n\\n`):" +
                officialExpression;
        }

        private static string BuildLegacyContentReplacement(
            string itemExpression,
            string officialExpression)
        {
            return ContentMarker +
                itemExpression + ".content&&" +
                itemExpression + ".content.some(e=>e.trim().length>0)?" +
                itemExpression + ".content.join(`\\n\\n`):" +
                officialExpression;
        }

        private static string BuildExpansionReplacement(
            string itemExpression,
            string officialExpression)
        {
            return ExpansionMarker + "()=>" + itemExpression +
                ".content.startsWith(`" + RawContentSentinel + "`)||" +
                officialExpression;
        }

        private static bool IsReasoningAssetEntry(AsarArchiveEntry entry)
        {
            return entry != null &&
                entry.Path.StartsWith("webview/assets/", StringComparison.Ordinal) &&
                entry.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsReasoningCase(string text)
        {
            foreach (char quote in new[] { '`', '\"', '\'' })
            {
                string literal = quote + "reasoning" + quote;
                int offset = 0;
                while ((offset = text.IndexOf(
                    literal,
                    offset,
                    StringComparison.Ordinal)) >= 0)
                {
                    int before = offset - 1;
                    while (before >= 0 && char.IsWhiteSpace(text[before])) before--;
                    int caseStart = before - 3;
                    if (caseStart >= 0 && string.Equals(
                            text.Substring(caseStart, 4),
                            "case",
                            StringComparison.Ordinal) &&
                        (caseStart == 0 ||
                         !char.IsLetterOrDigit(text[caseStart - 1]) &&
                         text[caseStart - 1] != '_' &&
                         text[caseStart - 1] != '$'))
                    {
                        return true;
                    }
                    offset += literal.Length;
                }
            }
            return false;
        }

        private static int Count(string text, string pattern)
        {
            int count = 0;
            int offset = 0;
            while (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(pattern))
            {
                int found = text.IndexOf(pattern, offset, StringComparison.Ordinal);
                if (found < 0) return count;
                count++;
                offset = found + pattern.Length;
            }
            return count;
        }

        private static int GetCount(
            IDictionary<string, int> counts,
            string pattern)
        {
            int value;
            return counts.TryGetValue(pattern, out value) ? value : 0;
        }

        private static void SafeLog(Action<string> log, string message)
        {
            if (log == null) return;
            try { log(message); }
            catch { }
        }

        private sealed class ReasoningDisplayAnalysis
        {
            internal CompatibilityPatchState State;
            internal ReasoningContentCandidate ContentCandidate;
            internal ReasoningLayoutCandidate LayoutCandidate;
            internal bool HasManagedMarker;
            internal bool NeedsUpgrade;
            internal string Error;
        }

        private enum ReasoningCandidateKind
        {
            Official,
            Patched,
            Legacy
        }

        private abstract class ReasoningCandidate
        {
            internal AsarArchiveEntry Entry;
            internal byte[] Data;
            internal string Text;
            internal int Start;
            internal int Length;
            internal ReasoningCandidateKind Kind;
        }

        private sealed class ReasoningContentCandidate : ReasoningCandidate
        {
            internal string ItemExpression;
            internal string OfficialExpression;
        }

        private sealed class ReasoningLayoutCandidate : ReasoningCandidate
        {
            internal int ExpansionStart;
            internal int ExpansionLength;
            internal string ItemExpression;
            internal string OfficialExpansionExpression;
        }

        private sealed class ReasoningEdit
        {
            internal AsarArchiveEntry Entry;
            internal byte[] Data;
            internal string Text;
            internal int Start;
            internal int Length;
            internal string Replacement;
        }
    }
}
