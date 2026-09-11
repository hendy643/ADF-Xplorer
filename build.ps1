#Requires -Version 7
<#
.SYNOPSIS
    Builds the AdfXplorer solution, publishes the app, and builds the installer.
#>
param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    # Full semantic version (e.g. "1.2.3" or "1.2.3-beta.1"), applied to the app assemblies.
    # Defaults to whatever Directory.Build.props declares for local/dev builds; CI passes the
    # version derived from the release tag instead.
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# MSI ProductVersion only supports numeric Major.Minor.Build - strip any semver prerelease/metadata suffix.
$installerVersion = if ($Version) { ($Version -split "[-+]")[0] } else { "" }
$versionArgs = if ($Version) { @("-p:Version=$Version", "-p:InstallerVersion=$installerVersion") } else { @() }

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

Write-Host "==> Publishing AdfXplorer.WinFspInstaller" -ForegroundColor Cyan
Invoke-Dotnet -Arguments (@("publish", "$root\src\AdfXplorer.WinFspInstaller\AdfXplorer.WinFspInstaller.csproj", "-c", $Configuration, "-p:Platform=$Platform", "-p:RuntimeIdentifier=win-x64") + $versionArgs)

# dotnet build on the .slnx skips the WiX installer/bundle projects (known dotnet CLI limitation
# for non-TFM project types), so they're built explicitly here.
Write-Host "==> Building installer" -ForegroundColor Cyan
Invoke-Dotnet -Arguments (@("build", "$root\src\AdfXplorer.Installer\AdfXplorer.Installer.wixproj", "-c", $Configuration, "-p:Platform=$Platform") + $versionArgs)

Write-Host "==> Building setup bundle (chains in .NET 10 / WinFsp)" -ForegroundColor Cyan
Invoke-Dotnet -Arguments (@("build", "$root\src\AdfXplorer.Bundle\AdfXplorer.Bundle.wixproj", "-c", $Configuration, "-p:Platform=$Platform") + $versionArgs)

$setup = Get-ChildItem "$root\src\AdfXplorer.Bundle\bin\$Platform\$Configuration\*.exe" | Select-Object -First 1
Write-Host "==> Done: $($setup.FullName)" -ForegroundColor Green
