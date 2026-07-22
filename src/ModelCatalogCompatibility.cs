using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Esprima.Ast;

namespace CodexPortableManager
{
    internal static class ModelCatalogCompatibility
    {
        internal const string RecipeId = "model-catalog.available-models";
        private const string AvailableModelsContext = "available_models";
        internal const string PatchedMarker = "/*codex-portable-manager:model-catalog-semantic*/";

        internal static IEnumerable<string> ManagedMarkers
        {
            get { return new[] { PatchedMarker }; }
        }

        public static void Configure(string executablePath, bool enabled, Action<string> log)
        {
            if (!new CompatibilityPlan(log).ApplyModel(executablePath, enabled))
            {
                throw new InvalidDataException("模型 catalog 兼容设置未能完成。");
            }
        }

        public static bool TryConfigure(string executablePath, bool enabled, Action<string> log)
        {
            return new CompatibilityPlan(log).ApplyModel(executablePath, enabled);
        }

        public static bool IsEnabled(string executablePath)
        {
            using (AsarSession session = AsarSession.Open(AsarSession.GetAsarPath(executablePath)))
            {
                CompatibilityPatchState state = AnalyzeSemantic(session).State;
                if (state == CompatibilityPatchState.Mixed)
                {
                    throw new InvalidDataException("模型 catalog 补丁处于混合状态。");
                }
                if (state == CompatibilityPatchState.Unsupported)
                {
                    throw new InvalidDataException("当前 Codex 版本不包含受支持的模型过滤指纹。");
                }
                return state == CompatibilityPatchState.Patched;
            }
        }

        internal static CompatibilityFeatureChange Plan(
            AsarSession session,
            bool enabled,
            Action<string> log)
        {
            return PlanSemantic(session, enabled, AnalyzeSemantic(session), log);
        }

        internal static CompatibilityFeatureChange Inspect(AsarSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            SemanticModelAnalysis analysis = AnalyzeSemantic(session);
            if (analysis.State == CompatibilityPatchState.Official ||
                analysis.State == CompatibilityPatchState.Patched)
            {
                return new CompatibilityFeatureChange
                {
                    Succeeded = true,
                    Changed = false,
                    Before = analysis.State.ToString(),
                    Desired = analysis.State.ToString(),
                    After = analysis.State.ToString(),
                    Status = CompatibilityFeatureStatus.AlreadySatisfied,
                    RecipeId = RecipeId
                };
            }
            if (!analysis.HasManagedMarker)
            {
                return new CompatibilityFeatureChange
                {
                    Succeeded = true,
                    Changed = false,
                    Before = "UnmanagedOrOfficial",
                    Desired = "UnmanagedOrOfficial",
                    After = "UnmanagedOrOfficial",
                    Status = CompatibilityFeatureStatus.AlreadySatisfied,
                    RecipeId = RecipeId
                };
            }

            return new CompatibilityFeatureChange
            {
                Succeeded = false,
                Changed = false,
                Before = analysis.State.ToString(),
                Desired = analysis.State.ToString(),
                After = analysis.State.ToString(),
                Status = analysis.State == CompatibilityPatchState.Unsupported
                    ? CompatibilityFeatureStatus.Unsupported
                    : CompatibilityFeatureStatus.Failed,
                Error = analysis.Error ?? "模型目录补丁处于混合或未知受管状态。",
                RecipeId = RecipeId
            };
        }

        internal static void LogUnavailable(Action<string> log, bool enabled, Exception exception)
        {
            SafeLog(
                log,
                (enabled
                    ? "警告：当前 Codex 版本的模型过滤语义与本工具不兼容，已保留官方 app.asar，不阻断安装或更新。原因："
                    : "警告：模型目录语义补丁无法安全恢复，已保留当前完整 app.asar。原因：") +
                (exception == null ? "未知错误。" : exception.Message));
        }

