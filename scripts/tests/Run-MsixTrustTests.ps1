[CmdletBinding()]
param(
    [string]$PackagePath
)

$ErrorActionPreference = "Stop"

function Remove-TestJunction {
    param(
        [string]$Path,
        [string]$TemporaryRoot,
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $resolvedJunction = [IO.Path]::GetFullPath($Path)
    $temporaryPrefix = [IO.Path]::GetFullPath($TemporaryRoot).TrimEnd('\') + '\'
    if (-not $resolvedJunction.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理测试临时目录之外的$Description：$resolvedJunction"
    }
    [IO.Directory]::Delete($resolvedJunction, $false)
}

function Get-PackageMetadataFromMsix {
    param([string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $fullPath = [IO.Path]::GetFullPath($Path)
    $archive = [IO.Compression.ZipFile]::OpenRead($fullPath)
    try {
        $manifestEntries = @($archive.Entries | Where-Object {
            [string]::Equals($_.FullName, "AppxManifest.xml", [StringComparison]::OrdinalIgnoreCase)
        })
        if ($manifestEntries.Count -ne 1) {
            throw "MSIX 必须包含且只包含一个 AppxManifest.xml：$fullPath"
        }

        $stream = $manifestEntries[0].Open()
        try {
            $settings = New-Object Xml.XmlReaderSettings
            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $reader = [Xml.XmlReader]::Create($stream, $settings)
            try {
                $document = New-Object Xml.XmlDocument
                $document.XmlResolver = $null
                $document.Load($reader)
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    $identity = $document.DocumentElement.SelectSingleNode("*[local-name()='Identity']")
    if ($null -eq $identity) {
        throw "AppxManifest.xml 缺少 Identity：$fullPath"
    }
    $name = [string]$identity.GetAttribute("Name")
    $versionText = [string]$identity.GetAttribute("Version")
    $architecture = ([string]$identity.GetAttribute("ProcessorArchitecture")).ToLowerInvariant()
    $publisher = [string]$identity.GetAttribute("Publisher")
    $version = $null
    if (-not [string]::Equals($name, "OpenAI.Codex", [StringComparison]::Ordinal) -or
        -not [Version]::TryParse($versionText, [ref]$version) -or
        $version.ToString(4) -ne $versionText -or
        $architecture -notin @("x64", "arm64") -or
        -not [string]::Equals($publisher, "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B", [StringComparison]::Ordinal)) {
        throw "MSIX Manifest 不是受支持的 OpenAI.Codex 主包：$fullPath"
    }

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $packageStream = [IO.File]::OpenRead($fullPath)
        try {
            $digest = [Convert]::ToBase64String($sha256.ComputeHash($packageStream))
        }
        finally {
            $packageStream.Dispose()
        }
    }
    finally {
        $sha256.Dispose()
    }

    $info = Get-Item -LiteralPath $fullPath
    [pscustomobject]@{
        Version = $versionText
        FullName = "OpenAI.Codex_${versionText}_${architecture}__2p2nqsd0c76g0"
        Digest = $digest
        SizeInBytes = [int64]$info.Length
        Architecture = $architecture
    }
}

$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$testExecutable = Join-Path $projectRoot "dist\tests\CodexPortableManager.Tests.exe"
if (-not (Test-Path -LiteralPath $testExecutable -PathType Leaf)) {
    throw "未找到正式测试程序，请先运行 build.ps1：$testExecutable"
}

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $currentArchitecture = if (("$env:PROCESSOR_ARCHITEW6432$env:PROCESSOR_ARCHITECTURE") -match "ARM64") {
        "arm64"
    } else {
        "x64"
    }
    $cacheRoots = @(
        (Join-Path $projectRoot "dist\app\data\cache"),
        (Join-Path $projectRoot "dist\data\cache"),
        (Join-Path $projectRoot "data\cache")
    )
    $PackagePath = $cacheRoots |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter "*.msix" -File -ErrorAction SilentlyContinue } |
        Where-Object { $_.Name -match "_$currentArchitecture\.msix$" } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($PackagePath)) {
        throw "缓存中没有可复用的 $currentArchitecture Codex MSIX；请先运行管理器下载，或用 -PackagePath 指定任意受支持版本。"
    }
}
if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "没有找到用于测试的真实 MSIX：$PackagePath"
}
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$metadata = Get-PackageMetadataFromMsix $PackagePath

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("CodexPortableManager-msix-trust-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$packageJunction = Join-Path $temporaryRoot "package-cache-link"
$heldPackageJunction = Join-Path $temporaryRoot "package-cache-link-held"
$swappedPackageDirectory = Join-Path $temporaryRoot "swapped-cache"
$readyPath = Join-Path $temporaryRoot "junction-ready.txt"
$continuePath = Join-Path $temporaryRoot "junction-continue.txt"
$standardOutputPath = Join-Path $temporaryRoot "harness-stdout.txt"
$standardErrorPath = Join-Path $temporaryRoot "harness-stderr.txt"
$packageDirectory = Split-Path -Parent $PackagePath
$packageLeafName = Split-Path -Leaf $PackagePath
New-Item -ItemType Junction -Path $packageJunction -Target $packageDirectory | Out-Null
New-Item -ItemType Directory -Path $swappedPackageDirectory | Out-Null
[IO.File]::WriteAllText(
    (Join-Path $swappedPackageDirectory $packageLeafName),
    "UNTRUSTED_SWAP_TARGET",
    [Text.Encoding]::UTF8)
$junctionPackagePath = Join-Path $packageJunction $packageLeafName
$harnessProcess = $null

try {
    $harnessArguments = @(
        "--msix-trust-test",
        ('"' + $junctionPackagePath + '"'),
        $metadata.Version,
        $metadata.FullName,
        $metadata.Digest,
        $metadata.SizeInBytes.ToString([Globalization.CultureInfo]::InvariantCulture),
        $metadata.Architecture,
        ('"' + $readyPath + '"'),
        ('"' + $continuePath + '"')
    )
    $harnessProcess = Start-Process `
        -FilePath $testExecutable `
        -ArgumentList $harnessArguments `
        -PassThru `
        -WindowStyle Hidden `
        -RedirectStandardOutput $standardOutputPath `
        -RedirectStandardError $standardErrorPath

    # 大型官方包的首次摘要、签名与 Defender 扫描可能超过三分钟。
    $readyDeadline = [DateTime]::UtcNow.AddMinutes(10)
    while (-not (Test-Path -LiteralPath $readyPath) -and -not $harnessProcess.HasExited) {
        if ([DateTime]::UtcNow -ge $readyDeadline) {
            throw "等待 MSIX junction 换向测试进入持锁状态超时。"
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $readyPath)) {
        $harnessProcess.WaitForExit()
        throw "MSIX 可信测试在 junction 换向前提前退出：$($harnessProcess.ExitCode)"
    }

    [IO.Directory]::Move($packageJunction, $heldPackageJunction)
    New-Item -ItemType Junction -Path $packageJunction -Target $swappedPackageDirectory | Out-Null
    [IO.File]::WriteAllText($continuePath, "continue", [Text.Encoding]::UTF8)
    if (-not $harnessProcess.WaitForExit(600000)) {
        try { $harnessProcess.Kill() } catch { }
        throw "MSIX 可信测试在 junction 换向后没有按时退出。"
    }
    $harnessProcess.WaitForExit()
    $harnessProcess.Refresh()
    $harnessOutput = @(Get-Content -LiteralPath $standardOutputPath -Encoding UTF8)
    $harnessOutput
    if (Test-Path -LiteralPath $standardErrorPath) {
        $standardError = Get-Content -LiteralPath $standardErrorPath -Raw -Encoding UTF8
        if (-not [string]::IsNullOrWhiteSpace($standardError)) {
            Write-Error $standardError -ErrorAction Continue
        }
    }
    $harnessExitCode = $harnessProcess.ExitCode
    if ($null -eq $harnessExitCode) {
        if (-not ($harnessOutput -contains "RESULT=PASS")) {
            throw "MSIX 可信测试退出码不可用，且测试程序没有报告 RESULT=PASS。"
        }
    }
    elseif ($harnessExitCode -ne 0) {
        throw "MSIX 可信测试失败：$harnessExitCode"
    }
}
finally {
    if ($null -ne $harnessProcess -and -not $harnessProcess.HasExited) {
        try { $harnessProcess.Kill() } catch { }
        try { $harnessProcess.WaitForExit() } catch { }
    }
    Remove-TestJunction $packageJunction $temporaryRoot "junction"
    Remove-TestJunction $heldPackageJunction $temporaryRoot "备用 junction"
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
        $temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedTemporaryRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not ([IO.Path]::GetFileName($resolvedTemporaryRoot)).StartsWith("CodexPortableManager-msix-trust-", [StringComparison]::Ordinal)) {
            throw "拒绝清理预期临时目录之外的路径：$resolvedTemporaryRoot"
        }
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
