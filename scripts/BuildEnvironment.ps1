function Get-CpmCompilerPath
{
    if (-not [string]::IsNullOrWhiteSpace($env:CPM_CSC_PATH)) {
        if (-not (Test-Path -LiteralPath $env:CPM_CSC_PATH -PathType Leaf)) {
            throw "CPM_CSC_PATH 指向的 Roslyn C# 编译器不存在：$env:CPM_CSC_PATH"
        }
        return [IO.Path]::GetFullPath($env:CPM_CSC_PATH)
    }

    $pathCompiler = Get-Command "csc.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $pathCompiler -and (Test-Path -LiteralPath $pathCompiler.Source -PathType Leaf)) {
        return [IO.Path]::GetFullPath($pathCompiler.Source)
    }

    $searchPatterns = @()
    foreach ($programFilesRoot in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not [string]::IsNullOrWhiteSpace($programFilesRoot)) {
            $searchPatterns += Join-Path $programFilesRoot "Microsoft Visual Studio\*\*\MSBuild\*\Bin\Roslyn\csc.exe"
        }
    }
    foreach ($drive in Get-PSDrive -PSProvider FileSystem) {
        $searchPatterns += Join-Path $drive.Root "ide\*\MSBuild\*\Bin\Roslyn\csc.exe"
    }

    $discoveredCompiler = $searchPatterns |
        ForEach-Object { Get-ChildItem -Path $_ -File -ErrorAction SilentlyContinue } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -ne $discoveredCompiler) {
        return $discoveredCompiler.FullName
    }

    throw "未找到 Roslyn C# 编译器。请安装 Visual Studio Build Tools，或通过 CPM_CSC_PATH 指定 csc.exe。"
}

function Get-CpmFrameworkPath
{
    if (-not [string]::IsNullOrWhiteSpace($env:CPM_FRAMEWORK_PATH)) {
        if (-not (Test-Path -LiteralPath $env:CPM_FRAMEWORK_PATH -PathType Container)) {
            throw "CPM_FRAMEWORK_PATH 指向的 .NET Framework 目录不存在：$env:CPM_FRAMEWORK_PATH"
        }
        $configuredFramework = [IO.Path]::GetFullPath($env:CPM_FRAMEWORK_PATH)
        if (-not (Test-Path -LiteralPath (Join-Path $configuredFramework "mscorlib.dll") -PathType Leaf)) {
            throw "CPM_FRAMEWORK_PATH 中缺少 mscorlib.dll：$configuredFramework"
        }
        return $configuredFramework
    }

    if ([string]::IsNullOrWhiteSpace($env:WINDIR)) {
        throw "WINDIR 未设置，无法定位 .NET Framework。请通过 CPM_FRAMEWORK_PATH 指定 Framework 目录。"
    }

    foreach ($relativePath in @(
        "Microsoft.NET\Framework64\v4.0.30319",
        "Microsoft.NET\Framework\v4.0.30319"
    )) {
        $candidate = Join-Path $env:WINDIR $relativePath
        if (Test-Path -LiteralPath (Join-Path $candidate "mscorlib.dll") -PathType Leaf) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }

    throw "未找到 .NET Framework 4.x 引用程序集。请通过 CPM_FRAMEWORK_PATH 指定 Framework 目录。"
}

function Get-CpmWpfReferencePaths
{
    if ([string]::IsNullOrWhiteSpace($env:WINDIR)) {
        throw "WINDIR 未设置，无法定位 WPF 引用程序集。"
    }

    $assemblies = @(
        @{ Gac = "GAC_MSIL"; Name = "PresentationFramework"; Token = "31bf3856ad364e35" },
        @{ Gac = "GAC_64"; Name = "PresentationCore"; Token = "31bf3856ad364e35" },
        @{ Gac = "GAC_MSIL"; Name = "WindowsBase"; Token = "31bf3856ad364e35" },
        @{ Gac = "GAC_MSIL"; Name = "System.Xaml"; Token = "b77a5c561934e089" }
    )

    foreach ($assembly in $assemblies) {
        $pattern = Join-Path $env:WINDIR (
            "Microsoft.NET\assembly\{0}\{1}\v4.0_*__{2}\{1}.dll" -f
            $assembly.Gac,
            $assembly.Name,
            $assembly.Token)
        $match = Get-ChildItem -Path $pattern -File -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -eq $match) {
            throw "未找到 WPF 引用程序集：$($assembly.Name).dll"
        }
        $match.FullName
    }
}

