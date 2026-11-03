using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Switch2ProManager.Models;
using Switch2ProManager.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Switch2ProManager.Services;

/// <summary>
/// GATT connection to the controller: host-registration handshake, player LED, input stream, rumble, motion,
/// battery, and the headset-audio research helpers.
/// </summary>
public sealed class Switch2Session : IDisposable
{
    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattCharacteristic? _input, _cmdWrite, _cmdResponse, _vibration, _headsetIn, _headsetOut;
    private IReadOnlyList<GattDeviceService>? _services;
    private TaskCompletionSource<byte[]>? _pendingResponse;
    private object? _connParamsRequest;                       // keep the reference for the lifetime of the connection
    private readonly SemaphoreSlim _headsetGate = new(1, 1);  // one user of the headset characteristic's CCCD at a time
    private byte _rumbleSeq;
    private volatile bool _closed;

    public event Action<ControllerState>? StateReceived;
    public event Action? Disconnected;
    public event Action<string>? Log;

    public bool SupportsHaptics => _vibration is not null;

    // ───────────────────────── Connection ─────────────────────────

    public async Task ConnectAsync(DiscoveredController target, ulong hostAddress)
    {
        Log?.Invoke("Opening BLE device…");
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(target.Address)
            ?? throw new InvalidOperationException("Windows could not open the BLE device.");
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        // Keep the GATT link alive even without traffic.
        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
        _session.MaintainConnection = true;

        // Windows 11: ask for connection parameters optimised for throughput / low latency.
        try
        {
            _connParamsRequest = _device.RequestPreferredConnectionParameters(
                BluetoothLEPreferredConnectionParameters.ThroughputOptimized);
        }
        catch (Exception ex) { Log?.Invoke($"(Connection parameters not applied: {ex.Message})"); }

        // GATT discovery
        var services = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Could not read GATT services ({services.Status}). " +
                "Is Steam running? Steam can grab the controller's BLE connection.");

        _services = services.Services;
        foreach (var svc in services.Services)
        {
            var chars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (chars.Status != GattCommunicationStatus.Success) continue;
            foreach (var c in chars.Characteristics)
            {
                Log?.Invoke($"GATT {svc.Uuid} -> {c.Uuid}");   // handy to diagnose other firmware versions
                if (c.Uuid == Switch2Protocol.InputReportChar) _input = c;
                else if (c.Uuid == Switch2Protocol.CommandWriteChar) _cmdWrite = c;
                else if (c.Uuid == Switch2Protocol.CommandResponseChar) _cmdResponse = c;
                else if (c.Uuid == Switch2Protocol.VibrationChar) _vibration = c;
                else if (c.Uuid == Switch2Protocol.HeadsetInputChar) _headsetIn = c;
                else if (c.Uuid == Switch2Protocol.HeadsetOutputChar) _headsetOut = c;
            }
        }
        if (_input is null || _cmdWrite is null)
            throw new InvalidOperationException("The Switch 2 protocol characteristics were not found.");

        if (_cmdResponse is not null)
        {
            _cmdResponse.ValueChanged += OnCommandResponse;
            await _cmdResponse.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
        }

        // Host-registration handshake, only when the controller was in SYNC (pairing) mode.
        // WARNING: this stores this PC's address/key in the controller and can replace the console's registration.
        if (target.Mode == AdvertisingMode.Pairing)
        {
            Log?.Invoke("Registering this PC on the controller (handshake)…");
            await PairAsync(hostAddress);
        }

        try { await SendAsync(Switch2Protocol.CmdLeds, Switch2Protocol.SubLedsSetPlayer, new byte[] { 0x01, 0, 0, 0 }); }
        catch (Exception ex) { Log?.Invoke($"(Player LED not set: {ex.Message})"); }

        // Input reports: GATT notifications at ~250 Hz
        _input.ValueChanged += OnInput;
        var status = await _input.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Could not subscribe to input reports ({status}).");

