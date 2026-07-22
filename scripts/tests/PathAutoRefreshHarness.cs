using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

internal static class PathAutoRefreshHarness
{
    private static int exitCode = 1;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("用法：PathAutoRefreshHarness.exe <manager.exe> <test-root> <report-path>");
            return 64;
        }

        string managerPath = Path.GetFullPath(args[0]);
        string testRoot = Path.GetFullPath(args[1]);
        string reportPath = Path.GetFullPath(args[2]);
        try
        {
            string validRoot = Path.Combine(testRoot, "valid");
            string emptyRoot = Path.Combine(testRoot, "empty");
            CreateRunnableCodex(validRoot, "9.8.7.6");
            Directory.CreateDirectory(emptyRoot);

            Application application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Assembly assembly = Assembly.LoadFrom(managerPath);
            Type windowType = assembly.GetType("CodexPortableManager.MainWindow", true, false);
            MethodInfo resolveFolderBrowserInitialPath = windowType.GetMethod(
                "ResolveFolderBrowserInitialPath",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert(resolveFolderBrowserInitialPath != null, "未找到目录选择器初始路径解析方法。");
            Assert(
                string.Equals(
                    InvokePathResolver(resolveFolderBrowserInitialPath, string.Empty),
                    string.Empty,
                    StringComparison.Ordinal),
                "空安装目录不应触发路径解析异常或指定初始目录。");
            Assert(
                string.Equals(
                    InvokePathResolver(resolveFolderBrowserInitialPath, "invalid\0path"),
                    string.Empty,
                    StringComparison.Ordinal),
                "非法安装目录不应触发路径解析异常或指定初始目录。");
            Assert(
                string.Equals(
                    InvokePathResolver(resolveFolderBrowserInitialPath, validRoot),
                    Path.GetFullPath(validRoot),
                    StringComparison.OrdinalIgnoreCase),
                "已存在的安装目录没有作为目录选择器初始位置。");
            Assert(
                string.Equals(
                    InvokePathResolver(resolveFolderBrowserInitialPath, Path.Combine(testRoot, "missing", "CodexDesktop")),
                    Path.GetFullPath(testRoot),
                    StringComparison.OrdinalIgnoreCase),
                "尚未创建的安装目录没有回退到最近的现有父目录。");
            Window window = (Window)Activator.CreateInstance(
                windowType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new object[] { false },
                CultureInfo.InvariantCulture);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.ShowInTaskbar = false;

            TextBox pathTextBox = (TextBox)GetField(windowType, "installPathTextBox").GetValue(window);
            TextBlock portableLabel = (TextBlock)GetField(windowType, "portableValueLabel").GetValue(window);
            TextBlock applicationLabel = (TextBlock)GetField(windowType, "portableApplicationValueLabel").GetValue(window);
            TextBlock progressLabel = (TextBlock)GetField(windowType, "progressLabel").GetValue(window);
            Button launchButton = (Button)GetField(windowType, "launchButton").GetValue(window);

            window.Loaded += async delegate
            {
                try
                {
                    pathTextBox.Text = validRoot;
                    await Task.Delay(1400);
                    Assert(portableLabel.Text == "9.8.7.6", "有效目录没有自动显示版本：" + portableLabel.Text);
                    Assert(applicationLabel.Text == "9.8.76123", "有效目录没有自动显示应用内部版本：" + applicationLabel.Text);
                    Assert(launchButton.IsEnabled, "有效目录自动检测后启动按钮没有启用。");
                    Assert(progressLabel.Text.IndexOf("已检测到", StringComparison.Ordinal) >= 0,
                        "有效目录没有显示自动检测完成提示：" + progressLabel.Text);

                    pathTextBox.Text = emptyRoot;
                    await Task.Delay(1400);
                    Assert(portableLabel.Text == "未安装", "空目录没有自动显示未安装：" + portableLabel.Text);
                    Assert(applicationLabel.Text == "未安装", "空目录的应用内部版本状态错误：" + applicationLabel.Text);
                    Assert(!launchButton.IsEnabled, "空目录自动检测后启动按钮仍然启用。");
                    Assert(progressLabel.Text.IndexOf("未检测到", StringComparison.Ordinal) >= 0,
                        "空目录没有显示自动检测结果：" + progressLabel.Text);

                    pathTextBox.Text = string.Empty;
                    await Task.Delay(1400);
                    Assert(portableLabel.Text == "未选择", "空安装目录没有显示未选择：" + portableLabel.Text);
                    Assert(applicationLabel.Text == "未选择", "空安装目录的应用版本状态错误：" + applicationLabel.Text);
                    Assert(!launchButton.IsEnabled, "空安装目录仍然启用了启动按钮。");
                    Assert(progressLabel.Text.IndexOf("尚未选择", StringComparison.Ordinal) >= 0,
                        "空安装目录没有显示选择提示：" + progressLabel.Text);

                    File.WriteAllText(
                        reportPath,
                        "RESULT=PASS" + Environment.NewLine +
                        "VALID_VERSION=9.8.7.6" + Environment.NewLine +
                        "VALID_APPLICATION_VERSION=9.8.76123" + Environment.NewLine +
                        "EMPTY_STATUS=未安装" + Environment.NewLine +
                        "BLANK_STATUS=未选择" + Environment.NewLine,
                        new UTF8Encoding(true));
                    exitCode = 0;
                }
                catch (Exception exception)
                {
                    File.WriteAllText(reportPath, "RESULT=FAIL" + Environment.NewLine + exception, new UTF8Encoding(true));
                }
                finally
                {
                    window.Close();
                    application.Shutdown();
                }
            };
            application.Run(window);
            return exitCode;
        }
        catch (Exception exception)
        {
            File.WriteAllText(reportPath, "RESULT=FAIL" + Environment.NewLine + exception, new UTF8Encoding(true));
            return 1;
        }
    }

    private static FieldInfo GetField(Type type, string name)
    {
        FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) throw new MissingFieldException(type.FullName, name);
        return field;
    }

    private static string InvokePathResolver(MethodInfo method, string path)
    {
        return (string)method.Invoke(null, new object[] { path });
    }

    private static void CreateRunnableCodex(string root, string version)
    {
        string appRoot = Path.Combine(root, "app");
        string resourcesRoot = Path.Combine(appRoot, "resources");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(Path.Combine(appRoot, "Codex.exe"), new byte[] { 0x4D, 0x5A, 0x01 });
        WriteMinimalAsar(
            Path.Combine(resourcesRoot, "app.asar"),
            "{\"name\":\"openai-codex-electron\",\"version\":\"9.8.76123\"}");
        File.WriteAllText(Path.Combine(resourcesRoot, "codex.exe"), "codex", Encoding.ASCII);

        string manifest =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">" +
            "<Identity Name=\"OpenAI.Codex\" Publisher=\"CN=OpenAI\" Version=\"" + version + "\" />" +
            "<Properties><DisplayName>Codex</DisplayName></Properties>" +
            "<Applications><Application Id=\"App\" Executable=\"app\\Codex.exe\" EntryPoint=\"Windows.FullTrustApplication\" /></Applications>" +
            "</Package>";
        File.WriteAllText(Path.Combine(root, "AppxManifest.xml"), manifest, new UTF8Encoding(false));
    }

    private static void WriteMinimalAsar(string path, string packageJson)
    {
        byte[] packageBytes = Encoding.UTF8.GetBytes(packageJson);
        string headerJson =
            "{\"files\":{\"package.json\":{\"size\":" +
            packageBytes.Length.ToString(CultureInfo.InvariantCulture) +
            ",\"offset\":\"0\"}}}";
        byte[] headerBytes = Encoding.UTF8.GetBytes(headerJson);
        int paddedHeaderSize = (headerBytes.Length + 3) & ~3;
        uint headerSize = checked((uint)(paddedHeaderSize + 8));
        using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write((uint)4);
            writer.Write(headerSize);
            writer.Write(headerSize - 4);
            writer.Write((uint)paddedHeaderSize);
            writer.Write(headerBytes);
            for (int index = headerBytes.Length; index < paddedHeaderSize; index++)
            {
                writer.Write((byte)' ');
            }
            writer.Write(packageBytes);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
