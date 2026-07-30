using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexPortableManager
{
internal static partial class RegressionTestRunner
{
    private static void TestModelCatalogPatchRoundTrip()
    {
        string caseRoot = NewCaseRoot("model-catalog-round-trip");
        string executableRoot = Path.Combine(caseRoot, "app");
        string resourcesRoot = Path.Combine(executableRoot, "resources");
        string executablePath = Path.Combine(executableRoot, "Codex.exe");
        string asarPath = Path.Combine(resourcesRoot, "app.asar");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });

        const string officialExpression =
            "catalogEnabled ? availableSet.has(candidate.model) : !candidate.hidden";
        byte[] payload = Encoding.UTF8.GetBytes(
            "const settings={available_models:[]};" +
            "function filter({availableModels:availableSet,catalogEnabled},candidate){return " +
            officialExpression + ";}const after=1;");
        string payloadHash = ComputeSha256Hex(payload);
        string header =
            "{\"files\":{\"webview/assets/catalog-model-panel-test.js\":{\"size\":" + payload.Length.ToString(CultureInfo.InvariantCulture) +
            ",\"offset\":\"0\",\"integrity\":{\"algorithm\":\"SHA256\",\"hash\":\"" + payloadHash +
            "\",\"blockSize\":4096,\"blocks\":[\"" + payloadHash + "\"]}}}}";
        byte[] originalAsar = BuildTestAsar(header, payload);
        File.WriteAllBytes(asarPath, originalAsar);

        List<string> enableLogs = new List<string>();
        bool enabled = ModelCatalogCompatibility.TryConfigure(executablePath, true, enableLogs.Add);
        Assert(enabled, "有效模型 catalog 指纹没有成功启用补丁。日志：" + string.Join(" | ", enableLogs.ToArray()));
        Assert(ModelCatalogCompatibility.IsEnabled(executablePath),
            "模型 catalog 补丁启用后状态检测仍为关闭。");
        string patched = Encoding.UTF8.GetString(File.ReadAllBytes(asarPath));
        Assert(patched.IndexOf(ModelCatalogCompatibility.PatchedMarker, StringComparison.Ordinal) >= 0 &&
            patched.IndexOf(officialExpression, StringComparison.Ordinal) >= 0,
            "启用后没有写入唯一的模型 catalog 补丁标记。");

        bool disabled = ModelCatalogCompatibility.TryConfigure(executablePath, false, delegate { });
        Assert(disabled, "有效模型 catalog 补丁没有成功关闭。");
        Assert(!ModelCatalogCompatibility.IsEnabled(executablePath),
            "模型 catalog 补丁关闭后状态检测仍为开启。");
        Assert(BytesEqual(File.ReadAllBytes(asarPath), originalAsar),
            "模型 catalog 补丁关闭后没有字节级恢复原始 ASAR。");
    }

    private static void TestModelCatalogUnknownFingerprintFallback()
    {
        string caseRoot = NewCaseRoot("model-unknown-fingerprint");
        string executableRoot = Path.Combine(caseRoot, "app");
        string resourcesRoot = Path.Combine(executableRoot, "resources");
        string executablePath = Path.Combine(executableRoot, "Codex.exe");
        string asarPath = Path.Combine(resourcesRoot, "app.asar");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });
        byte[] unknownAsar = Encoding.UTF8.GetBytes("new official format without either managed model expression fingerprint");
        File.WriteAllBytes(asarPath, unknownAsar);

        List<string> enableLogs = new List<string>();
        bool enabled = ModelCatalogCompatibility.TryConfigure(executablePath, true, enableLogs.Add);
        Assert(!enabled, "开启未知指纹时不应报告补丁成功。");
        Assert(BytesEqual(File.ReadAllBytes(asarPath), unknownAsar), "开启未知指纹时 app.asar 被修改。");
        Assert(enableLogs.Exists(value => value.IndexOf("不阻断安装或更新", StringComparison.Ordinal) >= 0),
            "开启未知指纹时缺少明确的安全降级日志。");

        List<string> disableLogs = new List<string>();
        bool disabled = ModelCatalogCompatibility.TryConfigure(executablePath, false, disableLogs.Add);
        Assert(disabled, "关闭且不存在本工具补丁标记时应安全视为无需修改。");
        Assert(BytesEqual(File.ReadAllBytes(asarPath), unknownAsar), "关闭未知指纹时 app.asar 被修改。");
        Assert(disableLogs.Exists(value => value.IndexOf("未检测到本工具", StringComparison.Ordinal) >= 0),
            "关闭未知指纹时缺少保留官方文件的日志。");
    }

    private static void TestModelCatalogRecipeConstraints()
    {
        string caseRoot = NewCaseRoot("model-recipe-constraints");
        string executableRoot = Path.Combine(caseRoot, "app");
        string resourcesRoot = Path.Combine(executableRoot, "resources");
        string executablePath = Path.Combine(executableRoot, "Codex.exe");
        string asarPath = Path.Combine(resourcesRoot, "app.asar");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });

        byte[] unrelatedPayload = Encoding.UTF8.GetBytes(
            "const settings={available_models:[]};" +
            "function filter({availableModels:n},r){return u?n.has(r.model):!r.hidden;}");
        byte[] unrelatedArchive = BuildTestAsar(
            "{\"files\":{" + BuildAsarEntryJson("unrelated.js", unrelatedPayload, 0) + "}}",
            unrelatedPayload);
        File.WriteAllBytes(asarPath, unrelatedArchive);
        Assert(!ModelCatalogCompatibility.TryConfigure(executablePath, true, delegate { }),
            "模型短表达式出现在无关 JS 路径时仍被应用。");
        Assert(BytesEqual(File.ReadAllBytes(asarPath), unrelatedArchive),
            "无关 JS 路径被模型配方修改。");

        byte[] missingContextPayload = Encoding.UTF8.GetBytes(
            "function filter({availableModels:n},r){return u?n.has(r.model):!r.hidden;}");
        byte[] missingContextArchive = BuildTestAsar(
            "{\"files\":{" + BuildAsarEntryJson(
                "webview/assets/model-list-filter-test.js",
                missingContextPayload,
                0) + "}}",
            missingContextPayload);
        File.WriteAllBytes(asarPath, missingContextArchive);
        Assert(!ModelCatalogCompatibility.TryConfigure(executablePath, true, delegate { }),
            "模型选择器缺少 available_models 上下文时仍被应用。");
        Assert(BytesEqual(File.ReadAllBytes(asarPath), missingContextArchive),
            "缺少模型选择器上下文的 ASAR 被修改。");

        byte[] separatedFilterEntry = Encoding.UTF8.GetBytes(
            "const state={availableModels:new Set()};" +
            "function filter({availableModels:n},r){return u?n.has(r.model):!r.hidden;}");
        byte[] separatedQueryEntry = Encoding.UTF8.GetBytes(
            "const source=`available_models`;");
        byte[] separatedPayload = CombineBytes(separatedFilterEntry, separatedQueryEntry);
        byte[] separatedArchive = BuildTestAsar(
            "{\"files\":{" +
            BuildAsarEntryJson(
                "webview/assets/runtime-chunk-test.js",
                separatedFilterEntry,
                0) + "," +
            BuildAsarEntryJson(
                "webview/assets/runtime-settings-test.js",
                separatedQueryEntry,
                separatedFilterEntry.Length) +
            "}}",
            separatedPayload);
        File.WriteAllBytes(asarPath, separatedArchive);
        string boundedDiagnosis = ModelCatalogCompatibility.DiagnoseBoundedAnalysisForTest(
            executablePath);
        Assert(boundedDiagnosis.IndexOf("有界=True", StringComparison.Ordinal) >= 0 &&
            boundedDiagnosis.IndexOf("语义候选=1", StringComparison.Ordinal) >= 0,
            "模型快速索引没有把已验证函数限制到有界 AST：" + boundedDiagnosis);
        Assert(ModelCatalogCompatibility.TryConfigure(executablePath, true, delegate { }),
            "模型入口和上下文 chunk 改名后，唯一语义锚点没有被识别。");
        Assert(ModelCatalogCompatibility.TryConfigure(executablePath, false, delegate { }) &&
            BytesEqual(File.ReadAllBytes(asarPath), separatedArchive),
            "分离上下文模型补丁无法完整往返恢复。");

        byte[] fallbackFilterEntry = Encoding.UTF8.GetBytes(
            "const settings={available_models:[]};" +
            "function filter({availableModels:n},r){return u?n.has(r.model):!r.hidden;}");
        byte[] fallbackUncertainEntry = Encoding.UTF8.GetBytes(
            "const has=model=hidden=;{");
        byte[] fallbackPayload = CombineBytes(fallbackFilterEntry, fallbackUncertainEntry);
        byte[] fallbackArchive = BuildTestAsar(
            "{\"files\":{" +
            BuildAsarEntryJson(
                "webview/assets/model-filter-fallback.js",
                fallbackFilterEntry,
                0) + "," +
            BuildAsarEntryJson(
                "webview/assets/runtime-uncertain.js",
                fallbackUncertainEntry,
                fallbackFilterEntry.Length) +
            "}}",
            fallbackPayload);
        File.WriteAllBytes(asarPath, fallbackArchive);
        Assert(ModelCatalogCompatibility.TryConfigure(executablePath, true, delegate { }) &&
            ModelCatalogCompatibility.TryConfigure(executablePath, false, delegate { }) &&
            BytesEqual(File.ReadAllBytes(asarPath), fallbackArchive),
            "快速索引遇到无法解析的候选脚本时没有回退原有语义扫描。");

        byte[] mergedChunkEntry = Encoding.UTF8.GetBytes(
            "const a={available_models:[]},b={available_models:[]},c={available_models:[]}," +
            "d={available_models:[]};" +
            "function filter({availableModels:n,useHiddenModels:u},r){" +
            "return u?n.has(r.model):!r.hidden;}");
        byte[] mergedChunkArchive = BuildTestAsar(
            "{\"files\":{" + BuildAsarEntryJson(
                "webview/assets/app-initial-merged.js",
                mergedChunkEntry,
                0) + "}}",
            mergedChunkEntry);
        File.WriteAllBytes(asarPath, mergedChunkArchive);
        Assert(ModelCatalogCompatibility.TryConfigure(executablePath, true, delegate { }) &&
            ModelCatalogCompatibility.TryConfigure(executablePath, false, delegate { }) &&
            BytesEqual(File.ReadAllBytes(asarPath), mergedChunkArchive),
            "模型资源合并到含多个 available_models 键的大 chunk 后无法按参数数据流往返。");

        byte[] escapedBindingEntry = Encoding.UTF8.GetBytes(
            "const settings={available_models:[]};" +
            "function filter({avail\\u0061bleModels:n},r){" +
            "return u?n[\"h\\x61s\"](r[\"mo\\u0064el\"]):!r[\"hid\\144en\"];}");
        byte[] escapedBindingArchive = BuildTestAsar(
            "{\"files\":{" + BuildAsarEntryJson(
                "webview/assets/app-initial-escaped.js",
                escapedBindingEntry,
                0) + "}}",
            escapedBindingEntry);
        File.WriteAllBytes(asarPath, escapedBindingArchive);
        Assert(ModelCatalogCompatibility.TryConfigure(executablePath, true, delegate { }) &&
            ModelCatalogCompatibility.TryConfigure(executablePath, false, delegate { }) &&
            BytesEqual(File.ReadAllBytes(asarPath), escapedBindingArchive),
            "转义后的 availableModels/has/model/hidden 属性没有被快速索引或 AST 正确识别。");

        byte[] arrowFunctionEntry = Encoding.UTF8.GetBytes(
            "const settings={available_models:[]};" +
            "function noop({availableModels:n}){return n;}" +
            "const filter=({availableModels:n},r)=>u?n.has(r.model):!r.hidden;");
        byte[] arrowFunctionArchive = BuildTestAsar(
            "{\"files\":{" + BuildAsarEntryJson(
                "webview/assets/app-initial-arrow.js",
                arrowFunctionEntry,
                0) + "}}",
            arrowFunctionEntry);
        File.WriteAllBytes(asarPath, arrowFunctionArchive);
        Assert(ModelCatalogCompatibility.TryConfigure(executablePath, true, delegate { }) &&
            ModelCatalogCompatibility.TryConfigure(executablePath, false, delegate { }) &&
            BytesEqual(File.ReadAllBytes(asarPath), arrowFunctionArchive),
            "模型过滤改为箭头函数后没有回退完整 AST 或无法完整往返。");

        byte[] mismatchedBindingEntry = Encoding.UTF8.GetBytes(
            "const settings={available_models:[]};" +
            "function filter({availableModels:x},r){return u?n.has(r.model):!r.hidden;}");
        byte[] mismatchedBindingArchive = BuildTestAsar(
            "{\"files\":{" + BuildAsarEntryJson(
                "webview/assets/model-filter-binding-mismatch.js",
                mismatchedBindingEntry,
                0) + "}}",
            mismatchedBindingEntry);
        File.WriteAllBytes(asarPath, mismatchedBindingArchive);
        Assert(!ModelCatalogCompatibility.TryConfigure(executablePath, true, delegate { }) &&
            BytesEqual(File.ReadAllBytes(asarPath), mismatchedBindingArchive),
            "模型集合未绑定 availableModels 参数时仍被错误修改。");

        byte[] shadowedBindingEntry = Encoding.UTF8.GetBytes(
            "const settings={available_models:[]};" +
            "function outer({availableModels:n}){" +
            "return function inner(n,r){return u?n.has(r.model):!r.hidden;};}");
        byte[] shadowedBindingArchive = BuildTestAsar(
            "{\"files\":{" + BuildAsarEntryJson(
                "webview/assets/model-filter-shadowed-binding.js",
                shadowedBindingEntry,
                0) + "}}",
            shadowedBindingEntry);
        File.WriteAllBytes(asarPath, shadowedBindingArchive);
        Assert(!ModelCatalogCompatibility.TryConfigure(executablePath, true, delegate { }) &&
            BytesEqual(File.ReadAllBytes(asarPath), shadowedBindingArchive),
            "内层同名参数遮蔽外层 availableModels 绑定时仍被错误修改。");

        byte[] duplicateInlineEntry = Encoding.UTF8.GetBytes(
            "const settings={available_models:[]};" +
            "function filter({availableModels:n},r){return u?n.has(r.model):!r.hidden;}");
        byte[] duplicateFilterEntry = Encoding.UTF8.GetBytes(
            "function second({avail\\u0061bleModels:n},r){" +
            "return u?n[\"has\"](r[\"model\"]):!r[\"hidden\"];}");
        byte[] duplicatePayload = CombineBytes(duplicateInlineEntry, duplicateFilterEntry);
        byte[] duplicateArchive = BuildTestAsar(
            "{\"files\":{" +
            BuildAsarEntryJson(
                "webview/assets/model-list-filter-test.js",
                duplicateInlineEntry,
                0) + "," +
            BuildAsarEntryJson(
                "webview/assets/runtime-peer-test.js",
                duplicateFilterEntry,
                duplicateInlineEntry.Length) +
            "}}",
            duplicatePayload);
        File.WriteAllBytes(asarPath, duplicateArchive);
        Assert(!ModelCatalogCompatibility.TryConfigure(executablePath, true, delegate { }) &&
            BytesEqual(File.ReadAllBytes(asarPath), duplicateArchive),
            "存在两个模型过滤候选时仍被当作唯一语义入口。");

    }

    private static string DescribeCompatibilityResult(CompatibilityResult result)
    {
        if (result == null) return "结果=null；";
        return "提交=" + result.TransactionCommitted + "；" +
            string.Join(" | ", result.FeatureResults.Select(feature =>
                feature.FeatureId + "=" + feature.Before + "->" + feature.After +
                "/" + feature.Status + "/changed=" + feature.Changed +
                "/recipe=" + feature.RecipeId +
                (string.IsNullOrWhiteSpace(feature.Error)
                    ? string.Empty
                    : "/error=" + feature.Error)).ToArray()) + "；";
    }

    private static void TestCombinedAsarCompatibilityPlan()
    {
        string caseRoot = NewCaseRoot("combined-asar-plan");
        string executableRoot = Path.Combine(caseRoot, "app");
        string resourcesRoot = Path.Combine(executableRoot, "resources");
        string executablePath = Path.Combine(executableRoot, "Codex.exe");
        string asarPath = Path.Combine(resourcesRoot, "app.asar");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });

        byte[] payload = Encoding.UTF8.GetBytes(
            "const settings={available_models:[]};" +
            "function filter({availableModels:n},r){return u?n.has(r.model):!r.hidden;}");
        string header = "{\"files\":{" + BuildAsarEntryJson("webview/assets/model-list-filter-combined.js", payload, 0) + "}}";
        byte[] originalAsar = BuildTestAsar(header, payload);
        File.WriteAllBytes(asarPath, originalAsar);

        List<string> logs = new List<string>();
        CompatibilityPlan plan = new CompatibilityPlan(logs.Add);

        CompatibilityOptions enableOptions = new CompatibilityOptions(false, true, false, false);
        CompatibilityPlanResult enabled = plan.Apply(executablePath, enableOptions);
        Assert(enabled.ModelCatalogSucceeded, "统一计划未成功应用模型补丁。");
        string enabledText = Encoding.UTF8.GetString(File.ReadAllBytes(asarPath));
        Assert(enabledText.Contains(ModelCatalogCompatibility.PatchedMarker),
            "统一计划缺少模型补丁结果。");

        CompatibilityOptions disableOptions = new CompatibilityOptions(false, false, false, false);
        CompatibilityPlanResult disabled = plan.Apply(executablePath, disableOptions);
        Assert(disabled.ModelCatalogSucceeded, "统一计划未成功恢复模型补丁。");
        Assert(BytesEqual(File.ReadAllBytes(asarPath), originalAsar),
            "统一计划关闭模型补丁后没有字节级恢复原始 ASAR。");
    }

    private static void TestCompatibilityPlanKeepsSupportedFeatureWhenPeerDrifts()
    {
        string caseRoot = NewCaseRoot("compatibility-target-drift");
        string executableRoot = Path.Combine(caseRoot, "app");
        string resourcesRoot = Path.Combine(executableRoot, "resources");
        string executablePath = Path.Combine(executableRoot, "Codex.exe");
        string asarPath = Path.Combine(resourcesRoot, "app.asar");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });

        byte[] changedModelUi = Encoding.UTF8.GetBytes(
            "const futureModelCatalog={source:'changed-server-catalog'};");
        byte[] reasoning = Encoding.UTF8.GetBytes(string.Join(";", new[]
        {
            "composer.mode.local.reasoning.minimal.label",
            "composer.mode.local.reasoning.low.label",
            "composer.mode.local.reasoning.medium.label",
            "composer.mode.local.reasoning.high.label",
            "composer.mode.local.reasoning.xhigh.label",
            "composer.mode.local.reasoning.max.label",
            "composer.mode.local.reasoning.ultra.label"
        }));
        byte[] payload = CombineBytes(changedModelUi, reasoning);
        string header = "{\"files\":{" +
            BuildAsarEntryJson(
                "webview/assets/model-catalog-changed.js",
                changedModelUi,
                0) + "," +
            BuildAsarEntryJson(
                "webview/assets/zh-CN-future.js",
                reasoning,
                changedModelUi.Length) + "}}";
        byte[] original = BuildTestAsar(header, payload);
        File.WriteAllBytes(asarPath, original);

        CompatibilityPlan plan = new CompatibilityPlan(delegate { });
        CompatibilityPlanResult applied = plan.Apply(
            executablePath,
            new CompatibilityOptions(false, true, false, true));
        Assert(!applied.ModelCatalogSucceeded &&
            applied.ModelCatalogChange.Status == CompatibilityFeatureStatus.Unsupported &&
            !applied.ModelCatalogChange.Changed,
            "模型目标结构漂移后没有安全降级为未改写的 Unsupported。");
        Assert(applied.LocalizationSucceeded &&
            applied.LocalizationChange.Status == CompatibilityFeatureStatus.Applied &&
            applied.LocalizationChange.Changed &&
            Encoding.UTF8.GetString(File.ReadAllBytes(asarPath)).Contains(
                "composer.mode.local.reasoning.low.labe_"),
            "模型结构漂移错误阻断了仍可支持的推理英文变换。");

        CompatibilityPlanResult restored = plan.Apply(
            executablePath,
            new CompatibilityOptions(false, false, false, false));
        Assert(restored.LocalizationSucceeded &&
            BytesEqual(File.ReadAllBytes(asarPath), original),
            "部分成功场景关闭后没有字节级恢复原始 ASAR。");

        List<string> stagingLogs = new List<string>();
        CompatibilityResult stagingFallback = new CompatibilityCoordinator(stagingLogs.Add)
            .ApplyOfficialStaging(
                executablePath,
                new CompatibilityOptions(false, true, false, false));
        Assert(stagingFallback.AllSucceeded &&
            stagingFallback.ModelCatalog.Status == CompatibilityFeatureStatus.NotRequired &&
            string.Equals(stagingFallback.ModelCatalog.After, "Official", StringComparison.OrdinalIgnoreCase) &&
            !stagingFallback.ModelCatalog.Changed &&
            BytesEqual(File.ReadAllBytes(asarPath), original),
            "可信 staging 遇到不支持的模型白名单结构时没有默认关闭并保持官方文件。");
        Assert(stagingLogs.Any(value => value.IndexOf(
                "已默认关闭",
                StringComparison.Ordinal) >= 0),
            "可信 staging 的不支持功能默认关闭没有记录简要原因。");
    }

    private static void TestCompatibilityFeatureResultsRemainDetailed()
    {
        string caseRoot = NewCaseRoot("compatibility-feature-results");
        string executableRoot = Path.Combine(caseRoot, "app");
        string resourcesRoot = Path.Combine(executableRoot, "resources");
        string executablePath = Path.Combine(executableRoot, "Codex.exe");
        string asarPath = Path.Combine(resourcesRoot, "app.asar");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });
        byte[] payload = Encoding.UTF8.GetBytes(
            "const settings={available_models:[]};" +
            "function filter({availableModels:n},r){return u?n.has(r.model):!r.hidden;}");
        File.WriteAllBytes(
            asarPath,
            BuildTestAsar(
                "{\"files\":{" + BuildAsarEntryJson(
                    "webview/assets/model-list-filter-test.js",
                    payload,
                    0) + "}}",
                payload));

        CompatibilityOptions options = CreateCompatibilityOptions(false, true, false, false);
        CompatibilityPlan plan = new CompatibilityPlan(delegate { });
        CompatibilityPlanResult applied = plan.Apply(executablePath, options);
        CompatibilityFeatureResult first = applied.ModelCatalogChange.ToFeatureResult(
            "ModelCatalog",
            "模型目录",
            "Patched",
            ModelCatalogCompatibility.RecipeId);
        Assert(first.Before == "Official" && first.Desired == "Patched" && first.After == "Patched",
            "模型补丁结果没有保留 Before/Desired/After。");
        Assert(first.Changed && first.Status == CompatibilityFeatureStatus.Applied &&
            first.RecipeId == ModelCatalogCompatibility.RecipeId,
            "模型补丁结果没有保留 Changed/Status/RecipeId。");

        CompatibilityPlanResult repeated = plan.Apply(executablePath, options);
        CompatibilityFeatureResult second = repeated.ModelCatalogChange.ToFeatureResult(
            "ModelCatalog",
            "模型目录",
            "Patched",
            ModelCatalogCompatibility.RecipeId);
        Assert(!second.Changed && second.Status == CompatibilityFeatureStatus.AlreadySatisfied &&
            second.Before == "Patched" && second.After == "Patched",
            "重复应用没有被区分为 AlreadySatisfied。");

        CompatibilityResult rolledBack = new CompatibilityResult
        {
            ModelCatalogSucceeded = true,
            SandboxSucceeded = true,
            LocalizationSucceeded = false,
            ModelCatalog = first,
            Localization = new CompatibilityFeatureResult
            {
                FeatureId = "Localization",
                DisplayName = "界面语言",
                Before = "Unsupported",
                Desired = "Patched",
                After = "Unsupported",
                Status = CompatibilityFeatureStatus.Unsupported,
                RecipeId = CodexLocalizationCompatibility.RecipeId,
                Error = "测试不支持指纹"
            }
        };
        rolledBack.MarkChangedFeaturesRolledBack();
        Assert(first.Status == CompatibilityFeatureStatus.RolledBack &&
            !first.Changed && first.After == first.Before,
            "整体事务回滚后已应用功能没有改写为 RolledBack 实际状态。");
        Assert(rolledBack.Localization.Status == CompatibilityFeatureStatus.Unsupported &&
            rolledBack.Localization.Error == "测试不支持指纹",
            "整体事务回滚遮蔽了原始不支持状态和错误。");
    }

    private static void TestReasoningMixedStateRejected()
    {
        string caseRoot = NewCaseRoot("reasoning-mixed-state");
        string executableRoot = Path.Combine(caseRoot, "app");
        string resourcesRoot = Path.Combine(executableRoot, "resources");
        string executablePath = Path.Combine(executableRoot, "Codex.exe");
        string asarPath = Path.Combine(resourcesRoot, "app.asar");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });

        string[] keys =
        {
            "composer.mode.local.reasoning.none.label",
            "composer.mode.local.reasoning.minimal.label",
            "composer.mode.local.reasoning.low.label",
            "composer.mode.local.reasoning.medium.label",
            "composer.mode.local.reasoning.high.label",
            "composer.mode.local.reasoning.xhigh.label",
            "composer.mode.local.reasoning.max.label",
            "composer.mode.local.reasoning.ultra.label"
        };
        keys[2] = keys[2].Substring(0, keys[2].Length - 1) + "_";
        byte[] payload = Encoding.UTF8.GetBytes(string.Join(";", keys));
        string header = "{\"files\":{" + BuildAsarEntryJson("webview/assets/zh-CN-test.js", payload, 0) + "}}";
        byte[] mixedAsar = BuildTestAsar(header, payload);
        File.WriteAllBytes(asarPath, mixedAsar);

        List<string> logs = new List<string>();
        bool configured = CodexLocalizationCompatibility.TryConfigure(
            executablePath,
            false,
            false,
            logs.Add);
        Assert(!configured, "推理键族只有部分受管时不应报告成功。");
        Assert(BytesEqual(File.ReadAllBytes(asarPath), mixedAsar), "拒绝推理键混合状态时原 ASAR 被修改。");
        Assert(logs.Exists(value => value.IndexOf("Mixed", StringComparison.Ordinal) >= 0 ||
            value.IndexOf("混合状态", StringComparison.Ordinal) >= 0),
            "推理键族混合状态缺少明确诊断。");
    }

    private static void TestLocaleMenuResourcePatchRoundTrip()
    {
        string caseRoot = NewCaseRoot("locale-menu-resource-round-trip");
        string executableRoot = Path.Combine(caseRoot, "app");
        string resourcesRoot = Path.Combine(executableRoot, "resources");
        string executablePath = Path.Combine(executableRoot, "Codex.exe");
        string asarPath = Path.Combine(resourcesRoot, "app.asar");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });

        byte[] main = Encoding.UTF8.GetBytes(
            "const nativeLocale=`native-menu-locales`,menuIntl=`menuTitleIntlId`;" +
            "const ignored=/^\\p{Default_Ignorable_Code_Point}$/u;" +
            string.Join(
                ";",
                CodexLocalizationCompatibility.CurrentLocaleMenuTranslations
                    .Select(pair => pair.Key)
                    .ToArray()) +
            ";settings.getEffective(config.desktop.localeOverride.key);" +
            ";const traceStart=`Start Performance Trace`,traceStop=`Stop Performance Trace`;" +
            "function traceLabel(state){return state===`recording`?traceStop:" +
            "state===`awaiting-start-confirmation`?traceWaiting:" +
            "state===`saving`?traceSaving:state===`awaiting-upload-details`?traceDetails:" +
            "state===`uploading`?traceUploading:traceStart}" +
            "function trayExit ( applicationName ) { const trayMenu = electron.Menu.buildFromTemplate(" +
            "[{label:'ignored',role:'quit'}]);return (Array.isArray(trayMenu) ? trayMenu : " +
            "trayMenu.items)[0]?.label ?? `Quit $" +
            "{applicationName}`}" +
            "class TrayMenu{getNativeTrayMenuItems(){let{runningThreads:n}=this.trayMenuThreads," +
            "more=this.nativeIntl.formatMessage({messageId:tray.more,defaultMessage:More})," +
            "newTask=this.nativeIntl.formatMessage({messageId:tray.newChat,defaultMessage:NewChat});" +
            "return[{label:more},{label:newTask,click:()=>this.onTrayMenuOpenNewThread()}]}" +
            "updateChronicleTrayIcon(){}}" +
            "function decoy(){let applicationMenu=electron.Menu.buildFromTemplate(fake);" +
            "applicationMenu.getMenuItemById(fakeId)}" +
            "const template=[{id:'file-menu'}," +
            "{submenu:[{label:\"Release Notes\"},{label:`About ChatGPT`}],id:\"support-menu\"}];" +
            "applicationMenu=electron.Menu.buildFromTemplate(template)," +
            "editMenu=applicationMenu.getMenuItemById(ids.edit)?.submenu;" +
            "fileMenu=applicationMenu.getMenuItemById(ids.file)?.submenu;" +
            "fileMenu.append(new electron.MenuItem({label:`Log Out`}));" +
            "fileMenu.append(new electron.MenuItem({role:`quit`}));" +
            "electron . Menu . setApplicationMenu ( applicationMenu );" +
            "const menuFactory=({appVersion:v,errorReporter:x,globalState:g,buildFlavor:b})=>{let ready=true;};" +
            "menuFactory({appVersion:version,errorReporter:error,globalState:context.globalState,buildFlavor:build});" +
            "class Settings{refreshApplicationMenu(){}applySettingSideEffects(key,value){key&&value}}");
        byte[] locale = Encoding.UTF8.GetBytes("{\"existing\":\"保留\"}");
        byte[] payload = CombineBytes(main, locale);
        string header = "{\"files\":{" +
            BuildAsarEntryJson(".vite/build/main-test.js", main, 0) + "," +
            BuildAsarEntryJson(
                "native-menu-locales/zh-CN.json",
                locale,
                main.Length) + "}}";
        byte[] original = BuildTestAsar(header, payload);
        File.WriteAllBytes(asarPath, original);

        bool enabled = CodexLocalizationCompatibility.TryConfigure(
            executablePath,
            true,
            false,
            delegate { });
        Assert(enabled, "结构化中文菜单资源没有成功启用。");
        string patchedLocale = Encoding.UTF8.GetString(
            CodexLocalizationCompatibility.ReadCurrentMenuResourceForValidation(executablePath));
        Assert(patchedLocale.IndexOf(
                CodexLocalizationCompatibility.LocaleMenuMarkerKey,
                StringComparison.Ordinal) >= 0 &&
            CodexLocalizationCompatibility.CurrentLocaleMenuTranslations.All(pair =>
                patchedLocale.IndexOf(
                    "\"" + pair.Key + "\":\"" + pair.Value + "\"",
                    StringComparison.Ordinal) >= 0) &&
            patchedLocale.IndexOf("\"existing\":\"保留\"", StringComparison.Ordinal) >= 0,
            "结构化中文菜单资源没有完整保留官方键并追加翻译。");
        string patchedMain = Encoding.UTF8.GetString(
            CodexLocalizationCompatibility.ReadCurrentNativeMenuScriptForValidation(executablePath));
        Assert(
            patchedMain.IndexOf(
                CodexLocalizationCompatibility.NativeMenuScriptMarker,
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                CodexLocalizationCompatibility.NativeTrayExitMarker,
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                CodexLocalizationCompatibility.NativeTrayLabelsMarker,
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                CodexLocalizationCompatibility.NativeMenuSettingsStoreMarker,
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                CodexLocalizationCompatibility.NativeMenuLocaleRefreshMarker,
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                CodexLocalizationCompatibility.NativeTraceResolverMarker,
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                "/^zh(?:-|_|$)/i.test(CPMSettingsStore?.getEffective(" +
                    "config.desktop.localeOverride.key)??electron.app.getLocale())",
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                "let CPMTranslateMenu=CPMCurrentMenu=>",
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                "globalThis.__codexPortableManagerMenuLabels",
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                "CPMFallbacks",
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                "CPMChinese",
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                "for(let CPMMenuItem of CPMCurrentMenu.items??[]){if(",
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                "CPMMenuItem.submenu&&CPMTranslateMenu(CPMMenuItem.submenu)}};CPMTranslateMenu",
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                "CPMTrayFormat=CPMTrayMessage=>{",
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                "CPMTrayChinese=/^zh(?:-|_|$)/i.test(CPMSettingsStore?.getEffective(`localeOverride`)??electron.app.getLocale())",
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                "CPMTrayChinese?(CPMTrayTranslations[CPMTrayDefault]??this.nativeIntl.formatMessage(CPMTrayMessage)):CPMTrayDefault",
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                "\"New Chat\":\"新建任务\"",
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                "\"About ChatGPT\":\"关于 ChatGPT\"",
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                "more=CPMTrayFormat({messageId:tray.more,defaultMessage:More})",
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                "newTask=CPMTrayFormat({messageId:tray.newChat,defaultMessage:NewChat})",
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf("let n=e=>", StringComparison.Ordinal) < 0 &&
            patchedMain.IndexOf("\"Undo\":\"撤销\"", StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                "\"Start Performance Trace\":\"开始性能跟踪\"",
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf("`Start Performance Trace`", StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf(
                "\"Stop Performance Trace\":\"停止性能跟踪\"",
                StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf("`Stop Performance Trace`", StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf("?`退出`:", StringComparison.Ordinal) >= 0 &&
            patchedMain.IndexOf("`退出`", StringComparison.Ordinal) >= 0,
            "顶部菜单、托盘静态标签、动态性能跟踪或托盘退出项没有按 Codex 当前语言进入条件化变换。");

        bool disabled = CodexLocalizationCompatibility.TryConfigure(
            executablePath,
            false,
            false,
            delegate { });
        Assert(disabled && BytesEqual(File.ReadAllBytes(asarPath), original),
            "关闭结构化中文菜单后没有字节级恢复官方 ASAR。");
    }

    private static void TestLocalizationComponentsCommitIndependently()
    {
        string caseRoot = NewCaseRoot("localization-components-independent");
        string executableRoot = Path.Combine(caseRoot, "app");
        string resourcesRoot = Path.Combine(executableRoot, "resources");
        string executablePath = Path.Combine(executableRoot, "Codex.exe");
        string asarPath = Path.Combine(resourcesRoot, "app.asar");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });

        string[] keys =
        {
            "composer.mode.local.reasoning.none.label",
            "composer.mode.local.reasoning.minimal.label",
            "composer.mode.local.reasoning.low.label",
            "composer.mode.local.reasoning.medium.label",
            "composer.mode.local.reasoning.high.label",
            "composer.mode.local.reasoning.xhigh.label",
            "composer.mode.local.reasoning.max.label",
            "composer.mode.local.reasoning.adaptive.label",
            "composer.mode.local.reasoning.adaptive.label",
            "composer.mode.local.reasoning.adaptive.label",
            "composer.mode.local.reasoning.adaptive.label"
        };
        byte[] reasoning = Encoding.UTF8.GetBytes(string.Join(";", keys));
        byte[] original = BuildTestAsar(
            "{\"files\":{" + BuildAsarEntryJson(
                "webview/assets/locale-runtime-test.js",
                reasoning,
                0) + "}}",
            reasoning);
        File.WriteAllBytes(asarPath, original);

        bool allSucceeded = CodexLocalizationCompatibility.TryConfigure(
            executablePath,
            true,
            true,
            delegate { });
        byte[] changed = File.ReadAllBytes(asarPath);
        Assert(!allSucceeded &&
            !BytesEqual(changed, original) &&
            Encoding.UTF8.GetString(changed).IndexOf(
                "composer.mode.local.reasoning.low.labe_",
                StringComparison.Ordinal) >= 0 &&
            Encoding.UTF8.GetString(changed).IndexOf(
                "composer.mode.local.reasoning.adaptive.labe_",
                StringComparison.Ordinal) >= 0 &&
            Regex.Matches(
                Encoding.UTF8.GetString(changed),
                Regex.Escape("composer.mode.local.reasoning.adaptive.labe_")).Count == 4,
            "菜单不支持时已验证的推理英文组件没有独立提交。");

        bool restored = CodexLocalizationCompatibility.TryConfigure(
            executablePath,
            false,
            false,
            delegate { });
        Assert(restored && BytesEqual(File.ReadAllBytes(asarPath), original),
            "动态发现的专业参数语言键族没有完整恢复。");
    }

    private static void TestLocalizationMenuAppliesSupportedSubset()
    {
        string caseRoot = NewCaseRoot("localization-menu-supported-subset");
        string executableRoot = Path.Combine(caseRoot, "app");
        string resourcesRoot = Path.Combine(executableRoot, "resources");
        string executablePath = Path.Combine(executableRoot, "Codex.exe");
        string asarPath = Path.Combine(resourcesRoot, "app.asar");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });

        byte[] main = Encoding.UTF8.GetBytes(
            "const locale='native-menu-locales',title='menuTitleIntlId';" +
            string.Join(";", CodexLocalizationCompatibility.CurrentLocaleMenuTranslations
                .Select(value => value.Key).ToArray()));
        byte[] locale = Encoding.UTF8.GetBytes("{\"official\":\"保留\"}");
        byte[] payload = CombineBytes(main, locale);
        byte[] original = BuildTestAsar(
            "{\"files\":{" +
            BuildAsarEntryJson(".vite/build/main-partial.js", main, 0) + "," +
            BuildAsarEntryJson("native-menu-locales/zh-CN.json", locale, main.Length) +
            "}}",
            payload);
        File.WriteAllBytes(asarPath, original);

        List<string> logs = new List<string>();
        Assert(CodexLocalizationCompatibility.TryConfigure(
                executablePath,
                true,
                false,
                logs.Add),
            "主进程脚本不匹配时，没有继续应用可验证的中文菜单资源。");
        byte[] enabledArchive = File.ReadAllBytes(asarPath);
        string changed = Encoding.UTF8.GetString(enabledArchive);
        Assert(changed.IndexOf(
                CodexLocalizationCompatibility.LocaleMenuMarkerKey,
                StringComparison.Ordinal) >= 0 &&
            logs.Any(value => value.IndexOf("未匹配部分保持官方状态", StringComparison.Ordinal) >= 0),
            "部分兼容提交后缺少资源标记或明确提示。");
        Assert(CodexLocalizationCompatibility.TryConfigure(
                executablePath,
                false,
                false,
                delegate { }) &&
            BytesEqual(File.ReadAllBytes(asarPath), original),
            "部分中文菜单资源补丁没有完整恢复。");
    }

    private static void TestNativeMenuScriptComponentDrift()
    {
        string caseRoot = NewCaseRoot("localization-native-menu-component-drift");
        string executableRoot = Path.Combine(caseRoot, "app");
        string resourcesRoot = Path.Combine(executableRoot, "resources");
        string executablePath = Path.Combine(executableRoot, "Codex.exe");
        string asarPath = Path.Combine(resourcesRoot, "app.asar");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });

        byte[] main = Encoding.UTF8.GetBytes(
            "const nativeLocale=`native-menu-locales`,menuIntl=`menuTitleIntlId`;" +
            "const command=`codex.commandMenuTitle.newThread`;" +
            "settings.getEffective(config.desktop.localeOverride.key);" +
            "function trayExit(applicationName){const trayMenu=electron.Menu.buildFromTemplate(" +
            "[{role:`quit`}]);return trayMenu.items[0]?.label??`Quit ${applicationName}`}" +
            "class TrayMenu{getNativeTrayMenuItems(){let more=this.nativeIntl.formatMessage(" +
            "{messageId:tray.more,defaultMessage:`More`});return[{label:more}]}}" +
            "const template=[{id:`file-menu`}];" +
            "applicationMenu=electron.Menu.buildFromTemplate(template)," +
            "fileMenu=applicationMenu.getMenuItemById(ids.file)?.submenu;" +
            "electron.Menu.setApplicationMenu(applicationMenu);" +
            "const menuFactory=({appVersion:v,globalState:g,buildFlavor:b})=>{let ready=true;};" +
            "menuFactory({appVersion:version,globalState:context.globalState,buildFlavor:build});" +
            "class Settings{refreshApplicationMenu(){}" +
            "applySettingSideEffects(key,value){key&&value}}");
        byte[] locale = Encoding.UTF8.GetBytes("{\"official\":\"保留\"}");
        byte[] payload = CombineBytes(main, locale);
        byte[] original = BuildTestAsar(
            "{\"files\":{" +
            BuildAsarEntryJson(".vite/build/application-shell.js", main, 0) + "," +
            BuildAsarEntryJson("native-menu-locales/zh-CN.json", locale, main.Length) +
            "}}",
            payload);
        File.WriteAllBytes(asarPath, original);

        using (AsarSession session = AsarSession.Open(asarPath))
        {
            CompatibilityFeatureChange observed =
                CodexLocalizationCompatibility.Inspect(session);
            Assert(observed.Succeeded &&
                observed.After.IndexOf("Menus=Official", StringComparison.Ordinal) >= 0 &&
                observed.Error != null &&
                observed.Error.IndexOf("性能跟踪文本", StringComparison.Ordinal) >= 0,
                "官方脚本缺少可选组件时没有保持可继承关闭状态和能力提示。");
        }

        List<string> logs = new List<string>();
        Assert(CodexLocalizationCompatibility.TryConfigure(
                executablePath,
                true,
                false,
                logs.Add),
            "性能跟踪入口缺失时没有继续应用其他可验证菜单组件。");
        byte[] enabledArchive = File.ReadAllBytes(asarPath);
        string changed = Encoding.UTF8.GetString(enabledArchive);
        Assert(changed.IndexOf(
                CodexLocalizationCompatibility.NativeMenuScriptMarker,
                StringComparison.Ordinal) >= 0 &&
            changed.IndexOf(
                CodexLocalizationCompatibility.NativeTrayLabelsMarker,
                StringComparison.Ordinal) >= 0 &&
            changed.IndexOf(
                CodexLocalizationCompatibility.NativeTrayExitMarker,
                StringComparison.Ordinal) >= 0 &&
            changed.IndexOf(
                CodexLocalizationCompatibility.NativeMenuSettingsStoreMarker,
                StringComparison.Ordinal) >= 0 &&
            changed.IndexOf(
                CodexLocalizationCompatibility.NativeTraceResolverMarker,
                StringComparison.Ordinal) < 0 &&
            logs.Any(value => value.IndexOf(
                "性能跟踪文本",
                StringComparison.Ordinal) >= 0) &&
            !logs.Any(value => value.IndexOf(
                "trayMenu.items",
                StringComparison.Ordinal) >= 0),
            "主脚本部分兼容没有只写入已验证组件或缺少跳过提示。");
        Assert(CodexLocalizationCompatibility.TryConfigure(
                executablePath,
                true,
                false,
                delegate { }) &&
            BytesEqual(File.ReadAllBytes(asarPath), enabledArchive),
            "主脚本部分兼容重复启用时没有保持幂等。");
        using (AsarSession session = AsarSession.Open(asarPath))
        {
            CompatibilityFeatureChange observed =
                CodexLocalizationCompatibility.Inspect(session);
            Assert(observed.Succeeded &&
                observed.After.IndexOf("Menus=Patched", StringComparison.Ordinal) >= 0 &&
                observed.Error != null &&
                observed.Error.IndexOf("性能跟踪文本", StringComparison.Ordinal) >= 0,
                "已完成的部分兼容没有保留可继承开启状态和跳过说明。");
        }
        Assert(CodexLocalizationCompatibility.TryConfigure(
                executablePath,
                false,
                false,
                delegate { }) &&
            BytesEqual(File.ReadAllBytes(asarPath), original),
            "主脚本部分兼容关闭后没有字节级恢复官方 ASAR。");
    }

    private static void TestLocalizationDisabledUnknownArchive()
    {
        string caseRoot = NewCaseRoot("localization-disabled-unknown");
        string executableRoot = Path.Combine(caseRoot, "app");
        string resourcesRoot = Path.Combine(executableRoot, "resources");
        string executablePath = Path.Combine(executableRoot, "Codex.exe");
        string asarPath = Path.Combine(resourcesRoot, "app.asar");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });
        byte[] unknownAsar = Encoding.UTF8.GetBytes("unknown upstream archive without managed compatibility markers");
        File.WriteAllBytes(asarPath, unknownAsar);

        List<string> logs = new List<string>();
        bool configured = CodexLocalizationCompatibility.TryConfigure(
            executablePath,
            false,
            false,
            logs.Add);
        Assert(configured, "语言功能全部关闭且无本工具标记时应安全跳过未知 ASAR。");
        Assert(BytesEqual(File.ReadAllBytes(asarPath), unknownAsar), "安全跳过未知 ASAR 时文件被修改。");
        Assert(logs.Exists(value => value.IndexOf("未检测到本工具", StringComparison.Ordinal) >= 0),
            "安全跳过未知 ASAR 时缺少明确日志。");

        foreach (string marker in new[]
        {
            CodexLocalizationCompatibility.NativeMenuSettingsStoreMarker,
            CodexLocalizationCompatibility.NativeMenuLocaleRefreshMarker,
            CodexLocalizationCompatibility.NativeTraceResolverMarker,
            CodexLocalizationCompatibility.NativeMenuManagedPrefix + "future-component*/"
        })
        {
            byte[] markedPayload = Encoding.UTF8.GetBytes("const damaged='" + marker + "';");
            byte[] markedArchive = BuildTestAsar(
                "{\"files\":{" + BuildAsarEntryJson(
                    ".vite/build/future-menu.js",
                    markedPayload,
                    0) + "}}",
                markedPayload);
            File.WriteAllBytes(asarPath, markedArchive);
            logs.Clear();
            Assert(!CodexLocalizationCompatibility.TryConfigure(
                    executablePath,
                    false,
                    false,
                    logs.Add) &&
                BytesEqual(File.ReadAllBytes(asarPath), markedArchive) &&
                !logs.Any(value => value.IndexOf(
                    "未检测到本工具",
                    StringComparison.Ordinal) >= 0),
                "残留菜单子标记被误当作未管理状态跳过：" + marker);
        }
    }

    private static void TestLocaleMenuTracksActualConsumerKeys()
    {
        string caseRoot = NewCaseRoot("locale-menu-consumer-drift");
        string executableRoot = Path.Combine(caseRoot, "app");
        string resourcesRoot = Path.Combine(executableRoot, "resources");
        string executablePath = Path.Combine(executableRoot, "Codex.exe");
        string asarPath = Path.Combine(resourcesRoot, "app.asar");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });

        byte[] main = Encoding.UTF8.GetBytes(
            "const locale='native-menu-locales',title='menuTitleIntlId';" +
            "const known='codex.commandMenuTitle.newThread';" +
            "const future='codex.commandMenuTitle.futureCommand';");
        byte[] locale = Encoding.UTF8.GetBytes("{\"official\":\"保留\"}");
        byte[] payload = CombineBytes(main, locale);
        byte[] original = BuildTestAsar(
            "{\"files\":{" +
            BuildAsarEntryJson(".vite/build/application-shell.js", main, 0) + "," +
            BuildAsarEntryJson("native-menu-locales/zh-CN.json", locale, main.Length) +
            "}}",
            payload);
        File.WriteAllBytes(asarPath, original);

        List<string> logs = new List<string>();
        Assert(CodexLocalizationCompatibility.TryConfigure(
                executablePath,
                true,
                false,
                logs.Add),
            "菜单消费者增减时没有应用仍可验证的已知翻译。");
        string changed = Encoding.UTF8.GetString(File.ReadAllBytes(asarPath));
        Assert(changed.IndexOf(
                "\"codex.commandMenuTitle.newThread\":\"新任务\"",
                StringComparison.Ordinal) >= 0 &&
            changed.IndexOf("codex.commandMenuTitle.newWindow", StringComparison.Ordinal) < 0 &&
            changed.IndexOf("codex.commandMenuTitle.futureCommand\":", StringComparison.Ordinal) < 0 &&
            logs.Any(value => value.IndexOf("暂无中文翻译", StringComparison.Ordinal) >= 0),
            "菜单消费者动态集合没有丢弃已删除键、保留未知新增键或给出提示。");
        Assert(CodexLocalizationCompatibility.TryConfigure(
                executablePath,
                false,
                false,
                delegate { }) &&
            BytesEqual(File.ReadAllBytes(asarPath), original),
            "动态菜单消费者补丁关闭后没有字节级恢复。");
    }

    private static void TestAsarSessionRetainsOnlyTargetEntry()
    {
        string caseRoot = NewCaseRoot("asar-session-memory-boundary");
        string patternPath = Path.Combine(caseRoot, "pattern-boundary.bin");
        byte[] patternData = Enumerable.Repeat((byte)'x', 1024 * 1024 + 8).ToArray();
        byte[] boundaryPattern = Encoding.ASCII.GetBytes("ababa");
        Buffer.BlockCopy(
            boundaryPattern,
            0,
            patternData,
            1024 * 1024 - 2,
            boundaryPattern.Length);
        byte[] overlapPattern = Encoding.ASCII.GetBytes("aaaaa");
        Buffer.BlockCopy(overlapPattern, 0, patternData, 64, overlapPattern.Length);
        File.WriteAllBytes(patternPath, patternData);
        IDictionary<string, int> patternCounts = AsarSession.CountPatterns(
            patternPath,
            new[] { "ababa", "aaa" });
        Assert(patternCounts["ababa"] == 1 && patternCounts["aaa"] == 3,
            "ASAR 模式计数没有保留跨缓冲区和重叠匹配语义。");

        string asarPath = Path.Combine(caseRoot, "app.asar");
        byte[] large = new byte[4 * 1024 * 1024];
        for (int index = 0; index < large.Length; index++) large[index] = (byte)(index % 251);
        byte[] target = Encoding.UTF8.GetBytes("const target='small';");
        byte[] payload = CombineBytes(large, target);
        string header = "{\"files\":{" +
            BuildAsarEntryJson("large.bin", large, 0) + "," +
            BuildAsarEntryJson("target.js", target, large.Length) + "}}";
        File.WriteAllBytes(asarPath, BuildTestAsar(header, payload));

        using (AsarSession session = AsarSession.Open(asarPath))
        {
            Assert(session.RetainedEntryBytes == 0,
                "ASAR 会话打开时不应加载 payload 条目。");

            byte[] read = session.ReadEntryData("target.js");
            Assert(BytesEqual(read, target), "ASAR 会话读取的目标条目内容不正确。");
            long retainedBytes = session.RetainedEntryBytes;
            Assert(retainedBytes == target.Length,
                "ASAR 会话保留了非目标条目，实际保留字节=" + retainedBytes.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void TestAsarSessionLocksAnalyzedSource()
    {
        string caseRoot = NewCaseRoot("asar-source-identity-lock");
        string asarPath = Path.Combine(caseRoot, "app.asar");
        string replacementPath = Path.Combine(caseRoot, "replacement.asar");
        string backupPath = Path.Combine(caseRoot, "replacement-backup.asar");
        byte[] originalPayload = Encoding.UTF8.GetBytes("const source='original';");
        byte[] replacementPayload = Encoding.UTF8.GetBytes("const source='replaced';");
        byte[] originalArchive = BuildTestAsar(
            "{\"files\":{" + BuildAsarEntryJson("source.js", originalPayload, 0) + "}}",
            originalPayload);
        File.WriteAllBytes(asarPath, originalArchive);
        File.WriteAllBytes(
            replacementPath,
            BuildTestAsar(
                "{\"files\":{" + BuildAsarEntryJson("source.js", replacementPayload, 0) + "}}",
                replacementPayload));

        using (AsarSession session = AsarSession.Open(asarPath))
        {
            Exception replacementFailure = CaptureFailure(delegate
            {
                File.Replace(replacementPath, asarPath, backupPath, true);
            });
            Assert(replacementFailure is IOException || replacementFailure is UnauthorizedAccessException,
                "分析期间替换 ASAR 源路径没有被稳定源句柄阻止。实际异常：" +
                (replacementFailure == null ? "无" : replacementFailure.GetType().FullName));
            Assert(BytesEqual(session.ReadEntryData("source.js"), originalPayload),
                "源路径替换尝试后会话读取到的已不是分析时文件。");
        }
        Assert(BytesEqual(File.ReadAllBytes(asarPath), originalArchive),
            "源路径替换尝试改变了正式 ASAR。");
    }

    private static void TestCompatibilityAnalysisMemoryPolicy()
    {
        long megabyte = 1024L * 1024;
        Assert(!CompatibilityAnalysisMemory.ShouldReclaim(40 * megabyte, 90 * megabyte),
            "小规模兼容检查错误触发了全代压缩回收。");
        Assert(CompatibilityAnalysisMemory.ShouldReclaim(40 * megabyte, 104 * megabyte),
            "达到 64 MiB 的临时增长后没有安排回收。");
        Assert(CompatibilityAnalysisMemory.ShouldReclaim(250 * megabyte, 256 * megabyte),
            "托管堆达到 256 MiB 后没有安排回收。");
        Assert(!CompatibilityAnalysisMemory.ShouldReclaim(-1, 300 * megabyte),
            "无效内存采样不应触发回收。");
    }

    private static void TestAsarCommitValidatesUnmodifiedEntries()
    {
        string caseRoot = NewCaseRoot("asar-unmodified-integrity");
        string executableRoot = Path.Combine(caseRoot, "app");
        string resourcesRoot = Path.Combine(executableRoot, "resources");
        string executablePath = Path.Combine(executableRoot, "Codex.exe");
        string asarPath = Path.Combine(resourcesRoot, "app.asar");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });

        byte[] modelEntry = Encoding.UTF8.GetBytes(
            "const settings={available_models:[]};" +
            "function filter({availableModels:n},r){return u?n.has(r.model):!r.hidden;}");
        byte[] expectedUnmodified = Encoding.ASCII.GetBytes("trusted-unmodified-entry");
        byte[] corruptedUnmodified = (byte[])expectedUnmodified.Clone();
        corruptedUnmodified[corruptedUnmodified.Length - 1] ^= 0x01;
        string header = "{\"files\":{" +
                BuildAsarEntryJson("webview/assets/model-list-filter-test.js", modelEntry, 0) + "," +
            BuildAsarEntryJson("unrelated.bin", expectedUnmodified, modelEntry.Length) + "}}";
        byte[] archive = BuildTestAsar(header, CombineBytes(modelEntry, corruptedUnmodified));
        File.WriteAllBytes(asarPath, archive);

        List<string> logs = new List<string>();
        bool configured = ModelCatalogCompatibility.TryConfigure(executablePath, true, logs.Add);
        Assert(!configured, "未修改条目 integrity 已损坏时仍报告模型补丁成功。");
        Assert(BytesEqual(File.ReadAllBytes(asarPath), archive),
            "未修改条目 integrity 校验失败后正式 ASAR 仍被替换。");
        Assert(logs.Any(value => value.IndexOf("完整性", StringComparison.Ordinal) >= 0),
            "未修改条目 integrity 校验失败没有进入明确日志。");

        byte[] alreadyPatchedEntry = Encoding.UTF8.GetBytes(
            "const settings={available_models:[]};" +
            "function filter({availableModels:n},r){return !r.hidden||(false&&" +
            "(u?n.has(r.model):!r.hidden))" + ModelCatalogCompatibility.PatchedMarker + ";}");
        string noChangeHeader = "{\"files\":{" +
                BuildAsarEntryJson("webview/assets/model-list-filter-test.js", alreadyPatchedEntry, 0) + "," +
            BuildAsarEntryJson("unrelated.bin", expectedUnmodified, alreadyPatchedEntry.Length) + "}}";
        byte[] noChangeArchive = BuildTestAsar(
            noChangeHeader,
            CombineBytes(alreadyPatchedEntry, corruptedUnmodified));
        File.WriteAllBytes(asarPath, noChangeArchive);
        Assert(!ModelCatalogCompatibility.TryConfigure(executablePath, true, delegate { }),
            "补丁已是目标状态时跳过了未修改条目的完整性验证。");
        Assert(BytesEqual(File.ReadAllBytes(asarPath), noChangeArchive),
            "无需写入路径的完整性验证失败后正式 ASAR 被修改。");
    }

    private static void TestAsarStagingTransactionRollsBackOnFailure()
    {
        string caseRoot = NewCaseRoot("asar-staging-transaction");
        string asarPath = Path.Combine(caseRoot, "app.asar");
        byte[] original = Encoding.ASCII.GetBytes("original-stage-data");
        File.WriteAllBytes(
            asarPath,
            BuildTestAsar(
                "{\"files\":{" + BuildAsarEntryJson("entry.bin", original, 0) + "}}",
                original));
        using (AsarSession session = AsarSession.Open(asarPath))
        {
            AsarArchiveEntry entry = session.Entries.Single();
            Exception failure = CaptureFailure(delegate
            {
                session.RunStagingTransaction(delegate
                {
                    session.StageEntry(entry, Encoding.ASCII.GetBytes("replacement-stage"));
                    throw new IOException("模拟第二步暂存失败");
                });
            });
            Assert(failure is IOException,
                "ASAR 暂存事务没有向调用方返回原始失败。");
            Assert(!session.HasChanges &&
                BytesEqual(session.GetEntryData(entry), original),
                "ASAR 暂存事务失败后仍残留部分功能变更。");
        }
    }

    private static void TestLocalizationRejectsIncompleteAsarPayload()
    {
        string caseRoot = NewCaseRoot("localization-incomplete-asar");
        string executableRoot = Path.Combine(caseRoot, "app");
        string resourcesRoot = Path.Combine(executableRoot, "resources");
        string executablePath = Path.Combine(executableRoot, "Codex.exe");
        string asarPath = Path.Combine(resourcesRoot, "app.asar");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });
        byte[] firstPayload = new byte[] { (byte)'A', (byte)'B' };
        string firstHash = ComputeSha256Hex(new byte[] { firstPayload[0] });
        string missingIntegrityHeader =
            "{\"files\":{" +
            "\"valid.bin\":{\"size\":1,\"offset\":\"0\",\"integrity\":{\"algorithm\":\"SHA256\",\"hash\":\"" + firstHash + "\",\"blockSize\":4,\"blocks\":[\"" + firstHash + "\"]}}," +
            "\"missing.bin\":{\"size\":1,\"offset\":\"1\"}}}";
        byte[] missingIntegrityAsar = BuildTestAsar(missingIntegrityHeader, firstPayload);
        File.WriteAllBytes(asarPath, missingIntegrityAsar);
        List<string> missingIntegrityLogs = new List<string>();
        bool missingIntegrityConfigured = CodexLocalizationCompatibility.TryConfigure(
            executablePath,
            true,
            true,
            missingIntegrityLogs.Add);
        Assert(!missingIntegrityConfigured, "缺少 integrity 的已打包条目不应允许语言补丁继续。");
        Assert(BytesEqual(File.ReadAllBytes(asarPath), missingIntegrityAsar), "拒绝缺少 integrity 的 ASAR 时原文件被修改。");
        Assert(missingIntegrityLogs.Exists(value => value.IndexOf("缺少完整性信息", StringComparison.Ordinal) >= 0),
            "缺少 integrity 的已打包条目没有在 ASAR 解析阶段被明确拒绝。");

        string unreferencedPayloadHeader =
            "{\"files\":{" +
            "\"valid.bin\":{\"size\":1,\"offset\":\"0\",\"integrity\":{\"algorithm\":\"SHA256\",\"hash\":\"" + firstHash + "\",\"blockSize\":4,\"blocks\":[\"" + firstHash + "\"]}}}}";
        byte[] unreferencedPayloadAsar = BuildTestAsar(unreferencedPayloadHeader, firstPayload);
        File.WriteAllBytes(asarPath, unreferencedPayloadAsar);
        List<string> unreferencedPayloadLogs = new List<string>();
        bool unreferencedPayloadConfigured = CodexLocalizationCompatibility.TryConfigure(
            executablePath,
            true,
            true,
            unreferencedPayloadLogs.Add);
        Assert(!unreferencedPayloadConfigured, "存在未引用 payload 数据的 ASAR 不应允许语言补丁继续。");
        Assert(BytesEqual(File.ReadAllBytes(asarPath), unreferencedPayloadAsar), "拒绝 payload 覆盖不完整的 ASAR 时原文件被修改。");
        Assert(unreferencedPayloadLogs.Exists(value => value.IndexOf("未被已打包条目引用", StringComparison.Ordinal) >= 0),
            "未引用的 payload 数据没有在 ASAR 解析阶段被明确拒绝。");
    }

    private static void TestSandboxCompatibilityBestEffort()
    {
        string caseRoot = NewCaseRoot("sandbox-compatibility-best-effort");
        string missingExecutable = Path.Combine(caseRoot, "app", "Codex.exe");
        List<string> logs = new List<string>();
        bool configured = SandboxCompatibility.TryConfigure(missingExecutable, true, logs.Add);
        Assert(!configured, "缺少 resources 时沙箱兼容配置不应报告成功。");
        Assert(logs.Exists(value => value.IndexOf("已保留当前 app.asar", StringComparison.Ordinal) >= 0),
            "沙箱兼容安全降级没有记录明确警告。");
    }

    private static void TestSandboxCompatibilityAsarRoundTrip()
    {
        string caseRoot = NewCaseRoot("sandbox-account-environment-round-trip");
        string executableRoot = Path.Combine(caseRoot, "app");
        string resourcesRoot = Path.Combine(executableRoot, "resources");
        string executablePath = Path.Combine(executableRoot, "Codex.exe");
        string asarPath = Path.Combine(resourcesRoot, "app.asar");
        string helperPath = Path.Combine(resourcesRoot, "codex-windows-sandbox-setup.exe");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });

        string packageJson =
            "{\"name\":\"openai-codex-electron\",\"version\":\"0.0.0-test\"," +
            "\"main\":\".vite/build/early-bootstrap.js\"}";
        byte[] entry = Encoding.UTF8.GetBytes(
            "#!/usr/bin/env node\n\"use strict\"\nrequire('./bootstrap-test.js');");
        byte[] originalAsar = BuildSandboxElectronAsar(
            packageJson,
            new[] { "early-bootstrap.js" },
            new[] { entry });
        byte[] officialHelper = Encoding.ASCII.GetBytes("SIGNED_OFFICIAL_HELPER_FIXTURE");
        File.WriteAllBytes(asarPath, originalAsar);
        File.WriteAllBytes(helperPath, officialHelper);

        SandboxCompatibility.Configure(executablePath, true, delegate { });
        string enabledText;
        using (AsarSession session = AsarSession.Open(asarPath))
        {
            enabledText = Encoding.UTF8.GetString(
                session.ReadEntryData(".vite/build/early-bootstrap.js"));
        }
        Assert(SandboxCompatibility.IsEnabled(executablePath) &&
            enabledText.Contains(SandboxCompatibility.ManagedMarker) &&
            enabledText.StartsWith(
                "#!/usr/bin/env node\n\"use strict\";",
                StringComparison.Ordinal) &&
            enabledText.Contains("process.env.USERNAME") &&
            enabledText.Contains("process.env.USERDOMAIN"),
            "沙箱账户环境补丁没有注入当前唯一脚本。");
        Assert(BytesEqual(File.ReadAllBytes(helperPath), officialHelper),
            "启用沙箱账户环境补丁时改写了官方 helper。");
        string[] helperArtifacts = Directory.GetFiles(
            resourcesRoot,
            "codex-windows-sandbox-setup*",
            SearchOption.TopDirectoryOnly);
        Assert(helperArtifacts.Length == 1 && PathsEqual(helperArtifacts[0], helperPath),
            "沙箱账户环境补丁生成了额外 helper 制品。");

        SandboxCompatibility.Configure(executablePath, false, delegate { });
        Assert(!SandboxCompatibility.IsEnabled(executablePath) &&
            BytesEqual(File.ReadAllBytes(asarPath), originalAsar),
            "关闭沙箱账户环境补丁后没有字节级恢复官方 ASAR。");
        Assert(BytesEqual(File.ReadAllBytes(helperPath), officialHelper),
            "关闭沙箱账户环境补丁时改写了官方 helper。");
    }

    private static void TestSandboxCompatibilityUsesPackageMain()
    {
        string[] bundleNames = { "main-FGp_fjyX.js", "main-CmXfwZWv.js" };
        string[] bootstrapNames = { "bootstrap-X3A_test.js", "bootstrap-Y7B_test.js" };
        for (int index = 0; index < bundleNames.Length; index++)
        {
            string caseRoot = NewCaseRoot("sandbox-package-main-" + index);
            string executableRoot = Path.Combine(caseRoot, "app");
            string resourcesRoot = Path.Combine(executableRoot, "resources");
            string executablePath = Path.Combine(executableRoot, "Codex.exe");
            string asarPath = Path.Combine(resourcesRoot, "app.asar");
            Directory.CreateDirectory(resourcesRoot);
            File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });

            string packageJson =
                "{\"name\":\"openai-codex-electron\"," +
                "\"main\":\".vite/build/early-bootstrap.js\"}";
            byte[] entry = Encoding.UTF8.GetBytes(
                "require('./" + bootstrapNames[index] + "');");
            byte[] unrelatedBundle = Encoding.UTF8.GetBytes(
                "const upstreamChanged='no menu implementation anchors remain';");
            byte[] original = BuildSandboxElectronAsar(
                packageJson,
                new[] { "early-bootstrap.js", bundleNames[index] },
                new[] { entry, unrelatedBundle });
            File.WriteAllBytes(asarPath, original);

            SandboxCompatibility.Configure(executablePath, true, delegate { });
            using (AsarSession session = AsarSession.Open(asarPath))
            {
                string patchedEntry = Encoding.UTF8.GetString(
                    session.ReadEntryData(".vite/build/early-bootstrap.js"));
                string unchangedBundle = Encoding.UTF8.GetString(
                    session.ReadEntryData(".vite/build/" + bundleNames[index]));
                Assert(
                    patchedEntry.StartsWith(";(()=>{let u=process.env.USERNAME", StringComparison.Ordinal) &&
                    patchedEntry.Contains(SandboxCompatibility.ManagedMarker),
                    "package.json.main 声明的 Electron 入口没有收到沙箱环境修正。");
                Assert(
                    !unchangedBundle.Contains(SandboxCompatibility.ManagedMarker) &&
                    string.Equals(
                        unchangedBundle,
                        Encoding.UTF8.GetString(unrelatedBundle),
                        StringComparison.Ordinal),
                    "哈希 bundle 被误当成 package.json.main 入口修改。");
            }

            SandboxCompatibility.Configure(executablePath, false, delegate { });
            Assert(BytesEqual(File.ReadAllBytes(asarPath), original),
                "按 package.json.main 启停后没有字节级恢复不同哈希 bundle 的官方 ASAR。");
        }
    }

    private static void TestSandboxCompatibilityRejectsInvalidEntryMetadata()
    {
        string[] invalidPackages =
        {
            "{\"name\":\"openai-codex-electron\"}",
            "{\"name\":\"unexpected-electron-package\",\"main\":\".vite/build/early-bootstrap.js\"}",
            "{\"name\":\"openai-codex-electron\",\"main\":\"../early-bootstrap.js\"}",
            "{\"name\":\"openai-codex-electron\",\"main\":\"/early-bootstrap.js\"}",
            "{\"name\":\"openai-codex-electron\",\"main\":\"C:/early-bootstrap.js\"}",
            "{\"name\":\"openai-codex-electron\",\"main\":\".vite\\\\build\\\\early-bootstrap.js\"}",
            "{\"name\":\"openai-codex-electron\",\"main\":\".vite//build/early-bootstrap.js\"}",
            "{\"name\":\"openai-codex-electron\",\"main\":\".vite/./build/early-bootstrap.js\"}",
            "{\"name\":\"openai-codex-electron\",\"main\":\".vite/build/missing.js\"}",
            "{\"name\":\"openai-codex-electron\",\"main\":\".vite/build/early-bootstrap.json\"}"
        };
        byte[] officialEntry = Encoding.UTF8.GetBytes("require('./bootstrap-test.js');");
        for (int index = 0; index < invalidPackages.Length; index++)
        {
            string caseRoot = NewCaseRoot("sandbox-invalid-main-" + index);
            string executableRoot = Path.Combine(caseRoot, "app");
            string resourcesRoot = Path.Combine(executableRoot, "resources");
            string executablePath = Path.Combine(executableRoot, "Codex.exe");
            string asarPath = Path.Combine(resourcesRoot, "app.asar");
            Directory.CreateDirectory(resourcesRoot);
            File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A, 0x01 });
            byte[] original = BuildSandboxElectronAsar(
                invalidPackages[index],
                new[] { "early-bootstrap.js" },
                new[] { officialEntry });
            File.WriteAllBytes(asarPath, original);

            List<string> logs = new List<string>();
            bool configured = SandboxCompatibility.TryConfigure(executablePath, true, logs.Add);
            Assert(!configured && BytesEqual(File.ReadAllBytes(asarPath), original),
                "无效 package.json.main 没有失败关闭，测试索引=" + index + "。");
        }

        string unpackedRoot = NewCaseRoot("sandbox-unpacked-main");
        string unpackedExecutableRoot = Path.Combine(unpackedRoot, "app");
        string unpackedResourcesRoot = Path.Combine(unpackedExecutableRoot, "resources");
        string unpackedExecutable = Path.Combine(unpackedExecutableRoot, "Codex.exe");
        string unpackedAsarPath = Path.Combine(unpackedResourcesRoot, "app.asar");
        Directory.CreateDirectory(unpackedResourcesRoot);
        File.WriteAllBytes(unpackedExecutable, new byte[] { 0x4D, 0x5A, 0x01 });
        byte[] unpackedPackage = Encoding.UTF8.GetBytes(
            "{\"name\":\"openai-codex-electron\"," +
            "\"main\":\".vite/build/early-bootstrap.js\"}");
        string unpackedHeader =
            "{\"files\":{" + BuildAsarEntryJson("package.json", unpackedPackage, 0) + "," +
            "\".vite\":{\"files\":{\"build\":{\"files\":{" +
            "\"early-bootstrap.js\":{\"size\":1,\"unpacked\":true}" +
            "}}}}}}";
        byte[] unpackedArchive = BuildTestAsar(unpackedHeader, unpackedPackage);
        File.WriteAllBytes(unpackedAsarPath, unpackedArchive);
        Assert(
            !SandboxCompatibility.TryConfigure(unpackedExecutable, true, delegate { }) &&
            BytesEqual(File.ReadAllBytes(unpackedAsarPath), unpackedArchive),
            "package.json.main 指向未打包条目时没有失败关闭。");

        foreach (bool markerInEntry in new[] { false, true })
        {
            string markerRoot = NewCaseRoot(
                markerInEntry ? "sandbox-misplaced-main-marker" : "sandbox-external-marker");
            string markerExecutableRoot = Path.Combine(markerRoot, "app");
            string markerResourcesRoot = Path.Combine(markerExecutableRoot, "resources");
            string markerExecutable = Path.Combine(markerExecutableRoot, "Codex.exe");
            string markerAsarPath = Path.Combine(markerResourcesRoot, "app.asar");
            Directory.CreateDirectory(markerResourcesRoot);
            File.WriteAllBytes(markerExecutable, new byte[] { 0x4D, 0x5A, 0x01 });
            string packageJson =
                "{\"name\":\"openai-codex-electron\"," +
                "\"main\":\".vite/build/early-bootstrap.js\"}";
            byte[] main = markerInEntry
                ? Encoding.UTF8.GetBytes(
                    "require('./bootstrap-test.js');" + SandboxCompatibility.ManagedMarker)
                : officialEntry;
            byte[] external = markerInEntry
                ? Encoding.UTF8.GetBytes("const clean=true;")
                : Encoding.UTF8.GetBytes(
                    "const stale='" + SandboxCompatibility.ManagedMarker + "';");
            byte[] markerArchive = BuildSandboxElectronAsar(
                packageJson,
                new[] { "early-bootstrap.js", "main-upstream.js" },
                new[] { main, external });
            File.WriteAllBytes(markerAsarPath, markerArchive);
            Assert(
                !SandboxCompatibility.TryConfigure(markerExecutable, true, delegate { }) &&
                BytesEqual(File.ReadAllBytes(markerAsarPath), markerArchive),
                "位置异常的受管 marker 没有被严格拒绝。");
        }
    }

    private static byte[] BuildSandboxElectronAsar(
        string packageJson,
        string[] buildEntryNames,
        byte[][] buildEntryData)
    {
        if (buildEntryNames == null || buildEntryData == null ||
            buildEntryNames.Length != buildEntryData.Length)
        {
            throw new ArgumentException("沙箱 ASAR 测试条目名称与数据数量不一致。");
        }

        byte[] package = Encoding.UTF8.GetBytes(packageJson);
        List<string> buildEntries = new List<string>();
        List<byte[]> payloadParts = new List<byte[]> { package };
        int offset = package.Length;
        for (int index = 0; index < buildEntryNames.Length; index++)
        {
            buildEntries.Add(BuildAsarEntryJson(
                buildEntryNames[index],
                buildEntryData[index],
                offset));
            payloadParts.Add(buildEntryData[index]);
            offset += buildEntryData[index].Length;
        }

        string header =
            "{\"files\":{" + BuildAsarEntryJson("package.json", package, 0) + "," +
            "\".vite\":{\"files\":{\"build\":{\"files\":{" +
            string.Join(",", buildEntries.ToArray()) +
            "}}}}}}";
        return BuildTestAsar(header, CombineBytes(payloadParts.ToArray()));
    }

    private static void TestCompatibilitySettingsAreStagingScoped()
    {
        MethodInfo[] installMethods = typeof(CodexPortableService).GetMethods(AnyInstance)
            .Where(method => string.Equals(method.Name, "InstallOrUpdateAsync", StringComparison.Ordinal))
            .ToArray();
        Assert(installMethods.Length > 0 && installMethods.All(method => method.GetParameters().Count(
                parameter => parameter.ParameterType == typeof(CompatibilityOptions)) == 0),
            "部署服务仍接收界面兼容快照，而不是从当前安装文件继承实际状态。");

        foreach (string methodName in new[] { "Rollback", "CreateIntegration" })
        {
            MethodInfo[] methods = typeof(CodexPortableService).GetMethods(AnyInstance)
                .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
                .ToArray();
            Assert(methods.Length > 0, "服务缺少部署方法：" + methodName);
            Assert(methods.All(method => method.GetParameters().All(parameter => parameter.ParameterType != typeof(CompatibilityOptions))),
                methodName + " 不应接收 staging 兼容设置。");
        }

        Assert(typeof(ArtifactPipeline).GetMethods(AnyInstance).All(method => method.GetParameters().All(
                parameter => parameter.ParameterType != typeof(CompatibilityOptions))),
            "兼容设置泄漏到了制品获取或验证管线。");

        MethodInfo apply = RequireMethod(
            typeof(CodexPortableService),
            "ApplyCompatibilitySettings",
            AnyInstance,
            new[] { typeof(string), typeof(CompatibilityOptions) });
        Assert(apply.ReturnType == typeof(CompatibilityResult),
            "独立功能应用接口没有返回 CompatibilityResult。");

        MethodInfo create = RequireMethod(
            typeof(ShellIntegrationCoordinator),
            "Create",
            AnyInstance,
            new[] { typeof(string) });
        Assert(create.GetParameters().Length == 1,
            "系统集成协调器仍携带沙箱功能配置参数。");
    }

    private static void TestTrustedStagingCompatibilityApplication()
    {
        string version = "1.2.3.4";

        string officialRoot = Path.Combine(NewCaseRoot("staging-compatibility-official"), "staging");
        string officialId = Guid.NewGuid().ToString("N");
        CreateHealthyCompatibilityInstallation(officialRoot, officialId, version);
        ArtifactProvenance officialBaseline = InstallOwnership.ReadInstallationRecord(officialRoot).Provenance;
        File.Delete(InstallOwnership.GetMarkerPath(officialRoot));
        int officialApplyCalls = 0;
        CompatibilityMaintenance officialMaintenance = new CompatibilityMaintenance(
            (executablePath, options) =>
            {
                officialApplyCalls++;
                throw new InvalidOperationException("全关闭设置不应进入兼容协调器。");
            },
            InstallOwnership.WriteMarker,
            delegate { });
        PackageProfile officialProfile = PackageProfileReader.Read(officialRoot);
        CompatibilityResult officialResult = officialMaintenance.ApplyTrustedStaging(
            officialRoot,
            officialProfile,
            officialId,
            CreateCompatibilityOptions(false, false, false, false),
            officialBaseline);
        InstallationRecord officialRecord = InstallOwnership.ReadInstallationRecord(officialRoot);
        Assert(officialApplyCalls == 0 && officialResult.TransactionCommitted &&
            officialRecord.Provenance.CompatibilityFeatures.Count == 3,
            "全关闭设置没有直接登记完整官方状态，或仍执行了兼容变换。");

        string successRoot = Path.Combine(NewCaseRoot("staging-compatibility-success"), "staging");
        string successId = Guid.NewGuid().ToString("N");
        CreateHealthyCompatibilityInstallation(successRoot, successId, version);
        ArtifactProvenance successBaseline = InstallOwnership.ReadInstallationRecord(successRoot).Provenance;
        File.Delete(InstallOwnership.GetMarkerPath(successRoot));
        string successAsar = Path.Combine(successRoot, "app", "resources", "app.asar");
        CompatibilityOptions successOptions = CreateCompatibilityOptions(false, true, false, false);
        string[] successProtected = CompatibilityMaintenance.GetStagingProtectedArtifacts(
            PackageProfileReader.Read(successRoot),
            successOptions).ToArray();
        Assert(successProtected.Length == 2 && successProtected.Any(path => path.EndsWith(
                "app.asar",
                StringComparison.OrdinalIgnoreCase)),
            "仅启用 ASAR 功能时 staging 事务仍保护了无关 sandbox 制品。");
        CompatibilityMaintenance successMaintenance = new CompatibilityMaintenance(
            (executablePath, options) =>
            {
                File.AppendAllText(successAsar, "-staging-patched", Encoding.ASCII);
                return CreateStagingCompatibilityResult(true);
            },
            InstallOwnership.WriteMarker,
            delegate { });
        CompatibilityResult success = successMaintenance.ApplyTrustedStaging(
            successRoot,
            PackageProfileReader.Read(successRoot),
            successId,
            successOptions,
            successBaseline);
        InstallationRecord successRecord = InstallOwnership.ReadInstallationRecord(successRoot);
        Assert(success.TransactionCommitted &&
            successRecord.Provenance.AppliedFeatures.Contains("ModelCatalog") &&
            ArtifactHash.FixedTimeEquals(
                FindArtifact(successRecord.Provenance, "app/resources/app.asar").Sha256,
                ArtifactHash.ComputeSha256(successAsar)) &&
            !CompatibilityTransaction.Exists(successRoot),
            "成功的 staging 兼容变换没有提交最终摘要或清理事务。");

        string failureRoot = Path.Combine(NewCaseRoot("staging-compatibility-failure"), "staging");
        string failureId = Guid.NewGuid().ToString("N");
        CreateHealthyCompatibilityInstallation(failureRoot, failureId, version);
        ArtifactProvenance failureBaseline = InstallOwnership.ReadInstallationRecord(failureRoot).Provenance;
        File.Delete(InstallOwnership.GetMarkerPath(failureRoot));
        string failureAsar = Path.Combine(failureRoot, "app", "resources", "app.asar");
        byte[] originalAsar = File.ReadAllBytes(failureAsar);
        CompatibilityMaintenance failureMaintenance = new CompatibilityMaintenance(
            (executablePath, options) =>
            {
                File.AppendAllText(failureAsar, "-must-rollback", Encoding.ASCII);
                CompatibilityResult modifiedFailure = CreateStagingCompatibilityResult(false);
                modifiedFailure.Sandbox.Changed = true;
                modifiedFailure.Sandbox.After = "Enabled";
                return modifiedFailure;
            },
            InstallOwnership.WriteMarker,
            delegate { });
        CompatibilityResult failure = failureMaintenance.ApplyTrustedStaging(
            failureRoot,
            PackageProfileReader.Read(failureRoot),
            failureId,
            CreateCompatibilityOptions(false, true, false, false),
            failureBaseline);
        InstallationRecord failureRecord = InstallOwnership.ReadInstallationRecord(failureRoot);
        CompatibilityFeatureRecord failedSandbox = failureRecord.Provenance.CompatibilityFeatures.Single(feature =>
            feature.FeatureId == "SandboxCompatibility");
        Assert(!failure.TransactionCommitted && BytesEqual(File.ReadAllBytes(failureAsar), originalAsar) &&
            !failureRecord.Provenance.AppliedFeatures.Contains("ModelCatalog") &&
            failedSandbox.Status == CompatibilityFeatureStatus.RolledBack &&
            InstallationHealth.Evaluate(failureRoot).Status == InstallationHealthStatus.Healthy &&
            !CompatibilityTransaction.Exists(failureRoot),
            "已经修改文件的失败 staging 兼容变换没有恢复官方文件、记录回滚状态或清理事务。");
    }

    private static void TestTrustedStagingPartialCompatibilityApplication()
    {
        string root = Path.Combine(NewCaseRoot("staging-compatibility-partial"), "staging");
        string installId = Guid.NewGuid().ToString("N");
        CreateHealthyCompatibilityInstallation(root, installId, "1.2.3.4");
        ArtifactProvenance baseline = InstallOwnership.ReadInstallationRecord(root).Provenance;
        File.Delete(InstallOwnership.GetMarkerPath(root));
        string asar = Path.Combine(root, "app", "resources", "app.asar");
        CompatibilityMaintenance maintenance = new CompatibilityMaintenance(
            (executablePath, options) =>
            {
                File.AppendAllText(asar, "-localization-patched", Encoding.ASCII);
                return new CompatibilityResult
                {
                    ModelCatalogSucceeded = false,
                    SandboxSucceeded = false,
                    LocalizationSucceeded = true,
                    ModelCatalog = new CompatibilityFeatureResult
                    {
                        FeatureId = "ModelCatalog",
                        DisplayName = "模型目录",
                        Before = "Official",
                        Desired = "Patched",
                        After = "Official",
                        Changed = false,
                        Status = CompatibilityFeatureStatus.Unsupported,
                        Error = "新版本缺少模型目录锚点。",
                        RecipeId = ModelCatalogCompatibility.RecipeId
                    },
                    Sandbox = new CompatibilityFeatureResult
                    {
                        FeatureId = "SandboxCompatibility",
                        DisplayName = "Windows 沙箱兼容",
                        Before = "Disabled",
                        Desired = "Enabled",
                        After = "Disabled",
                        Changed = false,
                        Status = CompatibilityFeatureStatus.Failed,
                        Error = "模拟沙箱配置失败。",
                        RecipeId = CompatibilityCoordinator.SandboxRecipeId
                    },
                    Localization = new CompatibilityFeatureResult
                    {
                        FeatureId = "Localization",
                        DisplayName = "界面语言",
                        Before = "Official",
                        Desired = "Menus=Patched;Reasoning=Patched",
                        After = "Menus=Patched;Reasoning=Patched",
                        Changed = true,
                        Status = CompatibilityFeatureStatus.Applied,
                        RecipeId = CodexLocalizationCompatibility.RecipeId
                    }
                };
            },
            InstallOwnership.WriteMarker,
            delegate { });

        CompatibilityResult result = maintenance.ApplyTrustedStaging(
            root,
            PackageProfileReader.Read(root),
            installId,
            CreateCompatibilityOptions(true, true, true, false),
            baseline);
        InstallationRecord record = InstallOwnership.ReadInstallationRecord(root);
        Assert(result.TransactionCommitted && !result.AllSucceeded && result.HasPartialSuccess,
            "不支持功能未改写时没有提交独立成功的兼容设置。");
        Assert(File.ReadAllText(asar).Contains("-localization-patched") &&
            record.Provenance.AppliedFeatures.Contains("Localization") &&
            record.Provenance.CompatibilityFeatures.Single(feature => feature.FeatureId == "ModelCatalog").Status == CompatibilityFeatureStatus.Unsupported &&
            record.Provenance.CompatibilityFeatures.Single(feature => feature.FeatureId == "SandboxCompatibility").Status == CompatibilityFeatureStatus.Failed &&
            !CompatibilityTransaction.Exists(root),
            "部分兼容提交没有保留成功文件、记录模型不支持与沙箱失败状态或清理事务。");

        string failedRoot = Path.Combine(NewCaseRoot("staging-compatibility-failed-peer"), "staging");
        string failedInstallId = Guid.NewGuid().ToString("N");
        CreateHealthyCompatibilityInstallation(failedRoot, failedInstallId, "1.2.3.4");
        ArtifactProvenance failedBaseline = InstallOwnership.ReadInstallationRecord(failedRoot).Provenance;
        File.Delete(InstallOwnership.GetMarkerPath(failedRoot));
        string failedAsar = Path.Combine(failedRoot, "app", "resources", "app.asar");
        CompatibilityMaintenance failedMaintenance = new CompatibilityMaintenance(
            (executablePath, options) =>
            {
                File.AppendAllText(failedAsar, "-independent-localization", Encoding.ASCII);
                return new CompatibilityResult
                {
                    ModelCatalogSucceeded = false,
                    SandboxSucceeded = true,
                    LocalizationSucceeded = true,
                    ModelCatalog = new CompatibilityFeatureResult
                    {
                        FeatureId = "ModelCatalog",
                        DisplayName = "模型目录",
                        Before = "Official",
                        Desired = "Patched",
                        After = "Official",
                        Changed = false,
                        Status = CompatibilityFeatureStatus.Failed,
                        Error = "模拟模型分析失败。",
                        RecipeId = ModelCatalogCompatibility.RecipeId
                    },
                    Sandbox = CreateAlreadySatisfiedFeature(
                        "SandboxCompatibility", "Windows 沙箱兼容", "Disabled", CompatibilityCoordinator.SandboxRecipeId),
                    Localization = new CompatibilityFeatureResult
                    {
                        FeatureId = "Localization",
                        DisplayName = "界面语言",
                        Before = "Menus=Official;Reasoning=Official",
                        Desired = "Menus=Patched;Reasoning=Official",
                        After = "Menus=Patched;Reasoning=Official",
                        Changed = true,
                        Status = CompatibilityFeatureStatus.Applied,
                        RecipeId = CodexLocalizationCompatibility.RecipeId
                    }
                };
            },
            InstallOwnership.WriteMarker,
            delegate { });

        CompatibilityResult failedPeerResult = failedMaintenance.ApplyTrustedStaging(
            failedRoot,
            PackageProfileReader.Read(failedRoot),
            failedInstallId,
            CreateCompatibilityOptions(false, true, true, false),
            failedBaseline);
        InstallationRecord failedPeerRecord = InstallOwnership.ReadInstallationRecord(failedRoot);
        Assert(failedPeerResult.TransactionCommitted &&
            failedPeerResult.HasPartialSuccess &&
            File.ReadAllText(failedAsar).Contains("-independent-localization") &&
            failedPeerRecord.Provenance.CompatibilityFeatures.Single(feature =>
                feature.FeatureId == "ModelCatalog").Status == CompatibilityFeatureStatus.Failed,
            "模型失败且文件未变化时错误回滚了其他成功功能。");

        string invalidRoot = Path.Combine(NewCaseRoot("staging-compatibility-inconsistent"), "staging");
        string invalidInstallId = Guid.NewGuid().ToString("N");
        CreateHealthyCompatibilityInstallation(invalidRoot, invalidInstallId, "1.2.3.4");
        ArtifactProvenance invalidBaseline = InstallOwnership.ReadInstallationRecord(invalidRoot).Provenance;
        File.Delete(InstallOwnership.GetMarkerPath(invalidRoot));
        string invalidAsar = Path.Combine(invalidRoot, "app", "resources", "app.asar");
        byte[] invalidOriginalAsar = File.ReadAllBytes(invalidAsar);
        CompatibilityMaintenance invalidMaintenance = new CompatibilityMaintenance(
            (executablePath, options) =>
            {
                File.AppendAllText(invalidAsar, "-must-rollback", Encoding.ASCII);
                CompatibilityResult inconsistent = CreateStagingCompatibilityResult(true);
                inconsistent.ModelCatalogSucceeded = false;
                inconsistent.ModelCatalog = null;
                return inconsistent;
            },
            InstallOwnership.WriteMarker,
            delegate { });

        CompatibilityResult invalidResult = invalidMaintenance.ApplyTrustedStaging(
            invalidRoot,
            PackageProfileReader.Read(invalidRoot),
            invalidInstallId,
            CreateCompatibilityOptions(false, true, false, false),
            invalidBaseline);
        Assert(!invalidResult.TransactionCommitted &&
            BytesEqual(File.ReadAllBytes(invalidAsar), invalidOriginalAsar) &&
            !CompatibilityTransaction.Exists(invalidRoot),
            "缺失逐项结果的兼容协调器输出被错误地作为部分成功提交。");
    }

    private static CompatibilityResult CreateStagingCompatibilityResult(bool succeed)
    {
        return new CompatibilityResult
        {
            ModelCatalogSucceeded = true,
            SandboxSucceeded = succeed,
            LocalizationSucceeded = true,
            ModelCatalog = new CompatibilityFeatureResult
            {
                FeatureId = "ModelCatalog",
                DisplayName = "模型目录",
                Before = "Official",
                Desired = "Patched",
                After = "Patched",
                Changed = true,
                Status = CompatibilityFeatureStatus.Applied,
                RecipeId = ModelCatalogCompatibility.RecipeId
            },
            Sandbox = new CompatibilityFeatureResult
            {
                FeatureId = "SandboxCompatibility",
                DisplayName = "Windows 沙箱兼容",
                Before = "Disabled",
                Desired = "Disabled",
                After = "Disabled",
                Changed = false,
                Status = succeed
                    ? CompatibilityFeatureStatus.AlreadySatisfied
                    : CompatibilityFeatureStatus.Failed,
                Error = succeed ? null : "测试注入失败。",
                RecipeId = CompatibilityCoordinator.SandboxRecipeId
            },
            Localization = CreateAlreadySatisfiedFeature(
                "Localization",
                "界面语言",
                "Menus=Official;Reasoning=Official",
                CodexLocalizationCompatibility.RecipeId)
        };
    }

    private static void TestDeploymentCompletionPreservesWarnings()
    {
        DeploymentResult integrationFailure = new DeploymentResult(
            true,
            new List<string> { "协议 codex 注册失败：测试注入" }.AsReadOnly(),
            false);
        Assert(!integrationFailure.IntegrationSucceeded,
            "系统集成警告没有进入部署结果。");

        OperationProgress currentProgress = DeploymentCompletion.ForCurrentVersion(
            new Version(1, 0, 0, 0),
            new Version(1, 0, 0, 0),
            integrationFailure);
        string currentDetail = currentProgress.Detail;
        Assert(currentDetail.IndexOf("系统集成未能完整注册", StringComparison.Ordinal) >= 0,
            "当前版本同步完成信息没有呈现系统集成失败。");
        Assert(currentDetail.IndexOf("已同步完成", StringComparison.Ordinal) < 0,
            "当前版本同步在系统集成失败时仍声称已同步完成。");

        OperationProgress installedProgress = DeploymentCompletion.ForInstalledVersion(
            new Version(2, 0, 0, 0),
            integrationFailure);
        string installedMessage = installedProgress.Message;
        string installedDetail = installedProgress.Detail;
        Assert(installedMessage.IndexOf("系统集成未完成", StringComparison.Ordinal) >= 0,
            "新版本切换在系统集成失败时仍显示完整安装成功。");
        Assert(installedDetail.IndexOf("系统集成未能完整注册", StringComparison.Ordinal) >= 0,
            "新版本切换完成信息没有呈现系统集成失败。");

        DeploymentResult combinedWarnings = new DeploymentResult(
            true,
            new List<string> { "快捷方式创建失败：测试注入" }.AsReadOnly(),
            true);
        OperationProgress combinedProgress = DeploymentCompletion.ForInstalledVersion(
            new Version(3, 0, 0, 0),
            combinedWarnings);
        string combinedDetail = combinedProgress.Detail;
        Assert(combinedDetail.IndexOf("系统集成未能完整注册", StringComparison.Ordinal) >= 0,
            "旧备份待清理状态遮蔽了系统集成警告。");
        Assert(combinedDetail.IndexOf("旧回滚备份已隔离", StringComparison.Ordinal) >= 0 &&
            combinedDetail.IndexOf("独立后台进程", StringComparison.Ordinal) >= 0,
            "组合完成信息遗漏了旧备份待清理状态。");

        DeploymentResult compatibilityFailure = new DeploymentResult(
            false,
            new List<string>().AsReadOnly(),
            false,
            new CompatibilityResult { TransactionCommitted = false });
        OperationProgress compatibilityProgress = DeploymentCompletion.ForInstalledVersion(
            new Version(4, 0, 0, 0),
            compatibilityFailure);
        Assert(compatibilityProgress.Message.IndexOf("等待适配", StringComparison.Ordinal) >= 0 &&
            compatibilityProgress.Detail.IndexOf("已恢复官方程序文件", StringComparison.Ordinal) >= 0 &&
            compatibilityProgress.Detail.IndexOf("保留当前选择", StringComparison.Ordinal) >= 0,
            "更新完成状态没有呈现兼容设置失败后的官方回退和期望保留。");
        OperationProgress migrationCompatibilityProgress = MainWindow.CreateMigrationCompletion(
            compatibilityFailure);
        Assert(migrationCompatibilityProgress.Message.IndexOf("等待适配", StringComparison.Ordinal) >= 0 &&
            migrationCompatibilityProgress.Detail.IndexOf("保留当前选择", StringComparison.Ordinal) >= 0,
            "迁移完成状态遗漏了兼容设置等待适配警告。");

        CompatibilityResult partialCompatibility = new CompatibilityResult
        {
            TransactionCommitted = true,
            ModelCatalogSucceeded = false,
            SandboxSucceeded = true,
            LocalizationSucceeded = true
        };
        DeploymentResult partialMigrationResult = new DeploymentResult(
            false,
            new List<string>().AsReadOnly(),
            false,
            partialCompatibility);
        OperationProgress partialMigration = MainWindow.CreateMigrationCompletion(
            partialMigrationResult);
        Assert(partialMigration.Detail.IndexOf("部分兼容设置已应用", StringComparison.Ordinal) >= 0 &&
            partialMigration.Detail.IndexOf("不支持的功能保留官方文件", StringComparison.Ordinal) >= 0 &&
            partialMigration.Detail.IndexOf("已恢复官方程序文件", StringComparison.Ordinal) < 0,
            "迁移部分提交仍被错误描述成整体恢复官方文件。");

        OperationProgress migrationProgress = DeploymentCompletion.ForMigration(integrationFailure);
        string migrationDetail = migrationProgress.Detail;
        Assert(migrationDetail.IndexOf("系统集成已切换", StringComparison.Ordinal) < 0,
            "迁移完成信息在系统集成失败时仍声称已切换。");
    }

    private static void TestRollbackCompletionDisplaysRestoredVersion()
    {
        DeploymentResult result = new DeploymentResult(
            true,
            new List<string>().AsReadOnly(),
            false);
        OperationProgress progress = MainWindow.CreateRollbackCompletion(
            new Version(26, 707, 9564, 0),
            result);

        Assert(
            string.Equals(
                progress.Message,
                "已回滚到 Codex 26.707.9564.0",
                StringComparison.Ordinal),
            "回滚完成标题没有显示实际恢复版本。");
        string detail = progress.Detail;
        Assert(detail.IndexOf("版本 26.707.9564.0 已恢复", StringComparison.Ordinal) >= 0,
            "回滚完成详情没有确认恢复后的版本。");
        Assert(detail.IndexOf("可再次回滚切换", StringComparison.Ordinal) >= 0,
            "回滚完成详情没有说明 .previous 可再次切换。");
        Assert(progress.DisplayPercent == 100,
            "回滚完成状态没有显示 100% 进度。");
    }

    private static void TestCheckCompletionMatchesLocalState()
    {
        PortableStatus status = new PortableStatus
        {
            LatestPackage = new PackageMetadata { version = "26.707.9981.0" },
            StoreState = StorePackageState.NotInstalled
        };

        PortableLocalStatus noRoot = new PortableLocalStatus(null, null, false, null, false);
        OperationProgress noRootProgress = MainWindow.CreateCheckCompletion(noRoot, status);
        string noRootMessage = noRootProgress.Message;
        Assert(noRootMessage.IndexOf("尚未选择", StringComparison.Ordinal) >= 0 &&
            noRootMessage.IndexOf("尚未安装", StringComparison.Ordinal) < 0,
            "未选择目标目录仍被描述为尚未安装。");

        PortableLocalStatus invalidRoot = new PortableLocalStatus(
            null,
            null,
            false,
            "测试路径不可用",
            true);
        OperationProgress invalidProgress = MainWindow.CreateCheckCompletion(invalidRoot, status);
        Assert(invalidProgress.Message.IndexOf("目标目录无法读取", StringComparison.Ordinal) >= 0 &&
            invalidProgress.Detail.IndexOf("测试路径不可用", StringComparison.Ordinal) >= 0,
            "无效目标目录没有保留真实错误状态。");

        PortableLocalStatus installedRoot = new PortableLocalStatus(
            new Version(26, 707, 9564, 0),
            "26.707.71524",
            true,
            null,
            true);
        OperationProgress installedProgress = MainWindow.CreateCheckCompletion(installedRoot, status);
        string installedDetail = installedProgress.Detail;
        Assert(installedProgress.Message.IndexOf("发现 Codex 新版本", StringComparison.Ordinal) >= 0 &&
            installedDetail.IndexOf("回滚目标可用", StringComparison.Ordinal) >= 0,
            "已安装目录的版本和回滚状态没有进入检查完成文案。");

        PortableLocalStatus cachedRollbackRoot = new PortableLocalStatus(
            new Version(26, 707, 9564, 0),
            "26.707.71524",
            false,
            null,
            true,
            false,
            false,
            false,
            true);
        OperationProgress cachedRollbackProgress = MainWindow.CreateCheckCompletion(
            cachedRollbackRoot,
            status);
        Assert(cachedRollbackRoot.RollbackVersionAvailable &&
            !cachedRollbackRoot.PreviousVersionAvailable &&
            cachedRollbackProgress.Detail.IndexOf("回滚目标可用", StringComparison.Ordinal) >= 0,
            "缓存低版本没有独立形成可用回滚目标，或被误记为 .previous 备份。");

        PortableLocalStatus updateCleanupPending = new PortableLocalStatus(
            new Version(26, 707, 9564, 0),
            "26.707.71524",
            true,
            null,
            true,
            true,
            false);
        OperationProgress updateCleanupProgress = MainWindow.CreateCheckCompletion(
            updateCleanupPending,
            status);
        Assert(updateCleanupProgress.Message.IndexOf("目标目录无法读取", StringComparison.Ordinal) < 0 &&
            updateCleanupProgress.Detail.IndexOf("不影响当前版本启动", StringComparison.Ordinal) >= 0,
            "已提交更新的清理待办仍被显示成路径错误或没有说明可正常启动。");

        PortableLocalStatus uninstallCleanupPending = new PortableLocalStatus(
            null,
            null,
            false,
            null,
            true,
            false,
            true);
        OperationProgress uninstallCleanupProgress = MainWindow.CreateCheckCompletion(
            uninstallCleanupPending,
            status);
        Assert(uninstallCleanupProgress.Message.IndexOf("卸载清理待完成", StringComparison.Ordinal) >= 0,
            "卸载 tombstone 待清理没有独立显示。");
    }

    private static void TestMigrationCompletionDescribesActualOutcome()
    {
        DeploymentResult result = new DeploymentResult(
            true,
            new List<string>().AsReadOnly(),
            false);
        OperationProgress progress = MainWindow.CreateMigrationCompletion(result);
        string message = progress.Message;
        string detail = progress.Detail;
        Assert(message.IndexOf("已切换到 Codex 便携版", StringComparison.Ordinal) >= 0,
            "迁移完成标题没有说明部署类型已经切换。");
        Assert(detail.IndexOf("官方桌面版已卸载", StringComparison.Ordinal) >= 0 &&
            detail.IndexOf("已验证并发起启动", StringComparison.Ordinal) >= 0 &&
            detail.IndexOf("便携版已启动", StringComparison.Ordinal) < 0,
            "迁移完成详情夸大了便携版持续运行状态。");

        OperationProgress partial = MainWindow.CreateMigrationStoreUninstallFailure(
            result,
            new InvalidOperationException("Windows 部署服务拒绝卸载"),
            StorePackageState.Installed);
        Assert(string.Equals(
                partial.Message,
                "便携版已完成，官方版卸载失败",
                StringComparison.Ordinal) &&
            partial.Detail.IndexOf("复查确认官方桌面版仍然存在", StringComparison.Ordinal) >= 0 &&
            partial.Detail.IndexOf("Windows 部署服务拒绝卸载", StringComparison.Ordinal) >= 0 &&
            partial.Detail.IndexOf("不会因官方版卸载失败而回退", StringComparison.Ordinal) >= 0,
            "Store 卸载失败没有按迁移部分成功呈现真实结果。");
    }

    private static void TestInstallationProvenanceAndHealth()
    {
        string caseRoot = NewCaseRoot("installation-provenance-health");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string installId = Guid.NewGuid().ToString("N");
        string version = "1.2.3.4";
        CreateRunnableCodex(installRoot, version, installId, "provenance");

        InstallationHealthReport initialHealth = InstallationHealth.Evaluate(installRoot);
        Assert(initialHealth.Status == InstallationHealthStatus.Unverified,
            "缺少 provenance 的安装应明确标记为 Unverified。");

        PackageProfile profile = PackageProfileReader.Read(installRoot);
        string sourceDigest = Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        PackageMetadata package = CreatePackageMetadata(
            version,
            "OpenAI.Codex_1.2.3.4_x64__2p2nqsd0c76g0",
            sourceDigest,
            1234);
        CompatibilityOptions options = new CompatibilityOptions(false, false, false, false);
        CompatibilityResult compatibilityResult = new CompatibilityResult
        {
            ModelCatalogSucceeded = true,
            SandboxSucceeded = true,
            LocalizationSucceeded = true
        };

        ArtifactProvenance provenance = ArtifactProvenance.Capture(
            installRoot,
            profile,
            package,
            null,
            options,
            compatibilityResult);
        InstallOwnership.WriteMarker(installRoot, installId, version, provenance);

        InstallationHealthReport healthy = InstallationHealth.Evaluate(installRoot);
        Assert(healthy.Status == InstallationHealthStatus.Healthy,
            "带官方 digest 和派生摘要的安装未被识别为 Healthy。");
        string markerJson = File.ReadAllText(Path.Combine(installRoot, ".codex-portable-manager.json"), Encoding.UTF8);
        Dictionary<string, object> markerValues =
            new JavaScriptSerializer().DeserializeObject(markerJson) as Dictionary<string, object>;
        Assert(markerValues != null && markerValues.Count == 3 &&
            markerValues.ContainsKey("Identity") &&
            markerValues.ContainsKey("Provenance") &&
            markerValues.ContainsKey("UpdatedUtc"),
            "安装记录没有严格使用当前唯一结构。");
        Assert(markerJson.Contains(sourceDigest), "安装记录缺少官方 MSIX SHA-256。");
        Assert(markerJson.Contains("app/resources/app.asar"), "安装记录缺少 app.asar 派生摘要。");

        File.AppendAllText(Path.Combine(installRoot, "app", "resources", "app.asar"), "tampered", Encoding.ASCII);
        InstallationHealthReport tampered = InstallationHealth.Evaluate(installRoot);
        Assert(tampered.Status == InstallationHealthStatus.Tampered,
            "关键派生文件被篡改后没有变为 Tampered。");
        Assert(tampered.Errors.Any(value =>
            value.IndexOf("app.asar", StringComparison.OrdinalIgnoreCase) >= 0),
            "篡改健康报告没有指出 app.asar。");
    }

    private static void TestCompatibilityStatusOverview()
    {
        string caseRoot = NewCaseRoot("compatibility-status-overview");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string installId = Guid.NewGuid().ToString("N");
        CreateHealthyCompatibilityInstallation(installRoot, installId, "1.2.3.4");

        CompatibilityOverview inspected = CompatibilityStatusReader.Read(installRoot, false);
        Assert(inspected.State == CompatibilityOverviewState.Inspected &&
            inspected.Features.Count == 3 &&
            inspected.Features.All(feature => feature.RecipeCurrent),
            "启动时没有直接检查当前文件的逐功能状态。");

        CompatibilityOverview verified = CompatibilityStatusReader.Read(installRoot, true);
        Assert((verified.State == CompatibilityOverviewState.Verified ||
                verified.State == CompatibilityOverviewState.Inspected) &&
            InstallationHealth.Evaluate(installRoot).Status == InstallationHealthStatus.Healthy,
            "手动状态检查没有区分摘要健康与功能现场识别结果。");

        InstallationRecord record = InstallOwnership.ReadInstallationRecord(installRoot);
        CompatibilityFeatureRecord model = record.Provenance.CompatibilityFeatures.Single(feature =>
            feature.FeatureId == "ModelCatalog");
        model.After = "Patched";
        InstallOwnership.WriteMarker(installRoot, installId, "1.2.3.4", record.Provenance);
        CompatibilityOverview markerDisagrees = CompatibilityStatusReader.Read(installRoot, false);
        Assert(markerDisagrees.State == CompatibilityOverviewState.Inspected &&
            markerDisagrees.Features.Count == 3 &&
            markerDisagrees.Features.All(feature => feature.RecipeCurrent) &&
            !string.Equals(
                markerDisagrees.Features.Single(feature =>
                    feature.FeatureId == "ModelCatalog").After,
                "Patched",
                StringComparison.OrdinalIgnoreCase),
            "兼容状态错误信任 marker，而没有直接读取当前文件。");

        File.AppendAllText(
            Path.Combine(installRoot, "app", "resources", "app.asar"),
            "tampered",
            Encoding.ASCII);
        CompatibilityOverview lightweightAfterTamper = CompatibilityStatusReader.Read(installRoot, false);
        Assert(lightweightAfterTamper.State == CompatibilityOverviewState.Inspected,
            "过期配方没有继续主动读取实际状态，或轻量读取意外退化为完整摘要校验。");
        CompatibilityOverview invalid = CompatibilityStatusReader.Read(installRoot, true);
        Assert(invalid.State == CompatibilityOverviewState.Invalid,
            "手动状态检查没有检测关键文件篡改。");

        string unmanagedRoot = Path.Combine(caseRoot, "UnmanagedCodexDesktop");
        string unmanagedId = Guid.NewGuid().ToString("N");
        CreateRunnableCodex(unmanagedRoot, "1.2.3.4", unmanagedId, "compatibility-status-unmanaged");
        byte[] unmanagedEntry = Encoding.UTF8.GetBytes(
            "const settings={available_models:[]};" +
            "function filter({availableModels:n},r){return u?n.has(r.model):!r.hidden;}");
        File.WriteAllBytes(
            Path.Combine(unmanagedRoot, "app", "resources", "app.asar"),
            BuildTestAsar(
                "{\"files\":{" + BuildAsarEntryJson(
                    "webview/assets/model-list-filter-unmanaged.js",
                    unmanagedEntry,
                    0) + "}}",
                unmanagedEntry));
        CompatibilityOverview unmanaged = CompatibilityStatusReader.Read(unmanagedRoot, false);
        Assert(unmanaged.State == CompatibilityOverviewState.Inspected &&
            unmanaged.Features.Count == 3 &&
            unmanaged.Features.All(feature => feature.RecipeCurrent) &&
            unmanaged.Features.Single(feature => feature.FeatureId == "ModelCatalog").After == "Official",
            "缺少来源记录的安装没有主动读取逐功能状态。");
    }

    private static void TestUnknownCompatibilityStateFailsClosed()
    {
        string caseRoot = NewCaseRoot("compatibility-unknown-official");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string installId = Guid.NewGuid().ToString("N");
        CreateRunnableCodex(
            installRoot,
            "1.2.3.4",
            installId,
            "compatibility-unknown-official");

        byte[] payload = Encoding.UTF8.GetBytes(
            "const officialStructureChanged=true;renderFutureCodex();");
        File.WriteAllBytes(
            Path.Combine(installRoot, "app", "resources", "app.asar"),
            BuildTestAsar(
                "{\"files\":{" + BuildAsarEntryJson(
                    "webview/assets/app-main-future.js",
                    payload,
                    0) + "}}",
                payload));

        CompatibilityOverview overview = CompatibilityStatusReader.Read(installRoot, false);
        Assert(overview.State == CompatibilityOverviewState.Inspected &&
            overview.Features.Count == 3 &&
            overview.Features.All(feature => feature.RecipeCurrent),
            "未知官方结构没有返回完整的现场状态。");
        CompatibilityObservedFeature sandbox = overview.Features.Single(feature =>
            feature.FeatureId == "SandboxCompatibility");
        CompatibilityObservedFeature model = overview.Features.Single(feature =>
            feature.FeatureId == "ModelCatalog");
        Assert(sandbox.After == "Unknown" &&
            sandbox.Status == CompatibilityFeatureStatus.Unsupported &&
            model.After == "Official" &&
            model.Status == CompatibilityFeatureStatus.Unsupported &&
            overview.Features.Single(feature =>
                feature.FeatureId == "Localization").After ==
                "Menus=UnmanagedOrOfficial;Reasoning=UnmanagedOrOfficial",
            "未知官方结构没有把不可用的沙箱和模型功能关闭，或影响了其他独立功能的状态读取。");

        CompatibilitySwitchFacts unsupportedFacts =
            CompatibilityStatusReader.ResolveSwitchFacts(overview);
        Assert(!unsupportedFacts.SandboxCompatibilityEnabled.HasValue &&
            !unsupportedFacts.UnlockModelCatalogEnabled.HasValue,
            "当前版本不可用的功能仍被解析为可点击开关。");

        CompatibilityOptions resolved = CompatibilityStatusReader.ResolveOptions(overview);
        Assert(resolved == null,
            "未知沙箱主进程结构仍被推断为可安全应用的兼容设置。");
    }

    private static void TestCompatibilityDesiredAndActualStatesRemainSeparate()
    {
        CompatibilityOverview observed = new CompatibilityOverview(
            CompatibilityOverviewState.Inspected,
            "test",
            new[]
            {
                new CompatibilityObservedFeature(
                    "SandboxCompatibility", "Enabled", CompatibilityFeatureStatus.AlreadySatisfied,
                    null, "sandbox", true),
                new CompatibilityObservedFeature(
                    "ModelCatalog", "Patched", CompatibilityFeatureStatus.AlreadySatisfied,
                    null, "model", true),
                new CompatibilityObservedFeature(
                    "Localization", "Menus=Patched;Reasoning=Official",
                    CompatibilityFeatureStatus.AlreadySatisfied, null, "localization", true)
            },
            new[] { "SandboxCompatibility", "ModelCatalog", "Localization" },
            true);

        Assert(MainWindow.ResolveSimpleCompatibilityState(
            observed, "SandboxCompatibility", "Enabled", "Disabled") == true,
            "沙箱开关没有采用实际启用状态。");
        Assert(MainWindow.ResolveSimpleCompatibilityState(
            observed, "ModelCatalog", "Patched", "Official") == true,
            "模型开关没有采用实际补丁状态。");
        Assert(MainWindow.ResolveLocalizationCompatibilityState(observed, "Menus") == true,
            "中文菜单开关没有采用实际补丁状态。");
        Assert(MainWindow.ResolveLocalizationCompatibilityState(observed, "Reasoning") == false,
            "推理英文开关没有采用实际官方状态。");

        Assert(MainWindow.CanInitializeCompatibilitySwitch(observed, "ModelCatalog"),
            "健康且配方有效的识别结果不能用于首次初始化兼容开关。");
        Assert(MainWindow.CanApplyCompatibilityChanges(1, false, 1, 1),
            "一项已知更改被无关的不支持或未知状态阻断。");
        Assert(!MainWindow.CanApplyCompatibilityChanges(0, false, 1, 1),
            "没有任何可应用更改时仍错误启用应用按钮。");
        CompatibilityOptions initialized = MainWindow.ResolveCompatibilityOptionsForInitialization(observed);
        Assert(initialized != null &&
            initialized.SandboxCompatibilityEnabled &&
            initialized.UnlockModelCatalogEnabled &&
            initialized.SupplementChineseUiEnabled &&
            !initialized.EnglishTechnicalParametersEnabled,
            "首次识别没有把四项实际状态完整映射为兼容开关。");

        CompatibilityOverview partiallyFailed = new CompatibilityOverview(
            CompatibilityOverviewState.Inspected,
            "partially-failed",
            new[]
            {
                new CompatibilityObservedFeature(
                    "SandboxCompatibility", "Disabled", CompatibilityFeatureStatus.AlreadySatisfied,
                    null, CompatibilityCoordinator.SandboxRecipeId, true),
                new CompatibilityObservedFeature(
                    "ModelCatalog", "Patched", CompatibilityFeatureStatus.AlreadySatisfied,
                    null, ModelCatalogCompatibility.RecipeId, true),
                new CompatibilityObservedFeature(
                    "Localization", "Menus=Mixed;Reasoning=Official",
                    CompatibilityFeatureStatus.Failed, "本地化文件状态异常",
                    CodexLocalizationCompatibility.RecipeId, true)
            },
            new[] { "ModelCatalog" },
            true);
        CompatibilitySwitchFacts partialFacts =
            CompatibilityStatusReader.ResolveSwitchFacts(partiallyFailed);
        Assert(partialFacts.SandboxCompatibilityEnabled == false &&
            partialFacts.UnlockModelCatalogEnabled == true,
            "一个兼容功能异常时错误丢弃了其他功能的可确认事实状态。");
        Assert(!partialFacts.SupplementChineseUiEnabled.HasValue &&
            !partialFacts.EnglishTechnicalParametersEnabled.HasValue,
            "异常的界面语言功能仍被解析为可点击开关。");
        Assert(MainWindow.ResolveCompatibilityOptionsForInitialization(partiallyFailed) == null,
            "存在异常项时仍生成了可整体应用的完整兼容选项。");

        CompatibilityOverview failed = new CompatibilityOverview(
            CompatibilityOverviewState.Recorded,
            "failed",
            new[]
            {
                new CompatibilityObservedFeature(
                    "ModelCatalog",
                    "Official",
                    CompatibilityFeatureStatus.RolledBack,
                    "等待新版适配",
                    "model",
                    true)
            },
            new string[0],
            true);
        Assert(!MainWindow.CanInitializeCompatibilitySwitch(failed, "ModelCatalog"),
            "回滚后的实际关闭状态会覆盖用户保留的开启期望。");
        Assert(MainWindow.ResolveCompatibilityOptionsForInitialization(failed) == null,
            "功能状态不完整或已回滚时仍生成了自动初始化选项。");

        CompatibilityOverview unmanaged = new CompatibilityOverview(
            CompatibilityOverviewState.Recorded,
            "unmanaged",
            new[]
            {
                new CompatibilityObservedFeature(
                    "SandboxCompatibility", "Disabled", CompatibilityFeatureStatus.AlreadySatisfied,
                    null, CompatibilityCoordinator.SandboxRecipeId, true),
                new CompatibilityObservedFeature(
                    "ModelCatalog", "UnmanagedOrOfficial", CompatibilityFeatureStatus.AlreadySatisfied,
                    null, ModelCatalogCompatibility.RecipeId, true),
                new CompatibilityObservedFeature(
                    "Localization", "Menus=Official;Reasoning=NotManaged",
                    CompatibilityFeatureStatus.AlreadySatisfied, null,
                    CodexLocalizationCompatibility.RecipeId, true)
            },
            new string[0],
            true);
        Assert(MainWindow.ResolveSimpleCompatibilityState(
            unmanaged, "ModelCatalog", "Patched", "Official") == false,
            "未受本工具管理的模型状态被误报为无法读取。");
        CompatibilityOptions unmanagedOptions =
            MainWindow.ResolveCompatibilityOptionsForInitialization(unmanaged);
        Assert(unmanagedOptions != null &&
            !unmanagedOptions.UnlockModelCatalogEnabled,
            "完整的未管理状态记录不能初始化为关闭的兼容开关。");

        CompatibilityOverview invalid = new CompatibilityOverview(
            CompatibilityOverviewState.Invalid,
            "invalid",
            new CompatibilityObservedFeature[0],
            new string[0],
            true);
        Assert(!MainWindow.CanInitializeCompatibilitySwitch(invalid, "ModelCatalog"),
            "无法验证的状态仍允许初始化兼容开关。");

        CompatibilityOverview sandboxNotRequired = new CompatibilityOverview(
            CompatibilityOverviewState.Recorded,
            "not-required",
            new[]
            {
                new CompatibilityObservedFeature(
                    "SandboxCompatibility",
                    "Disabled",
                    CompatibilityFeatureStatus.NotRequired,
                    null,
                    "sandbox",
                    true)
            },
            new string[0],
            true);
        Assert(MainWindow.ResolveSimpleCompatibilityState(
            sandboxNotRequired, "SandboxCompatibility", "Enabled", "Disabled") == false,
            "沙箱开关没有按 helper 的实际关闭状态显示。");

        CompatibilityOverview recordOnly = new CompatibilityOverview(
            CompatibilityOverviewState.Recorded,
            "record-only",
            new CompatibilityObservedFeature[0],
            new[] { "ModelCatalog", "Localization" },
            true);
        Assert(MainWindow.ResolveSimpleCompatibilityState(
            recordOnly, "ModelCatalog", "Patched", "Official") == null,
            "缺少现场分析结果时仍使用 AppliedFeatures 猜测模型开关。");
        Assert(MainWindow.ResolveLocalizationCompatibilityState(recordOnly, "Menus") == null,
            "缺少现场分析结果时仍根据记录猜测中文菜单。");

        CompatibilityResult committedModel = new CompatibilityResult
        {
            TransactionCommitted = true,
            ModelCatalogSucceeded = true,
            SandboxSucceeded = true,
            LocalizationSucceeded = true,
            ModelCatalog = new CompatibilityFeatureResult
            {
                FeatureId = "ModelCatalog",
                DisplayName = "模型目录",
                Before = "Official",
                Desired = "Patched",
                After = "Patched",
                Changed = true,
                Status = CompatibilityFeatureStatus.Applied,
                RecipeId = ModelCatalogCompatibility.RecipeId
            }
        };
        CompatibilityOptions manageModelOnly = new CompatibilityOptions(
            true,
            true,
            true,
            false,
            false,
            true,
            false);
        CompatibilityOverview committedOverview =
            MainWindow.CreateCommittedCompatibilityOverview(
                observed,
                committedModel,
                manageModelOnly);
        Assert(committedOverview != null &&
            committedOverview.Features.Single(feature =>
                feature.FeatureId == "ModelCatalog").After == "Patched" &&
            committedOverview.Features.Single(feature =>
                feature.FeatureId == "SandboxCompatibility").After == "Enabled" &&
            committedOverview.Features.Single(feature =>
                feature.FeatureId == "Localization").After ==
                "Menus=Patched;Reasoning=Official",
            "已提交兼容结果没有只更新受管功能并保留其他现场事实。");
        committedModel.TransactionCommitted = false;
        Assert(MainWindow.CreateCommittedCompatibilityOverview(
            observed,
            committedModel,
            manageModelOnly) == null,
            "未提交事务仍绕过了完整现场状态读取。");

        MainWindow window = null;
        try
        {
            window = new MainWindow(false, false, new ManagerSettings
            {
                InstallRoot = Path.Combine(NewCaseRoot("partial-compatibility-window"), "Codex")
            })
            {
                ShowInTaskbar = false,
                ShowActivated = false
            };
            window.Show();

            FieldInfo portableField = typeof(MainWindow).GetField(
                "portableVersionAvailable",
                AnyInstance);
            FieldInfo statusField = typeof(MainWindow).GetField(
                "statusMatchesCurrentPath",
                AnyInstance);
            FieldInfo overviewField = typeof(MainWindow).GetField(
                "compatibilityOverview",
                AnyInstance);
            FieldInfo overviewRevisionField = typeof(MainWindow).GetField(
                "compatibilityOverviewPathRevision",
                AnyInstance);
            Assert(portableField != null && statusField != null && overviewField != null &&
                overviewRevisionField != null,
                "无法注入窗口兼容状态测试所需字段。");
            portableField.SetValue(window, true);
            statusField.SetValue(window, true);
            overviewField.SetValue(window, partiallyFailed);
            overviewRevisionField.SetValue(window, 0);

            MethodInfo captureSnapshot = typeof(MainWindow).GetMethod(
                "CaptureOperationSnapshot",
                AnyInstance);
            MethodInfo captureApplySnapshot = typeof(MainWindow).GetMethod(
                "CaptureCompatibilityApplySnapshot",
                AnyInstance);
            MethodInfo initializeSwitches = typeof(MainWindow).GetMethod(
                "InitializeCompatibilitySwitchesFromOverview",
                AnyInstance);
            MethodInfo applyUiState = typeof(MainWindow).GetMethod(
                "ApplyUiState",
                AnyInstance);
            Assert(captureSnapshot != null && captureApplySnapshot != null &&
                initializeSwitches != null && applyUiState != null,
                "窗口缺少兼容状态初始化或界面刷新方法。");
            OperationSnapshot snapshot = (OperationSnapshot)captureSnapshot.Invoke(window, null);
            initializeSwitches.Invoke(window, new object[] { snapshot });
            applyUiState.Invoke(window, null);

            System.Windows.Controls.CheckBox sandbox =
                (System.Windows.Controls.CheckBox)window.FindName("sandboxCompatibilityCheckBox");
            System.Windows.Controls.CheckBox model =
                (System.Windows.Controls.CheckBox)window.FindName("unlockModelCatalogCheckBox");
            System.Windows.Controls.CheckBox chinese =
                (System.Windows.Controls.CheckBox)window.FindName("supplementChineseUiCheckBox");
            System.Windows.Controls.CheckBox english =
                (System.Windows.Controls.CheckBox)window.FindName("englishTechnicalParametersCheckBox");
            Assert(sandbox.IsChecked == false && sandbox.IsEnabled,
                "可确认关闭的沙箱开关没有按事实显示或被错误禁用。");
            Assert(model.IsChecked == true && model.IsEnabled,
                "实际已开启的模型功能没有显示为开启或被错误禁用。");
            Assert(chinese.IsChecked == false && !chinese.IsEnabled &&
                english.IsChecked == false && !english.IsEnabled,
                "异常界面语言开关没有默认关闭并禁止点击。");

            sandbox.IsChecked = true;
            chinese.IsChecked = true;
            OperationSnapshot applySnapshot =
                (OperationSnapshot)captureApplySnapshot.Invoke(window, null);
            Assert(applySnapshot.Compatibility.ManageSandboxCompatibility &&
                !applySnapshot.Compatibility.ManageModelCatalog &&
                !applySnapshot.Compatibility.ManageLocalization,
                "只修改沙箱时仍把未改动模型或异常界面语言纳入应用事务。");

            sandbox.IsChecked = false;
            model.IsChecked = false;
            chinese.IsChecked = true;
            snapshot = (OperationSnapshot)captureSnapshot.Invoke(window, null);
            initializeSwitches.Invoke(window, new object[] { snapshot });
            applyUiState.Invoke(window, null);
            Assert(model.IsChecked == false && model.IsEnabled,
                "重新检查时没有保留可确认功能的会话草稿。");
            Assert(chinese.IsChecked == false && !chinese.IsEnabled,
                "重新检查发现异常后没有清除异常功能的开启草稿。");

            CompatibilityOverview unsupportedModel = new CompatibilityOverview(
                CompatibilityOverviewState.Inspected,
                "模型白名单入口不可用",
                new[]
                {
                    new CompatibilityObservedFeature(
                        "SandboxCompatibility", "Disabled",
                        CompatibilityFeatureStatus.AlreadySatisfied, null,
                        CompatibilityCoordinator.SandboxRecipeId, true),
                    new CompatibilityObservedFeature(
                        "ModelCatalog", "Official",
                        CompatibilityFeatureStatus.Unsupported,
                        "当前版本没有可安全修改的模型白名单入口。",
                        ModelCatalogCompatibility.RecipeId, true),
                    new CompatibilityObservedFeature(
                        "Localization", "Menus=Official;Reasoning=Official",
                        CompatibilityFeatureStatus.AlreadySatisfied, null,
                        CodexLocalizationCompatibility.RecipeId, true)
                },
                new string[0],
                true);
            overviewField.SetValue(window, unsupportedModel);
            snapshot = (OperationSnapshot)captureSnapshot.Invoke(window, null);
            initializeSwitches.Invoke(window, new object[] { snapshot });
            applyUiState.Invoke(window, null);
            System.Windows.Controls.TextBlock modelStatus =
                (System.Windows.Controls.TextBlock)window.FindName("modelCatalogStatusLabel");
            Assert(model.IsChecked == false && !model.IsEnabled &&
                string.Equals(modelStatus.Text, "不可用，已关闭", StringComparison.Ordinal) &&
                (modelStatus.ToolTip as string ?? string.Empty).IndexOf(
                    "模型白名单入口",
                    StringComparison.Ordinal) >= 0,
                "当前版本不可用的模型功能没有默认关闭、禁止点击或显示简要原因。");
        }
        finally
        {
            if (window != null) window.Close();
        }
    }

    private static void TestMainWindowStartsWithoutCompatibilityState()
    {
        MainWindow window = null;
        try
        {
            window = new MainWindow(false, false, new ManagerSettings
            {
                InstallRoot = Path.Combine(NewCaseRoot("window-waits-for-artifact-state"), "Codex")
            });
            Assert(
                ((System.Windows.Controls.CheckBox)window.FindName("sandboxCompatibilityCheckBox")).IsChecked == false &&
                ((System.Windows.Controls.CheckBox)window.FindName("unlockModelCatalogCheckBox")).IsChecked == false &&
                ((System.Windows.Controls.CheckBox)window.FindName("supplementChineseUiCheckBox")).IsChecked == false &&
                ((System.Windows.Controls.CheckBox)window.FindName("englishTechnicalParametersCheckBox")).IsChecked == false,
                "窗口启动时仍显示上次保存的兼容选择，而不是等待读取当前文件。");
        }
        finally
        {
            if (window != null) window.Close();
        }
    }

    private static void TestCompatibilityOptionDescriptionsAreClear()
    {
        MainWindow window = null;
        try
        {
            window = new MainWindow(false, false, new ManagerSettings());
            string[] descriptionNames =
            {
                "sandboxCompatibilityDescriptionLabel",
                "modelCatalogDescriptionLabel",
                "chineseUiDescriptionLabel",
                "englishParametersDescriptionLabel"
            };
            string[] checkBoxNames =
            {
                "sandboxCompatibilityCheckBox",
                "unlockModelCatalogCheckBox",
                "supplementChineseUiCheckBox",
                "englishTechnicalParametersCheckBox"
            };
            string[][] requiredPhrases =
            {
                new[] { "检测 Windows 对当前用户名的 SID 解析", "补全“账户域\\用户名”", "官方沙箱初始化程序使用当前登录用户" },
                new[] { "本地白名单过滤", "DeepSeek", "外部模型显示在模型列表中" },
                new[] { "文件、编辑、视图、帮助", "托盘右键菜单", "打开 Codex", "退出" },
                new[] { "推理强度名称固定显示为官方英文" }
            };

            for (int index = 0; index < descriptionNames.Length; index++)
            {
                System.Windows.Controls.TextBlock description =
                    (System.Windows.Controls.TextBlock)window.FindName(descriptionNames[index]);
                System.Windows.Controls.CheckBox checkBox =
                    (System.Windows.Controls.CheckBox)window.FindName(checkBoxNames[index]);
                Assert(description != null && checkBox != null,
                    "兼容选项缺少可见说明或对应开关：" + descriptionNames[index]);
                foreach (string phrase in requiredPhrases[index])
                {
                    Assert(description.Text.IndexOf(phrase, StringComparison.Ordinal) >= 0,
                        "兼容选项说明没有讲清实际作用：" + phrase);
                }
                Assert(description.Text.IndexOf("不会", StringComparison.Ordinal) < 0 &&
                    description.Text.IndexOf("不改变", StringComparison.Ordinal) < 0,
                    "兼容选项说明仍包含未执行事项，而不是只描述实际作用：" +
                    descriptionNames[index]);
                Assert(string.Equals(
                    System.Windows.Automation.AutomationProperties.GetHelpText(checkBox),
                    description.Text,
                    StringComparison.Ordinal),
                    "兼容选项的辅助功能说明与可见说明不一致：" + checkBoxNames[index]);
            }
        }
        finally
        {
            if (window != null) window.Close();
        }
    }

    private static void TestCompatibilityApplyMaskSkipsUnmanagedFeatures()
    {
        CompatibilityOptions sandboxOnly = new CompatibilityOptions(
            true,
            true,
            false,
            true,
            true,
            false,
            false);
        List<string> planLogs = new List<string>();
        CompatibilityPlanResult plan = new CompatibilityPlan(planLogs.Add).Apply(
            "Z:\\missing\\Codex.exe",
            sandboxOnly);
        Assert(plan.ModelCatalogSucceeded && plan.LocalizationSucceeded &&
            plan.ModelCatalogChange == null && plan.LocalizationChange == null &&
            !plan.SandboxSucceeded && planLogs.Count > 0,
            "沙箱单项分析失败时错误读取或影响了未纳入事务的功能。");

        PackageProfile profile = new PackageProfile
        {
            ExecutableRelativePath = "app/Codex.exe"
        };
        string[] protectedArtifacts = CompatibilityMaintenance
            .GetProtectedArtifacts(profile, sandboxOnly)
            .ToArray();
        Assert(protectedArtifacts.Length == 2 &&
            protectedArtifacts.Contains(InstallOwnership.MarkerFileName) &&
            protectedArtifacts.Any(path => path.EndsWith(
                "app.asar",
                StringComparison.OrdinalIgnoreCase)) &&
            protectedArtifacts.All(path => path.IndexOf(
                "codex-windows-sandbox-setup",
                StringComparison.OrdinalIgnoreCase) < 0),
            "沙箱单项维护没有只保护 marker 与 app.asar。");

        ArtifactProvenance previous = new ArtifactProvenance
        {
            AppliedFeatures = new List<string> { "ModelCatalog", "Localization" },
            IncompleteFeatures = new List<string>(),
            CompatibilityFeatures = new List<CompatibilityFeatureRecord>
            {
                new CompatibilityFeatureRecord
                {
                    FeatureId = "ModelCatalog",
                    Before = "Patched",
                    Desired = "Patched",
                    After = "Patched",
                    Status = CompatibilityFeatureStatus.AlreadySatisfied,
                    RecipeId = ModelCatalogCompatibility.RecipeId
                },
                new CompatibilityFeatureRecord
                {
                    FeatureId = "Localization",
                    Before = "Menus=Patched;Reasoning=Patched",
                    Desired = "Menus=Patched;Reasoning=Patched",
                    After = "Menus=Patched;Reasoning=Patched",
                    Status = CompatibilityFeatureStatus.AlreadySatisfied,
                    RecipeId = CodexLocalizationCompatibility.RecipeId
                },
                new CompatibilityFeatureRecord
                {
                    FeatureId = "SandboxCompatibility",
                    Before = "Disabled",
                    Desired = "Disabled",
                    After = "Disabled",
                    Status = CompatibilityFeatureStatus.AlreadySatisfied,
                    RecipeId = CompatibilityCoordinator.SandboxRecipeId
                }
            },
            Artifacts = new List<ArtifactDigest>()
        };
        CompatibilityResult result = new CompatibilityResult
        {
            ModelCatalogSucceeded = true,
            LocalizationSucceeded = true,
            SandboxSucceeded = true,
            ModelCatalog = new CompatibilityFeatureResult
            {
                FeatureId = "ModelCatalog",
                DisplayName = "模型目录",
                Before = "NotManaged",
                Desired = "NotManaged",
                After = "NotManaged",
                Status = CompatibilityFeatureStatus.AlreadySatisfied,
                RecipeId = ModelCatalogCompatibility.RecipeId
            },
            Localization = new CompatibilityFeatureResult
            {
                FeatureId = "Localization",
                DisplayName = "界面语言",
                Before = "Menus=NotManaged;Reasoning=NotManaged",
                Desired = "Menus=NotManaged;Reasoning=NotManaged",
                After = "Menus=NotManaged;Reasoning=NotManaged",
                Status = CompatibilityFeatureStatus.AlreadySatisfied,
                RecipeId = CodexLocalizationCompatibility.RecipeId
            },
            Sandbox = new CompatibilityFeatureResult
            {
                FeatureId = "SandboxCompatibility",
                DisplayName = "Windows 沙箱兼容",
                Before = "Disabled",
                Desired = "Enabled",
                After = "Enabled",
                Changed = true,
                Status = CompatibilityFeatureStatus.Applied,
                RecipeId = CompatibilityCoordinator.SandboxRecipeId
            }
        };
        ArtifactProvenance updated = ArtifactProvenance.UpdateCompatibilityArtifacts(
            NewCaseRoot("partial-compatibility-provenance"),
            previous,
            sandboxOnly,
            result,
            new CompatibilityArtifactState[0]);
        Assert(updated.AppliedFeatures.Contains("ModelCatalog") &&
            updated.AppliedFeatures.Contains("Localization") &&
            updated.AppliedFeatures.Contains("SandboxCompatibility"),
            "沙箱单项应用覆盖了未纳入事务的模型或本地化 provenance。");
        Assert(updated.CompatibilityFeatures.Single(feature =>
                feature.FeatureId == "ModelCatalog").After == "Patched" &&
            updated.CompatibilityFeatures.Single(feature =>
                feature.FeatureId == "Localization").After ==
                "Menus=Patched;Reasoning=Patched" &&
            updated.CompatibilityFeatures.Single(feature =>
                feature.FeatureId == "SandboxCompatibility").After == "Enabled",
            "单项应用没有保留跳过功能的事实记录或更新目标功能记录。");
    }

    private static void TestMainWindowCompatibilityDraftIsSessionOnly()
    {
        string configPath = Path.Combine(PortableStorage.UserDataRoot, "config.json");
        byte[] previousConfig = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
        MainWindow window = null;
        MainWindow reopened = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
            File.WriteAllText(configPath, "{\"InstallRoot\":null}", new UTF8Encoding(false));
            byte[] originalConfig = File.ReadAllBytes(configPath);

            window = new MainWindow(false, false, PortableStorage.LoadSettings())
            {
                ShowInTaskbar = false,
                ShowActivated = false
            };
            window.Show();
            System.Windows.Controls.CheckBox sandbox =
                (System.Windows.Controls.CheckBox)window.FindName("sandboxCompatibilityCheckBox");
            System.Windows.Controls.CheckBox model =
                (System.Windows.Controls.CheckBox)window.FindName("unlockModelCatalogCheckBox");
            sandbox.IsChecked = true;
            model.IsChecked = true;

            FieldInfo dirtyField = typeof(MainWindow).GetField(
                "compatibilityDraftDirty",
                AnyInstance);
            Assert(dirtyField != null && (bool)dirtyField.GetValue(window),
                "兼容开关变化没有登记为会话草稿。");
            Assert(BytesEqual(File.ReadAllBytes(configPath), originalConfig),
                "未点击应用型命令时兼容草稿改写了持久配置。");

            window.Close();
            window = null;
            reopened = new MainWindow(false, false, PortableStorage.LoadSettings());
            Assert(
                ((System.Windows.Controls.CheckBox)reopened.FindName(
                    "sandboxCompatibilityCheckBox")).IsChecked == false &&
                ((System.Windows.Controls.CheckBox)reopened.FindName(
                    "unlockModelCatalogCheckBox")).IsChecked == false,
                "重新打开窗口仍显示上次未应用草稿或持久兼容选择。");
        }
        finally
        {
            if (window != null) window.Close();
            if (reopened != null) reopened.Close();
            RestoreOptionalFile(configPath, previousConfig);
        }
    }

    private static void TestStatusSummaryPresentation()
    {
        MainWindow.StatusSummaryPresentation unselected = MainWindow.ResolveStatusSummary(
            new PortableLocalStatus(null, null, false, null, false),
            true,
            null);
        Assert(unselected.Text == "检测到官方桌面版" &&
            unselected.BrushKey == "WarningActionBrush",
            "未选择目录时顶部状态没有保留官方桌面版提示。");

        MainWindow.StatusSummaryPresentation invalid = MainWindow.ResolveStatusSummary(
            new PortableLocalStatus(null, null, false, "路径损坏", true),
            false,
            null);
        Assert(invalid.Text == "路径无效" && invalid.BrushKey == "DangerBrush",
            "无效目录没有映射到危险状态。");

        MainWindow.StatusSummaryPresentation current = MainWindow.ResolveStatusSummary(
            new PortableLocalStatus(new Version(2, 0), null, false, null, true),
            false,
            new Version(2, 0));
        Assert(current.Text == "已是最新版本" && current.BrushKey == "SuccessBrush",
            "最新便携版没有映射到成功状态。");

        MainWindow.StatusSummaryPresentation cleanup = MainWindow.ResolveStatusSummary(
            new PortableLocalStatus(null, null, false, null, true, false, true, false),
            false,
            null);
        Assert(cleanup.Text == "卸载清理待完成" && cleanup.BrushKey == "WarningBrush",
            "卸载清理待办没有映射到警告状态。");
    }

    private static void TestCompactLayoutPreservesPrimaryWorkspace()
    {
        MainWindow window = null;
        try
        {
            window = new MainWindow(
                false,
                false,
                new ManagerSettings { InstallRoot = @"C:\CodexPortableManagerRender\Codex" })
            {
                Width = 760,
                Height = 620,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            window.Show();
            ((System.Windows.Controls.Button)window.FindName("pauseButton")).Visibility =
                System.Windows.Visibility.Visible;
            ((System.Windows.Controls.Button)window.FindName("cancelButton")).Visibility =
                System.Windows.Visibility.Visible;
            window.UpdateLayout();

            System.Windows.Controls.Border summary =
                (System.Windows.Controls.Border)window.FindName("statusSummaryCard");
            System.Windows.Controls.ScrollViewer workspace =
                (System.Windows.Controls.ScrollViewer)window.FindName("mainScrollViewer");
            System.Windows.Controls.Border activityPane =
                (System.Windows.Controls.Border)window.FindName("activityPane");
            System.Windows.Controls.TextBox logBox =
                (System.Windows.Controls.TextBox)window.FindName("logBox");
            Assert(summary.Visibility == System.Windows.Visibility.Collapsed,
                "紧凑高度仍保留重复顶部摘要，压缩了主操作区。");
            Assert(workspace.ActualHeight >= 180,
                "紧凑窗口主操作区高度不足。实际：" + workspace.ActualHeight);
            Assert(logBox.TextWrapping == System.Windows.TextWrapping.NoWrap &&
                logBox.HorizontalScrollBarVisibility ==
                    System.Windows.Controls.ScrollBarVisibility.Auto,
                "运行日志仍会随窗口宽度变化重排整段文本。");
            MethodInfo appendLog = typeof(MainWindow).GetMethod("AppendLog", AnyInstance);
            Assert(appendLog != null, "无法定位运行日志批量刷新入口。");
            appendLog.Invoke(window, new object[] { "批量日志测试一" });
            appendLog.Invoke(window, new object[] { "批量日志测试二" });
            appendLog.Invoke(window, new object[] { "批量日志测试三" });
            window.Dispatcher.Invoke(
                delegate { },
                System.Windows.Threading.DispatcherPriority.ContextIdle);
            Assert(logBox.Text.IndexOf("批量日志测试一", StringComparison.Ordinal) >= 0 &&
                logBox.Text.IndexOf("批量日志测试二", StringComparison.Ordinal) >= 0 &&
                logBox.Text.IndexOf("批量日志测试三", StringComparison.Ordinal) >= 0,
                "连续日志没有通过合并调度完整刷新到界面。");

            bool observedSynchronousWideLayout = false;
            bool observedSynchronousNarrowLayout = false;
            window.SizeChanged += delegate
            {
                if (window.ActualWidth >= 1090)
                {
                    observedSynchronousWideLayout =
                        System.Windows.Controls.Grid.GetColumn(activityPane) == 2;
                }
                else if (window.ActualWidth <= 1030)
                {
                    observedSynchronousNarrowLayout =
                        System.Windows.Controls.Grid.GetRow(activityPane) == 1;
                }
            };

            window.Width = 1070;
            window.UpdateLayout();
            Assert(System.Windows.Controls.Grid.GetRow(activityPane) == 1,
                "窄布局在断点滞回区内过早切换到宽布局。");
            window.Width = 1100;
            window.UpdateLayout();
            Assert(System.Windows.Controls.Grid.GetColumn(activityPane) == 2,
                "越过宽布局滞回区后没有在当前布局周期切换到侧栏布局。");
            window.Width = 1050;
            window.UpdateLayout();
            Assert(System.Windows.Controls.Grid.GetColumn(activityPane) == 2,
                "宽布局在断点滞回区内过早退回窄布局。");
            window.Width = 1020;
            window.UpdateLayout();
            Assert(System.Windows.Controls.Grid.GetRow(activityPane) == 1,
                "低于宽布局滞回区后没有在当前布局周期退回底部任务布局。");
            Assert(observedSynchronousWideLayout && observedSynchronousNarrowLayout,
                "响应式布局没有在 SizeChanged 的同一事件周期完成。");
            System.Windows.Interop.HwndSource source =
                System.Windows.PresentationSource.FromVisual(window) as
                    System.Windows.Interop.HwndSource;
            System.Windows.Media.SolidColorBrush expectedBackground =
                window.FindResource("WindowBackgroundBrush") as
                    System.Windows.Media.SolidColorBrush;
            Assert(source != null && source.CompositionTarget != null &&
                expectedBackground != null &&
                source.CompositionTarget.BackgroundColor == expectedBackground.Color,
                "窗口合成目标没有使用界面背景色，恢复时可能暴露黑色清屏帧。");
        }
        finally
        {
            if (window != null) window.Close();
        }
    }

    private static void TestMainWindowDesignTokensAndAccessibility()
    {
        string projectRoot = FindProjectRoot();
        string appXaml = File.ReadAllText(
            Path.Combine(projectRoot, "src", "App.xaml"),
            Encoding.UTF8);
        string windowXaml = File.ReadAllText(
            Path.Combine(projectRoot, "src", "MainWindow.xaml"),
            Encoding.UTF8);
        string windowCode = File.ReadAllText(
            Path.Combine(projectRoot, "src", "MainWindow.xaml.cs"),
            Encoding.UTF8);
        string statusCode = File.ReadAllText(
            Path.Combine(projectRoot, "src", "MainWindow.Operations.Status.cs"),
            Encoding.UTF8);
        string interactionCode = File.ReadAllText(
            Path.Combine(projectRoot, "src", "MainWindow.Operations.Interaction.cs"),
            Encoding.UTF8);
        string aboutCode = File.ReadAllText(
            Path.Combine(projectRoot, "src", "MainWindow.About.cs"),
            Encoding.UTF8);
        string readme = File.ReadAllText(
            Path.Combine(projectRoot, "README.md"),
            Encoding.UTF8);
        string assemblyInfo = File.ReadAllText(
            Path.Combine(projectRoot, "src", "AssemblyInfo.cs"),
            Encoding.UTF8);
        string appManifest = File.ReadAllText(
            Path.Combine(projectRoot, "app.manifest"),
            Encoding.UTF8);
        string artifactPipeline = File.ReadAllText(
            Path.Combine(projectRoot, "src", "ArtifactPipeline.cs"),
            Encoding.UTF8);
        string packageResolver = File.ReadAllText(
            Path.Combine(projectRoot, "src", "PackageResolver.cs"),
            Encoding.UTF8);
        string tokensXaml = File.ReadAllText(
            Path.Combine(projectRoot, "src", "DesignTokens.xaml"),
            Encoding.UTF8);

        Assert(appXaml.IndexOf("DesignTokens.xaml", StringComparison.Ordinal) >= 0 &&
            windowXaml.IndexOf("DesignTokens.xaml", StringComparison.Ordinal) >= 0,
            "应用或独立窗口没有加载共享设计令牌。");
        string[] requiredTokens =
        {
            "SuccessBrush",
            "WarningBrush",
            "DangerBrush",
            "FontSizeBodyCompact",
            "ControlHeightCompact",
            "CardCornerRadius",
            "WorkspaceInset"
        };
        Assert(requiredTokens.All(token => tokensXaml.IndexOf(
            "x:Key=\"" + token + "\"",
            StringComparison.Ordinal) >= 0),
            "共享设计令牌缺少颜色、字号、尺寸、圆角或间距定义。");
        Assert(!Regex.IsMatch(windowXaml, "#[0-9A-Fa-f]{6}") &&
            windowXaml.IndexOf("Height=\"32\"", StringComparison.Ordinal) < 0,
            "主窗口仍包含裸颜色或过小的 32px 工具按钮。");
        Assert(windowXaml.IndexOf("DropShadowEffect", StringComparison.Ordinal) < 0,
            "主窗口仍包含会在滚动或缩放时触发离屏重绘的阴影。");
        Assert(windowXaml.IndexOf("TextWrapping=\"NoWrap\"", StringComparison.Ordinal) >= 0 &&
            windowXaml.IndexOf(
                "HorizontalScrollBarVisibility=\"Auto\"",
                StringComparison.Ordinal) >= 0,
            "运行日志没有保持不换行和横向滚动配置。");
        Assert(windowCode.IndexOf(
                "SizeChanged += (sender, args) => UpdateResponsiveLayout()",
                StringComparison.Ordinal) >= 0 &&
            windowCode.IndexOf("QueueResponsiveLayoutUpdate", StringComparison.Ordinal) < 0 &&
            windowCode.IndexOf("WideLayoutHysteresis", StringComparison.Ordinal) >= 0 &&
            windowCode.IndexOf("DwmSetWindowAttribute", StringComparison.Ordinal) >= 0 &&
            interactionCode.IndexOf("pendingUiLog", StringComparison.Ordinal) >= 0 &&
            interactionCode.IndexOf("FlushPendingUiLog", StringComparison.Ordinal) >= 0,
            "窗口同步布局、原生边框或运行日志刷新保护不完整。");
        Assert(windowXaml.IndexOf("Text=\"QQ 交流群\"", StringComparison.Ordinal) >= 0 &&
            windowXaml.IndexOf("Text=\"535990598\"", StringComparison.Ordinal) >= 0 &&
            windowXaml.IndexOf("copyQqGroupButton", StringComparison.Ordinal) >= 0 &&
            aboutCode.IndexOf("QqGroupNumber = \"535990598\"", StringComparison.Ordinal) >= 0 &&
            readme.IndexOf("QQ 群 `535990598`", StringComparison.Ordinal) >= 0 &&
            windowXaml.IndexOf("1105711986", StringComparison.Ordinal) < 0 &&
            aboutCode.IndexOf("1105711986", StringComparison.Ordinal) < 0 &&
            readme.IndexOf("1105711986", StringComparison.Ordinal) < 0,
            "关于页与 README 的 QQ 群号没有保持一致。");
        Assert(windowXaml.IndexOf("managerVersionLabel", StringComparison.Ordinal) >= 0 &&
            windowXaml.IndexOf("复制版本", StringComparison.Ordinal) < 0 &&
            aboutCode.IndexOf("version.Major", StringComparison.Ordinal) >= 0 &&
            assemblyInfo.IndexOf("AssemblyVersion(\"1.1.0.0\")", StringComparison.Ordinal) >= 0 &&
            assemblyInfo.IndexOf("AssemblyFileVersion(\"1.1.0.0\")", StringComparison.Ordinal) >= 0 &&
            appManifest.IndexOf("version=\"1.1.0.0\"", StringComparison.Ordinal) >= 0 &&
            artifactPipeline.IndexOf("CodexPortableManager/1.1.0", StringComparison.Ordinal) >= 0 &&
            packageResolver.IndexOf("CodexPortableManager/1.1.0", StringComparison.Ordinal) >= 0,
            "1.1.0 版本来源或关于页版本显示没有保持一致。");
        Assert(windowCode.IndexOf(
                "EnsureCompatibilityOverviewLoadedAsync",
                StringComparison.Ordinal) >= 0 &&
            statusCode.IndexOf(
                "EnsureCompatibilityOverviewLoadedAsync",
                StringComparison.Ordinal) >= 0 &&
            statusCode.IndexOf(
                "await LoadCompatibilityOverviewAsync",
                StringComparison.Ordinal) < 0,
            "启动或普通路径刷新仍会无条件执行高内存兼容语义分析。");
        Assert(windowXaml.IndexOf(
                "Target=\"{Binding ElementName=installPathTextBox}\"",
                StringComparison.Ordinal) >= 0 &&
            windowXaml.IndexOf(
                "AutomationProperties.Name=\"当前任务进度\"",
                StringComparison.Ordinal) >= 0 &&
            windowXaml.IndexOf(
                "AutomationProperties.Name=\"运行日志\"",
                StringComparison.Ordinal) >= 0,
            "路径、进度或日志区域缺少辅助功能语义。");
    }

    private static void TestCompatibilityMaintenanceHealthGate()
    {
        string caseRoot = NewCaseRoot("compatibility-health-gate");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string installId = Guid.NewGuid().ToString("N");
        CreateRunnableCodex(installRoot, "1.2.3.4", installId, "compatibility-health-gate");
        byte[] baselineEntry = Encoding.UTF8.GetBytes("const baseline=true;");
        File.WriteAllBytes(
            Path.Combine(installRoot, "app", "resources", "app.asar"),
            BuildTestAsar(
                "{\"files\":{" + BuildAsarEntryJson("baseline.js", baselineEntry, 0) + "}}",
                baselineEntry));
        CompatibilityOptions options = CreateCompatibilityOptions(false, false, false, false);
        int applyCalls = 0;
        CompatibilityMaintenance maintenance = new CompatibilityMaintenance(
            (executablePath, desired) =>
            {
                applyCalls++;
                return CreateSuccessfulCompatibilityResult();
            },
            InstallOwnership.WriteMarker,
            delegate { });

        Exception unapproved = CaptureFailure(delegate
        {
            maintenance.Apply(installRoot, options, null);
        });
        Assert(unapproved is InvalidOperationException,
            "未验证安装未经明确批准仍进入兼容维护。实际异常：" +
            (unapproved == null ? "无" : unapproved.GetType().FullName));
        Assert(applyCalls == 0, "未验证安装被拒绝前已经调用兼容协调器。");

        string invalidRoot = Path.Combine(caseRoot, "InvalidCodexDesktop");
        CreateRunnableCodex(
            invalidRoot,
            "1.2.3.4",
            Guid.NewGuid().ToString("N"),
            "invalid-compatibility-baseline");
        Exception invalidBaseline = CaptureFailure(delegate
        {
            maintenance.Apply(
                invalidRoot,
                options,
                CompatibilityBaselineApproval.Create(invalidRoot));
        });
        Assert(invalidBaseline is InvalidDataException,
            "显式建基线前没有完整验证 ASAR 结构与 integrity。实际异常：" +
            (invalidBaseline == null ? "无" : invalidBaseline.GetType().FullName));
        Assert(applyCalls == 0, "无效 ASAR 被拒绝前已经调用兼容协调器。");

        CompatibilityResult approved = maintenance.Apply(
            installRoot,
            options,
            CompatibilityBaselineApproval.Create(installRoot));
        Assert(approved.TransactionCommitted && applyCalls == 1,
            "明确批准后没有建立本地基线并提交兼容维护。");
        InstallationRecord baselined = InstallOwnership.ReadInstallationRecord(installRoot);
        Assert(baselined.Provenance != null && baselined.Provenance.Artifacts.Count > 0,
            "明确批准后 marker 没有保存本地制品摘要基线。");

        File.AppendAllText(
            Path.Combine(installRoot, "app", "resources", "codex.exe"),
            "tampered",
            Encoding.ASCII);
        Exception tampered = CaptureFailure(delegate
        {
            maintenance.Apply(
                installRoot,
                options,
                CompatibilityBaselineApproval.Create(installRoot));
        });
        Assert(tampered is InvalidDataException,
            "Tampered 安装即使带基线批准也没有被拒绝。实际异常：" +
            (tampered == null ? "无" : tampered.GetType().FullName));
        Assert(applyCalls == 1, "Tampered 安装被拒绝前已经调用兼容协调器。");
    }

    private static void TestCompatibilityPreflightRejectsUnownedRootBeforeStoppingProcesses()
    {
        string installRoot = Path.Combine(
            NewCaseRoot("compatibility-preflight-unowned"),
            "NotCodex");
        Directory.CreateDirectory(installRoot);
        string sentinelPath = Path.Combine(installRoot, "keep.txt");
        File.WriteAllText(sentinelPath, "不可修改", new UTF8Encoding(false));
        int stopCalls = 0;
        int waitCalls = 0;
        using (CodexPortableService service = new CodexPortableService(
            delegate { },
            root => stopCalls++,
            (root, timeout) => waitCalls++))
        {
            Exception failure = CaptureFailure(delegate
            {
                service.ApplyCompatibilitySettings(
                    installRoot,
                    CreateCompatibilityOptions(false, false, false, false));
            });
            Assert(failure is InvalidDataException,
                "非 Codex 目录没有在进程停止前被健康预检拒绝。实际异常：" +
                (failure == null ? "无" : failure.GetType().FullName));
        }

        Assert(stopCalls == 0 && waitCalls == 0,
            "非 Codex 目录被拒绝前已经调用进程停止或等待逻辑。");
        Assert(File.Exists(sentinelPath) &&
            string.Equals(File.ReadAllText(sentinelPath), "不可修改", StringComparison.Ordinal),
            "非 Codex 目录被拒绝时文件发生了变化。");
    }

    private static void TestCompatibilityPreflightRejectsReplacementAfterProcessStop()
    {
        string caseRoot = NewCaseRoot("compatibility-preflight-replacement");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string displacedRoot = Path.Combine(caseRoot, "CodexDesktop-original");
        string originalInstallId = Guid.NewGuid().ToString("N");
        string replacementInstallId = Guid.NewGuid().ToString("N");
        CreateHealthyCompatibilityInstallation(installRoot, originalInstallId, "1.2.3.4");
        int stopCalls = 0;
        int waitCalls = 0;

        using (CodexPortableService service = new CodexPortableService(
            delegate { },
            root =>
            {
                stopCalls++;
                Directory.Move(root, displacedRoot);
                CreateHealthyCompatibilityInstallation(root, replacementInstallId, "1.2.3.4");
            },
            (root, timeout) => waitCalls++))
        {
            Exception failure = CaptureFailure(delegate
            {
                service.ApplyCompatibilitySettings(
                    installRoot,
                    CreateCompatibilityOptions(false, false, false, false));
            });
            Assert(failure is InvalidDataException && failure.ToString().IndexOf(
                    "预检后已被替换",
                    StringComparison.Ordinal) >= 0,
                "预检到执行之间替换为另一套健康安装后没有被身份复验拒绝。实际异常：" +
                (failure == null ? "无" : failure.ToString()));
        }

        InstallationRecord replacement = InstallOwnership.ReadInstallationRecord(installRoot);
        Assert(stopCalls == 1 && waitCalls == 1,
            "目录替换竞态夹具没有经过一次停止和等待阶段。");
        Assert(string.Equals(
                replacement.Identity.InstallId,
                replacementInstallId,
                StringComparison.OrdinalIgnoreCase) &&
            InstallationHealth.Evaluate(installRoot).Status == InstallationHealthStatus.Healthy,
            "被拒绝后替换安装被修改，或其健康状态不再完整。");
        Assert(Directory.Exists(displacedRoot) &&
            string.Equals(
                InstallOwnership.ReadInstallationRecord(displacedRoot).Identity.InstallId,
                originalInstallId,
                StringComparison.OrdinalIgnoreCase),
            "被移开的原安装没有保持完整。");
    }

    private static void TestCompatibilityMarkerFailureRollsBack()
    {
        AssertCompatibilityStorageFailureRollsBack(
            "compatibility-marker-failure",
            new UnauthorizedAccessException("marker 写入失败（测试注入）"));
        AssertCompatibilityPostCaptureChangeRollsBack();
    }

    private static void TestCompatibilityDiskFullRollsBack()
    {
        AssertCompatibilityStorageFailureRollsBack(
            "compatibility-disk-full",
            new IOException("磁盘空间不足（测试注入）"));
    }

    private static void TestCompatibilityInterruptedRecovery()
    {
        string caseRoot = NewCaseRoot("compatibility-interrupted-recovery");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string installId = Guid.NewGuid().ToString("N");
        CreateHealthyCompatibilityInstallation(installRoot, installId, "1.2.3.4");
        string asarPath = Path.Combine(installRoot, "app", "resources", "app.asar");
        string markerPath = InstallOwnership.GetMarkerPath(installRoot);
        byte[] originalAsar = File.ReadAllBytes(asarPath);
        byte[] originalMarker = File.ReadAllBytes(markerPath);

        CompatibilityTransaction transaction = CompatibilityTransaction.Begin(
            installRoot,
            installId,
            CreateCompatibilityOptions(false, true, false, false),
            new[] { "app/resources/app.asar", InstallOwnership.MarkerFileName });
        transaction.BeginMutation();
        File.AppendAllText(asarPath, "interrupted-change", Encoding.ASCII);
        File.WriteAllText(markerPath, "interrupted-marker", new UTF8Encoding(false));
        transaction.CaptureChanges();

        List<string> logs = new List<string>();
        using (DeploymentEngineScope scope = new DeploymentEngineScope(logs))
        {
            scope.Engine.RecoverPendingCompatibilityMaintenance(installRoot);
        }
        Assert(!CompatibilityTransaction.Exists(installRoot),
            "部署维护入口没有先恢复兼容 journal。");
        Assert(BytesEqual(File.ReadAllBytes(asarPath), originalAsar),
            "中断恢复后 app.asar 未恢复到原始字节。");
        Assert(BytesEqual(File.ReadAllBytes(markerPath), originalMarker),
            "中断恢复后 marker 未恢复到原始字节。");
        Assert(!File.Exists(CompatibilityTransaction.GetJournalPath(installRoot)),
            "中断恢复成功后 journal 未清理。");
        Assert(!Directory.EnumerateDirectories(
            caseRoot,
            "CodexDesktop.compatibility-backup-*",
            SearchOption.TopDirectoryOnly).Any(),
            "中断恢复成功后备份目录未清理。");
        Assert(logs.Any(value => value.IndexOf("恢复", StringComparison.Ordinal) >= 0),
            "中断恢复没有记录明确日志。");
    }

    private static void TestCompatibilityRecoveryRejectsUnknownArtifactWithDamagedMarker()
    {
        foreach (bool removeMarker in new[] { true, false })
        {
            string markerState = removeMarker ? "missing" : "damaged";
            string caseRoot = NewCaseRoot("compatibility-unknown-state-" + markerState);
            string installRoot = Path.Combine(caseRoot, "CodexDesktop");
            string installId = Guid.NewGuid().ToString("N");
            CreateHealthyCompatibilityInstallation(installRoot, installId, "1.2.3.4");
            string asarPath = Path.Combine(installRoot, "app", "resources", "app.asar");
            string markerPath = InstallOwnership.GetMarkerPath(installRoot);
            string journalPath = CompatibilityTransaction.GetJournalPath(installRoot);

            CompatibilityTransaction transaction = CompatibilityTransaction.Begin(
                installRoot,
                installId,
                CreateCompatibilityOptions(false, true, false, false),
                new[] { "app/resources/app.asar", InstallOwnership.MarkerFileName });
            transaction.BeginMutation();
            File.AppendAllText(asarPath, "captured-change", Encoding.ASCII);
            transaction.CaptureChanges();

            if (removeMarker) NativeFileSystem.DeleteFile(markerPath);
            else File.WriteAllText(markerPath, "damaged-marker", new UTF8Encoding(false));
            File.AppendAllText(asarPath, "foreign-change", Encoding.ASCII);
            byte[] foreignAsar = File.ReadAllBytes(asarPath);
            string backupRoot = Directory.EnumerateDirectories(
                caseRoot,
                "CodexDesktop.compatibility-backup-*",
                SearchOption.TopDirectoryOnly).Single();

            Exception failure = CaptureFailure(delegate
            {
                CompatibilityTransaction.RecoverPending(installRoot, delegate { });
            });
            Assert(failure is InvalidDataException && failure.ToString().IndexOf(
                    "原始态和已捕获目标态之外",
                    StringComparison.Ordinal) >= 0,
                "marker " + markerState + " 时兼容恢复接受了陌生制品摘要。实际异常：" +
                (failure == null ? "无" : failure.ToString()));
            Assert(BytesEqual(File.ReadAllBytes(asarPath), foreignAsar),
                "拒绝陌生制品状态时恢复流程改写了当前 app.asar。");
            Assert(removeMarker
                    ? !File.Exists(markerPath)
                    : File.ReadAllText(markerPath, Encoding.UTF8) == "damaged-marker",
                "拒绝陌生制品状态时恢复流程改写了异常 marker。");
            Assert(File.Exists(journalPath) && Directory.Exists(backupRoot) &&
                Directory.EnumerateFiles(backupRoot, "*.bak", SearchOption.TopDirectoryOnly).Any(),
                "拒绝陌生制品状态时 journal 或可信备份没有保留。");
        }
    }

    private static void TestCompatibilityRecoveryAllowsDamagedMarkerAtFilesChanged()
    {
        foreach (bool removeMarker in new[] { true, false })
        {
            string markerState = removeMarker ? "missing" : "damaged";
            string caseRoot = NewCaseRoot("compatibility-known-state-" + markerState);
            string installRoot = Path.Combine(caseRoot, "CodexDesktop");
            string installId = Guid.NewGuid().ToString("N");
            CreateHealthyCompatibilityInstallation(installRoot, installId, "1.2.3.4");
            string asarPath = Path.Combine(installRoot, "app", "resources", "app.asar");
            string markerPath = InstallOwnership.GetMarkerPath(installRoot);
            byte[] originalAsar = File.ReadAllBytes(asarPath);
            byte[] originalMarker = File.ReadAllBytes(markerPath);

            CompatibilityTransaction transaction = CompatibilityTransaction.Begin(
                installRoot,
                installId,
                CreateCompatibilityOptions(false, true, false, false),
                new[] { "app/resources/app.asar", InstallOwnership.MarkerFileName });
            transaction.BeginMutation();
            File.AppendAllText(asarPath, "captured-change", Encoding.ASCII);
            transaction.CaptureChanges();
            if (removeMarker) NativeFileSystem.DeleteFile(markerPath);
            else File.WriteAllText(markerPath, "damaged-marker", new UTF8Encoding(false));

            bool recovered = CompatibilityTransaction.RecoverPending(
                installRoot,
                delegate { });
            Assert(recovered,
                "FilesChanged 阶段 marker " + markerState + " 时没有执行降级恢复。");
            Assert(BytesEqual(File.ReadAllBytes(asarPath), originalAsar) &&
                BytesEqual(File.ReadAllBytes(markerPath), originalMarker),
                "FilesChanged 阶段 marker " + markerState + " 后没有恢复原始制品。");
            Assert(!CompatibilityTransaction.Exists(installRoot) &&
                !Directory.EnumerateDirectories(
                    caseRoot,
                    "CodexDesktop.compatibility-backup-*",
                    SearchOption.TopDirectoryOnly).Any(),
                "FilesChanged 阶段降级恢复成功后 journal 或备份未清理。");
        }
    }

    private static void TestCompatibilityRecoveryRejectsDamagedMarkerBeforeFilesChanged()
    {
        foreach (CompatibilityTransactionPhase phase in new[]
        {
            CompatibilityTransactionPhase.Prepared,
            CompatibilityTransactionPhase.Mutating
        })
        {
            foreach (bool removeMarker in new[] { true, false })
            {
                string markerState = removeMarker ? "missing" : "damaged";
                string caseRoot = NewCaseRoot(
                    "compat-early-" +
                    (phase == CompatibilityTransactionPhase.Prepared ? "p" : "m") + "-" +
                    (removeMarker ? "x" : "d"));
                string installRoot = Path.Combine(caseRoot, "CodexDesktop");
                string installId = Guid.NewGuid().ToString("N");
                CreateHealthyCompatibilityInstallation(installRoot, installId, "1.2.3.4");
                string asarPath = Path.Combine(installRoot, "app", "resources", "app.asar");
                string markerPath = InstallOwnership.GetMarkerPath(installRoot);
                string journalPath = CompatibilityTransaction.GetJournalPath(installRoot);
                byte[] originalAsar = File.ReadAllBytes(asarPath);

                CompatibilityTransaction transaction = CompatibilityTransaction.Begin(
                    installRoot,
                    installId,
                    CreateCompatibilityOptions(false, true, false, false),
                    new[] { "app/resources/app.asar", InstallOwnership.MarkerFileName });
                if (phase == CompatibilityTransactionPhase.Mutating)
                {
                    transaction.BeginMutation();
                }
                if (removeMarker) NativeFileSystem.DeleteFile(markerPath);
                else File.WriteAllText(markerPath, "damaged-marker", new UTF8Encoding(false));
                string backupRoot = Directory.EnumerateDirectories(
                    caseRoot,
                    "CodexDesktop.compatibility-backup-*",
                    SearchOption.TopDirectoryOnly).Single();

                Exception failure = CaptureFailure(delegate
                {
                    CompatibilityTransaction.RecoverPending(installRoot, delegate { });
                });
                Assert(failure is InvalidDataException && failure.ToString().IndexOf(
                        "事务阶段不允许降级恢复",
                        StringComparison.Ordinal) >= 0,
                    phase + " 阶段 marker " + markerState +
                    " 时仍执行了降级恢复。实际异常：" +
                    (failure == null ? "无" : failure.ToString()));
                Assert(BytesEqual(File.ReadAllBytes(asarPath), originalAsar),
                    "拒绝较早阶段降级恢复时 app.asar 被改写。");
                Assert(removeMarker
                        ? !File.Exists(markerPath)
                        : File.ReadAllText(markerPath, Encoding.UTF8) == "damaged-marker",
                    "拒绝较早阶段降级恢复时异常 marker 被改写。");
                Assert(File.Exists(journalPath) && Directory.Exists(backupRoot),
                    "拒绝较早阶段降级恢复时 journal 或备份没有保留。");
            }
        }
    }

    private static void TestCompatibilityRecoveryRejectsReplacementInstall()
    {
        string caseRoot = NewCaseRoot("compatibility-replacement-install");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string originalInstallId = Guid.NewGuid().ToString("N");
        CreateHealthyCompatibilityInstallation(
            installRoot,
            originalInstallId,
            "1.2.3.4");
        string asarPath = Path.Combine(installRoot, "app", "resources", "app.asar");

        CompatibilityTransaction transaction = CompatibilityTransaction.Begin(
            installRoot,
            originalInstallId,
            CreateCompatibilityOptions(false, true, false, false),
            new[] { "app/resources/app.asar", InstallOwnership.MarkerFileName });
        transaction.BeginMutation();
        File.AppendAllText(asarPath, "interrupted-old-install", Encoding.ASCII);
        transaction.CaptureChanges();

        string displacedRoot = Path.Combine(caseRoot, "displaced-original");
        Directory.Move(installRoot, displacedRoot);
        string replacementInstallId = Guid.NewGuid().ToString("N");
        CreateHealthyCompatibilityInstallation(
            installRoot,
            replacementInstallId,
            "9.8.7.6");
        byte[] replacementAsar = File.ReadAllBytes(asarPath);
        byte[] replacementMarker = File.ReadAllBytes(
            InstallOwnership.GetMarkerPath(installRoot));

        Exception failure = CaptureFailure(delegate
        {
            CompatibilityTransaction.RecoverPending(installRoot, delegate { });
        });
        Assert(failure is InvalidDataException,
            "兼容维护恢复接受了同路径的新安装。实际异常：" +
            (failure == null ? "无" : failure.ToString()));
        Assert(BytesEqual(File.ReadAllBytes(asarPath), replacementAsar) &&
            BytesEqual(
                File.ReadAllBytes(InstallOwnership.GetMarkerPath(installRoot)),
                replacementMarker),
            "旧兼容事务覆盖了同路径新安装的 ASAR 或 marker。");
        Assert(File.Exists(CompatibilityTransaction.GetJournalPath(installRoot)),
            "安装根身份不匹配时兼容 journal 被提前删除。");
    }

    private static void TestCompatibilityRecoveryRequiresBackupIdentity()
    {
        string caseRoot = NewCaseRoot("compatibility-backup-identity");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string installId = Guid.NewGuid().ToString("N");
        CreateHealthyCompatibilityInstallation(installRoot, installId, "1.2.3.4");
        string journalPath = CompatibilityTransaction.GetJournalPath(installRoot);
        CompatibilityTransaction.Begin(
            installRoot,
            installId,
            CreateCompatibilityOptions(false, true, false, false),
            new[] { "app/resources/app.asar", InstallOwnership.MarkerFileName });

        JavaScriptSerializer serializer = new JavaScriptSerializer();
        IDictionary<string, object> journal = serializer.DeserializeObject(
            File.ReadAllText(journalPath, Encoding.UTF8)) as IDictionary<string, object>;
        Assert(journal != null, "测试无法读取兼容维护 journal。");
        string operationId = journal["OperationId"] as string;
        string backupRoot = installRoot + ".compatibility-backup-" + operationId;
        string sentinel = Path.Combine(backupRoot, "保留现场.txt");
        File.WriteAllText(sentinel, "缺少身份时不得删除", Encoding.UTF8);
        journal["Phase"] = (int)CompatibilityTransactionPhase.Preparing;
        journal["BackupDirectoryIdentity"] = null;
        File.WriteAllText(
            journalPath,
            serializer.Serialize(journal),
            new UTF8Encoding(false));

        Exception failure = CaptureFailure(delegate
        {
            CompatibilityTransaction.RecoverPending(installRoot, delegate { });
        });
        Assert(failure is InvalidDataException,
            "缺少备份目录身份的兼容事务仍执行了递归清理。实际异常：" +
            (failure == null ? "无" : failure.ToString()));
        Assert(File.Exists(journalPath) && File.Exists(sentinel),
            "缺少备份目录身份时 journal 或待诊断备份被删除。");
    }

    private static void TestCompatibilityJournalRejectsNestedJunction()
    {
        string caseRoot = NewCaseRoot("compatibility-nested-junction");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string appRoot = Path.Combine(installRoot, "app");
        string outsideRoot = Path.Combine(caseRoot, "outside-resources");
        string resourcesAlias = Path.Combine(appRoot, "resources");
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(outsideRoot);
        string sentinel = Path.Combine(outsideRoot, "victim.txt");
        File.WriteAllText(sentinel, "安装根外文件必须保留", Encoding.UTF8);
        CreateJunction(resourcesAlias, outsideRoot);

        Exception failure = CaptureFailure(delegate
        {
            CompatibilityTransaction.Begin(
                installRoot,
                Guid.NewGuid().ToString("N"),
                CreateCompatibilityOptions(false, true, false, false),
                new[] { "app/resources/victim.txt" });
        });

        Assert(failure is InvalidDataException,
            "兼容维护没有拒绝安装树内部 junction。实际异常：" +
            (failure == null ? "无" : failure.ToString()));
        Assert(File.Exists(sentinel) &&
            File.ReadAllText(sentinel, Encoding.UTF8) == "安装根外文件必须保留",
            "兼容维护越过 junction 修改或删除了安装根外文件。");
        Assert(!File.Exists(CompatibilityTransaction.GetJournalPath(installRoot)),
            "兼容维护拒绝 junction 后没有清理未进入修改阶段的 journal。");
    }

    private static void TestCompatibilityJournalRejectsMissingOrCoercedFields()
    {
        AssertCompatibilityJournalShapeRejected(
            "missing",
            null,
            delegate(IDictionary<string, object> artifact)
            {
                artifact.Remove("OriginalExists");
            });
        AssertCompatibilityJournalShapeRejected(
            "coerced",
            null,
            delegate(IDictionary<string, object> artifact)
            {
                artifact["OriginalExists"] = "true";
            });
        AssertCompatibilityJournalShapeRejected(
            "root-id",
            delegate(IDictionary<string, object> root)
            {
                root.Remove("InstallRootIdentity");
            },
            null);
        AssertCompatibilityJournalShapeRejected(
            "marker-required",
            delegate(IDictionary<string, object> root)
            {
                root.Remove("InstallMarkerRequired");
            },
            null);
        AssertCompatibilityJournalShapeRejected(
            "backup-id",
            delegate(IDictionary<string, object> root)
            {
                root.Remove("BackupDirectoryIdentity");
            },
            null);
    }

    private static void AssertCompatibilityJournalShapeRejected(
        string caseName,
        Action<IDictionary<string, object>> mutateRoot,
        Action<IDictionary<string, object>> mutateArtifact)
    {
        string caseRoot = NewCaseRoot("cj-shape-" + caseName);
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string installId = Guid.NewGuid().ToString("N");
        CreateHealthyCompatibilityInstallation(installRoot, installId, "1.2.3.4");
        string asarPath = Path.Combine(installRoot, "app", "resources", "app.asar");
        string journalPath = CompatibilityTransaction.GetJournalPath(installRoot);

        CompatibilityTransaction transaction = CompatibilityTransaction.Begin(
            installRoot,
            installId,
            CreateCompatibilityOptions(false, true, false, false),
            new[] { "app/resources/app.asar" });
        transaction.BeginMutation();
        File.AppendAllText(asarPath, "interrupted-change", Encoding.ASCII);
        transaction.CaptureChanges();
        byte[] changedAsar = File.ReadAllBytes(asarPath);

        JavaScriptSerializer serializer = new JavaScriptSerializer();
        IDictionary<string, object> root =
            serializer.DeserializeObject(File.ReadAllText(journalPath, Encoding.UTF8))
                as IDictionary<string, object>;
        Assert(root != null, "测试无法读取兼容维护 journal 根对象。");
        object[] artifacts = root["Artifacts"] as object[];
        Assert(artifacts != null && artifacts.Length == 1,
            "测试兼容维护 journal 没有预期的单个制品记录。");
        IDictionary<string, object> artifact =
            artifacts[0] as IDictionary<string, object>;
        Assert(artifact != null, "测试兼容维护 journal 制品记录格式异常。");
        if (mutateRoot != null) mutateRoot(root);
        if (mutateArtifact != null) mutateArtifact(artifact);
        File.WriteAllText(
            journalPath,
            serializer.Serialize(root),
            new UTF8Encoding(false));

        Exception failure = CaptureFailure(delegate
        {
            CompatibilityTransaction.RecoverPending(installRoot, delegate { });
        });
        Assert(failure is InvalidDataException,
            "兼容维护 journal 接受了缺失或被强制转换的关键字段。实际异常：" +
            (failure == null ? "无" : failure.ToString()));
        Assert(File.Exists(journalPath),
            "兼容维护 journal 格式无效时恢复流程删除了待诊断记录。");
        Assert(BytesEqual(File.ReadAllBytes(asarPath), changedAsar),
            "兼容维护 journal 格式无效时恢复流程改动了现场文件。");
    }

    private static void TestCompatibilityProvenanceUpdatesOnlyChangedArtifacts()
    {
        string caseRoot = NewCaseRoot("compatibility-minimal-provenance");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string installId = Guid.NewGuid().ToString("N");
        CreateHealthyCompatibilityInstallation(installRoot, installId, "1.2.3.4");
        InstallationRecord before = InstallOwnership.ReadInstallationRecord(installRoot);
        ArtifactDigest beforeAsar = FindArtifact(before.Provenance, "app/resources/app.asar");
        ArtifactDigest beforeCodex = FindArtifact(before.Provenance, "app/resources/codex.exe");
        string asarPath = Path.Combine(installRoot, "app", "resources", "app.asar");
        string codexPath = Path.Combine(installRoot, "app", "resources", "codex.exe");

        CompatibilityMaintenance maintenance = new CompatibilityMaintenance(
            (executablePath, desired) =>
            {
                File.AppendAllText(asarPath, "managed-change", Encoding.ASCII);
                File.AppendAllText(codexPath, "unrelated-change", Encoding.ASCII);
                return CreateSuccessfulCompatibilityResult();
            },
            InstallOwnership.WriteMarker,
            delegate { });
        CompatibilityResult result = maintenance.Apply(
            installRoot,
            CreateCompatibilityOptions(false, false, false, false),
            null);
        Assert(result.TransactionCommitted, "最小 provenance 更新事务没有提交。");

        InstallationRecord after = InstallOwnership.ReadInstallationRecord(installRoot);
        ArtifactDigest afterAsar = FindArtifact(after.Provenance, "app/resources/app.asar");
        ArtifactDigest afterCodex = FindArtifact(after.Provenance, "app/resources/codex.exe");
        Assert(!ArtifactHash.FixedTimeEquals(beforeAsar.Sha256, afterAsar.Sha256) &&
            ArtifactHash.FixedTimeEquals(afterAsar.Sha256, ArtifactHash.ComputeSha256(asarPath)),
            "实际修改的 app.asar 摘要没有更新。");
        Assert(ArtifactHash.FixedTimeEquals(beforeCodex.Sha256, afterCodex.Sha256),
            "与兼容维护无关的 codex.exe 被重新登记为新基线。");
        Assert(!ArtifactHash.FixedTimeEquals(afterCodex.Sha256, ArtifactHash.ComputeSha256(codexPath)),
            "测试注入的无关 codex.exe 变化没有保留为可检测篡改。");
        Assert(after.Provenance.CompatibilityFeatures != null &&
            after.Provenance.CompatibilityFeatures.Count == 3 &&
            after.Provenance.CompatibilityFeatures.Any(feature =>
                feature.FeatureId == "ModelCatalog" &&
                feature.RecipeId == ModelCatalogCompatibility.RecipeId),
            "已提交 marker 没有持久化丰富兼容结果和 RecipeId。");
        Assert(InstallationHealth.Evaluate(installRoot).Status == InstallationHealthStatus.Tampered,
            "无关制品在维护期间变化后未保持 Tampered 可检测状态。");
    }

    private static void AssertCompatibilityStorageFailureRollsBack(string caseName, Exception injectedFailure)
    {
        string caseRoot = NewCaseRoot(caseName);
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string installId = Guid.NewGuid().ToString("N");
        CreateHealthyCompatibilityInstallation(installRoot, installId, "1.2.3.4");
        string asarPath = Path.Combine(installRoot, "app", "resources", "app.asar");
        string helperPath = Path.Combine(
            installRoot,
            "app",
            "resources",
            "codex-windows-sandbox-setup.exe");
        string markerPath = InstallOwnership.GetMarkerPath(installRoot);
        byte[] originalAsar = File.ReadAllBytes(asarPath);
        byte[] originalMarker = File.ReadAllBytes(markerPath);
        byte[] officialHelper = Encoding.ASCII.GetBytes("SIGNED_OFFICIAL_HELPER_FIXTURE");
        File.WriteAllBytes(helperPath, officialHelper);

        CompatibilityMaintenance maintenance = new CompatibilityMaintenance(
            (executablePath, desired) =>
            {
                File.AppendAllText(asarPath, "compatibility-change", Encoding.ASCII);
                return CreateSuccessfulCompatibilityResult();
            },
            (root, id, version, provenance) =>
            {
                File.WriteAllText(markerPath, "partial-marker", new UTF8Encoding(false));
                throw injectedFailure;
            },
            delegate { });

        Exception failure = CaptureFailure(delegate
        {
            maintenance.Apply(
                installRoot,
                CreateCompatibilityOptions(true, false, false, false),
                null);
        });
        Assert(failure != null && failure.GetType() == injectedFailure.GetType(),
            "注入存储失败没有原样传播。实际异常：" +
            (failure == null ? "无" : failure.GetType().FullName));
        Assert(BytesEqual(File.ReadAllBytes(asarPath), originalAsar),
            "存储失败后 app.asar 未恢复到原始字节。");
        Assert(BytesEqual(File.ReadAllBytes(markerPath), originalMarker),
            "存储失败后 marker 未恢复到原始字节。");
        Assert(BytesEqual(File.ReadAllBytes(helperPath), officialHelper),
            "沙箱兼容维护或失败回滚改写了官方 helper。");
        Assert(!File.Exists(CompatibilityTransaction.GetJournalPath(installRoot)),
            "存储失败回滚成功后 journal 未清理。");
        Assert(!Directory.EnumerateDirectories(
            caseRoot,
            "CodexDesktop.compatibility-backup-*",
            SearchOption.TopDirectoryOnly).Any(),
            "存储失败回滚成功后备份目录未清理。");
        Assert(InstallationHealth.Evaluate(installRoot).Status == InstallationHealthStatus.Healthy,
            "存储失败回滚后安装健康状态没有恢复为 Healthy。");
    }

    private static void AssertCompatibilityPostCaptureChangeRollsBack()
    {
        string caseRoot = NewCaseRoot("compatibility-post-capture-change");
        string installRoot = Path.Combine(caseRoot, "CodexDesktop");
        string installId = Guid.NewGuid().ToString("N");
        CreateHealthyCompatibilityInstallation(installRoot, installId, "1.2.3.4");
        string asarPath = Path.Combine(installRoot, "app", "resources", "app.asar");
        string markerPath = InstallOwnership.GetMarkerPath(installRoot);
        byte[] originalAsar = File.ReadAllBytes(asarPath);
        byte[] originalMarker = File.ReadAllBytes(markerPath);

        CompatibilityMaintenance maintenance = new CompatibilityMaintenance(
            (executablePath, desired) =>
            {
                File.AppendAllText(asarPath, "managed-change", Encoding.ASCII);
                return CreateSuccessfulCompatibilityResult();
            },
            (root, id, version, provenance) =>
            {
                InstallOwnership.WriteMarker(root, id, version, provenance);
                File.AppendAllText(asarPath, "late-external-change", Encoding.ASCII);
            },
            delegate { });

        Exception failure = CaptureFailure(delegate
        {
            maintenance.Apply(
                installRoot,
                CreateCompatibilityOptions(false, false, false, false),
                null);
        });
        Assert(failure is InvalidDataException,
            "制品在摘要捕获后变化仍被事务提交。实际异常：" +
            (failure == null ? "无" : failure.GetType().FullName));
        Assert(BytesEqual(File.ReadAllBytes(asarPath), originalAsar),
            "摘要捕获后的竞态失败未恢复 app.asar。");
        Assert(BytesEqual(File.ReadAllBytes(markerPath), originalMarker),
            "摘要捕获后的竞态失败未恢复 marker。");
        Assert(!File.Exists(CompatibilityTransaction.GetJournalPath(installRoot)),
            "摘要捕获后的竞态回滚成功后 journal 未清理。");
        Assert(InstallationHealth.Evaluate(installRoot).Status == InstallationHealthStatus.Healthy,
            "摘要捕获后的竞态回滚后安装未恢复 Healthy。");
    }

    private static void CreateHealthyCompatibilityInstallation(
        string installRoot,
        string installId,
        string version)
    {
        CreateRunnableCodex(installRoot, version, installId, "healthy-compatibility");
        PackageProfile profile = PackageProfileReader.Read(installRoot);
        string sourceDigest = Convert.ToBase64String(
            Enumerable.Range(0, 32).Select(value => (byte)(value + 1)).ToArray());
        PackageMetadata package = CreatePackageMetadata(
            version,
            "OpenAI.Codex_" + version + "_x64__2p2nqsd0c76g0",
            sourceDigest,
            1234);
        CompatibilityResult result = CreateSuccessfulCompatibilityResult();
        ArtifactProvenance provenance = ArtifactProvenance.Capture(
            installRoot,
            profile,
            package,
            null,
            CreateCompatibilityOptions(false, false, false, false),
            result);
        InstallOwnership.WriteMarker(installRoot, installId, version, provenance);
    }

    private static CompatibilityResult CreateSuccessfulCompatibilityResult()
    {
        return new CompatibilityResult
        {
            ModelCatalogSucceeded = true,
            SandboxSucceeded = true,
            LocalizationSucceeded = true,
            ModelCatalog = CreateAlreadySatisfiedFeature(
                "ModelCatalog",
                "模型目录",
                "Official",
                ModelCatalogCompatibility.RecipeId),
            Sandbox = CreateAlreadySatisfiedFeature(
                "SandboxCompatibility",
                "Windows 沙箱兼容",
                "Disabled",
                CompatibilityCoordinator.SandboxRecipeId),
            Localization = CreateAlreadySatisfiedFeature(
                "Localization",
                "界面语言",
                "Menus=Official;Reasoning=Official",
                CodexLocalizationCompatibility.RecipeId)
        };
    }

    private static CompatibilityFeatureResult CreateAlreadySatisfiedFeature(
        string featureId,
        string displayName,
        string state,
        string recipeId)
    {
        return new CompatibilityFeatureResult
        {
            FeatureId = featureId,
            DisplayName = displayName,
            Before = state,
            Desired = state,
            After = state,
            Changed = false,
            Status = CompatibilityFeatureStatus.AlreadySatisfied,
            RecipeId = recipeId
        };
    }

    private static ArtifactDigest FindArtifact(ArtifactProvenance provenance, string relativePath)
    {
        ArtifactDigest artifact = provenance.Artifacts.SingleOrDefault(value => string.Equals(
            value.RelativePath,
            relativePath,
            StringComparison.OrdinalIgnoreCase));
        if (artifact == null)
        {
            throw new InvalidOperationException("测试 provenance 缺少制品：" + relativePath);
        }
        return artifact;
    }

    private static void TestIconPatchIsTransactional()
    {
        string caseRoot = NewCaseRoot("icon-patch-transactional");
        string validTarget = Path.Combine(caseRoot, "valid-target.exe");
        File.Copy(managerPath, validTarget, true);
        IconResourcePatcher.CopyIcons(managerPath, validTarget);
        Assert(IconResourcePatcher.HaveSameIcons(managerPath, validTarget),
            "临时 EXE 图标补丁成功后图标复验失败。");

        string invalidTarget = Path.Combine(caseRoot, "invalid-target.exe");
        byte[] original = Encoding.ASCII.GetBytes("not-a-valid-pe-target");
        File.WriteAllBytes(invalidTarget, original);
        Exception failure = CaptureFailure(delegate
        {
            IconResourcePatcher.CopyIcons(managerPath, invalidTarget);
        });
        Assert(failure != null, "无效目标 EXE 的图标补丁应当失败。");
        Assert(BytesEqual(File.ReadAllBytes(invalidTarget), original), "临时 EXE 补丁失败后正式目标被修改。");
        Assert(!Directory.EnumerateFiles(caseRoot, "invalid-target.exe.icon-new-*", SearchOption.TopDirectoryOnly).Any(),
            "临时 EXE 补丁失败后遗留了中间文件。");
    }

    private static void TestVisualCompatibilityBestEffort()
    {
        string caseRoot = NewCaseRoot("visual-compatibility-best-effort");
        List<string> logs = new List<string>();
        CompatibilityCoordinator coordinator = new CompatibilityCoordinator(logs.Add);
        {
            string missingRoot = Path.Combine(caseRoot, "missing-resource");
            string missingExeRoot = Path.Combine(missingRoot, "app");
            string missingExe = Path.Combine(missingExeRoot, "Codex.exe");
            Directory.CreateDirectory(missingExeRoot);
            byte[] missingExeBytes = new byte[] { 0x4D, 0x5A, 0x10, 0x20 };
            File.WriteAllBytes(missingExe, missingExeBytes);
            coordinator.ApplyVisual(missingRoot, missingExe);
            Assert(BytesEqual(File.ReadAllBytes(missingExe), missingExeBytes), "图标资源缺失时 EXE 被修改。");
            Assert(!File.Exists(Path.Combine(missingRoot, "Codex.ico")), "托盘图标缺失时不应生成 Codex.ico。");

            string failureRoot = Path.Combine(caseRoot, "patch-failure");
            string failureExeRoot = Path.Combine(failureRoot, "app");
            string failureResources = Path.Combine(failureExeRoot, "resources");
            string failureExe = Path.Combine(failureExeRoot, "Codex.exe");
            Directory.CreateDirectory(failureResources);
            byte[] failureExeBytes = Encoding.ASCII.GetBytes("not-a-valid-pe-file");
            byte[] invalidIcon = Encoding.ASCII.GetBytes("not-a-valid-ico-file");
            File.WriteAllBytes(failureExe, failureExeBytes);
            File.WriteAllBytes(Path.Combine(failureResources, "chatgpt-tray-light.ico"), invalidIcon);
            coordinator.ApplyVisual(failureRoot, failureExe);
            Assert(BytesEqual(File.ReadAllBytes(failureExe), failureExeBytes), "EXE 图标补丁失败后官方 EXE 被破坏。");
            Assert(!File.Exists(Path.Combine(failureRoot, "Codex.ico")),
                "无效 ICO 不应作为独立派生图标发布。");
            Assert(logs.Exists(value => value.IndexOf("未找到官方 ICO", StringComparison.Ordinal) >= 0),
                "资源缺失场景缺少警告日志。");
            Assert(logs.Exists(value => value.IndexOf("无法生成独立 Codex.ico", StringComparison.Ordinal) >= 0),
                "无效图标被拒绝时缺少非阻断警告日志。");
        }
    }

    private static void TestMsixTrustTransientFileRetry()
    {
        int attempts = 0;
        List<TimeSpan> delays = new List<TimeSpan>();
        List<string> logs = new List<string>();
        int recovered = MsixPackageTrust.RetryTransientFileTrust(
            () => ++attempts < 3 ? unchecked((int)0x80092003) : 0,
            delays.Add,
            logs.Add);
        Assert(recovered == 0 && attempts == 3 && delays.Count == 2 &&
            delays[0] == TimeSpan.FromSeconds(1) && delays[1] == TimeSpan.FromSeconds(2),
            "MSIX 签名文件瞬时读取失败没有按有限退避恢复。");
        Assert(logs.Count == 2 && logs.All(value => value.IndexOf("0x80092003", StringComparison.Ordinal) >= 0),
            "MSIX 签名文件瞬时读取重试缺少明确日志。");

        attempts = 0;
        delays.Clear();
        int exhausted = MsixPackageTrust.RetryTransientFileTrust(
            () => { attempts++; return unchecked((int)0x80070020); },
            delays.Add,
            delegate { });
        Assert(exhausted == unchecked((int)0x80070020) &&
            attempts == 5 &&
            delays.SequenceEqual(new[]
            {
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(8)
            }),
            "MSIX 签名瞬时读取重试没有在有限预算耗尽后返回原始失败。");

        attempts = 0;
        delays.Clear();
        int untrusted = MsixPackageTrust.RetryTransientFileTrust(
            () => { attempts++; return unchecked((int)0x800B0109); },
            delays.Add,
            delegate { });
        Assert(untrusted == unchecked((int)0x800B0109) && attempts == 1 && delays.Count == 0,
            "非瞬时信任失败被错误重试或降级。");
    }

    private static void TestMsixSignatureSignerExtraction()
    {
        const string fixtureBase64 =
            "UEtDWDCCBGsGCSqGSIb3DQEHAqCCBFwwggRYAgEBMQ0wCwYJYIZIAWUDBAIBMBYGCSqGSIb3DQEHAaAJ" +
            "BAdmaXh0dXJloIICzjCCAsowggGyoAMCAQICCH6soEdk+0a8MA0GCSqGSIb3DQEBCwUAMCUxIzAhBgNV" +
            "BAMTGk1TSVggU2lnbmF0dXJlIFBhcnNlciBUZXN0MB4XDTI2MDcxNjAyNDI0MVoXDTI3MDcxNzAyNDI0" +
            "MVowJTEjMCEGA1UEAxMaTVNJWCBTaWduYXR1cmUgUGFyc2VyIFRlc3QwggEiMA0GCSqGSIb3DQEBAQUA" +
            "A4IBDwAwggEKAoIBAQC3iC1cJ8pmqtNSMtzypMXSviJdukIyjRiRLBz4n6XgilEigjM9xKke/Ll/FRFv" +
            "vPcJW+UDHCAo+IjXpwCMYUIL33qPGXvQvkC0c9zMLNsCFLrA8i3thVnL9kXAjxzzTMsgj4/2Lr9ad/a" +
            "9uHBYu7xIvACaTIuugrs0+SD3mz4f3nY2L4+O7s+Zb1qmRlGpDkNSPWrul917mzyyD4Cdy3n9YzIJAT" +
            "G5BxrWnRo4cio6BlTfRE+/WBjsvNojg2VkyOW+nhr3EzWClEviRvn6mxNLc8Gkj+5eVcj+7Ct6OSrj5" +
            "BoG8HFW3P3uLzD1XsDC2NjkDI+umumC76E2XjyVkC6pAgMBAAEwDQYJKoZIhvcNAQELBQADggEBAGU9" +
            "pEzM2ANRgJ98gPL5CX1pqTDg/M2+ZQnB828oTAlMYlg2Ne9sJxi1L++9IbssDC/Z7oCssgpybxZ/8ZOj" +
            "o12hyNv3SsrBvDMYxUQib1pK8/fcnOvXh42TxkC7kJoCWgTbEqx5B8ufXP/oAlERMjctimclRyzARlLf" +
            "scrd511V+8q0W1rioWeusT0ZBuRd//nOAX38AWxwslp9ndBkc1zwuBTCvgGMd7C6U0X8m6Zu8qGQ/no" +
            "jMucrKc95CZ+gPYmVyUO9/qn5KMPn2BT5/qYDxrN5+lxkRnlSyGdcPy8FgBn8Nj9MyBeKdbjaeq7Wu5" +
            "hgaAW2U7SD8JwFLsF0NuoxggFYMIIBVAIBATAxMCUxIzAhBgNVBAMTGk1TSVggU2lnbmF0dXJlIFBhcn" +
            "NlciBUZXN0Agh+rKBHZPtGvDALBglghkgBZQMEAgEwCwYJKoZIhvcNAQEBBIIBAK47tOa6h5pH/B7UX" +
            "6Hm5SkcYc5JnWYXF8YYY60Cf7HqSwwDyZvKRw3CMNqqyG3mFiaK+lJ4DfoyVSL/bhF6FKPEtWYuyA6E" +
            "gIOI/r8RYKjJMkwNM9LP15hHs1OhUJo9+Y98ZXpP8yYhNvxgpszW2C3MF4CIWPcKDFApZ9yV8Aw374m" +
            "aQNFS8sM/CaEN9KmthMuQaimYXgvfy12Itn7VgJY9AEnsz1Pr0myH28sKqh0NT2sdEpJP2+UgEQgadkp" +
            "kmgqOm+t4XDLBDp5sHo4MMVSs+rTIA/fn5gOh1+4h0clouANYwTZuUg/3hI8BgmHDVJX+oT3r3lPeAK" +
            "ExfdX4jzQ=";

        byte[] signatureBytes = Convert.FromBase64String(fixtureBase64);
        string subject = MsixPackageTrust
            .DecodeAppxSignatureSignerSubject(signatureBytes)
            .Name;
        Assert(string.Equals(
            subject,
            "CN=MSIX Signature Parser Test",
            StringComparison.Ordinal),
            "MSIX PKCX 没有解析出绑定的唯一签名者 Subject：" + subject);

        byte[] wrongHeader = (byte[])signatureBytes.Clone();
        wrongHeader[0] = 0x58;
        ExpectInvocationFailure(
            () => MsixPackageTrust.DecodeAppxSignatureSignerSubject(wrongHeader),
            "缺少 PKCX 标头的 MSIX 签名被接受。");

        byte[] tamperedSignature = (byte[])signatureBytes.Clone();
        tamperedSignature[tamperedSignature.Length - 1] ^= 0x01;
        ExpectInvocationFailure(
            () => MsixPackageTrust.DecodeAppxSignatureSignerSubject(tamperedSignature),
            "内容签名损坏的 MSIX PKCS#7 被接受。");
    }

    private static void TestMsixPackageMetadataValidation()
    {
        string version = "1.2.3.4";
        string architecture = "x64";
        string fullName = "OpenAI.Codex_" + version + "_" + architecture + "__2p2nqsd0c76g0";
        // 使用不存在的占位路径，确保这两项在访问包文件之前拒绝错误元数据。
        string metadataOnlyPackagePath = Path.Combine(suiteRoot, "metadata-only.msix");
        string placeholderDigest = Convert.ToBase64String(new byte[32]);
        ExpectInvocationFailure(
            delegate
            {
                PackageMetadata wrongFullName = CreatePackageMetadata(
                    version,
                    "OpenAI.Codex_" + version + "_" + architecture + "__wrongpublisher",
                    placeholderDigest,
                    1);
                using (VerifiedArtifactLease lease = MsixPackageTrust.VerifyAndLock(
                    metadataOnlyPackagePath,
                    wrongFullName,
                    architecture,
                    delegate { })) { }
            },
            "错误 PackageFullName 没有被拒绝。");

        string otherArchitecture = architecture == "x64" ? "arm64" : "x64";
        PackageMetadata architectureMetadata = CreatePackageMetadata(
            version,
            fullName,
            placeholderDigest,
            1);
        ExpectInvocationFailure(
            delegate
            {
                using (VerifiedArtifactLease lease = MsixPackageTrust.VerifyAndLock(
                    metadataOnlyPackagePath,
                    architectureMetadata,
                    otherArchitecture,
                    delegate { })) { }
            },
            "错误目标架构没有被拒绝。");
    }

    private static void TestMsixPackageTrust()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("CPM_RUN_LARGE_MSIX_TESTS"),
            "1",
            StringComparison.Ordinal))
        {
            Skip("完整签名、digest 与篡改副本由 Run-MsixTrustTests.ps1 覆盖；设置 CPM_RUN_LARGE_MSIX_TESTS=1 可在本回归中显式执行。");
        }

        string managerDirectory = Path.GetDirectoryName(managerPath);
        string cacheRoot = Path.Combine(managerDirectory, "data", "cache");
        FileInfo officialPackage = FindOfficialCachedPackage(cacheRoot);
        Match fileName = Regex.Match(
            officialPackage.Name,
            @"^OpenAI\.Codex_(?<version>[0-9]+(?:\.[0-9]+){3})_(?<arch>x64|arm64)\.msix$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert(fileName.Success, "正式缓存包文件名无法解析：" + officialPackage.Name);
        string version = fileName.Groups["version"].Value;
        string architecture = fileName.Groups["arch"].Value.ToLowerInvariant();
        string fullName = "OpenAI.Codex_" + version + "_" + architecture + "__2p2nqsd0c76g0";
        string digest = ComputeSha256Base64(officialPackage.FullName);
        string caseRoot = NewCaseRoot("msix-package-trust");
        string linkedPackage = Path.Combine(caseRoot, officialPackage.Name);
        string tamperedPackage = Path.Combine(caseRoot, "tampered-" + officialPackage.Name);
        if (!CreateHardLink(linkedPackage, officialPackage.FullName, IntPtr.Zero))
        {
            throw new IOException("无法为正式 MSIX 创建只读测试硬链接，Win32=" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
        }

        try
        {
            List<string> logs = new List<string>();
            PackageMetadata validMetadata = CreatePackageMetadata(version, fullName, digest, officialPackage.Length);
            using (VerifiedArtifactLease lease = MsixPackageTrust.VerifyAndLock(
                linkedPackage,
                validMetadata,
                architecture,
                logs.Add)) { }
            Assert(logs.Exists(value => value.IndexOf("MSIX 官方完整性校验通过", StringComparison.Ordinal) >= 0),
                "正式缓存包通过后缺少可信验证日志。");

            ExpectInvocationFailure(
                delegate
                {
                    PackageMetadata wrongDigest = CreatePackageMetadata(
                        version,
                        fullName,
                        Convert.ToBase64String(new byte[32]),
                        officialPackage.Length);
                    using (VerifiedArtifactLease lease = MsixPackageTrust.VerifyAndLock(
                        linkedPackage,
                        wrongDigest,
                        architecture,
                        delegate { })) { }
                },
                "错误 MSIX digest 没有被拒绝。");

            File.Copy(linkedPackage, tamperedPackage, false);
            using (FileStream stream = new FileStream(tamperedPackage, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Position = stream.Length - 1;
                int original = stream.ReadByte();
                stream.Position = stream.Length - 1;
                stream.WriteByte((byte)(original ^ 0x5A));
                stream.Flush(true);
            }
            ExpectInvocationFailure(
                delegate
                {
                    using (VerifiedArtifactLease lease = MsixPackageTrust.VerifyAndLock(
                        tamperedPackage,
                        validMetadata,
                        architecture,
                        delegate { })) { }
                },
                "篡改后的 MSIX 副本没有被 digest 校验拒绝。");
            Assert(string.Equals(ComputeSha256Base64(officialPackage.FullName), digest, StringComparison.Ordinal),
                "MSIX 篡改副本测试影响了原始正式缓存包。");
        }
        finally
        {
            if (File.Exists(tamperedPackage)) File.Delete(tamperedPackage);
            if (File.Exists(linkedPackage)) File.Delete(linkedPackage);
        }
    }
}
}