        private static void ValidateAvailableModelsContext(
            AsarSession session,
            AsarArchiveEntry candidateEntry)
        {
            int peerEntries = 0;
            int peerOccurrences = 0;
            int candidateOccurrences = 0;
            Action<AsarArchiveEntry, byte[]> collect = (entry, data) =>
            {
                int count = AsarSession.CountAscii(data, AvailableModelsContext);
                if (object.ReferenceEquals(entry, candidateEntry))
                {
                    candidateOccurrences += count;
                }
                else if (count > 0)
                {
                    peerEntries++;
                    peerOccurrences += count;
                }
            };
            session.ScanEntries(IsModelAssetEntry, collect);
            bool inlineContext = candidateOccurrences >= 1 &&
                candidateOccurrences <= 3 &&
                peerOccurrences == 0;
            bool separatedContext = candidateOccurrences == 0 &&
                peerEntries == 1 &&
                peerOccurrences >= 1 &&
                peerOccurrences <= 3;
            if (!inlineContext && !separatedContext &&
                candidateOccurrences == 0 && peerOccurrences == 0)
            {
                // 入口文件可能已经被 bundler 改名；上下文仍以稳定的
                // available_models 键识别，但不把所有静态资源直接当候选。
                session.ScanEntries(
                    IsWebviewJavaScriptEntry,
                    (entry, data) =>
                    {
                        if (object.ReferenceEquals(entry, candidateEntry)) return;
                        int count = AsarSession.CountAscii(data, AvailableModelsContext);
                        if (count > 0)
                        {
                            peerEntries++;
                            peerOccurrences += count;
                        }
                    });
                separatedContext = candidateOccurrences == 0 &&
                    peerEntries == 1 &&
                    peerOccurrences >= 1 &&
                    peerOccurrences <= 3;
            }
            if (!inlineContext && !separatedContext)
            {
                throw new InvalidDataException(
                    "模型选择器缺少唯一的 available_models 上下文锚点，拒绝修改可能无关的 JavaScript。");
            }
        }

        private static SemanticModelAnalysis AnalyzeSemantic(AsarSession session)
        {
            List<SemanticModelCandidate> candidates = new List<SemanticModelCandidate>();
            int markerCount = 0;
            string parseError = null;
            session.ScanEntries(
                IsModelAssetEntry,
                (entry, data) =>
                {
                    string text = Encoding.UTF8.GetString(data);
                    markerCount += Count(text, PatchedMarker);
                    try
                    {
                        JavaScriptSemanticDocument document = JavaScriptSemanticDocument.Parse(text);
                        candidates.AddRange(FindSemanticCandidates(entry, data, document));
                    }
                    catch (Exception exception)
                    {
                        parseError = exception.Message;
                    }
                });

            if (candidates.Count == 0)
            {
                // 先用文件名快路径降低解析量；只有入口被重命名或拆到未知
                // chunk 时，才按稳定的 AST 词法特征扩大到 webview 资源。
                session.ScanEntries(
                    IsWebviewJavaScriptEntry,
                    (entry, data) =>
                    {
                        if (IsModelAssetEntry(entry)) return;
                        string text = Encoding.UTF8.GetString(data);
                        markerCount += Count(text, PatchedMarker);
                        if (!text.Contains("hidden") && !text.Contains(PatchedMarker))
                        {
                            return;
                        }
                        try
                        {
                            JavaScriptSemanticDocument document = JavaScriptSemanticDocument.Parse(text);
                            candidates.AddRange(FindSemanticCandidates(entry, data, document));
                        }
                        catch (Exception exception)
                        {
                            parseError = exception.Message;
                        }
                    });
            }

            if (markerCount > 1)
            {
                return SemanticModelAnalysis.Create(
                    CompatibilityPatchState.Mixed,
                    null,
                    true,
                    "模型目录语义补丁标记出现多次。");
            }
            if (candidates.Count != 1)
            {
                return SemanticModelAnalysis.Create(
                    markerCount > 0 ? CompatibilityPatchState.Mixed : CompatibilityPatchState.Unsupported,
                    candidates.FirstOrDefault(),
                    markerCount > 0,
                    parseError ?? "没有找到唯一的模型目录过滤语义入口。");
            }
            SemanticModelCandidate candidate = candidates[0];
            CompatibilityPatchState state = candidate.Patched
                ? CompatibilityPatchState.Patched
                : CompatibilityPatchState.Official;
            if ((markerCount == 1) != candidate.Patched)
            {
                return SemanticModelAnalysis.Create(
                    CompatibilityPatchState.Mixed,
                    candidate,
                    markerCount > 0,
                    "模型目录语义补丁正文与标记不一致。");
            }
            try { ValidateAvailableModelsContext(session, candidate.Entry); }
            catch (Exception exception)
            {
                return SemanticModelAnalysis.Create(
                    CompatibilityPatchState.Unsupported,
                    candidate,
                    markerCount > 0,
                    exception.Message);
            }
            session.RetainEntryData(candidate.Entry, candidate.Data);
            return SemanticModelAnalysis.Create(state, candidate, markerCount > 0, null);
        }

