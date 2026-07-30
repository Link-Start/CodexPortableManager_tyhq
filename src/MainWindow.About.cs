using System;
using System.Diagnostics;
using System.Windows;

namespace CodexPortableManager
{
    internal sealed partial class MainWindow
    {
        private const string ProjectUrl = "https://github.com/tyhq/CodexPortableManager";
        private const string IssueTrackerUrl = ProjectUrl + "/issues";
        private const string LicenseUrl = ProjectUrl + "/blob/main/LICENSE";
        private const string ThirdPartyNoticesUrl = ProjectUrl + "/blob/main/THIRD_PARTY_NOTICES.md";
        private const string QqGroupNumber = "535990598";

        private void InitializeAboutPage()
        {
            managerIconImage.Source = Icon;
            Version version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            managerVersionLabel.Text = version == null
                ? "版本未知"
                : "版本 " + version.Major + "." + version.Minor + "." + version.Build;
        }

        private void WireAboutEvents()
        {
            openProjectButton.Click += (sender, args) => OpenAboutUrl(ProjectUrl, "项目主页");
            reportIssueButton.Click += (sender, args) => OpenAboutUrl(IssueTrackerUrl, "问题反馈页面");
            copyQqGroupButton.Click += (sender, args) => CopyQqGroupNumber();
            openLicenseButton.Click += (sender, args) => OpenAboutUrl(LicenseUrl, "开源许可证");
            openThirdPartyNoticesButton.Click += (sender, args) => OpenAboutUrl(ThirdPartyNoticesUrl, "第三方声明");
        }

        private void OpenAboutUrl(string url, string destinationName)
        {
            Uri target;
            if (!Uri.TryCreate(url, UriKind.Absolute, out target) ||
                !string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                aboutActionStatusLabel.Text = "链接无效，未打开。";
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = target.AbsoluteUri,
                    UseShellExecute = true
                });
                aboutActionStatusLabel.Text = "已在浏览器中打开" + destinationName + "。";
            }
            catch (Exception exception)
            {
                aboutActionStatusLabel.Text = "无法打开" + destinationName + "。";
                MessageBox.Show(
                    this,
                    "无法打开浏览器：" + exception.Message,
                    "打开失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void CopyQqGroupNumber()
        {
            try
            {
                Clipboard.SetText(QqGroupNumber);
                aboutActionStatusLabel.Text = "QQ 群号 " + QqGroupNumber + " 已复制。";
            }
            catch (Exception exception)
            {
                aboutActionStatusLabel.Text = "无法复制 QQ 群号。";
                MessageBox.Show(
                    this,
                    "无法写入剪贴板：" + exception.Message,
                    "复制失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

    }
}
