#Requires -Version 7
<#
.SYNOPSIS
    Builds the AdfXplorer solution, publishes the app, and builds the Inno Setup installer.
#>
param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    # Full semantic version (e.g. "1.2.3" or "1.2.3-beta.1"), applied to the app assemblies and installer.
    # Defaults to whatever Directory.Build.props declares for local/dev builds; CI passes the
    # version derived from the release tag instead.
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

$versionArgs = if ($Version) { @("-p:Version=$Version") } else { @() }

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

Write-Host "==> Building solution ($Configuration|$Platform)" -ForegroundColor Cyan
Invoke-Dotnet -Arguments (@("build", "$root\AdfXplorer.slnx", "-c", $Configuration, "-p:Platform=$Platform") + $versionArgs)

Write-Host "==> Publishing AdfXplorer.WinFsp" -ForegroundColor Cyan
Invoke-Dotnet -Arguments (@("publish", "$root\src\AdfXplorer.WinFsp\AdfXplorer.WinFsp.csproj", "-c", $Configuration, "-p:Platform=$Platform", "-p:RuntimeIdentifier=win-x64") + $versionArgs)

# Resolve effective version for installer if not explicitly passed
$effectiveVersion = $Version
if (-not $effectiveVersion) {
    [xml]$props = Get-Content "$root\Directory.Build.props"
    $effectiveVersion = $props.Project.PropertyGroup.Version
}

Write-Host "==> Building installer with Inno Setup (v$effectiveVersion)" -ForegroundColor Cyan

$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue)?.Source
if (-not $iscc) {
    $possiblePaths = @(
        "${env:ProgramFiles}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
        "$env:USERPROFILE\scoop\apps\inno-setup\current\ISCC.exe"
    )
    $iscc = $possiblePaths | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $iscc) {
    throw "Inno Setup compiler (ISCC.exe) not found. Please install Inno Setup 7."
}

$issFile = "$root\src\AdfXplorer.Installer\AdfXplorer.iss"
$publishDir = "$root\src\AdfXplorer.WinFsp\bin\$Platform\$Configuration\net10.0-windows\win-x64\publish"
$outputDir = "$root\src\AdfXplorer.Installer\bin\$Platform\$Configuration"

& $iscc "/DMyAppVersion=$effectiveVersion" "/DPublishDir=$publishDir" "/O$outputDir" $issFile
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE"
}

$setup = Get-ChildItem "$outputDir\*.exe" | Select-Object -First 1
Write-Host "==> Done: $($setup.FullName)" -ForegroundColor Green
