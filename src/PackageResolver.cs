using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPortableManager
{
    internal sealed class PackageResolver : IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly CodexMicrosoftStoreSource packageSource;
        private readonly Action<string> log;

        internal PackageResolver(Action<string> logAction)
            : this(logAction, new HttpClientHandler { AllowAutoRedirect = false })
        {
        }

        internal PackageResolver(Action<string> logAction, HttpMessageHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            log = logAction ?? delegate { };
            httpClient = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CodexPortableManager/1.1.0");
            packageSource = new CodexMicrosoftStoreSource(new MicrosoftStoreProtocolClient(httpClient));
        }

        internal async Task<PackageMetadata> ResolveLatestAsync(CancellationToken cancellationToken)
        {
            log("正在查询微软商店 ChatGPT 程序包目录。");
            PackageMetadata package = await packageSource.ResolveLatestAsync(cancellationToken).ConfigureAwait(false);
            log(string.Format(
                CultureInfo.InvariantCulture,
                "微软最新版本：{0}，架构：{1}，程序包大小：{2:F1} MiB。",
                package.version,
                package.architecture,
                package.sizeInBytes / 1048576d));
            return package;
        }

        public void Dispose()
        {
            httpClient.Dispose();
        }
    }
}
