# Headphone-jack audio over Bluetooth: research log

**Goal:** play PC audio through headphones plugged into the controller's 3.5 mm jack, wirelessly.
**Status:** unsolved. The controller→PC (microphone) direction is understood; the PC→controller direction is not.
**Works today:** USB-C (the jack shows up as a Windows sound device) and pairing Bluetooth headphones directly with the PC.

Controller: Switch 2 Pro Controller with the headset characteristics present (firmware 2.0.0+ according to community docs).

## Input (controller → PC): solved

Characteristic `7492866c-ec3e-4619-8258-32755ffcc0f9`, 112-byte reports at ≈250/s. Evidence: [`research/captures`](../research/captures).

| Capture | Situation | Result |
|---|---|---|
| A | Nothing in the jack | jack state `0x00`, no audio |
| B | Headphones without mic | state alternates `0x0D`/`0x05`; audio slots hold `F8 FF FE` + zeros |
| C | Headset with mic, silent | states `0x0F`/`0x07`; real packets |
| D | Headset with mic, speaking | same, speech decodes |

* Offsets: `0x0D` jack state (`0x05/0x0D` headphones, `0x07/0x0F` headset with mic; bit 3 marks the first chunk), `0x0E` audio length (`0x32`), `0x0F…0x40` 50 audio bytes.
* Two consecutive reports form **one 100-byte Opus packet every 20 ms**: 48 kHz, mono, CELT fullband, CBR (~40 kbps), TOC `0xF8`.
  501/502 packets per capture, all `0xF8`; decoded with libopus without errors; speech shows a ≈100 Hz fundamental, silence shows very low noise.
* Reproduce: `python research/tools/analyze_headset_capture.py research/captures/headset_D_headset_mic_speaking.txt --decode d.wav`.

## Output (PC → controller): unsolved

Characteristic `cc483f51-9258-427d-a939-630c31f72b06`, write without response, undocumented. We wrote an Opus test tone
(440 Hz, same packet format as the microphone path) in 17 layouts from the app's *Research* panel.

| Variants | What was written | Controller reaction |
|---|---|---|
| 1, 2 | raw packet, no header | buzzing sequence of sounds |
| 3, 4 | `[0F,32,…]`, `[00,F8,…]` | nothing |
| 5–8 | `[00, state, 32] + 50 bytes` ×2, states `0F/07`, `0D/05`, `09/01`, `0B/03` | vibrates and makes a sequence of sounds |
| 9–11 | same, states `0C/04`, `08/00`, `0E/06` | nothing |
| 12–15 | single 100-byte block, pre-buffering, counter, standalone 50-byte packets | same "sequence of sounds"; 15 more choppy |
| 16, 17 | controls: zeroed data, valid Opus silence | **not yet tested** |

Findings:

1. **The second byte of the write is a control byte and its bit 0 enables playback.** Every layout with bit 0 set at index 1 reacted; every layout without it was ignored. This explains variants 1–4 as well (the 2nd byte of an Opus packet varies).
2. The structure that reacts is `[0x00][control][0x32][50 bytes]`.
3. The reaction is **vibration plus a buzzing sound, never a clean tone and never audio in the headphones.** Official specifications list no speaker, so the audible part is most likely the haptic actuators. Nothing reached the jack.
4. Official sample vibrations (command `0x0A/0x02`) include beeps, which probably explains the "sounds" reported earlier.

## Open hypotheses

1. The console first sends an undocumented command that enables headset playback (commands `0x04–0x07, 0x0E, 0x0F, 0x11–0x14, 0x16–0x18` are "unknown" in the community docs).
2. The output is not plain Opus, or uses other parameters, and this characteristic feeds the haptics path.
3. Routing to the jack needs state that only the console configures.

Blindly sending unknown commands is **not** an option: some commands in the same family write permanent data (see PROTOCOL_NOTES).

## What would settle it

A passive capture of a real Switch 2 playing audio to the controller's jack: [SNIFFER_SETUP.md](SNIFFER_SETUP.md).
Things to extract: any command written before audio starts, and the exact bytes written to the audio output characteristic
(value handle `0x002C`) while sound plays, correlated with what was done on the console.
