# Audio PTT Latch

A small native Windows WinForms utility for push-to-talk latching.

## How it works

1. Press the configured activation key.
2. The original key-down is allowed through to Windows, so your game sees push-to-talk start normally.
3. Release the physical key.
4. If the selected input or output device is still above the activity threshold, the physical key-up is suppressed.
5. Once the selected device stays silent, the app sends the delayed key-up.

This is intended for voice-changer setups where the game can stop receiving push-to-talk before the changer is done outputting processed audio.

## Setup notes

- Use `Monitor: Output` if the voice changer exposes its processed sound on a playback device or virtual output.
- Use `Monitor: Input` if the processed signal appears as a microphone device.
- Start with a threshold around `0.020`.
- Use `100 ms` release delay as a starting point.
- If the game runs as administrator, run this app as administrator too so the keyboard hook can affect that elevated process.

## Build

```powershell
dotnet build -c Release -p:Platform=x64
dotnet publish -c Release -p:Platform=x64
```

The published executable is written to:

```text
bin\x64\Release\net10.0-windows\win-x64\publish\AudioPttLatch.exe
```
