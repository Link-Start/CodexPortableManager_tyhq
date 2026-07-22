using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace CodexPortableManager
{
    internal sealed class MicrosoftStoreProtocolClient
    {
        private const string DisplayCatalogEndpoint = "https://displaycatalog.mp.microsoft.com/v7.0/products/";
        private const int MaximumCatalogResponseBytes = 4 * 1024 * 1024;
        private const int MaximumCookieResponseBytes = 1024 * 1024;
        private const int MaximumSyncResponseBytes = 32 * 1024 * 1024;
        private const int MaximumLocationResponseBytes = 4 * 1024 * 1024;
        private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
        private static readonly DeliveryEndpoint[] DeliveryEndpoints =
        {
            new DeliveryEndpoint("FE3", "https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx"),
            new DeliveryEndpoint("FE6", "https://fe6.delivery.mp.microsoft.com/ClientWebService/client.asmx"),
            new DeliveryEndpoint("FE6CR", "https://fe6cr.delivery.mp.microsoft.com/ClientWebService/client.asmx")
        };
        private static readonly string[] InstalledUpdateBaseline =
        {
            "1", "2", "3", "11", "19", "544", "549", "2359974", "2359977", "5169044",
            "8788830", "23110993", "23110994", "54341900", "54343656", "59830006", "59830007",
            "59830008", "60484010", "62450018", "62450019", "62450020", "66027979", "66053150",
            "97657898", "98822896", "98959022", "98959023", "98959024", "98959025", "98959026",
            "104433538", "104900364", "105489019", "117765322", "129905029", "130040031",
            "132387090", "132393049", "133399034", "138537048", "140377312", "143747671",
            "158941041", "158941042", "158941043", "158941044", "159123858", "159130928",
            "164836897", "164847386", "164848327", "164852241", "164852246", "164852252", "164852253"
        };

        private static readonly XNamespace SoapNamespace = "http://www.w3.org/2003/05/soap-envelope";
        private static readonly XNamespace AddressingNamespace = "http://www.w3.org/2005/08/addressing";
        private static readonly XNamespace SecurityNamespace = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
        private static readonly XNamespace SecurityUtilityNamespace = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
        private static readonly XNamespace AuthorizationNamespace = "http://schemas.microsoft.com/msus/2014/10/WindowsUpdateAuthorization";
        private static readonly XNamespace ServiceNamespace = "http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService";
        private readonly HttpClient httpClient;
        private readonly TimeSpan initialRetryDelay;
        private readonly TimeSpan requestTimeout;

        internal MicrosoftStoreProtocolClient(HttpClient client)
            : this(client, HttpRetryPolicy.DefaultInitialDelay)
        {
        }

        internal MicrosoftStoreProtocolClient(HttpClient client, TimeSpan retryDelay)
            : this(client, retryDelay, DefaultRequestTimeout)
        {
        }

        internal MicrosoftStoreProtocolClient(
            HttpClient client,
            TimeSpan retryDelay,
            TimeSpan metadataRequestTimeout)
        {
            httpClient = client ?? throw new ArgumentNullException(nameof(client));
            initialRetryDelay = retryDelay < TimeSpan.Zero ? TimeSpan.Zero : retryDelay;
            if (metadataRequestTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(metadataRequestTimeout),
                    "元数据请求超时必须大于零。");
            }
            requestTimeout = metadataRequestTimeout;
        }

        internal async Task<CatalogProduct> GetCatalogProductAsync(
            string productId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(productId))
            {
                throw new ArgumentException("微软商店 ProductId 不能为空。", nameof(productId));
            }

            Uri endpoint = new Uri(
                DisplayCatalogEndpoint + Uri.EscapeDataString(productId) + "?market=US&languages=en-US",
                UriKind.Absolute);
            return await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, endpoint),
                endpoint,
                MaximumCatalogResponseBytes,
                "微软商店目录",
                (response, json) =>
                {
                    EnsureSuccessfulStatus(response, "微软商店目录");
                    return ParseCatalogResponse(json, productId);
                },
                cancellationToken).ConfigureAwait(false);
        }

        internal async Task<DeliveryFile> ResolvePackageFileAsync(
            string categoryId,
            string expectedFullName,
            string architecture,
            CancellationToken cancellationToken)
        {
            Guid parsedCategoryId;
            if (!Guid.TryParse(categoryId, out parsedCategoryId))
            {
                throw new ArgumentException("Windows Update 类别 ID 无效。", nameof(categoryId));
            }
            if (string.IsNullOrWhiteSpace(expectedFullName))
            {
                throw new ArgumentException("目标程序包完整名称不能为空。", nameof(expectedFullName));
            }
            architecture = NormalizeArchitecture(architecture);

            List<Exception> failures = new List<Exception>();
            bool publicationPending = false;
            foreach (DeliveryEndpoint endpoint in DeliveryEndpoints)
            {
                try
                {
                    return await ResolvePackageFileFromEndpointAsync(
                        endpoint,
                        parsedCategoryId.ToString("D"),
                        expectedFullName,
                        architecture,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (MicrosoftStoreUpdateNotFoundException exception)
                {
                    publicationPending = true;
                    failures.Add(new InvalidDataException(endpoint.Name + " 尚未同步目标程序包。", exception));
                }
                catch (TransientHttpRequestException exception)
                {
                    failures.Add(new HttpRequestException(endpoint.Name + " 暂时不可用：" + exception.Message, exception));
                }
            }

            if (publicationPending)
            {
                throw new MicrosoftStorePublicationPendingException(
                    "微软正在同步目标程序包到 Windows Update 分发端点，请稍后重试。",
                    new AggregateException(failures));
            }
            throw new TransientHttpRequestException(
                "所有 Windows Update 分发端点均暂时不可用。",
                null,
                null,
                new AggregateException(failures));
        }

        internal Task<DeliveryFile> ResolvePackageFileUsingEndpointAsync(
            string endpointName,
            string categoryId,
            string expectedFullName,
            string architecture,
            CancellationToken cancellationToken)
        {
            Guid parsedCategoryId;
            if (!Guid.TryParse(categoryId, out parsedCategoryId))
            {
                throw new ArgumentException("Windows Update 类别 ID 无效。", nameof(categoryId));
            }
            if (string.IsNullOrWhiteSpace(expectedFullName))
            {
                throw new ArgumentException("目标程序包完整名称不能为空。", nameof(expectedFullName));
            }
            DeliveryEndpoint endpoint = DeliveryEndpoints.FirstOrDefault(value =>
                string.Equals(value.Name, endpointName, StringComparison.OrdinalIgnoreCase));
            if (endpoint == null)
            {
                throw new ArgumentException("不支持的 Windows Update 分发端点：" + endpointName, nameof(endpointName));
            }
            return ResolvePackageFileFromEndpointAsync(
                endpoint,
                parsedCategoryId.ToString("D"),
                expectedFullName,
                NormalizeArchitecture(architecture),
                cancellationToken);
        }

        private async Task<DeliveryFile> ResolvePackageFileFromEndpointAsync(
            DeliveryEndpoint endpoint,
            string categoryId,
            string expectedFullName,
            string architecture,
            CancellationToken cancellationToken)
        {
            StoreCookie cookie = await GetCookieAsync(endpoint, cancellationToken).ConfigureAwait(false);
            string syncResponse = await PostSoapAsync(
                endpoint.BaseUri,
                CreateSyncRequest(cookie, categoryId, architecture, endpoint.BaseUri.AbsoluteUri),
                MaximumSyncResponseBytes,
                endpoint.Name + " Windows Update 同步响应",
                cancellationToken).ConfigureAwait(false);
            DeliveryFile update = ParseSyncResponse(syncResponse, expectedFullName);

            string locationResponse = await PostSoapAsync(
                endpoint.SecuredUri,
                CreateFileLocationRequest(
                    update.UpdateId,
                    update.RevisionNumber,
                    architecture,
                    endpoint.SecuredUri.AbsoluteUri),
                MaximumLocationResponseBytes,
                endpoint.Name + " Windows Update 下载地址响应",
                cancellationToken).ConfigureAwait(false);
            update.Url = ParseFileLocationResponse(locationResponse, update.FileDigest);
            return update;
        }

        internal static CatalogProduct ParseCatalogResponse(
            string json,
            string expectedProductId)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException("微软商店目录返回了空响应。");
            }
            if (string.IsNullOrWhiteSpace(expectedProductId))
            {
                throw new ArgumentException("微软商店 ProductId 不能为空。", nameof(expectedProductId));
            }

            StoreCatalogEnvelope envelope;
            try
            {
                envelope = new JavaScriptSerializer
                {
                    MaxJsonLength = 4 * 1024 * 1024,
                    RecursionLimit = 128
                }.Deserialize<StoreCatalogEnvelope>(json);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("无法解析微软商店目录响应。", exception);
            }

            StoreCatalogProduct product = envelope == null ? null : envelope.Product;
            if (product == null || !string.Equals(product.ProductId, expectedProductId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("微软商店目录返回了不匹配的产品身份。");
            }

            CatalogProduct result = new CatalogProduct
            {
                ProductId = product.ProductId,
                PackageIdentityName = product.Properties == null ? null : product.Properties.PackageIdentityName,
                PackageFamilyName = product.Properties == null ? null : product.Properties.PackageFamilyName,
                Packages = new List<CatalogPackage>()
            };
            foreach (StoreCatalogAvailability availability in product.DisplaySkuAvailabilities ?? new List<StoreCatalogAvailability>())
            {
                StoreCatalogSkuProperties properties = availability == null || availability.Sku == null
                    ? null
                    : availability.Sku.Properties;
                string categoryId = properties == null || properties.FulfillmentData == null
                    ? null
                    : properties.FulfillmentData.WuCategoryId;

                foreach (StoreCatalogPackage package in
                    (properties == null ? null : properties.Packages) ?? new List<StoreCatalogPackage>())
                {
                    if (package == null)
                    {
                        continue;
                    }

                    CatalogPackage parsedPackage = new CatalogPackage
                    {
                        WuCategoryId = categoryId,
                        Architectures = package.Architectures == null
                            ? new List<string>()
                            : new List<string>(package.Architectures),
                        Hash = package.Hash,
                        HashAlgorithm = package.HashAlgorithm,
                        MaxDownloadSizeInBytes = package.MaxDownloadSizeInBytes,
                        PackageFormat = package.PackageFormat,
                        PackageFullName = package.PackageFullName,
                        PlatformDependencies = new List<CatalogPlatformDependency>()
                    };
                    foreach (StoreCatalogPlatformDependency dependency in package.PlatformDependencies ?? new List<StoreCatalogPlatformDependency>())
                    {
                        if (dependency == null) continue;
                        parsedPackage.PlatformDependencies.Add(new CatalogPlatformDependency
                        {
                            PlatformName = dependency.PlatformName,
                            MinVersion = dependency.MinVersion,
                            MaxTested = dependency.MaxTested
                        });
                    }
                    result.Packages.Add(parsedPackage);
                }
            }
            return result;
        }

        internal static DeliveryFile ParseSyncResponse(string responseXml, string expectedFullName)
        {
            XDocument response = ParseXml(responseXml, "Windows Update 同步响应");
            foreach (XElement updateInfo in response.Descendants().Where(element => element.Name.LocalName == "UpdateInfo"))
            {
                XElement embedded = GetEmbeddedXml(updateInfo);
                XElement appxMetadata = embedded.Descendants().FirstOrDefault(element => element.Name.LocalName == "AppxMetadata");
                if (appxMetadata == null ||
                    !string.Equals((string)appxMetadata.Attribute("PackageMoniker"), expectedFullName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                XElement identity = embedded.Elements().FirstOrDefault(element => element.Name.LocalName == "UpdateIdentity");
                string numericId = GetDirectChildValue(updateInfo, "ID");
                string updateId = identity == null ? null : (string)identity.Attribute("UpdateID");
                string revision = identity == null ? null : (string)identity.Attribute("RevisionNumber");
                if (string.IsNullOrWhiteSpace(numericId) || !Guid.TryParse(updateId, out _) || !IsPositiveInteger(revision))
                {
                    throw new InvalidDataException("Windows Update 返回的目标程序包更新身份无效。");
                }

                XElement extended = response.Descendants()
                    .Where(element => element.Name.LocalName == "Update")
                    .FirstOrDefault(element => string.Equals(GetDirectChildValue(element, "ID"), numericId, StringComparison.Ordinal));
                XElement extendedXml = GetEmbeddedXml(extended);
                XElement file = extendedXml.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName == "File" &&
                    string.Equals((string)element.Attribute("InstallerSpecificIdentifier"), expectedFullName, StringComparison.OrdinalIgnoreCase));
                if (file == null)
                {
                    throw new InvalidDataException("Windows Update 没有返回目标程序包文件元数据。");
                }

                long size;
                XElement sha256 = file.Elements().FirstOrDefault(element =>
                    element.Name.LocalName == "AdditionalDigest" &&
                    string.Equals((string)element.Attribute("Algorithm"), "SHA256", StringComparison.OrdinalIgnoreCase));
                string fileDigest = (string)file.Attribute("Digest");
                string sha256Digest = sha256 == null ? null : sha256.Value;
                if (!long.TryParse((string)file.Attribute("Size"), NumberStyles.None, CultureInfo.InvariantCulture, out size) ||
                    size <= 0 || !IsBase64Digest(fileDigest, 20) || !IsBase64Digest(sha256Digest, 32))
                {
                    throw new InvalidDataException("Windows Update 返回的目标程序包摘要或大小无效。");
                }

                return new DeliveryFile
                {
                    UpdateId = updateId,
                    RevisionNumber = revision,
                    FileDigest = fileDigest,
                    Sha256Digest = sha256Digest,
                    SizeInBytes = size
                };
            }
            throw new MicrosoftStoreUpdateNotFoundException(
                "Windows Update 没有返回与目标程序包匹配的更新。");
        }

        internal static string ParseFileLocationResponse(string responseXml, string expectedFileDigest)
        {
            XDocument response = ParseXml(responseXml, "Windows Update 下载地址响应");
            foreach (XElement location in response.Descendants().Where(element => element.Name.LocalName == "FileLocation"))
            {
                string digest = GetDirectChildValue(location, "FileDigest");
                string url = GetDirectChildValue(location, "Url");
                if (!string.Equals(digest, expectedFileDigest, StringComparison.Ordinal)) continue;

                Uri parsed;
                if (!Uri.TryCreate(url, UriKind.Absolute, out parsed) ||
                    !IsMicrosoftDeliveryUri(parsed))
                {
                    throw new InvalidDataException("Windows Update 返回了不受支持的程序包下载地址。");
                }
                return parsed.AbsoluteUri;
            }
            throw new InvalidDataException("Windows Update 没有返回与目标程序包摘要匹配的下载地址。");
        }

        private async Task<StoreCookie> GetCookieAsync(
            DeliveryEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            string responseXml = await PostSoapAsync(
                endpoint.BaseUri,
                CreateCookieRequest(endpoint.BaseUri.AbsoluteUri),
                MaximumCookieResponseBytes,
                endpoint.Name + " Windows Update Cookie 响应",
                cancellationToken).ConfigureAwait(false);
            XDocument response = ParseXml(responseXml, "Windows Update Cookie 响应");
            XElement result = response.Descendants().FirstOrDefault(element => element.Name.LocalName == "GetCookieResult");
            string expiration = result == null ? null : GetDirectChildValue(result, "Expiration");
            string encryptedData = result == null ? null : GetDirectChildValue(result, "EncryptedData");
            if (string.IsNullOrWhiteSpace(expiration) || string.IsNullOrWhiteSpace(encryptedData))
            {
                throw new InvalidDataException("Windows Update 没有返回有效的同步 Cookie。");
            }
            return new StoreCookie { Expiration = expiration, EncryptedData = encryptedData };
        }

        private async Task<string> PostSoapAsync(
            Uri endpoint,
            XDocument document,
            int maximumResponseBytes,
            string description,
            CancellationToken cancellationToken)
        {
            string body = document.ToString(SaveOptions.DisableFormatting);
            return await SendWithRetryAsync(
                () =>
                {
                    HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    request.Content = new StringContent(body, Encoding.UTF8, "application/soap+xml");
                    request.Headers.Accept.ParseAdd("application/soap+xml");
                    request.Headers.TryAddWithoutValidation("User-Agent", "Microsoft-Delivery-Optimization/10.0");
                    return request;
                },
                endpoint,
                maximumResponseBytes,
                description,
                (response, responseText) =>
                {
                    ThrowIfSoapFault(responseText, description);
                    EnsureSuccessfulStatus(response, description);
                    return responseText;
                },
                cancellationToken).ConfigureAwait(false);
        }

        private static XDocument CreateCookieRequest(string endpoint)
        {
            DateTime now = DateTime.UtcNow;
            return CreateEnvelope(
                "GetCookie",
                endpoint,
                true,
                new XElement(ServiceNamespace + "GetCookie",
                    new XElement(ServiceNamespace + "oldCookie"),
                    new XElement(ServiceNamespace + "lastChange", "2015-10-21T17:01:07.1472913Z"),
                    new XElement(ServiceNamespace + "currentTime", FormatTime(now)),
                    new XElement(ServiceNamespace + "protocolVersion", "1.40")),
                now);
        }

        private static XDocument CreateSyncRequest(
            StoreCookie cookie,
            string categoryId,
            string architecture,
            string endpoint)
        {
            DateTime now = DateTime.UtcNow;
            Version osVersion = Environment.OSVersion.Version;
            string version = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.{1}.{2}.{3}",
                osVersion.Major,
                osVersion.Minor,
                osVersion.Build,
                Math.Max(0, osVersion.Revision));
            string deviceAttributes =
                "BranchReadinessLevel=CB;CurrentBranch=vb_release;InstallLanguage=en-US;OSUILocale=en-US;" +
                "InstallationType=Client;FlightingBranchName=external;FlightContent=Branch;App=WU;" +
                "AppVer=" + version + ";OSArchitecture=" + (architecture == "arm64" ? "ARM64" : "AMD64") + ";" +
                "IsFlightingEnabled=0;IsDeviceRetailDemo=0;TelemetryLevel=1;OSVersion=" + version + ";" +
                "DeviceFamily=Windows.Desktop;";

            XElement installed = new XElement(ServiceNamespace + "InstalledNonLeafUpdateIDs",
                InstalledUpdateBaseline.Select(value => new XElement(ServiceNamespace + "int", value)));
            XElement parameters = new XElement(ServiceNamespace + "parameters",
                new XElement(ServiceNamespace + "ExpressQuery", "false"),
                installed,
                new XElement(ServiceNamespace + "OtherCachedUpdateIDs"),
                new XElement(ServiceNamespace + "SkipSoftwareSync", "false"),
                new XElement(ServiceNamespace + "NeedTwoGroupOutOfScopeUpdates", "true"),
                new XElement(ServiceNamespace + "FilterAppCategoryIds",
                    new XElement(ServiceNamespace + "CategoryIdentifier",
                        new XElement(ServiceNamespace + "Id", categoryId))),
                new XElement(ServiceNamespace + "TreatAppCategoryIdsAsInstalled", "true"),
                new XElement(ServiceNamespace + "AlsoPerformRegularSync", "false"),
                new XElement(ServiceNamespace + "ComputerSpec"),
                new XElement(ServiceNamespace + "ExtendedUpdateInfoParameters",
                    new XElement(ServiceNamespace + "XmlUpdateFragmentTypes",
                        new XElement(ServiceNamespace + "XmlUpdateFragmentType", "Extended")),
                    new XElement(ServiceNamespace + "Locales",
                        new XElement(ServiceNamespace + "Locale",
                            new XElement(ServiceNamespace + "Language", "en-US")))),
                new XElement(ServiceNamespace + "ClientPreferredLanguages",
                    new XElement(ServiceNamespace + "Language", "en-US")),
                new XElement(ServiceNamespace + "ProductsParameters",
                    new XElement(ServiceNamespace + "SyncCurrentVersionOnly", "false"),
                    new XElement(ServiceNamespace + "DeviceAttributes", deviceAttributes),
                    new XElement(ServiceNamespace + "CallerAttributes", "Interactive=1;IsSeeker=0;"),
                    new XElement(ServiceNamespace + "Products")));

            return CreateEnvelope(
                "SyncUpdates",
                endpoint,
                false,
                new XElement(ServiceNamespace + "SyncUpdates",
                    new XElement(ServiceNamespace + "cookie",
                        new XElement(ServiceNamespace + "Expiration", cookie.Expiration),
                        new XElement(ServiceNamespace + "EncryptedData", cookie.EncryptedData)),
                    parameters),
                now);
        }

        private static XDocument CreateFileLocationRequest(
            string updateId,
            string revisionNumber,
            string architecture,
            string endpoint)
        {
            DateTime now = DateTime.UtcNow;
            return CreateEnvelope(
                "GetExtendedUpdateInfo2",
                endpoint,
                false,
                new XElement(ServiceNamespace + "GetExtendedUpdateInfo2",
                    new XElement(ServiceNamespace + "updateIDs",
                        new XElement(ServiceNamespace + "UpdateIdentity",
                            new XElement(ServiceNamespace + "UpdateID", updateId),
                            new XElement(ServiceNamespace + "RevisionNumber", revisionNumber))),
                    new XElement(ServiceNamespace + "infoTypes",
                        new XElement(ServiceNamespace + "XmlUpdateFragmentType", "FileUrl"),
                        new XElement(ServiceNamespace + "XmlUpdateFragmentType", "FileDecryption")),
                    new XElement(ServiceNamespace + "deviceAttributes",
                        "App=WU;OSArchitecture=" + (architecture == "arm64" ? "ARM64" : "AMD64") + ";DeviceFamily=Windows.Desktop;")),
                now);
        }

        private static XDocument CreateEnvelope(
            string action,
            string endpoint,
            bool includeUser,
            XElement body,
            DateTime now)
        {
            XElement ticketType = new XElement(AuthorizationNamespace + "TicketType",
                new XAttribute("Name", "MSA"),
                new XAttribute("Version", "1.0"),
                new XAttribute("Policy", "MBI_SSL"));
            if (includeUser) ticketType.Add(new XElement(AuthorizationNamespace + "User"));

            XElement security = new XElement(SecurityNamespace + "Security",
                new XAttribute(SoapNamespace + "mustUnderstand", "1"),
                new XElement(SecurityUtilityNamespace + "Timestamp",
                    new XElement(SecurityUtilityNamespace + "Created", FormatTime(now)),
                    new XElement(SecurityUtilityNamespace + "Expires", FormatTime(now.AddMinutes(5)))),
                new XElement(AuthorizationNamespace + "WindowsUpdateTicketsToken",
                    new XAttribute(SecurityUtilityNamespace + "id", "ClientMSA"),
                    ticketType));

            return new XDocument(
                new XElement(SoapNamespace + "Envelope",
                    new XAttribute(XNamespace.Xmlns + "s", SoapNamespace),
                    new XAttribute(XNamespace.Xmlns + "a", AddressingNamespace),
                    new XElement(SoapNamespace + "Header",
                        new XElement(AddressingNamespace + "Action",
                            new XAttribute(SoapNamespace + "mustUnderstand", "1"),
                            ServiceNamespace.NamespaceName + "/" + action),
                        new XElement(AddressingNamespace + "MessageID", "urn:uuid:" + Guid.NewGuid().ToString("D")),
                        new XElement(AddressingNamespace + "To",
                            new XAttribute(SoapNamespace + "mustUnderstand", "1"),
                            endpoint),
                        security),
                    new XElement(SoapNamespace + "Body", body)));
        }

        private async Task<T> SendWithRetryAsync<T>(
            Func<HttpRequestMessage> requestFactory,
            Uri expectedUri,
            int maximumResponseBytes,
            string description,
            Func<HttpResponseMessage, string, T> responseHandler,
            CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < HttpRetryPolicy.MaximumAttempts; attempt++)
            {
                using (HttpRequestMessage request = requestFactory())
                using (CancellationTokenSource requestCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    requestCancellation.CancelAfter(requestTimeout);
                    try
                    {
                        using (HttpResponseMessage response = await httpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            requestCancellation.Token).ConfigureAwait(false))
                        {
                            if (HttpRetryPolicy.IsTransientStatus(response.StatusCode))
                            {
                                TimeSpan? retryAfter = HttpRetryPolicy.GetRetryAfter(response.Headers);
                                HttpStatusCode statusCode = response.StatusCode;
                                if (attempt == HttpRetryPolicy.MaximumAttempts - 1)
                                {
                                    throw new TransientHttpRequestException(
                                        description + "连续返回可重试的 HTTP 状态：" + (int)statusCode + "。",
                                        statusCode,
                                        retryAfter);
                                }
                                response.Dispose();
                                await HttpRetryPolicy.DelayAsync(
                                    attempt,
                                    initialRetryDelay,
                                    retryAfter,
                                    cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            EnsureProtocolResponseDidNotRedirect(response, expectedUri, description);
                            string responseText = await ReadResponseTextAsync(
                                response,
                                maximumResponseBytes,
                                description,
                                requestCancellation.Token).ConfigureAwait(false);
                            return responseHandler(response, responseText);
                        }
                    }
                    catch (Exception exception)
                    {
                        if (exception is TransientHttpRequestException)
                        {
                            throw;
                        }
                        cancellationToken.ThrowIfCancellationRequested();

                        bool timedOut = requestCancellation.IsCancellationRequested &&
                            exception is OperationCanceledException;
                        if (!timedOut &&
                            !HttpRetryPolicy.IsTransientTransportException(exception, cancellationToken))
                        {
                            throw;
                        }

                        Exception failure = timedOut
                            ? new TimeoutException(
                                description + "请求超过 " + FormatTimeout(requestTimeout) + "仍未完成。",
                                exception)
                            : exception;
                        if (attempt == HttpRetryPolicy.MaximumAttempts - 1)
                        {
                            throw new TransientHttpRequestException(
                                description + "连续发生网络传输错误：" + GetExceptionSummary(failure) + "。",
                                null,
                                null,
                                failure);
                        }
                        await HttpRetryPolicy.DelayAsync(
                            attempt,
                            initialRetryDelay,
                            null,
                        cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            throw new InvalidOperationException(description + "重试状态异常。");
        }

        private static string FormatTimeout(TimeSpan timeout)
        {
            return timeout.TotalMinutes >= 1
                ? timeout.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " 分钟"
                : Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " 秒";
        }

        private static async Task<string> ReadResponseTextAsync(
            HttpResponseMessage response,
            int maximumBytes,
            string description,
            CancellationToken cancellationToken)
        {
            if (response.Content == null)
            {
                return string.Empty;
            }
            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > maximumBytes)
            {
                throw new InvalidDataException(
                    description + "超过响应大小限制：" + contentLength.Value + " > " + maximumBytes + " 字节。");
            }

            using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[81920];
                int total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maximumBytes)
                    {
                        throw new InvalidDataException(
                            description + "超过响应大小限制：大于 " + maximumBytes + " 字节。");
                    }
                    output.Write(buffer, 0, read);
                }
                output.Position = 0;
                using (StreamReader reader = new StreamReader(output, Encoding.UTF8, true))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static void EnsureProtocolResponseDidNotRedirect(
            HttpResponseMessage response,
            Uri expectedUri,
            string description)
        {
            Uri actualUri = response.RequestMessage == null ? null : response.RequestMessage.RequestUri;
            if (HttpRetryPolicy.IsRedirectStatus(response.StatusCode) ||
                actualUri == null ||
                !string.Equals(actualUri.AbsoluteUri, expectedUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                Uri location = response.Headers.Location;
                throw new InvalidDataException(
                    description + "返回了不允许的重定向" +
                    (location == null ? "。" : "：" + location + "。"));
            }
        }

        private static void EnsureSuccessfulStatus(HttpResponseMessage response, string description)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    description + "返回 HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + "。");
            }
        }

        private static void ThrowIfSoapFault(string responseXml, string description)
        {
            XDocument document = TryParseXml(responseXml);
            XElement fault = document == null
                ? null
                : document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Fault");
            if (fault == null)
            {
                return;
            }

            XElement codeElement = fault.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "Value" || element.Name.LocalName == "faultcode");
            XElement reasonElement = fault.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "Text" || element.Name.LocalName == "faultstring");
            string code = TruncateFaultText(codeElement == null ? null : codeElement.Value);
            string reason = TruncateFaultText(reasonElement == null ? null : reasonElement.Value);
            throw new InvalidDataException(
                description + "返回 SOAP Fault" +
                (string.IsNullOrWhiteSpace(code) ? string.Empty : " [" + code + "]") +
                (string.IsNullOrWhiteSpace(reason) ? "。" : "：" + reason + "。"));
        }

        private static string TruncateFaultText(string value)
        {
            string text = (value ?? string.Empty).Trim();
            return text.Length <= 512 ? text : text.Substring(0, 512);
        }

        private static string GetExceptionSummary(Exception exception)
        {
            Exception current = exception;
            while (current != null && current.InnerException != null)
            {
                current = current.InnerException;
            }
            return current == null || string.IsNullOrWhiteSpace(current.Message)
                ? "未知网络错误"
                : current.Message;
        }

        private static XElement GetEmbeddedXml(XElement parent)
        {
            if (parent == null) return new XElement("Root");
            XElement xml = parent.Elements().FirstOrDefault(element => element.Name.LocalName == "Xml");
            if (xml == null) return new XElement("Root");
            if (xml.HasElements) return new XElement("Root", xml.Elements());
            if (string.IsNullOrWhiteSpace(xml.Value)) return new XElement("Root");
            return ParseXml(
                "<Root>" + WebUtility.HtmlDecode(xml.Value) + "</Root>",
                "Windows Update 嵌入 XML").Root;
        }

        private static XDocument ParseXml(string xml, string source)
        {
            try
            {
                XDocument document = TryParseXml(xml);
                if (document == null)
                {
                    throw new XmlException("XML 文档为空或格式无效。");
                }
                return document;
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(source + "不是有效 XML。", exception);
            }
        }

        private static XDocument TryParseXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                return null;
            }
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = Math.Max(1024, xml.Length + 1)
                };
                using (StringReader input = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(input, settings))
                {
                    return XDocument.Load(reader, LoadOptions.None);
                }
            }
            catch
            {
                return null;
            }
        }

        private static string GetDirectChildValue(XElement parent, string localName)
        {
            XElement child = parent == null
                ? null
                : parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName);
            return child == null ? null : child.Value;
        }

        private static bool IsBase64Digest(string value, int expectedLength)
        {
            try
            {
                return Convert.FromBase64String(value ?? string.Empty).Length == expectedLength;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool IsPositiveInteger(string value)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed > 0;
        }

        internal static bool IsMicrosoftDeliveryUri(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri ||
                (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            string host = uri.Host;
            return !string.IsNullOrWhiteSpace(host) &&
                (string.Equals(host, "delivery.mp.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
                 host.EndsWith(".delivery.mp.microsoft.com", StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeArchitecture(string architecture)
        {
            if (string.Equals(architecture, "x64", StringComparison.OrdinalIgnoreCase)) return "x64";
            if (string.Equals(architecture, "arm64", StringComparison.OrdinalIgnoreCase)) return "arm64";
            throw new ArgumentException("不支持的微软商店程序包架构：" + architecture, nameof(architecture));
        }

        private static string FormatTime(DateTime value)
        {
            return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        internal sealed class CatalogProduct
        {
            public string ProductId { get; set; }
            public string PackageIdentityName { get; set; }
            public string PackageFamilyName { get; set; }
            public List<CatalogPackage> Packages { get; set; }
        }

        internal sealed class CatalogPackage
        {
            public string WuCategoryId { get; set; }
            public List<string> Architectures { get; set; }
            public string Hash { get; set; }
            public string HashAlgorithm { get; set; }
            public long MaxDownloadSizeInBytes { get; set; }
            public string PackageFormat { get; set; }
            public string PackageFullName { get; set; }
            public List<CatalogPlatformDependency> PlatformDependencies { get; set; }
        }

        internal sealed class CatalogPlatformDependency
        {
            public string PlatformName { get; set; }
            public long MinVersion { get; set; }
            public long MaxTested { get; set; }
        }

        internal sealed class DeliveryFile
        {
            public string UpdateId { get; set; }
            public string RevisionNumber { get; set; }
            public string FileDigest { get; set; }
            public string Sha256Digest { get; set; }
            public long SizeInBytes { get; set; }
            public string Url { get; set; }
        }

        private sealed class DeliveryEndpoint
        {
            internal DeliveryEndpoint(string name, string baseUrl)
            {
                Name = name;
                BaseUri = new Uri(baseUrl, UriKind.Absolute);
                SecuredUri = new Uri(baseUrl + "/secured", UriKind.Absolute);
            }

            internal string Name { get; private set; }
            internal Uri BaseUri { get; private set; }
            internal Uri SecuredUri { get; private set; }
        }

        private sealed class StoreCookie
        {
            public string Expiration { get; set; }
            public string EncryptedData { get; set; }
        }

        private sealed class StoreCatalogEnvelope
        {
            public StoreCatalogProduct Product { get; set; }
        }

        private sealed class StoreCatalogProduct
        {
            public string ProductId { get; set; }
            public StoreCatalogProductProperties Properties { get; set; }
            public List<StoreCatalogAvailability> DisplaySkuAvailabilities { get; set; }
        }

        private sealed class StoreCatalogProductProperties
        {
            public string PackageIdentityName { get; set; }
            public string PackageFamilyName { get; set; }
        }

        private sealed class StoreCatalogAvailability
        {
            public StoreCatalogSku Sku { get; set; }
        }

        private sealed class StoreCatalogSku
        {
            public StoreCatalogSkuProperties Properties { get; set; }
        }

        private sealed class StoreCatalogSkuProperties
        {
            public StoreCatalogFulfillmentData FulfillmentData { get; set; }
            public List<StoreCatalogPackage> Packages { get; set; }
        }

        private sealed class StoreCatalogFulfillmentData
        {
            public string WuCategoryId { get; set; }
        }

        private sealed class StoreCatalogPackage
        {
            public List<string> Architectures { get; set; }
            public string Hash { get; set; }
            public string HashAlgorithm { get; set; }
            public long MaxDownloadSizeInBytes { get; set; }
            public string PackageFormat { get; set; }
            public string PackageFullName { get; set; }
            public List<StoreCatalogPlatformDependency> PlatformDependencies { get; set; }
        }

        private sealed class StoreCatalogPlatformDependency
        {
            public string PlatformName { get; set; }
            public long MinVersion { get; set; }
            public long MaxTested { get; set; }
        }
    }

    internal sealed class MicrosoftStoreUpdateNotFoundException : IOException
    {
        internal MicrosoftStoreUpdateNotFoundException(string message)
            : base(message)
        {
        }
    }

    internal sealed class MicrosoftStorePublicationPendingException : IOException
    {
        internal MicrosoftStorePublicationPendingException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
