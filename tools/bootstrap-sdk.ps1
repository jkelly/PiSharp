#!/usr/bin/env pwsh
<#
.SYNOPSIS
Installs the exact .NET SDK pinned in global.json into a local directory (default: .dotnet)
so contributors can build and test PiSharp without modifying system-wide SDK installations
or relaxing global.json's deterministic offline contract.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$globalJson = Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json
$sdkVersion = [string]$globalJson.sdk.version
if ([string]::IsNullOrWhiteSpace($sdkVersion)) {
    throw 'global.json does not declare sdk.version.'
}
if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = Join-Path $repoRoot '.dotnet'
}
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

$dotnetExe = if ($IsWindows -or $env:OS -eq 'Windows_NT') { Join-Path $InstallDir 'dotnet.exe' } else { Join-Path $InstallDir 'dotnet' }
if (Test-Path -LiteralPath $dotnetExe) {
    $installed = @(& $dotnetExe --list-sdks 2>$null)
    if ($installed -match ("^" + [regex]::Escape($sdkVersion) + "\s")) {
        Write-Host ".NET SDK $sdkVersion is already installed in $InstallDir."
        Write-Host "Use in this shell:"
        Write-Host "  `$env:DOTNET_ROOT = '$InstallDir'; `$env:PATH = `"`$env:DOTNET_ROOT;`$env:PATH`""
        exit 0
    }
}

$installer = Join-Path $InstallDir 'dotnet-install.ps1'
Write-Host "Downloading official Microsoft dotnet-install.ps1 for .NET SDK $sdkVersion..."
Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
& $installer -Version $sdkVersion -InstallDir $InstallDir -NoPath
if ($LASTEXITCODE -ne 0) { throw "dotnet-install.ps1 failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "Installed .NET SDK $sdkVersion to $InstallDir."
Write-Host "Activate in PowerShell:"
Write-Host "  `$env:DOTNET_ROOT = '$InstallDir'; `$env:PATH = `"`$env:DOTNET_ROOT;`$env:PATH`""
