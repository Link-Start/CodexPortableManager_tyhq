using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CodexPortableManager
{
    internal static class CompatibilityPackageTestRunner
    {
        internal static int Run(string reportPath, string sourceAsar)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "CodexCompatibilityPackage-" + Guid.NewGuid().ToString("N"));
            try
            {
                string resources = Path.Combine(root, "app", "resources");
                Directory.CreateDirectory(resources);
                string executable = Path.Combine(root, "app", "Codex.exe");
                string asar = Path.Combine(resources, "app.asar");
                File.WriteAllBytes(executable, new byte[] { 0x4D, 0x5A, 0x01 });
                File.Copy(sourceAsar, asar);
                string originalHash = Hash(asar);
                List<string> logs = new List<string>();

                string modelInitial;
                string sandboxInitial;
                string localizationInitial;
                using (AsarSession session = AsarSession.Open(asar))
                {
                    modelInitial = ModelCatalogCompatibility.Inspect(session).After;
                    sandboxInitial = SandboxCompatibility.InspectFeature(session).After;
                    localizationInitial = CodexLocalizationCompatibility.Inspect(session).After;
                }

                bool modelEnabled = ModelCatalogCompatibility.TryConfigure(
                    executable,
                    true,
                    logs.Add) && ModelCatalogCompatibility.IsEnabled(executable);
                bool modelDisabled = ModelCatalogCompatibility.TryConfigure(
                    executable,
                    false,
                    logs.Add) && !ModelCatalogCompatibility.IsEnabled(executable);
                bool modelRestored = string.Equals(Hash(asar), originalHash, StringComparison.Ordinal);

                bool sandboxEnabled = SandboxCompatibility.TryConfigure(
                    executable,
                    true,
                    logs.Add) && SandboxCompatibility.IsEnabled(executable);
                bool sandboxDisabled = SandboxCompatibility.TryConfigure(
                    executable,
                    false,
                    logs.Add) && !SandboxCompatibility.IsEnabled(executable);
                bool sandboxRestored = string.Equals(Hash(asar), originalHash, StringComparison.Ordinal);

                bool localizationEnabled = CodexLocalizationCompatibility.TryConfigure(
                    executable,
                    true,
                    true,
                    logs.Add);
                bool localizationDisabled = CodexLocalizationCompatibility.TryConfigure(
                    executable,
                    false,
                    false,
                    logs.Add);
                bool localizationRestored = string.Equals(
                    Hash(asar),
                    originalHash,
                    StringComparison.Ordinal);

                bool passed = string.Equals(modelInitial, "Official", StringComparison.Ordinal) &&
                    string.Equals(sandboxInitial, "Disabled", StringComparison.Ordinal) &&
                    string.Equals(
                        localizationInitial,
                        "Menus=Official;Reasoning=Official",
                        StringComparison.Ordinal) &&
                    modelEnabled && modelDisabled && modelRestored &&
                    sandboxEnabled && sandboxDisabled && sandboxRestored &&
                    localizationEnabled && localizationDisabled && localizationRestored;
                File.WriteAllText(
                    reportPath,
                    "RESULT=" + (passed ? "PASS" : "FAIL") + Environment.NewLine +
                    "MODEL_INITIAL=" + modelInitial + Environment.NewLine +
                    "SANDBOX_INITIAL=" + sandboxInitial + Environment.NewLine +
                    "LOCALIZATION_INITIAL=" + localizationInitial + Environment.NewLine +
                    "MODEL_ROUNDTRIP=" + (modelEnabled && modelDisabled && modelRestored) + Environment.NewLine +
                    "SANDBOX_ROUNDTRIP=" + (sandboxEnabled && sandboxDisabled && sandboxRestored) + Environment.NewLine +
                    "LOCALIZATION_ROUNDTRIP=" +
                        (localizationEnabled && localizationDisabled && localizationRestored) + Environment.NewLine +
                    "ORIGINAL_SHA256=" + originalHash + Environment.NewLine +
                    "FINAL_SHA256=" + Hash(asar) + Environment.NewLine +
                    "LOG=" + string.Join(" | ", logs.ToArray()),
                    new UTF8Encoding(false));
                return passed ? 0 : 1;
            }
            catch (Exception exception)
            {
                File.WriteAllText(
                    reportPath,
                    "RESULT=FAIL" + Environment.NewLine + exception,
                    new UTF8Encoding(false));
                return 1;
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch { }
            }
        }

        private static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }
    }
}
