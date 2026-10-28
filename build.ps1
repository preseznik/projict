# Builds the self-contained app and the installer: artifacts\ProJict-Setup-<version>.exe
# Requires the .NET 10 SDK and Inno Setup 6.
param([string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Version) {
    [xml]$project = Get-Content 'src/ProJict.App/ProJict.App.csproj'
    $Version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}

$publish = 'artifacts/publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish src/ProJict.App -c Release -r win-x64 --self-contained -p:DebugType=none -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found: https://jrsoftware.org/isinfo.php' }

& $iscc "/DAppVersion=$Version" 'installer/ProJict.iss'
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
Write-Host "Installer: artifacts/ProJict-Setup-$Version.exe"
