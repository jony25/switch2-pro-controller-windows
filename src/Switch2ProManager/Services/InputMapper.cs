using Switch2ProManager.Models;

namespace Switch2ProManager.Services;

/// <summary>
/// Turns one physical report into what the outputs need: shaped sticks, Xbox buttons, keys and mouse buttons.
/// Immutable: the UI builds a new one when a setting changes, and the BLE thread reads the current one.
/// </summary>
public sealed class InputMapper
{
    private readonly StickSettings _left, _right;
    private readonly (SwitchButtons Input, PadOutput Pad, int[] Keys, MouseOutput Mouse)[] _routes;

    public InputMapper(bool positional, StickSettings left, StickSettings right, IEnumerable<ButtonBinding> bindings)
    {
        _left = left;
        _right = right;
        _routes = bindings
            .Select(b =>
            {
                var target = b.Target == PadTarget.Default ? DefaultTarget(b.Input, positional) : b.Target;
                return (b.Input, ToPad(target), b.Keys.ToArray(), b.Mouse);
            })
            .Where(r => r.Item2 != PadOutput.None || r.Item3.Length > 0 || r.Mouse != MouseOutput.None)
            .ToArray();
    }

    /// <summary>Applies the deadzones and curves of each stick. The result is what the rest of the app sees.</summary>
    public ControllerState Shape(ControllerState s)
    {
        var (lx, ly) = Shape(s.LeftX, s.LeftY, _left);
        var (rx, ry) = Shape(s.RightX, s.RightY, _right);
        return s with { LeftX = lx, LeftY = ly, RightX = rx, RightY = ry };
    }

    /// <summary>Fills the outputs for one shaped report. keys receives the virtual-key codes to hold.</summary>
    public void Map(ControllerState s, List<int> keys, out PadOutput pad, out MouseOutput mouse)
    {
        keys.Clear();
        pad = PadOutput.None;
        mouse = MouseOutput.None;
        foreach (var r in _routes)
        {
            if (!s.Has(r.Input)) continue;
            pad |= r.Pad;
            mouse |= r.Mouse;
            keys.AddRange(r.Keys);
        }
    }

    /// <summary>Output (0 to 1) for an input magnitude (0 to 1). Also used to draw the curve graph.</summary>
    public static double Transfer(double magnitude, StickSettings s)
    {
        double t = (magnitude - s.Inner) / (1 - s.Inner);   // inner deadzone: the rest of the travel is stretched to 0..1
        if (t <= 0) return 0;
        if (s.Outer > 0) t = Math.Min(1, t / (1 - s.Outer)); // outer deadzone: the last part of the travel reaches 100 %
        return s.Curve switch
        {
            StickCurve.Exponential => Math.Pow(t, s.Exponent),
            StickCurve.SCurve => t * t * (3 - 2 * t),
            _ => t,
        };
    }

    private static (float, float) Shape(float x, float y, StickSettings s)
    {
        double m = Math.Sqrt(x * x + y * y);
        if (m <= 0) return (0, 0);
        double o = Transfer(m, s);
        if (o <= 0) return (0, 0);
        double k = o / m;   // keep the direction, change only the magnitude
        return ((float)Math.Clamp(x * k, -1, 1), (float)Math.Clamp(y * k, -1, 1));
    }

    // The built-in layout. Positional: the physical B is Xbox A, A is B, X is Y, Y is X (Nintendo and Xbox positions).
    private static PadTarget DefaultTarget(SwitchButtons input, bool positional) => input switch
    {
        SwitchButtons.A => positional ? PadTarget.B : PadTarget.A,
        SwitchButtons.B => positional ? PadTarget.A : PadTarget.B,
        SwitchButtons.X => positional ? PadTarget.Y : PadTarget.X,
        SwitchButtons.Y => positional ? PadTarget.X : PadTarget.Y,
        SwitchButtons.L => PadTarget.LB,
        SwitchButtons.R => PadTarget.RB,
        SwitchButtons.ZL => PadTarget.LT,
        SwitchButtons.ZR => PadTarget.RT,
        SwitchButtons.Minus => PadTarget.Back,
        SwitchButtons.Plus => PadTarget.Start,
        SwitchButtons.Home => PadTarget.Guide,
        SwitchButtons.LThumb => PadTarget.L3,
        SwitchButtons.RThumb => PadTarget.R3,
        SwitchButtons.DpadUp => PadTarget.Up,
        SwitchButtons.DpadDown => PadTarget.Down,
        SwitchButtons.DpadLeft => PadTarget.Left,
        SwitchButtons.DpadRight => PadTarget.Right,
        _ => PadTarget.None,
    };

    private static PadOutput ToPad(PadTarget t) => t switch
    {
        PadTarget.A => PadOutput.A,
        PadTarget.B => PadOutput.B,
        PadTarget.X => PadOutput.X,
        PadTarget.Y => PadOutput.Y,
        PadTarget.LB => PadOutput.LB,
        PadTarget.RB => PadOutput.RB,
        PadTarget.LT => PadOutput.LT,
        PadTarget.RT => PadOutput.RT,
        PadTarget.Back => PadOutput.Back,
        PadTarget.Start => PadOutput.Start,
        PadTarget.Guide => PadOutput.Guide,
        PadTarget.L3 => PadOutput.L3,
        PadTarget.R3 => PadOutput.R3,
        PadTarget.Up => PadOutput.Up,
        PadTarget.Down => PadOutput.Down,
        PadTarget.Left => PadOutput.Left,
        PadTarget.Right => PadOutput.Right,
        _ => PadOutput.None,
    };
}
