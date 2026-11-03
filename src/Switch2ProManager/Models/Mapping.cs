using System.Text.Json.Serialization;

namespace Switch2ProManager.Models;

/// <summary>Where a physical input goes on the virtual Xbox 360 pad. Default is the built-in layout (positional or not).</summary>
public enum PadTarget { Default, None, A, B, X, Y, LB, RB, LT, RT, Back, Start, Guide, L3, R3, Up, Down, Left, Right }

/// <summary>Xbox 360 outputs as bit flags: several physical inputs can press the same output.</summary>
[Flags]
public enum PadOutput : uint
{
    None = 0,
    A = 1, B = 2, X = 4, Y = 8, LB = 16, RB = 32, LT = 64, RT = 128,
    Back = 256, Start = 512, Guide = 1024, L3 = 2048, R3 = 4096,
    Up = 8192, Down = 16384, Left = 32768, Right = 65536,
}

[Flags]
public enum MouseOutput { None = 0, Left = 1, Right = 2, Middle = 4, X1 = 8, X2 = 16 }

public enum StickCurve { Linear, Exponential, SCurve }

/// <summary>Deadzones and response curve of one stick. Values are fractions of the stick travel (0.05 = 5 %).</summary>
public sealed class StickSettings
{
    public double Inner { get; set; } = 0.05;      // 0 to 0.30: below it the stick reads 0
    public double Outer { get; set; } = 0;         // 0 to 0.20: above 1 - Outer the stick reads 100 %
    public StickCurve Curve { get; set; } = StickCurve.Linear;
    public double Exponent { get; set; } = 2.0;    // 1.0 to 3.0, used by the exponential curve
}

/// <summary>What one physical input does besides its normal action.</summary>
public sealed class ButtonBinding
{
    /// <summary>The physical inputs shown in the remap table, in table order.</summary>
    public static readonly SwitchButtons[] RemapInputs =
    {
        SwitchButtons.GL, SwitchButtons.GR, SwitchButtons.A, SwitchButtons.B, SwitchButtons.X, SwitchButtons.Y,
        SwitchButtons.L, SwitchButtons.R, SwitchButtons.ZL, SwitchButtons.ZR, SwitchButtons.Minus, SwitchButtons.Plus,
        SwitchButtons.Home, SwitchButtons.Capture, SwitchButtons.C, SwitchButtons.LThumb, SwitchButtons.RThumb,
        SwitchButtons.DpadUp, SwitchButtons.DpadDown, SwitchButtons.DpadLeft, SwitchButtons.DpadRight,
    };

    public SwitchButtons Input { get; set; }
    public PadTarget Target { get; set; } = PadTarget.Default;

    /// <summary>Windows virtual-key codes held together while the input is pressed, modifiers first (for example Alt then Tab).</summary>
    public List<int> Keys { get; set; } = new();

    /// <summary>Version 1 of the remap table stored one key. Read only, moved to Keys when the settings load.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Key { get; set; }

    public MouseOutput Mouse { get; set; }

    public static List<ButtonBinding> Defaults() => RemapInputs.Select(i => new ButtonBinding { Input = i }).ToList();
}
