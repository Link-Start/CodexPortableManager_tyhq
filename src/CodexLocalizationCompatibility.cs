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
        internal const string RecipeId = "localization.menu-locale-reasoning";

        internal const string ReasoningFamilyMarker = "composer.mode.local.reasoning.";

        private static readonly Regex ReasoningKeyFamilyRegex = new Regex(
            @"composer\.mode\.local\.reasoning\.(?<level>[a-z][a-z0-9-]*)\.labe(?<ending>l|_)(?![A-Za-z0-9_])",
            RegexOptions.CultureInvariant);

        internal static IEnumerable<string> MenuMarkers
        {
            get
            {
                return new[]
                {
                    NativeMenuManagedPrefix,
                    LocaleMenuMarkerKey,
                    NativeMenuScriptMarker,
                    NativeTrayLabelsMarker,
                    NativeTrayExitMarker,
                    NativeMenuSettingsStoreMarker,
                    NativeMenuLocaleRefreshMarker,
                    NativeTraceResolverMarker
                };
            }
        }

        internal static IEnumerable<string> ManagedMarkers
        {
            get { return MenuMarkers.Concat(new[] { ReasoningFamilyMarker }); }
        }

        public static bool TryConfigure(
            string executablePath,
            bool chineseMenusEnabled,
            bool englishReasoningEnabled,
            Action<string> log)
        {
            return new CompatibilityPlan(log).ApplyLocalization(
                executablePath,
                chineseMenusEnabled,
                englishReasoningEnabled);
        }

        public static void Configure(
            string executablePath,
            bool chineseMenusEnabled,
            bool englishReasoningEnabled,
            Action<string> log)
        {
            if (!TryConfigure(executablePath, chineseMenusEnabled, englishReasoningEnabled, log))
            {
                throw new InvalidDataException("Codex 界面语言兼容设置未能完成。");
            }
        }

        internal static void LogUnavailable(Action<string> log, Exception exception)
        {
            SafeLog(log, "警告：Codex 界面语言兼容设置与当前版本不兼容，已保留完整 app.asar。原因：" + exception.Message);
        }

        private static CompatibilityPatchState GetReasoningState(byte[] data)
        {
            string text = Encoding.UTF8.GetString(data);
            string[] keys = DiscoverReasoningKeys(text);
            if (keys.Length == 0) return CompatibilityPatchState.Unsupported;
            bool official = true;
            bool patched = true;
            foreach (string key in keys)
            {
                string patchedKey = ToPatchedReasoningKey(key);
                int officialCount = CountOccurrences(text, key);
                int patchedCount = CountOccurrences(text, patchedKey);
                official &= officialCount >= 1 && patchedCount == 0;
                patched &= officialCount == 0 && patchedCount >= 1;
            }
            if (official) return CompatibilityPatchState.Official;
            if (patched) return CompatibilityPatchState.Patched;
            return CompatibilityPatchState.Mixed;
        }

        private static byte[] TransformReasoning(byte[] data, bool enabled)
        {
            string text = Encoding.UTF8.GetString(data);
            string[] keys = DiscoverReasoningKeys(text);
            if (keys.Length == 0)
            {
                throw new InvalidDataException("没有找到完整的专业参数语言键族。");
            }
            foreach (string key in keys)
            {
                string patchedKey = ToPatchedReasoningKey(key);
                text = enabled
                    ? text.Replace(key, patchedKey)
                    : text.Replace(patchedKey, key);
            }
            byte[] result = Encoding.UTF8.GetBytes(text);
            if (result.Length != data.Length)
            {
                throw new InvalidDataException("专业参数语言补丁必须保持 ASAR 条目长度不变。");
            }
            CompatibilityPatchState expected = enabled
                ? CompatibilityPatchState.Patched
                : CompatibilityPatchState.Official;
            if (GetReasoningState(result) != expected)
            {
                throw new InvalidDataException("专业参数语言变换验证失败。");
            }
            return result;
        }

        private static string[] DiscoverReasoningKeys(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new string[0];
            Match[] matches = ReasoningKeyFamilyRegex.Matches(text)
                .Cast<Match>()
                .ToArray();
            if (matches.Length < 3) return new string[0];

            IGrouping<string, Match>[] levels = matches
                .GroupBy(match => match.Groups["level"].Value, StringComparer.Ordinal)
                .ToArray();
            if (!levels.Any(group => string.Equals(
                group.Key,
                "medium",
                StringComparison.Ordinal)))
            {
                return new string[0];
            }
            return levels
                .Select(group =>
                    "composer.mode.local.reasoning." + group.Key + ".label")
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        private static string ToPatchedReasoningKey(string key)
        {
            return key.Substring(0, key.Length - 1) + "_";
        }

        private static int CountOccurrences(string value, string expected)
        {
            int count = 0;
            int start = 0;
            while (true)
            {
                int index = value.IndexOf(expected, start, StringComparison.Ordinal);
                if (index < 0) return count;
                count++;
                start = index + expected.Length;
            }
        }

        private static string ReplaceOnce(string value, string source, string target)
        {
            int index = value.IndexOf(source, StringComparison.Ordinal);
            if (index < 0) throw new InvalidDataException("没有找到预期语言指纹：" + source);
            if (value.IndexOf(source, index + source.Length, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidDataException("语言指纹出现多次：" + source);
            }
            return value.Substring(0, index) + target + value.Substring(index + source.Length);
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static void SafeLog(Action<string> log, string message)
        {
            if (log == null || string.IsNullOrWhiteSpace(message)) return;
            try { log(message); }
            catch { }
        }
    }
}
