using System.Buffers.Binary;
using Switch2ProManager.Models;

namespace Switch2ProManager.Protocol;

/// <summary>
/// Decodes notifications of the input characteristic (60-64 bytes, no report-ID byte).
/// Layout (community reverse engineering; same as USB report 0x05 minus the ID):
///   [0..3]    counter / timestamp (u32 LE)
///   [4..7]    buttons (u32 LE), see SwitchButtons
///   [10..12]  left stick: two 12-bit values packed in 3 bytes
///   [13..15]  right stick (same)
///   [31..32]  battery gauge (raw; NOT a voltage)
///   [0x2A..0x3B] motion, only after the IMU feature is enabled (command 0x0C):
///             timestamp u32, temperature i16, accel X,Y,Z i16 (+-8 g), gyro X,Y,Z i16 (+-2000 dps)
/// </summary>
public static class InputReportParser
{
    private const int Center = 2048;      // uncalibrated stick centre
    private const int MaxDelta = 1750;    // approximate usable travel (one user measured radius 1765-1810 on 12 bits)

    public static bool TryParse(ReadOnlySpan<byte> d, out ControllerState state)
    {
        state = null!;
        if (d.Length < 16) return false;

        uint ts = BinaryPrimitives.ReadUInt32LittleEndian(d[..4]);
        var buttons = (SwitchButtons)BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4, 4));

        Unpack12(d.Slice(10, 3), out int lx, out int ly);
        Unpack12(d.Slice(13, 3), out int rx, out int ry);
        // Raw -1..1 values. Deadzones and curves are applied later by the InputMapper (they are per profile).
        float nlx = Norm(lx), nly = Norm(ly), nrx = Norm(rx), nry = Norm(ry);

        int gauge = d.Length >= 33 ? BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(31, 2)) : 0;

        float ax = 0, ay = 0, az = 0, gx = 0, gy = 0, gz = 0;
        bool motion = false;
        if (d.Length >= 0x3C)
        {
            short r0 = BinaryPrimitives.ReadInt16LittleEndian(d.Slice(0x30, 2));
            short r1 = BinaryPrimitives.ReadInt16LittleEndian(d.Slice(0x32, 2));
            short r2 = BinaryPrimitives.ReadInt16LittleEndian(d.Slice(0x34, 2));
            short r3 = BinaryPrimitives.ReadInt16LittleEndian(d.Slice(0x36, 2));
            short r4 = BinaryPrimitives.ReadInt16LittleEndian(d.Slice(0x38, 2));
            short r5 = BinaryPrimitives.ReadInt16LittleEndian(d.Slice(0x3A, 2));
            motion = r0 != 0 || r1 != 0 || r2 != 0 || r3 != 0 || r4 != 0 || r5 != 0;
            const float G = 8.0f / 32767f, Dps = 2000.0f / 32767f;
            ax = r0 * G; ay = r1 * G; az = r2 * G;          // accelerometer first...
            gx = r3 * Dps; gy = r4 * Dps; gz = r5 * Dps;    // ...gyroscope second
        }

        state = new ControllerState(ts, buttons, nlx, nly, nrx, nry, gauge, ax, ay, az, gx, gy, gz, motion);
        return true;
    }

    private static void Unpack12(ReadOnlySpan<byte> b, out int x, out int y)
    {
        int v = b[0] | (b[1] << 8) | (b[2] << 16);
        x = v & 0xFFF;
        y = (v >> 12) & 0xFFF;
    }

    private static float Norm(int raw) => Math.Clamp((raw - Center) / (float)MaxDelta, -1f, 1f);
}
