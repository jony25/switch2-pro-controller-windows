# Sniffer setup: capturing the console ↔ controller traffic

The only way to learn how a real Switch 2 sends audio (and which commands it sends first) is to **listen to the
Bluetooth LE link between the console and the controller**. A sniffer is passive: it never transmits, so it cannot harm
the controller or the console.

## 1. What to buy

| Option | Notes |
|---|---|
| **Nordic nRF52840 Dongle (PCA10059)**: recommended | Official hardware of Nordic's *nRF Sniffer for Bluetooth LE*. Roughly 10–20 €/$ from distributors (Mouser, DigiKey, etc.). Check the current price and stock. |
| Makerdiary nRF52840 MDK USB Dongle | Same chip. Ships with a UF2 bootloader, so flashing is drag-and-drop. A good choice if you want the easiest flashing. |
| Adafruit nRF52840 boards (e.g. Feather/ItsyBitsy nRF52840) | Work with the same sniffer firmware (UF2), more expensive than a dongle. |
| Avoid | nRF51-based dongles / the old Adafruit "Bluefruit LE Sniffer": no Bluetooth 5 features. |

Professional analysers (Ellisys, Frontline) work too but cost thousands; they are only worth it if the audio turns out to use
LE Isochronous channels, which the Nordic sniffer may not capture. The GATT table suggests plain GATT writes, so start cheap.

You also need: the **Switch 2 console**, your **controller**, and a Windows PC (it can be the same PC you use for this app).

## 2. Software on the PC

1. **Wireshark** 4.x (<https://www.wireshark.org>).
2. **nRF Connect for Desktop** with the **Programmer** app (to flash the dongle), from Nordic.
3. Nordic's **nRF Sniffer for Bluetooth LE** package (firmware + Wireshark capture plugin), from Nordic's download page.
   The versions we saw documented: sniffer firmware **4.1.1**, `nrfutil ble-sniffer` **0.21.0**, Wireshark **4.6.x**. Follow the instructions
   of the version you download if anything below differs.

### Flash the dongle

**Makerdiary-type dongle (UF2 bootloader, e.g. the "nRF52840 Micro Dev Kit USB Dongle" sold on Amazon by GeeekPi)**

Nordic's *Programmer* app cannot flash these boards (confirmed by a buyer's review); use the UF2 bootloader instead:

