using System;
using System.IO;
using System.Net;

namespace CodexPortableManager
{
    internal static class TestProgram
    {
        [STAThread]
        private static void Main(string[] args)
        {
            EmbeddedAssemblyResolver.Initialize();
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            if (args.Length == 0)
            {
                Environment.ExitCode = 64;
                return;
            }

            string command = args[0];
            if (EqualsCommand(command, "--render-test"))
            {
                RenderTestRunner.Run(
                    GetArgument(args, 1, Path.Combine(Path.GetTempPath(), "CodexPortableManager-render.png")),
                    GetDoubleArgument(args, 2),
                    GetDoubleArgument(args, 3),
                    GetDoubleArgument(args, 4),
                    args.Length > 5 ? args[5] : null);
                return;
            }

            if (EqualsCommand(command, "--model-bounded-diagnose"))
            {
                Console.WriteLine(ModelCatalogCompatibility.DiagnoseBoundedAnalysisForTest(
                    GetArgument(args, 1, string.Empty)));
                return;
            }

            if (EqualsCommand(command, "--localization-test"))
            {
                Environment.ExitCode = LocalizationCompatibilityTestRunner.Run(
                    GetArgument(args, 1, Path.Combine(Path.GetTempPath(), "CodexPortableManager-localization-test.txt")),
                    GetArgument(args, 2, Path.Combine(GetDefaultFixtureRoot(), "app", "resources", "app.asar")));
                return;
            }

            if (EqualsCommand(command, "--localization-diagnose"))
            {
                Environment.ExitCode = LocalizationCompatibilityTestRunner.Diagnose(
                    GetArgument(args, 1, Path.Combine(Path.GetTempPath(), "CodexPortableManager-localization-diagnose.txt")),
                    GetArgument(args, 2, Path.Combine(GetDefaultFixtureRoot(), "app", "resources", "app.asar")));
                return;
            }

            if (EqualsCommand(command, "--localization-inspect"))
            {
                Environment.ExitCode = LocalizationCompatibilityTestRunner.Inspect(
                    GetArgument(args, 1, Path.Combine(Path.GetTempPath(), "CodexPortableManager-localization-inspect.txt")),
                    GetArgument(args, 2, Path.Combine(GetDefaultFixtureRoot(), "app", "resources", "app.asar")));
                return;
            }

            if (EqualsCommand(command, "--localization-configure"))
            {
                string executable = GetArgument(args, 1, Path.Combine(GetDefaultFixtureRoot(), "app", "ChatGPT.exe"));
                bool enabled = IsEnabled(args, 2);
                Environment.ExitCode = CodexLocalizationCompatibility.TryConfigure(executable, enabled, enabled, Console.WriteLine) ? 0 : 1;
                return;
            }

            if (EqualsCommand(command, "--compatibility-package-test"))
            {
                Environment.ExitCode = CompatibilityPackageTestRunner.Run(
                    GetArgument(args, 1, Path.Combine(Path.GetTempPath(), "CodexPortableManager-compatibility-package.txt")),
                    GetArgument(args, 2, Path.Combine(GetDefaultFixtureRoot(), "app", "resources", "app.asar")));
                return;
            }

            if (EqualsCommand(command, "--reasoning-display-package-test"))
            {
                Environment.ExitCode = CompatibilityPackageTestRunner.RunReasoningDisplay(
                    GetArgument(args, 1, Path.Combine(Path.GetTempPath(), "CodexPortableManager-reasoning-display-package.txt")),
                    GetArgument(args, 2, Path.Combine(GetDefaultFixtureRoot(), "app", "resources", "app.asar")));
                return;
            }

            if (EqualsCommand(command, "--pipeline-test"))
            {
                Environment.ExitCode = PipelineTestRunner.Run(
                    GetArgument(args, 1, Path.Combine(Path.GetTempPath(), "CodexPortableManager-pipeline-test.txt")),
                    GetArgument(args, 2, Path.Combine(Path.GetTempPath(), "CodexPortableManagerPipeline")));
                return;
            }

            if (EqualsCommand(command, "--staging-benchmark"))
            {
                Environment.ExitCode = StagingBenchmarkRunner.Run(
                    GetArgument(args, 1, Path.Combine(Path.GetTempPath(), "CodexPortableManager-staging-benchmark.txt")),
                    GetArgument(args, 2, string.Empty),
                    GetArgument(args, 3, Path.Combine(Path.GetTempPath(), "CodexPortableManagerStagingBenchmark")));
                return;
            }

            if (EqualsCommand(command, "--store-resolver-test"))
            {
                Environment.ExitCode = StorePackageSourceTestRunner.Run(
                    GetArgument(args, 1, Path.Combine(Path.GetTempPath(), "CodexPortableManager-store-resolver-test.txt")),
                    IsEnabled(args, 2));
                return;
            }

            if (EqualsCommand(command, "--msix-trust-test"))
            {
                Environment.ExitCode = MsixTrustTestRunner.Run(args);
                return;
            }

            if (EqualsCommand(command, "--regression-test") ||
                EqualsCommand(command, "--regression-child"))
            {
                Environment.ExitCode = RegressionTestRunner.Run(SliceArguments(args, 1));
                return;
            }

            if (EqualsCommand(command, "--hold-lock") ||
                EqualsCommand(command, "--save-config-part"))
            {
                Environment.ExitCode = RegressionTestRunner.Run(args);
                return;
            }

            Environment.ExitCode = 64;
        }

        private static bool EqualsCommand(string value, string expected)
        {
            return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetDefaultFixtureRoot()
        {
            return Path.Combine(Path.GetTempPath(), "CodexPortableManagerFixture", "CodexDesktop");
        }

        private static string GetArgument(string[] args, int index, string fallback)
        {
            return args.Length > index ? args[index] : fallback;
        }

        private static bool IsEnabled(string[] args, int index)
        {
            return args.Length <= index || !string.Equals(args[index], "off", StringComparison.OrdinalIgnoreCase);
        }

        private static double GetDoubleArgument(string[] args, int index)
        {
            double value;
            return args.Length > index && double.TryParse(args[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static string[] SliceArguments(string[] args, int startIndex)
        {
            if (args == null || startIndex >= args.Length) return new string[0];
            string[] result = new string[args.Length - startIndex];
            Array.Copy(args, startIndex, result, 0, result.Length);
            return result;
        }
    }
}
