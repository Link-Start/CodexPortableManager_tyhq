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
            if (!analysis.HasManagedMarker &&
                analysis.State == CompatibilityPatchState.Unsupported)
            {
                return new CompatibilityFeatureChange
                {
                    Succeeded = false,
                    Changed = false,
                    Before = CompatibilityPatchState.Official.ToString(),
                    Desired = CompatibilityPatchState.Official.ToString(),
                    After = CompatibilityPatchState.Official.ToString(),
                    Status = CompatibilityFeatureStatus.Unsupported,
                    Error = analysis.Error ?? "当前版本没有可安全修改的模型白名单入口。",
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

        private static SemanticModelAnalysis AnalyzeSemantic(AsarSession session)
        {
            try
            {
                SemanticModelSourceIndex index = BuildSemanticSourceIndex(session);
                if (index.Sources.Count > 0)
                {
                    SemanticModelAnalysis indexed = AnalyzeIndexedSources(session, index);
                    if (indexed != null) return indexed;
                }
            }
            catch
            {
                // 索引只负责加速；任何不确定性都回到原有全量语义扫描。
            }

            return AnalyzeSemanticExhaustive(session);
        }

        private static SemanticModelSourceIndex BuildSemanticSourceIndex(AsarSession session)
        {
            SemanticModelSourceIndex index = new SemanticModelSourceIndex();
            session.ScanEntries(
                IsWebviewJavaScriptEntry,
                (entry, data) =>
                {
                    int markerOccurrences = AsarSession.CountAscii(data, PatchedMarker);
                    index.MarkerCount += markerOccurrences;
                    index.AvailableModelsContextOccurrences +=
                        AsarSession.CountAscii(data, AvailableModelsContext);
                    if (markerOccurrences == 0 &&
                        !(ContainsEncodedJavaScriptName(data, "availableModels") &&
                          ContainsEncodedJavaScriptName(data, "hidden") &&
                          ContainsEncodedJavaScriptName(data, "model") &&
                          ContainsEncodedJavaScriptName(data, "has")))
                    {
                        return;
                    }

                    index.Sources.Add(new SemanticModelSource(entry, data));
                });
            return index;
        }

        private static SemanticModelAnalysis AnalyzeIndexedSources(
            AsarSession session,
            SemanticModelSourceIndex index)
        {
            List<SemanticModelCandidate> candidates = new List<SemanticModelCandidate>();
            string parseError = null;
            foreach (SemanticModelSource source in index.Sources)
            {
                string text = Encoding.UTF8.GetString(source.Data);
                try
                {
                    JavaScriptSemanticDocument document = JavaScriptSemanticDocument.Parse(text);
                    candidates.AddRange(FindSemanticCandidates(
                        source.Entry,
                        source.Data,
                        document));
                }
                catch (Exception exception)
                {
                    parseError = exception.Message;
                }
            }

            if (parseError != null || candidates.Count == 0)
            {
                return null;
            }
            return CompleteSemanticAnalysis(
                session,
                candidates,
                index.MarkerCount,
                index.AvailableModelsContextOccurrences,
                parseError);
        }

        private static SemanticModelAnalysis AnalyzeSemanticExhaustive(AsarSession session)
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

            int availableModelsContextOccurrences = 0;
            if (candidates.Count == 1)
            {
                session.ScanEntries(
                    IsWebviewJavaScriptEntry,
                    (entry, data) => availableModelsContextOccurrences +=
                        AsarSession.CountAscii(data, AvailableModelsContext));
            }

            return CompleteSemanticAnalysis(
                session,
                candidates,
                markerCount,
                availableModelsContextOccurrences,
                parseError);
        }

        private static SemanticModelAnalysis AnalyzeVerifiedEntry(
            AsarSession session,
            string entryPath)
        {
            AsarArchiveEntry entry = session.Entries.SingleOrDefault(value =>
                string.Equals(value.Path, entryPath, StringComparison.Ordinal));
            if (entry == null)
            {
                return SemanticModelAnalysis.Create(
                    CompatibilityPatchState.Unsupported,
                    null,
                    false,
                    "模型目录语义入口在临时 ASAR 中缺失。");
            }

            byte[] data = session.GetEntryData(entry);
            List<SemanticModelCandidate> candidates = new List<SemanticModelCandidate>();
            string parseError = null;
            try
            {
                string text = Encoding.UTF8.GetString(data);
                JavaScriptSemanticDocument document = JavaScriptSemanticDocument.Parse(text);
                candidates.AddRange(FindSemanticCandidates(entry, data, document));
            }
            catch (Exception exception)
            {
                parseError = exception.Message;
            }

            IDictionary<string, int> counts = session.CountCurrentPatterns(
                new[] { PatchedMarker, AvailableModelsContext });
            return CompleteSemanticAnalysis(
                session,
                candidates,
                counts[PatchedMarker],
                counts[AvailableModelsContext],
                parseError);
        }

        private static SemanticModelAnalysis CompleteSemanticAnalysis(
            AsarSession session,
            List<SemanticModelCandidate> candidates,
            int markerCount,
            int availableModelsContextOccurrences,
            string parseError)
        {
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
            if (!candidate.AvailableModelsBindingVerified)
            {
                return SemanticModelAnalysis.Create(
                    CompatibilityPatchState.Unsupported,
                    candidate,
                    markerCount > 0,
                    "模型过滤集合没有绑定到同一函数的 availableModels 参数，拒绝修改可能无关的 JavaScript。");
            }
            if (availableModelsContextOccurrences == 0)
            {
                return SemanticModelAnalysis.Create(
                    CompatibilityPatchState.Unsupported,
                    candidate,
                    markerCount > 0,
                    "模型选择器缺少 available_models 配置来源，拒绝修改可能无关的 JavaScript。");
            }
            session.RetainEntryData(candidate.Entry, candidate.Data);
            return SemanticModelAnalysis.Create(state, candidate, markerCount > 0, null);
        }

        private static bool ContainsEncodedJavaScriptName(byte[] data, string name)
        {
            if (data == null || string.IsNullOrEmpty(name)) return false;
            if (AsarSession.ContainsAscii(data, name)) return true;
            if (Array.IndexOf(data, (byte)'\\') < 0) return false;
            byte[] expected = Encoding.ASCII.GetBytes(name);
            int matched = 0;
            int offset = 0;
            while (offset < data.Length)
            {
                int start = offset;
                int decoded;
                if (!TryReadEncodedAscii(data, ref offset, out decoded))
                {
                    offset = start + 1;
                    matched = 0;
                    continue;
                }
                if (decoded == expected[matched])
                {
                    matched++;
                    if (matched == expected.Length) return true;
                }
                else
                {
                    matched = decoded == expected[0] ? 1 : 0;
                }
            }
            return false;
        }

        private static bool TryReadEncodedAscii(
            byte[] data,
            ref int offset,
            out int decoded)
        {
            decoded = -1;
            if (offset >= data.Length) return false;
            byte current = data[offset++];
            if (current != (byte)'\\')
            {
                decoded = current;
                return true;
            }
            if (offset >= data.Length) return false;

            byte escape = data[offset++];
            if (escape == (byte)'u')
            {
                if (offset < data.Length && data[offset] == (byte)'{')
                {
                    offset++;
                    int value = 0;
                    int digits = 0;
                    int digit;
                    while (offset < data.Length && data[offset] != (byte)'}')
                    {
                        if (digits == 6 || !TryGetHexDigit(data[offset++], out digit)) return false;
                        value = (value << 4) | digit;
                        digits++;
                    }
                    if (digits == 0 || offset >= data.Length || data[offset++] != (byte)'}') return false;
                    decoded = value;
                    return true;
                }
                return TryReadFixedHex(data, ref offset, 4, out decoded);
            }
            if (escape == (byte)'x')
            {
                return TryReadFixedHex(data, ref offset, 2, out decoded);
            }
            if (escape >= (byte)'0' && escape <= (byte)'7')
            {
                int value = escape - (byte)'0';
                int maximumDigits = escape <= (byte)'3' ? 3 : 2;
                int digits = 1;
                while (digits < maximumDigits && offset < data.Length &&
                    data[offset] >= (byte)'0' && data[offset] <= (byte)'7')
                {
                    value = (value << 3) | (data[offset++] - (byte)'0');
                    digits++;
                }
                decoded = value;
                return true;
            }

            switch ((char)escape)
            {
                case 'b': decoded = '\b'; return true;
                case 'f': decoded = '\f'; return true;
                case 'n': decoded = '\n'; return true;
                case 'r': decoded = '\r'; return true;
                case 't': decoded = '\t'; return true;
                case 'v': decoded = 11; return true;
                case '\r':
                    if (offset < data.Length && data[offset] == (byte)'\n') offset++;
                    return false;
                case '\n': return false;
            }

            decoded = escape;
            return true;
        }

        private static bool TryReadFixedHex(
            byte[] data,
            ref int offset,
            int count,
            out int value)
        {
            value = 0;
            if (offset + count > data.Length) return false;
            for (int index = 0; index < count; index++)
            {
                int digit;
                if (!TryGetHexDigit(data[offset++], out digit)) return false;
                value = (value << 4) | digit;
            }
            return true;
        }

        private static bool TryGetHexDigit(byte value, out int digit)
        {
            if (value >= (byte)'0' && value <= (byte)'9')
            {
                digit = value - (byte)'0';
                return true;
            }
            if (value >= (byte)'a' && value <= (byte)'f')
            {
                digit = value - (byte)'a' + 10;
                return true;
            }
            if (value >= (byte)'A' && value <= (byte)'F')
            {
                digit = value - (byte)'A' + 10;
                return true;
            }
            digit = 0;
            return false;
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
                        AvailableModelsBindingVerified = HasAvailableModelsBinding(
                            record,
                            conditional),
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
                    AvailableModelsBindingVerified = HasAvailableModelsBinding(
                        record,
                        original),
                    Patched = true
                });
            }
            return result;
        }

        private static bool HasAvailableModelsBinding(
            JavaScriptNodeRecord record,
            ConditionalExpression conditional)
        {
            CallExpression available = conditional == null
                ? null
                : conditional.Consequent as CallExpression;
            string[] member = available == null
                ? new string[0]
                : JavaScriptSemanticDocument.GetMemberChain(available.Callee);
            if (member.Length != 2 ||
                !string.Equals(member[1], "has", StringComparison.Ordinal))
            {
                return false;
            }

            JavaScriptNodeRecord scope = record == null ? null : record.Parent;
            while (scope != null)
            {
                if (IsFunctionNode(scope.Node))
                {
                    Node[] parameters = GetFunctionParameters(scope.Node).ToArray();
                    foreach (Node parameter in parameters)
                    {
                        ObjectPattern pattern = parameter as ObjectPattern;
                        if (pattern == null) continue;
                        Property availableModels = pattern.ChildNodes
                            .Select(value => value as Property)
                            .FirstOrDefault(value => value != null && string.Equals(
                                JavaScriptSemanticDocument.PropertyName(value.Key),
                                "availableModels",
                                StringComparison.Ordinal));
                        if (availableModels == null) continue;
                        string binding = GetPatternBindingName(availableModels.Value);
                        if (string.Equals(binding, member[0], StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                    if (parameters.Any(parameter => PatternBindsName(parameter, member[0])))
                    {
                        return false;
                    }
                }
                scope = scope.Parent;
            }
            return false;
        }

        private static bool IsFunctionNode(Node node)
        {
            return node is FunctionDeclaration ||
                node is FunctionExpression ||
                node is ArrowFunctionExpression;
        }

        private static IEnumerable<Node> GetFunctionParameters(Node function)
        {
            FunctionDeclaration declaration = function as FunctionDeclaration;
            if (declaration != null) return declaration.Params;
            FunctionExpression expression = function as FunctionExpression;
            if (expression != null) return expression.Params;
            ArrowFunctionExpression arrow = function as ArrowFunctionExpression;
            return arrow == null ? Enumerable.Empty<Node>() : arrow.Params;
        }

        private static string GetPatternBindingName(Node value)
        {
            string identifier = JavaScriptSemanticDocument.IdentifierName(value);
            if (identifier != null) return identifier;
            AssignmentPattern assignment = value as AssignmentPattern;
            return assignment == null
                ? null
                : JavaScriptSemanticDocument.IdentifierName(assignment.Left);
        }

        private static bool PatternBindsName(Node pattern, string name)
        {
            if (pattern == null || string.IsNullOrWhiteSpace(name)) return false;
            if (string.Equals(
                JavaScriptSemanticDocument.IdentifierName(pattern),
                name,
                StringComparison.Ordinal))
            {
                return true;
            }
            AssignmentPattern assignment = pattern as AssignmentPattern;
            if (assignment != null) return PatternBindsName(assignment.Left, name);
            RestElement rest = pattern as RestElement;
            if (rest != null) return PatternBindsName(rest.Argument, name);
            ObjectPattern objectPattern = pattern as ObjectPattern;
            if (objectPattern != null)
            {
                return objectPattern.ChildNodes
                    .Select(value => value as Property)
                    .Where(value => value != null)
                    .Any(value => PatternBindsName(value.Value, name));
            }
            return false;
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
                string safeOfficialState = analysis.State == CompatibilityPatchState.Unsupported &&
                    !analysis.HasManagedMarker
                    ? CompatibilityPatchState.Official.ToString()
                    : analysis.State.ToString();
                return new CompatibilityFeatureChange
                {
                    Succeeded = false,
                    Changed = false,
                    Before = safeOfficialState,
                    Desired = desired.ToString(),
                    After = safeOfficialState,
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
                    SemanticModelAnalysis checkedAnalysis = AnalyzeVerifiedEntry(
                        verified,
                        candidate.Entry.Path);
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

        private sealed class SemanticModelSourceIndex
        {
            internal readonly List<SemanticModelSource> Sources =
                new List<SemanticModelSource>();
            internal int MarkerCount;
            internal int AvailableModelsContextOccurrences;
        }

        private sealed class SemanticModelSource
        {
            internal SemanticModelSource(AsarArchiveEntry entry, byte[] data)
            {
                Entry = entry;
                Data = data;
            }

            internal AsarArchiveEntry Entry;
            internal byte[] Data;
        }

        private sealed class SemanticModelCandidate
        {
            internal AsarArchiveEntry Entry;
            internal byte[] Data;
            internal int Start;
            internal int Length;
            internal string OfficialExpression;
            internal string HiddenExpression;
            internal bool AvailableModelsBindingVerified;
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
