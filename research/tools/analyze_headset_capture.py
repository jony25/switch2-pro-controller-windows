#!/usr/bin/env python3
"""Analyse a headset capture produced by the app ("Capture headset input").

Each report is 112 bytes; byte 0x0D is the jack state, 0x0E the audio length (0x32) and 0x0F..0x40 holds 50 bytes
of audio. Two consecutive reports form one 100-byte Opus packet (first chunk has state bit 3 set).

Usage:  python analyze_headset_capture.py capture.txt [--decode out.wav]
Decoding needs:  pip install av numpy
"""
import argparse
import collections
import re
import sys

LINE = re.compile(r"\s*(\d+) ms\s+len=\s*(\d+)\s+state=(\S+)\s+([0-9A-Fa-f]+)")


def load(path):
    rows = []
    for line in open(path, encoding="utf-8"):
        m = LINE.match(line)
        if m:
            rows.append((int(m[1]), m[3], bytes.fromhex(m[4])))
    return rows


def packets(rows):
    """Assemble 100-byte Opus packets from consecutive 50-byte chunks."""
    audio = [r for r in rows if len(r[2]) > 0x40 and r[2][0x0E] == 0x32]
    out, i = [], 0
    while i < len(audio) - 1:
        first, second = audio[i], audio[i + 1]
        if int(first[1], 16) & 0x08 and not int(second[1], 16) & 0x08:
            out.append(first[2][0x0F:0x41] + second[2][0x0F:0x41])
            i += 2
        else:
            i += 1
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("capture")
    ap.add_argument("--decode", metavar="WAV", help="decode the packets with libopus (PyAV) into a 48 kHz mono WAV")
    args = ap.parse_args()

    rows = load(args.capture)
    if not rows:
        sys.exit("no reports found")
    span = (rows[-1][0] - rows[0][0]) / 1000
    print(f"{len(rows)} reports in {span:.1f} s ({len(rows) / span:.0f}/s)")
    print("jack states:", {k: v for k, v in collections.Counter(r[1] for r in rows).items()})
    print("power-info byte (offset 1):", {hex(k): v for k, v in collections.Counter(r[2][1] for r in rows).items()})

    pk = packets(rows)
    print(f"{len(pk)} assembled 100-byte packets; TOC bytes:", collections.Counter(hex(p[0]) for p in pk).most_common(3))

    if args.decode and pk:
        import wave
        import av
        import numpy as np
        ctx = av.CodecContext.create("libopus", "r")
        ctx.sample_rate = 48000
        ctx.layout = "mono"
        ctx.open()
        samples = []
        for p in pk:
            for frame in ctx.decode(av.Packet(p)):
                samples.append(frame.to_ndarray().reshape(-1))
        pcm = np.concatenate(samples).astype(np.float32)
        if np.abs(pcm).max() > 1.5:
            pcm /= 32768
        print(f"decoded {len(pcm) / 48000:.2f} s; RMS {np.sqrt((pcm ** 2).mean()):.4f}; peak {np.abs(pcm).max():.3f}")
        with wave.open(args.decode, "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(48000)
            w.writeframes((np.clip(pcm, -1, 1) * 32767).astype(np.int16).tobytes())
        print("wrote", args.decode)


if __name__ == "__main__":
    main()
