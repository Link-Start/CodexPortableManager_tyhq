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

namespace CodexPortableManager
{
internal static partial class RegressionTestRunner
{
    private static string FindProjectRoot()
    {
        DirectoryInfo current = new DirectoryInfo(Path.GetDirectoryName(managerPath));
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CodexPortableManager.csproj")) &&
                File.Exists(Path.Combine(current.FullName, "src", "App.xaml")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException(
            "无法从待测试管理器路径定位项目根目录：" + managerPath);
    }

    private static void RunLegacyMigration()
    {
        PortableStorage.MigrateLegacyCacheAsync(null, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private static bool BytesEqual(byte[] first, byte[] second)
    {
        if (first == null || second == null || first.Length != second.Length)
        {
            return false;
        }
        for (int index = 0; index < first.Length; index++)
        {
            if (first[index] != second[index])
            {
                return false;
            }
        }
        return true;
    }

    private static string BuildAsarEntryJson(string name, byte[] data, int offset)
    {
        string hash = ComputeSha256Hex(data);
        return "\"" + name + "\":{" +
            "\"size\":" + data.Length.ToString(CultureInfo.InvariantCulture) + "," +
            "\"offset\":\"" + offset.ToString(CultureInfo.InvariantCulture) + "\"," +
            "\"integrity\":{" +
            "\"algorithm\":\"SHA256\"," +
            "\"hash\":\"" + hash + "\"," +
            "\"blockSize\":4194304," +
            "\"blocks\":[\"" + hash + "\"]}}";
    }

    private static byte[] CombineBytes(params byte[][] parts)
    {
        int length = parts.Sum(part => part.Length);
        byte[] result = new byte[length];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        return result;
    }

    private static byte[] BuildTestAsar(string headerJson, byte[] payload)
    {
        byte[] json = Encoding.UTF8.GetBytes(headerJson);
        int paddedJsonSize = (json.Length + 3) & ~3;
        uint headerSize = checked((uint)(paddedJsonSize + 8));
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write((uint)4);
            writer.Write(headerSize);
            writer.Write(headerSize - 4);
            writer.Write((uint)json.Length);
            writer.Write(json);
            for (int index = json.Length; index < paddedJsonSize; index++) writer.Write((byte)0);
            writer.Write(payload);
            writer.Flush();
            return stream.ToArray();
        }
    }

    private static string ComputeSha256Hex(byte[] data)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            StringBuilder result = new StringBuilder(64);
            foreach (byte value in sha256.ComputeHash(data)) result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return result.ToString();
        }
    }

