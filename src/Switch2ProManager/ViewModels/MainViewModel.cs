using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
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
    private const double StickRadius = 65;   // pixels of dot travel in the test panel

    private readonly ControllerScanner _scanner = new();
    private readonly XboxVirtualPad _pad = new();
    private readonly HapticsService _haptics = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _powerTimer;
    private Switch2Session? _session;
    private CancellationTokenSource? _scanCts;
    private volatile ControllerState? _latest;
    private volatile ControllerState? _latestRaw;            // before the deadzones and curves (live graph dot)
    private volatile InputMapper _mapper = null!;            // replaced when a mapping setting changes
    private readonly List<int> _mappedKeys = new();          // BLE thread only
    private readonly KeyMouseOutput _keyOutput = new();   // written by the BLE thread, read by the UI thread
    private readonly BatteryAlerts _batteryAlerts = new();
    private readonly AppSettings _settings = SettingsStore.Load();
    private readonly CemuhookServer _dsu = new();
    private readonly DispatcherTimer _staleTimer;
    private readonly Dictionary<ulong, long> _lastSeen = new();   // BLE address -> Environment.TickCount64 of the last advertisement
    private const long StaleAfterMs = 10_000;
    private const long AutoRetryMs = 15_000;
    private bool _autoScanning;        // the scanner is running only for auto-reconnect
    private long _nextAutoAttemptMs;
    private readonly double[] _latencyMs = new double[100];   // ring buffer of app-side submit times (BLE thread writes)
    private int _latencyCount, _latencyNext;
    private readonly object _latencyGate = new();
    private long _nextLatencyUiMs;

    public ObservableCollection<DiscoveredController> Devices { get; } = new();
    public ObservableCollection<ButtonIndicator> Buttons { get; }
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>Short user-facing messages (battery alerts) for the view to show, e.g. as a tray balloon.</summary>
    public event Action<string>? Notification;

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
        ConnectionStatus.Scanning => AppText.Get("Status.Scanning"),
        ConnectionStatus.Connecting => AppText.Get("Status.Connecting"),
        ConnectionStatus.Connected => AppText.Get("Status.Connected"),
        _ => AppText.Get("Status.Disconnected")
    };
    public string ScanButtonText => Status == ConnectionStatus.Scanning ? AppText.Get("Scan.Stop") : AppText.Get("Scan.Start");

    // ── Profiles (docs/UI_SPEC.md, section 9). The controls show the active profile; changes stay until Save. ──
    public ObservableCollection<string> ProfileNames { get; } = new();
    private Profile ActiveProfileData => _settings.Profiles.First(p => p.Name == _settings.ActiveProfile);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand), nameof(BeginRenameProfileCommand), nameof(RenameProfileCommand))]
    private string _activeProfileName = Profile.DefaultName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameProfileCommand))]
    private string _profileNameDraft = Profile.DefaultName;

    private bool _renamingProfile;   // true only while RenameProfile replaces the item in ProfileNames

    [ObservableProperty] private bool _isRenamingProfile;   // shows the name box; only after Rename is pressed

    [RelayCommand(CanExecute = nameof(CanRenameProfile))]
    private void BeginRenameProfile()
    {
        ProfileNameDraft = ActiveProfileName;
        IsRenamingProfile = true;
    }

    [RelayCommand]
    private void CancelRenameProfile()
    {
        ProfileNameDraft = ActiveProfileName;
        IsRenamingProfile = false;
    }

    public string AboutVersion => AppText.Format("About.Version", typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "");

    // Selecting a profile applies its values and saves the choice right away.
    partial void OnActiveProfileNameChanged(string value)
    {
        var profile = _settings.Profiles.FirstOrDefault(p => p.Name == value);
        if (profile is null || _renamingProfile) return;
        ProfileNameDraft = value;
        IsRenamingProfile = false;
        SyncActiveProfile();   // the edits made so far belong to the profile being left
        _settings.ActiveProfile = value;
        ApplyProfile(profile);
        SaveSettings();
    }

    private void ApplyProfile(Profile p)
    {
        PositionalLayout = p.PositionalLayout;
        VirtualPadEnabled = p.VirtualPadEnabled;
        GameRumbleEnabled = p.GameRumbleEnabled;
        RumbleStrength = p.RumbleStrength;
        MotionEnabled = p.MotionEnabled;
        LeftStickEditor.LoadFrom(p.LeftStick);
        RightStickEditor.LoadFrom(p.RightStick);
        LoadButtonRows(p.Buttons);
    }

    private Profile CurrentValues(string name) => new()
    {
        Name = name,
        VirtualPadEnabled = VirtualPadEnabled,
        PositionalLayout = PositionalLayout,
        GameRumbleEnabled = GameRumbleEnabled,
        RumbleStrength = RumbleStrength,
        MotionEnabled = MotionEnabled,
        LeftStick = LeftStickEditor.ToModel(),
        RightStick = RightStickEditor.ToModel(),
        Buttons = ButtonRows.Select(r => r.ToModel()).ToList(),
    };

    [RelayCommand]
    private void NewProfile()
    {
        string name = Enumerable.Range(2, int.MaxValue - 1).Select(i => AppText.Format("Profile.Name", i))
            .First(n => _settings.Profiles.All(p => p.Name != n));
        _settings.Profiles.Add(new Profile { Name = name });   // starts with the default values, mapping included
        ProfileNames.Add(name);
        ActiveProfileName = name;
        AddLog($"Profile \"{name}\" created.");
    }

    [RelayCommand]
    private void SaveProfile()
    {
        SyncActiveProfile();
        SaveSettings();
        AddLog($"Profile \"{ActiveProfileName}\" saved.");
    }

    // Copies the current values into the active profile. Runs on Save, on a profile switch and on exit,
    // so edits made without pressing Save are kept as well.
    private void SyncActiveProfile()
    {
        var index = _settings.Profiles.FindIndex(p => p.Name == _settings.ActiveProfile);
        if (index >= 0) _settings.Profiles[index] = CurrentValues(_settings.ActiveProfile);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private void DeleteProfile()
    {
        var name = ActiveProfileName;
        _settings.Profiles.RemoveAll(p => p.Name == name);
        ProfileNames.Remove(name);
        ActiveProfileName = Profile.DefaultName;   // applies the default values and saves
        AddLog($"Profile \"{name}\" deleted.");
    }
    private bool CanDeleteProfile() => ActiveProfileName != Profile.DefaultName;

    [RelayCommand(CanExecute = nameof(CanApplyRename))]
    private void RenameProfile()
    {
        var oldName = ActiveProfileName;
        var newName = ProfileNameDraft.Trim();
        var profile = _settings.Profiles.First(p => p.Name == oldName);
        profile.Name = newName;
        _settings.ActiveProfile = newName;

        // Replacing the item may clear the selection; the name is set again below, so the setter's side effects are skipped.
        int index = ProfileNames.IndexOf(oldName);
        if (index >= 0) ProfileNames[index] = newName;
        _renamingProfile = true;
        try { ActiveProfileName = newName; }
        finally { _renamingProfile = false; }
        ProfileNameDraft = newName;
        IsRenamingProfile = false;

        SaveSettings();
        AddLog($"Profile \"{oldName}\" renamed to \"{newName}\".");
    }

    private bool CanRenameProfile() => ActiveProfileName != Profile.DefaultName;

    private bool CanApplyRename()
    {
        var name = ProfileNameDraft.Trim();
        return ActiveProfileName != Profile.DefaultName && name.Length > 0 && name != ActiveProfileName
            && _settings.Profiles.All(p => p.Name != name);
    }

    // ── Mapping: stick deadzones and curves, button remap (docs/UI_SPEC.md, sections 6.1 and 6.2). Saved in the profile. ──
    public StickEditor LeftStickEditor { get; } = new();
    public StickEditor RightStickEditor { get; } = new();
    public ObservableCollection<ButtonRow> ButtonRows { get; } = new();

    public ChoiceOption<StickCurve>[] CurveOptions { get; } =
    [
        new(StickCurve.Linear, "Curve.Linear", null),
        new(StickCurve.Exponential, "Curve.Exponential", null),
        new(StickCurve.SCurve, "Curve.SCurve", null),
    ];

    public ChoiceOption<PadTarget>[] TargetOptions { get; } =
    [
        new(PadTarget.Default, "Target.Default", null),
        new(PadTarget.None, "Target.None", null),
        new(PadTarget.A, null, "A"), new(PadTarget.B, null, "B"), new(PadTarget.X, null, "X"), new(PadTarget.Y, null, "Y"),
        new(PadTarget.LB, null, "LB"), new(PadTarget.RB, null, "RB"), new(PadTarget.LT, null, "LT"), new(PadTarget.RT, null, "RT"),
        new(PadTarget.Back, null, "Back"), new(PadTarget.Start, null, "Start"), new(PadTarget.Guide, null, "Guide"),
        new(PadTarget.L3, null, "L3"), new(PadTarget.R3, null, "R3"),
        new(PadTarget.Up, null, "↑"), new(PadTarget.Down, null, "↓"), new(PadTarget.Left, null, "←"), new(PadTarget.Right, null, "→"),
    ];

    public ChoiceOption<MouseOutput>[] MouseOptions { get; } =
    [
        new(MouseOutput.None, "Mouse.None", null),
        new(MouseOutput.Left, "Mouse.Left", null),
        new(MouseOutput.Right, "Mouse.Right", null),
        new(MouseOutput.Middle, "Mouse.Middle", null),
        new(MouseOutput.X1, null, "X1"),
        new(MouseOutput.X2, null, "X2"),
    ];

    [ObservableProperty] private string _mappingWarning = "";
    [ObservableProperty] private ButtonRow? _capturingRow;

    private KeyCapture? _keyCapture;

    [RelayCommand]
    private void BeginKeyCapture(ButtonRow? row)
    {
        EndKeyCapture();
        if (row is null) return;
        row.IsCapturing = true;
        CapturingRow = row;

        _keyCapture = new KeyCapture();
        _keyCapture.Captured += keys =>
        {
            if (CapturingRow is { } target) target.Keys = keys;
            EndKeyCapture();
        };
        _keyCapture.Cancelled += EndKeyCapture;
        _keyCapture.Start();
    }

    private void EndKeyCapture()
    {
        _keyCapture?.Dispose();
        _keyCapture = null;
        if (CapturingRow is { } old) old.IsCapturing = false;
        CapturingRow = null;
    }

    private void RebuildMapping()
    {
        _mapper = new InputMapper(PositionalLayout, LeftStickEditor.ToModel(), RightStickEditor.ToModel(),
            ButtonRows.Select(r => r.ToModel()));
        MappingWarning = BuildMappingWarning();
    }

    private string BuildMappingWarning()
    {
        var lines = new List<string>();
        if (ButtonRows.Any(r => r.Target == PadTarget.Guide)) lines.Add(AppText.Get("Warn.Guide"));
        foreach (var group in ButtonRows.Where(r => r.Keys.Count > 0).GroupBy(r => string.Join("+", r.Keys)).Where(g => g.Count() > 1))
            lines.Add(AppText.Format("Warn.DupKey", group.First().KeyText));
        return string.Join(Environment.NewLine, lines);
    }

    private void LoadButtonRows(IEnumerable<ButtonBinding> bindings)
    {
        var list = bindings.ToList();
        foreach (var row in ButtonRows)
            row.LoadFrom(list.FirstOrDefault(b => b.Input == row.Input) ?? new ButtonBinding { Input = row.Input });
    }


    // ── Language (saved with the other settings on exit) ──
    public LanguageOption[] Languages => AppText.Options;
    [ObservableProperty] private string _language = "en";

    partial void OnLanguageChanged(string value)
    {
        AppText.Apply(value);
        _settings.Language = value;
        RefreshLocalizedText();
    }

    // ── Options ──
    [ObservableProperty] private bool _virtualPadEnabled = true;
    [ObservableProperty] private bool _positionalLayout = true;
    private enum PadState { Inactive, Active, Unavailable, Disabled }
    private PadState _padState = PadState.Inactive;
    public string PadStatus => _padState switch
    {
        PadState.Active => AppText.Get("Pad.Active"),
        PadState.Unavailable => AppText.Get("Pad.Unavailable"),
        PadState.Disabled => AppText.Get("Pad.Disabled"),
        _ => AppText.Get("Pad.Inactive")
    };
    private void SetPadState(PadState state)
    {
        _padState = state;
        OnPropertyChanged(nameof(PadStatus));
    }
    [ObservableProperty] private bool _gameRumbleEnabled = true;
    [ObservableProperty] private double _rumbleStrength = 100;   // percent
    [ObservableProperty] private bool _startWithWindows = StartupService.IsEnabled();
    [ObservableProperty] private bool _autoReconnect;
    [ObservableProperty] private bool _dsuServerEnabled;

    // ── Settings (saved on every change) ──
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private bool _closeToTray = true;
    [ObservableProperty] private bool _trayNotifications = true;
    [ObservableProperty] private bool _motionEnabled;
    [ObservableProperty] private string _motionText = "";

    // ── HD rumble lab ──
    [ObservableProperty] private bool _labEnabled;
    [ObservableProperty] private double _labHighFreq = HdRumble.HighBandFreq;
    [ObservableProperty] private double _labLowFreq = HdRumble.LowBandFreq;
    [ObservableProperty] private double _labLeftHigh = 120;
    [ObservableProperty] private double _labLeftLow = 120;
    [ObservableProperty] private double _labRightHigh = 120;
    [ObservableProperty] private double _labRightLow = 120;

    public VibrationSample[] Samples =>
    [
        new("1", AppText.Get("Sample.1")), new("2", AppText.Get("Sample.2")), new("3", AppText.Get("Sample.3")),
        new("4", AppText.Get("Sample.4")), new("5", AppText.Get("Sample.5")), new("6", AppText.Get("Sample.6")),
        new("7", AppText.Get("Sample.7")), new("0", AppText.Get("Sample.0")),
    ];

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
    private int _batteryLevel = -1;   // -1: no report yet
    private bool _batteryCharging, _batteryExternal;
    public string BatteryText => _batteryLevel < 0 ? AppText.Get("Battery.None")
        : AppText.Format("Battery.Level", _batteryLevel)
          + (_batteryCharging ? AppText.Get("Battery.Charging") : _batteryExternal ? AppText.Get("Battery.External") : "");
    private void SetBattery(int level, bool charging, bool external)
    {
        _batteryLevel = level;
        _batteryCharging = charging;
        _batteryExternal = external;
        OnPropertyChanged(nameof(BatteryText));
    }
    private void ClearBattery() => SetBattery(-1, false, false);
    [ObservableProperty] private bool _padActive;
    [ObservableProperty] private string _leftValueText = "";
    [ObservableProperty] private string _rightValueText = "";
    [ObservableProperty] private string _latencyText = "";

    // Button groups for the test panel (same objects as in Buttons, so one loop updates them all).
    public ButtonIndicator[] FaceButtons { get; private set; } = Array.Empty<ButtonIndicator>();
    public ButtonIndicator[] ShoulderButtons { get; private set; } = Array.Empty<ButtonIndicator>();
    public ButtonIndicator[] SystemButtons { get; private set; } = Array.Empty<ButtonIndicator>();
    public ButtonIndicator[] StickDpadButtons { get; private set; } = Array.Empty<ButtonIndicator>();
    public ButtonIndicator[] PaddleButtons { get; private set; } = Array.Empty<ButtonIndicator>();

    private static string StickText(float x, float y) => $"X {x:+0.00;-0.00;0.00}   Y {y:+0.00;-0.00;0.00}";

    public MainViewModel()
    {
        Buttons = new ObservableCollection<ButtonIndicator>(new (string, SwitchButtons)[]
        {
            ("A", SwitchButtons.A), ("B", SwitchButtons.B), ("X", SwitchButtons.X), ("Y", SwitchButtons.Y),
            ("L", SwitchButtons.L), ("R", SwitchButtons.R), ("ZL", SwitchButtons.ZL), ("ZR", SwitchButtons.ZR),
            ("+", SwitchButtons.Plus), ("−", SwitchButtons.Minus), ("Home", SwitchButtons.Home),
            ("Captura", SwitchButtons.Capture), ("C", SwitchButtons.C), ("L3", SwitchButtons.LThumb), ("R3", SwitchButtons.RThumb),
            ("↑", SwitchButtons.DpadUp), ("↓", SwitchButtons.DpadDown), ("←", SwitchButtons.DpadLeft), ("→", SwitchButtons.DpadRight),
            ("GL", SwitchButtons.GL), ("GR", SwitchButtons.GR),
        }.Select(t => new ButtonIndicator(t.Item1, t.Item2)));

        ButtonIndicator[] Pick(params string[] names) => names.Select(n => Buttons.First(b => b.Id == n)).ToArray();
        FaceButtons = Pick("A", "B", "X", "Y");
        ShoulderButtons = Pick("L", "R", "ZL", "ZR");
        SystemButtons = Pick("−", "+", "Home", "Captura", "C");
        StickDpadButtons = Pick("↑", "↓", "←", "→", "L3", "R3");
        PaddleButtons = Pick("GL", "GR");
        LeftValueText = RightValueText = StickText(0, 0);

        _scanner.Found += OnDeviceFound;
        _pad.Rumble += (large, small) => _haptics.SetGameRumble(large, small);   // game -> controller
        _haptics.Log += AddLogSafe;

        // Restore the saved configuration. Fields are set directly so the change handlers do not run at startup.
        var active = ActiveProfileData;
        _virtualPadEnabled = active.VirtualPadEnabled;
        _positionalLayout = active.PositionalLayout;
        _gameRumbleEnabled = active.GameRumbleEnabled;
        _haptics.GameRumbleEnabled = active.GameRumbleEnabled;
        _rumbleStrength = active.RumbleStrength;
        _haptics.RumbleStrength = (float)(active.RumbleStrength / 100.0);
        _motionEnabled = active.MotionEnabled;
        foreach (var p in _settings.Profiles) ProfileNames.Add(p.Name);
        _activeProfileName = _settings.ActiveProfile;
        _profileNameDraft = _settings.ActiveProfile;

        foreach (var input in ButtonBinding.RemapInputs) ButtonRows.Add(new ButtonRow(input));
        LeftStickEditor.LoadFrom(active.LeftStick);
        RightStickEditor.LoadFrom(active.RightStick);
        LoadButtonRows(active.Buttons);
        RebuildMapping();
        LeftStickEditor.Changed += RebuildMapping;
        RightStickEditor.Changed += RebuildMapping;
        foreach (var row in ButtonRows) row.Changed += RebuildMapping;
        _autoReconnect = _settings.AutoReconnect;
        _dsuServerEnabled = _settings.DsuServerEnabled;
        _minimizeToTray = _settings.MinimizeToTray;
        _closeToTray = _settings.CloseToTray;
        _trayNotifications = _settings.TrayNotifications;
        _language = _settings.Language;
        _batteryAlerts.Alert += (kind, level) =>
        {
            // The log stays in English; only the balloon follows the language.
            AddLog(kind == BatteryAlertKind.Critical
                ? $"Controller battery very low (~10 %, level {level}/9). Charge it soon."
                : $"Controller battery low (~20 %, level {level}/9).");
            Notification?.Invoke(AppText.Format(kind == BatteryAlertKind.Critical ? "Battery.Critical" : "Battery.Low", level));
        };
        _uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _uiTimer.Tick += OnUiTick;
        _powerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _powerTimer.Tick += (_, _) => _ = RefreshPowerAsync();
        _staleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _staleTimer.Tick += (_, _) => PruneStale();
        _staleTimer.Start();
        UpdateAutoScan();   // resumes background scanning if auto-reconnect was on when the app last closed
        _dsu.Log += AddLogSafe;
        if (DsuServerEnabled) StartDsu();
        UpdateLatencyText();
        RefreshMotionText(null);
    }

    // ───────────────────────── Commands ─────────────────────────

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (Status == ConnectionStatus.Scanning) { StopScan(); UpdateAutoScan(); return; }
        _autoScanning = false;   // a manual scan replaces the auto-reconnect watcher

        Devices.Clear();
        _lastSeen.Clear();
        SelectedDevice = null;
        try { await _scanner.StartAsync(); }
        catch (Exception ex) { AddLog($"Scan failed: {ex.Message}"); return; }

        Status = ConnectionStatus.Scanning;
        AddLog("Scanning… hold the controller's SYNC button until the LEDs animate.");
        _scanCts = new CancellationTokenSource();
        try { await Task.Delay(TimeSpan.FromSeconds(30), _scanCts.Token); }
        catch (TaskCanceledException) { return; }
        StopScan();
        UpdateAutoScan();
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
            if (DsuServerEnabled && !MotionEnabled) MotionEnabled = true;
            _dsu.SetController(target.Address, true);
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
            UpdateAutoScan();
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
        _autoScanning = false;
        if (Status == ConnectionStatus.Scanning) Status = ConnectionStatus.Disconnected;
    }

    // Scans in the background only while auto-reconnect is on and the controller is not connected.
    private async void UpdateAutoScan()
    {
        bool want = AutoReconnect && Status == ConnectionStatus.Disconnected && _session is null;
        if (want == _autoScanning) return;
        _autoScanning = want;
        if (!want) { _scanner.Stop(); return; }
        try { await _scanner.StartAsync(); }
        catch (Exception ex) { _autoScanning = false; AddLog($"Auto-reconnect scan failed: {ex.Message}"); }
    }

    // Only ReconnectThisPc is auto-connected: the registration handshake (SYNC) is never run without a manual action.
    private void TryAutoConnect(DiscoveredController d)
    {
        if (!AutoReconnect || !_autoScanning || Status != ConnectionStatus.Disconnected) return;
        if (d.Mode != AdvertisingMode.ReconnectThisPc) return;
        if (Environment.TickCount64 < _nextAutoAttemptMs) return;

        _nextAutoAttemptMs = Environment.TickCount64 + AutoRetryMs;
        AddLog("Controller bonded to this PC found; reconnecting…");
        SelectedDevice = d;
        if (ConnectCommand.CanExecute(null)) ConnectCommand.Execute(null);
    }

    partial void OnAutoReconnectChanged(bool value) => UpdateAutoScan();

    // DSU needs motion data, so enabling it also turns on motion reading while connected.
    partial void OnDsuServerEnabledChanged(bool value)
    {
        if (value) StartDsu(); else _dsu.Stop();
        if (value && Status == ConnectionStatus.Connected && !MotionEnabled) MotionEnabled = true;
    }

    private void StartDsu()
    {
        try { _dsu.Start(); }
        catch (Exception ex)
        {
            AddLog($"DSU server not started (port {CemuhookServer.DefaultPort} in use?): {ex.Message}");
            DsuServerEnabled = false;   // untick the box; the change handler stops the server
        }
    }

    // Saved once, when the app exits (see Dispose). Test tools are not part of the saved configuration.
    private void SaveSettings()
    {
        _settings.Version = AppSettings.CurrentVersion;
        _settings.AutoReconnect = AutoReconnect;
        _settings.DsuServerEnabled = DsuServerEnabled;
        _settings.MinimizeToTray = MinimizeToTray;
        _settings.CloseToTray = CloseToTray;
        _settings.TrayNotifications = TrayNotifications;
        SettingsStore.Save(_settings);
    }

    // Removes controllers that stopped advertising; otherwise a stale entry looks like a live controller.
    private void PruneStale()
    {
        long now = Environment.TickCount64;
        foreach (var d in Devices.ToList())
        {
            if (_lastSeen.TryGetValue(d.Address, out var seen) && now - seen <= StaleAfterMs) continue;
            _lastSeen.Remove(d.Address);
            if (SelectedDevice?.Address == d.Address) SelectedDevice = null;
            Devices.Remove(d);
        }
    }

    private void OnDeviceFound(DiscoveredController d) =>
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _lastSeen[d.Address] = Environment.TickCount64;
            int i = Devices.ToList().FindIndex(x => x.Address == d.Address);
            if (i < 0)
            {
                Devices.Add(d);
                AddLog($"Seen {d.Name} ({d.AddressText}): {d.Mode}, {d.Rssi} dBm");
            }
            else if (Devices[i].Mode != d.Mode)
            {
                bool wasSelected = SelectedDevice?.Address == d.Address;
                Devices[i] = d;
                if (wasSelected) SelectedDevice = d;
                AddLog($"{d.Name} changed mode: {d.Mode}");
            }
            if (SelectedDevice is null && d.CanConnect) SelectedDevice = d;
            TryAutoConnect(d);
        });

    // BLE thread: forward to the virtual pad immediately; the UI refreshes separately at 60 FPS.
    private void OnStateReceived(ControllerState raw)
    {
        var mapper = _mapper;
        var s = mapper.Shape(raw);
        mapper.Map(s, _mappedKeys, out var pad, out var mouse);

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _pad.Submit(s, pad);
        RecordLatency(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        _keyOutput.Apply(_mappedKeys, mouse);
        _dsu.Publish(s);

        _latest = s;
        _latestRaw = raw;
    }

    private void RecordLatency(double ms)
    {
        lock (_latencyGate)
        {
            _latencyMs[_latencyNext] = ms;
            _latencyNext = (_latencyNext + 1) % _latencyMs.Length;
            if (_latencyCount < _latencyMs.Length) _latencyCount++;
        }
    }

    // Median of the last 100 samples, refreshed 4 times per second so the number stays readable.
    private void UpdateLatencyText()
    {
        double median = 0;
        bool any;
        lock (_latencyGate)
        {
            any = _latencyCount > 0;
            if (any)
            {
                var sorted = _latencyMs.Take(_latencyCount).OrderBy(v => v).ToArray();
                median = sorted[sorted.Length / 2];
            }
        }
        LatencyText = any ? AppText.Format("Latency.Value", median) : AppText.Get("Latency.Empty");
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        var s = _latest;
        if (s is null) return;

        if (Environment.TickCount64 >= _nextLatencyUiMs)
        {
            _nextLatencyUiMs = Environment.TickCount64 + 250;
            UpdateLatencyText();
        }

        foreach (var b in Buttons) b.IsPressed = s.Has(b.Flag);
        LeftDotX = s.LeftX * StickRadius;   LeftDotY = -s.LeftY * StickRadius;
        RightDotX = s.RightX * StickRadius; RightDotY = -s.RightY * StickRadius;
        LeftValueText = StickText(s.LeftX, s.LeftY);
        RightValueText = StickText(s.RightX, s.RightY);
        ZlValue = s.Has(SwitchButtons.ZL) ? 1 : 0;
        ZrValue = s.Has(SwitchButtons.ZR) ? 1 : 0;

        if (_latestRaw is { } raw)
        {
            LeftStickEditor.UpdateLive(raw.LeftX, raw.LeftY, s.LeftX, s.LeftY);
            RightStickEditor.UpdateLive(raw.RightX, raw.RightY, s.RightX, s.RightY);
        }

        RefreshMotionText(s);
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
            SetBattery(level, charging, external);
            _batteryAlerts.Update(level, charging || external);
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
        _batteryAlerts.Reset();
        _dsu.SetController(0, false);
        ClearBattery();
        _haptics.Stop();
        _session?.Dispose();
        _session = null;
        _pad.Stop();
        SetPadState(PadState.Inactive);
        PadActive = false;
        _latest = null;
        _latestRaw = null;
        _keyOutput.ReleaseAll();
        LeftStickEditor.ResetLive();
        RightStickEditor.ResetLive();
        foreach (var b in Buttons) b.IsPressed = false;
        LeftDotX = LeftDotY = RightDotX = RightDotY = ZlValue = ZrValue = 0;
        LeftValueText = RightValueText = StickText(0, 0);
        LabEnabled = false;
        lock (_latencyGate) { _latencyCount = 0; _latencyNext = 0; }
        UpdateLatencyText();
        RefreshMotionText(null);
        Status = ConnectionStatus.Disconnected;
        AddLog(reason);
        UpdateAutoScan();
    }

    private void StartPad()
    {
        try
        {
            _pad.Start();
            SetPadState(PadState.Active);
            PadActive = true;
        }
        catch (Exception ex)
        {
            SetPadState(PadState.Unavailable);
            PadActive = false;
            AddLog($"ViGEm: {ex.Message}");
        }
    }

    partial void OnVirtualPadEnabledChanged(bool value)
    {
        if (Status != ConnectionStatus.Connected) return;
        if (value) StartPad(); else { _pad.Stop(); SetPadState(PadState.Disabled); PadActive = false; }
    }

    partial void OnPositionalLayoutChanged(bool value) => RebuildMapping();
    partial void OnGameRumbleEnabledChanged(bool value) => _haptics.GameRumbleEnabled = value;
    partial void OnRumbleStrengthChanged(double value) => _haptics.RumbleStrength = (float)(value / 100.0);

    partial void OnStartWithWindowsChanged(bool value)
    {
        try
        {
            StartupService.Set(value);
            AddLog(value ? "Will start with Windows, minimized to the tray." : "Removed from Windows startup.");
        }
        catch (Exception ex)
        {
            AddLog($"Start with Windows: {ex.Message}");
            SetProperty(ref _startWithWindows, StartupService.IsEnabled(), nameof(StartWithWindows));   // revert the checkbox
        }
    }
    partial void OnMotionEnabledChanged(bool value)
    {
        if (Status == ConnectionStatus.Connected) _ = SetMotionAsync(value);
        RefreshMotionText(_latest);
    }

    private void RefreshMotionText(ControllerState? s) => MotionText = !MotionEnabled ? AppText.Get("Motion.Off")
        : s is not { HasMotion: true } ? AppText.Get("Motion.NoData")
        : AppText.Format("Motion.Values", s.AccelX, s.AccelY, s.AccelZ, s.GyroX, s.GyroY, s.GyroZ);

    // Re-raises the bound texts after a language change. The Lab* values did not change, so the haptics lab
    // must not be told again: _refreshingText makes the override below skip the forwarding.
    private bool _refreshingText;
    private void RefreshLocalizedText()
    {
        _refreshingText = true;
        try
        {
            foreach (var b in Buttons) b.Refresh();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ScanButtonText));
            OnPropertyChanged(nameof(PadStatus));
            OnPropertyChanged(nameof(BatteryText));
            OnPropertyChanged(nameof(Samples));
            OnPropertyChanged(nameof(RumbleStrength));
            OnPropertyChanged(nameof(LabHighFreq));
            OnPropertyChanged(nameof(LabLowFreq));
            OnPropertyChanged(nameof(LabLeftHigh));
            OnPropertyChanged(nameof(LabLeftLow));
            OnPropertyChanged(nameof(LabRightHigh));
            OnPropertyChanged(nameof(LabRightLow));
        }
        finally { _refreshingText = false; }
        LeftStickEditor.Refresh();
        RightStickEditor.Refresh();
        foreach (var row in ButtonRows) row.Refresh();
        foreach (var o in CurveOptions) o.Refresh();
        foreach (var o in TargetOptions) o.Refresh();
        foreach (var o in MouseOptions) o.Refresh();
        OnPropertyChanged(nameof(AboutVersion));
        MappingWarning = BuildMappingWarning();
        RefreshMotionText(_latest);
        UpdateLatencyText();
    }

    // Any change to a "Lab*" property is forwarded to the haptics service.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_refreshingText && e.PropertyName is { } n && n.StartsWith("Lab", StringComparison.Ordinal))
            _haptics.SetLab(new HapticLabState(LabEnabled, (int)LabHighFreq, (int)LabLowFreq,
                (int)LabLeftHigh, (int)LabLeftLow, (int)LabRightHigh, (int)LabRightLow));
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(FileLog.Dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(FileLog.Dir) { UseShellExecute = true });
    }

    // Clears the on-screen list only; the log file on disk is never touched.
    [RelayCommand]
    private void ClearLogView() => LogLines.Clear();

    private void AddLogSafe(string m) => Application.Current.Dispatcher.BeginInvoke(() => AddLog(m));
    private void AddLog(string m)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {m}";
        LogLines.Add(line);
        FileLog.Write(line);
        while (LogLines.Count > 200) LogLines.RemoveAt(0);
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        _powerTimer.Stop();
        SyncActiveProfile();
        SaveSettings();
        _staleTimer.Stop();
        EndKeyCapture();
        _keyOutput.ReleaseAll();
        _scanner.Dispose();
        _dsu.Dispose();
        _haptics.Dispose();
        _session?.Dispose();
        _pad.Dispose();
    }
}
