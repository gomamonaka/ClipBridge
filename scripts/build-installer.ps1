[CmdletBinding()]
param(
    [Alias('IsccPath')]
    [string]$CompilerPath,
    [Alias('PublishDirectory')]
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '..\artifacts\win-x64'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\release')
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectFile = Join-Path $projectRoot 'src\ClipBridge.App\ClipBridge.App.csproj'
$installerScript = Join-Path $projectRoot 'installer\ClipBridge.iss'

if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    throw 'The Windows Inno Setup compiler is required. Run this script on Windows.'
}

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $compilerCommand = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($null -ne $compilerCommand) {
        $CompilerPath = $compilerCommand.Source
    }
    else {
        $compilerCandidates = @()
        if (${env:ProgramFiles(x86)}) { $compilerCandidates += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
        if ($env:ProgramFiles) { $compilerCandidates += Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe' }
        $CompilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if ([string]::IsNullOrWhiteSpace($CompilerPath) -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'ISCC.exe was not found. Pass -CompilerPath (or -IsccPath) to the official Inno Setup compiler.'
}
$resolvedCompiler = (Resolve-Path -LiteralPath $CompilerPath).ProviderPath

if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
    throw 'The published application directory is missing. Run scripts\build.ps1 first, or pass -SourceDirectory.'
}
$publishedRoot = (Resolve-Path -LiteralPath $SourceDirectory).ProviderPath
$installerOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)

[xml]$projectXml = Get-Content -LiteralPath $projectFile -Raw
$versionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $versionNode) { throw 'The application project does not define Version.' }
$appVersion = $versionNode.InnerText.Trim()
if ($appVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
    throw 'Version must contain three or four numeric components for the Windows installer.'
}

# Keep this list and the [Files] entries in ClipBridge.iss explicit. Do not package
# arbitrary publish-directory contents, local settings, backups, or OneDrive files.
$requiredPublishFiles = @(
    'ClipBridge.exe',
    '使い方.md',
    'TESTING.md',
    'Notices\NET-Runtime-LICENSE.txt',
    'Notices\NET-Third-Party-Notices.txt',
    'Notices\WindowsDesktop-LICENSE.txt'
)
foreach ($relativeFile in $requiredPublishFiles) {
    $requiredPath = Join-Path $publishedRoot $relativeFile
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf) -or (Get-Item -LiteralPath $requiredPath).Length -eq 0) {
        throw "A required distribution file is missing or empty: $relativeFile"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'LICENSE.txt') -PathType Leaf)) {
    throw 'LICENSE.txt is missing from the project root.'
}
if (-not (Test-Path -LiteralPath $installerScript -PathType Leaf)) {
    throw 'installer\ClipBridge.iss is missing.'
}

$publishedExe = Join-Path $publishedRoot 'ClipBridge.exe'
$publishedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExe).ProductVersion
$publishedVersionCore = if ([string]::IsNullOrWhiteSpace($publishedVersion)) { '' } else { $publishedVersion.Split('+')[0] }
if ($publishedVersionCore -ne $appVersion -and $publishedVersionCore -ne "$appVersion.0") {
    throw "The published executable does not match project version $appVersion. Publish the current application before building the installer."
}

New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null
$compilerArguments = @(
    '/Qp',
    "/DAppVersion=$appVersion",
    "/DPublishDir=$publishedRoot",
    "/DProjectRoot=$projectRoot",
    "/DInstallerOutputDir=$installerOutput",
    $installerScript
)
& $resolvedCompiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compiler failed with exit code $LASTEXITCODE." }

$installerPath = Join-Path $installerOutput "ClipBridge-$appVersion-Setup.exe"
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf) -or (Get-Item -LiteralPath $installerPath).Length -eq 0) {
    throw 'The compiler finished without producing the expected installer.'
}
Write-Host "Installer built: $installerPath"
Write-Output $installerPath