        private static bool IsModelAssetEntry(AsarArchiveEntry entry)
        {
            if (!IsWebviewJavaScriptEntry(entry)) return false;
            string fileName = Path.GetFileName(entry.Path);
            return fileName.IndexOf("model", StringComparison.OrdinalIgnoreCase) >= 0 ||
                fileName.IndexOf("catalog", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsWebviewJavaScriptEntry(AsarArchiveEntry entry)
        {
            return entry != null &&
                entry.Path.StartsWith("webview/assets/", StringComparison.Ordinal) &&
                entry.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<SemanticModelCandidate> FindSemanticCandidates(
            AsarArchiveEntry entry,
            byte[] data,
            JavaScriptSemanticDocument document)
        {
            List<SemanticModelCandidate> result = new List<SemanticModelCandidate>();
            foreach (JavaScriptNodeRecord record in document.Records)
            {
                ConditionalExpression conditional = record.Node as ConditionalExpression;
                bool preservedInsideManagedWrapper = conditional != null &&
                    record.FindAncestor(node =>
                        node is LogicalExpression &&
                        node.Range.End + PatchedMarker.Length <= document.Source.Length &&
                        string.Equals(
                            document.Source.Substring(node.Range.End, PatchedMarker.Length),
                            PatchedMarker,
                            StringComparison.Ordinal)) != null;
                if (conditional != null && !preservedInsideManagedWrapper &&
                    IsModelFilterConditional(conditional))
                {
                    result.Add(new SemanticModelCandidate
                    {
                        Entry = entry,
                        Data = data,
                        Start = conditional.Range.Start,
                        Length = conditional.Range.End - conditional.Range.Start,
                        OfficialExpression = document.Slice(conditional),
                        HiddenExpression = document.Slice(conditional.Alternate),
                        Patched = false
                    });
                    continue;
                }

                LogicalExpression logical = record.Node as LogicalExpression;
                if (logical == null || logical.Operator != BinaryOperator.LogicalOr ||
                    logical.Range.End + PatchedMarker.Length > document.Source.Length ||
                    !string.Equals(
                        document.Source.Substring(logical.Range.End, PatchedMarker.Length),
                        PatchedMarker,
                        StringComparison.Ordinal)) continue;
                LogicalExpression preserved = logical.Right as LogicalExpression;
                Literal disabled = preserved == null ? null : preserved.Left as Literal;
                ConditionalExpression original = preserved == null
                    ? null
                    : preserved.Right as ConditionalExpression;
                if (preserved == null || preserved.Operator != BinaryOperator.LogicalAnd ||
                    disabled == null || disabled.BooleanValue != false ||
                    original == null || !IsModelFilterConditional(original) ||
                    !IsHiddenExpression(logical.Left)) continue;
                result.Add(new SemanticModelCandidate
                {
                    Entry = entry,
                    Data = data,
                    Start = logical.Range.Start,
                    Length = logical.Range.End - logical.Range.Start + PatchedMarker.Length,
                    OfficialExpression = document.Slice(original),
                    HiddenExpression = document.Slice(logical.Left),
                    Patched = true
                });
            }
            return result;
        }

        private static bool IsModelFilterConditional(ConditionalExpression conditional)
        {
            CallExpression available = conditional == null ? null : conditional.Consequent as CallExpression;
            UnaryExpression hidden = conditional == null ? null : conditional.Alternate as UnaryExpression;
            if (available == null || available.Arguments.Count != 1 ||
                !JavaScriptSemanticDocument.MemberChainEndsWith(available.Callee, "has") ||
                hidden == null || hidden.Operator != UnaryOperator.LogicalNot ||
                !JavaScriptSemanticDocument.MemberChainEndsWith(hidden.Argument, "hidden"))
            {
                return false;
            }
            Expression model = available.Arguments[0] as Expression;
            if (model == null || !JavaScriptSemanticDocument.MemberChainEndsWith(model, "model"))
            {
                return false;
            }
            string[] modelChain = JavaScriptSemanticDocument.GetMemberChain(model);
            string[] hiddenChain = JavaScriptSemanticDocument.GetMemberChain(hidden.Argument);
            return modelChain.Length >= 2 && hiddenChain.Length >= 2 &&
                string.Equals(modelChain[0], hiddenChain[0], StringComparison.Ordinal);
        }

        private static bool IsHiddenExpression(Expression expression)
        {
            UnaryExpression hidden = expression as UnaryExpression;
            return hidden != null && hidden.Operator == UnaryOperator.LogicalNot &&
                JavaScriptSemanticDocument.MemberChainEndsWith(hidden.Argument, "hidden");
        }

        private static CompatibilityFeatureChange PlanSemantic(
            AsarSession session,
            bool enabled,
            SemanticModelAnalysis analysis,
            Action<string> log)
        {
            CompatibilityPatchState desired = enabled
                ? CompatibilityPatchState.Patched
                : CompatibilityPatchState.Official;
            if (analysis.State == desired)
            {
                return new CompatibilityFeatureChange
                {
                    Succeeded = true,
                    Changed = false,
                    Before = analysis.State.ToString(),
                    Desired = desired.ToString(),
                    After = desired.ToString(),
                    Status = CompatibilityFeatureStatus.AlreadySatisfied,
                    RecipeId = RecipeId
                };
            }
            CompatibilityPatchState expected = enabled
                ? CompatibilityPatchState.Official
                : CompatibilityPatchState.Patched;
            if (analysis.State != expected || analysis.Candidate == null)
            {
                SafeLog(log, "警告：模型目录语义定位失败，已保留完整 app.asar。原因：" + analysis.Error);
                return new CompatibilityFeatureChange
                {
                    Succeeded = false,
                    Changed = false,
                    Before = analysis.State.ToString(),
                    Desired = desired.ToString(),
                    After = analysis.State.ToString(),
                    Status = analysis.State == CompatibilityPatchState.Unsupported
                        ? CompatibilityFeatureStatus.Unsupported
                        : CompatibilityFeatureStatus.Failed,
                    Error = analysis.Error,
                    RecipeId = RecipeId
                };
            }

            SemanticModelCandidate candidate = analysis.Candidate;
            string text = Encoding.UTF8.GetString(candidate.Data);
            string replacement = enabled
                ? candidate.HiddenExpression + "||(false&&(" + candidate.OfficialExpression + "))" +
                    PatchedMarker
                : candidate.OfficialExpression;
            string transformedText = text.Substring(0, candidate.Start) + replacement +
                text.Substring(candidate.Start + candidate.Length);
            session.StageEntry(candidate.Entry, Encoding.UTF8.GetBytes(transformedText));
            return new CompatibilityFeatureChange
            {
                Succeeded = true,
                Changed = true,
                Before = analysis.State.ToString(),
                Desired = desired.ToString(),
                After = desired.ToString(),
                Status = CompatibilityFeatureStatus.Applied,
                RecipeId = RecipeId,
                CompletionMessage = enabled
                    ? "已按 JavaScript 语义解除 available_models 二次过滤。"
                    : "已恢复模型目录的官方语义过滤表达式。",
                Verify = verified =>
                {
                    SemanticModelAnalysis checkedAnalysis = AnalyzeSemantic(verified);
                    if (checkedAnalysis.State != desired || checkedAnalysis.Error != null)
                    {
                        throw new InvalidDataException("app.asar 模型语义修复完成后验证失败。");
                    }
                }
            };
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

        private sealed class SemanticModelAnalysis
        {
            internal CompatibilityPatchState State;
            internal SemanticModelCandidate Candidate;
            internal bool HasManagedMarker;
            internal string Error;

            internal static SemanticModelAnalysis Create(
                CompatibilityPatchState state,
                SemanticModelCandidate candidate,
                bool marker,
                string error)
            {
                return new SemanticModelAnalysis
                {
                    State = state,
                    Candidate = candidate,
                    HasManagedMarker = marker,
                    Error = error
                };
            }
        }

        private sealed class SemanticModelCandidate
        {
            internal AsarArchiveEntry Entry;
            internal byte[] Data;
            internal int Start;
            internal int Length;
            internal string OfficialExpression;
            internal string HiddenExpression;
            internal bool Patched;
        }

        private static void SafeLog(Action<string> log, string message)
        {
            if (log == null || string.IsNullOrWhiteSpace(message)) return;
            try { log(message); }
            catch { }
        }
    }
}