        Log?.Invoke("Receiving input reports.");
    }

    /// <summary>Host registration: this PC's address (twice) + two fixed payloads + finish.</summary>
    private async Task PairAsync(ulong hostAddress)
    {
        var mac = new byte[6];
        for (int i = 0; i < 6; i++) mac[i] = (byte)(hostAddress >> (8 * i));   // little-endian
        byte[] setMac = [0x00, 0x02, .. mac, .. mac];

        await SendAsync(Switch2Protocol.CmdPair, Switch2Protocol.SubPairSetMac, setMac);
        await Task.Delay(40);
        await SendAsync(Switch2Protocol.CmdPair, Switch2Protocol.SubPairStep1, Switch2Protocol.PairingStep1);
        await Task.Delay(40);
        await SendAsync(Switch2Protocol.CmdPair, Switch2Protocol.SubPairStep2, Switch2Protocol.PairingStep2);
        await Task.Delay(40);
        await SendAsync(Switch2Protocol.CmdPair, Switch2Protocol.SubPairFinish, new byte[] { 0x00 });
    }

    /// <summary>Writes a command (write without response) and waits up to 1 s for the notification reply.</summary>
    private async Task<byte[]?> SendAsync(byte cmd, byte sub, byte[] data)
    {
        var frame = Switch2Protocol.BuildFrame(cmd, sub, data);
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResponse = tcs;

        var writer = new DataWriter();
        writer.WriteBytes(frame);
        var result = await _cmdWrite!.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
        if (result.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Command 0x{cmd:X2}/0x{sub:X2} write failed: {result.Status}");

        var done = await Task.WhenAny(tcs.Task, Task.Delay(1000));
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    // ───────────────────────── Features ─────────────────────────

    /// <summary>
    /// Enables/disables the IMU with the same sequence the console uses (command 0x0C: set mask 0x2F, then enable).
    /// </summary>
    public async Task SetMotionAsync(bool enable)
    {
        byte[] mask = { 0x2F, 0, 0, 0 };
        if (enable)
        {
            await SendAsync(Switch2Protocol.CmdFeatureSelect, 0x02, mask);
            await SendAsync(Switch2Protocol.CmdFeatureSelect, 0x04, mask);
        }
        else await SendAsync(Switch2Protocol.CmdFeatureSelect, 0x05, new byte[] { 0x04, 0, 0, 0 });
    }

    /// <summary>Plays a built-in vibration sample (command 0x0A/0x02, as used by the console). 0 stops.</summary>
    public Task PlaySampleAsync(byte sampleId) =>
        SendAsync(Switch2Protocol.CmdVibration, 0x02, new byte[] { sampleId, 0, 0, 0 });

    /// <summary>HD rumble with separate frames for the left and right actuators.</summary>
    public async Task SendHapticsAsync(HdRumble.Band left, HdRumble.Band right)
    {
        if (_vibration is null || _closed) return;
        await WriteVibrationAsync(HdRumble.BuildBlePayload(left, right, _rumbleSeq++));
    }

    /// <summary>HD rumble with the default frequencies and the same amplitudes on both actuators.</summary>
    public async Task SendHapticsAsync(int highBandAmp, int lowBandAmp)
    {
        if (_vibration is null || _closed) return;
        await WriteVibrationAsync(HdRumble.BuildBlePayload(highBandAmp, lowBandAmp, _rumbleSeq++));
    }

    private async Task WriteVibrationAsync(byte[] payload)
    {
        var w = new DataWriter();
        w.WriteBytes(payload);
        await _vibration!.WriteValueAsync(w.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
    }

    /// <summary>
    /// Reads the "Power Info" byte of the 0x09-style report carried by the headset characteristic (…c0f9):
    /// bit0 = external power, bit1 = charging, bits 2..5 = battery level 0-9.
    /// A direct GATT read of that characteristic fails, so this subscribes briefly, takes the first report and unsubscribes.
    /// Returns null if the characteristic is busy (capture/probe running) or no report arrived.
    /// </summary>
    public async Task<byte?> ReadPowerInfoAsync()
    {
        var c = _headsetIn;
        if (c is null || _closed || !await _headsetGate.WaitAsync(0)) return null;

        var tcs = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReport(GattCharacteristic s, GattValueChangedEventArgs e)
        {
            if (e.CharacteristicValue.Length < 2) return;
            var b = new byte[e.CharacteristicValue.Length];
            DataReader.FromBuffer(e.CharacteristicValue).ReadBytes(b);
            tcs.TrySetResult(b[1]);
        }

        c.ValueChanged += OnReport;
        try
        {
            var st = await c.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (st != GattCommunicationStatus.Success) return null;
            var done = await Task.WhenAny(tcs.Task, Task.Delay(1500));
            return done == tcs.Task ? (byte?)tcs.Task.Result : null;
        }
        finally
        {
            c.ValueChanged -= OnReport;
            try { await c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None); } catch { }
            _headsetGate.Release();
        }
    }

    // ───────────────────────── Research helpers ─────────────────────────

    /// <summary>
    /// Read-only dump of the GATT table (services, characteristics, properties, descriptors and readable values).
    /// Writes nothing to the controller.
    /// </summary>
    public async Task<string> DumpGattAsync()
    {
        var sb = new StringBuilder();
        if (_services is null) return "(no GATT services)";
        foreach (var svc in _services)
        {
            sb.AppendLine($"SERVICE {svc.Uuid}");
            var chars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (chars.Status != GattCommunicationStatus.Success) { sb.AppendLine($"  (error {chars.Status})"); continue; }
            foreach (var c in chars.Characteristics)
            {
                sb.AppendLine($"  CHAR {c.Uuid}  handle=0x{c.AttributeHandle:X4}  props={c.CharacteristicProperties}");
                if (c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
                {
                    try
                    {
                        var r = await c.ReadValueAsync(BluetoothCacheMode.Uncached);
                        if (r.Status == GattCommunicationStatus.Success)
                        {
                            var b = new byte[r.Value.Length];
                            DataReader.FromBuffer(r.Value).ReadBytes(b);
                            string ascii = new string(b.Select(x => x is >= 32 and < 127 ? (char)x : '.').ToArray());
                            sb.AppendLine($"    value={Convert.ToHexString(b)}  ascii=\"{ascii}\"");
                        }
                    }
                    catch (Exception ex) { sb.AppendLine($"    (read failed: {ex.Message})"); }
                }
                var ds = await c.GetDescriptorsAsync(BluetoothCacheMode.Uncached);
                if (ds.Status == GattCommunicationStatus.Success)
                    foreach (var d in ds.Descriptors) sb.AppendLine($"    DESC {d.Uuid}");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Passive capture: subscribes to the headset characteristic for <paramref name="duration"/> and returns every report
    /// as hex with a timestamp. Report layout (112 bytes): [0] counter, [1] power info, [0x0D] jack state,
    /// [0x0E] audio length (0x32), [0x0F..0x40] 50 bytes of Opus data. Writes nothing to the controller.
    /// </summary>
    public async Task<string> CaptureHeadsetAsync(TimeSpan duration)
    {
        if (_headsetIn is null)
            throw new InvalidOperationException("This controller does not expose the headset characteristic (firmware < 2.0.0?).");

        await _headsetGate.WaitAsync();
        var lines = new List<string>();
        var sw = Stopwatch.StartNew();
        void OnHeadset(GattCharacteristic s, GattValueChangedEventArgs e)
        {
            var b = new byte[e.CharacteristicValue.Length];
            DataReader.FromBuffer(e.CharacteristicValue).ReadBytes(b);
            string state = b.Length > 0x0D ? $"0x{b[0x0D]:X2}" : "--";
            lock (lines) lines.Add($"{sw.ElapsedMilliseconds,6} ms  len={b.Length,3}  state={state}  {Convert.ToHexString(b)}");
        }

        _headsetIn.ValueChanged += OnHeadset;
        try
        {
            var st = await _headsetIn.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (st != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"Could not subscribe to the headset characteristic ({st}).");
            await Task.Delay(duration);
        }
        finally
        {
            _headsetIn.ValueChanged -= OnHeadset;
            try { await _headsetIn.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None); } catch { }
            _headsetGate.Release();
        }

        lock (lines)
            return $"# passive capture of {Switch2Protocol.HeadsetInputChar} - {lines.Count} reports in {duration.TotalSeconds:F0} s\n" + string.Join("\n", lines);
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    public static readonly string[] ProbeVariantNames =
    {
        "1 · two raw 50-byte writes",
        "2 · one raw 100-byte write",
        "3 · [state, 0x32] + 50 bytes, twice (mirrors the mic report)",
        "4 · 0x00 + 100 bytes",
        "5 · [0x00, state 0F/07, 0x32] + 50 bytes, twice",
        "6 · like 5, states 0D/05 (headphones)",
        "7 · like 5, states 09/01",
        "8 · like 5, states 0B/03",
        "9 · like 5, states 0C/04",
        "10 · like 5, states 08/00",
        "11 · like 5, states 0E/06",
        "12 · single block: [0x00, 0x0F, 0x64] + 100 bytes",
        "13 · like 5 with pre-buffering (5 packets at once)",
        "14 · like 5 with a counter in the first byte",
        "15 · standalone 50-byte packets (20 kbps)",
        "16 · CONTROL: structure 5 with zeroed data",
        "17 · CONTROL: structure 5 with a valid Opus silence packet (F8 FF FE)",
    };

    // (first block, second block) state bytes for variants 5..11
    private static readonly (byte first, byte second)[] ProbeStates =
    {
        (0x0F, 0x07), (0x0D, 0x05), (0x09, 0x01), (0x0B, 0x03), (0x0C, 0x04), (0x08, 0x00), (0x0E, 0x06),
    };

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();

    /// <summary>Plausible (undocumented) ways of handing a 100-byte Opus packet to the headset output characteristic.</summary>
    private static IEnumerable<byte[]> BuildAudioWrites(int variant, byte[] p, int index)
    {
        // Finding: the 2nd byte of the write is a control byte whose bit 0 enables playback;
        // the structure that triggers playback is [0x00][state][0x32][50 bytes].
        switch (variant)
        {
            case 0: yield return p[..50]; yield return p[50..]; break;
            case 1: yield return p; break;
            case 2: yield return Cat(new byte[] { 0x0F, 0x32 }, p[..50]); yield return Cat(new byte[] { 0x07, 0x32 }, p[50..]); break;
            case 3: yield return Cat(new byte[] { 0x00 }, p); break;
            case 11: yield return Cat(new byte[] { 0x00, 0x0F, 0x64 }, p); break;
            case 13:
                yield return Cat(new byte[] { (byte)(index * 2), 0x0F, 0x32 }, p[..50]);
                yield return Cat(new byte[] { (byte)(index * 2 + 1), 0x07, 0x32 }, p[50..]);
                break;
            case 14: yield return Cat(new byte[] { 0x00, 0x0F, 0x32 }, p); break;     // p is a standalone 50-byte packet
            case 15:
            case 16:
                var q = new byte[100];
                if (variant == 16) { q[0] = 0xF8; q[1] = 0xFF; q[2] = 0xFE; }
                yield return Cat(new byte[] { 0x00, 0x0F, 0x32 }, q[..50]);
                yield return Cat(new byte[] { 0x00, 0x07, 0x32 }, q[50..]);
                break;
            default:                                                                    // variants 5..11 and 13
                var st = variant == 12 ? ProbeStates[0] : ProbeStates[Math.Clamp(variant - 4, 0, ProbeStates.Length - 1)];
                yield return Cat(new byte[] { 0x00, st.first, 0x32 }, p[..50]);
                yield return Cat(new byte[] { 0x00, st.second, 0x32 }, p[50..]);
                break;
        }
    }

    /// <summary>
    /// EXPERIMENT: sends an Opus tone to the headset OUTPUT characteristic (…b06), one packet every 20 ms.
    /// The format of that characteristic is undocumented, so several layouts are tried (see docs/AUDIO_RESEARCH.md).
    /// While sending, the jack-state byte of the input characteristic is observed. Writes only to …b06.
    /// </summary>
    public async Task<string> ProbeHeadsetOutputAsync(int variant, byte[][] packets)
    {
        if (_headsetOut is null)
            throw new InvalidOperationException("This controller does not expose the headset output characteristic (firmware < 2.0.0?).");

        await _headsetGate.WaitAsync();
        var states = new SortedSet<byte>();
        void OnHeadset(GattCharacteristic s, GattValueChangedEventArgs e)
        {
            var buf = new byte[e.CharacteristicValue.Length];
            DataReader.FromBuffer(e.CharacteristicValue).ReadBytes(buf);
            if (buf.Length > 0x0D) lock (states) states.Add(buf[0x0D]);
        }

        bool subscribed = false;
        int ok = 0;
        string? error = null;
        timeBeginPeriod(1);   // 1 ms timer resolution to hold the 20 ms packet cadence
        try
        {
            if (_headsetIn is not null)
            {
                _headsetIn.ValueChanged += OnHeadset;
                subscribed = await _headsetIn.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify) == GattCommunicationStatus.Success;
            }

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            int idx = 0;
            foreach (var p in packets)
            {
                if (!(variant == 12 && idx < 5)) await timer.WaitForNextTickAsync();   // variant 13: pre-buffer 5 packets
                foreach (var w in BuildAudioWrites(variant, p, idx))
                {
                    try
                    {
                        var wr = new DataWriter();
                        wr.WriteBytes(w);
                        var res = await _headsetOut.WriteValueWithResultAsync(wr.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
                        if (res.Status == GattCommunicationStatus.Success) ok++;
                        else error ??= $"GATT status {res.Status}";
                    }
                    catch (Exception ex) { error ??= ex.Message; }
                }
                idx++;
            }
        }
        finally
        {
            timeEndPeriod(1);
            if (_headsetIn is not null)
            {
                _headsetIn.ValueChanged -= OnHeadset;
                if (subscribed)
                    try { await _headsetIn.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None); } catch { }
            }
            _headsetGate.Release();
        }

        string seen;
        lock (states) seen = states.Count == 0 ? "none" : string.Join(", ", states.Select(s => $"0x{s:X2}"));
        int perPacket = variant is 1 or 3 or 11 or 14 ? 1 : 2;
        return $"Variant {variant + 1}: {ok} of {packets.Length * perPacket} writes accepted" +
               $"{(error is null ? "" : $" · first error: {error}")} · jack states seen: {seen}";
    }

    // ───────────────────────── Callbacks ─────────────────────────

    private void OnCommandResponse(GattCharacteristic sender, GattValueChangedEventArgs e)
    {
        var buf = new byte[e.CharacteristicValue.Length];
        DataReader.FromBuffer(e.CharacteristicValue).ReadBytes(buf);
        _pendingResponse?.TrySetResult(buf);
    }

    // Runs on a thread-pool thread, NOT the UI thread.
    private void OnInput(GattCharacteristic sender, GattValueChangedEventArgs e)
    {
        var buf = new byte[e.CharacteristicValue.Length];
        DataReader.FromBuffer(e.CharacteristicValue).ReadBytes(buf);
        if (InputReportParser.TryParse(buf, out var state))
            StateReceived?.Invoke(state);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (!_closed && sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            Disconnected?.Invoke();
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            if (_input is not null) _input.ValueChanged -= OnInput;
            if (_cmdResponse is not null) _cmdResponse.ValueChanged -= OnCommandResponse;
            if (_device is not null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        }
        catch { }

        // Windows keeps the BLE link open while any service, session or device handle is alive,
        // so the release runs in the background: stop notifications, then close every GATT object.
        _ = ReleaseAsync(_session, _device, _services, _input, _cmdResponse, _headsetIn);
        _session = null;
        _device = null;
        _services = null;
        _connParamsRequest = null;
    }

    private static async Task ReleaseAsync(GattSession? session, BluetoothLEDevice? device,
        IReadOnlyList<GattDeviceService>? services, params GattCharacteristic?[] subscribed)
    {
        try
        {
            if (session is not null) session.MaintainConnection = false;
            foreach (var c in subscribed)
            {
                if (c is null) continue;
                try { await c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None); }
                catch { }
            }
        }
        finally
        {
            if (services is not null)
                foreach (var s in services)
                {
                    try { s.Dispose(); } catch { }
                }
            try { session?.Dispose(); } catch { }
            try { device?.Dispose(); } catch { }
        }
    }
}
