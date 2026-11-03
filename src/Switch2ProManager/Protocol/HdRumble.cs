namespace Switch2ProManager.Protocol;

/// <summary>
/// HD rumble (LRA) encoding of the Pro Controller 2.
/// A 5-byte frame is a 40-bit little-endian value with four 10-bit fields:
///   [0..9] high-band frequency | [10..19] high-band amplitude | [20..29] low-band frequency | [30..39] low-band amplitude
/// BLE packet (characteristic cc483f51-...-05): 0x00 + left block (offset 1) + right block (offset 17).
/// Each 16-byte block = (0x50 | sequence &amp; 0x0F) + the frame repeated 3 times.
/// The frame matches SDL's published example byte for byte (87 c5 21 91 38). The frequency-code to Hz mapping is undocumented.
/// </summary>
public static class HdRumble
{
    public const int HighBandFreq = 0x187;   // frequency codes used by SDL
    public const int LowBandFreq = 0x112;
    public const int MaxAmp = 453;           // 29000/65535 scaled to 10 bits (SDL)

    public readonly record struct Band(int HighFreq, int HighAmp, int LowFreq, int LowAmp);

    public static byte[] EncodeFrame(Band b)
    {
        ulong v = (ulong)(uint)Math.Clamp(b.HighFreq, 0, 1023)
                | ((ulong)Math.Clamp(b.HighAmp, 0, MaxAmp) << 10)
                | ((ulong)Math.Clamp(b.LowFreq, 0, 1023) << 20)
                | ((ulong)Math.Clamp(b.LowAmp, 0, MaxAmp) << 30);
        var f = new byte[5];
        for (int i = 0; i < 5; i++) f[i] = (byte)(v >> (8 * i));
        return f;
    }

    public static byte[] BuildBlePayload(Band left, Band right, byte seq)
    {
        var p = new byte[33];
        int o = 1;
        foreach (var band in new[] { left, right })
        {
            var frame = EncodeFrame(band);
            p[o] = (byte)(0x50 | (seq & 0x0F));
            for (int r = 0; r < 3; r++) frame.CopyTo(p, o + 1 + r * 5);
            o += 16;
        }
        return p;
    }

    public static byte[] BuildBlePayload(int highAmp, int lowAmp, byte seq)
    {
        var b = new Band(HighBandFreq, highAmp, LowBandFreq, lowAmp);
        return BuildBlePayload(b, b, seq);
    }
}
