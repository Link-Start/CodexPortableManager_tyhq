# Codex Portable Manager 架构

> 文档定位：当前模块边界、事务设计和扩展约束。构建与测试入口见 [开发与验证](DEVELOPMENT.md)，面向用户的产品边界与发布说明见 [README](../README.md)。

## 定位

本程序的核心产品形态是：从可信官方 MSIX **主动创建并维护普通目录便携版**。独立下载官方 MSIX 是同一可信包管线提供的辅助用例，下载后由 Windows 安装的官方桌面版不属于兼容模块或部署事务的管理范围。

兼容模块只允许作用于经过 `InstallOwnership` 和 payload 校验的便携安装根。架构不提供修改 `C:\Program Files\WindowsApps`、接管系统包目录权限、重签官方包或绕过 Windows 包更新生命周期的能力。

本程序采用安全优先的模块化单体结构。发布物保持单 EXE，第三方 JavaScript 解析器及兼容程序集内嵌且不要求外部运行文件；界面使用编译期 XAML/BAML 与轻量 code-behind，不引入完整 MVVM、DI 容器、多项目分层或通用基础设施框架。

架构目标按优先级排列：

1. 破坏性文件操作可验证、可恢复、可回归测试。
2. 官方包来源、缓存和可信验证具有明确边界。
3. 界面调用保持直接，普通维护不需要理解底层事务。
4. 模块数量服从实际变化原因，不为形式上的分层增加概念。

## 调用方向

```text
Program
  -> MainWindow
     -> OperationController + UiState
     -> CodexPortableService
        -> PackageResolver
           -> CodexMicrosoftStoreSource
              -> MicrosoftStoreProtocolClient
        -> StorePackageLifecycle
           -> WindowsStorePackageGateway
              -> Windows PackageManager
           -> ProcessesUnderPath
        -> ArtifactPipeline
        -> DeploymentEngine
           -> ArtifactPipeline
           -> ProcessesUnderPath
           -> CompatibilityCoordinator
              -> CompatibilityPlan
                 -> AsarSession
                 -> JavaScriptSemanticDocument
              -> SandboxCompatibility
           -> ShellIntegrationCoordinator
              -> ShellIntegration
           -> DeploymentJournal
           -> InstallationIdentity / ArtifactProvenance / InstallationHealth

MainWindow
  -> InstallLocationResolver
     -> PortableStorage
     -> ShellIntegration
     -> InstallOwnership
```

`PortableStorage` 只负责便携数据路径、安装目录配置和集成状态的持久化，不主动发现注册表目录，也不判断安装 payload 是否有效。`config.json` 只包含 `InstallRoot`，读取时不识别或迁移其他开发期格式。

## 主要职责

### CodexPortableService

应用门面和用例编排层。对窗口提供检查、便携版创建与更新、官方 MSIX 下载、回滚、卸载、启动、兼容设置和系统集成操作，不拥有目录交换、事务 journal 或恢复状态机。兼容设置入口必须继续要求有效的便携安装根，不能退化为任意 EXE 或 WindowsApps 路径修改器。

### OperationController 与 UiState

`OperationController` 唯一持有 UI 操作的忙碌状态、取消源、可取消阶段和界面锁定策略；每条 `OperationProgress` 可临时关闭取消，普通进度在阶段结束后恢复取消，而 `TryEnterNonCancelablePhase` 一旦进入迁移提交阶段就永久关闭本次操作的取消，后续进度不能误重新开启。下载暂停源还为每个运行代次提供一次性中断令牌，暂停时取消旧代次，继续时创建新代次，使在途网络读取能够释放并重建，而不是只改变 UI 布尔值。`OperationSnapshot` 在操作开始时冻结安装路径、路径修订号和 `CompatibilityOptions`。

兼容复选框在无草稿时表示当前安装文件的现场检测结果，旁边状态标签同样来自 helper 与 `app.asar` 的实际状态。普通 Checked/Unchecked 事件只标记当前会话草稿，不访问持久配置；只有“单独应用到便携版”会按冻结草稿修改现有安装。未应用就退出时草稿自然丢弃，不依赖 Closing 清理。状态解析缺少某项现场结果时返回未知，不得回退 `AppliedFeatures` 或其他记录猜测开启/关闭；provenance 只提供官方基线、完整性校验和审计结果。后台刷新不得覆盖当前会话脏草稿。

