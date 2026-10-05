# SonarFollow

Switch your audio output from the Windows tray — and SteelSeries Sonar follows.

With SteelSeries Sonar installed, picking a device in the Windows sound menu stops doing what you expect: audio has to go to a Sonar virtual device, and the real output (headphones or speakers) can only be changed inside SteelSeries GG. SonarFollow brings the normal Windows workflow back: click **Headphones** or **Speakers** in the tray, and Sonar's output is switched for you.

## Features

- **Switch from the Windows tray.** Pick a physical device in the Windows sound menu and every Sonar output channel (Game, Chat, Media, Aux) is routed to it.
- **Per-device mode.** Each output device is set to one of:
  - **Through Sonar** — audio keeps going through Sonar (EQ, mixer), and Windows is switched back to *Sonar - Gaming* right away.
  - **Direct** — Windows stays on the device, so the tray shows what you actually picked. Sonar is routed there too, so apps bound to Sonar devices (e.g. Discord on *Sonar - Chat*) play on the same device.
  - **Don't touch** — SonarFollow ignores the device.
- **Volume sync.** Optionally, the Windows volume keys change Chat, Media and Aux together with Gaming, keeping the balance you set in Sonar. Mute is synced too.
- **No tray icon.** A small settings window writes a JSON file and can be closed; a background process does the work.
- **Event-driven.** It reacts to Windows device and volume notifications instead of polling.
- **Single small exe.** It runs on the .NET Framework 4.8 built into Windows 10/11, so nothing needs to be installed.

## Requirements

- Windows 10 or 11
- [SteelSeries GG](https://steelseries.com/gg) with Sonar enabled, in **Classic** mode (Streamer mode is not supported yet)

## Install

1. Download `SonarFollow.exe` from the [latest release](../../releases/latest).
2. Put it in a permanent folder, e.g. `%LOCALAPPDATA%\Programs\SonarFollow\`. *Start with Windows* remembers this path.
3. Run it. Windows SmartScreen may warn about an unsigned app: click **More info → Run anyway**.
4. In the window, choose a mode for each device and tick **Start with Windows**. Close the window when you're done; SonarFollow keeps running in the background.

Run the exe again whenever you want to change the settings.

## Usage tips

- **Ignore the SteelSeries GG warning** "Sonar devices should be set as your default audio devices". SonarFollow manages the defaults itself; in *Through Sonar* mode it already puts Sonar back as the default.
- **The Sonar mixer in GG doesn't refresh** when SonarFollow changes volumes. The values are applied (you'll hear it); switch to another GG page and back to see them. Moving a slider in the stale GG window will apply that slider's value.
- **The volume keys** act on whatever Windows has as default. In *Direct* mode they control the device itself; in *Through Sonar* mode they control *Sonar - Gaming*, and with volume sync on, the other channels follow.

## Files

| What | Where |
|---|---|
| Settings | `%APPDATA%\SonarFollow\settings.json` |
| Log | `%LOCALAPPDATA%\SonarFollow\log.txt` (also via *Open log* in the window) |
| Autostart | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `SonarFollow` |

`settings.json` can be edited by hand; the background process picks up changes immediately:

```json
{
  "SyncVolume": true,
  "NewDeviceMode": "sonar",
  "Devices": [
    { "Id": "{0.0.0.00000000}.{...}", "Name": "Headset Earphone (SteelSeries Arctis 9)", "Mode": "sonar" },
    { "Id": "{0.0.0.00000000}.{...}", "Name": "Speakers (Realtek USB Audio)", "Mode": "direct" }
  ]
}
```

`Mode` is `sonar`, `direct` or `ignore`. Devices are matched by ID first and by name as a fallback, since IDs change when drivers are reinstalled. `NewDeviceMode` applies to devices not listed yet.

## Uninstall

1. Open SonarFollow, untick **Start with Windows** and click **Stop**.
2. Delete `SonarFollow.exe`, `%APPDATA%\SonarFollow` and `%LOCALAPPDATA%\SonarFollow`.

## How it works

- The background process (`SonarFollow.exe --agent`) subscribes to Windows Core Audio notifications (`IMMNotificationClient`) for default device changes.
- Sonar exposes a local, undocumented HTTP API on a random port. SonarFollow finds the port by looking at what the `SteelSeriesSonar` process listens on, and calls `PUT /classicRedirections/{channel}/deviceId/{id}` to route the output channels.
- In *Through Sonar* mode the Windows default is set back to the Sonar virtual devices through `IPolicyConfig`, the same interface the Windows sound settings use.
- Volume sync watches the endpoint volume of *Sonar - Gaming* (`IAudioEndpointVolume`) and applies `PUT /volumeSettings/classic/{channel}/Volume/{value}` to the other channels.

## Build from source

No .NET SDK or Visual Studio needed — the C# compiler that ships with Windows is enough:

```powershell
./build.ps1   # produces bin\SonarFollow.exe
```

Stop the background process first if it is running, otherwise the exe is locked.

Every push builds the exe in GitHub Actions; pushing a `v*` tag publishes it as a release.

## Disclaimer

SonarFollow is an independent community project, not affiliated with or endorsed by SteelSeries. SteelSeries, Sonar and GG are trademarks of their respective owner. It relies on Sonar's undocumented local API and on an undocumented Windows interface, so a SteelSeries GG update may break it — please open an issue if it does.
