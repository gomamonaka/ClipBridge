<#
.SYNOPSIS
Builds and creates one versioned release in the private ClipBridge repository.
.EXAMPLE
.\scripts\release.ps1 -Tag v1.4.0 -NotesFile .\artifacts\release-notes.md -CompilerPath 'C:\Tools\Inno Setup 6\ISCC.exe'
.EXAMPLE
.\scripts\release.ps1 -Tag v1.4.0 -NotesFile .\artifacts\release-notes.md -UseExistingArtifacts
.NOTES
Commit the version update, create its tag and push that tag before running.
UseExistingArtifacts reuses only the versioned Setup.exe and portable ZIP in
ArtifactsDirectory. It still validates versions and regenerates their hashes.
The script never pushes tags, edits releases, or replaces uploaded assets.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Tag,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = 'gomamonaka/ClipBridge',
    [Parameter(Mandatory)]
    [string]$NotesFile,
    [string]$ArtifactsDirectory = (Join-Path $PSScriptRoot '..\artifacts\release'),
    [string]$CompilerPath,
    [switch]$UseExistingArtifacts,
    [switch]$Draft,
    [switch]$GenerateNotes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseDirectory = [IO.Path]::GetFullPath($ArtifactsDirectory)
if (-not (Test-Path -LiteralPath $NotesFile -PathType Leaf)) { throw 'Release notes file does not exist.' }
$resolvedNotes = (Resolve-Path -LiteralPath $NotesFile).Path
if ((Get-Item -LiteralPath $resolvedNotes).Length -gt 1MB -or [string]::IsNullOrWhiteSpace([IO.File]::ReadAllText($resolvedNotes))) {
    throw 'Release notes must be nonempty and no larger than 1 MiB.'
}

function Invoke-CheckedNative {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}

function Assert-ProductVersion {
    param([string]$Path, [string]$ExpectedVersion)
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion
    if ([string]::IsNullOrWhiteSpace($productVersion)) {
        throw "Artifact has no product version: $([IO.Path]::GetFileName($Path))."
    }
    # The .NET SDK appends +<commit> informational metadata inside a Git checkout.
    # Inno Setup pads its resource string with spaces; they are not part of the version.
    $numericVersion = ($productVersion.Trim() -split '\+', 2)[0].Trim()
    if ([string]::IsNullOrWhiteSpace($numericVersion) -or $numericVersion -notin @($ExpectedVersion, ($ExpectedVersion + '.0'))) {
        throw "Artifact product version does not match $ExpectedVersion`: $([IO.Path]::GetFileName($Path))."
    }
}

