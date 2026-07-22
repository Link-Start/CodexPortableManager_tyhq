using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexPortableManager
{
    internal static partial class CodexLocalizationCompatibility
    {
        internal static IReadOnlyList<KeyValuePair<string, string>> CurrentLocaleMenuTranslations
        {
            get { return LocaleMenuTranslations; }
        }

        internal static byte[] ReadCurrentMenuResourceForValidation(string executablePath)
        {
            using (AsarSession session = AsarSession.Open(AsarSession.GetAsarPath(executablePath)))
            {
                AsarArchiveEntry entry = session.Entries.Single(value =>
                    string.Equals(value.Path, LocaleMenuPath, StringComparison.Ordinal));
                return session.GetEntryData(entry);
            }
        }

        internal static byte[] ReadCurrentNativeMenuScriptForValidation(string executablePath)
        {
            using (AsarSession session = AsarSession.Open(AsarSession.GetAsarPath(executablePath)))
            {
                AsarArchiveEntry entry = session.FindUniqueEntry(
                    value => value.Path.StartsWith(".vite/build/main-", StringComparison.Ordinal) &&
                        value.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase),
                    data => AsarSession.ContainsAscii(data, "native-menu-locales") &&
                        AsarSession.ContainsAscii(data, "menuTitleIntlId"),
                    "原生菜单主进程脚本");
                return session.GetEntryData(entry);
            }
        }
    }
}