`UiStateInput` 冻结控制器状态、当前路径校验结果、current、实体 `.previous` 与缓存低版本可用性、Store 安装状态、后台卸载清理、兼容状态修订和五项现场可判定性；`UiState` 一次性计算全部输入控件、兼容开关与命令按钮的最终可用性。回滚使用 `.previous` 与缓存候选的并集，卸载只把实体 `.previous` 视为回滚备份。窗口 code-behind 只把该状态一对一投影到控件，不在 `IsEnabled` 赋值处追加第二套条件。

### PackageResolver

只负责调用 `CodexMicrosoftStoreSource` 解析微软目录并返回已经选定的 `PackageMetadata`。它不接触缓存文件、下载目标、解包目录或部署状态机。

### ArtifactPipeline

只消费已经选定的 `PackageMetadata`，负责缓存校验、下载、SHA-256、MSIX 信任、解包、Manifest 和 BlockMap 校验。它不保存 Microsoft Store ProductId，也不决定 Codex 包名、最新版或目标架构。缓存回滚元数据由严格文件名和当前架构选择器生成，并以稳定文件摘要绑定本次读取；信任根仍是固定包身份、微软签名、Publisher 与 Manifest。`localCacheOnly` 校验失败时只隔离文件并停止，不因缺少微软目录 URL 转为联网下载。缓存原子发布和 Windows MSIX 签名/身份验证用进度模型临时关闭取消；保存已验证官方 MSIX 则以 1 MiB 分块写入同目录唯一临时文件，每块检查取消，完整刷新后原子替换目标。

`StagingBuilder` 先在单线程阶段确认 MSIX 中央目录、Manifest、文件层级、全部目标路径和唯一父目录；父目录首次出现时创建并检查重解析点，后续条目只命中内存集合。写入阶段最多启动 4 个工作线程，每个线程独占只读包流、`ZipArchive`、64 KiB 缓冲区和 BlockMap SHA-256，使用原子索引领取唯一文件，不共享解码状态。关键制品摘要和最终计数只在短锁内合并；任一线程失败或取消时记录首个真实异常、取消其余线程并等待全部退出，再由既有 staging 事务整体丢弃未完成目录。路径边界、文件层级冲突、中央目录一致性、BlockMap 全块摘要、长度、空 staging 和稳定包租约约束均未减少。

缓存发布锁只覆盖缓存检查、下载发布和建立 `VerifiedArtifactLease`。租约持有从已验证文件句柄解析的稳定路径，并在后续导出或解包期间继续禁止底层文件写入和删除；因此大型解包不再长期占用跨进程缓存锁，其他消费者仍可并发取得同一可信制品的只读租约。

完整下载完成后可能继续持有不共享写入的 `ReadWrite` 文件句柄。MSIX 信任链因此不能再按路径二次打开同一文件：`WinVerifyTrust` 直接使用租约句柄，Publisher 则从同一锁定包流读取唯一 `AppxSignature.p7x`，校验 `PKCX` 标头并以 `SignedCms` 复验 PKCS#7 内容签名后取得签名者证书。证书 Subject 必须同时匹配 Manifest Publisher 与固定 Store Publisher；摘要、证书链和吊销状态仍由微软目录摘要及句柄版 `WinVerifyTrust` 独立约束。

缓存未命中时，增量获取只对同架构、低于目标版本且文件名严格匹配的旧版官方 MSIX 生成复用计划。远程目标布局只 bootstrap 一次，随后有界评估最多四个旧缓存并按目标网络补集从小到大尝试；单个候选解析失败、收益不足或物化后整包摘要不匹配只淘汰该候选，全部不可用时才回退完整下载。单次明确 Range 上限仍为 16 MiB，但响应体按约 1 MiB 分块写入跨重试目标缓冲区；传输中断后剩余请求从已接收偏移继续。增量和完整下载都在百分比变化或约 250 ms 后上报实时字节、速度和预计剩余时间，暂停和网络等待会重置测速窗口；日志仍只保留阶段与里程碑。已安装便携目录包含解压与兼容变换结果，不作为目标包复用来源。

