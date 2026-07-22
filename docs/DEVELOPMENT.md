# Codex Portable Manager 开发与验证

> 本文档描述当前通用构建和验证流程，不记录个人开发过程或按日期排列的实施日志。模块边界见 [架构说明](ARCHITECTURE.md)，发布安全边界见 [功能与安全审计](../AUDIT.md)。

## 环境要求

- Windows 10/11。
- PowerShell 7（`pwsh.exe`），不使用 Windows PowerShell 5.1。
- Visual Studio 2022 或 Build Tools 中的 Roslyn 编译器、MSBuild 与 .NET 桌面构建工具。
- .NET Framework 4.6.2 targeting pack。
- Windows SDK 的 WinRT 投影程序集与 Windows 元数据。
- 首次恢复内嵌兼容解析器依赖时需要访问 NuGet。

`scripts/BuildEnvironment.ps1` 会从 PATH、Visual Studio/Build Tools、系统框架目录和 Windows SDK 自动定位依赖。自定义环境可设置：

- `CPM_CSC_PATH`
- `CPM_FRAMEWORK_PATH`
- `CPM_WINDOWS_RUNTIME_PATH`
- `CPM_SYSTEM_RUNTIME_PATH`
- `CPM_WINDOWS_METADATA_PATH`

## 构建

Release 构建：

```powershell
pwsh.exe -NoProfile -File .\build.ps1 -Configuration Release
```

Debug 构建：

```powershell
pwsh.exe -NoProfile -File .\build.ps1 -Configuration Debug
```

构建脚本会先恢复经过版本和摘要约束的 Esprima 兼容程序集，再通过 WPF MarkupCompile 生成 BAML，并编译正式程序、测试程序和便携启动退出夹具。

## 输出目录

| 路径 | 用途 |
| --- | --- |
| `release\CodexPortableManager.exe` | 对外发布的正式单文件管理器。 |
| `dist\app\CodexPortableManager.exe` | 本地开发和验证使用的程序。 |
| `dist\tests\CodexPortableManager.Tests.exe` | 回归与专项测试程序，不进入发布包。 |
| `dist\symbols\*.pdb` | 仅 Debug 构建生成的调试符号。 |

重复构建只替换 `release` 中的 EXE，并保留本地运行产生的 `release\data`。对外发布时只上传最终 EXE 和对应校验清单，不包含 `data`、`dist`、PDB、测试程序或本地 MSIX。

## 常用验证

先完成对应配置的构建，再运行：

```powershell
.\scripts\tests\Run-RegressionTests.ps1
.\scripts\tests\Run-PathAutoRefreshTest.ps1
```

验证真实官方 MSIX：

```powershell
.\scripts\tests\Run-MsixTrustTests.ps1 -PackagePath "D:\Packages\OpenAI.Codex_version_x64.msix"
```

运行离线 Store 协议、候选选择和网络韧性专项：

```powershell
& .\dist\tests\CodexPortableManager.Tests.exe --store-resolver-test "$env:TEMP\CodexPortableManager-store-resolver-test.txt" off
```

将最后一个参数改为 `on` 可额外访问实时 Catalog 和 Windows Update 端点。实时验证依赖外部服务状态，不作为每次离线回归的前提。

`Run-MsixTrustTests.ps1` 默认可复用当前架构最近使用的本地官方 MSIX 缓存，也可通过 `-PackagePath` 指定包。`Run-RegressionTests.ps1 -LargeMsix` 仍依赖测试源码中的固定历史双包基线，参数化前不能作为任意两个 MSIX 的通用验证入口。

## 持续集成

`.github/workflows/windows-ci.yml` 在 `windows-2022` 上执行：

1. PowerShell 语法检查。
2. Release 构建。
3. 常规隔离回归。
4. 离线 Store 与网络专项。
5. 路径自动刷新。

常规回归分别统计 PASS、FAIL 与 SKIP；过滤器零命中、测试入口重复、实现未注册或注册目标不存在都会使门禁失败。真实双包增量和真实缓存包篡改副本依赖大型本地夹具，默认明确记为 SKIP。

## 发布检查

发布前至少确认：

1. `main` 对应的 GitHub Windows 验证通过。
2. 最终 EXE 来自 Release 构建，且没有把本地 `data` 一并打包。
3. Release 页面附带最终 EXE 的 SHA-256 或 `SHA256SUMS.txt`。
4. 若配置 Authenticode，必须先完成最终构建，再签名并使用可信时间戳；签名后的文件需要重新计算 SHA-256。
5. 发布说明明确当前 EXE 是否已签名，并链接许可证与第三方声明。
