using System;
using System.IO;

namespace CodexPortableManager
{
    internal sealed class CompatibilityBaselineApproval
    {
        private readonly string installRoot;

        private CompatibilityBaselineApproval(string approvedInstallRoot)
        {
            installRoot = Normalize(approvedInstallRoot);
        }

        public static CompatibilityBaselineApproval Create(string installRoot)
        {
            return new CompatibilityBaselineApproval(installRoot);
        }

        internal bool Covers(string candidateRoot)
        {
            return string.Equals(
                installRoot,
                Normalize(candidateRoot),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("兼容维护基线批准的安装根不能为空。", nameof(path));
            }

            string fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
            string root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
