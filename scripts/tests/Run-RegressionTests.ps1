[CmdletBinding()]
param(
    [string]$ManagerPath,
    [switch]$KeepArtifacts,
    [string]$TestFilter,
    [switch]$LargeMsix
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$testRunnerPath = Join-Path $projectRoot "dist\tests\CodexPortableManager.Tests.exe"
$linkedManagerPath = Join-Path $projectRoot "dist\tests\CodexPortableManager.exe"
$portableExitFixturePath = Join-Path $projectRoot "dist\tests\CodexPortableManager.PortableExitFixture.exe"
if ([string]::IsNullOrWhiteSpace($ManagerPath)) {
    $ManagerPath = Join-Path $projectRoot "dist\app\CodexPortableManager.exe"
}
$ManagerPath = [IO.Path]::GetFullPath($ManagerPath)

foreach ($requiredPath in @($ManagerPath, $testRunnerPath, $linkedManagerPath, $portableExitFixturePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "未找到回归测试所需文件：$requiredPath。请先运行 build.ps1。"
    }
}

$requestedHash = (Get-FileHash -LiteralPath $ManagerPath -Algorithm SHA256).Hash
$linkedHash = (Get-FileHash -LiteralPath $linkedManagerPath -Algorithm SHA256).Hash
if (-not [string]::Equals($requestedHash, $linkedHash, [StringComparison]::OrdinalIgnoreCase)) {
    throw "待测试管理器与测试程序集编译时绑定的管理器不一致。请重新构建后再运行回归测试。"
}

$runRoot = Join-Path ([IO.Path]::GetTempPath()) ("CodexPortableManager-regression-{0}" -f [Guid]::NewGuid().ToString("N"))
$binRoot = Join-Path $runRoot "bin"
$suiteRoot = Join-Path $runRoot "suite"
$reportPath = Join-Path $runRoot "regression-report.txt"
$isolatedTestRunnerPath = Join-Path $binRoot "CodexPortableManager.Tests.exe"
$isolatedManagerPath = Join-Path $binRoot "CodexPortableManager.exe"
$isolatedPortableExitFixturePath = Join-Path $binRoot "CodexPortableManager.PortableExitFixture.exe"
New-Item -ItemType Directory -Force -Path $runRoot, $binRoot, $suiteRoot | Out-Null
Copy-Item -LiteralPath $testRunnerPath -Destination $isolatedTestRunnerPath
Copy-Item -LiteralPath $ManagerPath -Destination $isolatedManagerPath
Copy-Item -LiteralPath $portableExitFixturePath -Destination $isolatedPortableExitFixturePath

$exitCode = $null
try {
    $previousFilter = $env:CPM_REGRESSION_FILTER
    $previousLargeMsix = $env:CPM_RUN_LARGE_MSIX_TESTS
    if (-not [string]::IsNullOrWhiteSpace($TestFilter)) {
        $env:CPM_REGRESSION_FILTER = $TestFilter
    }
    if ($LargeMsix) {
        $env:CPM_RUN_LARGE_MSIX_TESTS = "1"
    }
    try {
        & $isolatedTestRunnerPath --regression-test $ManagerPath $suiteRoot $reportPath
        $exitCode = $LASTEXITCODE
    }
    finally {
        $env:CPM_REGRESSION_FILTER = $previousFilter
        $env:CPM_RUN_LARGE_MSIX_TESTS = $previousLargeMsix
    }

    Write-Host "回归报告：$reportPath"
    if ($exitCode -ne 0) {
        throw "隔离回归测试失败，退出码：$exitCode；报告：$reportPath"
    }
}
finally {
    if (-not $KeepArtifacts -and
        $null -ne $exitCode -and
        $exitCode -eq 0 -and
        (Test-Path -LiteralPath $runRoot)) {
        Remove-Item -LiteralPath $runRoot -Recurse -Force
    }
}
