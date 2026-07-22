[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSCommandPath
$sourceRoot = Join-Path $projectRoot "src"
$outputRoot = Join-Path $projectRoot "dist"
$releaseRoot = Join-Path $projectRoot "release"
. (Join-Path $projectRoot "scripts\BuildEnvironment.ps1")
$compiler = Get-CpmCompilerPath
$framework = Get-CpmFrameworkPath
$msbuild = Join-Path (Split-Path -Parent (Split-Path -Parent $compiler)) "MSBuild.exe"
if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
    $msbuild = Join-Path $framework "MSBuild.exe"
}
if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
    throw "未找到 MSBuild，无法编译 WPF XAML。"
}
$wpfTargets = Join-Path $framework "Microsoft.WinFX.targets"
if (-not (Test-Path -LiteralPath $wpfTargets -PathType Leaf)) {
    throw "未找到 WPF MarkupCompile targets：$wpfTargets"
}
$projectPath = Join-Path $projectRoot "CodexPortableManager.csproj"
$dependencyRoot = Join-Path $projectRoot ".packages\compatibility-parser"
$dependencyRestore = Join-Path $projectRoot "scripts\Restore-CompatibilityParser.ps1"
& $dependencyRestore -DestinationRoot $dependencyRoot
$dependencyReferences = @(
    "Esprima.dll",
    "System.Memory.dll",
    "System.Buffers.dll",
    "System.Numerics.Vectors.dll",
    "System.Runtime.CompilerServices.Unsafe.dll"
) | ForEach-Object { Join-Path $dependencyRoot $_ }
$optimizeArgument = if ($Configuration -eq "Release") { "/optimize+" } else { "/optimize-" }
$emitSymbols = $Configuration -eq "Debug"
$debugArgument = if ($emitSymbols) { "/debug:full" } else { "/debug-" }

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$developmentOutputRoot = Join-Path $outputRoot "app"
New-Item -ItemType Directory -Force -Path $developmentOutputRoot | Out-Null
$testOutputRoot = Join-Path $outputRoot "tests"
New-Item -ItemType Directory -Force -Path $testOutputRoot | Out-Null
$symbolOutputRoot = Join-Path $outputRoot "symbols"
if ($emitSymbols) {
    New-Item -ItemType Directory -Force -Path $symbolOutputRoot | Out-Null
}
$output = Join-Path $developmentOutputRoot "CodexPortableManager.exe"
$pdbOutput = Join-Path $symbolOutputRoot "CodexPortableManager.pdb"
$testOutput = Join-Path $testOutputRoot "CodexPortableManager.Tests.exe"
$portableExitFixtureOutput = Join-Path $testOutputRoot "CodexPortableManager.PortableExitFixture.exe"
$testPdbOutput = Join-Path $symbolOutputRoot "CodexPortableManager.Tests.pdb"
$temporaryRoot = Join-Path $outputRoot (".build-{0}" -f [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $temporaryRoot | Out-Null
$applicationOutputRoot = Join-Path $temporaryRoot "app"
$applicationIntermediateRoot = Join-Path $temporaryRoot "obj"
$temporaryOutput = Join-Path $applicationOutputRoot "CodexPortableManager.exe"
$temporaryPdb = [IO.Path]::ChangeExtension($temporaryOutput, ".pdb")
$temporaryTestOutput = Join-Path $temporaryRoot "CodexPortableManager.Tests.exe"
$temporaryPortableExitFixtureOutput = Join-Path $temporaryRoot "CodexPortableManager.PortableExitFixture.exe"
$temporaryTestPdb = [IO.Path]::ChangeExtension($temporaryTestOutput, ".pdb")
$portableExitFixtureSource = Join-Path $projectRoot "scripts\tests\fixtures\PortableExitFixture.cs"
$wpfReferences = @(Get-CpmWpfReferencePaths)
$windowsRuntimeReference = Get-CpmWindowsRuntimeReferencePath
$systemRuntimeFacadeReference = Get-CpmSystemRuntimeFacadePath
$windowsMetadataReference = Get-CpmWindowsMetadataPath
$presentationFrameworkPath = $wpfReferences[0]
$presentationCorePath = $wpfReferences[1]
$windowsBasePath = $wpfReferences[2]
$systemXamlPath = $wpfReferences[3]
$testSources = @(
    Join-Path $sourceRoot "RenderTestRunner.cs"
    Join-Path $projectRoot "scripts\tests\runners\MsixTrustTestRunner.cs"
    Get-ChildItem -LiteralPath (Join-Path $projectRoot "scripts\tests\runners") -Filter "*.cs" -File |
        Where-Object { $_.Name -ne "MsixTrustTestRunner.cs" } |
        Select-Object -ExpandProperty FullName
)
$references = @(
    "mscorlib.dll",
    "System.dll",
    "System.Core.dll",
    "System.Windows.Forms.dll",
    "System.Drawing.dll",
    "System.Net.Http.dll",
    "System.IO.Compression.dll",
    "System.IO.Compression.FileSystem.dll",
    "System.Security.dll",
    "System.Web.Extensions.dll",
    "System.Xml.dll",
    "System.Xml.Linq.dll",
    "Microsoft.CSharp.dll"
) | ForEach-Object { Join-Path $framework $_ }
$references += $windowsRuntimeReference
$references += $systemRuntimeFacadeReference
$references += $windowsMetadataReference

$portableExitFixtureArguments = @(
    "/nologo",
    "/noconfig",
    "/nostdlib+",
    "/target:exe",
    "/platform:x64",
    "/langversion:latest",
    $optimizeArgument,
    "/out:$temporaryPortableExitFixtureOutput",
    "/reference:$(Join-Path $framework 'mscorlib.dll')",
    "/reference:$(Join-Path $framework 'System.dll')",
    $portableExitFixtureSource
)
$testArguments = @(
    "/nologo",
    "/noconfig",
    "/nostdlib+",
    "/target:exe",
    "/platform:anycpu",
    "/langversion:latest",
    $optimizeArgument,
    $debugArgument,
    "/out:$temporaryTestOutput"
)
if ($emitSymbols) {
    $testArguments += "/pdb:$temporaryTestPdb"
}
$testArguments += $references | ForEach-Object { "/reference:$_" }
$testArguments += $wpfReferences | ForEach-Object { "/reference:$_" }
$testArguments += $dependencyReferences | ForEach-Object { "/reference:$_" }
$testArguments += "/reference:$temporaryOutput"
$testArguments += $testSources

try {
    & $compiler @portableExitFixtureArguments
    if ($LASTEXITCODE -ne 0) {
        throw "便携启动退出测试夹具编译失败，退出代码：$LASTEXITCODE"
    }
    & $msbuild $projectPath "/nologo" "/verbosity:minimal" "/target:Rebuild" "/property:Configuration=$Configuration" "/property:Platform=AnyCPU" "/property:OutputPath=$applicationOutputRoot\" "/property:BaseIntermediateOutputPath=$applicationIntermediateRoot\" "/property:IntermediateOutputPath=$applicationIntermediateRoot\$Configuration\" "/property:FrameworkPathOverride=$framework" "/property:WpfTargetsPath=$wpfTargets" "/property:PresentationFrameworkPath=$presentationFrameworkPath" "/property:PresentationCorePath=$presentationCorePath" "/property:WindowsBasePath=$windowsBasePath" "/property:SystemXamlPath=$systemXamlPath" "/property:SystemRuntimeWindowsRuntimePath=$windowsRuntimeReference" "/property:SystemRuntimeFacadePath=$systemRuntimeFacadeReference" "/property:WindowsMetadataPath=$windowsMetadataReference" "/property:CpmDependencyRoot=$dependencyRoot"
    if ($LASTEXITCODE -ne 0) {
        throw "WPF 项目编译失败，退出代码：$LASTEXITCODE"
    }
    & $compiler @testArguments
    if ($LASTEXITCODE -ne 0) {
        throw "测试程序编译失败，退出代码：$LASTEXITCODE"
    }

    if (-not (Test-Path -LiteralPath $temporaryOutput)) {
        throw "编译器未生成预期文件：$temporaryOutput"
    }

    Move-Item -LiteralPath $temporaryOutput -Destination $output -Force
    Move-Item -LiteralPath $temporaryTestOutput -Destination $testOutput -Force
    Move-Item -LiteralPath $temporaryPortableExitFixtureOutput -Destination $portableExitFixtureOutput -Force
    Copy-Item -LiteralPath $output -Destination (Join-Path $testOutputRoot "CodexPortableManager.exe") -Force
    if ($emitSymbols) {
        Move-Item -LiteralPath $temporaryPdb -Destination $pdbOutput -Force
        Move-Item -LiteralPath $temporaryTestPdb -Destination $testPdbOutput -Force
    }
    else {
        foreach ($staleSymbol in @($pdbOutput, $testPdbOutput)) {
            if (Test-Path -LiteralPath $staleSymbol -PathType Leaf) {
                Remove-Item -LiteralPath $staleSymbol -Force
            }
        }
    }

    if ($Configuration -eq "Release") {
        $expectedReleaseRoot = [IO.Path]::Combine([IO.Path]::GetFullPath($projectRoot), "release")
        $resolvedReleaseRoot = [IO.Path]::GetFullPath($releaseRoot)
        if (-not [string]::Equals($resolvedReleaseRoot, $expectedReleaseRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Release 输出目录越出预期位置：$resolvedReleaseRoot"
        }
        New-Item -ItemType Directory -Force -Path $resolvedReleaseRoot | Out-Null
        Copy-Item -LiteralPath $output -Destination (Join-Path $resolvedReleaseRoot "CodexPortableManager.exe") -Force
    }
}
catch {
    throw "构建或替换正式程序失败。请确认 Codex Portable Manager 已关闭。详细信息：$($_.Exception.Message)；位置：$($_.InvocationInfo.ScriptLineNumber)"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Host "构建完成（$Configuration）：$output"
Write-Host "测试程序：$testOutput"
Write-Host "便携启动退出测试夹具：$portableExitFixtureOutput"
if ($Configuration -eq "Release") {
    Write-Host "发布程序：$(Join-Path $releaseRoot 'CodexPortableManager.exe')（保留已有 release\data 运行数据）"
}
