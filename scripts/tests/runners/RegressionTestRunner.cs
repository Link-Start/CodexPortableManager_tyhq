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
    private static readonly BindingFlags AnyInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly List<string> Results = new List<string>();
    private static readonly List<string> RegisteredTestMethods = new List<string>();
    private static string managerPath;
    private static string suiteRoot;
    private static int passed;
    private static int failed;
    private static int skipped;
    private static int selected;
    private static string testFilter;

    internal static int Run(string[] args)
    {
        try
        {
            if (args.Length > 0 && string.Equals(args[0], "--hold-lock", StringComparison.Ordinal))
            {
                return HoldLock(args);
            }
            if (args.Length > 0 && string.Equals(args[0], "--save-config-part", StringComparison.Ordinal))
            {
                return SaveConfigPart(args);
            }
            if (args.Length > 0 && string.Equals(
                args[0],
                "--start-post-deployment-cleanup-and-exit",
                StringComparison.Ordinal))
            {
                return StartPostDeploymentCleanupAndExit(args);
            }

            if (args.Length != 3)
            {
                Console.Error.WriteLine("用法：CodexPortableManager.Tests.exe --regression-test <manager.exe> <suite-root> <report-path>");
                return 64;
            }

            managerPath = Path.GetFullPath(args[0]);
            suiteRoot = Path.GetFullPath(args[1]);
            string reportPath = Path.GetFullPath(args[2]);
            ValidateTestRoot(suiteRoot);
            Directory.CreateDirectory(suiteRoot);
            LoadManager(managerPath);
            testFilter = Environment.GetEnvironmentVariable("CPM_REGRESSION_FILTER");
            Results.Clear();
            RegisteredTestMethods.Clear();
            passed = 0;
            failed = 0;
            skipped = 0;
            selected = 0;

            RunCase("普通非 Codex 非空目录卸载被拒绝且文件保留", TestRejectsUnownedDirectory);
            RunCase("安装目录与管理器存储树重叠时被拒绝", TestInstallRootRejectsManagerStorageOverlap);
            RunCase("事务安装拒绝 UNC 和映射网络盘", TestInstallRootRejectsRemotePaths);
            RunCase("最终安装根 junction 被统一拒绝且卸载不误报", TestManagedRootRejectsTopLevelJunction);
            RunCase("安装根存在 junction 祖先时被拒绝", TestInstallRootRejectsJunctionAncestor);
            RunCase("非空安装位置自动选择 Codex 独立子目录", TestInstallDestinationResolution);
            RunCase("底层删除顶层 junction 时仅删除链接且保留目标哨兵", TestTopLevelJunctionDeletion);
            RunCase("空目录删除在目录变为非空时拒绝递归清理", TestEmptyDirectoryDeletionNeverRecurses);
            RunCase("普通文件删除拒绝 junction 祖先", TestFileDeletionRejectsJunctionAncestor);
            RunCase("普通文件删除拒绝 File ID 不匹配的替换文件", TestFileDeletionRejectsIdentityReplacement);
            RunCase("密集小文件目录批量删除保持线性性能", TestDenseDirectoryDeletion);
            RunCase("回滚优先使用较低 previous 并在需要时选择缓存低版本", TestRollbackTargetSelection);
            RunCase("回滚候选缺少 app.asar 时两个版本保持原位", TestRollbackPreflightFailureKeepsBothVersions);

            RunCase("更新事务恢复：current + transaction-old", TestUpdateRecoveryCurrentAndTransaction);
            RunCase("更新事务恢复：previous + transaction-old", TestUpdateRecoveryPreviousAndTransaction);
            RunCase("更新事务恢复：current + previous + transaction-old", TestUpdateRecoveryCommittedTopology);
            RunCase("更新事务恢复：仅 transaction-old", TestUpdateRecoverySoleTransaction);
            RunCase("更新拓扑预检把 previous-only 规范化为 current", TestPreviousOnlyNormalizationBeforeUpdate);
            RunCase("更新 journal 提交前恢复原 current/previous", TestUpdateJournalRecoveryBeforeCommit);
            RunCase("更新 journal 激活后完成提交并清理旧备份", TestUpdateJournalRecoveryAfterActivation);
            RunCase("首次安装在 payload 激活前的空 journal 可安全清理", TestFirstInstallPreparedJournalWithoutPayloadClears);
            RunCase("更新提交点后故障只向前完成且不移动新版本", TestUpdateFailureAfterCommitCompletesForward);
            RunCase("更新清理槽部分删除后仍按 receipt 继续清理", TestUpdateCleanupReceiptSurvivesPartialDeletion);
            RunCase("更新清理 receipt 拒绝路径上的替换目录", TestUpdateCleanupReceiptRejectsReplacement);
            RunCase("最终删除句柄拒绝 File ID 不匹配的替换目录", TestNativeDirectoryDeletionRejectsIdentityReplacement);
            RunCase("最终删除句柄拒绝带 receipt 的替换 junction", TestNativeDirectoryDeletionRejectsReceiptJunctionReplacement);
            RunCase("目录清理身份使用持久 File ID", TestManagedDirectoryIdentityUsesPersistentFileId);
            RunCase("部署清理 receipt 只在目录移动后绑定最终身份", TestDeploymentCleanupReceiptArmsAfterMove);
            RunCase("Prepared receipt 拒绝移动并授权被替换的来源目录", TestPreparedCleanupReceiptRejectsReplacedSource);
            RunCase("Prepared receipt 的更新和卸载首次移动窗口均回滚", TestPreparedCleanupMoveWindowsRollBack);
            RunCase("Prepared 恢复拒绝同 InstallId 替换目录", TestPreparedRecoveryRejectsSameInstallIdReplacement);
            RunCase("部署 journal 拒绝 receipt 阶段错配", TestDeploymentJournalRejectsCleanupReceiptPhaseMismatch);
            RunCase("部署 journal 拒绝缺失或被强制转换的关键字段", TestDeploymentJournalRejectsMissingOrCoercedFields);
            RunCase("部署 journal 拒绝区间内未定义阶段", TestDeploymentJournalRejectsUndefinedPhase);
            RunCase("已提交更新清理待办不隐藏有效当前版本", TestCommittedUpdateCleanupPendingKeepsCurrentStatus);

            RunCase("回滚事务恢复：previous + rollback-transaction", TestRollbackRecoveryPreviousAndTransaction);
            RunCase("回滚事务恢复：current + rollback-transaction", TestRollbackRecoveryCurrentAndTransaction);
            RunCase("回滚事务恢复：仅 rollback-transaction", TestRollbackRecoverySoleTransaction);
            RunCase("回滚事务异常三目录拓扑拒绝且不改动", TestRollbackRecoveryRejectsAmbiguousTopology);
            RunCase("回滚失败反向恢复：restore-current-moved", TestRollbackReversalCurrentMoved);
            RunCase("回滚失败反向恢复：restore-previous-moved-step1", TestRollbackReversalPreviousMoved);
            RunCase("回滚失败反向恢复：restore-swapped-step1", TestRollbackReversalCompletedSwap);
            RunCase("回滚 journal 从 current 已分离阶段继续完成", TestRollbackJournalRecoveryAfterCurrentDetached);
            RunCase("回滚 journal 的反向恢复可重入完成", TestRollbackJournalRestorationFromSwapped);
            RunCase("无标记目录未经显式批准不得接管和卸载", TestUnmanagedAdoptionRequiresExplicitApproval);
            RunCase("卸载事务提交前中断会恢复 current 和 previous", TestUninstallRecoveryBeforeCommit);
            RunCase("卸载目录移动后提交阶段未落盘会安全回滚", TestUninstallRecoveryAfterMoveBeforeCommitWriteRollsBack);
            RunCase("卸载 tombstone 部分删除后仍按 receipt 继续清理", TestUninstallCleanupReceiptSurvivesPartialDeletion);
            RunCase("无 receipt 的清理槽不会删除探测后出现的新目录", TestMissingCleanupReceiptNeverDeletesAppearingDirectory);
            RunCase("previous-only 历史状态可以直接事务化卸载", TestPreviousOnlyUninstall);
            RunCase("逻辑卸载立即分离活动目录并保留可恢复清理事务", TestDeferredUninstallDetachesBeforeCleanup);
            RunCase("独立卸载清理进程完成 tombstone 和 journal 回收", TestUninstallCleanupWorkerProcess);
            RunCase("独立部署后清理进程完成旧备份和 journal 回收", TestPostDeploymentCleanupWorkerProcess);

            RunCase("两个进程对同一安装根的操作锁互斥", TestCrossProcessOperationLock);
            RunCase("操作锁对 junction 别名和未创建目录保持互斥", TestOperationLockPathAliases);
            RunCase("操作锁不改写预先存在文件或硬链接目标", TestOperationLockPreservesExistingTargets);
            RunCase("用户级 Shell 集成资源锁保持互斥", TestShellIntegrationResourceLock);
            RunCase("本地状态探测与写操作共享安装路径锁", TestLocalStatusReadUsesOperationLock);
            RunCase("包解析器与制品管线职责保持单向分离", TestPackageResolverArtifactPipelineSeparation);
            RunCase("缓存发布锁释放后已验证制品租约仍保持文件不可变", TestVerifiedArtifactLeaseOutlivesCacheLock);
            RunCase("文件级增量从旧包和目标补集精确重建目标 MSIX", TestIncrementalPackageRebuildsExactTarget);
            RunCase("增量复用源被篡改时目标摘要拒绝并清理结果", TestIncrementalPackageRejectsTamperedReuseSource);
            RunCase("MSIX 布局拒绝百分号解码后的歧义路径", TestMsixLayoutRejectsAmbiguousPaths);
            RunCase("MSIX 布局拒绝与 ZIP 不一致的 BlockMap", TestMsixLayoutRejectsBlockMapMismatch);
            RunCase("MSIX 布局拒绝被截断的结束记录", TestMsixLayoutRejectsTruncatedPackage);
            RunCase("MSIX 布局支持 ZIP64 结束记录", TestMsixLayoutReadsZip64EndRecords);
            RunCase("MSIX 增量重建支持标准数据描述符", TestIncrementalPackageReadsStandardDataDescriptors);
            RunCase("远程 MSIX bootstrap 与目标补集精确重建目标包", TestRemoteIncrementalPackageRebuild);
            RunCase("增量候选选择与收益阈值保持保守", TestIncrementalCandidateSelectionAndThreshold);
            RunCase("多个旧缓存按收益选择且跳过损坏候选", TestArtifactPipelineSelectsBestIncrementalCandidate);
            RunCase("缓存未命中时采用远程增量物化", TestArtifactPipelineUsesIncrementalAcquisition);
            RunCase("增量 Range 失败时自动回退完整下载", TestArtifactPipelineFallsBackToFullDownload);
            RunCase("完整下载稳定句柄跨缓存发布并阻止并发写入", TestFullDownloadHandleSurvivesCachePublish);
            RunCase("真实双包文件级增量重建通过摘要与签名校验", TestRealIncrementalPackageRebuild);
            RunCase("OperationController 统一忙碌、暂停、取消与不可取消阶段", TestOperationControllerStateMachine);
            RunCase("官方 MSIX 保存取消时保留原目标并清理临时文件", TestVerifiedPackageCopyCancellation);
            RunCase("UiState 独占完整命令与兼容控件矩阵", TestUiStateOwnsCompleteControlMatrix);
            RunCase("下载完成后的处理阶段不会让可见进度倒退", TestVisibleProgressDoesNotRegressAfterDownload);
            RunCase("操作失败信息展开事务错误的真实原因", TestOperationFailureExpandsAggregateReasons);
            RunCase("资源管理器目录启动统一引用含空格路径", TestExplorerLaunchQuotesDirectoryPath);
            RunCase("打开目录不会重建清理待办的活动安装根", TestOpenInstallFolderDoesNotRecreatePendingRoot);
            RunCase("管理器跨位数发现并停止安装目录进程", TestProcessesUnderPathStops64BitProcess);
            RunCase("用户状态按 SID 分区且共享锁不依赖 LocalAppData", TestPortableStorageScopePartitioning);
            RunCase("配置读改写事务跨进程保留独立字段更新", TestPortableStorageConfigTransaction);
            RunCase("卸载只清除与目标路径匹配的成功目录记录", TestConditionalRecordedInstallRootClear);
            RunCase("模型 catalog 补丁可逆并保持 ASAR 完整性", TestModelCatalogPatchRoundTrip);
            RunCase("模型 catalog 对未知官方指纹安全降级", TestModelCatalogUnknownFingerprintFallback);
            RunCase("模型 catalog 配方拒绝无关路径和缺失上下文", TestModelCatalogRecipeConstraints);
            RunCase("模型原始推理显示补丁跨 chunk 可逆", TestReasoningDisplayPatchRoundTrip);
            RunCase("模型推理显示补丁拒绝重复、混合和未知结构", TestReasoningDisplayRecipeConstraints);
            RunCase("模型补丁由统一计划写入并可逆", TestCombinedAsarCompatibilityPlan);
            RunCase("目标结构漂移时统一计划保留可支持功能", TestCompatibilityPlanKeepsSupportedFeatureWhenPeerDrifts);
            RunCase("兼容结果保留配方、状态与回滚后的实际值", TestCompatibilityFeatureResultsRemainDetailed);
            RunCase("动态推理键族混合状态被拒绝且不误报成功", TestReasoningMixedStateRejected);
            RunCase("结构化中文菜单资源补丁可逆", TestLocaleMenuResourcePatchRoundTrip);
            RunCase("菜单不支持时推理英文仍独立提交", TestLocalizationComponentsCommitIndependently);
            RunCase("中文菜单仅应用匹配部分并提示跳过项", TestLocalizationMenuAppliesSupportedSubset);
            RunCase("中文菜单主脚本单组件漂移时保留可验证组件", TestNativeMenuScriptComponentDrift);
            RunCase("中文菜单按实际消费者增减并提示未知键", TestLocaleMenuTracksActualConsumerKeys);
            RunCase("语言设置全部关闭时未知 ASAR 无需解析即可跳过", TestLocalizationDisabledUnknownArchive);
            RunCase("ASAR 会话只保留按需读取的目标条目", TestAsarSessionRetainsOnlyTargetEntry);
            RunCase("兼容语义分析只在大幅临时分配后回收内存", TestCompatibilityAnalysisMemoryPolicy);
            RunCase("ASAR 会话锁定分析时的源文件身份", TestAsarSessionLocksAnalyzedSource);
            RunCase("ASAR 功能暂存事务异常时恢复进入前状态", TestAsarStagingTransactionRollsBackOnFailure);
            RunCase("ASAR 提交校验未修改条目的完整性", TestAsarCommitValidatesUnmodifiedEntries);
            RunCase("语言补丁拒绝无法完整重建的 ASAR payload", TestLocalizationRejectsIncompleteAsarPayload);
            RunCase("沙箱账户环境补丁可逆且不改写官方 helper", TestSandboxCompatibilityAsarRoundTrip);
            RunCase("沙箱入口按官方包元数据定位且不依赖菜单与哈希 bundle", TestSandboxCompatibilityUsesPackageMain);
            RunCase("沙箱入口元数据与补丁标记异常时严格拒绝", TestSandboxCompatibilityRejectsInvalidEntryMetadata);
            RunCase("沙箱兼容配置失败不阻断主操作", TestSandboxCompatibilityBestEffort);
            RunCase("部署兼容设置只进入 staging 变换层", TestCompatibilitySettingsAreStagingScoped);
            RunCase("更新 staging 兼容设置提交或整体回滚", TestTrustedStagingCompatibilityApplication);
            RunCase("更新 staging 兼容设置保留独立成功功能", TestTrustedStagingPartialCompatibilityApplication);
            RunCase("部署完成状态保留系统集成与维护警告", TestDeploymentCompletionPreservesWarnings);
            RunCase("版本检查完成状态区分未选择、无效和已安装目录", TestCheckCompletionMatchesLocalState);
            RunCase("顶部状态摘要由单一语义映射生成", TestStatusSummaryPresentation);
            RunCase("紧凑窗口优先保留主操作区高度", TestCompactLayoutPreservesPrimaryWorkspace);
            RunCase("主窗口使用共享设计令牌并保留辅助功能语义", TestMainWindowDesignTokensAndAccessibility);
            RunCase("回滚完成状态显示恢复版本和可再次切换说明", TestRollbackCompletionDisplaysRestoredVersion);
            RunCase("迁移完成状态不夸大便携版运行状态", TestMigrationCompletionDescribesActualOutcome);
            RunCase("子进程正常退出结果不被迟到取消覆盖", TestRunProcessExitWinsLateCancellation);
            RunCase("子进程取消等待目标进程实际退出", TestRunProcessCancellationWaitsForExit);
            RunCase("正式程序集保持 .NET Framework 4.6.2 且不强制 32 位", TestTargetFrameworkBaseline);
            RunCase("Store 包检测只接受官方 Codex 包身份", TestStorePackageDetectionFiltersIdentity);
            RunCase("Store 包卸载复用登记快照并按序关闭进程", TestStorePackageUninstallUsesSingleSnapshot);
            RunCase("WinRT PackageManager 可查询当前用户包登记", TestWindowsPackageManagerQuery);
            RunCase("便携版启动进程零代码立即退出不误报失败", TestPortableImmediateZeroExitIsAccepted);
            RunCase("便携版启动进程非零代码立即退出会中止迁移前置检查", TestPortableImmediateFailureIsRejected);
            RunCase("旧缓存逐文件迁移保留非缓存文件且冲突不覆盖", TestPortableStorageMigration);
            RunCase("未处理异常写入独立诊断日志", TestFatalExceptionLogging);
            RunCase("启动辅助维护失败保持 best-effort", TestStartupMaintenanceRemainsBestEffort);
            RunCase("存储维护按架构保留两个正式包并执行 invalid/log 策略", TestStorageMaintenancePolicy);
            RunCase("崩溃工作目录只清理过期且归属匹配的 marker", TestOwnedWorkDirectoryMaintenance);
            RunCase("崩溃工作目录清理拒绝 marker 对应目录被替换", TestOwnedWorkDirectoryRejectsIdentityReplacement);
            RunCase("IntegrationState 当前结构序列化往返", TestIntegrationStateSerialization);
            RunCase("Shell 资源名校验使用统一 canonical 规则", TestShellResourceNameRules);
            RunCase("便携版快捷方式冲突时保留原有入口", TestPortableShortcutConflictIsPreserved);
            RunCase("Shell 依赖项清理失败后仍可重入完成", TestShellCleanupRemainsRetryable);
            RunCase("卸载系统集成待清理时不回滚程序目录", TestUninstallReportsPendingShellCleanup);
            RunCase("Shell cleanup journal 跨 SUBST 与物理路径别名恢复", TestShellCleanupJournalSurvivesSubstAliases);
            RunCase("损坏集成状态的首次清理失败可由独立 journal 恢复", TestDamagedIntegrationStateFirstCleanupFailureRecovers);
            RunCase("语义损坏的 integration.json 按摘要清理", TestMalformedIntegrationStateIsRemovedByDigest);
            RunCase("integration.json 摘要删除保留原位替换的新状态", TestIntegrationStateDigestDeletePreservesReplacement);
            RunCase("不可读的 integration.json 阻止清理且可重试", TestLockedIntegrationStateBlocksCleanupUntilReadable);
            RunCase("伪造状态身份不会扩张 Shell 清理别名", TestMismatchedIntegrationIdentityDoesNotExpandCleanupAliases);
            RunCase("先前 Shell 清理不会删除同路径新 InstallId 资源", TestShellCleanupPreservesReusedInstallRoot);
            RunCase("Shell 清理保留 InstallId 未变但内容已被接管的注册项", TestShellCleanupPreservesRegistryContentWithStaleMarker);
            RunCase("即时清理 Armed 首写失败不遗留 Prepared", TestImmediateCleanupArmedWriteFailureLeavesNoPreparedJournal);
            RunCase("Prepared Shell journal 不会被即时清理自动提升", TestPreparedJournalCannotBePromotedByImmediateCleanup);
            RunCase("Armed journal 写入失败前不删除任何 Shell 资源", TestArmedJournalWriteFailureDeletesNothing);
            RunCase("Shell cleanup journal 拒绝错误部署操作 ID", TestShellCleanupRejectsMismatchedDeploymentOperationId);
            RunCase("Completed journal 写入失败后可幂等恢复", TestCompletedJournalWriteFailureRemainsRetryable);
            RunCase("Completed journal 删除失败后只重试元数据", TestCompletedJournalDeleteFailureOnlyRetriesJournal);
            RunCase("损坏的 Shell cleanup journal 保持 fail-closed", TestDamagedShellCleanupJournalFailsClosed);
            RunCase("Shell 清理保留原位替换的新快捷方式", TestShellCleanupPreservesReplacedShortcut);
            RunCase("Shell 清理待办根可在重启和状态刷新后持续显示", TestPendingShellCleanupRootIsResolvedAndVisible);
            RunCase("启动路径保留并恢复待处理卸载事务", TestPendingUninstallRootIsRecoveredOnStatus);
            RunCase("注册表自动发现只接受唯一完整受管便携目录", TestPortableRegistryDiscovery);
            RunCase("安装 provenance 可读取并检测关键派生文件篡改", TestInstallationProvenanceAndHealth);
            RunCase("兼容状态直接检查文件且手动检查检测篡改", TestCompatibilityStatusOverview);
            RunCase("未知沙箱主进程结构严格失败关闭", TestUnknownCompatibilityStateFailsClosed);
            RunCase("兼容开关按实际功能状态解析", TestCompatibilityDesiredAndActualStatesRemainSeparate);
            RunCase("兼容单项应用跳过异常和未改动功能", TestCompatibilityApplyMaskSkipsUnmanagedFeatures);
            RunCase("窗口启动不恢复持久兼容状态", TestMainWindowStartsWithoutCompatibilityState);
            RunCase("兼容选项说明清楚呈现实际作用", TestCompatibilityOptionDescriptionsAreClear);
            RunCase("未应用兼容草稿退出后清空", TestMainWindowCompatibilityDraftIsSessionOnly);
            RunCase("兼容维护拒绝篡改和未经批准的未验证安装", TestCompatibilityMaintenanceHealthGate);
            RunCase("兼容维护预检拒绝非 Codex 目录且不停止进程", TestCompatibilityPreflightRejectsUnownedRootBeforeStoppingProcesses);
            RunCase("兼容维护停止进程后拒绝被替换的安装目录", TestCompatibilityPreflightRejectsReplacementAfterProcessStop);
            RunCase("兼容维护 marker 写入失败时恢复全部文件", TestCompatibilityMarkerFailureRollsBack);
            RunCase("兼容维护磁盘不足时恢复全部文件", TestCompatibilityDiskFullRollsBack);
            RunCase("兼容维护中断后按 journal 可重入恢复", TestCompatibilityInterruptedRecovery);
            RunCase("兼容维护 marker 异常时拒绝恢复陌生制品状态", TestCompatibilityRecoveryRejectsUnknownArtifactWithDamagedMarker);
            RunCase("兼容维护 FilesChanged 阶段允许 marker 异常降级恢复", TestCompatibilityRecoveryAllowsDamagedMarkerAtFilesChanged);
            RunCase("兼容维护较早阶段拒绝 marker 异常降级恢复", TestCompatibilityRecoveryRejectsDamagedMarkerBeforeFilesChanged);
            RunCase("兼容维护恢复拒绝同路径的新安装", TestCompatibilityRecoveryRejectsReplacementInstall);
            RunCase("兼容维护无备份身份时保留待诊断现场", TestCompatibilityRecoveryRequiresBackupIdentity);
            RunCase("兼容维护拒绝安装树内部 junction 越界", TestCompatibilityJournalRejectsNestedJunction);
            RunCase("兼容维护 journal 拒绝缺失或被强制转换的关键字段", TestCompatibilityJournalRejectsMissingOrCoercedFields);
            RunCase("兼容维护可严格恢复升级前的七字段 journal", TestLegacyCompatibilityJournalRecovery);
            RunCase("兼容维护只更新实际修改制品的 provenance", TestCompatibilityProvenanceUpdatesOnlyChangedArtifacts);
            RunCase("EXE 图标补丁失败时正式文件保持不变且临时文件清理", TestIconPatchIsTransactional);
            RunCase("图标资源缺失或补丁失败不阻断部署", TestVisualCompatibilityBestEffort);
            RunCase("MSIX PKCX 签名者从锁定包数据读取并复验", TestMsixSignatureSignerExtraction);
            RunCase("MSIX 签名瞬时文件读取失败有限退避重试", TestMsixTrustTransientFileRetry);
            RunCase("MSIX 元数据在访问包前拒绝错误身份", TestMsixPackageMetadataValidation);
            RunCase("官方缓存 MSIX 可信验证及篡改元数据拒绝", TestMsixPackageTrust);
            RunCase("MSIX 大小与摘要失败使用可回退异常分类", TestMsixDigestMismatchClassification);
            RunCase("staging 写入时同步验证 BlockMap", TestStagingBuilderStreamsBlockMapValidation);
            RunCase("staging 流式构建拒绝被篡改的 payload", TestStagingBuilderRejectsTamperedPayload);
            RunCase("staging 流式构建拒绝非空目标目录", TestStagingBuilderRejectsNonemptyRoot);
            RunCase("staging 并行构建预取消时保持空目录", TestStagingBuilderHonorsPreCancellation);
            RunCase("provenance 复用受租约保护的 staging 摘要", TestProvenanceReusesLockedStagingDigests);

            VerifyTestRegistration();
            if (selected == 0)
            {
                failed++;
                Results.Add("FAIL [0] 没有测试匹配过滤器：" + testFilter);
            }
            bool succeeded = failed == 0;
            Results.Insert(0, "MANAGER=" + managerPath);
            Results.Insert(1, "SUITE_ROOT=" + suiteRoot);
            Results.Insert(2, "PASSED=" + passed.ToString(CultureInfo.InvariantCulture));
            Results.Insert(3, "FAILED=" + failed.ToString(CultureInfo.InvariantCulture));
            Results.Insert(4, "SKIPPED=" + skipped.ToString(CultureInfo.InvariantCulture));
            Results.Insert(5, succeeded ? "RESULT=PASS" : "RESULT=FAIL");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
            File.WriteAllLines(reportPath, Results.ToArray(), new UTF8Encoding(true));

            foreach (string line in Results)
            {
                Console.WriteLine(line);
            }
            return succeeded ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(Unwrap(exception));
            return 2;
        }
    }

    private static int SaveConfigPart(string[] args)
    {
        if (args.Length != 5)
        {
            return 64;
        }

        LoadManager(Path.GetFullPath(args[1]));
        string operation = args[2];
        string value = args[3];
        string readyPath = Path.GetFullPath(args[4]);
        File.WriteAllText(readyPath, Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture), Encoding.ASCII);

        if (string.Equals(operation, "install-root", StringComparison.Ordinal))
        {
            PortableStorage.SaveRecordedInstallRoot(value);
            return 0;
        }

        return 64;
    }

    private static int HoldLock(string[] args)
    {
        if (args.Length != 5)
        {
            return 64;
        }

        string assemblyPath = Path.GetFullPath(args[1]);
        string installRoot = Path.GetFullPath(args[2]);
        string readyPath = Path.GetFullPath(args[3]);
        int holdMilliseconds = int.Parse(args[4], CultureInfo.InvariantCulture);
        LoadManager(assemblyPath);

        OperationFileLock held = OperationFileLock.Acquire(installRoot);
        try
        {
            File.WriteAllText(readyPath, Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture), Encoding.ASCII);
            Thread.Sleep(holdMilliseconds);
            return 0;
        }
        finally
        {
            held.Dispose();
        }
    }

    private static int StartPostDeploymentCleanupAndExit(string[] args)
    {
        if (args.Length != 3)
        {
            return 64;
        }

        LoadManager(Path.GetFullPath(args[1]));
        using (CodexPortableService service = new CodexPortableService(delegate { }))
        {
            service.StartPostDeploymentCleanupAsync(Path.GetFullPath(args[2]));
        }
        return 0;
    }

    private static void LoadManager(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("未找到待测试的管理器程序。", path);
        }

        string linkedAssembly = typeof(CodexPortableService).Assembly.Location;
        if (string.IsNullOrWhiteSpace(linkedAssembly) || !File.Exists(linkedAssembly))
        {
            throw new FileNotFoundException("测试程序集没有绑定到正式管理器程序集。", linkedAssembly);
        }
    }

    private static void RunCase(string name, Action test)
    {
        RegisteredTestMethods.Add(test.Method.Name);
        if (!string.IsNullOrWhiteSpace(testFilter) &&
            name.IndexOf(testFilter, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }
        selected++;
        Stopwatch stopwatch = Stopwatch.StartNew();
        Console.WriteLine("START " + name);
        Console.Out.Flush();
        try
        {
            test();
            stopwatch.Stop();
            passed++;
            string result = string.Format(CultureInfo.InvariantCulture, "PASS [{0}] {1} ({2} ms)", selected, name, stopwatch.ElapsedMilliseconds);
            Results.Add(result);
            Console.WriteLine(result);
            Console.Out.Flush();
        }
        catch (RegressionTestSkippedException exception)
        {
            stopwatch.Stop();
            skipped++;
            string result = string.Format(
                CultureInfo.InvariantCulture,
                "SKIP [{0}] {1} ({2} ms): {3}",
                selected,
                name,
                stopwatch.ElapsedMilliseconds,
                exception.Message);
            Results.Add(result);
            Console.WriteLine(result);
            Console.Out.Flush();
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            failed++;
            Exception actual = Unwrap(exception);
            string result = string.Format(CultureInfo.InvariantCulture, "FAIL [{0}] {1} ({2} ms): {3}: {4}", selected, name, stopwatch.ElapsedMilliseconds, actual.GetType().FullName, actual.Message);
            Results.Add(result);
            Results.Add(actual.ToString());
            Console.WriteLine(result);
            Console.Out.Flush();
        }
    }

    private static void VerifyTestRegistration()
    {
        string[] defined = typeof(RegressionTestRunner)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method =>
                method.Name.StartsWith("Test", StringComparison.Ordinal) &&
                method.GetParameters().Length == 0 &&
                (method.ReturnType == typeof(void) || method.ReturnType == typeof(Task)))
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] registered = RegisteredTestMethods
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] duplicates = registered
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        string[] missing = defined
            .Except(registered, StringComparer.Ordinal)
            .ToArray();
        string[] unknown = registered
            .Except(defined, StringComparer.Ordinal)
            .ToArray();
        if (duplicates.Length == 0 && missing.Length == 0 && unknown.Length == 0)
        {
            return;
        }

        failed++;
        Results.Add(string.Format(
            CultureInfo.InvariantCulture,
            "FAIL [注册表] 回归入口不完整。重复：{0}；未注册：{1}；无对应实现：{2}",
            duplicates.Length == 0 ? "无" : string.Join(",", duplicates),
            missing.Length == 0 ? "无" : string.Join(",", missing),
            unknown.Length == 0 ? "无" : string.Join(",", unknown)));
    }

    private static void Skip(string reason)
    {
        throw new RegressionTestSkippedException(reason);
    }

    private sealed class RegressionTestSkippedException : Exception
    {
        internal RegressionTestSkippedException(string message)
            : base(message)
        {
        }
    }


}
}
