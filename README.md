# Tuesday Mixer

**Tuesday Mixer is created and published by Tuesday Works.**

Tuesday Mixer is a lightweight Windows volume mixer with a dark, compact interface inspired by Tuesday Recorder. It stays synchronized with Windows while giving you quick control over the master output and each app that is currently playing audio.

## Features

- Master Windows output volume and mute control
- Separate volume and mute controls for every active audio app
- Live left/right stereo meters with smooth 30 FPS animation
- Custom names for mixer channels, remembered between launches
- Drag-and-drop channel reordering, remembered between launches
- Dark Tuesday styling and the dedicated Tuesday Mixer icon
- Standard resizable Windows window with native Snap layouts, including one-third-screen arrangements
- Portable operation with no installer or administrator access required

## Download and run

Download `Tuesday Mixer.exe`, `tuesday.ico`, and `tuesday.png` into the same folder, then open `Tuesday Mixer.exe`.

Windows may show a SmartScreen warning for an unsigned portable app. If you trust this download, choose **More info** and then **Run anyway**.

## Rename a channel

Click the pencil button beside an app, or double-click its channel name. Your custom channel names are stored locally in `%LocalAppData%\Tuesday Mixer`.

## Build from source

The included `Build.ps1` compiles `TuesdayMixer.cs` using the C# compiler included with Windows/.NET Framework.

```powershell
powershell -ExecutionPolicy Bypass -File .\Build.ps1
```

## Credits

Tuesday Mixer is a **Tuesday Works** application. It is not published by Journey to Healing.

Copyright © 2026 Tuesday Works. All rights reserved.
