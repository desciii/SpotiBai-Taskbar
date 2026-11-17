$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $scriptDir "TaskbarNowPlaying.csproj"
$publishedDir = Join-Path $scriptDir "bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
$exePath = Join-Path $publishedDir "SpotiBaiTaskbar.exe"
$desktopPath = [Environment]::GetFolderPath("Desktop")
$startupDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Startup"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

function Ensure-Shortcut {
    param(
        [string]$ShortcutPath,
        [string]$TargetPath,
        [string]$WorkingDirectory
    )

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = $TargetPath
    $shortcut.Save()
}

if (-not (Test-Path $projectPath)) {
    throw "Project file not found: $projectPath"
}

$normalizedExePath = [System.IO.Path]::GetFullPath($exePath)
foreach ($process in Get-Process -Name "SpotiBaiTaskbar" -ErrorAction SilentlyContinue) {
    try {
        $processPath = $process.MainModule.FileName
        if ([string]::Equals(
                [System.IO.Path]::GetFullPath($processPath),
                $normalizedExePath,
                [System.StringComparison]::OrdinalIgnoreCase
            )) {
            Write-Host "Closing the currently running app before updating it..."
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit(5000)
        }
    }
    finally {
        $process.Dispose()
    }
}

Write-Host "Publishing the latest app..."
& dotnet publish $projectPath -c Release -r win-x64 --self-contained false --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path $exePath)) {
    throw "Publish failed: $exePath was not created."
}

$desktopShortcut = Join-Path $desktopPath "SpotiBai Taskbar.lnk"
Remove-Item -LiteralPath (Join-Path $desktopPath "TaskbarNowPlaying.lnk") -ErrorAction SilentlyContinue
Ensure-Shortcut -ShortcutPath $desktopShortcut -TargetPath $exePath -WorkingDirectory $publishedDir

if (-not (Test-Path $startupDir)) {
    New-Item -ItemType Directory -Path $startupDir -Force | Out-Null
}

$startupShortcut = Join-Path $startupDir "SpotiBai Taskbar.lnk"
Remove-Item -LiteralPath (Join-Path $startupDir "TaskbarNowPlaying.lnk") -ErrorAction SilentlyContinue
Ensure-Shortcut -ShortcutPath $startupShortcut -TargetPath $exePath -WorkingDirectory $publishedDir

Remove-ItemProperty -Path $runKey -Name "TaskbarNowPlaying" -ErrorAction SilentlyContinue
Set-ItemProperty -Path $runKey -Name "SpotiBai Taskbar" -Value "`"$exePath`"" -Type String -Force

Write-Host "Setup complete."
Write-Host "Desktop shortcut: $desktopShortcut"
Write-Host "Startup shortcut: $startupShortcut"
Write-Host "Run key: $runKey"

Start-Process -FilePath $exePath
