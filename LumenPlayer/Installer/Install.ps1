# Lumen Player per-user installer. No admin rights needed; everything goes under the current user.
# Installs to %LOCALAPPDATA%\Programs\Lumen Player, adds a Start menu shortcut, "Open with" entries,
# a Default Apps entry and an "Installed apps" (uninstall) entry.
param([switch]$Desktop, [switch]$Quiet)

$ErrorActionPreference = 'Stop'
$AppName = 'Lumen Player'
$Version = '1.1.0'
$Source  = $PSScriptRoot
$Dest    = Join-Path $env:LOCALAPPDATA 'Programs\Lumen Player'
$Exe     = Join-Path $Dest 'LumenPlayer.exe'
$ProgId  = 'LumenPlayer.Media'

$VideoExtensions = @(
    '.mp4','.m4v','.mkv','.webm','.avi','.mov','.wmv','.flv','.f4v','.mpg','.mpeg','.m2v','.m2ts','.mts','.ts',
    '.vob','.3gp','.3g2','.ogv','.divx','.xvid','.rm','.rmvb','.asf','.hevc','.h264','.h265','.mxf','.dv'
)
$Extensions = $VideoExtensions + @(
    '.iso',
    '.mp3','.flac','.wav','.aac','.m4a','.ogg','.oga','.opus','.wma','.ape','.aiff','.aif','.mka','.ac3','.dts',
    '.mpc','.wv','.amr','.mid','.midi',
    '.m3u','.m3u8','.pls'
)

# File Explorer thumbnail provider (Thumbnailer\LumenThumbs.comhost.dll), registered for the video types.
$ThumbClsid    = '{FED58414-101E-4886-9BD5-992E50FF490E}'
$ThumbProvider = '{e357fccd-a995-4576-b01f-234630154e96}'  # IThumbnailProvider
$ThumbDll      = Join-Path $Dest 'Thumbnailer\LumenThumbs.comhost.dll'

function Say($text) { if (-not $Quiet) { Write-Host $text } }

if (-not (Test-Path (Join-Path $Source 'LumenPlayer.exe'))) { throw "LumenPlayer.exe not found next to the installer ($Source)." }

# 1. Close any running copy of the installed app so its files can be replaced.
Get-Process LumenPlayer -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Dest, 'OrdinalIgnoreCase') } | ForEach-Object {
    Say "Closing running Lumen Player..."
    $_.CloseMainWindow() | Out-Null
    if (-not $_.WaitForExit(5000)) { $_.Kill() }
}
# Windows' thumbnail process (COM Surrogate) keeps the thumbnailer loaded; end it so the DLL can be replaced.
# Explorer simply starts a fresh one the next time it needs a thumbnail.
Get-Process dllhost -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Modules.FileName -like "$Dest\*" } catch { $false }
} | Stop-Process -Force -ErrorAction SilentlyContinue

# 2. Copy the app (mirror, so files removed in a newer version disappear too).
if ($Source.TrimEnd('\') -ne $Dest.TrimEnd('\')) {
    Say "Copying files to $Dest ..."
    New-Item -ItemType Directory -Force $Dest | Out-Null
    robocopy $Source $Dest /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying files failed (robocopy exit code $LASTEXITCODE)." }
}

# 3. Shortcuts.
$shell = New-Object -ComObject WScript.Shell
function New-Shortcut($path) {
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $Exe
    $lnk.WorkingDirectory = $Dest
    $lnk.IconLocation = "$Exe,0"
    $lnk.Description = 'Play videos, music and online streams'
    $lnk.Save()
}
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"
New-Shortcut $startMenu
Say "Added Start menu shortcut."
if ($Desktop) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk")
    Say "Added desktop shortcut."
}

# 4. Registry (all under HKCU).
function Set-Key($path, $values) {
    if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    foreach ($name in $values.Keys) {
        if ($name -eq '(default)') { Set-Item -Path $path -Value $values[$name] }
        else { New-ItemProperty -Path $path -Name $name -Value $values[$name] -Force | Out-Null }
    }
}
$classes = 'HKCU:\Software\Classes'
$open = "`"$Exe`" `"%1`""

# Lets Win+R / Start run "LumenPlayer".
Set-Key 'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\LumenPlayer.exe' @{ '(default)' = $Exe; 'Path' = $Dest }

# File type used for "Open with" and Default Apps.
Set-Key "$classes\$ProgId" @{ '(default)' = 'Media file'; 'FriendlyTypeName' = 'Media file' }
Set-Key "$classes\$ProgId\DefaultIcon" @{ '(default)' = "$Exe,0" }
Set-Key "$classes\$ProgId\shell\open\command" @{ '(default)' = $open }

# Application entry (name shown in "Open with").
Set-Key "$classes\Applications\LumenPlayer.exe" @{ 'FriendlyAppName' = $AppName }
Set-Key "$classes\Applications\LumenPlayer.exe\shell\open\command" @{ '(default)' = $open }
Set-Key "$classes\Applications\LumenPlayer.exe\shell\open" @{ 'FriendlyAppName' = $AppName }
$supported = @{}
foreach ($ext in $Extensions) { $supported[$ext] = '' }
Set-Key "$classes\Applications\LumenPlayer.exe\SupportedTypes" $supported

