using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Switch2ProManager.Models;

namespace Switch2ProManager.Services;

/// <summary>
/// Cemuhook "DSU" motion server (UDP, default port 26760). Emulators such as Dolphin or Cemu read gyro and
/// accelerometer data from it. Bound to the loopback address only, so other machines on the network cannot read it.
/// Serves one slot (slot 0). Output only: nothing is sent to the controller.
/// Protocol: community documentation (v1993/cemuhook-protocol). See docs/DSU_OUTPUT.md for what is verified.
/// </summary>
public sealed class CemuhookServer : IDisposable
{
    public const int DefaultPort = 26760;

    private const ushort ProtocolVersion = 1001;
    private const uint MsgVersion = 0x100000;
    private const uint MsgInfo = 0x100001;
    private const uint MsgData = 0x100002;
    private const int VersionLength = 22;
    private const int InfoLength = 32;
    private const int DataLength = 100;
    private const int HeaderLength = 16;
    private const long SendIntervalMs = 8;        // about 125 packets per second
    private const long SubscriptionTimeoutMs = 5000;

    private readonly Dictionary<IPEndPoint, long> _subscribers = new();   // endpoint -> last request time (ms)
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly uint _serverId = (uint)Random.Shared.Next();
    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private bool _connected;
    private readonly byte[] _mac = new byte[6];
    private uint _packetNumber;
    private long _lastSendMs = -SendIntervalMs;
    private long _statusAtMs;
    private int _sentSinceStatus;

    public int Port { get; }
    public bool IsRunning => _socket is not null;

    public event Action<string>? Log;

    public CemuhookServer(int port = DefaultPort) => Port = port;

    /// <summary>Starts listening. Throws if the port is already in use.</summary>
    public void Start()
    {
        if (_socket is not null) return;
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try { socket.Bind(new IPEndPoint(IPAddress.Loopback, Port)); }
        catch { socket.Dispose(); throw; }

        _socket = socket;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => ReceiveLoop(socket, ct));
        Log?.Invoke($"DSU server listening on 127.0.0.1:{Port}.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _socket?.Dispose();
        _socket = null;
        lock (_gate) _subscribers.Clear();
    }

    /// <summary>Sets the controller reported to clients. The MAC is the controller's Bluetooth address.</summary>
    public void SetController(ulong address, bool connected)
    {
        _connected = connected;
        for (int i = 0; i < 6; i++) _mac[i] = (byte)(address >> (40 - 8 * i));
    }

    /// <summary>Called from the BLE thread for every input report. Sends at most about 125 packets per second.</summary>
    public void Publish(ControllerState state)
    {
        var socket = _socket;
        if (socket is null || !_connected) return;

        long now = _clock.ElapsedMilliseconds;
        if (now - _lastSendMs < SendIntervalMs) return;
        _lastSendMs = now;

        IPEndPoint[] targets;
        List<IPEndPoint> expired;
        lock (_gate)
        {
            expired = PruneLocked(now);
            targets = _subscribers.Keys.ToArray();
        }
        foreach (var e in expired) Log?.Invoke($"DSU client {e} timed out (no data request for {SubscriptionTimeoutMs / 1000} s).");

        if (targets.Length > 0)
        {
            var packet = BuildData(state);
            foreach (var target in targets)
            {
                try { socket.SendTo(packet, target); _sentSinceStatus++; }
                catch { /* the client went away; it expires after the timeout */ }
            }
        }

        if (now - _statusAtMs >= 5000)
        {
            if (targets.Length > 0 || _sentSinceStatus > 0)
                Log?.Invoke($"DSU status: {targets.Length} client(s), {_sentSinceStatus} packets sent in the last 5 s.");
            _statusAtMs = now;
            _sentSinceStatus = 0;
        }
    }

