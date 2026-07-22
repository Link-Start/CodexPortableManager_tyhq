using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CodexPortableManager
{
    internal static class PackageProfileReader
    {
        private const string DefaultAppUserModelId = "com.openai.codex";

        public static PackageProfile Read(string installRoot)
        {
            string manifestPath = Path.Combine(installRoot, "AppxManifest.xml");
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException("没有找到 AppxManifest.xml。", manifestPath);
            }

            XDocument document = XDocument.Load(manifestPath, LoadOptions.None);
            return ReadDocument(document);
        }

        internal static PackageProfile Read(Stream manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            return ReadDocument(XDocument.Load(manifest, LoadOptions.None));
        }

        private static PackageProfile ReadDocument(XDocument document)
        {
            XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
            XElement root = document.Root;
            XElement identity = root == null ? null : root.Element(foundation + "Identity");
            XElement properties = root == null ? null : root.Element(foundation + "Properties");
            XElement applications = root == null ? null : root.Element(foundation + "Applications");
            XElement application = applications == null ? null : applications.Elements(foundation + "Application").FirstOrDefault();
            string executable = application == null ? null : (string)application.Attribute("Executable");
            if (identity == null || application == null || string.IsNullOrWhiteSpace(executable))
            {
                throw new InvalidDataException("AppxManifest.xml 缺少包身份或主应用信息。");
            }

            List<string> protocols = application.Descendants(uap + "Protocol")
                .Select(value => ((string)value.Attribute("Name") ?? string.Empty).Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<FileAssociationProfile> associations = new List<FileAssociationProfile>();
            foreach (XElement association in application.Descendants(uap + "FileTypeAssociation"))
            {
                List<string> extensions = association.Descendants(uap + "FileType")
                    .Select(value => (value.Value ?? string.Empty).Trim())
                    .Where(value => value.StartsWith(".", StringComparison.Ordinal) && value.Length > 1)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (extensions.Count > 0)
                {
                    associations.Add(new FileAssociationProfile
                    {
                        Name = ((string)association.Attribute("Name") ?? "file").Trim(),
                        Extensions = extensions
                    });
                }
            }

            return new PackageProfile
            {
                PackageName = (string)identity.Attribute("Name"),
                Version = (string)identity.Attribute("Version"),
                DisplayName = properties == null ? "Codex" : ((string)properties.Element(foundation + "DisplayName") ?? "Codex"),
                ExecutableRelativePath = executable.Replace('/', Path.DirectorySeparatorChar),
                AppUserModelId = DefaultAppUserModelId,
                Protocols = protocols,
                FileAssociations = associations
            };
        }

        public static string GetExecutablePath(string installRoot, PackageProfile profile)
        {
            string root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(installRoot, profile.ExecutableRelativePath));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("清单中的主程序路径越过安装目录边界。");
            }
            return path;
        }
    }
}