1. Hold the button while plugging the dongle into the PC. A removable drive (UF2BOOT or similar) appears and the LED indicates bootloader mode.
2. Download the **BLE sniffer `.uf2`** published by Makerdiary (the `firmware/ble_sniffer/` folder of
   <https://github.com/makerdiary/nrf52840-mdk-usb-dongle>, see its *BLE sniffer* guide) and drag it onto the drive.
3. The LED blinks for a few seconds and the dongle reboots. Windows now shows a serial port (COM*n*) for the sniffer.

Do not convert Nordic's PCA10059 `.hex` yourself unless you have no other choice: that firmware was built for a different bootloader
layout. If you must, Microsoft's `uf2conv.py` (microsoft/uf2) is what other users use. Check the dongle's UF2 bootloader version
(Makerdiary recommends 0.7.1+) in its documentation.

The board is bare: keep it on a non-conductive surface (or in a small case) so it cannot short against anything.

**Official Nordic nRF52840 Dongle (PCA10059)**

1. Plug it in while pressing its **RESET** (side) button: the red LED pulses = bootloader mode.
2. In *Programmer* (nRF Connect for Desktop), select the dongle, add the **nRF52840 Dongle** sniffer firmware from Nordic's package and click **Write**.
3. Unplug and replug: it now shows up as a serial port (COM*n*).

### Install the Wireshark plugin

Either with Nordic's command-line tool (as documented by users of firmware 4.1.1):

```powershell
nrfutil install ble-sniffer
nrfutil ble-sniffer bootstrap --extcap-dir "$env:APPDATA\Wireshark\extcap"
```

or manually: copy the package's `extcap` folder contents into Wireshark's *Personal Extcap* folder (Wireshark → Help → About → Folders) and
install the Python requirements shipped with the package.

Restart Wireshark. You should see **nRF Sniffer for Bluetooth LE COM*n*** on the start page. Enable **View → Interface Toolbars → nRF Sniffer for Bluetooth LE**.
Importing Nordic's Wireshark profile (included in the package) gives you useful columns and filters.

## 3. Before capturing

* Close this app, Steam and anything else that could connect to the controller. A controller talks to one host at a time.
* Place the sniffer within about a metre of the controller and console.
* Find the controller's BLE address: with the controller in SYNC mode, this app's scan list shows it (do **not** connect). You will pick
  it in the sniffer's *Device* dropdown. The pairing advertisement has no local name.
* The sniffer can only follow a connection if it sees the very start of it (the `CONNECT_IND`). **Start capturing first, then make the
  controller connect.** Missing that moment is the most common reason for an empty capture.

## 4. Capture 1: the pairing (gives you the key)

The console pairs with application commands, not BLE SMP. Because no key exists yet, this exchange should happen on an **unencrypted** link, so the key material should be visible (not yet confirmed with a capture).

1. Start the capture, pick the controller in the *Device* dropdown.
2. On the console open the screen used to register a new controller, then **hold SYNC** on the controller until it connects and the console confirms.
3. Wait ~15 s and stop the capture (save as `.pcapng`).
4. In Wireshark use the filter `btatt.opcode == 0x52` (Write Command) and find the write whose value starts with `15 91 01 04`.
   Its layout is `15 91 01 04 00 11 00 00 | 00 | A1 (16 bytes)`; **A1** is the 16 bytes after the first 9. The controller's reply carries
   `B1 = 5CF6EE792CDF05E1BA2B6325C41A5F10` (a constant).
5. Compute the link key (`LTK = A1 XOR B1`):

   ```bash
   python research/tools/ltk_from_pairing.py <A1 as 32 hex characters>
   ```

   It prints the LTK in both byte orders. We have not verified which one Wireshark expects for this device.

Pairing the controller to the console again is a normal operation; nothing is lost.

## 5. Capture 2: an audio session (gives you the answer)

1. In the sniffer toolbar enter the LTK in the **Key** field (try type *LTK* / *Legacy LTK* / *SC LTK* as offered, and the byte order that worked).
2. Start capturing, then make the controller reconnect to the console (press a button on the controller). On reconnection the console just
   re-enables encryption with the stored key; you should now see decrypted ATT traffic.
3. Perform a **scripted sequence** and write down the time of each step:
   plug headphones in → play sound on the console (a game or video) → change volume → pause → unplug headphones → (optional) plug a headset with mic and talk.
4. Stop and save.

## 6. What to look for

Attribute **value handles** are the declaration handles in [`research/captures/gatt_dump.txt`](../research/captures/gatt_dump.txt) plus 1:

| Value handle | Characteristic |
|---|---|
| `0x0012` | HD rumble output |
| `0x0014` | command channel (`…f005`) |
| `0x0016` | second command channel (`3dacbc7e…b379`) |
| `0x002C` | **headset audio output** (`…f72b06`): the target |
| `0x002E` | headset audio input (`…c0f9`) |

Filter examples: `btatt.handle == 0x002c`, `btatt.handle == 0x0014`, `btatt.handle == 0x0016`.

* Which command (if any) is written **just before** the first write to `0x002C` when you plug headphones or start sound?
* What is the exact byte layout of the writes to `0x002C`: length, header bytes, whether a counter increments, whether the payload starts with an Opus TOC byte (`F8`)?
* Do the writes change when you change the volume or unplug the headphones?
* How often are they sent (expected 20 ms per packet)?

Share the `.pcapng` (and the LTK, which is useless once the controller is re-paired) in an issue; with it the audio output can be implemented and tested quickly.

## 7. Troubleshooting

| Symptom | Likely cause |
|---|---|
| Sniffer sees advertising but no connection data | Capture started after the connection; restart the capture, then reconnect. |
| Packets show as "encrypted" and unreadable | Missing LTK, wrong byte order or wrong key type; try the other order. |
| Controller never reaches the console | Another host (this app, Steam) grabbed it; close them. |
| Too many dropped packets | Move the sniffer closer; use a USB extension cable to get it away from the PC. |
