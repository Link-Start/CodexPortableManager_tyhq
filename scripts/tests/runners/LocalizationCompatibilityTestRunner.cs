using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexPortableManager
{
    internal static class LocalizationCompatibilityTestRunner
    {
        public static int Inspect(string reportPath, string sourceAsar)
        {
            try
            {
                System.Diagnostics.Stopwatch stopwatch =
                    System.Diagnostics.Stopwatch.StartNew();
                CompatibilityFeatureChange result;
                using (AsarSession session = AsarSession.Open(sourceAsar))
                {
                    result = CodexLocalizationCompatibility.Inspect(session);
                }
                stopwatch.Stop();
                File.WriteAllText(
                    reportPath,
                    "RESULT=PASS" + Environment.NewLine +
                    "ELAPSED_MS=" + stopwatch.ElapsedMilliseconds + Environment.NewLine +
                    "SUCCEEDED=" + result.Succeeded + Environment.NewLine +
                    "STATUS=" + result.Status + Environment.NewLine +
                    "BEFORE=" + result.Before + Environment.NewLine +
                    "AFTER=" + result.After + Environment.NewLine +
                    "ERROR=" + (result.Error ?? string.Empty),
                    new UTF8Encoding(false));
                return 0;
            }
            catch (Exception exception)
            {
                File.WriteAllText(reportPath, "RESULT=FAIL" + Environment.NewLine + exception);
                return 1;
            }
        }

        public static int Diagnose(string reportPath, string sourceAsar)
        {
            try
            {
                List<string> lines = new List<string>();
                using (AsarSession session = AsarSession.Open(sourceAsar))
                {
                    foreach (AsarArchiveEntry entry in session.Entries.Where(value =>
                        value.Path.StartsWith(".vite/build/main-", StringComparison.Ordinal) &&
                        value.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)))
                    {
                        string text = Encoding.UTF8.GetString(session.GetEntryData(entry));
                        if (!text.Contains("native-menu-locales") &&
                            !text.Contains("menuTitleIntlId"))
                        {
                            continue;
                        }

                        lines.Add("ENTRY=" + entry.Path);
                        foreach (string needle in new[] { "native-menu-locales", "menuTitleIntlId" })
                        {
                            int index = 0;
                            int count = 0;
                            while ((index = text.IndexOf(
                                needle,
                                index,
                                StringComparison.Ordinal)) >= 0 && count < 16)
                            {
                                int start = Math.Max(0, index - 240);
                                int length = Math.Min(760, text.Length - start);
                                lines.Add(
                                    needle.ToUpperInvariant() + "_" + count + "=" +
                                    text.Substring(start, length)
                                        .Replace("\r", " ")
                                        .Replace("\n", " "));
                                index += needle.Length;
                                count++;
                            }
                        }
                    }
                }
                string[] consumerIds = ReadMenuConsumerIds(sourceAsar);
                Dictionary<string, object> locale = ReadMenuLocale(sourceAsar);
                lines.Add("CONSUMER_IDS=" + string.Join(",", consumerIds));
                lines.Add("LOCALE_KEYS=" + string.Join(
                    ",",
                    locale.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray()));
                lines.Add("MISSING_CONSUMER_IDS=" + string.Join(
                    ",",
                    consumerIds.Where(id => !locale.ContainsKey(id)).ToArray()));
                foreach (KeyValuePair<string, string> context in ReadMenuConsumerContexts(
                    sourceAsar,
                    consumerIds.Where(id => !locale.ContainsKey(id))))
                {
                    lines.Add("CONTEXT_" + context.Key + "=" + context.Value);
                }
                File.WriteAllLines(reportPath, lines, new UTF8Encoding(false));
                return lines.Count > 0 ? 0 : 1;
            }
            catch (Exception exception)
            {
                File.WriteAllText(reportPath, "RESULT=FAIL" + Environment.NewLine + exception);
                return 1;
            }
        }

        public static int Run(string reportPath, string sourceAsar)
        {
            string root = Path.Combine(Path.GetTempPath(), "CodexLocalizationTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                string resources = Path.Combine(root, "resources");
                Directory.CreateDirectory(resources);
                string asar = Path.Combine(resources, "app.asar");
                File.Copy(sourceAsar, asar);
                string exe = Path.Combine(root, "Codex.exe");
                File.WriteAllBytes(exe, new byte[] { 0 });

                List<string> logs = new List<string>();
                bool baselineRestored = CodexLocalizationCompatibility.TryConfigure(
                    exe,
                    false,
                    false,
                    logs.Add);
                string original = Hash(asar);

                bool enabled = CodexLocalizationCompatibility.TryConfigure(
                    exe,
                    true,
                    true,
                    logs.Add);
                string patched = Hash(asar);
                byte[] patchedArchive = File.ReadAllBytes(asar);
                Dictionary<string, object> locale = ParseLocale(
                    CodexLocalizationCompatibility.ReadCurrentMenuResourceForValidation(exe));
                string nativeMenuScript = Encoding.UTF8.GetString(
                    CodexLocalizationCompatibility.ReadCurrentNativeMenuScriptForValidation(exe));
                string nativeMenuScriptPath = reportPath + ".native-menu.js";
                File.WriteAllText(
                    nativeMenuScriptPath,
                    nativeMenuScript,
                    new UTF8Encoding(false));
                string[] consumerIds = ReadMenuConsumerIds(sourceAsar);
                Dictionary<string, string> knownTranslations =
                    CodexLocalizationCompatibility.CurrentLocaleMenuTranslations.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        StringComparer.Ordinal);
                string[] knownConsumerIds = consumerIds
                    .Where(knownTranslations.ContainsKey)
                    .ToArray();
                string[] unknownConsumerIds = consumerIds
                    .Where(id => !knownTranslations.ContainsKey(id))
                    .ToArray();
                string[] missingConsumerIds = knownConsumerIds
                    .Where(id => !locale.ContainsKey(id))
                    .ToArray();
                bool menuLocalized = LocaleTranslationsMatch(locale, knownConsumerIds) &&
                    locale.ContainsKey(CodexLocalizationCompatibility.LocaleMenuMarkerKey) &&
                    ContainsAscii(patchedArchive, CodexLocalizationCompatibility.LocaleMenuMarkerKey) &&
                    nativeMenuScript.Contains(CodexLocalizationCompatibility.NativeMenuScriptMarker) &&
                    nativeMenuScript.Contains(CodexLocalizationCompatibility.NativeTrayExitMarker) &&
                    nativeMenuScript.Contains(CodexLocalizationCompatibility.NativeMenuSettingsStoreMarker) &&
                    nativeMenuScript.Contains(CodexLocalizationCompatibility.NativeMenuLocaleRefreshMarker) &&
                    nativeMenuScript.Contains(CodexLocalizationCompatibility.NativeTraceResolverMarker) &&
                    nativeMenuScript.Contains("localeOverride.key") &&
                    nativeMenuScript.Contains("\"Undo\":\"撤销\"") &&
                    nativeMenuScript.Contains("\"Start Performance Trace\":\"开始性能跟踪\"") &&
                    nativeMenuScript.Contains("\"Stop Performance Trace\":\"停止性能跟踪\"") &&
                    nativeMenuScript.Contains("`退出`") &&
                    !ContainsAscii(patchedArchive, "/*Z*/") &&
                    !ContainsAscii(patchedArchive, "/*N*/");
                bool professionalKeysEnglish = ContainsAscii(
                    patchedArchive,
                    "composer.mode.local.reasoning.low.labe_");

                bool disabled = CodexLocalizationCompatibility.TryConfigure(
                    exe,
                    false,
                    false,
                    logs.Add);
                string restored = Hash(asar);
                Dictionary<string, object> restoredLocale = ParseLocale(
                    CodexLocalizationCompatibility.ReadCurrentMenuResourceForValidation(exe));
                string restoredNativeMenuScript = Encoding.UTF8.GetString(
                    CodexLocalizationCompatibility.ReadCurrentNativeMenuScriptForValidation(exe));
                bool menuRestored = !restoredLocale.ContainsKey(
                    CodexLocalizationCompatibility.LocaleMenuMarkerKey) &&
                    CodexLocalizationCompatibility.CurrentLocaleMenuTranslations.All(
                        pair => !restoredLocale.ContainsKey(pair.Key)) &&
                    !restoredNativeMenuScript.Contains(
                        CodexLocalizationCompatibility.NativeMenuScriptMarker) &&
                    !restoredNativeMenuScript.Contains(
                        CodexLocalizationCompatibility.NativeTrayExitMarker) &&
                    restoredNativeMenuScript.Contains("`Start Performance Trace`") &&
                    restoredNativeMenuScript.Contains("`Stop Performance Trace`");

                bool enabledAgain = CodexLocalizationCompatibility.TryConfigure(
                    exe,
                    true,
                    true,
                    logs.Add);
                string patchedAgain = Hash(asar);
                bool disabledAgain = CodexLocalizationCompatibility.TryConfigure(
                    exe,
                    false,
                    false,
                    logs.Add);
                string restoredAgain = Hash(asar);
                bool stable = patched == patchedAgain &&
                    original == restored &&
                    original == restoredAgain;
                bool modelPatchCompatible = ModelCatalogCompatibility.TryConfigure(exe, true, logs.Add);
                int translationCount =
                    CodexLocalizationCompatibility.CurrentLocaleMenuTranslations.Count;
                bool passed = baselineRestored &&
                    enabled &&
                    disabled &&
                    enabledAgain &&
                    disabledAgain &&
                    modelPatchCompatible &&
                    menuLocalized &&
                    menuRestored &&
                    professionalKeysEnglish &&
                    missingConsumerIds.Length == 0 &&
                    translationCount == 58 &&
                    original != patched &&
                    stable;
                File.WriteAllText(
                    reportPath,
                    "RESULT=" + (passed ? "PASS" : "FAIL") + Environment.NewLine +
                    "BASELINE_RESTORED=" + baselineRestored + Environment.NewLine +
                    "ENABLE=" + enabled + Environment.NewLine +
                    "DISABLE=" + disabled + Environment.NewLine +
                    "TRANSLATION_COUNT=" + translationCount + Environment.NewLine +
                    "CONSUMER_IDS=" + string.Join(",", consumerIds) + Environment.NewLine +
                    "UNKNOWN_CONSUMER_IDS=" + string.Join(",", unknownConsumerIds) + Environment.NewLine +
                    "MISSING_CONSUMER_IDS=" + string.Join(",", missingConsumerIds) + Environment.NewLine +
                    "MENU_LOCALIZED=" + menuLocalized + Environment.NewLine +
                    "MENU_RESTORED=" + menuRestored + Environment.NewLine +
                    "NATIVE_MENU_SCRIPT=" + nativeMenuScriptPath + Environment.NewLine +
                    "PRO_PARAMETERS_ENGLISH=" + professionalKeysEnglish + Environment.NewLine +
                    "MODEL_PATCH_COMPATIBLE=" + modelPatchCompatible + Environment.NewLine +
                    "ORIGINAL_SHA256=" + original + Environment.NewLine +
                    "PATCHED_SHA256=" + patched + Environment.NewLine +
                    "RESTORED_SHA256=" + restored + Environment.NewLine +
                    "PATCHED_AGAIN_SHA256=" + patchedAgain + Environment.NewLine +
                    "RESTORED_AGAIN_SHA256=" + restoredAgain + Environment.NewLine +
                    "REPEAT_STABLE=" + stable + Environment.NewLine +
                    "LOG=" + string.Join(" | ", logs));
                return passed ? 0 : 1;
            }
            catch (Exception exception)
            {
                File.WriteAllText(reportPath, "RESULT=FAIL" + Environment.NewLine + exception);
                return 1;
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch { }
            }
        }

        private static Dictionary<string, object> ParseLocale(byte[] data)
        {
            Dictionary<string, object> value = new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue
            }.DeserializeObject(Encoding.UTF8.GetString(data)) as Dictionary<string, object>;
            if (value == null) throw new InvalidDataException("中文菜单资源不是 JSON 对象。");
            return value;
        }

        private static string[] ReadMenuConsumerIds(string asarPath)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            using (AsarSession session = AsarSession.Open(asarPath))
            {
                foreach (AsarArchiveEntry entry in session.Entries.Where(value =>
                    value.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)))
                {
                    string text = Encoding.UTF8.GetString(session.GetEntryData(entry));
                    foreach (Match match in Regex.Matches(
                        text,
                        @"codex\.commandMenuTitle\.[A-Za-z0-9.]+",
                        RegexOptions.CultureInvariant))
                    {
                        ids.Add(match.Value);
                    }
                }
            }
            return ids.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        private static Dictionary<string, object> ReadMenuLocale(string asarPath)
        {
            using (AsarSession session = AsarSession.Open(asarPath))
            {
                AsarArchiveEntry entry = session.Entries.Single(value =>
                    string.Equals(
                        value.Path,
                        "native-menu-locales/zh-CN.json",
                        StringComparison.Ordinal));
                return ParseLocale(session.GetEntryData(entry));
            }
        }

        private static IReadOnlyList<KeyValuePair<string, string>> ReadMenuConsumerContexts(
            string asarPath,
            IEnumerable<string> ids)
        {
            HashSet<string> pending = new HashSet<string>(
                ids ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);
            List<KeyValuePair<string, string>> contexts =
                new List<KeyValuePair<string, string>>();
            using (AsarSession session = AsarSession.Open(asarPath))
            {
                foreach (AsarArchiveEntry entry in session.Entries.Where(value =>
                    value.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)))
                {
                    string text = Encoding.UTF8.GetString(session.GetEntryData(entry));
                    foreach (string id in pending.ToArray())
                    {
                        int index = text.IndexOf(id, StringComparison.Ordinal);
                        if (index < 0) continue;
                        int start = Math.Max(0, index - 160);
                        int length = Math.Min(520, text.Length - start);
                        contexts.Add(new KeyValuePair<string, string>(
                            id,
                            entry.Path + "|" + text.Substring(start, length)
                                .Replace("\r", " ")
                                .Replace("\n", " ")));
                        pending.Remove(id);
                    }
                    if (pending.Count == 0) break;
                }
            }
            return contexts.OrderBy(value => value.Key, StringComparer.Ordinal).ToList();
        }

        private static bool LocaleTranslationsMatch(
            Dictionary<string, object> locale,
            IEnumerable<string> consumerIds)
        {
            Dictionary<string, string> translations =
                CodexLocalizationCompatibility.CurrentLocaleMenuTranslations.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal);
            return (consumerIds ?? Enumerable.Empty<string>()).All(id =>
            {
                object value;
                string expected;
                return translations.TryGetValue(id, out expected) &&
                    locale.TryGetValue(id, out value) &&
                    string.Equals(value as string, expected, StringComparison.Ordinal);
            });
        }

        private static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }

        private static bool ContainsAscii(byte[] data, string value)
        {
            byte[] pattern = Encoding.UTF8.GetBytes(value);
            for (int index = 0; index <= data.Length - pattern.Length; index++)
            {
                int offset = 0;
                while (offset < pattern.Length && data[index + offset] == pattern[offset]) offset++;
                if (offset == pattern.Length) return true;
            }
            return false;
        }
    }
}
