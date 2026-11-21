param([string]$Destination = (Join-Path $PSScriptRoot '..\artifacts\win-x64'))
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $projectRoot
try {
    dotnet build src\ClipBridge.App\ClipBridge.App.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet run --project tests\ClipBridge.Core.Tests\ClipBridge.Core.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed' }
    dotnet run --project tests\ClipBridge.Transfer.Tests\ClipBridge.Transfer.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Transfer checks failed' }
    dotnet run --project tests\ClipBridge.Native.Integration\ClipBridge.Native.Integration.csproj -c Release -- artifacts\native-integration.json
    if ($LASTEXITCODE -ne 0) { throw 'Native integration checks failed' }
    $uiResult = Join-Path $projectRoot 'artifacts\ui-test.txt'
    New-Item -ItemType Directory -Force -Path (Split-Path $uiResult) | Out-Null
    $check = Start-Process -FilePath (Join-Path $projectRoot 'src\ClipBridge.App\bin\Release\net10.0-windows\ClipBridge.exe') -ArgumentList @('--self-test', ('"' + $uiResult + '"')) -WindowStyle Hidden -PassThru -Wait
    Get-Content -LiteralPath $uiResult
    if ($check.ExitCode -ne 0) { throw 'UI checks failed' }
    dotnet publish src\ClipBridge.App\ClipBridge.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $Destination --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\README.ja.md') -Destination (Join-Path $Destination '使い方.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\TESTING.md') -Destination (Join-Path $Destination 'TESTING.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE.txt') -Destination (Join-Path $Destination 'LICENSE.txt')
    & (Join-Path $PSScriptRoot 'copy-runtime-notices.ps1') -Destination $Destination
    Write-Output ('Build complete: ' + [System.IO.Path]::GetFullPath($Destination))
} finally { Pop-Location }