### CodexMicrosoftStoreSource

保存 Codex 的 ProductId、Package Family、Publisher、包名和架构策略，从微软目录候选中选择最新有效主包，并交叉核对 Display Catalog 与 Windows Update 返回的文件元数据。

### MicrosoftStoreProtocolClient

负责 Display Catalog JSON、Windows Update Cookie、SyncUpdates XML 和微软 CDN 地址解析。Catalog 与 SOAP 请求对瞬时网络错误执行有限退避重试，Windows Update 按 FE3、FE6、FE6CR 顺序完成整套事务回退；任何端点切换都必须重新获取 Cookie、同步元数据和下载地址。该模块限制响应大小、识别 SOAP Fault，只返回微软协议 DTO，不知道具体 Codex 产品身份，也不直接生成应用使用的 `PackageMetadata`。

下载器关闭自动重定向，最多逐跳跟随五次，并对每个目标重新验证 `delivery.mp.microsoft.com` 域名。`ArtifactPipeline` 内的下载恢复状态机以临时文件长度为断点，逐跳保留 Range 请求并严格核对 `206 Content-Range`、总大小和最终 SHA-256；服务器忽略 Range 时只允许清空当前临时文件后从头下载。HTTP `408`、`429`、`500`、`502`、`503`、`504` 和瞬时传输异常进入有限重试。`NetworkAvailabilityMonitor` 同时订阅系统网络可用性与地址变化，并通过 Windows Network List Manager 区分 Internet 可用状态；网络事件会取消当前请求代次并立即唤醒恢复循环。NCSI 无 Internet 只降低探测频率，达到当前指数退避截止点后仍主动探测 CDN，避免 NCSI 假阴性无限卡住；系统事件或轮询转为在线时立即探测。微软 CDN 接受受信任请求才是实际恢复依据，避免把网卡在线误判为下载源可达。响应头或响应体连续 30 秒无进展时，独立看门任务仍会主动取消请求并释放响应流。网络等待、用户暂停和立即重试使用独立中断代次：系统恢复可自动继续，但不能解除用户暂停；“立即重试”会中断当前等待或探测并马上重建请求；取消同样主动释放在途流，使 UI 操作最终退出 `Busy`。

### StorePackageLifecycle

只负责当前用户官方桌面版的登记查询与卸载编排。它按 Codex 包名和 Package Family 双重过滤登记，卸载时复用同一份查询快照，先通过 `ProcessesUnderPath` 关闭包目录内进程，再调用 `WindowsStorePackageGateway`；不读取下载目录、便携安装身份或部署 journal。

`WindowsStorePackageGateway` 是 `Windows.Management.Deployment.PackageManager` 的最薄适配层。每次调用在当前执行线程创建原生管理器，避免跨线程持有 WinRT 对象；产品运行时不启动 PowerShell，也不解析命令行文本。接口只隔离 Windows 系统边界并支持无破坏回归测试，不扩展成通用包管理框架。

### DeploymentEngine