function Assert-PortableVersion {
    param([string]$Path, [string]$ExpectedVersion)
    if ((Get-Item -LiteralPath $Path).Length -gt 250MB) { throw 'The portable ZIP is larger than 250 MiB.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    $temporaryExe = Join-Path ([IO.Path]::GetTempPath()) ('ClipBridge-release-version-' + [Guid]::NewGuid().ToString('N') + '.exe')
    try {
        # Reused packages must contain the same public distribution files as a
        # clean publish. In particular, never upload settings, caches or backups.
        $allowedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        @('ClipBridge.exe', '使い方.md', 'TESTING.md', 'LICENSE.txt',
            'Notices/NET-Runtime-LICENSE.txt', 'Notices/NET-Third-Party-Notices.txt',
            'Notices/WindowsDesktop-LICENSE.txt') | ForEach-Object { [void]$allowedFiles.Add($_) }
        $seenEntries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $seenFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$totalBytes = 0
        $wrapper = $null
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if ([string]::IsNullOrWhiteSpace($name) -or $name.Contains('\') -or $name.StartsWith('/') -or
                $name.Contains(':') -or $name.Contains('//') -or $name -match '(^|/)\.{1,2}(/|$)' -or
                -not $seenEntries.Add($name)) {
                throw 'The portable ZIP contains an unsafe or duplicate path.'
            }
            $wrapped = $name.StartsWith('ClipBridge/', [StringComparison]::OrdinalIgnoreCase)
            $relativeName = if ($wrapped) { $name.Substring('ClipBridge/'.Length) } else { $name }
            $isDirectory = $name.EndsWith('/')
            if ($isDirectory) {
                if ($relativeName -notin @('', 'Notices/') -or $entry.Length -ne 0) {
                    throw 'The portable ZIP contains an unexpected directory.'
                }
            }
            else {
                if ($null -eq $wrapper) { $wrapper = $wrapped }
                if ($wrapper -ne $wrapped -or -not $allowedFiles.Contains($relativeName) -or -not $seenFiles.Add($relativeName)) {
                    throw 'The portable ZIP must contain only the seven public publish files under one consistent root.'
                }
                if ($entry.Length -le 0 -or $entry.Length -gt 200MB) { throw 'A portable ZIP file has an invalid or oversized entry.' }
                $totalBytes += $entry.Length
                if ($totalBytes -gt 250MB) { throw 'The portable ZIP expands to more than 250 MiB.' }
            }
        }
        if (-not $seenFiles.SetEquals($allowedFiles)) { throw 'The portable ZIP is missing a required publish file.' }
        # Directory records, when present, must use the same root as the files.
        foreach ($name in $seenEntries) {
            if ($name.EndsWith('/') -and $name.StartsWith('ClipBridge/', [StringComparison]::OrdinalIgnoreCase) -ne $wrapper) {
                throw 'The portable ZIP mixes wrapped and unwrapped paths.'
            }
        }
        $programs = @($archive.Entries | Where-Object { $_.FullName -match '(^|/)ClipBridge\.exe$' })
        if ($programs.Count -ne 1 -or $programs[0].Length -le 0 -or $programs[0].Length -gt 200MB) {
            throw 'The portable ZIP must contain exactly one bounded ClipBridge.exe.'
        }
        $sourceStream = $programs[0].Open()
        try {
            $targetStream = [IO.File]::Open($temporaryExe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $sourceStream.CopyTo($targetStream) } finally { $targetStream.Dispose() }
        } finally { $sourceStream.Dispose() }
        Assert-ProductVersion -Path $temporaryExe -ExpectedVersion $ExpectedVersion
    } finally {
        $archive.Dispose()
        if (Test-Path -LiteralPath $temporaryExe -PathType Leaf) { Remove-Item -LiteralPath $temporaryExe }
    }
}

Push-Location $projectRoot
try {
    [xml]$project = Get-Content -LiteralPath 'src\ClipBridge.App\ClipBridge.App.csproj' -Raw
    $versions = $project.SelectNodes('/Project/PropertyGroup/Version')
    if ($versions.Count -ne 1) { throw 'The app project must declare exactly one Version.' }
    $version = $versions[0].InnerText.Trim()
    if ($Tag -cne ('v' + $version)) { throw "Tag $Tag does not match project version $version." }
    $gitRoot = ((Invoke-CheckedNative -Command git -Arguments @('rev-parse', '--show-toplevel')) -join '').Trim()
    if (-not [IO.Path]::GetFullPath($gitRoot).Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Run this script from its own Git checkout.'
    }
    $dirty = @(Invoke-CheckedNative -Command git -Arguments @('status', '--porcelain'))
    if ($dirty.Count -ne 0) { throw 'Commit or set aside checkout changes before creating a release. Store temporary release notes under artifacts/.' }
    $head = ((Invoke-CheckedNative -Command git -Arguments @('rev-parse', 'HEAD')) -join '').Trim()
    $tagCommit = ((Invoke-CheckedNative -Command git -Arguments @('rev-parse', '--verify', ('refs/tags/' + $Tag + '^{commit}'))) -join '').Trim()
    if ($head -cne $tagCommit) { throw 'HEAD must be the commit referenced by the release tag.' }

    $repoJson = (Invoke-CheckedNative -Command gh -Arguments @('repo', 'view', $Repository, '--json', 'nameWithOwner,visibility')) -join "`n"
    $repo = $repoJson | ConvertFrom-Json
    if ($repo.visibility -cne 'PRIVATE' -or -not $repo.nameWithOwner.Equals($Repository, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The release destination must be the requested private repository.'
    }
    $remoteRefJson = (Invoke-CheckedNative -Command gh -Arguments @('api', ('repos/' + $Repository + '/git/ref/tags/' + $Tag))) -join "`n"
    $remoteObject = ($remoteRefJson | ConvertFrom-Json).object
    for ($depth = 0; $remoteObject.type -eq 'tag' -and $depth -lt 8; $depth++) {
        $tagJson = (Invoke-CheckedNative -Command gh -Arguments @('api', ('repos/' + $Repository + '/git/tags/' + $remoteObject.sha))) -join "`n"
        $remoteObject = ($tagJson | ConvertFrom-Json).object
    }
    if ($remoteObject.type -cne 'commit' -or $remoteObject.sha -cne $head) {
        throw 'The remote tag must already exist and reference this exact checkout commit.'
    }
    $existingTags = @(Invoke-CheckedNative -Command gh -Arguments @('api', ('repos/' + $Repository + '/releases?per_page=100'), '--paginate', '--jq', '.[].tag_name'))
    if ($existingTags -ccontains $Tag) { throw 'A release for this tag already exists. Existing releases and assets are never overwritten.' }

    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    $setup = Join-Path $releaseDirectory "ClipBridge-$version-Setup.exe"
    $portable = Join-Path $releaseDirectory "ClipBridge-$version-win-x64.zip"
    $hashFile = Join-Path $releaseDirectory "ClipBridge-$version-SHA256SUMS.txt"
    if (-not $UseExistingArtifacts) {
        if ((Test-Path -LiteralPath $setup) -or (Test-Path -LiteralPath $portable)) {
            throw 'Versioned artifacts already exist locally. Use -UseExistingArtifacts to validate and reuse completed artifacts.'
        }
        $publishDirectory = Join-Path $projectRoot ('artifacts\release-publish-' + $version + '-' + [Guid]::NewGuid().ToString('N'))
        & (Join-Path $PSScriptRoot 'build.ps1') -Destination $publishDirectory
        $installerArguments = @{ SourceDirectory = $publishDirectory; OutputDirectory = $releaseDirectory }
        if (-not [string]::IsNullOrWhiteSpace($CompilerPath)) { $installerArguments.CompilerPath = $CompilerPath }
        & (Join-Path $PSScriptRoot 'build-installer.ps1') @installerArguments
        Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $portable -CompressionLevel Optimal
    }
    if (-not (Test-Path -LiteralPath $setup -PathType Leaf) -or -not (Test-Path -LiteralPath $portable -PathType Leaf)) {
        throw 'The versioned Setup.exe and portable ZIP are both required.'
    }
    Assert-ProductVersion -Path $setup -ExpectedVersion $version
    Assert-PortableVersion -Path $portable -ExpectedVersion $version
    $hashLines = foreach ($asset in @($setup, $portable)) {
        (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($asset)
    }
    [IO.File]::WriteAllLines($hashFile, [string[]]$hashLines, [Text.UTF8Encoding]::new($false))

    $releaseArguments = @('release', 'create', $Tag, '--repo', $Repository, '--verify-tag', '--title', ('ClipBridge ' + $version), '--notes-file', $resolvedNotes)
    if ($Draft) { $releaseArguments += '--draft' }
    if ($GenerateNotes) { $releaseArguments += '--generate-notes' }
    $releaseArguments += @($setup, $portable, $hashFile)
    Invoke-CheckedNative -Command gh -Arguments $releaseArguments
} finally { Pop-Location }
