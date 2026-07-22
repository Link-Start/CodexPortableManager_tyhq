using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPortableManager
{
    internal static class StorePackageSourceTestRunner
    {
        public static int Run(string reportPath, bool includeLiveTest)
        {
            StringBuilder report = new StringBuilder();
            try
            {
                RunProtocolParsingTests();
                report.AppendLine("PROTOCOL_PARSING=PASS");
                RunCodexSelectionTests();
                report.AppendLine("CODEX_SELECTION=PASS");
                RunNetworkResilienceTests();
                report.AppendLine("NETWORK_RESILIENCE=PASS");
                if (includeLiveTest)
                {
                    PackageMetadata x64Package = ResolveLivePackage("x64");
                    PackageMetadata arm64Package = ResolveLivePackage("arm64");
                    report.AppendLine("LIVE=PASS");
                    report.AppendLine("ENDPOINT_MATRIX=PASS");
                    AppendPackage(report, "X64", x64Package);
                    AppendPackage(report, "ARM64", arm64Package);
                }
                else
                {
                    report.AppendLine("LIVE=SKIPPED");
                }
                report.AppendLine("RESULT=PASS");
                WriteReport(reportPath, report.ToString());
                Console.Write(report.ToString());
                return 0;
            }
            catch (Exception exception)
            {
                report.AppendLine("RESULT=FAIL");
                report.AppendLine(exception.ToString());
                WriteReport(reportPath, report.ToString());
                Console.Error.Write(report.ToString());
                return 1;
            }
        }

        private static void RunProtocolParsingTests()
        {
            const string productId = "9PLM9XGG6VKS";
            const string packageName = "OpenAI.Codex";
            const string fullName = "OpenAI.Codex_26.707.3748.0_x64__2p2nqsd0c76g0";
            const string digest = "GB+qwTcR7TghPDhkS+L8F2949loFfNFn3ARJNdPoGrk=";
            const string sha1 = "Z6R/QmpRhHtxZwAQwroyo/LxF7Q=";
            const long size = 728683082;

            string json = "{\"Product\":{\"ProductId\":\"" + productId +
                "\",\"Properties\":{\"PackageIdentityName\":\"" + packageName +
                "\",\"PackageFamilyName\":\"OpenAI.Codex_2p2nqsd0c76g0\"}," +
                "\"DisplaySkuAvailabilities\":[{\"Sku\":{\"Properties\":{" +
                "\"FulfillmentData\":{\"WuCategoryId\":\"fdf7dba1-a7bc-4592-ad8e-04aa3b974675\"}," +
                "\"Packages\":[" +
                PackageJson("OpenAI.Codex_99.0.0.0_arm64__2p2nqsd0c76g0", "arm64", digest, size) + "," +
                PackageJson("OpenAI.Codex_25.1.2.3_x64__2p2nqsd0c76g0", "x64", digest, size) + "," +
                PackageJson(fullName, "x64", digest, size) +
                "]}}}]}}";
            MicrosoftStoreProtocolClient.CatalogProduct product =
                MicrosoftStoreProtocolClient.ParseCatalogResponse(json, productId);
            Assert(product.ProductId == productId, "目录协议解析得到的 ProductId 错误。");
            Assert(product.PackageIdentityName == packageName, "目录协议解析得到的包身份错误。");
            Assert(product.Packages.Count == 3, "目录协议解析没有保留全部候选包。");

            string updateId = "61ef02c8-ab21-4318-aa8a-47ccb1d8b9dc";
            string syncXml = "<Root><UpdateInfo><ID>42</ID><Xml><UpdateIdentity UpdateID=\"" + updateId +
                "\" RevisionNumber=\"1\"/><ApplicabilityRules><Metadata><AppxPackageMetadata><AppxMetadata " +
                "PackageMoniker=\"" + fullName + "\"/></AppxPackageMetadata></Metadata></ApplicabilityRules></Xml></UpdateInfo>" +
                "<ExtendedUpdateInfo><Updates><Update><ID>42</ID><Xml><Files><File InstallerSpecificIdentifier=\"" +
                fullName + "\" Size=\"" + size.ToString(CultureInfo.InvariantCulture) + "\" Digest=\"" + sha1 +
                "\"><AdditionalDigest Algorithm=\"SHA256\">" + digest +
                "</AdditionalDigest></File></Files></Xml></Update></Updates></ExtendedUpdateInfo></Root>";
            MicrosoftStoreProtocolClient.DeliveryFile update =
                MicrosoftStoreProtocolClient.ParseSyncResponse(syncXml, fullName);
            Assert(update.UpdateId == updateId && update.RevisionNumber == "1", "更新身份解析错误。");
            Assert(update.FileDigest == sha1 && update.Sha256Digest == digest && update.SizeInBytes == size,
                "更新文件元数据解析错误。");

            string locations = "<Root><FileLocation><FileDigest>other</FileDigest><Url>http://example.invalid/file</Url></FileLocation>" +
                "<FileLocation><FileDigest>" + sha1 + "</FileDigest>" +
                "<Url>http://tlu.dl.delivery.mp.microsoft.com/filestreamingservice/files/package?P1=1&amp;P2=2</Url>" +
                "</FileLocation></Root>";
            string resolved = MicrosoftStoreProtocolClient.ParseFileLocationResponse(locations, sha1);
            Assert(resolved.StartsWith("http://tlu.dl.delivery.mp.microsoft.com/", StringComparison.OrdinalIgnoreCase),
                "下载地址没有保留 Windows Update 返回的微软 CDN 协议。");

            bool rejected = false;
            try
            {
                MicrosoftStoreProtocolClient.ParseFileLocationResponse(
                    "<Root><FileLocation><FileDigest>" + sha1 + "</FileDigest><Url>https://example.invalid/file</Url></FileLocation></Root>",
                    sha1);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            Assert(rejected, "非微软下载主机没有被拒绝。");
        }

        private static void RunCodexSelectionTests()
        {
            const string productId = "9PLM9XGG6VKS";
            const string packageName = "OpenAI.Codex";
            const string fullName = "OpenAI.Codex_26.707.3748.0_x64__2p2nqsd0c76g0";
            const string digest = "GB+qwTcR7TghPDhkS+L8F2949loFfNFn3ARJNdPoGrk=";
            const long size = 728683082;
            string json = "{\"Product\":{\"ProductId\":\"" + productId +
                "\",\"Properties\":{\"PackageIdentityName\":\"" + packageName +
                "\",\"PackageFamilyName\":\"OpenAI.Codex_2p2nqsd0c76g0\"}," +
                "\"DisplaySkuAvailabilities\":[{\"Sku\":{\"Properties\":{" +
                "\"FulfillmentData\":{\"WuCategoryId\":\"fdf7dba1-a7bc-4592-ad8e-04aa3b974675\"}," +
                "\"Packages\":[" +
                PackageJson("OpenAI.Codex_99.0.0.0_arm64__2p2nqsd0c76g0", "arm64", digest, size) + "," +
                PackageJson("OpenAI.Codex_25.1.2.3_x64__2p2nqsd0c76g0", "x64", digest, size) + "," +
                PackageJson(fullName, "x64", digest, size) +
                "]}}}]}}";
            MicrosoftStoreProtocolClient.CatalogProduct product =
                MicrosoftStoreProtocolClient.ParseCatalogResponse(json, productId);
            CodexMicrosoftStoreSource.CatalogSelection catalog =
                CodexMicrosoftStoreSource.SelectLatestPackage(
                    product,
                    "x64",
                    2814751015246136L);
            Assert(catalog.Metadata.fullName == fullName, "Codex 来源没有按架构选择 x64 主包。");
            Assert(catalog.Metadata.version == "26.707.3748.0", "Codex 来源选择的版本错误。");
            Assert(catalog.Metadata.packageName == packageName && catalog.Metadata.architecture == "x64",
                "Codex 来源没有返回包名或架构策略结果。");
            Assert(catalog.Metadata.digest == digest && catalog.Metadata.sizeInBytes == size,
                "Codex 来源选择的摘要或大小错误。");
            string cachePath = CacheFileLock.GetPackagePath(
                Path.GetTempPath(),
                catalog.Metadata.packageName,
                catalog.Metadata.version,
                catalog.Metadata.architecture);
            Assert(Path.GetFileName(cachePath) == "OpenAI.Codex_26.707.3748.0_x64.msix",
                "缓存路径没有使用来源返回的包名、版本和架构。");

            product.PackageFamilyName = "Other.Package_family";
            bool rejected = false;
            try
            {
                CodexMicrosoftStoreSource.SelectLatestPackage(product, "x64", 2814751015246136L);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            Assert(rejected, "Codex 来源没有拒绝不匹配的 Package Family。");
        }

        private static void RunNetworkResilienceTests()
        {
            TestCatalogRetry();
            TestEndpointFallback();
            TestSoapFault();
            TestPublicationPending();
            TestSourcePublicationRetry();
            TestCatalogResponseLimit();
            TestMetadataRequestTimeout();
            TestDownloadRedirectValidation();
            TestDownloadInactivityTimeout();
            TestFullDownloadReportsRealtimeSpeed();
            TestDownloadRetriesInternalServerError();
            TestDownloadRecoveryWindowUsesElapsedTime();
            TestDownloadRecoveryProbesWhenSystemReportsOffline();
            TestDownloadResumeAfterInterruption();
            TestDownloadRangeFallback();
            TestDownloadInvalidRangeRejected();
            TestDownloadPauseAndResume();
            TestPauseInterruptsUnresponsiveDownload();
            TestCancellationInterruptsUnresponsiveDownload();
            TestExplicitRangeReaderValidation();
            TestExplicitRangePauseInterruptsUnresponsiveDownload();
            TestExplicitRangeImmediateRetryInterruptsUnresponsiveDownload();
            TestExplicitRangeResumePreservesPartialBytes();
            TestExplicitRangeReportsRealtimeSpeed();
            TestExplicitRangeReaderRejectsIgnoredAndInvalidResponses();
            TestExplicitRangeReaderRejectsEntityTagChanges();
            TestLatestVersionCancellationDisplay();
        }

        private static void TestCatalogRetry()
        {
            int requests = 0;
            bool correlationHeaderFound = false;
            using (HttpClient client = new HttpClient(new TestHttpMessageHandler((request, attempt) =>
            {
                requests++;
                correlationHeaderFound = correlationHeaderFound || request.Headers.Contains("MS-CV");
                if (requests == 1) throw new HttpRequestException("unexpected EOF from transport");
                if (requests == 2) return Task.FromResult(CreateResponse(request, (HttpStatusCode)429, string.Empty));
                return Task.FromResult(CreateResponse(
                    request,
                    HttpStatusCode.OK,
                    "{\"Product\":{\"ProductId\":\"9PLM9XGG6VKS\",\"Properties\":{" +
                    "\"PackageIdentityName\":\"Any.Package\",\"PackageFamilyName\":\"Any.Package_family\"}," +
                    "\"DisplaySkuAvailabilities\":[]}}"));
            })))
            {
                MicrosoftStoreProtocolClient protocol = new MicrosoftStoreProtocolClient(client, TimeSpan.Zero);
                MicrosoftStoreProtocolClient.CatalogProduct product = protocol.GetCatalogProductAsync(
                    "9PLM9XGG6VKS",
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert(requests == 3 && product.ProductId == "9PLM9XGG6VKS",
                    "Catalog 没有在瞬时状态后按上限重试并恢复。");
                Assert(!correlationHeaderFound, "Catalog 请求仍发送了非规范 MS-CV。");
            }
        }

        private static void TestEndpointFallback()
        {
            const string fullName = "OpenAI.Codex_26.707.3748.0_x64__2p2nqsd0c76g0";
            const string digest = "GB+qwTcR7TghPDhkS+L8F2949loFfNFn3ARJNdPoGrk=";
            const string sha1 = "Z6R/QmpRhHtxZwAQwroyo/LxF7Q=";
            const long size = 728683082;
            Dictionary<string, int> requests = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (HttpClient client = new HttpClient(new TestHttpMessageHandler(async (request, attempt) =>
            {
                string key = request.RequestUri.Host + request.RequestUri.AbsolutePath;
                requests[key] = requests.ContainsKey(key) ? requests[key] + 1 : 1;
                if (request.RequestUri.Host.StartsWith("fe3.", StringComparison.OrdinalIgnoreCase))
                {
                    return CreateResponse(request, HttpStatusCode.ServiceUnavailable, string.Empty);
                }
                if (!request.RequestUri.Host.StartsWith("fe6.", StringComparison.OrdinalIgnoreCase))
                {
                    return CreateResponse(request, HttpStatusCode.InternalServerError, "unexpected endpoint");
                }
                if (request.RequestUri.AbsolutePath.EndsWith("/secured", StringComparison.OrdinalIgnoreCase))
                {
                    return CreateResponse(
                        request,
                        HttpStatusCode.OK,
                        "<Root><FileLocation><FileDigest>" + sha1 + "</FileDigest>" +
                        "<Url>https://tlu.dl.delivery.mp.microsoft.com/package</Url></FileLocation></Root>");
                }

                string body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (body.IndexOf("GetCookie", StringComparison.Ordinal) >= 0)
                {
                    return CreateResponse(
                        request,
                        HttpStatusCode.OK,
                        "<Root><GetCookieResult><Expiration>2099-01-01T00:00:00Z</Expiration>" +
                        "<EncryptedData>cookie</EncryptedData></GetCookieResult></Root>");
                }
                return CreateResponse(request, HttpStatusCode.OK, SyncResponse(fullName, digest, sha1, size));
            })))
            {
                MicrosoftStoreProtocolClient protocol = new MicrosoftStoreProtocolClient(client, TimeSpan.Zero);
                MicrosoftStoreProtocolClient.DeliveryFile delivery = protocol.ResolvePackageFileAsync(
                    "fdf7dba1-a7bc-4592-ad8e-04aa3b974675",
                    fullName,
                    "x64",
                    CancellationToken.None).GetAwaiter().GetResult();
                string fe3 = "fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx";
                string fe6 = "fe6.delivery.mp.microsoft.com/ClientWebService/client.asmx";
                string fe6Secured = fe6 + "/secured";
                Assert(requests.ContainsKey(fe3) && requests[fe3] == HttpRetryPolicy.MaximumAttempts,
                    "FE3 没有先完成同端点重试。");
                Assert(requests.ContainsKey(fe6) && requests[fe6] == 2 &&
                    requests.ContainsKey(fe6Secured) && requests[fe6Secured] == 1,
                    "切换到 FE6 后没有重新完成 Cookie、同步和地址获取。");
                Assert(!requests.Keys.Any(value => value.StartsWith("fe6cr.", StringComparison.OrdinalIgnoreCase)),
                    "FE6 成功后仍继续访问了 FE6CR。");
                Assert(delivery.Url == "https://tlu.dl.delivery.mp.microsoft.com/package",
                    "FE6 回退没有返回可信 CDN 地址。");
            }
        }

        private static void TestSoapFault()
        {
            using (HttpClient client = new HttpClient(new TestHttpMessageHandler((request, attempt) =>
                Task.FromResult(CreateResponse(
                    request,
                    HttpStatusCode.BadRequest,
                    "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body><s:Fault>" +
                    "<s:Code><s:Value>s:Sender</s:Value></s:Code><s:Reason><s:Text>invalid request</s:Text></s:Reason>" +
                    "</s:Fault></s:Body></s:Envelope>")))))
            {
                MicrosoftStoreProtocolClient protocol = new MicrosoftStoreProtocolClient(client, TimeSpan.Zero);
                bool rejected = false;
                try
                {
                    protocol.ResolvePackageFileAsync(
                        "fdf7dba1-a7bc-4592-ad8e-04aa3b974675",
                        "OpenAI.Codex_1.0.0.0_x64__2p2nqsd0c76g0",
                        "x64",
                        CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (InvalidDataException exception)
                {
                    rejected = exception.Message.IndexOf("SOAP Fault", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        exception.Message.IndexOf("invalid request", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                Assert(rejected, "SOAP Fault 没有转换为准确的协议错误。");
            }
        }

        private static void TestPublicationPending()
        {
            using (HttpClient client = new HttpClient(new TestHttpMessageHandler(async (request, attempt) =>
            {
                string body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                return body.IndexOf("GetCookie", StringComparison.Ordinal) >= 0
                    ? CreateResponse(
                        request,
                        HttpStatusCode.OK,
                        "<Root><GetCookieResult><Expiration>2099-01-01T00:00:00Z</Expiration>" +
                        "<EncryptedData>cookie</EncryptedData></GetCookieResult></Root>")
                    : CreateResponse(request, HttpStatusCode.OK, "<Root/>");
            })))
            {
                MicrosoftStoreProtocolClient protocol = new MicrosoftStoreProtocolClient(client, TimeSpan.Zero);
                bool pending = false;
                try
                {
                    protocol.ResolvePackageFileAsync(
                        "fdf7dba1-a7bc-4592-ad8e-04aa3b974675",
                        "OpenAI.Codex_1.0.0.0_x64__2p2nqsd0c76g0",
                        "x64",
                        CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (MicrosoftStorePublicationPendingException)
                {
                    pending = true;
                }
                Assert(pending, "三端点均未同步时没有报告微软发布同步延迟。");
            }
        }

        private static void TestSourcePublicationRetry()
        {
            const string fullName = "OpenAI.Codex_26.707.3748.0_x64__2p2nqsd0c76g0";
            const string digest = "GB+qwTcR7TghPDhkS+L8F2949loFfNFn3ARJNdPoGrk=";
            const string sha1 = "Z6R/QmpRhHtxZwAQwroyo/LxF7Q=";
            const long size = 728683082;
            int syncRequests = 0;
            using (HttpClient client = new HttpClient(new TestHttpMessageHandler(async (request, attempt) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    string json = "{\"Product\":{\"ProductId\":\"9PLM9XGG6VKS\",\"Properties\":{" +
                        "\"PackageIdentityName\":\"OpenAI.Codex\",\"PackageFamilyName\":\"OpenAI.Codex_2p2nqsd0c76g0\"}," +
                        "\"DisplaySkuAvailabilities\":[{\"Sku\":{\"Properties\":{" +
                        "\"FulfillmentData\":{\"WuCategoryId\":\"fdf7dba1-a7bc-4592-ad8e-04aa3b974675\"}," +
                        "\"Packages\":[" + PackageJson(fullName, "x64", digest, size) + "]}}}]}}";
                    return CreateResponse(request, HttpStatusCode.OK, json);
                }
                if (request.RequestUri.AbsolutePath.EndsWith("/secured", StringComparison.OrdinalIgnoreCase))
                {
                    return CreateResponse(
                        request,
                        HttpStatusCode.OK,
                        "<Root><FileLocation><FileDigest>" + sha1 + "</FileDigest>" +
                        "<Url>https://tlu.dl.delivery.mp.microsoft.com/package</Url></FileLocation></Root>");
                }

                string body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (body.IndexOf("GetCookie", StringComparison.Ordinal) >= 0)
                {
                    return CreateResponse(
                        request,
                        HttpStatusCode.OK,
                        "<Root><GetCookieResult><Expiration>2099-01-01T00:00:00Z</Expiration>" +
                        "<EncryptedData>cookie</EncryptedData></GetCookieResult></Root>");
                }
                syncRequests++;
                return CreateResponse(
                    request,
                    HttpStatusCode.OK,
                    syncRequests <= 3 ? "<Root/>" : SyncResponse(fullName, digest, sha1, size));
            })))
            {
                MicrosoftStoreProtocolClient protocol = new MicrosoftStoreProtocolClient(client, TimeSpan.Zero);
                CodexMicrosoftStoreSource source = new CodexMicrosoftStoreSource(protocol, TimeSpan.Zero);
                PackageMetadata package = source.ResolveLatestAsync(
                    "x64",
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert(syncRequests == 4 && package.fullName == fullName &&
                    package.url == "https://tlu.dl.delivery.mp.microsoft.com/package",
                    "Codex 来源没有在微软发布同步延迟后完整重试并恢复。");
            }
        }

        private static void TestCatalogResponseLimit()
        {
            string oversized = new string('x', 4 * 1024 * 1024 + 1);
            using (HttpClient client = new HttpClient(new TestHttpMessageHandler((request, attempt) =>
                Task.FromResult(CreateResponse(request, HttpStatusCode.OK, oversized)))))
            {
                MicrosoftStoreProtocolClient protocol = new MicrosoftStoreProtocolClient(client, TimeSpan.Zero);
                bool rejected = false;
                try
                {
                    protocol.GetCatalogProductAsync("9PLM9XGG6VKS", CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (InvalidDataException)
                {
                    rejected = true;
                }
                Assert(rejected, "超限 Catalog 响应没有在 JSON 解析前被拒绝。");
            }
        }

        private static void TestMetadataRequestTimeout()
        {
            int requests = 0;
            using (HttpClient client = new HttpClient(new TestHttpMessageHandler(
                async (request, attempt, cancellationToken) =>
                {
                    requests++;
                    if (attempt == 1)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    }
                    return CreateStreamingResponse(
                        request,
                        HttpStatusCode.OK,
                        new PacedReadStream(new byte[0], TimeSpan.FromSeconds(1)));
                })))
            {
                MicrosoftStoreProtocolClient protocol = new MicrosoftStoreProtocolClient(
                    client,
                    TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(50));
                bool timedOut = false;
                try
                {
                    protocol.GetCatalogProductAsync(
                        "9PLM9XGG6VKS",
                        CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (TransientHttpRequestException exception)
                {
                    timedOut = exception.InnerException is TimeoutException;
                }
                Assert(timedOut && requests == HttpRetryPolicy.MaximumAttempts,
                    "元数据请求没有对响应头和正文使用独立短超时并按上限重试。");
            }
        }

        private static void TestDownloadRedirectValidation()
        {
            using (ArtifactPipeline pipeline = new ArtifactPipeline(
                delegate { },
                (file, arguments, token) => Task.FromResult(new ProcessResult()),
                new TestHttpMessageHandler((request, attempt) =>
                {
                    HttpResponseMessage response = CreateResponse(request, HttpStatusCode.Redirect, string.Empty);
                    response.Headers.Location = new Uri("https://example.invalid/package");
                    return Task.FromResult(response);
                })))
            {
                bool rejected = false;
                try
                {
                    using (pipeline.SendDownloadRequestAsync(
                        "https://tlu.dl.delivery.mp.microsoft.com/package",
                        CancellationToken.None).GetAwaiter().GetResult())
                    {
                    }
                }
                catch (InvalidDataException)
                {
                    rejected = true;
                }
                Assert(rejected, "下载器没有拒绝指向非微软域名的重定向。");
            }

            int requests = 0;
            using (ArtifactPipeline pipeline = new ArtifactPipeline(
                delegate { },
                (file, arguments, token) => Task.FromResult(new ProcessResult()),
                new TestHttpMessageHandler((request, attempt) =>
                {
                    requests++;
                    Assert(request.Headers.Range != null &&
                        request.Headers.Range.Ranges.First().From == 128,
                        "下载重定向没有保留 Range 断点。");
                    if (requests == 1)
                    {
                        HttpResponseMessage redirect = CreateResponse(request, HttpStatusCode.Redirect, string.Empty);
                        redirect.Headers.Location = new Uri("https://dl.delivery.mp.microsoft.com/final");
                        return Task.FromResult(redirect);
                    }
                    return Task.FromResult(CreateResponse(request, HttpStatusCode.OK, string.Empty));
            })))
            using (HttpResponseMessage response = pipeline.SendDownloadRequestAsync(
                "https://tlu.dl.delivery.mp.microsoft.com/package",
                128,
                CancellationToken.None).GetAwaiter().GetResult())
            {
                Assert(response.IsSuccessStatusCode && requests == 2,
                    "下载器没有正确跟随受信任的微软域名重定向。");
            }
        }

        private static void TestDownloadInactivityTimeout()
        {
            string stalledPath = Path.Combine(
                Path.GetTempPath(),
                "CodexPortableManager-stalled-" + Guid.NewGuid().ToString("N") + ".msix");
            string pacedPath = Path.Combine(
                Path.GetTempPath(),
                "CodexPortableManager-paced-" + Guid.NewGuid().ToString("N") + ".msix");
            IProgress<OperationProgress> progress = new DirectProgress<OperationProgress>(delegate { });
            try
            {
                using (ArtifactPipeline pipeline = new ArtifactPipeline(
                    delegate { },
                    (file, arguments, token) => Task.FromResult(new ProcessResult()),
                    new TestHttpMessageHandler(async (request, attempt, cancellationToken) =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                        return CreateResponse(request, HttpStatusCode.OK, string.Empty);
                    }),
                    TimeSpan.FromMilliseconds(50)))
                {
                    bool stalled = false;
                    try
                    {
                        using (pipeline.SendDownloadRequestAsync(
                            "https://tlu.dl.delivery.mp.microsoft.com/package",
                            CancellationToken.None).GetAwaiter().GetResult())
                        {
                        }
                    }
                    catch (IOException exception)
                    {
                        stalled = exception.Message.IndexOf("停滞", StringComparison.Ordinal) >= 0;
                    }
                    Assert(stalled, "下载请求等待响应头时没有触发停滞超时。");
                }

                using (ArtifactPipeline pipeline = new ArtifactPipeline(
                    delegate { },
                    (file, arguments, token) => Task.FromResult(new ProcessResult()),
                    new TestHttpMessageHandler((request, attempt) => Task.FromResult(
                        CreateStreamingResponse(
                            request,
                            HttpStatusCode.OK,
                            new PacedReadStream(new byte[0], TimeSpan.FromSeconds(1))))),
                    TimeSpan.FromMilliseconds(50)))
                {
                    bool stalled = false;
                    try
                    {
                        pipeline.DownloadFileFromUrlAsync(
                            "https://tlu.dl.delivery.mp.microsoft.com/package",
                            stalledPath,
                            1,
                            progress,
                            CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (IOException exception)
                    {
                        stalled = exception.Message.IndexOf("停滞", StringComparison.Ordinal) >= 0;
                    }
                    Assert(stalled, "下载流连续无数据时没有触发停滞超时。");
                }

                byte[] expected = { 1, 2, 3, 4 };
                using (ArtifactPipeline pipeline = new ArtifactPipeline(
                    delegate { },
                    (file, arguments, token) => Task.FromResult(new ProcessResult()),
                    new TestHttpMessageHandler((request, attempt) => Task.FromResult(
                        CreateStreamingResponse(
                            request,
                            HttpStatusCode.OK,
                            new PacedReadStream(expected, TimeSpan.FromMilliseconds(80)),
                            expected.Length))),
                    TimeSpan.FromMilliseconds(200)))
                {
                    string digest = pipeline.DownloadFileFromUrlAsync(
                        "https://tlu.dl.delivery.mp.microsoft.com/package",
                        pacedPath,
                        expected.Length,
                        progress,
                        CancellationToken.None).GetAwaiter().GetResult();
                    Assert(!string.IsNullOrWhiteSpace(digest) &&
                        File.ReadAllBytes(pacedPath).SequenceEqual(expected),
                        "持续有进展的慢下载被错误地按总耗时取消。");
                }
            }
            finally
            {
                if (File.Exists(stalledPath)) File.Delete(stalledPath);
                if (File.Exists(pacedPath)) File.Delete(pacedPath);
            }
        }

        private static void TestFullDownloadReportsRealtimeSpeed()
        {
            string downloadPath = Path.Combine(
                Path.GetTempPath(),
                "CodexPortableManager-realtime-download-" + Guid.NewGuid().ToString("N") + ".msix");
            byte[] expected = new byte[8 * 1024 * 1024];
            List<OperationProgress> reports = new List<OperationProgress>();
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                try
                {
                    using (ArtifactPipeline pipeline = new ArtifactPipeline(
                        delegate { },
                        (file, arguments, token) => Task.FromResult(new ProcessResult()),
                        new TestHttpMessageHandler((request, attempt) => Task.FromResult(
                            CreateStreamingResponse(
                                request,
                                HttpStatusCode.OK,
                                new PacedChunkReadStream(
                                    expected,
                                    8 * 1024,
                                    TimeSpan.FromMilliseconds(100)),
                                expected.Length))),
                        TimeSpan.FromSeconds(2)))
                    {
                        bool canceled = false;
                        try
                        {
                            pipeline.DownloadFileFromUrlAsync(
                                "https://tlu.dl.delivery.mp.microsoft.com/package",
                                downloadPath,
                                expected.Length,
                                new DirectProgress<OperationProgress>(value =>
                                {
                                    if (value == null ||
                                        !string.Equals(value.Message, "下载微软官方程序包", StringComparison.Ordinal))
                                    {
                                        return;
                                    }
                                    reports.Add(value);
                                    if (value.DisplayPercent.HasValue &&
                                        reports.Count(previous => previous.DisplayPercent == value.DisplayPercent) >= 2)
                                    {
                                        cancellation.Cancel();
                                    }
                                }),
                                cancellation.Token).GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException)
                        {
                            canceled = true;
                        }

                        Assert(canceled &&
                            reports.GroupBy(value => value.DisplayPercent).Any(group => group.Count() >= 2) &&
                            reports.All(value => value.Detail != null &&
                                value.Detail.IndexOf("MiB/s", StringComparison.Ordinal) >= 0),
                            "完整下载没有在同一整数百分比内按时间刷新实时下载速度。");
                    }
                }
                finally
                {
                    if (File.Exists(downloadPath)) File.Delete(downloadPath);
                }
            }
        }

        private static void TestDownloadRetriesInternalServerError()
        {
            string downloadPath = Path.Combine(
                Path.GetTempPath(),
                "CodexPortableManager-http-500-" + Guid.NewGuid().ToString("N") + ".msix");
            byte[] expected = Enumerable.Range(0, 4096)
                .Select(value => (byte)(value % 251))
                .ToArray();
            int requests = 0;
            try
            {
                using (ArtifactPipeline pipeline = new ArtifactPipeline(
                    delegate { },
                    (file, arguments, token) => Task.FromResult(new ProcessResult()),
                    new TestHttpMessageHandler((request, attempt) =>
                    {
                        Interlocked.Increment(ref requests);
                        return Task.FromResult(attempt == 1
                            ? CreateResponse(request, HttpStatusCode.InternalServerError, string.Empty)
                            : CreateStreamingResponse(
                                request,
                                HttpStatusCode.OK,
                                new MemoryStream(expected, false),
                                expected.Length));
                    }),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromSeconds(2),
                    new NetworkAvailabilityMonitor(() => true)))
                {
                    pipeline.DownloadFileAsync(
                        "https://tlu.dl.delivery.mp.microsoft.com/package",
                        downloadPath,
                        expected.Length,
                        new DirectProgress<OperationProgress>(delegate { }),
                        new OperationPauseToken(null),
                        CancellationToken.None).GetAwaiter().GetResult();
                }
                Assert(requests == 2 && File.ReadAllBytes(downloadPath).SequenceEqual(expected),
                    "微软 CDN 返回 HTTP 500 后没有自动重试并完成下载。");
            }
            finally
            {
                if (File.Exists(downloadPath)) File.Delete(downloadPath);
            }
        }

        private static void TestDownloadResumeAfterInterruption()
        {
            string downloadPath = Path.Combine(
                Path.GetTempPath(),
                "CodexPortableManager-resume-" + Guid.NewGuid().ToString("N") + ".msix");
            byte[] expected = Enumerable.Range(0, 256 * 1024)
                .Select(value => (byte)(value % 251))
                .ToArray();
            const int interruptionOffset = 96 * 1024;
            int requests = 0;
            bool accurateDownloadPercentObserved = false;
            bool networkWaitObserved = false;
            bool automaticRecoveryLogged = false;
            try
            {
                using (ArtifactPipeline pipeline = new ArtifactPipeline(
                    message => automaticRecoveryLogged = automaticRecoveryLogged ||
                        message.IndexOf("网络已恢复", StringComparison.Ordinal) >= 0,
                    (file, arguments, token) => Task.FromResult(new ProcessResult()),
                    new TestHttpMessageHandler((request, attempt) =>
                    {
                        requests++;
                        if (attempt == 1)
                        {
                            Assert(request.Headers.Range == null,
                                "首次下载请求不应携带 Range。");
                            return Task.FromResult(CreateStreamingResponse(
                                request,
                                HttpStatusCode.OK,
                                new InterruptingReadStream(expected, interruptionOffset),
                                expected.Length));
                        }

                        Assert(request.Headers.Range != null &&
                            request.Headers.Range.Ranges.Count == 1 &&
                            request.Headers.Range.Ranges.First().From == interruptionOffset &&
                            !request.Headers.Range.Ranges.First().To.HasValue,
                            "连接中断后的请求没有从已保留字节位置续传。");
                        MemoryStream remaining = new MemoryStream(
                            expected,
                            interruptionOffset,
                            expected.Length - interruptionOffset,
                            false);
                        HttpResponseMessage response = CreateStreamingResponse(
                            request,
                            HttpStatusCode.PartialContent,
                            remaining,
                            expected.Length - interruptionOffset);
                        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                            interruptionOffset,
                            expected.Length - 1,
                            expected.Length);
                        return Task.FromResult(response);
                    }),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromSeconds(2)))
                using (OperationPauseTokenSource pauseSource = new OperationPauseTokenSource())
                {
                    string digest = pipeline.DownloadFileAsync(
                        "https://tlu.dl.delivery.mp.microsoft.com/package",
                        downloadPath,
                        expected.Length,
                        new DirectProgress<OperationProgress>(value =>
                        {
                            networkWaitObserved = networkWaitObserved || value.IsNetworkWaiting;
                            if (value.DisplayPercent.HasValue &&
                                value.Percent.HasValue &&
                                value.DisplayPercent.Value != value.Percent.Value)
                            {
                                accurateDownloadPercentObserved = true;
                            }
                        }),
                        pauseSource.Token,
                        CancellationToken.None).GetAwaiter().GetResult();
                    Assert(requests == 2, "下载连接中断后没有只发起一次 Range 恢复请求。");
                    Assert(accurateDownloadPercentObserved,
                        "下载字节百分比仍与内部工作流加权进度混用。");
                    Assert(networkWaitObserved && automaticRecoveryLogged,
                        "网络中断后没有显示自动等待状态，或恢复后没有记录自动继续。");
                    Assert(File.ReadAllBytes(downloadPath).SequenceEqual(expected),
                        "Range 恢复后的文件内容不完整或发生重复拼接。");
                    using (SHA256 sha256 = SHA256.Create())
                    {
                        Assert(digest == Convert.ToBase64String(sha256.ComputeHash(expected)),
                            "Range 恢复后的流式 SHA-256 不正确。");
                    }
                }
            }
            finally
            {
                if (File.Exists(downloadPath)) File.Delete(downloadPath);
            }
        }

        private static void TestDownloadRecoveryWindowUsesElapsedTime()
        {
            string downloadPath = Path.Combine(
                Path.GetTempPath(),
                "CodexPortableManager-recovery-window-" + Guid.NewGuid().ToString("N") + ".msix");
            int requests = 0;
            using (CancellationTokenSource cancellation = new CancellationTokenSource(
                TimeSpan.FromSeconds(2)))
            {
                try
                {
                    using (ArtifactPipeline pipeline = new ArtifactPipeline(
                        delegate { },
                        (file, arguments, token) => Task.FromResult(new ProcessResult()),
                        new TestHttpMessageHandler(async (request, attempt, cancellationToken) =>
                        {
                            Interlocked.Increment(ref requests);
                            await Task.Delay(
                                TimeSpan.FromMilliseconds(80),
                                cancellationToken).ConfigureAwait(false);
                            throw new HttpRequestException("模拟连接在返回响应前失败");
                        }),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromMilliseconds(1),
                        TimeSpan.FromMilliseconds(220)))
                    {
                        bool expired = false;
                        try
                        {
                            pipeline.DownloadFileAsync(
                                "https://tlu.dl.delivery.mp.microsoft.com/package",
                                downloadPath,
                                4096,
                                new DirectProgress<OperationProgress>(delegate { }),
                                new OperationPauseToken(null),
                                cancellation.Token).GetAwaiter().GetResult();
                        }
                        catch (InvalidOperationException exception)
                        {
                            expired = exception.Message.IndexOf("恢复窗口", StringComparison.Ordinal) >= 0;
                        }
                        Assert(expired && requests >= 1 && requests <= 5,
                            "下载恢复窗口没有把请求失败前耗时计入真实的无进展时间。");
                    }
                }
                finally
                {
                    if (File.Exists(downloadPath)) File.Delete(downloadPath);
                }
            }
        }

        private static void TestDownloadRecoveryProbesWhenSystemReportsOffline()
        {
            string downloadPath = Path.Combine(
                Path.GetTempPath(),
                "CodexPortableManager-offline-probe-" + Guid.NewGuid().ToString("N") + ".msix");
            byte[] expected = Enumerable.Range(0, 4096)
                .Select(value => (byte)(value % 251))
                .ToArray();
            int requests = 0;
            bool offlineWaitObserved = false;
            Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                using (ArtifactPipeline pipeline = new ArtifactPipeline(
                    delegate { },
                    (file, arguments, token) => Task.FromResult(new ProcessResult()),
                    new TestHttpMessageHandler((request, attempt) =>
                    {
                        Interlocked.Increment(ref requests);
                        if (attempt == 1)
                        {
                            throw new HttpRequestException("模拟首次连接中断");
                        }
                        return Task.FromResult(CreateStreamingResponse(
                            request,
                            HttpStatusCode.OK,
                            new MemoryStream(expected, false),
                            expected.Length));
                    }),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromMilliseconds(40),
                    TimeSpan.FromSeconds(2),
                    new NetworkAvailabilityMonitor(() => false)))
                {
                    pipeline.DownloadFileAsync(
                        "https://tlu.dl.delivery.mp.microsoft.com/package",
                        downloadPath,
                        expected.Length,
                        new DirectProgress<OperationProgress>(value =>
                        {
                            if (value != null && value.Message == "网络不可用，已自动暂停")
                            {
                                offlineWaitObserved = true;
                            }
                        }),
                        new OperationPauseToken(null),
                        CancellationToken.None).GetAwaiter().GetResult();
                }
                Assert(requests == 2 && offlineWaitObserved &&
                    elapsed.Elapsed < TimeSpan.FromSeconds(1) &&
                    File.ReadAllBytes(downloadPath).SequenceEqual(expected),
                    "系统持续报告离线时没有按退避截止点主动探测 CDN 并恢复下载。");
            }
            finally
            {
                elapsed.Stop();
                if (File.Exists(downloadPath)) File.Delete(downloadPath);
            }
        }

        private static void TestDownloadRangeFallback()
        {
            string downloadPath = Path.Combine(
                Path.GetTempPath(),
                "CodexPortableManager-range-fallback-" + Guid.NewGuid().ToString("N") + ".msix");
            byte[] expected = Enumerable.Range(0, 4096).Select(value => (byte)(value % 239)).ToArray();
            File.WriteAllBytes(downloadPath, expected.Take(512).ToArray());
            try
            {
                using (ArtifactPipeline pipeline = new ArtifactPipeline(
                    delegate { },
                    (file, arguments, token) => Task.FromResult(new ProcessResult()),
                    new TestHttpMessageHandler((request, attempt) =>
                    {
                        Assert(request.Headers.Range != null &&
                            request.Headers.Range.Ranges.First().From == 512,
                            "已有临时文件时没有发出 Range 请求。");
                        return Task.FromResult(CreateStreamingResponse(
                            request,
                            HttpStatusCode.OK,
                            new MemoryStream(expected, false),
                            expected.Length));
                    })))
                {
                    pipeline.DownloadFileFromUrlAsync(
                        "https://tlu.dl.delivery.mp.microsoft.com/package",
                        downloadPath,
                        expected.Length,
                        new DirectProgress<OperationProgress>(delegate { }),
                        CancellationToken.None).GetAwaiter().GetResult();
                    Assert(File.ReadAllBytes(downloadPath).SequenceEqual(expected),
                        "CDN 忽略 Range 时没有安全清空临时文件并从头下载。");
                }
            }
            finally
            {
                if (File.Exists(downloadPath)) File.Delete(downloadPath);
            }
        }

        private static void TestDownloadInvalidRangeRejected()
        {
            string downloadPath = Path.Combine(
                Path.GetTempPath(),
                "CodexPortableManager-invalid-range-" + Guid.NewGuid().ToString("N") + ".msix");
            byte[] expected = Enumerable.Range(0, 4096).Select(value => (byte)(value % 211)).ToArray();
            byte[] prefix = expected.Take(512).ToArray();
            File.WriteAllBytes(downloadPath, prefix);
            try
            {
                using (ArtifactPipeline pipeline = new ArtifactPipeline(
                    delegate { },
                    (file, arguments, token) => Task.FromResult(new ProcessResult()),
                    new TestHttpMessageHandler((request, attempt) =>
                    {
                        HttpResponseMessage response = CreateStreamingResponse(
                            request,
                            HttpStatusCode.PartialContent,
                            new MemoryStream(expected, 512, expected.Length - 512, false),
                            expected.Length - 512);
                        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                            0,
                            expected.Length - 513,
                            expected.Length);
                        return Task.FromResult(response);
                    })))
                {
                    bool rejected = false;
                    try
                    {
                        pipeline.DownloadFileFromUrlAsync(
                            "https://tlu.dl.delivery.mp.microsoft.com/package",
                            downloadPath,
                            expected.Length,
                            new DirectProgress<OperationProgress>(delegate { }),
                            CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (InvalidDataException)
                    {
                        rejected = true;
                    }
                    Assert(rejected && File.ReadAllBytes(downloadPath).SequenceEqual(prefix),
                        "无效 Content-Range 未被拒绝，或拒绝前改写了已有断点。");
                }
            }
            finally
            {
                if (File.Exists(downloadPath)) File.Delete(downloadPath);
            }
        }

        private static void TestDownloadPauseAndResume()
        {
            string downloadPath = Path.Combine(
                Path.GetTempPath(),
                "CodexPortableManager-pause-" + Guid.NewGuid().ToString("N") + ".msix");
            byte[] expected = Enumerable.Range(0, 8192).Select(value => (byte)(value % 227)).ToArray();
            TrackingReadStream stream = new TrackingReadStream(expected);
            int requests = 0;
            try
            {
                using (ArtifactPipeline pipeline = new ArtifactPipeline(
                    delegate { },
                    (file, arguments, token) => Task.FromResult(new ProcessResult()),
                    new TestHttpMessageHandler((request, attempt) =>
                    {
                        Interlocked.Increment(ref requests);
                        return Task.FromResult(CreateStreamingResponse(
                            request,
                            HttpStatusCode.OK,
                            stream,
                            expected.Length));
                    })))
                using (OperationPauseTokenSource pauseSource = new OperationPauseTokenSource())
                {
                    pauseSource.Pause();
                    Task<string> download = pipeline.DownloadFileFromUrlAsync(
                        "https://tlu.dl.delivery.mp.microsoft.com/package",
                        downloadPath,
                        expected.Length,
                        new DirectProgress<OperationProgress>(delegate { }),
                        pauseSource.Token,
                        CancellationToken.None);
                    Thread.Sleep(50);
                    Assert(!download.IsCompleted &&
                        Volatile.Read(ref requests) == 0 &&
                        stream.ReadCount == 0,
                        "暂停状态下下载器仍在建立连接或读取网络响应。");
                    pauseSource.Resume();
                    download.GetAwaiter().GetResult();
                    Assert(Volatile.Read(ref requests) == 1 &&
                        stream.ReadCount > 0 &&
                        File.ReadAllBytes(downloadPath).SequenceEqual(expected),
                        "继续下载后没有完成剩余网络读取。");
                }
            }
            finally
            {
                if (File.Exists(downloadPath)) File.Delete(downloadPath);
            }
        }

        private static void TestPauseInterruptsUnresponsiveDownload()
        {
            string downloadPath = Path.Combine(
                Path.GetTempPath(),
                "CodexPortableManager-pause-unresponsive-" + Guid.NewGuid().ToString("N") + ".msix");
            byte[] expected = Enumerable.Range(0, 16384).Select(value => (byte)(value % 229)).ToArray();
            UncancellableReadStream stalled = new UncancellableReadStream();
            int requests = 0;
            try
            {
                using (ArtifactPipeline pipeline = new ArtifactPipeline(
                    delegate { },
                    (file, arguments, token) => Task.FromResult(new ProcessResult()),
                    new TestHttpMessageHandler((request, attempt) =>
                    {
                        Interlocked.Increment(ref requests);
                        return Task.FromResult(CreateStreamingResponse(
                            request,
                            HttpStatusCode.OK,
                            attempt == 1 ? (Stream)stalled : new MemoryStream(expected, false),
                            expected.Length));
                    }),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromSeconds(10)))
                using (OperationPauseTokenSource pauseSource = new OperationPauseTokenSource())
                {
                    Task<string> download = pipeline.DownloadFileAsync(
                        "https://tlu.dl.delivery.mp.microsoft.com/package",
                        downloadPath,
                        expected.Length,
                        new DirectProgress<OperationProgress>(delegate { }),
                        pauseSource.Token,
                        CancellationToken.None);
                    Assert(stalled.ReadStarted.Wait(TimeSpan.FromSeconds(2)),
                        "测试下载没有进入模拟的无响应读取。");
                    pauseSource.Pause();
                    Assert(stalled.Disposed.Wait(TimeSpan.FromSeconds(2)),
                        "暂停后没有主动释放无响应的网络流。");
                    pauseSource.Resume();
                    Task completed = Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(3)))
                        .GetAwaiter().GetResult();
                    Assert(completed == download,
                        "继续下载后没有及时重建被中断的请求。");
                    download.GetAwaiter().GetResult();
                    Assert(Volatile.Read(ref requests) == 2 &&
                        File.ReadAllBytes(downloadPath).SequenceEqual(expected),
                        "暂停并继续后没有从安全断点完成下载。");
                }
            }
            finally
            {
                stalled.Dispose();
                if (File.Exists(downloadPath)) File.Delete(downloadPath);
            }
        }

        private static void TestCancellationInterruptsUnresponsiveDownload()
        {
            string downloadPath = Path.Combine(
                Path.GetTempPath(),
                "CodexPortableManager-cancel-unresponsive-" + Guid.NewGuid().ToString("N") + ".msix");
            UncancellableReadStream stalled = new UncancellableReadStream();
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                try
                {
                    using (ArtifactPipeline pipeline = new ArtifactPipeline(
                        delegate { },
                        (file, arguments, token) => Task.FromResult(new ProcessResult()),
                        new TestHttpMessageHandler((request, attempt) => Task.FromResult(
                            CreateStreamingResponse(
                                request,
                                HttpStatusCode.OK,
                                stalled,
                                16384))),
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromMilliseconds(1),
                        TimeSpan.FromSeconds(10)))
                    {
                        Task<string> download = pipeline.DownloadFileAsync(
                            "https://tlu.dl.delivery.mp.microsoft.com/package",
                            downloadPath,
                            16384,
                            new DirectProgress<OperationProgress>(delegate { }),
                            new OperationPauseToken(null),
                            cancellation.Token);
                        Assert(stalled.ReadStarted.Wait(TimeSpan.FromSeconds(2)),
                            "取消测试没有进入模拟的无响应读取。");
                        cancellation.Cancel();
                        Assert(stalled.Disposed.Wait(TimeSpan.FromSeconds(2)),
                            "取消后没有主动释放无响应的网络流。");
                        Task completed = Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(3)))
                            .GetAwaiter().GetResult();
                        Assert(completed == download,
                            "取消后下载任务没有在秒级退出。");
                        bool canceled = false;
                        try
                        {
                            download.GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException)
                        {
                            canceled = true;
                        }
                        Assert(canceled, "无响应下载取消后没有传播取消结果。");
                    }
                }
                finally
                {
                    stalled.Dispose();
                    if (File.Exists(downloadPath)) File.Delete(downloadPath);
                }
            }
        }

        private static void TestExplicitRangeReaderValidation()
        {
            byte[] package = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
            int requests = 0;
            using (ArtifactPipeline pipeline = new ArtifactPipeline(
                delegate { },
                (file, arguments, token) => Task.FromResult(new ProcessResult()),
                new TestHttpMessageHandler((request, attempt) =>
                {
                    requests++;
                    RangeItemHeaderValue range = request.Headers.Range.Ranges.Single();
                    Assert(range.From.HasValue && range.To.HasValue,
                        "明确 Range 读取没有同时携带起止位置。");
                    if (attempt == 1)
                    {
                        HttpResponseMessage redirect = CreateResponse(request, HttpStatusCode.Redirect, string.Empty);
                        redirect.Headers.Location = new Uri("https://tlu.dl.delivery.mp.microsoft.com/range-target");
                        return Task.FromResult(redirect);
                    }
                    int start = checked((int)range.From.Value);
                    int length = checked((int)(range.To.Value - range.From.Value + 1));
                    HttpResponseMessage response = CreateStreamingResponse(
                        request,
                        HttpStatusCode.PartialContent,
                        new MemoryStream(package, start, length, false),
                        length);
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                        start,
                        start + length - 1,
                        package.Length);
                    response.Headers.ETag = new EntityTagHeaderValue("\"fixture-etag\"");
                    return Task.FromResult(response);
                })))
            {
                RemoteRangeReader reader = new RemoteRangeReader(
                    pipeline,
                    "https://tlu.dl.delivery.mp.microsoft.com/range-source",
                    package.Length,
                    new OperationPauseToken(null));
                byte[] first = reader.ReadRangeAsync(128, 512, true, CancellationToken.None).GetAwaiter().GetResult();
                byte[] cached = reader.ReadRangeAsync(128, 512, false, CancellationToken.None).GetAwaiter().GetResult();
                Assert(first.SequenceEqual(package.Skip(128).Take(512)) && cached.SequenceEqual(first),
                    "明确 Range 读取或缓存内容不正确。");
                Assert(requests == 2 && reader.RequestCount == 1,
                    "重定向后的 Range 请求数或缓存命中统计不正确。");
            }
        }

        private static void TestExplicitRangePauseInterruptsUnresponsiveDownload()
        {
            byte[] expected = Enumerable.Range(0, 4096).Select(value => (byte)(value % 241)).ToArray();
            UncancellableReadStream stalled = new UncancellableReadStream();
            int requests = 0;
            try
            {
                using (ArtifactPipeline pipeline = new ArtifactPipeline(
                    delegate { },
                    (file, arguments, token) => Task.FromResult(new ProcessResult()),
                    new TestHttpMessageHandler((request, attempt) =>
                    {
                        Interlocked.Increment(ref requests);
                        HttpResponseMessage response = CreateStreamingResponse(
                            request,
                            HttpStatusCode.PartialContent,
                            attempt == 1 ? (Stream)stalled : new MemoryStream(expected, false),
                            expected.Length);
                        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                            0,
                            expected.Length - 1,
                            expected.Length);
                        response.Headers.ETag = new EntityTagHeaderValue("\"range-pause-etag\"");
                        return Task.FromResult(response);
                    }),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromSeconds(10)))
                using (OperationPauseTokenSource pauseSource = new OperationPauseTokenSource())
                {
                    RemoteRangeReader reader = new RemoteRangeReader(
                        pipeline,
                        "https://tlu.dl.delivery.mp.microsoft.com/package",
                        expected.Length,
                        pauseSource.Token);
                    Task<byte[]> read = reader.ReadRangeAsync(
                        0,
                        expected.Length,
                        false,
                        CancellationToken.None);
                    Assert(stalled.ReadStarted.Wait(TimeSpan.FromSeconds(2)),
                        "Range 暂停测试没有进入模拟的无响应读取。");
                    pauseSource.Pause();
                    Assert(stalled.Disposed.Wait(TimeSpan.FromSeconds(2)),
                        "暂停增量下载后没有主动释放无响应的 Range 流。");
                    pauseSource.Resume();
                    Task completed = Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(3)))
                        .GetAwaiter().GetResult();
                    Assert(completed == read,
                        "继续增量下载后没有及时重建 Range 请求。");
                    Assert(read.GetAwaiter().GetResult().SequenceEqual(expected) &&
                        Volatile.Read(ref requests) == 2,
                        "继续增量下载后 Range 内容或请求次数不正确。");
                }
            }
            finally
            {
                stalled.Dispose();
            }
        }

        private static void TestExplicitRangeImmediateRetryInterruptsUnresponsiveDownload()
        {
            byte[] expected = Enumerable.Range(0, 4096).Select(value => (byte)(value % 233)).ToArray();
            UncancellableReadStream stalled = new UncancellableReadStream();
            int requests = 0;
            try
            {
                using (ArtifactPipeline pipeline = new ArtifactPipeline(
                    delegate { },
                    (file, arguments, token) => Task.FromResult(new ProcessResult()),
                    new TestHttpMessageHandler((request, attempt) =>
                    {
                        Interlocked.Increment(ref requests);
                        HttpResponseMessage response = CreateStreamingResponse(
                            request,
                            HttpStatusCode.PartialContent,
                            attempt == 1 ? (Stream)stalled : new MemoryStream(expected, false),
                            expected.Length);
                        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                            0,
                            expected.Length - 1,
                            expected.Length);
                        response.Headers.ETag = new EntityTagHeaderValue("\"range-retry-etag\"");
                        return Task.FromResult(response);
                    }),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(10)))
                using (OperationPauseTokenSource pauseSource = new OperationPauseTokenSource())
                {
                    RemoteRangeReader reader = new RemoteRangeReader(
                        pipeline,
                        "https://tlu.dl.delivery.mp.microsoft.com/package",
                        expected.Length,
                        pauseSource.Token);
                    Task<byte[]> read = reader.ReadRangeAsync(
                        0,
                        expected.Length,
                        false,
                        CancellationToken.None);
                    Assert(stalled.ReadStarted.Wait(TimeSpan.FromSeconds(2)),
                        "立即重试测试没有进入模拟的无响应 Range 读取。");
                    pauseSource.RequestRetry();
                    Assert(stalled.Disposed.Wait(TimeSpan.FromSeconds(2)),
                        "立即重试没有主动释放无响应的 Range 流。");
                    Task completed = Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(3)))
                        .GetAwaiter().GetResult();
                    Assert(completed == read &&
                        read.GetAwaiter().GetResult().SequenceEqual(expected) &&
                        Volatile.Read(ref requests) == 2,
                        "立即重试没有及时重建 Range 请求并完成读取。");
                }
            }
            finally
            {
                stalled.Dispose();
            }
        }

        private static void TestExplicitRangeResumePreservesPartialBytes()
        {
            byte[] expected = Enumerable.Range(0, 8192).Select(value => (byte)(value % 239)).ToArray();
            const int interruptionOffset = 3072;
            int requests = 0;
            using (ArtifactPipeline pipeline = new ArtifactPipeline(
                delegate { },
                (file, arguments, token) => Task.FromResult(new ProcessResult()),
                new TestHttpMessageHandler((request, attempt) =>
                {
                    Interlocked.Increment(ref requests);
                    RangeItemHeaderValue requested = request.Headers.Range.Ranges.Single();
                    int start = checked((int)requested.From.Value);
                    int end = checked((int)requested.To.Value);
                    Assert(end == expected.Length - 1 &&
                        (attempt == 1 ? start == 0 : start == interruptionOffset),
                        "Range 中断后的请求没有从已接收字节之后继续。");
                    int length = end - start + 1;
                    Stream stream = attempt == 1
                        ? (Stream)new InterruptingReadStream(expected, interruptionOffset)
                        : new MemoryStream(expected, start, length, false);
                    HttpResponseMessage response = CreateStreamingResponse(
                        request,
                        HttpStatusCode.PartialContent,
                        stream,
                        length);
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                        start,
                        end,
                        expected.Length);
                    response.Headers.ETag = new EntityTagHeaderValue("\"partial-resume-etag\"");
                    return Task.FromResult(response);
                }),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(5)))
            {
                RemoteRangeReader reader = new RemoteRangeReader(
                    pipeline,
                    "https://tlu.dl.delivery.mp.microsoft.com/package",
                    expected.Length,
                    new OperationPauseToken(null));
                byte[] actual = reader.ReadRangeAsync(
                    0,
                    expected.Length,
                    false,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert(actual.SequenceEqual(expected) &&
                    Volatile.Read(ref requests) == 2 &&
                    reader.NetworkBytesRead == expected.Length,
                    "Range 部分成功字节没有保留，或重试重复下载了已接收内容。");
            }
        }

        private static void TestExplicitRangeReportsRealtimeSpeed()
        {
            byte[] expected = Enumerable.Range(0, 64 * 1024).Select(value => (byte)(value % 229)).ToArray();
            List<OperationProgress> reports = new List<OperationProgress>();
            using (ArtifactPipeline pipeline = new ArtifactPipeline(
                delegate { },
                (file, arguments, token) => Task.FromResult(new ProcessResult()),
                new TestHttpMessageHandler((request, attempt) =>
                {
                    HttpResponseMessage response = CreateStreamingResponse(
                        request,
                        HttpStatusCode.PartialContent,
                        new PacedChunkReadStream(expected, 8 * 1024, TimeSpan.FromMilliseconds(100)),
                        expected.Length);
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                        0,
                        expected.Length - 1,
                        expected.Length);
                    response.Headers.ETag = new EntityTagHeaderValue("\"realtime-progress-etag\"");
                    return Task.FromResult(response);
                }),
                TimeSpan.FromSeconds(2)))
            {
                RemoteRangeReader reader = new RemoteRangeReader(
                    pipeline,
                    "https://tlu.dl.delivery.mp.microsoft.com/package",
                    expected.Length,
                    new OperationPauseToken(null),
                    new DirectProgress<OperationProgress>(value => reports.Add(value)));
                reader.UpdateMaterializationProgress(0, expected.Length, 0);
                byte[] actual = reader.ReadRangeAsync(
                    0,
                    expected.Length,
                    false,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert(actual.SequenceEqual(expected) &&
                    reports.Any(value => value.DisplayPercent.HasValue &&
                        value.DisplayPercent.Value > 0 && value.DisplayPercent.Value < 100) &&
                    reports.Any(value => value.Detail != null &&
                        value.Detail.IndexOf("MiB/s", StringComparison.Ordinal) >= 0),
                    "增量 Range 完成前没有上报中间进度或实时下载速度。");
            }
        }

        private static void TestExplicitRangeReaderRejectsIgnoredAndInvalidResponses()
        {
            byte[] package = Enumerable.Range(0, 1024).Select(value => (byte)(value % 239)).ToArray();
            using (ArtifactPipeline ignoredPipeline = new ArtifactPipeline(
                delegate { },
                (file, arguments, token) => Task.FromResult(new ProcessResult()),
                new TestHttpMessageHandler((request, attempt) => Task.FromResult(
                    CreateStreamingResponse(request, HttpStatusCode.OK, new MemoryStream(package, false), package.Length)))))
            {
                RemoteRangeReader reader = new RemoteRangeReader(
                    ignoredPipeline,
                    "https://tlu.dl.delivery.mp.microsoft.com/package",
                    package.Length,
                    new OperationPauseToken(null));
                bool rejected = false;
                try
                {
                    reader.ReadRangeAsync(0, 128, false, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (InvalidDataException)
                {
                    rejected = true;
                }
                Assert(rejected, "CDN 返回 200 忽略明确 Range 时没有被拒绝。");
            }

            using (ArtifactPipeline invalidPipeline = new ArtifactPipeline(
                delegate { },
                (file, arguments, token) => Task.FromResult(new ProcessResult()),
                new TestHttpMessageHandler((request, attempt) =>
                {
                    HttpResponseMessage response = CreateStreamingResponse(
                        request,
                        HttpStatusCode.PartialContent,
                        new MemoryStream(package, 0, 128, false),
                        128);
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1, 128, package.Length);
                    return Task.FromResult(response);
                })))
            {
                RemoteRangeReader reader = new RemoteRangeReader(
                    invalidPipeline,
                    "https://tlu.dl.delivery.mp.microsoft.com/package",
                    package.Length,
                    new OperationPauseToken(null));
                bool rejected = false;
                try
                {
                    reader.ReadRangeAsync(0, 128, false, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (InvalidDataException)
                {
                    rejected = true;
                }
                Assert(rejected, "错误 Content-Range 没有被拒绝。");
            }
        }

        private static void TestExplicitRangeReaderRejectsEntityTagChanges()
        {
            byte[] package = Enumerable.Range(0, 1024).Select(value => (byte)(value % 227)).ToArray();
            using (ArtifactPipeline pipeline = new ArtifactPipeline(
                delegate { },
                (file, arguments, token) => Task.FromResult(new ProcessResult()),
                new TestHttpMessageHandler((request, attempt) =>
                {
                    RangeItemHeaderValue range = request.Headers.Range.Ranges.Single();
                    int start = checked((int)range.From.Value);
                    int length = checked((int)(range.To.Value - range.From.Value + 1));
                    HttpResponseMessage response = CreateStreamingResponse(
                        request,
                        HttpStatusCode.PartialContent,
                        new MemoryStream(package, start, length, false),
                        length);
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                        start,
                        start + length - 1,
                        package.Length);
                    response.Headers.ETag = new EntityTagHeaderValue(attempt == 1 ? "\"etag-before\"" : "\"etag-after\"");
                    return Task.FromResult(response);
                })))
            {
                RemoteRangeReader reader = new RemoteRangeReader(
                    pipeline,
                    "https://tlu.dl.delivery.mp.microsoft.com/package",
                    package.Length,
                    new OperationPauseToken(null));
                reader.ReadRangeAsync(0, 64, false, CancellationToken.None).GetAwaiter().GetResult();
                bool rejected = false;
                try
                {
                    reader.ReadRangeAsync(64, 64, false, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (InvalidDataException)
                {
                    rejected = true;
                }
                Assert(rejected, "同一增量任务中强 ETag 变化没有被拒绝。");
            }
        }

        private static void TestLatestVersionCancellationDisplay()
        {
            OperationProgress running = MainWindow.CreateCheckRunningProgress();
            Assert(running.Message == "正在检查 Codex 版本与安装状态",
                "检查运行态标题没有明确显示正在检查。");
            Assert(!running.Percent.HasValue,
                "检查尚未完成时不应显示确定进度。");
            Assert(running.Detail.Contains("微软最新版本"),
                "检查运行态详情没有说明微软最新版本仍在检测。");
            Assert(MainWindow.ResolveLatestVersionAfterIncompleteCheckText(
                "26.707.9981.0",
                "未完成检查") == "26.707.9981.0",
                "取消重新检查时没有保留上次成功的微软版本。");
            Assert(MainWindow.ResolveLatestVersionAfterIncompleteCheckText(
                "尚未检查",
                "未完成检查") == "未完成检查",
                "首次检查取消后仍保留了加载占位文本。");
            Assert(MainWindow.ResolveLatestVersionAfterIncompleteCheckText(
                "检测中...",
                "检查失败") == "检查失败",
                "检查失败后仍显示为正在检测。");
            OperationProgress waiting = new OperationProgress(
                "网络不可用，已自动暂停",
                10,
                "等待网络恢复。",
                true,
                20,
                true);
            Assert(MainWindow.ResolvePauseButtonText(false, waiting) == "立即重试" &&
                MainWindow.ResolvePauseButtonText(true, waiting) == "继续下载" &&
                MainWindow.ResolvePauseButtonText(false, null) == "暂停下载",
                "网络等待、手动暂停和正常下载的按钮文案没有明确区分。");
        }

        private static string SyncResponse(string fullName, string digest, string sha1, long size)
        {
            string updateId = "61ef02c8-ab21-4318-aa8a-47ccb1d8b9dc";
            return "<Root><UpdateInfo><ID>42</ID><Xml><UpdateIdentity UpdateID=\"" + updateId +
                "\" RevisionNumber=\"1\"/><ApplicabilityRules><Metadata><AppxPackageMetadata><AppxMetadata " +
                "PackageMoniker=\"" + fullName + "\"/></AppxPackageMetadata></Metadata></ApplicabilityRules></Xml></UpdateInfo>" +
                "<ExtendedUpdateInfo><Updates><Update><ID>42</ID><Xml><Files><File InstallerSpecificIdentifier=\"" +
                fullName + "\" Size=\"" + size.ToString(CultureInfo.InvariantCulture) + "\" Digest=\"" + sha1 +
                "\"><AdditionalDigest Algorithm=\"SHA256\">" + digest +
                "</AdditionalDigest></File></Files></Xml></Update></Updates></ExtendedUpdateInfo></Root>";
        }

        private static HttpResponseMessage CreateResponse(
            HttpRequestMessage request,
            HttpStatusCode statusCode,
            string content)
        {
            return new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent(content ?? string.Empty, Encoding.UTF8, "application/xml")
            };
        }

        private static HttpResponseMessage CreateStreamingResponse(
            HttpRequestMessage request,
            HttpStatusCode statusCode,
            Stream stream,
            long? contentLength = null)
        {
            StreamContent content = new StreamContent(stream);
            if (contentLength.HasValue)
            {
                content.Headers.ContentLength = contentLength.Value;
            }
            return new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = content
            };
        }

        private static PackageMetadata ResolveLivePackage(string architecture)
        {
            using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexPortableManager-Tests/1.0.0");
                MicrosoftStoreProtocolClient protocolClient = new MicrosoftStoreProtocolClient(client);
                MicrosoftStoreProtocolClient.CatalogProduct product = protocolClient.GetCatalogProductAsync(
                    CodexMicrosoftStoreSource.ProductId,
                    CancellationToken.None).GetAwaiter().GetResult();
                CodexMicrosoftStoreSource.CatalogSelection selection =
                    CodexMicrosoftStoreSource.SelectLatestPackage(
                        product,
                        architecture,
                        CodexMicrosoftStoreSource.GetCurrentWindowsPlatformVersion());
                PackageMetadata package = selection.Metadata;
                foreach (string endpointName in new[] { "FE3", "FE6", "FE6CR" })
                {
                    MicrosoftStoreProtocolClient.DeliveryFile delivery = protocolClient
                        .ResolvePackageFileUsingEndpointAsync(
                            endpointName,
                            selection.WuCategoryId,
                            package.fullName,
                            architecture,
                            CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Assert(delivery.SizeInBytes == package.sizeInBytes &&
                        delivery.Sha256Digest == package.digest,
                        endpointName + " 返回的文件元数据与 Catalog 不一致。");
                    Uri endpointUri;
                    Assert(Uri.TryCreate(delivery.Url, UriKind.Absolute, out endpointUri) &&
                        MicrosoftStoreProtocolClient.IsMicrosoftDeliveryUri(endpointUri),
                        endpointName + " 没有返回可信的微软 CDN 地址。");
                    if (string.Equals(endpointName, "FE3", StringComparison.Ordinal))
                    {
                        package.url = delivery.Url;
                    }
                }
                Version version;
                Uri uri;
                Assert(Version.TryParse(package.version, out version), "实时目录版本无效。");
                Assert(Regex.IsMatch(package.fullName ?? string.Empty,
                    "^OpenAI\\.Codex_[0-9]+(?:\\.[0-9]+){3}_" + architecture + "__2p2nqsd0c76g0$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "实时目录包身份无效。");
                Assert(Convert.FromBase64String(package.digest).Length == 32, "实时目录 SHA-256 无效。");
                Assert(package.sizeInBytes > 0, "实时目录包大小无效。");
                Assert(package.packageName == "OpenAI.Codex" && package.architecture == architecture,
                    "实时目录没有返回 Codex 包名或目标架构。");
                Assert(Uri.TryCreate(package.url, UriKind.Absolute, out uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                    uri.Host.EndsWith(".delivery.mp.microsoft.com", StringComparison.OrdinalIgnoreCase),
                    "实时目录下载地址不是微软 CDN。");
                return package;
            }
        }

        private static void AppendPackage(StringBuilder report, string prefix, PackageMetadata package)
        {
            Uri parsedUrl;
            string safeUrl = Uri.TryCreate(package.url, UriKind.Absolute, out parsedUrl)
                ? parsedUrl.GetLeftPart(UriPartial.Path)
                : "<invalid>";
            report.AppendLine(prefix + "_VERSION=" + package.version);
            report.AppendLine(prefix + "_FULL_NAME=" + package.fullName);
            report.AppendLine(prefix + "_DIGEST=" + package.digest);
            report.AppendLine(prefix + "_SIZE=" + package.sizeInBytes.ToString(CultureInfo.InvariantCulture));
            report.AppendLine(prefix + "_URL=" + safeUrl);
        }

        private static string PackageJson(string fullName, string architecture, string digest, long size)
        {
            return "{\"Architectures\":[\"" + architecture + "\"],\"Hash\":\"" + digest +
                "\",\"HashAlgorithm\":\"SHA256\",\"MaxDownloadSizeInBytes\":" +
                size.ToString(CultureInfo.InvariantCulture) +
                ",\"PackageFormat\":\"Msix\",\"PackageFullName\":\"" + fullName +
                "\",\"PlatformDependencies\":[{\"PlatformName\":\"Windows.Desktop\"," +
                "\"MinVersion\":2814751014977536,\"MaxTested\":2814751477596160}]}";
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }

        private static void WriteReport(string reportPath, string contents)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(reportPath, contents, new UTF8Encoding(true));
        }

        private sealed class TestHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder;
            private int requestCount;

            internal TestHttpMessageHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> responseFactory)
                : this((request, attempt, cancellationToken) => responseFactory(request, attempt))
            {
            }

            internal TestHttpMessageHandler(
                Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responseFactory)
            {
                responder = responseFactory ?? throw new ArgumentNullException(nameof(responseFactory));
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                int attempt = Interlocked.Increment(ref requestCount);
                return responder(request, attempt, cancellationToken);
            }
        }

        private sealed class PacedReadStream : Stream
        {
            private readonly byte[] data;
            private readonly TimeSpan delay;
            private int position;

            internal PacedReadStream(byte[] contents, TimeSpan readDelay)
            {
                data = contents ?? throw new ArgumentNullException(nameof(contents));
                delay = readDelay;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return data.Length; } }

            public override long Position
            {
                get { return position; }
                set { throw new NotSupportedException(); }
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (position >= data.Length) return 0;
                buffer[offset] = data[position++];
                return 1;
            }

            public override async Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                return Read(buffer, offset, count);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class PacedChunkReadStream : Stream
        {
            private readonly byte[] data;
            private readonly int chunkSize;
            private readonly TimeSpan delay;
            private int position;

            internal PacedChunkReadStream(byte[] contents, int bytesPerRead, TimeSpan readDelay)
            {
                data = contents ?? throw new ArgumentNullException(nameof(contents));
                chunkSize = Math.Max(1, bytesPerRead);
                delay = readDelay;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return data.Length; } }
            public override long Position { get { return position; } set { throw new NotSupportedException(); } }
            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (position >= data.Length) return 0;
                int read = Math.Min(Math.Min(count, chunkSize), data.Length - position);
                Buffer.BlockCopy(data, position, buffer, offset, read);
                position += read;
                return read;
            }

            public override async Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                return Read(buffer, offset, count);
            }

            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }

        private sealed class InterruptingReadStream : Stream
        {
            private readonly byte[] data;
            private readonly int interruptionOffset;
            private int position;
            private bool interrupted;

            internal InterruptingReadStream(byte[] contents, int offset)
            {
                data = contents;
                interruptionOffset = offset;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return data.Length; } }
            public override long Position { get { return position; } set { throw new NotSupportedException(); } }
            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (!interrupted && position >= interruptionOffset)
                {
                    interrupted = true;
                    throw new IOException("模拟网络切换导致连接中断。");
                }
                if (position >= data.Length) return 0;
                int available = Math.Min(count, Math.Min(data.Length - position, interruptionOffset - position));
                Array.Copy(data, position, buffer, offset, available);
                position += available;
                return available;
            }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }

        private sealed class TrackingReadStream : Stream
        {
            private readonly MemoryStream inner;
            private int readCount;

            internal TrackingReadStream(byte[] contents)
            {
                inner = new MemoryStream(contents, false);
            }

            internal int ReadCount { get { return Volatile.Read(ref readCount); } }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return inner.Length; } }
            public override long Position { get { return inner.Position; } set { throw new NotSupportedException(); } }
            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                Interlocked.Increment(ref readCount);
                return inner.Read(buffer, offset, count);
            }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }
        }

        private sealed class UncancellableReadStream : Stream
        {
            private readonly TaskCompletionSource<int> completion = new TaskCompletionSource<int>();

            internal ManualResetEventSlim ReadStarted { get; } = new ManualResetEventSlim(false);
            internal ManualResetEventSlim Disposed { get; } = new ManualResetEventSlim(false);
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return 0; } }
            public override long Position { get { return 0; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                ReadStarted.Set();
                return completion.Task;
            }

            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Disposed.Set();
                    completion.TrySetException(new IOException("模拟忽略取消令牌的网络流被主动释放。"));
                }
                base.Dispose(disposing);
            }
        }
    }
}
