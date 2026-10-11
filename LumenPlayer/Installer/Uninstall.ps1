# Removes the per-user Lumen Player install. Your settings in %APPDATA%\LumenPlayer are kept.
$ErrorActionPreference = 'SilentlyContinue'
$AppName = 'Lumen Player'
$Dest    = Join-Path $env:LOCALAPPDATA 'Programs\Lumen Player'
$ProgId  = 'LumenPlayer.Media'
$classes = 'HKCU:\Software\Classes'

Set-Location $env:TEMP

Get-Process LumenPlayer | Where-Object { $_.Path -and $_.Path.StartsWith($Dest, 'OrdinalIgnoreCase') } | ForEach-Object {
    $_.CloseMainWindow() | Out-Null
    if (-not $_.WaitForExit(5000)) { $_.Kill() }
}
# Release the thumbnailer DLL held by Windows' thumbnail process (COM Surrogate).
Get-Process dllhost | Where-Object { try { $_.Modules.FileName -like "$Dest\*" } catch { $false } } | Stop-Process -Force

Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk") -Force
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk") -Force

# Drop our entry from each extension's "Open with" list.
Get-ChildItem $classes | Where-Object { $_.PSChildName -like '.*' } | ForEach-Object {
    $owp = Join-Path $_.PSPath 'OpenWithProgids'
    if (Get-ItemProperty $owp -Name $ProgId) { Remove-ItemProperty $owp -Name $ProgId -Force }
}

# Hand video thumbnails back to Windows.
$ThumbClsid = '{FED58414-101E-4886-9BD5-992E50FF490E}'
Get-ChildItem $classes | Where-Object { $_.PSChildName -like '.*' } | ForEach-Object {
    $key = Join-Path $_.PSPath 'ShellEx\{e357fccd-a995-4576-b01f-234630154e96}'
    if ((Get-ItemProperty $key).'(default)' -eq $ThumbClsid) { Remove-Item $key -Recurse -Force }
}
Remove-Item "$classes\CLSID\$ThumbClsid" -Recurse -Force
if (Test-Path "HKLM:\Software\Classes\CLSID\$ThumbClsid") {
    # Registered machine-wide (see Install.ps1), so removing it needs one UAC prompt.
    try { Start-Process reg.exe -ArgumentList "delete `"HKLM\Software\Classes\CLSID\$ThumbClsid`" /f" -Verb RunAs -Wait -WindowStyle Hidden } catch { }
}
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved' -Name $ThumbClsid -Force

Remove-Item "$classes\$ProgId" -Recurse -Force
Remove-Item "$classes\Applications\LumenPlayer.exe" -Recurse -Force
Remove-Item 'HKCU:\Software\LumenPlayer' -Recurse -Force
Remove-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name $AppName -Force
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\LumenPlayer.exe' -Recurse -Force
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LumenPlayer' -Recurse -Force

Remove-Item $Dest -Recurse -Force

Add-Type -Namespace LumenUninstall -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, uint f, IntPtr a, IntPtr b);'
[LumenUninstall.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