    private void ReceiveLoop(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[256];
        EndPoint remote = new IPEndPoint(IPAddress.Loopback, 0);
        while (!ct.IsCancellationRequested)
        {
            int length;
            try { length = socket.ReceiveFrom(buffer, ref remote); }
            catch { break; }                  // the socket was closed by Stop()

            if (length < HeaderLength + 4 || buffer[0] != 'D' || buffer[1] != 'S' || buffer[2] != 'U' || buffer[3] != 'C')
                continue;

            // EndPoint is reused by ReceiveFrom, so keep a copy.
            var from = (IPEndPoint)remote;
            var client = new IPEndPoint(from.Address, from.Port);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(HeaderLength, 4));
            try
            {
                switch (type)
                {
                    case MsgVersion:
                        socket.SendTo(BuildVersion(), client);
                        break;
                    case MsgInfo:
                        Log?.Invoke($"DSU info request from {client} (controller connected: {_connected}).");
                        socket.SendTo(BuildInfo(), client);
                        break;
                    case MsgData:
                        bool isNew;
                        lock (_gate)
                        {
                            isNew = !_subscribers.ContainsKey(client);
                            _subscribers[client] = _clock.ElapsedMilliseconds;
                        }
                        if (isNew) Log?.Invoke($"DSU client subscribed from {client} (controller connected: {_connected}).");
                        break;
                }
            }
            catch { /* ignore send errors to a single client */ }
        }
    }

    private List<IPEndPoint> PruneLocked(long now)
    {
        var expired = new List<IPEndPoint>();
        foreach (var (endpoint, last) in _subscribers.ToArray())
            if (now - last > SubscriptionTimeoutMs)
            {
                _subscribers.Remove(endpoint);
                expired.Add(endpoint);
            }
        return expired;
    }

    // ───────────────────────── Packets ─────────────────────────

    private byte[] BuildVersion()
    {
        var p = NewPacket(VersionLength, MsgVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(20), ProtocolVersion);
        return Seal(p);
    }

    private byte[] BuildInfo()
    {
        var p = NewPacket(InfoLength, MsgInfo);
        WriteSharedFields(p);
        p[31] = (byte)(_connected ? 1 : 0);
        return Seal(p);
    }

    private byte[] BuildData(ControllerState s)
    {
        var p = NewPacket(DataLength, MsgData);
        WriteSharedFields(p);
        p[31] = (byte)(_connected ? 1 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(32), _packetNumber++);

        // Digital buttons. The face bits carry the Nintendo letters at their physical positions:
        // bit7 = Y (left), bit6 = B (bottom), bit5 = A (right), bit4 = X (top). Emulators read them by position.
        p[36] = Bits(
            (s.Has(SwitchButtons.DpadLeft), 7), (s.Has(SwitchButtons.DpadDown), 6),
            (s.Has(SwitchButtons.DpadRight), 5), (s.Has(SwitchButtons.DpadUp), 4),
            (s.Has(SwitchButtons.Plus), 3), (s.Has(SwitchButtons.RThumb), 2),
            (s.Has(SwitchButtons.LThumb), 1), (s.Has(SwitchButtons.Minus), 0));
        p[37] = Bits(
            (s.Has(SwitchButtons.Y), 7), (s.Has(SwitchButtons.B), 6),
            (s.Has(SwitchButtons.A), 5), (s.Has(SwitchButtons.X), 4),
            (s.Has(SwitchButtons.R), 3), (s.Has(SwitchButtons.L), 2),
            (s.Has(SwitchButtons.ZR), 1), (s.Has(SwitchButtons.ZL), 0));
        p[38] = (byte)(s.Has(SwitchButtons.Home) ? 1 : 0);
        p[39] = 0;                                   // touch button: not available

        p[40] = StickByte(s.LeftX);
        p[41] = StickByte(s.LeftY);                  // up is positive, as in DSU
        p[42] = StickByte(s.RightX);
        p[43] = StickByte(s.RightY);

        // Analog buttons: 255 when pressed (the controller's digital buttons have no pressure).
        // Order (community spec): D-pad left, down, right, up, Y, B, A, X, R1, L1, R2, L2.
        byte[] analog =
        {
            Pressed(s, SwitchButtons.DpadLeft), Pressed(s, SwitchButtons.DpadDown),
            Pressed(s, SwitchButtons.DpadRight), Pressed(s, SwitchButtons.DpadUp),
            Pressed(s, SwitchButtons.Y), Pressed(s, SwitchButtons.B),
            Pressed(s, SwitchButtons.A), Pressed(s, SwitchButtons.X),
            Pressed(s, SwitchButtons.R), Pressed(s, SwitchButtons.L),
            Pressed(s, SwitchButtons.ZR), Pressed(s, SwitchButtons.ZL),
        };
        analog.CopyTo(p, 44);

        // Touch points (56..67) stay zero.

        BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(68), _clock.Elapsed.Ticks / 10);   // microseconds
        WriteFloat(p, 76, s.AccelX);
        WriteFloat(p, 80, s.AccelY);
        WriteFloat(p, 84, s.AccelZ);
        WriteFloat(p, 88, s.GyroX);                  // pitch
        WriteFloat(p, 92, s.GyroY);                  // yaw
        WriteFloat(p, 96, s.GyroZ);                  // roll
        return Seal(p);
    }

    // Slot 0, connected Bluetooth controller with full gyro. Battery is not reported (0).
    private void WriteSharedFields(byte[] p)
    {
        p[20] = 0;                                    // slot
        p[21] = (byte)(_connected ? 2 : 0);           // slot state: 2 = connected
        p[22] = (byte)(_connected ? 2 : 0);           // model: 2 = full gyro
        p[23] = (byte)(_connected ? 2 : 0);           // connection type: 2 = Bluetooth
        _mac.CopyTo(p, 24);
        p[30] = 0;                                    // battery: not reported
    }

    private byte[] NewPacket(int length, uint type)
    {
        var p = new byte[length];
        "DSUS"u8.CopyTo(p);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), (ushort)(length - HeaderLength));
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), _serverId);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(16), type);
        return p;
    }

    private static byte[] Seal(byte[] p)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), Crc32(p));
        return p;
    }

    private static byte Bits(params (bool on, int bit)[] items)
    {
        int value = 0;
        foreach (var (on, bit) in items)
            if (on) value |= 1 << bit;
        return (byte)value;
    }

    private static byte Pressed(ControllerState s, SwitchButtons b) => s.Has(b) ? (byte)255 : (byte)0;

    private static byte StickByte(float v) => (byte)Math.Clamp(Math.Round(128 + v * 127), 0, 255);

    private static void WriteFloat(byte[] p, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(offset), value);

    /// <summary>CRC-32 (zlib polynomial 0xEDB88320), as required by the DSU header.</summary>
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    public void Dispose() => Stop();
}
