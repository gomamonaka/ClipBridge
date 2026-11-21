param([Parameter(Mandatory = $true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$configurationPath = Join-Path $projectRoot 'src\ClipBridge.App\bin\Release\net10.0-windows\win-x64\ClipBridge.runtimeconfig.json'
$configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
$packageRoot = $env:NUGET_PACKAGES
if ([string]::IsNullOrWhiteSpace($packageRoot)) { $packageRoot = Join-Path $env:USERPROFILE '.nuget\packages' }
$noticesPath = Join-Path $Destination 'Notices'
New-Item -ItemType Directory -Force -Path $noticesPath | Out-Null
foreach ($framework in $configuration.runtimeOptions.includedFrameworks) {
    if ($framework.name -eq 'Microsoft.NETCore.App') {
        $packagePath = Join-Path $packageRoot ('microsoft.netcore.app.runtime.win-x64\' + $framework.version)
        Copy-Item -LiteralPath (Join-Path $packagePath 'LICENSE.TXT') -Destination (Join-Path $noticesPath 'NET-Runtime-LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $packagePath 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $noticesPath 'NET-Third-Party-Notices.txt')
    }
    if ($framework.name -eq 'Microsoft.WindowsDesktop.App') {
        $packagePath = Join-Path $packageRoot ('microsoft.windowsdesktop.app.runtime.win-x64\' + $framework.version)
        Copy-Item -LiteralPath (Join-Path $packagePath 'LICENSE') -Destination (Join-Path $noticesPath 'WindowsDesktop-LICENSE.txt')
    }
}
