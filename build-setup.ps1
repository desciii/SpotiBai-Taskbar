$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProject = Join-Path $root "TaskbarNowPlaying.csproj"
$setupProject = Join-Path $root "installer\TaskbarNowPlaying.Setup.csproj"
$payloadDir = Join-Path $root "installer\payload"
$appPublishDir = Join-Path $root "installer\app-publish"
$outputDir = Join-Path $root "dist"
$appExe = Join-Path $appPublishDir "SpotiBaiTaskbar.exe"
$payloadExe = Join-Path $payloadDir "SpotiBaiTaskbar.exe"
$setupExe = Join-Path $outputDir "SpotiBai-Taskbar-Setup.exe"

Write-Host "Publishing the app with its .NET runtime..."
& dotnet publish $appProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $appPublishDir --nologo
if ($LASTEXITCODE -ne 0) {
    throw "App publish failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path $appExe)) {
    throw "App publish did not produce $appExe."
}

New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
Copy-Item -LiteralPath $appExe -Destination $payloadExe -Force

Write-Host "Building the standalone installer..."
& dotnet publish $setupProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $outputDir --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Installer publish failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path $setupExe)) {
    throw "Installer publish did not produce $setupExe."
}

$setupPdb = Join-Path $outputDir "SpotiBai-Taskbar-Setup.pdb"
if (Test-Path $setupPdb) {
    Remove-Item -LiteralPath $setupPdb
}
$oldSetupExe = Join-Path $outputDir "TaskbarNowPlaying-Setup.exe"
if (Test-Path $oldSetupExe) {
    Remove-Item -LiteralPath $oldSetupExe
}

Write-Host "Ready to share: $setupExe"