# Add to each extension's "Open with" list without taking over the current default.
foreach ($ext in $Extensions) { Set-Key "$classes\$ext\OpenWithProgids" @{ $ProgId = '' } }

# Default Apps (Settings > Apps > Default apps > Lumen Player).
$caps = 'HKCU:\Software\LumenPlayer\Capabilities'
Set-Key $caps @{ 'ApplicationName' = $AppName; 'ApplicationDescription' = 'Modern media player powered by VLC'; 'ApplicationIcon' = "$Exe,0" }
$assoc = @{}
foreach ($ext in $Extensions) { $assoc[$ext] = $ProgId }
Set-Key "$caps\FileAssociations" $assoc
Set-Key 'HKCU:\Software\RegisteredApplications' @{ $AppName = 'Software\LumenPlayer\Capabilities' }

# Video thumbnails in File Explorer, decoded with Lumen's own VLC engine (so HEVC, AV1, FLV... need no extra
# Windows codecs). COM hosting needs the .NET 8+ runtime installed system-wide; skip quietly without it.
$runtimes = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.NETCore.App'
$hasRuntime = (Test-Path $runtimes) -and (Get-ChildItem $runtimes -Directory | Where-Object { [int]($_.Name.Split('.')[0]) -ge 8 })
if ((Test-Path $ThumbDll) -and $hasRuntime) {
    # Windows' isolated thumbnail process ignores per-user COM classes, so the class itself must be registered
    # machine-wide. That one step needs admin rights (a single UAC prompt), and only when it isn't already in place.
    Remove-Item "$classes\CLSID\$ThumbClsid" -Recurse -Force -ErrorAction SilentlyContinue  # per-user copy from 1.1.0 builds
    $machineKey = "HKLM:\Software\Classes\CLSID\$ThumbClsid\InprocServer32"
    if ((Get-ItemProperty $machineKey -ErrorAction SilentlyContinue).'(default)' -ne $ThumbDll) {
        $esc = $ThumbDll.Replace('\', '\\')
        $regFile = Join-Path $env:TEMP 'LumenThumbs.reg'
        @(
            'Windows Registry Editor Version 5.00', '',
            "[HKEY_LOCAL_MACHINE\Software\Classes\CLSID\$ThumbClsid]", '@="Lumen Player video thumbnails"', '',
            "[HKEY_LOCAL_MACHINE\Software\Classes\CLSID\$ThumbClsid\InprocServer32]", "@=`"$esc`"", '"ThreadingModel"="Both"', ''
        ) | Set-Content $regFile -Encoding Unicode
        Say "Windows will ask for permission to register the video thumbnail add-on..."
        try { Start-Process reg.exe -ArgumentList "import `"$regFile`"" -Verb RunAs -Wait -WindowStyle Hidden } catch { }
        Remove-Item $regFile -ErrorAction SilentlyContinue
    }
    if ((Get-ItemProperty $machineKey -ErrorAction SilentlyContinue).'(default)' -eq $ThumbDll) {
        foreach ($ext in $VideoExtensions) { Set-Key "$classes\$ext\ShellEx\$ThumbProvider" @{ '(default)' = $ThumbClsid } }
        Set-Key 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved' @{ $ThumbClsid = 'Lumen Player video thumbnails' }
        Say "Enabled video thumbnails in File Explorer."
    } else {
        # Without the class, a mapping would only blank out the thumbnails Windows can make itself.
        foreach ($ext in $VideoExtensions) {
            $key = "$classes\$ext\ShellEx\$ThumbProvider"
            if ((Get-ItemProperty $key -ErrorAction SilentlyContinue).'(default)' -eq $ThumbClsid) { Remove-Item $key -Force }
        }
        Say "Skipped video thumbnails (permission was not granted). Run the installer again to retry."
    }
} else {
    Say "Skipped video thumbnails (needs the .NET 8 runtime: https://dotnet.microsoft.com/download/dotnet/8.0)."
}

# Installed apps entry.
$sizeKb = [int]((Get-ChildItem $Dest -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
Set-Key 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LumenPlayer' @{
    'DisplayName'     = $AppName
    'DisplayVersion'  = $Version
    'DisplayIcon'     = "$Exe,0"
    'Publisher'       = 'Lumen Player'
    'InstallLocation' = $Dest
    'UninstallString' = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$Dest\Uninstall.ps1`""
    'NoModify'        = 1
    'NoRepair'        = 1
    'EstimatedSize'   = $sizeKb
}
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LumenPlayer' -Name EstimatedSize -Value $sizeKb -Type DWord
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LumenPlayer' -Name NoModify -Value 1 -Type DWord
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LumenPlayer' -Name NoRepair -Value 1 -Type DWord

# Tell Explorer that file associations changed.
Add-Type -Namespace LumenInstall -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, uint f, IntPtr a, IntPtr b);'
[LumenInstall.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

Say ""
Say "Lumen Player $Version is installed."
Say "Find it by typing 'Lumen' in Start. To make it your default player:"
Say "  Settings > Apps > Default apps > Lumen Player."
