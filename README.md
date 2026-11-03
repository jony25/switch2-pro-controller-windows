# Switch 2 Pro Manager

A Windows 11 desktop app (WPF, .NET) that connects a **Nintendo Switch 2 Pro Controller** over **Bluetooth LE**,
shows a live input test panel and exposes the controller to games as a **virtual Xbox 360 pad** (ViGEmBus).
It also has per-profile stick deadzones and curves, a button remap table, a DSU motion server for emulators,
and an interface in English (default) and Spanish.

The Pro Controller 2 is *not* a standard HID device over Bluetooth: it speaks a proprietary GATT protocol, so Windows
cannot use it out of the box and `Windows.Gaming.Input` / HID libraries see nothing. This app talks to it directly through
the WinRT Bluetooth APIs. The protocol comes from community reverse engineering (see [Credits](#credits)); Nintendo does not publish it.

> **Status: community research project.** Verified on one real controller, but the code was written without a CI build,
> so expect to fix small things. Not affiliated with Nintendo.

## What works

| Feature | Status |
|---|---|
| BLE scan, connect, disconnect, auto-reconnect | Works |
| Live test panel (21 buttons, both sticks, ZL/ZR, motion) | Works |
| Virtual Xbox 360 pad (ViGEmBus), positional or by-letter face-button layout | Works |
| Game rumble → controller HD rumble | Works (basic mapping) |
| HD rumble lab (built-in samples + raw frames per actuator) | Works |
| Stick deadzones and curves (inner/outer deadzone, linear, exponential, S-curve) | Works, per profile |
| Button remapping: Xbox target, keys and key combinations (e.g. Alt+Tab), mouse buttons | Works, per profile |
| Profiles (create, rename, delete, switch) | Works; the active one is shown in the header |
| DSU motion server for emulators (Cemuhook protocol) | Works (checked with Eden) |
| Accelerometer / gyroscope (opt-in) | Works over BLE |
| Battery level (0–9, charging flag) | Implemented (read from the headset report) |
| Interface language: English (default) and Spanish | Works; logs stay in English |
| Tray icon, start with Windows, app-side latency figure | Works |
| Audio to the headphone jack over **USB-C** | Works natively in Windows (no app code needed) |
| Audio to the headphone jack over **Bluetooth** | **Not solved**, see [docs/AUDIO_RESEARCH.md](docs/AUDIO_RESEARCH.md) |

## Requirements

* Windows 11 with a Bluetooth 4.0+ (LE) adapter
* [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases) driver for the virtual Xbox pad (the project is retired but still works)
* To build: the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`winget install Microsoft.DotNet.SDK.10`). With .NET 9 change the target framework in the `.csproj`.

## Build and run

```bat
scripts\publish.bat
```

produces `dist\Switch2ProManager.exe` (single file, no .NET runtime needed to run it). Alternatively run the
**build-exe** GitHub Action (Actions → *build-exe* → *Run workflow*) and download the artifact, or open
`src/Switch2ProManager/Switch2ProManager.csproj` in Visual Studio (platform **x64**).

## Using it

1. Close Steam and remove the controller from *Windows Settings → Bluetooth* if it was paired there (Windows cannot pair it).
2. Click **Search for devices**, hold the controller's **SYNC** button for ~3 s until the LEDs animate.
3. Select it in the list and click **Connect**, below the list. With *Emulate Xbox 360 pad* on, games see an
   "Xbox 360 Controller" (check `joy.cpl`).

The window has five tabs, and the active profile and the connection state are always in the header:

* **General**: connection, battery, quick settings (profile management, virtual pad, DSU motion server).
  **New** creates a profile with default values; **Save** writes the current changes to the active profile.
* **Testing and calibration**: live sticks, buttons, vibration strength, motion sensors and the app latency figure.
* **Mapping and deadzones**: inner/outer deadzone and response curve per stick, with a live graph, and the button
  remap table (Xbox target, one or more keys such as Alt+Tab, or a mouse button). Click the Key cell and press the key
  you want; `Del` clears it and `Esc` cancels.
* **Advanced and log**: HD rumble lab, audio experiments, GATT dump and the log viewer.
* **Settings**: window and tray behaviour, start with Windows, auto-reconnect, interface language and About.

Face buttons: *positional* (default) maps Nintendo B → Xbox A, A → B, Y → X, X → Y; untick it to map by letter.
L/R → LB/RB, ZL/ZR → LT/RT (digital: 0 or 255), −/+ → Back/Start, Home → Guide, stick clicks and D-pad as expected.
Capture, C, GL and GR show in the test panel and are not sent to the virtual pad by default; to use the Capture button
for Steam screenshots, assign F12 to Capture in the Mapping tab. Sticks have an inner and an outer deadzone and a
response curve. Everything in the Mapping tab is saved in the active profile when you press Save.

## Settings and logs

The app stores its state in `%LOCALAPPDATA%\Switch2ProManager\settings.json`. An old `settings.json` from v1 is migrated
automatically (its single profile becomes **Default**) and kept as `settings.v1.json.bak`. Unhandled errors and protocol
diagnostics go to `%LOCALAPPDATA%\Switch2ProManager\logs\app.log`; the **Advanced and log** tab has a shortcut to that folder.

## Warnings

* **Registration overwrites the console's pairing.** When you connect while the controller is in SYNC mode, the app runs the
  controller's host-registration handshake (command `0x15`). The controller stores at most two hosts and the app registers
  this PC twice, so your Switch 2 may stop recognising the controller; just pair it again on the console.
* Do **not** send undocumented or flash commands to the controller. Some documented commands write to its internal flash
  (factory calibration, pairing keys) or start a firmware update, and cannot be undone. The app only uses documented,
  console-observed commands: LED, feature select (IMU), vibration samples and the pairing handshake above.
* Keep rumble-lab amplitudes moderate; do not leave the actuators at full power for long.

## Repository layout

```
src/Switch2ProManager/   WPF app (MVVM): Models, Protocol, Services, ViewModels, Views, Resources, Assets
docs/                    PROTOCOL_NOTES.md · AUDIO_RESEARCH.md · SNIFFER_SETUP.md · DSU_OUTPUT.md · UI_SPEC.md
research/captures/       Real captures behind the audio findings + a GATT dump of the controller
research/tools/          Capture analysis, LTK derivation and tone-generation scripts (Python)
scripts/publish.bat      Builds the single-file exe
.github/workflows/       CI that builds the exe
```

Code overview: `Services/ControllerScanner` (BLE advertisements), `Services/Switch2Session` (GATT connection, commands,
rumble, battery, research helpers), `Services/XboxVirtualPad` (ViGEm mapping), `Services/InputMapper` (deadzones, curves and
routing), `Services/HapticsService` (rumble scheduler), `Services/CemuhookServer` (DSU motion server), `Services/SettingsStore`
(profiles and settings v2), `Services/AppText` (localization), `Services/KeyCapture` (low-level keyboard hook for
combinations), `Protocol/*` (UUIDs, report parser, HD rumble encoder), `ViewModels/MainViewModel` (UI logic).

## Research

* [docs/PROTOCOL_NOTES.md](docs/PROTOCOL_NOTES.md): what is known about the BLE protocol and how much of it we verified ourselves.
* [docs/AUDIO_RESEARCH.md](docs/AUDIO_RESEARCH.md): headphone-jack audio over Bluetooth: findings, experiments, open questions.
* [docs/SNIFFER_SETUP.md](docs/SNIFFER_SETUP.md): hardware and step-by-step setup to capture the console↔controller traffic, the missing piece.
* [docs/DSU_OUTPUT.md](docs/DSU_OUTPUT.md): DSU (Cemuhook) motion server: packet layout, clients tested and diagnostics.
* [docs/UI_SPEC.md](docs/UI_SPEC.md): interface design, phase status and the differences from the original plan.

Contributions that would help most: a capture of a real Switch 2 sending audio to the controller, tests on other firmware
versions, and a second unit to confirm the measurements.

## Credits

Protocol knowledge builds on public reverse-engineering work, especially
[ndeadly/switch2_controller_research](https://github.com/ndeadly/switch2_controller_research),
[mlstr0m/switch2bridge-macos](https://github.com/mlstr0m/switch2bridge-macos),
[PRX-24hz/switch2pro-telemetry](https://github.com/PRX-24hz/switch2pro-telemetry) (stick range, gauge and IMU measurements, USB)
and SDL's Switch 2 HD rumble encoding. No Nintendo code or assets are included.

## License

[MIT](LICENSE). Switch and Nintendo are trademarks of Nintendo; this project is independent and unaffiliated.