    private static void WithIsolatedLocalAppData(string name, Action action)
    {
        string container = Path.Combine(
            Path.GetTempPath(),
            "CodexPortableManager-" + name + "-" + Guid.NewGuid().ToString("N"));
        string profile = Path.Combine(container, "profile");
        string localAppData = Path.Combine(profile, "AppData", "Local");
        Directory.CreateDirectory(localAppData);
        string previousLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        string previousUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData);
            Environment.SetEnvironmentVariable("USERPROFILE", profile);
            string resolved = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            Assert(PathsEqual(resolved, localAppData),
                "无法把 LocalApplicationData 隔离到测试目录，已停止测试：" + resolved);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocalAppData);
            Environment.SetEnvironmentVariable("USERPROFILE", previousUserProfile);
            if (Directory.Exists(container))
            {
                Directory.Delete(container, true);
            }
        }
    }

    private static string CreateTimedFile(
        string root,
        string name,
        string contents,
        DateTime lastWriteUtc)
    {
        string path = Path.Combine(root, name);
        File.WriteAllText(path, contents, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static string CreateWorkDirectory(string parentRoot, string installRoot, string sentinel)
    {
        string workRoot = Path.Combine(parentRoot, ".cpm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        File.WriteAllText(Path.Combine(workRoot, "sentinel.txt"), sentinel, Encoding.UTF8);
        StorageMaintenance.WriteWorkMarker(workRoot, installRoot);
        return workRoot;
    }

    private static void RewriteWorkMarkerCreatedUtc(string workRoot, DateTime createdUtc)
    {
        string markerPath = Path.Combine(workRoot, ".codex-portable-manager-work.json");
        string json = File.ReadAllText(markerPath, Encoding.UTF8);
        string replacement = "\"CreatedUtc\":\"" + createdUtc.ToString("O", CultureInfo.InvariantCulture) + "\"";
        string updated = Regex.Replace(json, "\"CreatedUtc\"\\s*:\\s*\"[^\"]+\"", replacement);
        Assert(!string.Equals(json, updated, StringComparison.Ordinal), "无法改写测试工作目录 marker 时间。");
        File.WriteAllText(markerPath, updated, new UTF8Encoding(false));
    }

    private static bool PathsEqual(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }
        string normalizedFirst = Path.GetFullPath(first)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedSecond = Path.GetFullPath(second)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!(File.Exists(normalizedFirst) || Directory.Exists(normalizedFirst)) ||
            !(File.Exists(normalizedSecond) || Directory.Exists(normalizedSecond)))
        {
            return false;
        }
        try
        {
            return string.Equals(
                NativeFileSystem.GetStablePathForExistingPath(normalizedFirst),
                NativeFileSystem.GetStablePathForExistingPath(normalizedSecond),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPathUnderRoot(string candidatePath, string rootPath)
    {
        string candidate = Path.GetFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void RestoreOptionalFile(string path, byte[] previousContents)
    {
        if (previousContents == null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, previousContents);
    }

    private static string JsonEscape(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private static FileInfo FindOfficialCachedPackage(string cacheRoot)
    {
        if (!Directory.Exists(cacheRoot))
        {
            throw new DirectoryNotFoundException("没有找到正式缓存目录：" + cacheRoot);
        }
        FileInfo package = new DirectoryInfo(cacheRoot)
            .GetFiles("OpenAI.Codex_*.msix", SearchOption.TopDirectoryOnly)
            .Where(value => Regex.IsMatch(
                value.Name,
                @"^OpenAI\.Codex_[0-9]+(?:\.[0-9]+){3}_(x64|arm64)\.msix$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .OrderByDescending(value => value.LastWriteTimeUtc)
            .ThenByDescending(value => value.Length)
            .FirstOrDefault();
        if (package == null || package.Length <= 0)
        {
            throw new FileNotFoundException("没有找到可验证的正式 Codex MSIX 缓存。", cacheRoot);
        }
        return package;
    }

    private static string ComputeSha256Base64(string path)
    {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024))
        using (SHA256 sha256 = SHA256.Create())
        {
            return Convert.ToBase64String(sha256.ComputeHash(stream));
        }
    }

    private static PackageMetadata CreatePackageMetadata(
        string version,
        string fullName,
        string digest,
        long size)
    {
        return new PackageMetadata
        {
            version = version,
            packageName = "OpenAI.Codex",
            architecture = "x64",
            fullName = fullName,
            digest = digest,
            url = "https://example.invalid/official.msix",
            sizeInBytes = size
        };
    }

    private static CompatibilityOptions CreateCompatibilityOptions(
        bool sandbox,
        bool modelCatalog,
        bool chineseUi,
        bool englishParameters)
    {
        return new CompatibilityOptions(
            sandbox,
            modelCatalog,
            chineseUi,
            englishParameters);
    }

    private static void ExpectInvocationFailure(Action action, string message)
    {
        Exception failure = CaptureFailure(action);
        Assert(failure is InvalidDataException, message + " 实际异常：" + (failure == null ? "无" : failure.ToString()));
    }

    private static Exception CaptureFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return Unwrap(exception);
        }
    }

    private static void RecoverDeployment(string installRoot)
    {
        List<string> logs = new List<string>();
        using (DeploymentEngineScope scope = new DeploymentEngineScope(logs))
        {
            scope.Engine.RecoverInterruptedDeployment(
                installRoot,
                Directory.GetParent(installRoot).FullName);
        }
    }

    private static CodexPortableService CreateService(List<string> logs)
    {
        return new CodexPortableService(logs.Add);
    }

    private sealed class DeploymentEngineScope : IDisposable
    {
        private readonly ArtifactPipeline artifactPipeline;

        public DeploymentEngineScope(List<string> logs)
        {
            Action<string> log = logs.Add;
            artifactPipeline = new ArtifactPipeline(
                log,
                delegate { return Task.FromResult(new ProcessResult()); });
            Engine = new DeploymentEngine(
                log,
                artifactPipeline,
                new CompatibilityCoordinator(log),
                new ShellIntegrationCoordinator(log));
        }

        public DeploymentEngine Engine { get; private set; }

        public void Dispose()
        {
            artifactPipeline.Dispose();
        }
    }

    private static void CreateMinimalCodex(string root, string version, string installId, string identity)
    {
        string appRoot = Path.Combine(root, "app");
        Directory.CreateDirectory(appRoot);
        File.WriteAllBytes(Path.Combine(appRoot, "Codex.exe"), new byte[] { 0x4D, 0x5A, 0x43, 0x50, 0x4D });
        File.WriteAllText(Path.Combine(root, "identity.txt"), identity, Encoding.UTF8);

        string manifest =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">" +
            "<Identity Name=\"OpenAI.Codex\" Publisher=\"CN=OpenAI\" Version=\"" + version + "\" />" +
            "<Properties><DisplayName>Codex</DisplayName></Properties>" +
            "<Applications><Application Id=\"App\" Executable=\"app\\Codex.exe\" EntryPoint=\"Windows.FullTrustApplication\" /></Applications>" +
            "</Package>";
        File.WriteAllText(Path.Combine(root, "AppxManifest.xml"), manifest, new UTF8Encoding(false));

        string marker = string.Format(
            CultureInfo.InvariantCulture,
            "{{\"Identity\":{{\"InstallId\":\"{0}\",\"PackageName\":\"OpenAI.Codex\",\"PackageVersion\":\"{1}\"}},\"Provenance\":null,\"UpdatedUtc\":\"{2}\"}}",
            installId,
            version,
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        File.WriteAllText(Path.Combine(root, ".codex-portable-manager.json"), marker, new UTF8Encoding(false));
    }

    private static void CreateRunnableCodex(string root, string version, string installId, string identity)
    {
        CreateMinimalCodex(root, version, installId, identity);
        string resourcesRoot = Path.Combine(root, "app", "resources");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllText(Path.Combine(resourcesRoot, "app.asar"), "asar", Encoding.ASCII);
        File.WriteAllText(Path.Combine(resourcesRoot, "codex.exe"), "codex", Encoding.ASCII);
    }

    private static void SetPortableRegistryMarker(string registryPath, string installRoot)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(registryPath))
        {
            key.SetValue("CodexPortableInstallRoot", installRoot, RegistryValueKind.String);
        }
    }

    private static void AssertVersionAt(string root, string identity, string version)
    {
        Assert(Directory.Exists(root), "预期版本目录不存在：" + root);
        string identityPath = Path.Combine(root, "identity.txt");
        Assert(File.Exists(identityPath), "版本身份哨兵不存在：" + identityPath);
        Assert(File.ReadAllText(identityPath, Encoding.UTF8) == identity, "版本目录身份错误：" + root);
        string manifest = File.ReadAllText(Path.Combine(root, "AppxManifest.xml"), Encoding.UTF8);
        Assert(manifest.IndexOf("Version=\"" + version + "\"", StringComparison.Ordinal) >= 0, "版本清单内容错误：" + root);
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/d /c mklink /J " + QuoteArgument(linkPath) + " " + QuoteArgument(targetPath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using (Process process = Process.Start(startInfo))
        {
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new IOException("创建测试 junction 失败，退出码 " + process.ExitCode + "：" + output + error);
            }
        }
    }

    private static string QuoteArgument(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            return value;
        }

        StringBuilder result = new StringBuilder();
        result.Append('"');
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }
        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    private static string NewCaseRoot(string name)
    {
        string root = Path.Combine(suiteRoot, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string GetCrossProcessLockPath(string category, string key)
    {
        string categoryRoot = Path.Combine(PortableStorage.SharedLocksRoot, category);
        Directory.CreateDirectory(categoryRoot);
        return Path.Combine(categoryRoot, CrossProcessFileLock.ComputeKeyHash(key) + ".lock");
    }

    private sealed class HeldFileLock : IDisposable
    {
        private FileStream stream;

        public HeldFileLock(FileStream value)
        {
            stream = value ?? throw new ArgumentNullException(nameof(value));
        }

        public void Dispose()
        {
            FileStream value = stream;
            stream = null;
            if (value != null) value.Dispose();
        }
    }

    private static void ValidateTestRoot(string root)
    {
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("测试根目录必须位于 %TEMP% 内，已拒绝运行：" + root);
        }
    }

    private static MethodInfo RequireMethod(Type type, string name, BindingFlags flags, Type[] parameterTypes)
    {
        MethodInfo method = type.GetMethod(name, flags, null, parameterTypes, null);
        if (method == null)
        {
            throw new MissingMethodException(type.FullName, name);
        }
        return method;
    }

    private static Exception Unwrap(Exception exception)
    {
        Exception current = exception;
        while (current is TargetInvocationException && current.InnerException != null)
        {
            current = current.InnerException;
        }
        AggregateException aggregate = current as AggregateException;
        if (aggregate != null)
        {
            AggregateException flattened = aggregate.Flatten();
            if (flattened.InnerExceptions.Count == 1)
            {
                return Unwrap(flattened.InnerExceptions[0]);
            }
        }
        return current;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
}
