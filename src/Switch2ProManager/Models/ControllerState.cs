namespace Switch2ProManager.Models;

/// <summary>Button bit masks of the Pro Controller 2 input report (u32 little-endian at payload offset 4).</summary>
[Flags]
public enum SwitchButtons : uint
{
    None = 0,
    Y = 0x00000001, X = 0x00000002, B = 0x00000004, A = 0x00000008,
    SrR = 0x00000010, SlR = 0x00000020, R = 0x00000040, ZR = 0x00000080,
    Minus = 0x00000100, Plus = 0x00000200, RThumb = 0x00000400, LThumb = 0x00000800,
    Home = 0x00001000, Capture = 0x00002000, C = 0x00004000,
    DpadDown = 0x00010000, DpadUp = 0x00020000, DpadRight = 0x00040000, DpadLeft = 0x00080000,
    SrL = 0x00100000, SlL = 0x00200000, L = 0x00400000, ZL = 0x00800000,
    GR = 0x01000000, // right back paddle
    GL = 0x02000000, // left back paddle
}

/// <summary>Normalised, immutable snapshot of one input report (safe to hand between threads).</summary>
public sealed record ControllerState(
    uint Timestamp,
    SwitchButtons Buttons,
    float LeftX, float LeftY,     // -1..1 (up = +1)
    float RightX, float RightY,   // -1..1 (up = +1)
    int GaugeRaw,                 // raw battery gauge (offset 0x1F). NOT a voltage: do not convert to a percentage.
    float AccelX = 0, float AccelY = 0, float AccelZ = 0,   // g   (+Z out of the front face, +Y towards USB-C, +X to the player's right)
    float GyroX = 0, float GyroY = 0, float GyroZ = 0,      // deg/s (right-hand rule)
    bool HasMotion = false)
{
    public bool Has(SwitchButtons flag) => (Buttons & flag) == flag;
}
