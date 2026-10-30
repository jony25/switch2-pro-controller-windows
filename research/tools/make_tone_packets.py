#!/usr/bin/env python3
"""Regenerate the Opus test-tone packets embedded in src/Switch2ProManager/Protocol/TonePackets.cs.

Needs:  pip install av numpy
Packets are CBR, 20 ms, 48 kHz, mono, CELT fullband (TOC 0xF8) - the format seen in the controller's microphone path.
Prints a JSON object {quiet100, normal100, quiet50, normal50} of base64 packets.
"""
import base64
import json

import av
import numpy as np

SR = 48000


def tone(seconds, amp, freq=440.0):
    n = int(SR * seconds)
    t = np.arange(n) / SR
    rng = np.random.default_rng(1)
    x = amp * np.sin(2 * np.pi * freq * t) + amp * 0.15 * rng.standard_normal(n)   # a little noise keeps the encoder fullband
    fade = int(0.05 * SR)
    w = np.ones(n)
    w[:fade] = np.linspace(0, 1, fade)
    w[-fade:] = np.linspace(1, 0, fade)
    return (x * w * 32767).astype(np.int16)


def encode(pcm, bitrate):
    ctx = av.CodecContext.create("libopus", "w")
    ctx.sample_rate, ctx.layout, ctx.format, ctx.bit_rate = SR, "mono", "s16", bitrate
    ctx.options = {"vbr": "off", "frame_duration": "20", "application": "lowdelay", "cutoff": "20000"}
    ctx.open()
    packets = []
    for i in range(0, len(pcm) - 959, 960):
        frame = av.AudioFrame.from_ndarray(pcm[i:i + 960].reshape(1, -1), format="s16", layout="mono")
        frame.sample_rate, frame.pts = SR, i
        packets += [bytes(p) for p in ctx.encode(frame)]
    packets += [bytes(p) for p in ctx.encode(None)]
    return packets


out = {}
for name, amp in (("quiet", 0.012), ("normal", 0.05)):
    for size, bitrate in ((100, 40000), (50, 20000)):
        pk = [p for p in encode(tone(3.0, amp), bitrate) if len(p) == size]
        out[f"{name}{size}"] = [base64.b64encode(p).decode() for p in pk]
print(json.dumps(out))
