using Switch2ProManager.Protocol;
using Switch2ProManager.Services;

namespace Switch2ProManager.Models;

public enum AdvertisingMode
{
    Pairing,            // SYNC held: reconnect address in the advertisement is 0
    ReconnectThisPc,    // the controller remembers this PC's adapter
    ReconnectOtherHost  // looking for another console/PC: it will ignore new hosts
}

public sealed record DiscoveredController(ulong Address, ushort ProductId, short Rssi, AdvertisingMode Mode)
{
    public string Name => ProductId == Switch2Protocol.ProController2Pid
        ? "Switch 2 Pro Controller" : $"Nintendo 0x{ProductId:X4}";

    public string AddressText =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((Address >> (40 - 8 * i)) & 0xFF).ToString("X2")));

    public bool CanConnect => Mode != AdvertisingMode.ReconnectOtherHost;

    public string Hint => Mode switch
    {
        AdvertisingMode.Pairing => AppText.Format("Device.Pairing", Rssi),
        AdvertisingMode.ReconnectThisPc => AppText.Format("Device.Reconnect", Rssi),
        _ => AppText.Get("Device.Other")
    };
}