`DeploymentEngine` 仍是部署状态机的唯一所有者，并以 `partial` 文件分别承载安装更新、回滚恢复、卸载恢复和路径/文件操作。它负责 install、update、rollback、uninstall 的操作锁、安装根校验、目录移动、安全删除、当前 journal、恢复、staging 兼容设置和 Shell 集成的事务编排，以及崩溃工作目录维护。回滚目标按“更低的 `.previous`、缓存中最高的同架构低版本、原双向 `.previous` 切换”排序；缓存目标允许显式降级，但仍复用完整包信任、staging、兼容事务和 update journal，绝不原位覆盖 current。删除原语按对象语义分层：空安装槽只在同一句柄确认无子项后直接删除；普通文件相对稳定父句柄 no-follow 打开，并在迁移或维护场景复验持久 File ID；受管目录树才允许递归，并继续要求允许父目录、最终目录 File ID 或 receipt。枚举属性只作为最小权限提示，普通文件不申请 `FILE_READ_DATA`，只有经句柄复验的普通目录才申请 `FILE_LIST_DIRECTORY`。枚举后类型、身份或只读状态发生变化时失败并由既有重试重新取得权限，不放宽竞态边界。部署 API 不接收窗口兼容快照；新安装使用全关闭选项，更新在持有安装锁后直接检查上一安装的 helper 与 `app.asar`，把可确认的实际状态交给官方 staging 变换。未知官方结构且不存在本工具受管标记时解析为关闭，不阻断更新；正常更新遇到受管标记混合或无法识别时停止，明确缓存回滚则以全关闭目标继续并保留原 current 为 `.previous`，不解释或迁移旧开发配方。增量获取、`ArtifactPipeline`、回滚和 Shell 集成不接收窗口兼容选项。目录内进程关闭由共享的 `ProcessesUnderPath` 实现，部署引擎只决定调用时机和超时策略。更新在新 current、`.previous` 和外部状态提交后即可向 UI 返回；`PostDeploymentCleanupWorker` 通过同一 EXE 的非 GUI 命令启动独立后台进程，重新取得安装根操作锁并回收已隔离的 `transaction-old`，随后执行缓存维护。便携卸载同样在 current/previous 已原子移动到带 Armed receipt 的 tombstone、活动槽提交为空且 Shell 清理已发起后返回，再由 `UninstallCleanupWorker` 完成物理删除。窗口退出不会终止两类进程，异常退出则保留 journal 供后续启动继续恢复。它只单向调用具体协调器，不通过委托回调 Service。任何目录拓扑或提交阶段变化都必须继续由故障注入回归测试覆盖。

### DeploymentJournal

记录操作 ID、安装 ID、操作类型和阶段枚举，以及恢复所需的初始拓扑和选项快照。更新、回滚和卸载共享当前唯一结构，不读取旧 journal，也不根据目录拓扑补造事务授权。清理 receipt 使用 `Prepared -> Armed`：`Prepared` 同时绑定来源目录身份和安装 marker 文件的持久 File ID，已经验证所有权的来源句柄必须跨越 journal 落盘与目录移动；移动后再从同一句柄取得最终 tombstone 身份，并把 `Armed` 与对应 `Detached` 阶段作为同一个候选记录原子写入。Prepared 崩溃恢复必须同时复验来源目录和 marker；目录 ID 因文件系统语义变化时保持 pending，不猜测恢复。只有 `Armed` 可以授权递归删除，最终删除仍必须在同一打开句柄上复验目标身份；无 receipt 的槽只确认缺失，绝不执行无身份删除。更新或卸载的逻辑完成与物理回收之间允许跨进程、跨窗口生命周期，journal 在后台进程完成前不得提前删除。原始 JSON 严格要求当前全部字段及其类型，缺失值不能默认成 `false`。journal 自身读写使用扩展路径，原子临时名不会重新引入传统 `MAX_PATH` 限制。

### 安装身份、来源与健康

`InstallationIdentity` 只记录安装 ID、包名和版本，用于所有权与版本配对。`ArtifactProvenance` 记录经过可信管线验证的官方 MSIX PackageFullName、SHA-256、架构、已应用/未完成功能，以及最终关键 EXE、ASAR、helper、图标和 Manifest 摘要。两者写入当前唯一安装记录结构；其他结构不兼容读取。

`InstallationHealth` 独立读取身份与 provenance，重新计算关键文件摘要并返回 Healthy、Unverified、Tampered 或 Invalid。健康判断不与“目录是否归本管理器所有”混为一体，也不阻止用户显式启动自定义过的普通目录；需要可信派生状态的维护入口可以单独采用健康结果。

### CompatibilityCoordinator 与 ShellIntegrationCoordinator

是 `DeploymentEngine` 使用的具体应用协调器，负责把事务选项映射到兼容模块和 Shell 操作。协调器不拥有目录事务，也不反向调用 Service。

