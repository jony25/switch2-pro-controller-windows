using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Switch2ProManager.Models;

namespace Switch2ProManager.Services;

/// <summary>
/// Translates the controller state into a virtual Xbox 360 pad (ViGEmBus) so any XInput game recognises it.
/// Requires the ViGEmBus driver. Rumble requested by games is surfaced through the Rumble event.
/// </summary>
public sealed class XboxVirtualPad : IDisposable
{
    private readonly object _gate = new();
    private ViGEmClient? _client;
    private IXbox360Controller? _pad;

    /// <summary>true: physical position (Nintendo B, bottom, becomes Xbox A). false: same letter (A to A).</summary>
    public bool PositionalLayout { get; set; } = true;

    public bool IsRunning => _pad is not null;

    /// <summary>Rumble requested by the game: (large motor, small motor), 0..255.</summary>
    public event Action<byte, byte>? Rumble;

    public void Start()
    {
        lock (_gate)
        {
            if (_pad is not null) return;
            _client = new ViGEmClient();                    // throws VigemBusNotFoundException if the driver is missing
            _pad = _client.CreateXbox360Controller();
            _pad.AutoSubmitReport = false;                  // one atomic report per update
            _pad.FeedbackReceived += (_, e) => Rumble?.Invoke(e.LargeMotor, e.SmallMotor);
            _pad.Connect();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _pad?.Disconnect(); } catch { }
            _pad = null;
            _client?.Dispose();
            _client = null;
        }
    }

    /// <summary>Called from the BLE thread for every report (low latency, bypasses the UI).</summary>
    public void Submit(ControllerState s)
    {
        lock (_gate)
        {
            if (_pad is null) return;

            bool Down(SwitchButtons b) => s.Has(b);
            var p = _pad;

            p.SetButtonState(Xbox360Button.A, PositionalLayout ? Down(SwitchButtons.B) : Down(SwitchButtons.A));
            p.SetButtonState(Xbox360Button.B, PositionalLayout ? Down(SwitchButtons.A) : Down(SwitchButtons.B));
            p.SetButtonState(Xbox360Button.X, PositionalLayout ? Down(SwitchButtons.Y) : Down(SwitchButtons.X));
            p.SetButtonState(Xbox360Button.Y, PositionalLayout ? Down(SwitchButtons.X) : Down(SwitchButtons.Y));

            p.SetButtonState(Xbox360Button.LeftShoulder, Down(SwitchButtons.L));
            p.SetButtonState(Xbox360Button.RightShoulder, Down(SwitchButtons.R));
            p.SetButtonState(Xbox360Button.Back, Down(SwitchButtons.Minus));
            p.SetButtonState(Xbox360Button.Start, Down(SwitchButtons.Plus));
            p.SetButtonState(Xbox360Button.Guide, Down(SwitchButtons.Home));
            p.SetButtonState(Xbox360Button.LeftThumb, Down(SwitchButtons.LThumb));
            p.SetButtonState(Xbox360Button.RightThumb, Down(SwitchButtons.RThumb));
            p.SetButtonState(Xbox360Button.Up, Down(SwitchButtons.DpadUp));
            p.SetButtonState(Xbox360Button.Down, Down(SwitchButtons.DpadDown));
            p.SetButtonState(Xbox360Button.Left, Down(SwitchButtons.DpadLeft));
            p.SetButtonState(Xbox360Button.Right, Down(SwitchButtons.DpadRight));

            // ZL/ZR are digital on this controller: full or released Xbox trigger
            p.SetSliderValue(Xbox360Slider.LeftTrigger, (byte)(Down(SwitchButtons.ZL) ? 255 : 0));
            p.SetSliderValue(Xbox360Slider.RightTrigger, (byte)(Down(SwitchButtons.ZR) ? 255 : 0));

            // XInput: up = +32767
            p.SetAxisValue(Xbox360Axis.LeftThumbX, ToAxis(s.LeftX));
            p.SetAxisValue(Xbox360Axis.LeftThumbY, ToAxis(s.LeftY));
            p.SetAxisValue(Xbox360Axis.RightThumbX, ToAxis(s.RightX));
            p.SetAxisValue(Xbox360Axis.RightThumbY, ToAxis(s.RightY));

            p.SubmitReport();
        }
    }

    private static short ToAxis(float v) => (short)Math.Clamp((int)MathF.Round(v * 32767f), -32768, 32767);

    public void Dispose() => Stop();
}
