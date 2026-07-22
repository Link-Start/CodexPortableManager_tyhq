[CmdletBinding()]
param(
    [string]$ManagerPath
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($ManagerPath)) {
    $ManagerPath = Join-Path $projectRoot "dist\app\CodexPortableManager.exe"
}
$ManagerPath = [IO.Path]::GetFullPath($ManagerPath)
if (-not (Test-Path -LiteralPath $ManagerPath -PathType Leaf)) {
    throw "未找到待测试程序：$ManagerPath"
}
. (Join-Path $projectRoot "scripts\BuildEnvironment.ps1")
$compiler = Get-CpmCompilerPath
$framework = Get-CpmFrameworkPath
$wpfReferences = @(Get-CpmWpfReferencePaths)
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ("CodexPortableManager-path-refresh-{0}" -f [Guid]::NewGuid().ToString("N"))
$harnessPath = Join-Path $runRoot "PathAutoRefreshHarness.exe"
$reportPath = Join-Path $runRoot "path-auto-refresh-report.txt"
$testRoot = Join-Path $runRoot "suite"
New-Item -ItemType Directory -Force -Path $runRoot, $testRoot | Out-Null

try {
    $arguments = @(
        "/nologo",
        "/noconfig",
        "/nostdlib+",
        "/target:winexe",
        "/platform:anycpu",
        "/langversion:latest",
        "/optimize+",
        "/out:$harnessPath",
        "/reference:$(Join-Path $framework 'mscorlib.dll')",
        "/reference:$(Join-Path $framework 'System.dll')",
        "/reference:$(Join-Path $framework 'System.Core.dll')",
        (Join-Path $PSScriptRoot "PathAutoRefreshHarness.cs")
    )
    $arguments += $wpfReferences | ForEach-Object { "/reference:$_" }
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "路径自动检测 harness 编译失败，退出码：$LASTEXITCODE"
    }

    $process = Start-Process -FilePath $harnessPath -ArgumentList @(
        ('"' + $ManagerPath + '"'),
        ('"' + $testRoot + '"'),
        ('"' + $reportPath + '"')
    ) -Wait -PassThru -WindowStyle Hidden
    if (Test-Path -LiteralPath $reportPath) {
        Get-Content -LiteralPath $reportPath
    }
    if ($process.ExitCode -ne 0) {
        throw "路径自动检测测试失败，退出码：$($process.ExitCode)"
    }
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $fullRunRoot = [IO.Path]::GetFullPath($runRoot).TrimEnd('\') + '\'
    if ((Test-Path -LiteralPath $runRoot) -and
        $fullRunRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $runRoot) -like "CodexPortableManager-path-refresh-*") {
        Remove-Item -LiteralPath $runRoot -Recurse -Force
    }
}