`IntegrationState` 只描述当前注册状态，并严格要求 `InstallId`、逻辑注册根、最终物理根、目录身份和精确资源范围。注册表根同时写 `CodexPortableInstallId`、路径 marker 和物理根；归属判断同时复验当前 command/resource 内容，陈旧 marker 不能覆盖已被其他程序接管的注册项。`ShellIntegrationCleanupJournal` 是独立的持久清理事务，使用 `Prepared -> Armed -> Completed`：卸载必须在 payload 分离前冻结范围并写入 `Prepared`，提交后先持久化 `Armed` 才能删除 Shell 资源，全部幂等清理完成后写 `Completed` 并删除 journal。所有 deployment 用途的 Prepare、Complete 和 Cancel 都要反查实际 `DeploymentJournal` 的 root、`InstallId`、operation ID 与持久化提交阶段；只匹配 Shell journal 自身字段不能取得删除或取消授权。快捷方式最终删除在同一文件句柄上重算 receipt 摘要，失效 SUBST 根会回落到持久物理别名显示待办。

### CompatibilityPlan 与 AsarSession

`CompatibilityPlan` 收集模型目录、沙箱账户环境和界面语言的目标状态。源状态探测与目标包能力探测相互独立：旧安装通过本工具唯一标记判断功能是否实际受管，新官方 staging 再按当前代码结构判断是否能应用。全部关闭且没有标记时不解析功能指纹；需要处理时，所有目标变换在同一 ASAR 会话中组合。

`CompatibilityJournal` 与部署 journal 一样先验证原始 JSON 的当前完整字段和精确类型，再反序列化为事务记录。当前日志带 `SchemaVersion`，严格要求五个 Enabled 与四个 Manage 共九项选项布尔值；升级前无版本号的七字段日志只在精确匹配旧结构时允许进入恢复，不能让新字段的反序列化默认值取得恢复授权。`OriginalExists`、`TargetExists`、`Modified`、`InstallRootIdentity`、`InstallMarkerRequired` 和 `BackupDirectoryIdentity` 缺失或被错误类型替代时同样失败关闭。事务开始时绑定安装根持久 File ID、原安装 ID 和 marker 要求；恢复前先复验安装根身份，当前 marker 可解析时还必须匹配原安装 ID。marker 本身属于受保护制品；只有 `FilesChanged` 已持久化全部目标摘要后，marker 缺失或损坏才允许依靠根身份降级恢复，并且其余每个受保护制品都必须匹配 journal 的原始态或已捕获目标态。较早阶段、已提交阶段或任一陌生摘要均保留 journal、备份和现场并失败关闭；备份身份缺失或清理失败时同样不得清理现场。

兼容设置的进程关闭发生在持有安装根操作锁之后。`CompatibilityMaintenance.PreflightApply` 先验证安装健康门、所有权、安装 ID 和安装根 File ID，再允许 `ProcessesUnderPath` 停止目录内进程；停止后通过预检快照复验目标未变化，随后 `Apply` 继续执行完整校验和事务写入。该双重校验保证任意目录、非 Codex payload 或竞态替换目标不会仅因用户点击兼容操作就先被结束进程。

新版本 staging 先建立官方 provenance 基线，再按部署引擎从旧安装文件解析出的实际状态执行兼容事务；新安装则使用全关闭选项。继承目标在新版本中明确 `Unsupported` 且没有受管标记或文件变更时，staging 专用协调入口将其归一为官方关闭的 `NotRequired`，不把部署报告成部分失败；独立手动应用入口仍严格报告无法开启。任一功能开启时只备份并验证 ASAR、sandbox helper 和 marker。失败功能只要 `Changed=false` 且 `Before==After`，就保持原文件不变，不阻止同一事务中其他独立成功功能提交；语言内部的菜单与推理组件也分别暂存和恢复。任何失败功能已经修改文件且无法证明恢复、逐项结果自相矛盾、marker/摘要写入失败或事务基础设施异常时，仍整体回滚受保护文件。最终 provenance 记录结果用于审计和完整性校验，但不能替代后续现场检测。

