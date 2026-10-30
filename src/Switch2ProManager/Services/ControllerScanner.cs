using System.Buffers.Binary;
using Switch2ProManager.Models;
using Switch2ProManager.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace Switch2ProManager.Services;

/// <summary>
/// Scans BLE advertisements and keeps the Nintendo ones (company ID 0x0553).
/// Do NOT pair the controller from Windows Settings > Bluetooth: it uses its own handshake (see Switch2Session).
/// </summary>
public sealed class ControllerScanner : IDisposable
{
    private BluetoothLEAdvertisementWatcher? _watcher;

    /// <summary>Bluetooth address of THIS PC (needed by the registration handshake).</summary>
    public ulong HostAddress { get; private set; }

    public event Action<DiscoveredController>? Found;

    public async Task StartAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync()
            ?? throw new InvalidOperationException("No Bluetooth adapter found.");
        if (!adapter.IsLowEnergySupported)
            throw new InvalidOperationException("The adapter does not support Bluetooth Low Energy (BT 4.0+ required).");

        HostAddress = adapter.BluetoothAddress;

        Stop();
        // Active scanning also requests scan responses, where manufacturer data may travel.
        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        _watcher.Received += OnReceived;
        _watcher.Start();
    }

    public void Stop()
    {
        if (_watcher is null) return;
        _watcher.Received -= OnReceived;
        try { _watcher.Stop(); } catch { /* already stopped */ }
        _watcher = null;
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        foreach (var md in e.Advertisement.ManufacturerData)
        {
            if (md.CompanyId != Switch2Protocol.NintendoBleCompanyId) continue;

            var data = new byte[md.Data.Length];
            DataReader.FromBuffer(md.Data).ReadBytes(data);
            if (data.Length < 7) continue;

            // Manufacturer data (without company ID): [3..4] = VID, [5..6] = PID, [10..15] = reconnect MAC
            ushort vid = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(3, 2));
            ushort pid = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(5, 2));
            if (vid != Switch2Protocol.NintendoVendorId || pid != Switch2Protocol.ProController2Pid) continue;

            ulong reconnect = 0;
            if (data.Length >= 16)
            {
                Span<byte> tmp = stackalloc byte[8];
                data.AsSpan(10, 6).CopyTo(tmp);
                reconnect = BinaryPrimitives.ReadUInt64LittleEndian(tmp);
            }

            // 0 = SYNC held (pairing mode); this PC's address = reconnect to us; anything else = bonded to another host.
            var mode = reconnect == 0 ? AdvertisingMode.Pairing
                     : reconnect == HostAddress ? AdvertisingMode.ReconnectThisPc
                     : AdvertisingMode.ReconnectOtherHost;

            Found?.Invoke(new DiscoveredController(e.BluetoothAddress, pid, e.RawSignalStrengthInDBm, mode));
        }
    }

    public void Dispose() => Stop();
}
