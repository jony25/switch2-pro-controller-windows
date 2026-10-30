using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Switch2ProManager.Models;
using Switch2ProManager.Protocol;
using Switch2ProManager.Services;

namespace Switch2ProManager.ViewModels;

public enum ConnectionStatus { Disconnected, Scanning, Connecting, Connected }

public sealed record VibrationSample(string Id, string Name);

public partial class MainViewModel : ObservableObject, IDisposable
{
    private const double StickRadius = 57;   // pixels of dot travel in the test panel

    private readonly ControllerScanner _scanner = new();
    private readonly XboxVirtualPad _pad = new();
    private readonly HapticsService _haptics = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _powerTimer;
    private Switch2Session? _session;
    private CancellationTokenSource? _scanCts;
    private volatile ControllerState? _latest;   // written by the BLE thread, read by the UI thread
    private bool _prevCapture;

    public ObservableCollection<DiscoveredController> Devices { get; } = new();
    public ObservableCollection<ButtonIndicator> Buttons { get; }
    public ObservableCollection<string> LogLines { get; } = new();

    // ── Connection ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ScanButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(ConnectCommand), nameof(DisconnectCommand), nameof(TestHapticsCommand),
        nameof(DumpGattCommand), nameof(CaptureHeadsetCommand), nameof(ProbeAudioCommand), nameof(PlaySampleCommand))]
    private ConnectionStatus _status = ConnectionStatus.Disconnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private DiscoveredController? _selectedDevice;

    public string StatusText => Status switch
    {
        ConnectionStatus.Scanning => "Scanning…",
        ConnectionStatus.Connecting => "Connecting…",
        ConnectionStatus.Connected => "Connected",
        _ => "Disconnected"
    };
    public string ScanButtonText => Status == ConnectionStatus.Scanning ? "Stop scanning" : "Scan for devices";

    // ── Options ──
    [ObservableProperty] private bool _virtualPadEnabled = true;
    [ObservableProperty] private bool _positionalLayout = true;
    [ObservableProperty] private string _padStatus = "Virtual pad inactive";
    [ObservableProperty] private bool _gameRumbleEnabled = true;
    [ObservableProperty] private bool _captureToF12 = true;
    [ObservableProperty] private bool _motionEnabled;
    [ObservableProperty] private string _motionText = "Motion: off";

    // ── HD rumble lab ──
    [ObservableProperty] private bool _labEnabled;
    [ObservableProperty] private double _labHighFreq = HdRumble.HighBandFreq;
    [ObservableProperty] private double _labLowFreq = HdRumble.LowBandFreq;
    [ObservableProperty] private double _labLeftHigh = 120;
    [ObservableProperty] private double _labLeftLow = 120;
    [ObservableProperty] private double _labRightHigh = 120;
    [ObservableProperty] private double _labRightLow = 120;

    public VibrationSample[] Samples { get; } =
    {
        new("1", "1 · low rumble"), new("2", "2 · alarm"), new("3", "3 · soft click"), new("4", "4 · low battery"),
        new("5", "5 · hard click"), new("6", "6 · short beep"), new("7", "7 · high beep"), new("0", "0 · stop"),
    };

    // ── Headset audio research ──
    public string[] ProbeVariants => Switch2Session.ProbeVariantNames;
    [ObservableProperty] private int _probeVariantIndex;
    [ObservableProperty] private bool _probeNormalLevel;

    // ── Test panel ──
    [ObservableProperty] private double _leftDotX;
    [ObservableProperty] private double _leftDotY;
    [ObservableProperty] private double _rightDotX;
    [ObservableProperty] private double _rightDotY;
    [ObservableProperty] private double _zlValue;
    [ObservableProperty] private double _zrValue;
    [ObservableProperty] private double _batteryPercent;
    [ObservableProperty] private string _batteryText = "Battery: —";

    public MainViewModel()
    {
        Buttons = new ObservableCollection<ButtonIndicator>(new (string, SwitchButtons)[]
        {
            ("A", SwitchButtons.A), ("B", SwitchButtons.B), ("X", SwitchButtons.X), ("Y", SwitchButtons.Y),
            ("L", SwitchButtons.L), ("R", SwitchButtons.R), ("ZL", SwitchButtons.ZL), ("ZR", SwitchButtons.ZR),
            ("+", SwitchButtons.Plus), ("−", SwitchButtons.Minus), ("Home", SwitchButtons.Home),
            ("Capture", SwitchButtons.Capture), ("C", SwitchButtons.C), ("L3", SwitchButtons.LThumb), ("R3", SwitchButtons.RThumb),
            ("↑", SwitchButtons.DpadUp), ("↓", SwitchButtons.DpadDown), ("←", SwitchButtons.DpadLeft), ("→", SwitchButtons.DpadRight),
            ("GL", SwitchButtons.GL), ("GR", SwitchButtons.GR),
        }.Select(t => new ButtonIndicator(t.Item1, t.Item2)));

        _scanner.Found += OnDeviceFound;
        _pad.Rumble += (large, small) => _haptics.SetGameRumble(large, small);   // game -> controller
        _haptics.Log += AddLogSafe;
        _uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _uiTimer.Tick += OnUiTick;
        _powerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _powerTimer.Tick += (_, _) => _ = RefreshPowerAsync();
    }

    // ───────────────────────── Commands ─────────────────────────

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (Status == ConnectionStatus.Scanning) { StopScan(); return; }

        Devices.Clear();
        SelectedDevice = null;
        try { await _scanner.StartAsync(); }
        catch (Exception ex) { AddLog($"Scan failed: {ex.Message}"); return; }

        Status = ConnectionStatus.Scanning;
        AddLog("Scanning… hold the controller's SYNC button until the LEDs animate.");
        _scanCts = new CancellationTokenSource();
        try { await Task.Delay(TimeSpan.FromSeconds(30), _scanCts.Token); }
        catch (TaskCanceledException) { return; }
        StopScan();
    }
    private bool CanScan() => Status is ConnectionStatus.Disconnected or ConnectionStatus.Scanning;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var target = SelectedDevice!;
        StopScan();
        Status = ConnectionStatus.Connecting;

        var session = new Switch2Session();
        session.Log += AddLogSafe;
        session.StateReceived += OnStateReceived;
        session.Disconnected += () => Application.Current.Dispatcher.BeginInvoke(() => Teardown("The controller disconnected."));

        try
        {
            await session.ConnectAsync(target, _scanner.HostAddress);
            _session = session;
            _haptics.Start(session);
            _powerTimer.Start();
            _ = RefreshPowerAsync();
            if (MotionEnabled) _ = SetMotionAsync(true);
            if (VirtualPadEnabled) StartPad();
            _uiTimer.Start();
            Status = ConnectionStatus.Connected;
        }
        catch (Exception ex)
        {
            AddLog($"Could not connect: {ex.Message}");
            session.Dispose();
            Status = ConnectionStatus.Disconnected;
        }
    }
    private bool CanConnect() =>
        SelectedDevice is { CanConnect: true } && Status is ConnectionStatus.Disconnected or ConnectionStatus.Scanning;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void Disconnect() => Teardown("Disconnected.");
    private bool CanDisconnect() => Status == ConnectionStatus.Connected;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void TestHaptics() => _haptics.Pulse();

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task PlaySampleAsync(string? id)
    {
        if (_session is null || !byte.TryParse(id, out var sample)) return;
        try { await _session.PlaySampleAsync(sample); }
        catch (Exception ex) { AddLog($"Vibration sample failed: {ex.Message}"); }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task ProbeAudioAsync()
    {
        if (_session is null) return;
        try
        {
            AddLog($"Sending test tone (variant {ProbeVariantIndex + 1}, {(ProbeNormalLevel ? "normal" : "quiet")} level, 3 s)…");
            var tone = ProbeVariantIndex == 14 ? TonePackets.Get50(ProbeNormalLevel) : TonePackets.Get(ProbeNormalLevel);
            AddLog(await _session.ProbeHeadsetOutputAsync(ProbeVariantIndex, tone));
        }
        catch (Exception ex) { AddLog($"Audio probe failed: {ex.Message}"); }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task CaptureHeadsetAsync()
    {
        if (_session is null) return;
        try
        {
            AddLog("Capturing the headset input for 10 s… (talk or tap the mic if you have a headset)");
            var text = await _session.CaptureHeadsetAsync(TimeSpan.FromSeconds(10));
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"switch2_headset_{DateTime.Now:HHmmss}.txt");
            await File.WriteAllTextAsync(path, text);
            AddLog($"Capture saved to {path}");
        }
        catch (Exception ex) { AddLog($"Capture failed: {ex.Message}"); }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DumpGattAsync()
    {
        if (_session is null) return;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "switch2_gatt_dump.txt");
            await File.WriteAllTextAsync(path, await _session.DumpGattAsync());
            AddLog($"GATT dump saved to {path}");
        }
        catch (Exception ex) { AddLog($"GATT dump failed: {ex.Message}"); }
    }

    // ───────────────────────── Logic ─────────────────────────

    private void StopScan()
    {
        _scanCts?.Cancel();
        _scanner.Stop();
        if (Status == ConnectionStatus.Scanning) Status = ConnectionStatus.Disconnected;
    }

    private void OnDeviceFound(DiscoveredController d) =>
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            int i = Devices.ToList().FindIndex(x => x.Address == d.Address);
            if (i < 0) Devices.Add(d);
            else if (Devices[i].Mode != d.Mode)
            {
                bool wasSelected = SelectedDevice?.Address == d.Address;
                Devices[i] = d;
                if (wasSelected) SelectedDevice = d;
            }
            if (SelectedDevice is null && d.CanConnect) SelectedDevice = d;
        });

    // BLE thread: forward to the virtual pad immediately; the UI refreshes separately at 60 FPS.
    private void OnStateReceived(ControllerState s)
    {
        _pad.Submit(s);

        // Controller Capture button -> F12 (rising edge)
        bool capture = s.Has(SwitchButtons.Capture);
        if (capture && !_prevCapture && CaptureToF12) KeyboardInjector.TapF12();
        _prevCapture = capture;

        _latest = s;
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        var s = _latest;
        if (s is null) return;

        foreach (var b in Buttons) b.IsPressed = s.Has(b.Flag);
        LeftDotX = s.LeftX * StickRadius;   LeftDotY = -s.LeftY * StickRadius;
        RightDotX = s.RightX * StickRadius; RightDotY = -s.RightY * StickRadius;
        ZlValue = s.Has(SwitchButtons.ZL) ? 1 : 0;
        ZrValue = s.Has(SwitchButtons.ZR) ? 1 : 0;

        MotionText = !MotionEnabled ? "Motion: off"
            : !s.HasMotion ? "Motion: no data"
            : $"Accel (g)  X {s.AccelX,6:F2}  Y {s.AccelY,6:F2}  Z {s.AccelZ,6:F2}   ·   Gyro (°/s)  X {s.GyroX,7:F1}  Y {s.GyroY,7:F1}  Z {s.GyroZ,7:F1}";
    }

    private async Task RefreshPowerAsync()
    {
        var session = _session;
        if (session is null) return;
        try
        {
            if (await session.ReadPowerInfoAsync() is not { } info) return;   // busy or no report: keep the last value
            int level = Math.Min((info >> 2) & 0x0F, 9);
            bool charging = (info & 0x02) != 0, external = (info & 0x01) != 0;
            BatteryPercent = level / 9.0 * 100;
            BatteryText = $"Battery: level {level}/9" + (charging ? " · charging" : external ? " · external power" : "");
        }
        catch { /* keep the last value */ }
    }

    private async Task SetMotionAsync(bool on)
    {
        var session = _session;
        if (session is null) return;
        try
        {
            await session.SetMotionAsync(on);
            AddLog(on ? "Motion sensors enabled." : "Motion sensors disabled.");
        }
        catch (Exception ex) { AddLog($"Motion: {ex.Message}"); }
    }

    private void Teardown(string reason)
    {
        _uiTimer.Stop();
        _powerTimer.Stop();
        BatteryPercent = 0;
        BatteryText = "Battery: —";
        _haptics.Stop();
        _session?.Dispose();
        _session = null;
        _pad.Stop();
        PadStatus = "Virtual pad inactive";
        _latest = null;
        foreach (var b in Buttons) b.IsPressed = false;
        LeftDotX = LeftDotY = RightDotX = RightDotY = ZlValue = ZrValue = 0;
        LabEnabled = false;
        Status = ConnectionStatus.Disconnected;
        AddLog(reason);
    }

    private void StartPad()
    {
        try
        {
            _pad.PositionalLayout = PositionalLayout;
            _pad.Start();
            PadStatus = "Virtual Xbox 360 pad active";
        }
        catch (Exception ex)
        {
            PadStatus = "ViGEmBus unavailable (is the driver installed?)";
            AddLog($"ViGEm: {ex.Message}");
        }
    }

    partial void OnVirtualPadEnabledChanged(bool value)
    {
        if (Status != ConnectionStatus.Connected) return;
        if (value) StartPad(); else { _pad.Stop(); PadStatus = "Virtual pad disabled"; }
    }

    partial void OnPositionalLayoutChanged(bool value) => _pad.PositionalLayout = value;
    partial void OnGameRumbleEnabledChanged(bool value) => _haptics.GameRumbleEnabled = value;
    partial void OnMotionEnabledChanged(bool value) { if (Status == ConnectionStatus.Connected) _ = SetMotionAsync(value); }

    // Any change to a "Lab*" property is forwarded to the haptics service.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } n && n.StartsWith("Lab", StringComparison.Ordinal))
            _haptics.SetLab(new HapticLabState(LabEnabled, (int)LabHighFreq, (int)LabLowFreq,
                (int)LabLeftHigh, (int)LabLeftLow, (int)LabRightHigh, (int)LabRightLow));
    }

    private void AddLogSafe(string m) => Application.Current.Dispatcher.BeginInvoke(() => AddLog(m));
    private void AddLog(string m)
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {m}");
        while (LogLines.Count > 200) LogLines.RemoveAt(0);
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        _powerTimer.Stop();
        _scanner.Dispose();
        _haptics.Dispose();
        _session?.Dispose();
        _pad.Dispose();
    }
}