`AsarSession` 是唯一的 ASAR 结构解析与写入实现。打开时只解析头部和条目元数据，目标条目按需读取并验证完整性，未修改条目在输出时从源文件流式复制。单个功能的多步暂存使用可回滚 checkpoint，失败不能夹带部分暂存进入其他成功功能；所有变换统一更新结构化哈希元数据，经临时文件重新打开和目标状态验证后，只执行一次原子替换。写回严格遵循官方 Pickle 头格式：JSON 字段记录未补齐字节数，对齐区使用 `0x00`；因此可逆变换关闭后能恢复官方 ASAR 的同一 SHA-256。

每个受管功能内部使用 `Official`、`Patched`、`Mixed`、`Unsupported` 四态。只有 Official 与 Patched 可以相互转换；Mixed 必须拒绝修改。现场读取还允许 `UnmanagedOrOfficial` 和 `NativeSupported`：前者表示未知结构中没有受管标记，后者表示官方已经原生提供目标能力。能够确认官方关闭且配方可用时开关保持可操作；`Unsupported` 则强制显示关闭、禁用开关并附简要原因，不触发文件写入。

### InstallLocationResolver

协调已记录目录、待恢复 deployment/Shell journal、Shell 注册项发现和 Codex payload 校验。对 UI 而言，用户选择的是候选存放位置，`ResolveInstallDestination` 返回并回填的是最终便携版目标目录；只有完整可运行的目录可以成为成功记录。卸载已经提交但清理未完成时，原安装根可从持久 journal 恢复用于状态展示，且路径输入保持锁定，避免切换目标后遗失恢复入口。

### PortableStorage

负责 `data` 路径、配置序列化、当前集成状态、原子写入和旧缓存迁移。`integration.json` 不是清理事务 journal；损坏或缺失时，独立 Shell cleanup journal 仍必须提供完整重试意图。存储层不得反向调用 Shell 或部署服务。

### 静态系统适配器

`ShellIntegration` 继续保持单一具体静态模块，并以 `partial` 文件分别承载门面 API、注册编排、清理事务、状态归属和平台适配；实际注册表/快捷方式写入与三态归属读取分别下沉到无状态具体类 `ShellRegistrationWriter` 和 `ShellOwnershipChecker`，不引入接口或 DI。`NativeFileSystem`、`MsixPackageTrust`、`ProcessesUnderPath`、各兼容模块和锁实现同样保持具体模块。协议、扩展名、ProgID、AppUserModelID 和可执行文件名的 canonical 安全规则集中在 `ShellResourceNameRules`，注册前的清单值规范化仍由 `ShellIntegration` 负责。只有出现真实替换需求时才引入接口。

模型目录、推理内容映射、推理卡片布局、菜单提交、托盘和性能跟踪入口由内嵌 Esprima 建立只读 AST 索引，以成员关系、对象属性、局部数据流和调用上下文定位原始源码区间；同一源码版本只解析一次，预序索引记录连续子树区间，避免子节点查询重复扫描整个 bundle。模型目录在完整 AST 前先以可解码属性名建立轻量候选索引，原始、Unicode、十六进制、八进制和字符串身份转义都进入同一判断；索引异常或零候选时回退原有全量扫描，不能把索引结果当成兼容白名单。推理显示以 `reasoning` 分支中 summary 格式化结果到 UI `push` 的数据流定位映射入口，以 `reasoning-markdown`、`maxHeightByState` 和 `disableMaxHeight` 能力定位布局入口，并在同一组件函数内定位绑定 `item` 的 `useState(false)` 展开状态。原始 `content` 有非空块时添加不可见来源标记并按空行拼接，否则完整保留官方 summary 调用作为回退；来源标记只用于让原始推理块默认展开，用户仍能手动折叠，官方摘要保持原行为。映射入口可与布局组件位于不同 chunk，三处编辑必须作为一个功能在同一 ASAR 临时文件中验证，任一候选重复、缺失或受管标记不完整时均失败关闭；已部署的上一版双标记开发配方作为唯一迁移来源，可在同一事务内升级为三标记当前配方。临时 ASAR 已完成全条目 integrity 校验后，只复验原分析确认且唯一被修改的条目；事务提交成功时 UI 直接采用该验证结果，事务未提交、旧现场状态缺失或路径修订变化时仍重新读取文件。JavaScript 正则字面量保留词法节点和源码区间，但不转换或编译为 .NET 正则，因此新版 ECMAScript Unicode 属性不会阻断与正则无关的菜单分析。变换只包裹、插入或替换唯一确认的区间，不重新生成 JavaScript bundle；同一条目的多处编辑按源码区间逆序执行。模型补丁把官方过滤表达式保留在不可达分支中，推理显示把官方 summary 调用和默认折叠表达式保留在条件回退分支中，关闭时均直接恢复原文。推理强度从 `composer.mode.local.reasoning.<level>.label` 键族动态发现，少量新增或减少档位不会要求更新固定清单。中文菜单资源和主进程脚本独立提交：匹配部分应用，未匹配部分保留官方状态并返回兼容提示；受管标记损坏、恢复不确定或候选不唯一的部分仍失败关闭。产品不安装 loader、preload 或 Electron Hook，不执行运行时注入。