function Get-CpmWindowsRuntimeReferencePath
{
    if (-not [string]::IsNullOrWhiteSpace($env:CPM_WINDOWS_RUNTIME_PATH)) {
        if (-not (Test-Path -LiteralPath $env:CPM_WINDOWS_RUNTIME_PATH -PathType Leaf)) {
            throw "CPM_WINDOWS_RUNTIME_PATH 指向的 System.Runtime.WindowsRuntime.dll 不存在：$env:CPM_WINDOWS_RUNTIME_PATH"
        }
        return [IO.Path]::GetFullPath($env:CPM_WINDOWS_RUNTIME_PATH)
    }

    $searchPatterns = @(
        (Join-Path $env:WINDIR "Microsoft.NET\assembly\GAC_MSIL\System.Runtime.WindowsRuntime\v4.0_*\System.Runtime.WindowsRuntime.dll"),
        (Join-Path ${env:ProgramFiles(x86)} "Reference Assemblies\Microsoft\Framework\.NETFramework\v*\System.Runtime.WindowsRuntime.dll")
    )
    $match = $searchPatterns |
        ForEach-Object { Get-ChildItem -Path $_ -File -ErrorAction SilentlyContinue } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $match) {
        throw "未找到 System.Runtime.WindowsRuntime.dll，无法编译原生 Windows 包管理功能。"
    }
    return $match.FullName
}

function Get-CpmSystemRuntimeFacadePath
{
    if (-not [string]::IsNullOrWhiteSpace($env:CPM_SYSTEM_RUNTIME_PATH)) {
        if (-not (Test-Path -LiteralPath $env:CPM_SYSTEM_RUNTIME_PATH -PathType Leaf)) {
            throw "CPM_SYSTEM_RUNTIME_PATH 指向的 System.Runtime.dll 不存在：$env:CPM_SYSTEM_RUNTIME_PATH"
        }
        return [IO.Path]::GetFullPath($env:CPM_SYSTEM_RUNTIME_PATH)
    }

    $searchPatterns = @(
        (Join-Path $env:WINDIR "Microsoft.NET\assembly\GAC_MSIL\System.Runtime\v4.0_*\System.Runtime.dll"),
        (Join-Path ${env:ProgramFiles(x86)} "Reference Assemblies\Microsoft\Framework\.NETFramework\v*\Facades\System.Runtime.dll")
    )
    $match = $searchPatterns |
        ForEach-Object { Get-ChildItem -Path $_ -File -ErrorAction SilentlyContinue } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $match) {
        throw "未找到 System.Runtime.dll facade，无法编译 WinRT 类型投影。"
    }
    return $match.FullName
}

function Get-CpmWindowsMetadataPath
{
    if (-not [string]::IsNullOrWhiteSpace($env:CPM_WINDOWS_METADATA_PATH)) {
        if (-not (Test-Path -LiteralPath $env:CPM_WINDOWS_METADATA_PATH -PathType Leaf)) {
            throw "CPM_WINDOWS_METADATA_PATH 指向的 Windows.winmd 不存在：$env:CPM_WINDOWS_METADATA_PATH"
        }
        return [IO.Path]::GetFullPath($env:CPM_WINDOWS_METADATA_PATH)
    }

    $unionMetadataRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\UnionMetadata"
    if (Test-Path -LiteralPath $unionMetadataRoot -PathType Container) {
        $matches = Get-ChildItem -LiteralPath $unionMetadataRoot -Directory -ErrorAction SilentlyContinue |
            ForEach-Object {
                $version = $null
                if ([Version]::TryParse($_.Name, [ref]$version)) {
                    $metadataPath = Join-Path $_.FullName "Windows.winmd"
                    if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
                        [PSCustomObject]@{ Version = $version; Path = $metadataPath }
                    }
                }
            } |
            Sort-Object Version -Descending
        if ($null -ne $matches -and $matches.Count -gt 0) {
            return [IO.Path]::GetFullPath($matches[0].Path)
        }
    }

    $windows81Metadata = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\8.1\References\CommonConfiguration\Neutral\Windows.winmd"
    if (Test-Path -LiteralPath $windows81Metadata -PathType Leaf) {
        return [IO.Path]::GetFullPath($windows81Metadata)
    }

    throw "未找到 Windows.winmd。请安装 Windows 10/11 SDK，或通过 CPM_WINDOWS_METADATA_PATH 指定。"
}
