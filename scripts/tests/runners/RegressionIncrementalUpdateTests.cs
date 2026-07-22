using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CodexPortableManager
{
internal static partial class RegressionTestRunner
{
    private static void TestIncrementalPackageRebuildsExactTarget()
    {
        string caseRoot = NewCaseRoot("incremental-exact-rebuild");
        string previousPath = Path.Combine(caseRoot, "previous.msix");
        string targetPath = Path.Combine(caseRoot, "target.msix");
        string outputPath = Path.Combine(caseRoot, "materialized.msix");
        FixturePackageEntry stable = new FixturePackageEntry(
            "app/%40scope/stable.txt",
            "app\\@scope\\stable.txt",
            Encoding.UTF8.GetBytes("stable payload shared by both packages"));
        FixturePackageEntry secondStable = new FixturePackageEntry(
            "app/bin/shared.bin",
            "app\\bin\\shared.bin",
            Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        CreateFixturePackage(
            previousPath,
            new[]
            {
                stable,
                secondStable,
                new FixturePackageEntry("app/changed.txt", "app\\changed.txt", Encoding.UTF8.GetBytes("old value")),
                new FixturePackageEntry("app/removed.txt", "app\\removed.txt", Encoding.UTF8.GetBytes("removed value"))
            },
            new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
            0);
        CreateFixturePackage(
            targetPath,
            new[]
            {
                stable,
                secondStable,
                new FixturePackageEntry("app/changed.txt", "app\\changed.txt", Encoding.UTF8.GetBytes("new value with a different length")),
                new FixturePackageEntry("app/added.txt", "app\\added.txt", Encoding.UTF8.GetBytes("added value"))
            },
            new DateTimeOffset(2026, 7, 2, 11, 30, 0, TimeSpan.Zero),
            0);

        MsixZipLayout previous = MsixZipLayout.Read(previousPath);
        MsixZipLayout target = MsixZipLayout.Read(targetPath);
        MsixZipEntry encodedEntry;
        Assert(target.TryGetEntry("app/@scope/stable.txt", out encodedEntry),
            "百分号编码 ZIP 路径没有映射到 BlockMap 规范路径。");
        PackageReusePlan plan = PackageReusePlanner.Create(previous, target);
        Assert(plan.ReusedEntryCount == 2, "合成包没有只复用两个未变化文件条目。");
        Assert(plan.TargetBytes > 0 && plan.ReusedBytes > 0 && plan.SynthesizedBytes > 0,
            "合成包复用计划缺少目标补集、本地复用或合成本地头。");

        string targetDigest = IncrementalPackageMaterializer.ComputeSha256Base64(targetPath);
        PackageMaterializationResult result = IncrementalPackageMaterializer.MaterializeFromLocalTarget(
            previousPath,
            targetPath,
            outputPath,
            plan,
            targetDigest);
        Assert(result.Sha256Base64 == targetDigest, "增量物化返回的目标摘要不正确。");
        Assert(BytesEqual(File.ReadAllBytes(outputPath), File.ReadAllBytes(targetPath)),
            "旧包加目标补集没有重建逐字节一致的目标 MSIX。");
        Assert(result.ReusedEntryCount == 2 && result.TargetEntryCount == target.Entries.Count,
            "增量物化统计没有保留计划中的条目计数。");
    }

    private static void TestIncrementalPackageRejectsTamperedReuseSource()
    {
        string caseRoot = NewCaseRoot("incremental-tampered-source");
        string previousPath = Path.Combine(caseRoot, "previous.msix");
        string targetPath = Path.Combine(caseRoot, "target.msix");
        string outputPath = Path.Combine(caseRoot, "materialized.msix");
        FixturePackageEntry stable = new FixturePackageEntry(
            "app/stable.bin",
            "app\\stable.bin",
            Enumerable.Range(0, 8192).Select(value => (byte)(value % 239)).ToArray());
        CreateFixturePackage(previousPath, new[] { stable }, new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero), 0);
        CreateFixturePackage(targetPath, new[] { stable }, new DateTimeOffset(2026, 7, 2, 8, 0, 0, TimeSpan.Zero), 0);

        MsixZipLayout previous = MsixZipLayout.Read(previousPath);
        MsixZipLayout target = MsixZipLayout.Read(targetPath);
        PackageReusePlan plan = PackageReusePlanner.Create(previous, target);
        MsixZipEntry reusedEntry;
        Assert(previous.TryGetEntry("app/stable.bin", out reusedEntry), "测试旧包缺少预期复用条目。");
        using (FileStream stream = new FileStream(previousPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = reusedEntry.DataOffset + reusedEntry.CompressedSize / 2;
            int value = stream.ReadByte();
            Assert(value >= 0, "无法读取待篡改的旧包字节。");
            stream.Position--;
            stream.WriteByte((byte)(value ^ 0x5A));
            stream.Flush(true);
        }

        string targetDigest = IncrementalPackageMaterializer.ComputeSha256Base64(targetPath);
        Exception failure = CaptureFailure(delegate
        {
            IncrementalPackageMaterializer.MaterializeFromLocalTarget(
                previousPath,
                targetPath,
                outputPath,
                plan,
                targetDigest);
        });
        Assert(failure is InvalidDataException && failure.Message.IndexOf("SHA-256", StringComparison.Ordinal) >= 0,
            "旧包复用字节被篡改后没有被目标整包摘要拒绝。实际异常：" + (failure == null ? "无" : failure.ToString()));
        Assert(!File.Exists(outputPath), "摘要失败后仍保留了未验证的增量物化结果。");
    }

    private static void TestMsixLayoutRejectsAmbiguousPaths()
    {
        string caseRoot = NewCaseRoot("incremental-path-collision");
        string packagePath = Path.Combine(caseRoot, "ambiguous.msix");
        using (FileStream stream = new FileStream(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create, false))
        {
            WriteFixtureEntry(archive, "app/%40scope/file.txt", Encoding.UTF8.GetBytes("first"), DateTimeOffset.UtcNow);
            WriteFixtureEntry(archive, "app/@scope/file.txt", Encoding.UTF8.GetBytes("second"), DateTimeOffset.UtcNow);
        }
        Exception failure = CaptureFailure(delegate { MsixZipLayout.Read(packagePath); });
        Assert(failure is InvalidDataException && failure.Message.IndexOf("歧义路径", StringComparison.Ordinal) >= 0,
            "百分号解码后的重复路径没有在中央目录阶段被拒绝。");
    }

    private static void TestMsixLayoutRejectsBlockMapMismatch()
    {
        string caseRoot = NewCaseRoot("incremental-blockmap-mismatch");
        string packagePath = Path.Combine(caseRoot, "mismatch.msix");
        CreateFixturePackage(
            packagePath,
            new[]
            {
                new FixturePackageEntry("app/file.txt", "app\\file.txt", Encoding.UTF8.GetBytes("payload"))
            },
            new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
            1);
        Exception failure = CaptureFailure(delegate { MsixZipLayout.Read(packagePath); });
        Assert(failure is InvalidDataException && failure.Message.IndexOf("文件大小", StringComparison.Ordinal) >= 0,
            "BlockMap 与 ZIP 文件大小不一致时没有被拒绝。");
    }

    private static void TestMsixLayoutRejectsTruncatedPackage()
    {
        string caseRoot = NewCaseRoot("incremental-truncated-package");
        string packagePath = Path.Combine(caseRoot, "valid.msix");
        string truncatedPath = Path.Combine(caseRoot, "truncated.msix");
        CreateFixturePackage(
            packagePath,
            new[]
            {
                new FixturePackageEntry("app/file.txt", "app\\file.txt", Encoding.UTF8.GetBytes("payload"))
            },
            new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
            0);
        byte[] bytes = File.ReadAllBytes(packagePath);
        File.WriteAllBytes(truncatedPath, bytes.Take(bytes.Length - 11).ToArray());
        Exception failure = CaptureFailure(delegate { MsixZipLayout.Read(truncatedPath); });
        Assert(failure is InvalidDataException || failure is EndOfStreamException,
            "截断 MSIX 没有被布局解析器拒绝。实际异常：" + (failure == null ? "无" : failure.ToString()));
    }

    private static void TestMsixLayoutReadsZip64EndRecords()
    {
        string caseRoot = NewCaseRoot("incremental-zip64");
        string packagePath = Path.Combine(caseRoot, "regular.msix");
        string zip64Path = Path.Combine(caseRoot, "zip64.msix");
        CreateFixturePackage(
            packagePath,
            new[]
            {
                new FixturePackageEntry("app/file.txt", "app\\file.txt", Encoding.UTF8.GetBytes("payload"))
            },
            new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
            0);
        ConvertFixtureToZip64(packagePath, zip64Path);
        MsixZipLayout layout = MsixZipLayout.Read(zip64Path);
        Assert(layout.Entries.Count == 2, "ZIP64 结束记录没有保留 payload 与 BlockMap 条目。");
        Assert(layout.EndRecordsOffset > layout.CentralDirectoryOffset, "ZIP64 结束记录偏移无效。");
    }

    private static void TestIncrementalPackageReadsStandardDataDescriptors()
    {
        string caseRoot = NewCaseRoot("incremental-data-descriptor");
        string previousPath = Path.Combine(caseRoot, "previous.msix");
        string targetPath = Path.Combine(caseRoot, "target.msix");
        string outputPath = Path.Combine(caseRoot, "materialized.msix");
        FixturePackageEntry stable = new FixturePackageEntry(
            "app/stable.bin",
            "app\\stable.bin",
            Enumerable.Range(0, 2048).Select(value => (byte)(value % 197)).ToArray());
        CreateFixturePackageCore(
            previousPath,
            new[] { stable },
            new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
            0,
            true);
        CreateFixturePackageCore(
            targetPath,
            new[] { stable },
            new DateTimeOffset(2026, 7, 2, 8, 0, 0, TimeSpan.Zero),
            0,
            true);

        MsixZipLayout previous = MsixZipLayout.Read(previousPath);
        MsixZipLayout target = MsixZipLayout.Read(targetPath);
        MsixZipEntry targetEntry;
        Assert(target.TryGetEntry("app/stable.bin", out targetEntry) && targetEntry.DataDescriptorLength > 0,
            "非可定位流夹具没有生成标准 ZIP 数据描述符。");
        PackageReusePlan plan = PackageReusePlanner.Create(previous, target);
        Assert(plan.ReusedEntryCount == 1, "带标准数据描述符的稳定条目没有被复用。");
        string targetDigest = IncrementalPackageMaterializer.ComputeSha256Base64(targetPath);
        IncrementalPackageMaterializer.MaterializeFromLocalTarget(
            previousPath,
            targetPath,
            outputPath,
            plan,
            targetDigest);
        Assert(BytesEqual(File.ReadAllBytes(outputPath), File.ReadAllBytes(targetPath)),
            "带标准数据描述符的合成包没有逐字节重建。");
    }

    private static void TestRealIncrementalPackageRebuild()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CPM_RUN_LARGE_MSIX_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip("设置 CPM_RUN_LARGE_MSIX_TESTS=1 后才执行真实双包增量重建。");
        }

        string projectRoot = FindProjectRoot();
        string previousPath = Path.Combine(projectRoot, "dist", "data", "cache", "OpenAI.Codex_26.707.8479.0_x64.msix");
        string targetPath = Path.Combine(projectRoot, "output", "ui-verify", "app", "data", "cache", "OpenAI.Codex_26.707.9564.0_x64.msix");
        Assert(File.Exists(previousPath), "真实增量测试缺少旧版 MSIX：" + previousPath);
        Assert(File.Exists(targetPath), "真实增量测试缺少目标 MSIX：" + targetPath);

        MsixZipLayout previous = MsixZipLayout.Read(previousPath);
        MsixZipLayout target = MsixZipLayout.Read(targetPath);
        PackageReusePlan plan = PackageReusePlanner.Create(previous, target);
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "REAL INCREMENTAL PLAN：复用条目={0}，复用字节={1}，目标补集字节={2}，合成本地头字节={3}",
            plan.ReusedEntryCount,
            plan.ReusedBytes,
            plan.TargetBytes,
            plan.SynthesizedBytes));
        Assert(plan.ReusedEntryCount == 9505,
            "真实双包可复用文件数偏离已验证基线：" + plan.ReusedEntryCount.ToString(CultureInfo.InvariantCulture));
        Assert(plan.TargetBytes < 100L * 1024 * 1024,
            "真实双包目标补集超过 100 MiB：" + plan.TargetBytes.ToString(CultureInfo.InvariantCulture));

        string outputPath = Path.Combine(NewCaseRoot("incremental-real-rebuild"), "materialized.msix");
        string targetDigest = IncrementalPackageMaterializer.ComputeSha256Base64(targetPath);
        PackageMaterializationResult result = IncrementalPackageMaterializer.MaterializeFromLocalTarget(
            previousPath,
            targetPath,
            outputPath,
            plan,
            targetDigest);
        Assert(result.Sha256Base64 == targetDigest, "真实双包物化摘要与目标 MSIX 不一致。");
        Assert(new FileInfo(outputPath).Length == new FileInfo(targetPath).Length, "真实双包物化长度不一致。");

        PackageMetadata metadata = CreatePackageMetadata(
            "26.707.9564.0",
            "OpenAI.Codex_26.707.9564.0_x64__2p2nqsd0c76g0",
            targetDigest,
            new FileInfo(targetPath).Length);
        metadata.url = "https://tlu.dl.delivery.mp.microsoft.com/real-target.msix";
        using (VerifiedArtifactLease lease = MsixPackageTrust.VerifyAndLock(
            outputPath,
            metadata,
            "x64",
            delegate { })) { }

        string acquisitionRoot = NewCaseRoot("incremental-real-acquisition");
        string acquisitionPackagePath = Path.Combine(
            acquisitionRoot,
            "OpenAI.Codex_26.707.9564.0_x64.msix");
        string acquisitionDownloadPath = acquisitionPackagePath +
            ".download-" + Guid.NewGuid().ToString("N") + ".msix";
        using (FilePackageMessageHandler handler = new FilePackageMessageHandler(targetPath))
        using (ArtifactPipeline pipeline = new ArtifactPipeline(
            delegate { },
            (file, arguments, token) => Task.FromResult(new ProcessResult()),
            handler))
        {
            PackageAcquisitionResult acquisition = pipeline.AcquirePackageBytesAsync(
                metadata,
                Path.GetDirectoryName(previousPath),
                acquisitionPackagePath,
                acquisitionDownloadPath,
                new DirectProgress<OperationProgress>(delegate { }),
                new OperationPauseToken(null),
                CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "REAL REMOTE ACQUISITION：模式={0}，网络字节={1}，Range={2}",
                acquisition.Mode,
                acquisition.RemoteBytes,
                acquisition.RangeRequestCount));
            Assert(acquisition.Mode == PackageAcquisitionMode.Incremental &&
                acquisition.RemoteBytes < 100L * 1024 * 1024 &&
                handler.FullRequests == 0,
                "真实生产获取路径没有采用增量，或远程补集超过 100 MiB。");
            using (VerifiedArtifactLease lease = MsixPackageTrust.VerifyAndLock(
                acquisitionDownloadPath,
                metadata,
                "x64",
                delegate { })) { }
        }

        string stagingRoot = Path.Combine(NewCaseRoot("incremental-real-staging"), "staging");
        Stopwatch stagingStopwatch = Stopwatch.StartNew();
        using (StagingBuildResult staging = StagingBuilder.ExtractAndValidate(
            targetPath,
            stagingRoot,
            CancellationToken.None))
        {
            stagingStopwatch.Stop();
            PackageProfile stagedProfile = staging.Profile;
            Assert(stagedProfile != null, "真实 staging 没有返回 PackageProfile。");
            string stagedExecutable = PackageProfileReader.GetExecutablePath(stagingRoot, stagedProfile);
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "REAL STREAMING STAGING：文件={0}，字节={1}，块={2}，关键摘要={3}/{4}字节，耗时={5:F1}秒",
                staging.ExtractedFileCount,
                staging.ExtractedBytes,
                staging.VerifiedBlockCount,
                staging.OfficialArtifactDigestCount,
                staging.OfficialArtifactDigestBytes,
                stagingStopwatch.Elapsed.TotalSeconds));
            Assert(staging.ExtractedFileCount == target.Entries.Count &&
                staging.ExtractedBytes > 1536L * 1024 * 1024 &&
                staging.VerifiedBlockCount > 30000 &&
                staging.OfficialArtifactDigestCount >= 4 &&
                staging.OfficialArtifactDigestBytes > 100L * 1024 * 1024 &&
                File.Exists(stagedExecutable),
                "真实流式 staging 的文件、字节、BlockMap 块、关键摘要或主程序结果不正确。");

            string executableDirectory = Path.GetDirectoryName(stagedProfile.ExecutableRelativePath) ?? string.Empty;
            staging.ReleaseOfficialArtifactDigest(stagedProfile.ExecutableRelativePath);
            staging.ReleaseOfficialArtifactDigest(Path.Combine(
                executableDirectory,
                "resources",
                "icon-chatgpt.ico"));
            Stopwatch provenanceStopwatch = Stopwatch.StartNew();
            ArtifactProvenance provenance = ArtifactProvenance.Capture(
                stagingRoot,
                stagedProfile,
                metadata,
                null,
                staging);
            provenanceStopwatch.Stop();
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "REAL PROVENANCE：制品={0}，复用摘要={1}/{2}字节，耗时={3:F2}秒",
                provenance.Artifacts.Count,
                staging.ReusedArtifactDigestCount,
                staging.ReusedArtifactDigestBytes,
                provenanceStopwatch.Elapsed.TotalSeconds));
            Assert(staging.ReusedArtifactDigestCount >= 4 &&
                staging.ReusedArtifactDigestBytes > 500L * 1024 * 1024,
                "真实 provenance 没有复用预期的 staging 关键摘要。");

            CompatibilityOptions compatibility = new CompatibilityOptions(
                false,
                true,
                true,
                true);
            foreach (string protectedArtifact in CompatibilityMaintenance.GetStagingProtectedArtifacts(
                stagedProfile,
                compatibility))
            {
                staging.ReleaseOfficialArtifactDigest(protectedArtifact);
            }
            List<string> compatibilityLogs = new List<string>();
            CompatibilityCoordinator compatibilityCoordinator = new CompatibilityCoordinator(
                compatibilityLogs.Add);
            CompatibilityMaintenance maintenance = new CompatibilityMaintenance(
                compatibilityCoordinator.ApplyOfficialStaging,
                InstallOwnership.WriteMarker,
                compatibilityLogs.Add);
            Stopwatch compatibilityStopwatch = Stopwatch.StartNew();
            CompatibilityResult compatibilityResult = maintenance.ApplyTrustedStaging(
                stagingRoot,
                stagedProfile,
                Guid.NewGuid().ToString("N"),
                compatibility,
                provenance);
            compatibilityStopwatch.Stop();
            staging.Dispose();
            InstallationRecord stagedRecord = InstallOwnership.ReadInstallationRecord(stagingRoot);
            InstallationHealthReport stagedHealth = InstallationHealth.Evaluate(stagingRoot);
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "REAL COMPATIBILITY：提交={0}，状态={1}，耗时={2:F2}秒",
                compatibilityResult.TransactionCommitted,
                string.Join(",", compatibilityResult.FeatureResults.Select(feature =>
                    feature.FeatureId + "=" + feature.Status).ToArray()),
                compatibilityStopwatch.Elapsed.TotalSeconds));
            Assert(stagedRecord.Provenance.CompatibilityFeatures.Count == 3 &&
                stagedHealth.Status == InstallationHealthStatus.Healthy &&
                !CompatibilityTransaction.Exists(stagingRoot),
                "真实 staging 兼容设置没有记录完整状态、保持健康或清理事务。");
        }
    }

    private static void TestRemoteIncrementalPackageRebuild()
    {
        string caseRoot = NewCaseRoot("incremental-remote-rebuild");
        string previousPath = Path.Combine(caseRoot, "previous.msix");
        string targetPath = Path.Combine(caseRoot, "target.msix");
        string outputPath = Path.Combine(caseRoot, "materialized.msix");
        List<FixturePackageEntry> previousEntries = new List<FixturePackageEntry>();
        List<FixturePackageEntry> targetEntries = new List<FixturePackageEntry>();
        for (int index = 0; index < 20; index++)
        {
            byte[] contents = CreatePseudoRandomBytes(24 * 1024, 1000 + index);
            string zipName = "app/stable-" + index.ToString("D2", CultureInfo.InvariantCulture) + ".bin";
            string blockMapName = zipName.Replace('/', '\\');
            previousEntries.Add(new FixturePackageEntry(zipName, blockMapName, contents));
            targetEntries.Add(new FixturePackageEntry(zipName, blockMapName, contents));
        }
        previousEntries.Add(new FixturePackageEntry(
            "app/changed.bin",
            "app\\changed.bin",
            CreatePseudoRandomBytes(32 * 1024, 7001)));
        targetEntries.Add(new FixturePackageEntry(
            "app/changed.bin",
            "app\\changed.bin",
            CreatePseudoRandomBytes(32 * 1024, 7002)));
        CreateFixturePackage(
            previousPath,
            previousEntries,
            new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
            0);
        CreateFixturePackage(
            targetPath,
            targetEntries,
            new DateTimeOffset(2026, 7, 2, 8, 0, 0, TimeSpan.Zero),
            0);

        byte[] targetBytes = File.ReadAllBytes(targetPath);
        using (ArtifactPipeline pipeline = new ArtifactPipeline(
            delegate { },
            (file, arguments, token) => Task.FromResult(new ProcessResult()),
            new RangePackageMessageHandler(targetBytes)))
        {
            RemoteRangeReader ranges = new RemoteRangeReader(
                pipeline,
                "https://tlu.dl.delivery.mp.microsoft.com/target.msix",
                targetBytes.LongLength,
                new OperationPauseToken(null));
            MsixZipLayout remoteTarget = RemoteMsixLayoutReader.ReadAsync(
                ranges,
                "fixture-target",
                CancellationToken.None).GetAwaiter().GetResult();
            Assert(remoteTarget.IsRemote && remoteTarget.Entries.Count == targetEntries.Count + 1,
                "远程 MSIX bootstrap 没有生成完整布局。");

            MsixZipLayout previous = MsixZipLayout.Read(previousPath);
            PackageReusePlan plan = PackageReusePlanner.CreateForRemoteTarget(previous, remoteTarget);
            Assert(plan.ReusedEntryCount == 20, "远程复用计划没有复用全部稳定文件。");
            string targetDigest = IncrementalPackageMaterializer.ComputeSha256Base64(targetPath);
            IncrementalPackageMaterializer.MaterializeFromRemoteTargetAsync(
                previousPath,
                outputPath,
                plan,
                ranges,
                targetDigest,
                new DirectProgress<OperationProgress>(delegate { }),
                CancellationToken.None).GetAwaiter().GetResult();
            Assert(BytesEqual(File.ReadAllBytes(outputPath), targetBytes),
                "远程目标补集没有重建逐字节一致的目标 MSIX。");
            Assert(ranges.NetworkBytesRead < targetBytes.LongLength / 2,
                "远程 bootstrap 与补集读取退化成接近整包下载。");
        }
    }

    private static void TestIncrementalCandidateSelectionAndThreshold()
    {
        string caseRoot = NewCaseRoot("incremental-candidate-policy");
        string cacheRoot = Path.Combine(caseRoot, "cache");
        Directory.CreateDirectory(cacheRoot);
        string version1 = Path.Combine(cacheRoot, "OpenAI.Codex_1.0.0.0_x64.msix");
        string version2 = Path.Combine(cacheRoot, "OpenAI.Codex_2.0.0.0_x64.msix");
        File.WriteAllBytes(version1, new byte[] { 1 });
        File.WriteAllBytes(version2, new byte[] { 2, 2 });
        File.WriteAllBytes(Path.Combine(cacheRoot, "OpenAI.Codex_4.0.0.0_x64.msix"), new byte[] { 4 });
        File.WriteAllBytes(Path.Combine(cacheRoot, "OpenAI.Codex_2.5.0.0_arm64.msix"), new byte[] { 5 });
        PackageMetadata target = CreatePackageMetadata(
            "3.0.0.0",
            "OpenAI.Codex_3.0.0.0_x64__2p2nqsd0c76g0",
            Convert.ToBase64String(new byte[32]),
            1000);
        IList<PackageCacheCandidate> candidates = PackageCacheSelector.FindPreviousCandidates(
            cacheRoot,
            target,
            Path.Combine(cacheRoot, "OpenAI.Codex_3.0.0.0_x64.msix"));
        Assert(candidates.Count == 2 &&
            candidates[0].Version == new Version(2, 0, 0, 0) && candidates[0].Path == version2 &&
            candidates[1].Version == new Version(1, 0, 0, 0) && candidates[1].Path == version1,
            "增量候选没有返回按版本降序排列的同架构旧包。");

        PackageReusePlan plan = new PackageReusePlan(
            1000,
            new List<PackageMaterializationSegment>(),
            1,
            2,
            100,
            900,
            0);
        string reason;
        Assert(!IncrementalAcquisitionPolicy.ShouldUse(plan, 200, 0.95d, out reason) &&
            reason.IndexOf("节省量", StringComparison.Ordinal) >= 0,
            "小收益增量计划没有被保守阈值拒绝。");
        Assert(!IncrementalAcquisitionPolicy.ShouldUse(plan, 0, 0.80d, out reason) &&
            reason.IndexOf("目标补集", StringComparison.Ordinal) >= 0,
            "远程比例过高的增量计划没有被拒绝。");
    }

    private static void TestArtifactPipelineSelectsBestIncrementalCandidate()
    {
        string caseRoot = NewCaseRoot("incremental-best-candidate");
        string cacheRoot = Path.Combine(caseRoot, "cache");
        Directory.CreateDirectory(cacheRoot);
        string olderPath = Path.Combine(cacheRoot, "OpenAI.Codex_1.0.0.0_x64.msix");
        string newerPath = Path.Combine(cacheRoot, "OpenAI.Codex_2.0.0.0_x64.msix");
        string brokenPath = Path.Combine(cacheRoot, "OpenAI.Codex_2.5.0.0_x64.msix");
        string targetFixture = Path.Combine(caseRoot, "target-fixture.msix");
        string packagePath = Path.Combine(cacheRoot, "OpenAI.Codex_3.0.0.0_x64.msix");
        string downloadPath = packagePath + ".download-" + Guid.NewGuid().ToString("N") + ".msix";
        FixturePackageEntry stableFirst = new FixturePackageEntry(
            "app/stable-first.bin",
            "app\\stable-first.bin",
            CreatePseudoRandomBytes(48 * 1024, 8200));
        FixturePackageEntry stableSecond = new FixturePackageEntry(
            "app/stable-second.bin",
            "app\\stable-second.bin",
            CreatePseudoRandomBytes(48 * 1024, 8201));
        CreateFixturePackage(
            olderPath,
            new[]
            {
                stableFirst,
                stableSecond,
                new FixturePackageEntry("app/changed.bin", "app\\changed.bin", CreatePseudoRandomBytes(4096, 8202))
            },
            new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
            0);
        CreateFixturePackage(
            newerPath,
            new[]
            {
                stableFirst,
                new FixturePackageEntry("app/stable-second.bin", "app\\stable-second.bin", CreatePseudoRandomBytes(48 * 1024, 8203)),
                new FixturePackageEntry("app/changed.bin", "app\\changed.bin", CreatePseudoRandomBytes(4096, 8204))
            },
            new DateTimeOffset(2026, 7, 2, 8, 0, 0, TimeSpan.Zero),
            0);
        File.WriteAllBytes(brokenPath, new byte[] { 1, 2, 3, 4 });
        CreateFixturePackage(
            targetFixture,
            new[]
            {
                stableFirst,
                stableSecond,
                new FixturePackageEntry("app/changed.bin", "app\\changed.bin", CreatePseudoRandomBytes(4096, 8205))
            },
            new DateTimeOffset(2026, 7, 3, 8, 0, 0, TimeSpan.Zero),
            0);

        MsixZipLayout targetLayout = MsixZipLayout.Read(targetFixture);
        PackageReusePlan olderPlan = PackageReusePlanner.Create(MsixZipLayout.Read(olderPath), targetLayout);
        PackageReusePlan newerPlan = PackageReusePlanner.Create(MsixZipLayout.Read(newerPath), targetLayout);
        Assert(olderPlan.TargetBytes < newerPlan.TargetBytes,
            "多候选测试前置条件无效：旧候选没有比新候选产生更小的目标补集。");
        MsixZipLayout olderLayout = MsixZipLayout.Read(olderPath);
        MsixZipEntry tamperedEntry;
        Assert(olderLayout.TryGetEntry("app/stable-second.bin", out tamperedEntry),
            "多候选测试缺少待篡改的复用条目。");
        using (FileStream stream = new FileStream(olderPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = tamperedEntry.DataOffset + tamperedEntry.CompressedSize / 2;
            int value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(value ^ 0x40));
            stream.Flush(true);
        }

        byte[] targetBytes = File.ReadAllBytes(targetFixture);
        string digest;
        using (SHA256 sha256 = SHA256.Create())
        {
            digest = Convert.ToBase64String(sha256.ComputeHash(targetBytes));
        }
        PackageMetadata metadata = CreatePackageMetadata(
            "3.0.0.0",
            "OpenAI.Codex_3.0.0.0_x64__2p2nqsd0c76g0",
            digest,
            targetBytes.LongLength);
        metadata.url = "https://tlu.dl.delivery.mp.microsoft.com/target.msix";
        List<string> logs = new List<string>();
        AcquisitionPackageMessageHandler handler = new AcquisitionPackageMessageHandler(targetBytes, false);
        using (ArtifactPipeline pipeline = new ArtifactPipeline(
            logs.Add,
            (file, arguments, token) => Task.FromResult(new ProcessResult()),
            handler))
        {
            PackageAcquisitionResult result = pipeline.AcquirePackageBytesAsync(
                metadata,
                cacheRoot,
                packagePath,
                downloadPath,
                new DirectProgress<OperationProgress>(delegate { }),
                new OperationPauseToken(null),
                CancellationToken.None,
                0,
                1.0d).GetAwaiter().GetResult();
            Assert(result.Mode == PackageAcquisitionMode.Incremental &&
                result.ReusedBytes == newerPlan.ReusedBytes &&
                handler.FullRequests == 0 &&
                File.ReadAllBytes(downloadPath).SequenceEqual(targetBytes),
                "最佳候选 payload 损坏后没有切换到次优候选，或错误发起了完整下载。");
            Assert(logs.Exists(value => value.IndexOf("尝试旧缓存 1.0.0.0", StringComparison.Ordinal) >= 0) &&
                logs.Exists(value => value.IndexOf("增量候选 1.0.0.0 物化失败", StringComparison.Ordinal) >= 0) &&
                logs.Exists(value => value.IndexOf("最终采用旧缓存 2.0.0.0", StringComparison.Ordinal) >= 0) &&
                logs.Exists(value => value.IndexOf("增量候选 2.5.0.0 无法使用", StringComparison.Ordinal) >= 0),
                "多候选选择日志没有记录收益排序、payload 损坏降级或最终来源。");
        }
    }

    private static void TestArtifactPipelineUsesIncrementalAcquisition()
    {
        RunArtifactPipelineAcquisitionCase(false);
    }

    private static void TestArtifactPipelineFallsBackToFullDownload()
    {
        RunArtifactPipelineAcquisitionCase(true);
    }

    private static void TestFullDownloadHandleSurvivesCachePublish()
    {
        string caseRoot = NewCaseRoot("full-download-stable-handle");
        string cacheRoot = Path.Combine(caseRoot, "cache");
        Directory.CreateDirectory(cacheRoot);
        string packagePath = Path.Combine(cacheRoot, "OpenAI.Codex_2.0.0.0_x64.msix");
        string downloadPath = packagePath + ".download-" + Guid.NewGuid().ToString("N") + ".msix";
        byte[] packageBytes = CreatePseudoRandomBytes(2 * 1024 * 1024, 8601);
        string digest;
        using (SHA256 sha256 = SHA256.Create())
        {
            digest = Convert.ToBase64String(sha256.ComputeHash(packageBytes));
        }
        PackageMetadata metadata = CreatePackageMetadata(
            "2.0.0.0",
            "OpenAI.Codex_2.0.0.0_x64__2p2nqsd0c76g0",
            digest,
            packageBytes.LongLength);
        metadata.url = "https://tlu.dl.delivery.mp.microsoft.com/full.msix";

        using (ArtifactPipeline pipeline = new ArtifactPipeline(
            delegate { },
            (file, arguments, token) => Task.FromResult(new ProcessResult()),
            new AcquisitionPackageMessageHandler(packageBytes, false)))
        {
            PackageAcquisitionResult acquisition = pipeline.AcquirePackageBytesAsync(
                metadata,
                cacheRoot,
                packagePath,
                downloadPath,
                new DirectProgress<OperationProgress>(delegate { }),
                new OperationPauseToken(null),
                CancellationToken.None,
                IncrementalAcquisitionPolicy.MinimumSavingsBytes,
                IncrementalAcquisitionPolicy.MaximumRemoteFraction,
                true).GetAwaiter().GetResult();
            DownloadedPackageLease downloadedPackage = acquisition.DetachDownloadedPackage();
            Assert(downloadedPackage != null && acquisition.Mode == PackageAcquisitionMode.FullDownload,
                "完整下载没有返回持续持有文件身份的稳定句柄。");

            FileStream lockedStream = downloadedPackage.DetachStream();
            downloadedPackage.Dispose();
            try
            {
                File.Move(downloadPath, packagePath);
                Assert(File.Exists(packagePath) && !File.Exists(downloadPath),
                    "稳定句柄存续期间没有完成缓存原子重命名。");
                Exception concurrentWrite = CaptureFailure(delegate
                {
                    using (FileStream ignored = new FileStream(
                        packagePath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite)) { }
                });
                Assert(concurrentWrite is IOException,
                    "可信句柄存续期间仍允许其他进程写入缓存文件。");
                lockedStream.Position = 0;
                using (SHA256 sha256 = SHA256.Create())
                {
                    Assert(Convert.ToBase64String(sha256.ComputeHash(lockedStream)) == digest,
                        "缓存发布后稳定句柄读取到的文件摘要发生变化。");
                }
            }
            finally
            {
                lockedStream.Dispose();
            }
        }
    }

    private static void TestMsixDigestMismatchClassification()
    {
        string caseRoot = NewCaseRoot("msix-digest-mismatch-classification");
        string packagePath = Path.Combine(caseRoot, "mismatch.msix");
        File.WriteAllBytes(packagePath, Enumerable.Range(0, 256).Select(value => (byte)value).ToArray());
        PackageMetadata metadata = CreatePackageMetadata(
            "1.0.0.0",
            "OpenAI.Codex_1.0.0.0_x64__2p2nqsd0c76g0",
            Convert.ToBase64String(new byte[32]),
            new FileInfo(packagePath).Length);
        Exception failure = CaptureFailure(delegate
        {
            using (VerifiedArtifactLease lease = MsixPackageTrust.VerifyAndLock(
                packagePath,
                metadata,
                "x64",
                delegate { })) { }
        });
        Assert(failure is InvalidDataException && failure.InnerException is MsixPackageDigestMismatchException,
            "MSIX 摘要不匹配没有使用仅供缓存回退的明确异常类型。实际异常：" +
            (failure == null ? "无" : failure.ToString()));
    }

    private static void TestStagingBuilderStreamsBlockMapValidation()
    {
        string caseRoot = NewCaseRoot("staging-stream-validation");
        string packagePath = Path.Combine(caseRoot, "fixture.msix");
        string stagingRoot = Path.Combine(caseRoot, "staging");
        byte[] first = CreatePseudoRandomBytes(32 * 1024, 9201);
        byte[] second = Encoding.UTF8.GetBytes("second payload");
        CreateFixturePackage(
            packagePath,
            new[]
            {
                new FixturePackageEntry("app/%40scope/first.bin", "app\\@scope\\first.bin", first),
                new FixturePackageEntry("app/second.txt", "app\\second.txt", second)
            },
            new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
            0);
        using (StagingBuildResult result = StagingBuilder.ExtractAndValidate(
            packagePath,
            stagingRoot,
            CancellationToken.None,
            4))
        {
            Assert(File.ReadAllBytes(Path.Combine(stagingRoot, "app", "@scope", "first.bin")).SequenceEqual(first) &&
                File.ReadAllBytes(Path.Combine(stagingRoot, "app", "second.txt")).SequenceEqual(second),
                "staging 流式构建没有写出原始 payload。");
            Assert(result.ExtractedFileCount == 3 && result.VerifiedBlockCount == 2 &&
                result.FootprintFileCount == 1 && result.ValidatedDirectoryCount == 2 &&
                result.SkippedDirectoryProbeCount == 1 && result.WorkerCount == 3 &&
                File.Exists(Path.Combine(stagingRoot, "AppxBlockMap.xml")),
                "staging 流式构建统计或 footprint 提取不正确。");
        }
    }

    private static void TestProvenanceReusesLockedStagingDigests()
    {
        string caseRoot = NewCaseRoot("staging-provenance-digests");
        string packagePath = Path.Combine(caseRoot, "fixture.msix");
        string stagingRoot = Path.Combine(caseRoot, "staging");
        string version = "1.2.3.4";
        byte[] manifest = new UTF8Encoding(false).GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">" +
            "<Identity Name=\"OpenAI.Codex\" Publisher=\"CN=OpenAI\" Version=\"" + version +
            "\" ProcessorArchitecture=\"x64\" />" +
            "<Properties><DisplayName>Codex</DisplayName></Properties>" +
            "<Applications><Application Id=\"App\" Executable=\"app\\Codex.exe\" " +
            "EntryPoint=\"Windows.FullTrustApplication\" /></Applications></Package>");
        byte[] executable = Encoding.ASCII.GetBytes("MZ-official-executable");
        byte[] asar = CreatePseudoRandomBytes(48 * 1024, 9401);
        byte[] codex = CreatePseudoRandomBytes(32 * 1024, 9402);
        CreateFixturePackage(
            packagePath,
            new[]
            {
                new FixturePackageEntry("AppxManifest.xml", "AppxManifest.xml", manifest),
                new FixturePackageEntry("app/Codex.exe", "app\\Codex.exe", executable),
                new FixturePackageEntry("app/resources/app.asar", "app\\resources\\app.asar", asar),
                new FixturePackageEntry("app/resources/codex.exe", "app\\resources\\codex.exe", codex)
            },
            new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
            0);

        using (StagingBuildResult result = StagingBuilder.ExtractAndValidate(
            packagePath,
            stagingRoot,
            CancellationToken.None))
        {
            PackageProfile profile = result.Profile;
            Assert(profile != null, "staging 没有从受信任包内流返回 PackageProfile。");
            string asarPath = Path.Combine(stagingRoot, "app", "resources", "app.asar");
            Exception writeFailure = CaptureFailure(delegate
            {
                File.AppendAllText(asarPath, "blocked", Encoding.ASCII);
            });
            Assert(writeFailure is IOException || writeFailure is UnauthorizedAccessException,
                "受摘要租约保护的 staging 制品仍可被写入。");

            result.ReleaseOfficialArtifactDigest(profile.ExecutableRelativePath);
            string executablePath = PackageProfileReader.GetExecutablePath(stagingRoot, profile);
            File.AppendAllText(executablePath, "-visual-change", Encoding.ASCII);
            PackageMetadata package = CreatePackageMetadata(
                version,
                "OpenAI.Codex_1.2.3.4_x64__2p2nqsd0c76g0",
                Convert.ToBase64String(new byte[32]),
                new FileInfo(packagePath).Length);
            ArtifactProvenance provenance = ArtifactProvenance.Capture(
                stagingRoot,
                profile,
                package,
                null,
                result);

            Assert(result.OfficialArtifactDigestCount == 4 &&
                result.ReusedArtifactDigestCount == 3 &&
                result.ReusedArtifactDigestBytes == manifest.LongLength + asar.LongLength + codex.LongLength,
                "provenance 没有只复用仍受租约保护的三个官方摘要。");
            Assert(ArtifactHash.FixedTimeEquals(
                    FindArtifact(provenance, "app/resources/app.asar").Sha256,
                    ComputeSha256Hex(asar)) &&
                ArtifactHash.FixedTimeEquals(
                    FindArtifact(provenance, "app/Codex.exe").Sha256,
                    ArtifactHash.ComputeSha256(executablePath)),
                "复用的 app.asar 摘要或视觉变换后的主程序摘要不正确。");
        }
    }

    private static void TestStagingBuilderRejectsTamperedPayload()
    {
        string caseRoot = NewCaseRoot("staging-stream-tamper");
        string packagePath = Path.Combine(caseRoot, "fixture.msix");
        string stagingRoot = Path.Combine(caseRoot, "staging");
        CreateFixturePackage(
            packagePath,
            new[]
            {
                new FixturePackageEntry(
                    "app/payload.bin",
                    "app\\payload.bin",
                    CreatePseudoRandomBytes(48 * 1024, 9301))
            },
            new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
            0);
        MsixZipLayout layout = MsixZipLayout.Read(packagePath);
        MsixZipEntry payload;
        Assert(layout.TryGetEntry("app/payload.bin", out payload), "篡改测试缺少 payload 条目。");
        using (FileStream stream = new FileStream(packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = payload.DataOffset + payload.CompressedSize / 2;
            int value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(value ^ 0x80));
            stream.Flush(true);
        }
        Exception failure = CaptureFailure(delegate
        {
            StagingBuilder.ExtractAndValidate(packagePath, stagingRoot, CancellationToken.None, 4);
        });
        Assert(failure is InvalidDataException || failure is IOException,
            "payload 篡改没有在 staging 写入期间被拒绝。实际异常：" +
            (failure == null ? "无" : failure.ToString()));
    }

    private static void TestStagingBuilderHonorsPreCancellation()
    {
        string caseRoot = NewCaseRoot("staging-parallel-cancellation");
        string packagePath = Path.Combine(caseRoot, "fixture.msix");
        string stagingRoot = Path.Combine(caseRoot, "staging");
        CreateFixturePackage(
            packagePath,
            new[]
            {
                new FixturePackageEntry("app/first.bin", "app\\first.bin", CreatePseudoRandomBytes(32 * 1024, 9351)),
                new FixturePackageEntry("app/second.bin", "app\\second.bin", CreatePseudoRandomBytes(32 * 1024, 9352))
            },
            new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
            0);
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            Exception failure = CaptureFailure(delegate
            {
                StagingBuilder.ExtractAndValidate(packagePath, stagingRoot, cancellation.Token, 4);
            });
            Assert(failure is OperationCanceledException &&
                Directory.Exists(stagingRoot) &&
                !Directory.EnumerateFileSystemEntries(stagingRoot).Any(),
                "staging 并行构建预取消后没有保持空目录，或异常类型不正确。实际异常：" +
                (failure == null ? "无" : failure.ToString()));
        }
    }

    private static void TestStagingBuilderRejectsNonemptyRoot()
    {
        string caseRoot = NewCaseRoot("staging-stream-nonempty");
        string packagePath = Path.Combine(caseRoot, "fixture.msix");
        string stagingRoot = Path.Combine(caseRoot, "staging");
        CreateFixturePackage(
            packagePath,
            new[]
            {
                new FixturePackageEntry("app/file.txt", "app\\file.txt", Encoding.UTF8.GetBytes("payload"))
            },
            new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
            0);
        Directory.CreateDirectory(stagingRoot);
        File.WriteAllText(Path.Combine(stagingRoot, "sentinel.txt"), "keep", Encoding.UTF8);
        Exception failure = CaptureFailure(delegate
        {
            StagingBuilder.ExtractAndValidate(packagePath, stagingRoot, CancellationToken.None);
        });
        Assert(failure is InvalidDataException && File.Exists(Path.Combine(stagingRoot, "sentinel.txt")),
            "非空 staging 没有被拒绝，或既有文件被改写。");
    }

    private static void RunArtifactPipelineAcquisitionCase(bool ignoreRanges)
    {
        string caseRoot = NewCaseRoot(ignoreRanges
            ? "incremental-acquisition-fallback"
            : "incremental-acquisition-success");
        string cacheRoot = Path.Combine(caseRoot, "cache");
        Directory.CreateDirectory(cacheRoot);
        string previousPath = Path.Combine(cacheRoot, "OpenAI.Codex_1.0.0.0_x64.msix");
        string targetFixture = Path.Combine(caseRoot, "target-fixture.msix");
        string packagePath = Path.Combine(cacheRoot, "OpenAI.Codex_2.0.0.0_x64.msix");
        string downloadPath = packagePath + ".download-" + Guid.NewGuid().ToString("N") + ".msix";
        FixturePackageEntry stable = new FixturePackageEntry(
            "app/stable.bin",
            "app\\stable.bin",
            CreatePseudoRandomBytes(48 * 1024, 8100),
            CompressionLevel.NoCompression);
        CreateFixturePackage(
            previousPath,
            new[]
            {
                stable,
                new FixturePackageEntry(
                    "app/changed.bin",
                    "app\\changed.bin",
                    CreatePseudoRandomBytes(4096, 8101),
                    CompressionLevel.NoCompression)
            },
            new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
            0);
        CreateFixturePackage(
            targetFixture,
            new[]
            {
                stable,
                new FixturePackageEntry(
                    "app/changed.bin",
                    "app\\changed.bin",
                    CreatePseudoRandomBytes(4096, 8102),
                    CompressionLevel.NoCompression)
            },
            new DateTimeOffset(2026, 7, 2, 8, 0, 0, TimeSpan.Zero),
            0);
        byte[] targetBytes = File.ReadAllBytes(targetFixture);
        string digest;
        using (SHA256 sha256 = SHA256.Create())
        {
            digest = Convert.ToBase64String(sha256.ComputeHash(targetBytes));
        }
        PackageMetadata metadata = CreatePackageMetadata(
            "2.0.0.0",
            "OpenAI.Codex_2.0.0.0_x64__2p2nqsd0c76g0",
            digest,
            targetBytes.LongLength);
        metadata.url = "https://tlu.dl.delivery.mp.microsoft.com/target.msix";
        AcquisitionPackageMessageHandler handler = new AcquisitionPackageMessageHandler(targetBytes, ignoreRanges);
        List<string> logs = new List<string>();
        using (ArtifactPipeline pipeline = new ArtifactPipeline(
            logs.Add,
            (file, arguments, token) => Task.FromResult(new ProcessResult()),
            handler))
        {
            PackageAcquisitionResult result = pipeline.AcquirePackageBytesAsync(
                metadata,
                cacheRoot,
                packagePath,
                downloadPath,
                new DirectProgress<OperationProgress>(delegate { }),
                new OperationPauseToken(null),
                CancellationToken.None,
                0,
                1.0d).GetAwaiter().GetResult();
            string diagnostics = string.Format(
                CultureInfo.InvariantCulture,
                "模式={0}，Range={1}，完整 GET={2}，复用字节={3}，回退原因={4}，日志={5}",
                result.Mode,
                handler.RangeRequests,
                handler.FullRequests,
                result.ReusedBytes,
                result.FallbackReason ?? "无",
                string.Join(" | ", logs.ToArray()));
            Assert(File.ReadAllBytes(downloadPath).SequenceEqual(targetBytes),
                "缓存获取流程没有产生完整目标 MSIX。");
            if (ignoreRanges)
            {
                Assert(result.Mode == PackageAcquisitionMode.FullDownload &&
                    handler.RangeRequests > 0 && handler.FullRequests == 1 &&
                    !string.IsNullOrWhiteSpace(result.FallbackReason),
                    "Range 失败后没有自动回退一次完整下载。" + diagnostics);
            }
            else
            {
                Assert(result.Mode == PackageAcquisitionMode.Incremental &&
                    handler.RangeRequests > 0 && handler.FullRequests == 0 &&
                    result.ReusedBytes > 0,
                    "收益达标时没有采用增量物化，或仍发起了完整 GET。" + diagnostics);
            }
            Assert(!Directory.EnumerateFiles(cacheRoot, "*.materialize-*.msix").Any(),
                "缓存获取完成后残留增量物化临时文件。");
        }
    }

    private static void CreateFixturePackage(
        string packagePath,
        IList<FixturePackageEntry> entries,
        DateTimeOffset timestamp,
        long blockMapSizeAdjustment)
    {
        CreateFixturePackageCore(packagePath, entries, timestamp, blockMapSizeAdjustment, false);
    }

    private static byte[] CreatePseudoRandomBytes(int length, int seed)
    {
        byte[] bytes = new byte[length];
        uint state = unchecked((uint)seed) | 1u;
        for (int index = 0; index < bytes.Length; index++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            bytes[index] = (byte)state;
        }
        return bytes;
    }

    private static void CreateFixturePackageCore(
        string packagePath,
        IList<FixturePackageEntry> entries,
        DateTimeOffset timestamp,
        long blockMapSizeAdjustment,
        bool useDataDescriptors)
    {
        string measurementPath = packagePath + ".measure";
        try
        {
            WriteFixtureArchive(measurementPath, entries, timestamp, null, useDataDescriptors);
            Dictionary<string, long> compressedSizes;
            using (ZipArchive archive = ZipFile.OpenRead(measurementPath))
            {
                compressedSizes = archive.Entries.ToDictionary(
                    value => value.FullName,
                    value => value.CompressedLength,
                    StringComparer.Ordinal);
            }

            XNamespace ns = "http://schemas.microsoft.com/appx/2010/blockmap";
            XElement root = new XElement(
                ns + "BlockMap",
                new XAttribute("HashMethod", "http://www.w3.org/2001/04/xmlenc#sha256"));
            foreach (FixturePackageEntry entry in entries)
            {
                byte[] hash;
                using (SHA256 sha256 = SHA256.Create())
                {
                    hash = sha256.ComputeHash(entry.Contents);
                }
                XElement file = new XElement(
                    ns + "File",
                    new XAttribute("Name", entry.BlockMapName),
                    new XAttribute("Size", checked(entry.Contents.LongLength + blockMapSizeAdjustment).ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("LfhSize", (30 + Encoding.UTF8.GetByteCount(entry.ZipName)).ToString(CultureInfo.InvariantCulture)));
                if (entry.Contents.Length > 0)
                {
                    file.Add(new XElement(
                        ns + "Block",
                        new XAttribute("Hash", Convert.ToBase64String(hash)),
                        new XAttribute("Size", compressedSizes[entry.ZipName].ToString(CultureInfo.InvariantCulture))));
                }
                root.Add(file);
            }
            XDocument blockMap = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
            string blockMapXml = blockMap.Declaration + blockMap.ToString(SaveOptions.DisableFormatting);
            WriteFixtureArchive(
                packagePath,
                entries,
                timestamp,
                new UTF8Encoding(false).GetBytes(blockMapXml),
                useDataDescriptors);
        }
        finally
        {
            if (File.Exists(measurementPath)) File.Delete(measurementPath);
        }
    }

    private static void WriteFixtureArchive(
        string path,
        IList<FixturePackageEntry> entries,
        DateTimeOffset timestamp,
        byte[] blockMapBytes,
        bool useDataDescriptors = false)
    {
        using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (Stream destination = useDataDescriptors ? (Stream)new NonSeekableWriteStream(stream) : stream)
        using (ZipArchive archive = new ZipArchive(destination, ZipArchiveMode.Create, false))
        {
            foreach (FixturePackageEntry entry in entries)
            {
                WriteFixtureEntry(
                    archive,
                    entry.ZipName,
                    entry.Contents,
                    timestamp,
                    entry.CompressionLevel);
            }
            if (blockMapBytes != null)
            {
                WriteFixtureEntry(archive, "AppxBlockMap.xml", blockMapBytes, timestamp);
            }
        }
    }

    private static void WriteFixtureEntry(
        ZipArchive archive,
        string name,
        byte[] contents,
        DateTimeOffset timestamp,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, compressionLevel);
        entry.LastWriteTime = timestamp;
        using (Stream output = entry.Open())
        {
            output.Write(contents, 0, contents.Length);
        }
    }

    private static void ConvertFixtureToZip64(string sourcePath, string destinationPath)
    {
        byte[] source = File.ReadAllBytes(sourcePath);
        int eocd = source.Length - 22;
        Assert(ReadFixtureUInt32(source, eocd) == 0x06054b50, "测试 ZIP 不以标准 EOCD 结束。");
        ushort entryCount = ReadFixtureUInt16(source, eocd + 10);
        uint centralSize = ReadFixtureUInt32(source, eocd + 12);
        uint centralOffset = ReadFixtureUInt32(source, eocd + 16);
        Assert((long)centralOffset + centralSize == eocd, "测试 ZIP 中央目录与 EOCD 不连续。");

        using (MemoryStream output = new MemoryStream(source.Length + 76))
        {
            output.Write(source, 0, eocd);
            long zip64Offset = output.Position;
            WriteFixtureUInt32(output, 0x06064b50);
            WriteFixtureUInt64(output, 44);
            WriteFixtureUInt16(output, 45);
            WriteFixtureUInt16(output, 45);
            WriteFixtureUInt32(output, 0);
            WriteFixtureUInt32(output, 0);
            WriteFixtureUInt64(output, entryCount);
            WriteFixtureUInt64(output, entryCount);
            WriteFixtureUInt64(output, centralSize);
            WriteFixtureUInt64(output, centralOffset);
            WriteFixtureUInt32(output, 0x07064b50);
            WriteFixtureUInt32(output, 0);
            WriteFixtureUInt64(output, checked((ulong)zip64Offset));
            WriteFixtureUInt32(output, 1);

            byte[] patchedEocd = source.Skip(eocd).Take(22).ToArray();
            WriteFixtureUInt16(patchedEocd, 8, ushort.MaxValue);
            WriteFixtureUInt16(patchedEocd, 10, ushort.MaxValue);
            WriteFixtureUInt32(patchedEocd, 12, uint.MaxValue);
            WriteFixtureUInt32(patchedEocd, 16, uint.MaxValue);
            output.Write(patchedEocd, 0, patchedEocd.Length);
            File.WriteAllBytes(destinationPath, output.ToArray());
        }
    }

    private static ushort ReadFixtureUInt16(byte[] bytes, int offset)
    {
        return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
    }

    private static uint ReadFixtureUInt32(byte[] bytes, int offset)
    {
        return (uint)(bytes[offset] | (bytes[offset + 1] << 8) |
            (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
    }

    private static void WriteFixtureUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
    }

    private static void WriteFixtureUInt32(Stream stream, uint value)
    {
        WriteFixtureUInt16(stream, (ushort)value);
        WriteFixtureUInt16(stream, (ushort)(value >> 16));
    }

    private static void WriteFixtureUInt64(Stream stream, ulong value)
    {
        WriteFixtureUInt32(stream, (uint)value);
        WriteFixtureUInt32(stream, (uint)(value >> 32));
    }

    private static void WriteFixtureUInt16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteFixtureUInt32(byte[] bytes, int offset, uint value)
    {
        WriteFixtureUInt16(bytes, offset, (ushort)value);
        WriteFixtureUInt16(bytes, offset + 2, (ushort)(value >> 16));
    }

    private sealed class FixturePackageEntry
    {
        internal FixturePackageEntry(string zipName, string blockMapName, byte[] contents)
            : this(zipName, blockMapName, contents, CompressionLevel.Optimal)
        {
        }

        internal FixturePackageEntry(
            string zipName,
            string blockMapName,
            byte[] contents,
            CompressionLevel compressionLevel)
        {
            ZipName = zipName;
            BlockMapName = blockMapName;
            Contents = contents;
            CompressionLevel = compressionLevel;
        }

        internal string ZipName { get; private set; }
        internal string BlockMapName { get; private set; }
        internal byte[] Contents { get; private set; }
        internal CompressionLevel CompressionLevel { get; private set; }
    }

    private sealed class NonSeekableWriteStream : Stream
    {
        private readonly Stream inner;
        private long position;

        internal NonSeekableWriteStream(Stream inner)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { return position; } }
        public override long Position { get { return position; } set { throw new NotSupportedException(); } }

        public override void Flush() { inner.Flush(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            position = checked(position + count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class RangePackageMessageHandler : HttpMessageHandler
    {
        private readonly byte[] package;

        internal RangePackageMessageHandler(byte[] packageBytes)
        {
            package = packageBytes ?? throw new ArgumentNullException(nameof(packageBytes));
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RangeHeaderValue requestedRange = request.Headers.Range;
            if (requestedRange == null || requestedRange.Ranges.Count != 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { RequestMessage = request });
            }
            RangeItemHeaderValue range = requestedRange.Ranges.Single();
            if (!range.From.HasValue || !range.To.HasValue || range.From.Value < 0 ||
                range.To.Value < range.From.Value || range.To.Value >= package.LongLength)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    RequestMessage = request
                });
            }
            int start = checked((int)range.From.Value);
            int length = checked((int)(range.To.Value - range.From.Value + 1));
            StreamContent content = new StreamContent(new MemoryStream(package, start, length, false));
            content.Headers.ContentLength = length;
            content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + length - 1, package.Length);
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                RequestMessage = request,
                Content = content
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture-package\"");
            return Task.FromResult(response);
        }
    }

    private sealed class AcquisitionPackageMessageHandler : HttpMessageHandler
    {
        private readonly byte[] package;
        private readonly bool ignoreRanges;

        internal AcquisitionPackageMessageHandler(byte[] packageBytes, bool shouldIgnoreRanges)
        {
            package = packageBytes ?? throw new ArgumentNullException(nameof(packageBytes));
            ignoreRanges = shouldIgnoreRanges;
        }

        internal int RangeRequests { get; private set; }
        internal int FullRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RangeHeaderValue requestedRange = request.Headers.Range;
            if (requestedRange == null)
            {
                FullRequests++;
                return Task.FromResult(CreateResponse(request, HttpStatusCode.OK, 0, package.Length));
            }
            RangeRequests++;
            if (ignoreRanges)
            {
                return Task.FromResult(CreateResponse(request, HttpStatusCode.OK, 0, package.Length));
            }
            RangeItemHeaderValue range = requestedRange.Ranges.Single();
            int start = checked((int)range.From.Value);
            int length = checked((int)(range.To.Value - range.From.Value + 1));
            HttpResponseMessage response = CreateResponse(request, HttpStatusCode.PartialContent, start, length);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + length - 1, package.Length);
            response.Headers.ETag = new EntityTagHeaderValue("\"acquisition-fixture\"");
            return Task.FromResult(response);
        }

        private HttpResponseMessage CreateResponse(
            HttpRequestMessage request,
            HttpStatusCode status,
            int offset,
            int length)
        {
            StreamContent content = new StreamContent(new MemoryStream(package, offset, length, false));
            content.Headers.ContentLength = length;
            return new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = content
            };
        }
    }

    private sealed class FilePackageMessageHandler : HttpMessageHandler
    {
        private readonly string packagePath;
        private readonly long packageLength;

        internal FilePackageMessageHandler(string path)
        {
            packagePath = Path.GetFullPath(path);
            packageLength = new FileInfo(packagePath).Length;
        }

        internal int FullRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RangeHeaderValue requestedRange = request.Headers.Range;
            long start = 0;
            long length = packageLength;
            HttpStatusCode status = HttpStatusCode.OK;
            if (requestedRange == null)
            {
                FullRequests++;
            }
            else
            {
                RangeItemHeaderValue range = requestedRange.Ranges.Single();
                start = range.From.Value;
                length = checked(range.To.Value - range.From.Value + 1);
                status = HttpStatusCode.PartialContent;
            }

            FileStream file = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                true);
            file.Position = start;
            StreamContent content = new StreamContent(new BoundedFileReadStream(file, length));
            content.Headers.ContentLength = length;
            if (status == HttpStatusCode.PartialContent)
            {
                content.Headers.ContentRange = new ContentRangeHeaderValue(
                    start,
                    checked(start + length - 1),
                    packageLength);
            }
            HttpResponseMessage response = new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = content
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"real-package-fixture\"");
            return Task.FromResult(response);
        }
    }

    private sealed class BoundedFileReadStream : Stream
    {
        private readonly FileStream inner;
        private readonly long length;
        private long position;

        internal BoundedFileReadStream(FileStream stream, long boundedLength)
        {
            inner = stream ?? throw new ArgumentNullException(nameof(stream));
            length = boundedLength;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return length; } }
        public override long Position { get { return position; } set { throw new NotSupportedException(); } }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= length) return 0;
            int requested = checked((int)Math.Min(count, length - position));
            int read = inner.Read(buffer, offset, requested);
            position += read;
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            if (position >= length) return 0;
            int requested = checked((int)Math.Min(count, length - position));
            int read = await inner.ReadAsync(buffer, offset, requested, cancellationToken).ConfigureAwait(false);
            position += read;
            return read;
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
}
}
