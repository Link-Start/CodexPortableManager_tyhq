using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace CodexPortableManager
{
    internal sealed class StorePackageRegistration
    {
        internal string Name { get; set; }
        internal string FamilyName { get; set; }
        internal string FullName { get; set; }
        internal string InstallLocation { get; set; }
    }

    internal interface IStorePackageGateway
    {
        IReadOnlyList<StorePackageRegistration> FindPackagesForCurrentUser(string packageName);
        Task RemovePackageForCurrentUserAsync(string packageFullName, CancellationToken cancellationToken);
    }

    internal sealed class WindowsStorePackageGateway : IStorePackageGateway
    {
        public IReadOnlyList<StorePackageRegistration> FindPackagesForCurrentUser(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName)) throw new ArgumentException("程序包名称不能为空。", nameof(packageName));

            List<StorePackageRegistration> registrations = new List<StorePackageRegistration>();
            PackageManager packageManager = new PackageManager();
            foreach (Package package in packageManager.FindPackagesForUser(string.Empty, packageName))
            {
                string installLocation = null;
                try
                {
                    if (package.InstalledLocation != null) installLocation = package.InstalledLocation.Path;
                }
                catch
                {
                    // 包登记可能残缺；卸载仍可仅凭完整包名继续。
                }

                registrations.Add(new StorePackageRegistration
                {
                    Name = package.Id.Name,
                    FamilyName = package.Id.FamilyName,
                    FullName = package.Id.FullName,
                    InstallLocation = installLocation
                });
            }
            return registrations;
        }

        public async Task RemovePackageForCurrentUserAsync(
            string packageFullName,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(packageFullName))
            {
                throw new ArgumentException("完整包名不能为空。", nameof(packageFullName));
            }

            PackageManager packageManager = new PackageManager();
            Windows.Management.Deployment.DeploymentResult result = await packageManager
                .RemovePackageAsync(packageFullName)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            Exception deploymentError = result == null ? null : result.ExtendedErrorCode;
            string errorText = result == null ? null : result.ErrorText;
            if (deploymentError != null || !string.IsNullOrWhiteSpace(errorText))
            {
                string message = !string.IsNullOrWhiteSpace(errorText)
                    ? errorText.Trim()
                    : deploymentError.Message;
                throw new InvalidOperationException("Windows 包部署服务返回错误：" + message, deploymentError);
            }
        }
    }

    internal sealed class StorePackageLifecycle
    {
        private readonly IStorePackageGateway gateway;
        private readonly Action<string> stopProcesses;
        private readonly Action<string, TimeSpan> waitForProcesses;
        private readonly Action<string> log;

        internal StorePackageLifecycle(Action<string> logAction)
            : this(
                new WindowsStorePackageGateway(),
                ProcessesUnderPath.Stop,
                ProcessesUnderPath.WaitForExit,
                logAction)
        {
        }

        internal StorePackageLifecycle(
            IStorePackageGateway packageGateway,
            Action<string> stopProcessesAction,
            Action<string, TimeSpan> waitForProcessesAction,
            Action<string> logAction)
        {
            gateway = packageGateway ?? throw new ArgumentNullException(nameof(packageGateway));
            stopProcesses = stopProcessesAction ?? throw new ArgumentNullException(nameof(stopProcessesAction));
            waitForProcesses = waitForProcessesAction ?? throw new ArgumentNullException(nameof(waitForProcessesAction));
            log = logAction ?? delegate { };
        }

        internal async Task<bool> IsInstalledAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<StorePackageRegistration> packages = await FindTrustedPackagesAsync(cancellationToken)
                .ConfigureAwait(false);
            return packages.Count > 0;
        }

        internal async Task UninstallAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<StorePackageRegistration> packages = await FindTrustedPackagesAsync(cancellationToken)
                .ConfigureAwait(false);
            if (packages.Count == 0)
            {
                log("未检测到官方桌面版，无需卸载。");
                return;
            }

            foreach (StorePackageRegistration package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(package.InstallLocation))
                {
                    stopProcesses(package.InstallLocation);
                    waitForProcesses(package.InstallLocation, TimeSpan.FromSeconds(5));
                }

                try
                {
                    await gateway.RemovePackageForCurrentUserAsync(package.FullName, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        "卸载官方桌面版程序包失败（" + package.FullName + "）：" + exception.Message,
                        exception);
                }
            }

            log("官方桌面版 OpenAI.Codex 已卸载。");
        }

        private Task<IReadOnlyList<StorePackageRegistration>> FindTrustedPackagesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run<IReadOnlyList<StorePackageRegistration>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<StorePackageRegistration> packages =
                    gateway.FindPackagesForCurrentUser(CodexMicrosoftStoreSource.PackageName) ??
                    new StorePackageRegistration[0];
                cancellationToken.ThrowIfCancellationRequested();
                return packages
                    .Where(IsTrustedCodexPackage)
                    .OrderBy(package => package.FullName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }, cancellationToken);
        }

        private static bool IsTrustedCodexPackage(StorePackageRegistration package)
        {
            return package != null &&
                string.Equals(package.Name, CodexMicrosoftStoreSource.PackageName, StringComparison.Ordinal) &&
                string.Equals(package.FamilyName, CodexMicrosoftStoreSource.PackageFamilyName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(package.FullName);
        }
    }
}