EXE 图标、独立 ICO 和窗口 ICO 均先复制到同目录临时文件，完成资源/格式复验后原子替换；正式 EXE 不再传给 `BeginUpdateResource`。沙箱账户环境修正与其他 ASAR 功能共用临时文件验证和原子替换；官方签名 helper 只作为来源派生制品校验，永远不进入兼容写入事务。

## 扩展规则

- 新兼容开关加入 `CompatibilityOptions`，不要继续扩散布尔参数。
- Microsoft Store 协议变化限制在 `MicrosoftStoreProtocolClient`，Codex 包身份或选择策略变化限制在 `CodexMicrosoftStoreSource` 和 `PackageResolver`；缓存、下载和制品验证变化限制在 `ArtifactPipeline`。
- 当前用户 Store/MSIX 登记或卸载行为变化限制在 `StorePackageLifecycle` 与 `WindowsStorePackageGateway`，不要回退到脚本进程或把系统包生命周期并入便携部署事务。
- 只有增加真实的第二下载源、需要完全替换在线来源或需要多来源回退时，才为包来源引入接口。
- 新目录事务阶段加入 `DeploymentEngine`，并为每种中断拓扑增加回归用例。
- 配置恢复策略加入 `InstallLocationResolver`，序列化细节留在 `PortableStorage`。
- UI 使用编译期 XAML/BAML 与轻量 code-behind；跨窗口颜色、字号、控件高度、圆角和常用间距集中在 `DesignTokens.xaml` 并由 `App.xaml` 全局加载，窗口专属模板和布局留在 `MainWindow.xaml`。交互编排留在 `MainWindow.xaml.cs` 与按职责拆分的 `MainWindow.Operations.*.cs`，操作生命周期与完整按钮矩阵留在轻量 `OperationController`/`UiState`，顶部状态由单一语义映射生成，不引入完整 MVVM。短高度窗口折叠可由当前任务区替代的顶部摘要，优先保留主操作区。

## 验证基线

架构调整至少需要通过 Release 构建、常规隔离回归、离线 Store/网络专项、路径自动刷新和 WPF 渲染测试。GitHub `Windows 验证` 工作流固定执行 PowerShell 语法检查、Release 构建、常规隔离回归、离线 Store/网络专项和路径自动刷新；真实 MSIX 的签名、摘要、包身份和稳定句柄需要通过 `Run-MsixTrustTests.ps1 -PackagePath` 逐包手动验证。常规回归分别统计 PASS、FAIL 与 SKIP，过滤器零命中时失败，并通过反射校验测试实现与注册入口一一对应；真实双包增量与真实缓存包篡改副本用例默认明确跳过。`-LargeMsix` 目前依赖测试源码中的固定历史包路径和基线计数，参数化前不能作为任意版本的通用发布门禁。
