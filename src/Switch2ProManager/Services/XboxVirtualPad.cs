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

    /// <summary>Called from the BLE thread for every report (low latency, bypasses the UI). pad comes from the InputMapper.</summary>
    public void Submit(ControllerState s, PadOutput pad)
    {
        lock (_gate)
        {
            if (_pad is null) return;

            bool On(PadOutput f) => (pad & f) != 0;
            var p = _pad;

            p.SetButtonState(Xbox360Button.A, On(PadOutput.A));
            p.SetButtonState(Xbox360Button.B, On(PadOutput.B));
            p.SetButtonState(Xbox360Button.X, On(PadOutput.X));
            p.SetButtonState(Xbox360Button.Y, On(PadOutput.Y));

            p.SetButtonState(Xbox360Button.LeftShoulder, On(PadOutput.LB));
            p.SetButtonState(Xbox360Button.RightShoulder, On(PadOutput.RB));
            p.SetButtonState(Xbox360Button.Back, On(PadOutput.Back));
            p.SetButtonState(Xbox360Button.Start, On(PadOutput.Start));
            p.SetButtonState(Xbox360Button.Guide, On(PadOutput.Guide));
            p.SetButtonState(Xbox360Button.LeftThumb, On(PadOutput.L3));
            p.SetButtonState(Xbox360Button.RightThumb, On(PadOutput.R3));
            p.SetButtonState(Xbox360Button.Up, On(PadOutput.Up));
            p.SetButtonState(Xbox360Button.Down, On(PadOutput.Down));
            p.SetButtonState(Xbox360Button.Left, On(PadOutput.Left));
            p.SetButtonState(Xbox360Button.Right, On(PadOutput.Right));

            // Triggers: full or released Xbox trigger (ZL/ZR are digital on this controller)
            p.SetSliderValue(Xbox360Slider.LeftTrigger, (byte)(On(PadOutput.LT) ? 255 : 0));
            p.SetSliderValue(Xbox360Slider.RightTrigger, (byte)(On(PadOutput.RT) ? 255 : 0));

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
