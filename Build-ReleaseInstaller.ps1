param (
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$proj = Get-Content "./Backtrack.csproj"
    $Version = $proj.Project.PropertyGroup.Version
}

Write-Host "Releasing Backtrack v$Version (Local release, no CI)..." -ForegroundColor Cyan

Write-Host "1. Packaging Stream Deck plugin..." -ForegroundColor Green
Get-Process Backtrack -ErrorAction SilentlyContinue | Stop-Process -Force
$sdDir = [System.IO.Path]::GetFullPath("StreamDeck/com.ilyambr.backtrack.sdPlugin")
$sdPluginDest = [System.IO.Path]::GetFullPath("Backtrack.streamDeckPlugin")
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $sdPluginDest) { Remove-Item $sdPluginDest -Force }
$zip = [System.IO.Compression.ZipFile]::Open($sdPluginDest, [System.IO.Compression.ZipArchiveMode]::Create)
$files = [System.IO.Directory]::GetFiles($sdDir, "*", [System.IO.SearchOption]::AllDirectories)
foreach ($file in $files) {
    $rel = $file.Substring($sdDir.Length).TrimStart('\', '/')
    $entryName = "com.ilyambr.backtrack.sdPlugin/" + $rel.Replace('\', '/')
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file, $entryName) | Out-Null
}
$zip.Dispose()

Write-Host "2. Publishing Backtrack binaries (Self-Contained win-x64)..." -ForegroundColor Green
dotnet publish -c Release -r win-x64 --self-contained true -o "./publish"

Copy-Item $sdPluginDest -Destination "./publish/Backtrack.streamDeckPlugin" -Force

Write-Host "2. Creating payload ZIP..." -ForegroundColor Green
$payloadZipPath = [System.IO.Path]::GetFullPath("installer/payload.zip")
$releaseZipPath = [System.IO.Path]::GetFullPath("Backtrack-v$Version-win-x64.zip")

if ([System.IO.File]::Exists($payloadZipPath)) { [System.IO.File]::Delete($payloadZipPath) }
if ([System.IO.File]::Exists($releaseZipPath)) { [System.IO.File]::Delete($releaseZipPath) }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory([System.IO.Path]::GetFullPath("./publish"), $payloadZipPath)
[System.IO.File]::Copy($payloadZipPath, $releaseZipPath, $true)

Write-Host "3. Compiling Backtrack-Setup-v$Version.exe installer..." -ForegroundColor Green
dotnet publish ./installer/BacktrackSetup.csproj -c Release -r win-x64 -o "./dist" -p:AssemblyName="Backtrack-Setup-v$Version"

if (Test-Path $payloadZipPath) { Remove-Item $payloadZipPath -Force -ErrorAction SilentlyContinue }

$installerExe = "./dist/Backtrack-Setup-v$Version.exe"
if ([System.IO.File]::Exists($installerExe)) {
    Move-Item $installerExe -Destination "./Backtrack-Setup-v$Version.exe" -Force
    Write-Host "Installer created successfully: Backtrack-Setup-v$Version.exe" -ForegroundColor Green
} else {
    Write-Error "Failed to build installer executable."
}

Write-Host "4. Publishing GitHub Release v$Version..." -ForegroundColor Green
$notesArg = @()
$customNotes = "C:/Users/Administrator/.gemini/antigravity/brain/477d99df-f01e-49d5-95ba-f96e84ce9ed8/scratch/release_notes_v$Version.md"
if (Test-Path $customNotes) {
    $notesArg = @("--notes-file", $customNotes)
} else {
    $notesArg = @("--generate-notes")
}

if (Test-Path "Backtrack.streamDeckPlugin") {
    gh release create "v$Version" "Backtrack-Setup-v$Version.exe" "$releaseZipPath" "Backtrack.streamDeckPlugin" --title "v$Version" @notesArg
} else {
    gh release create "v$Version" "Backtrack-Setup-v$Version.exe" "$releaseZipPath" --title "v$Version" @notesArg
}

Write-Host "Done! Release v$Version published successfully to GitHub!" -ForegroundColor Cyan
