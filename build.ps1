<#
.SYNOPSIS
    Builds Mic Booster into a single self-contained .exe you can hand to someone.

.DESCRIPTION
    Publishes a self-contained win-x64 build, so the machine it runs on does NOT need
    the .NET runtime installed. The result is one file: dist\MicBooster.exe

.PARAMETER Run
    Launch the app once the build finishes.

.PARAMETER Configuration
    Which configuration to build. Defaults to Release.
    (Named Configuration rather than -Debug because -Debug is a reserved PowerShell
    common parameter and would collide.)

.PARAMETER FrameworkDependent
    Produce a much smaller .exe that requires the .NET 9 Desktop Runtime to be installed.
    Only worth it if you know the target machine already has it.

.EXAMPLE
    .\build.ps1
    Builds dist\MicBooster.exe, self-contained.

.EXAMPLE
    .\build.ps1 -Run
    Builds it and launches it.
#>
[CmdletBinding()]
param(
    [switch]$Run,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\MicBooster\MicBooster.csproj'
$dist = Join-Path $root 'dist'
$configuration = $Configuration

Write-Host ''
Write-Host '  Mic Booster - build' -ForegroundColor Cyan
Write-Host '  -------------------' -ForegroundColor DarkGray

if (-not (Test-Path $project)) {
    Write-Host "  Could not find the project at $project" -ForegroundColor Red
    exit 1
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host '  The .NET SDK is not installed, or dotnet is not on PATH.' -ForegroundColor Red
    Write-Host '  Install the .NET 9 SDK from https://dotnet.microsoft.com/download' -ForegroundColor Yellow
    exit 1
}

$sdkList = & dotnet --list-sdks
$hasNine = $sdkList | Where-Object { $_ -match '^9\.' }
if (-not $hasNine) {
    Write-Host '  Warning: no .NET 9 SDK detected. Installed SDKs:' -ForegroundColor Yellow
    $sdkList | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    Write-Host '  The build may fail. Install the .NET 9 SDK if it does.' -ForegroundColor Yellow
}

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

$publishArgs = @(
    'publish', $project,
    '-c', $configuration,
    '-r', 'win-x64',
    '-o', $dist,
    '--nologo',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:PublishReadyToRun=false',
    '-p:PublishTrimmed=false',
    '-p:DebugType=none',
    '-p:GenerateDocumentationFile=false'
)

if ($FrameworkDependent) {
    Write-Host '  Mode: framework-dependent (target machine needs the .NET 9 Desktop Runtime)' -ForegroundColor DarkGray
    $publishArgs += '--self-contained:false'
}
else {
    Write-Host '  Mode: self-contained (no .NET install needed on the target machine)' -ForegroundColor DarkGray
    $publishArgs += '--self-contained:true'
    # Compression only applies to self-contained single-file builds.
    $publishArgs += '-p:EnableCompressionInSingleFile=true'
}

Write-Host "  Configuration: $configuration"
Write-Host '  Publishing...' -ForegroundColor DarkGray
Write-Host ''

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host '  Build FAILED.' -ForegroundColor Red
    exit $LASTEXITCODE
}

# Single-file publish still drops a few loose files; the .exe is the deliverable.
$exe = Join-Path $dist 'MicBooster.exe'
if (-not (Test-Path $exe)) {
    Write-Host '  Build reported success but MicBooster.exe is missing.' -ForegroundColor Red
    exit 1
}

Get-ChildItem $dist -File | Where-Object {
    $_.Extension -in @('.pdb', '.xml') -or $_.Name -like '*.deps.json'
} | Remove-Item -Force -ErrorAction SilentlyContinue

$sizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ''
Write-Host '  Build succeeded.' -ForegroundColor Green
Write-Host "  Output: $exe" -ForegroundColor White
Write-Host "  Size:   $sizeMb MB"
Write-Host ''
Write-Host '  Hand your friend that single .exe. Nothing to install.' -ForegroundColor Cyan
Write-Host '  For routing into Discord/Zoom/OBS they also need a virtual audio cable -' -ForegroundColor DarkGray
Write-Host '  see the Routing section of README.md.' -ForegroundColor DarkGray
Write-Host ''

if ($Run) {
    Write-Host '  Launching...' -ForegroundColor DarkGray
    Start-Process $exe
}
