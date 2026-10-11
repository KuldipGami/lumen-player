# Lumen Player

A modern-looking desktop media player for Windows, built on VLC's engine (LibVLC), so it plays everything VLC plays, plus YouTube and other web video links.

## Install

1. Download `LumenPlayer-1.1.0-win-x64.zip` from [Releases](../../releases) and unzip it anywhere.
2. Double-click **`Install.cmd`**. No admin rights needed, and the app is self-contained (no .NET install required).
3. Find **Lumen Player** in the Start menu. To make it your default player: Settings › Apps › Default apps › Lumen Player.

The installer copies the app to `%LOCALAPPDATA%\Programs\Lumen Player`, adds a Start menu shortcut, "Open with" entries and an Installed apps entry. Uninstall from Settings › Apps › Installed apps. You can also skip installing and run `LumenPlayer.exe` straight from the unzipped folder.

The very first launch takes ~10–20 s while LibVLC scans its plugins; later launches are fast.

## Features

- **Formats:** every format VLC supports, including MKV, MP4, AVI, MOV, WebM, HEVC/H.265 (10-bit too), AV1, VP9, MPEG-2, DVD `.iso`, FLAC, Opus, DTS, AC3 and more, with hardware decoding
- **YouTube & online video:** paste a YouTube, Vimeo, Twitch or other web link (Ctrl+N) and it plays, via the bundled [yt-dlp](https://github.com/yt-dlp/yt-dlp), which can update itself from Settings
- **Network streams:** http(s), HLS (`.m3u8`), DASH, RTSP, RTMP, UDP, MMS, FTP
- **File Explorer thumbnails:** video thumbnails for every format, decoded with Lumen's own engine, so HEVC/AV1/FLV files get thumbnails without extra Windows codecs (see below)
- **Playlist:** sidebar with durations, drag & drop of files or folders (recursive), natural sorting, reorder, remove, save as `.m3u8`, open `.m3u`/`.m3u8`/`.pls`
- **Playback:** shuffle, repeat all/one, A-B loop, speed 0.25×–4×, frame stepping, jump to 0–90%, resume where you left off, configurable seek distances
- **Audio:** track selection, output device selection, 10-band equalizer with VLC presets and preamp, volume up to 200%, audio delay
- **Subtitles:** embedded tracks, load external files (or drag & drop `.srt`/`.ass`/…), subtitle delay
- **Video:** aspect ratio, crop, zoom, deinterlace modes, brightness/contrast/saturation/hue/gamma, snapshots (saved to `Pictures\Lumen Player`)
- **Interface:** dark design with gradient accents, auto-hiding controls, on-screen display, fullscreen with clock, always-on-top, media information sheet, artwork for audio files, taskbar thumbnail buttons, system sleep blocked while playing
- **Memory:** recent files, window size, volume, equalizer and per-file resume positions are saved in `%APPDATA%\LumenPlayer`

Press **F1** in the app for every keyboard shortcut. The main ones are Space (play/pause), F (fullscreen), ←/→ (seek), ↑/↓ or mouse wheel (volume), [ / ] (speed), Ctrl+O (open), Ctrl+N (open link), Ctrl+L (playlist), Ctrl+E (effects) and Ctrl+P (settings, including seek distances).

### Explorer thumbnails

The thumbnail add-on needs the [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0) installed system-wide (the player itself doesn't). Windows only loads thumbnail add-ons that are registered machine-wide, so the installer asks for permission once (a UAC prompt) for that single step. If you decline, everything else still installs and Windows keeps its own thumbnails. Problems are logged to `%APPDATA%\LumenPlayer\thumbnailer.log`.

## Building from source

Requires the .NET 8 SDK on Windows.

```bash
dotnet build LumenPlayer
```

```bash
dotnet publish LumenPlayer -c Release -r win-x64 --self-contained true -p:DebugType=none -o "Release/Lumen Player"
```

Publishing also builds the thumbnail add-on into `Release/Lumen Player/Thumbnailer`, and copies the installer scripts to the root of the folder.

## Code layout

| Path | Purpose |
|---|---|
| `LumenPlayer/MainWindow.xaml` | Layout: title bar, video overlay, controls, effects flyout, playlist |
| `LumenPlayer/MainWindow.xaml.cs` | Playback core: opening media, playlist navigation, timeline, OSD |
| `LumenPlayer/MainWindow.Input.cs` | Keyboard, mouse, drag & drop, fullscreen, playlist panel |
| `LumenPlayer/MainWindow.Menus.cs` | Context menus, track and device selection, video geometry, info sheets |
| `LumenPlayer/MainWindow.Effects.cs` | Image adjustments, equalizer, A/V sync |
| `LumenPlayer/MainWindow.Settings.cs` | Settings dialog: seek distances, volume step, yt-dlp updates |
| `LumenPlayer/Models/` | Settings, playlist items, supported formats, web link resolving (yt-dlp) |
| `LumenPlayer/Themes/Styles.xaml` | Palette and control styles |
| `LumenPlayer/Installer/` | Per-user `Install.ps1` / `Uninstall.ps1` |
| `LumenThumbs/` | File Explorer thumbnail provider (COM, decodes a frame with LibVLC) |

## Third-party components

- [LibVLC / LibVLCSharp](https://www.videolan.org/vlc/libvlc.html) by VideoLAN (LGPL-2.1)
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense), bundled as `LumenPlayer/Tools/yt-dlp.exe`
