# Build-ReleaseInstaller.ps1
param (
    [string]$Version = "0.2.1"
)

$ErrorActionPreference = "Stop"

Write-Host "1. Publishing Backtrack binaries..." -ForegroundColor Green
dotnet publish -c Release -r win-x64 -o "./publish"

Write-Host "2. Creating payload ZIP..." -ForegroundColor Green
$payloadZipPath = [System.IO.Path]::GetFullPath("installer/payload.zip")
$releaseZipPath = [System.IO.Path]::GetFullPath("Backtrack-v$Version-win-x64.zip")

if ([System.IO.File]::Exists($payloadZipPath)) { [System.IO.File]::Delete($payloadZipPath) }
if ([System.IO.File]::Exists($releaseZipPath)) { [System.IO.File]::Delete($releaseZipPath) }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory([System.IO.Path]::GetFullPath("./publish"), $payloadZipPath)
[System.IO.File]::Copy($payloadZipPath, $releaseZipPath, $true)

Write-Host "3. Compiling Backtrack-Setup-v$Version.exe installer..." -ForegroundColor Green
dotnet publish ./installer/BacktrackSetup.csproj -c Release -r win-x64 -o "./dist"

$installerExe = "./dist/Backtrack-Setup-v$Version.exe"
if ([System.IO.File]::Exists($installerExe)) {
    Copy-Item $installerExe -Destination "./Backtrack-Setup-v$Version.exe" -Force
    Write-Host "Installer created successfully: Backtrack-Setup-v$Version.exe" -ForegroundColor Green
} else {
    Write-Error "Failed to build installer executable."
}

Write-Host "4. Uploading release assets to GitHub Release v$Version..." -ForegroundColor Green
gh release upload "v$Version" "Backtrack-Setup-v$Version.exe" "$releaseZipPath" --clobber

Write-Host "Done! Release v$Version now includes Backtrack-Setup-v$Version.exe installer!" -ForegroundColor Cyan
