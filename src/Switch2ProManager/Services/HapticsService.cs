using Switch2ProManager.Protocol;

namespace Switch2ProManager.Services;

/// <summary>State of the HD rumble "lab" (raw values of the 10-bit fields).</summary>
public sealed record HapticLabState(bool Enabled, int HighFreq, int LowFreq, int LeftHigh, int LeftLow, int RightHigh, int RightLow);

/// <summary>
/// Turns game rumble (via the virtual Xbox pad), the test pulse and the lab sliders into HD rumble,
/// sent every 30 ms (about the BLE connection interval: writing faster only queues frames).
/// </summary>
public sealed class HapticsService : IDisposable
{
    private CancellationTokenSource? _cts;
    private volatile byte _gameLarge, _gameSmall;
    private long _pulseUntil;
    private volatile HapticLabState? _lab;
    private volatile float _strength = 1f;

    public event Action<string>? Log;

    public bool GameRumbleEnabled { get; set; } = true;

    /// <summary>Global intensity 0..1, applied to game rumble, the test pulse and the lab. Built-in samples are not scaled.</summary>
    public float RumbleStrength
    {
        get => _strength;
        set => _strength = Math.Clamp(value, 0f, 1f);
    }

    private int Scale(int amplitude) => (int)(amplitude * _strength);

    public void SetLab(HapticLabState state) => _lab = state;

    public void SetGameRumble(byte large, byte small) { _gameLarge = large; _gameSmall = small; }

    public void Pulse() => Interlocked.Exchange(ref _pulseUntil, Environment.TickCount64 + 350);

    public void Start(Switch2Session session)
    {
        Stop();
        if (!session.SupportsHaptics) { Log?.Invoke("This controller did not expose the vibration characteristic."); return; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => LoopAsync(session, ct));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _gameLarge = _gameSmall = 0;
    }

    private async Task LoopAsync(Switch2Session session, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(30));
        bool wasActive = false;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // Lab has priority and allows different frames per actuator.
                var lab = _lab;
                if (lab is { Enabled: true })
                {
                    var l = new HdRumble.Band(lab.HighFreq, Scale(lab.LeftHigh), lab.LowFreq, Scale(lab.LeftLow));
                    var r = new HdRumble.Band(lab.HighFreq, Scale(lab.RightHigh), lab.LowFreq, Scale(lab.RightLow));
                    try { await session.SendHapticsAsync(l, r); }
                    catch (Exception ex) { Log?.Invoke($"Rumble: {ex.Message}"); }
                    wasActive = true;      // a final "zero" frame is sent once the lab is disabled
                    continue;
                }

                float lo = 0, hi = 0;
                if (GameRumbleEnabled) { lo = _gameLarge / 255f; hi = _gameSmall / 255f; }
                if (Environment.TickCount64 < Interlocked.Read(ref _pulseUntil))
                {
                    lo = MathF.Max(lo, 0.6f);
                    hi = MathF.Max(hi, 0.6f);
                }

                int loA = Scale((int)(Math.Clamp(lo, 0f, 1f) * HdRumble.MaxAmp));
                int hiA = Scale((int)(Math.Clamp(hi, 0f, 1f) * HdRumble.MaxAmp));
                bool active = loA > 0 || hiA > 0;

                // Repeat the frame while active; send one last zero frame to stop.
                if (active || wasActive)
                {
                    try { await session.SendHapticsAsync(hiA, loA); }
                    catch (Exception ex) { Log?.Invoke($"Rumble: {ex.Message}"); }
                }
                wasActive = active;
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose() => Stop();
}
