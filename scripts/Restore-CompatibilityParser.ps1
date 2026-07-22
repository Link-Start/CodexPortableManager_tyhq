[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DestinationRoot
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.IO.Compression
$DestinationRoot = [IO.Path]::GetFullPath($DestinationRoot)
$packageCache = Join-Path $DestinationRoot "packages"
New-Item -ItemType Directory -Force -Path $DestinationRoot, $packageCache | Out-Null

$packages = @(
    @{
        Id = "esprima"; Version = "3.0.6";
        PackageSha256 = "3d4ac8575bce23d97bede8b2737eab4c3b05af85701164b99ca9d16a975505f6";
        Entry = "lib/net462/Esprima.dll"; File = "Esprima.dll";
        FileSha256 = "eb1d27fdf2f22394211c2120ddd9fb025f2928c62b3bf32d2da3654e8597cd1f"
    },
    @{
        Id = "system.memory"; Version = "4.5.5";
        PackageSha256 = "10f43da352a29fb2b3188e4edd4dcf5100194c8b526e4f61fe2e2b5623775a22";
        Entry = "lib/net461/System.Memory.dll"; File = "System.Memory.dll";
        FileSha256 = "bf3fb84664f4097f1a8a9bc71a51dcf8cf1a905d4080a4d290da1730866e856f"
    },
    @{
        Id = "system.buffers"; Version = "4.5.1";
        PackageSha256 = "c30b3dd2c7e2f4cee4b823d692fd42118309b42ab1f5007f923d329a5b0d6b12";
        Entry = "lib/net461/System.Buffers.dll"; File = "System.Buffers.dll";
        FileSha256 = "accccfbe45d9f08ffeed9916e37b33e98c65be012cfff6e7fa7b67210ce1fefb"
    },
    @{
        Id = "system.numerics.vectors"; Version = "4.5.0";
        PackageSha256 = "a9d49320581fda1b4f4be6212c68c01a22cdf228026099c20a8eabefcf90f9cf";
        Entry = "lib/net46/System.Numerics.Vectors.dll"; File = "System.Numerics.Vectors.dll";
        FileSha256 = "1d3ef8698281e7cf7371d1554afef5872b39f96c26da772210a33da041ba1183"
    },
    @{
        Id = "system.runtime.compilerservices.unsafe"; Version = "4.5.3";
        PackageSha256 = "96764c52a44ee1161151e48ef07489f72047a851cb55b99e9f01d6908536d1a9";
        Entry = "lib/net461/System.Runtime.CompilerServices.Unsafe.dll";
        File = "System.Runtime.CompilerServices.Unsafe.dll";
        FileSha256 = "66409f670315afe8610f17a4d3a1ee52d72b6a46c544cec97544e8385f90ad74"
    }
)

function Get-Sha256([string]$Path)
{
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-Hash([string]$Path, [string]$Expected, [string]$Description)
{
    $actual = Get-Sha256 $Path
    if (-not [string]::Equals($actual, $Expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description 摘要不匹配：$Path；期望=$Expected；实际=$actual"
    }
}

foreach ($package in $packages) {
    $packageName = "$($package.Id).$($package.Version).nupkg"
    $packagePath = Join-Path $packageCache $packageName
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        $temporaryPath = $packagePath + ".tmp-" + [Guid]::NewGuid().ToString("N")
        try {
            $uri = "https://api.nuget.org/v3-flatcontainer/$($package.Id)/$($package.Version)/$packageName"
            $client = [Net.Http.HttpClient]::new()
            try {
                $bytes = $client.GetByteArrayAsync($uri).GetAwaiter().GetResult()
                [IO.File]::WriteAllBytes($temporaryPath, $bytes)
            }
            finally {
                $client.Dispose()
            }
            Assert-Hash $temporaryPath $package.PackageSha256 "NuGet 包"
            Move-Item -LiteralPath $temporaryPath -Destination $packagePath
        }
        finally {
            if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
                Remove-Item -LiteralPath $temporaryPath -Force
            }
        }
    }
    Assert-Hash $packagePath $package.PackageSha256 "NuGet 包"

    $targetPath = Join-Path $DestinationRoot $package.File
    if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf) -or
        -not [string]::Equals(
            (Get-Sha256 $targetPath),
            $package.FileSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        $temporaryTarget = $targetPath + ".tmp-" + [Guid]::NewGuid().ToString("N")
        try {
            $stream = [IO.File]::OpenRead($packagePath)
            try {
                $archive = [IO.Compression.ZipArchive]::new(
                    $stream,
                    [IO.Compression.ZipArchiveMode]::Read,
                    $false)
                try {
                    $entry = $archive.GetEntry($package.Entry)
                    if ($null -eq $entry) {
                        throw "NuGet 包缺少预期程序集：$($package.Entry)"
                    }
                    $input = $entry.Open()
                    try {
                        $output = [IO.File]::Open(
                            $temporaryTarget,
                            [IO.FileMode]::CreateNew,
                            [IO.FileAccess]::Write,
                            [IO.FileShare]::None)
                        try { $input.CopyTo($output) }
                        finally { $output.Dispose() }
                    }
                    finally { $input.Dispose() }
                }
                finally { $archive.Dispose() }
            }
            finally { $stream.Dispose() }
            Assert-Hash $temporaryTarget $package.FileSha256 "解析器程序集"
            Move-Item -LiteralPath $temporaryTarget -Destination $targetPath -Force
        }
        finally {
            if (Test-Path -LiteralPath $temporaryTarget -PathType Leaf) {
                Remove-Item -LiteralPath $temporaryTarget -Force
            }
        }
    }
    Assert-Hash $targetPath $package.FileSha256 "解析器程序集"
}
