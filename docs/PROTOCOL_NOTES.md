# Protocol notes

What we know about the Pro Controller 2 (USB `057E:2069`) over **Bluetooth LE**, with how each item was established:

* **Verified**: observed on a real controller during this project.
* **Community**: from the public documentation of [ndeadly/switch2_controller_research](https://github.com/ndeadly/switch2_controller_research) or other projects; not independently re-checked.
* **Unknown**: not documented or not understood.

## Advertising (Community, parsed by the app)

Manufacturer data with company ID `0x0553`. Without the company ID: bytes 3–4 = VID (`0x057E`), 5–6 = PID (`0x2069`),
10–15 = the address of the host the controller wants to reconnect to. `0` means SYNC/pairing mode.

## GATT table (Verified: [captures/gatt_dump.txt](../research/captures/gatt_dump.txt))

One proprietary service `ab7de9be-89fe-49ad-828f-118f09df7fd0`. The dump shows the *declaration* handle; the value handle is +1.

| UUID (last bytes) | Props | Role |
|---|---|---|
| `…7fd2` | Notify | Input report (the 0x05-style report the app parses) |
| `…c0f8` | Notify | Input report 0x09 (Community) |
| `…c0f9` | Notify | Input report with headset audio (**Verified**: Opus from the jack microphone); also carries power info. Direct reads fail |
| `cc483f51…f72b05` | Write w/o response | HD rumble output |
| `cc483f51…f72b06` | Write w/o response | Headset audio **output** (format unknown) |
| `649d4ac9…f005` | Write w/o response | Command channel |
| `c765a961…836a` | Notify | Command responses |
| others | | Present in the dump, unused by the app |

The controller does **not** expose the standard Device Information service, so the firmware version cannot be read over BLE.

## Input report (Verified on BLE; same layout as USB report 0x05 without the ID)

| Offset | Content |
|---|---|
| 0–3 | Counter / timestamp (u32 LE) |
| 4–7 | Button bits (u32 LE), see `SwitchButtons` |
| 10–12 / 13–15 | Left / right stick, two 12-bit values per 3 bytes |
| 31–32 | Battery **gauge**: *not* a voltage (≈3670–3728 when full per PRX-24hz); do not convert to a percentage |
| 0x2A–0x3B | Motion: timestamp u32, temperature i16, **accelerometer** X,Y,Z i16, then **gyroscope** X,Y,Z i16 |

* Sticks: centre ≈2048; one user measured a maximum radius of 1765–1810 (≈86 % of the 12-bit range). The app uses 1750.
  Factory calibration lives in the controller's flash and is not read.
* Motion is only present after enabling the IMU feature with command `0x0C` (set mask `0x2F`, then enable, as the console does).
  Scale: accelerometer 8/32767 g per LSB, gyroscope 2000/32767 °/s per LSB. Axis convention measured by PRX-24hz over USB:
  +Z out of the front face, +Y towards the USB-C port, +X to the player's right; right-hand rule for rotation.
  Over BLE the order *accelerometer first, gyroscope second* was confirmed on our unit.

## Commands (Community; frame verified by use)

Written to `…f005`: `[cmd, 0x91, 0x01, sub, 0x00, len, 0x00, 0x00] + data`; replies arrive on `…836a`.
Used by the app: `0x09/0x07` player LED, `0x0C/0x02,0x04,0x05` feature select, `0x0A/0x02` play a vibration sample (IDs 0–7, 0 stops),
`0x15/*` host registration. **Dangerous, never used**: `0x02` (flash read/write/erase), `0x03/0x07-0x09` (pairing storage), `0x0D` (firmware update).

## Host registration / pairing (Community)

The console does *application-level* pairing with command `0x15` instead of BLE SMP: it sends its addresses (`0x01`), a 16-byte
host key `A1` (`0x04`), the controller answers `B1` (a constant, `5CF6EE79…1A5F10`), and both derive **`LTK = A1 XOR B1`**, which then
encrypts the link. A challenge/response (`0x02`) and a finish command (`0x03`) complete it. The app replays a fixed `A1` so
its own LTK is constant; see the warning in the README.

## HD rumble (Verified encoding, community-validated delivery)

Five-byte frame = 40-bit LE value, four 10-bit fields: high-band frequency, high-band amplitude, low-band frequency,
low-band amplitude. Matches SDL's published example (`87 c5 21 91 38`) byte for byte. BLE payload to `…05`:
`0x00` + left block + right block, each block `0x50|seq` followed by the frame ×3. The frequency-code to Hz mapping is unknown;
the lab sliders in the app are for exploring it. The HD rumble lab works on real hardware.

## Battery level (Community, verified to be readable)

The headset report (`…c0f9`) carries a *Power Info* byte at offset 1: bit0 = external power, bit1 = charging, bits 2–5 = level 0–9.
It reads `0x20` (level 8) in all our captures. The app subscribes to the characteristic for a moment every 10 s to read it,
because a direct GATT read of that characteristic fails.

## Headset audio (Verified input, unknown output)

See [AUDIO_RESEARCH.md](AUDIO_RESEARCH.md).
