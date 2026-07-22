using System;
using System.IO;

namespace CodexPortableManager
{
    internal sealed class LegacyAdoptionApproval
    {
        private readonly string installRoot;
        private readonly string previousRoot;

        private LegacyAdoptionApproval(string approvedInstallRoot)
        {
            installRoot = Normalize(approvedInstallRoot);
            previousRoot = installRoot + ".previous";
        }

        public static LegacyAdoptionApproval Create(string installRoot)
        {
            return new LegacyAdoptionApproval(installRoot);
        }

        internal bool Covers(string candidateRoot)
        {
            string normalized = Normalize(candidateRoot);
            return string.Equals(normalized, installRoot, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, previousRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("无标记目录接管批准的安装根不能为空。", nameof(path));
            }
            string fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
            string root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
