namespace Switch2ProManager.Protocol;

/// <summary>
/// Constants of the Pro Controller 2 BLE protocol. It is NOT standard HID-over-GATT but a proprietary GATT protocol,
/// documented by the community (see docs/PROTOCOL_NOTES.md). Nintendo does not publish it.
/// </summary>
public static class Switch2Protocol
{
    public const ushort NintendoBleCompanyId = 0x0553;   // manufacturer data company ID in the advertisement
    public const ushort NintendoVendorId = 0x057E;
    public const ushort ProController2Pid = 0x2069;

    // GATT characteristics (searched across ALL services; the controller exposes two proprietary ones)
    public static readonly Guid InputReportChar     = Guid.Parse("ab7de9be-89fe-49ad-828f-118f09df7fd2");
    public static readonly Guid CommandWriteChar    = Guid.Parse("649d4ac9-8eb7-4e6c-af44-1ea54fe5f005");
    public static readonly Guid CommandResponseChar = Guid.Parse("c765a961-d9d8-4d36-a20a-5315b111836a");
    public static readonly Guid VibrationChar       = Guid.Parse("cc483f51-9258-427d-a939-630c31f72b05");

    // Headset jack audio (firmware 2.0.0+). Input format verified (Opus); output format still unknown.
    public static readonly Guid HeadsetInputChar    = Guid.Parse("7492866c-ec3e-4619-8258-32755ffcc0f9");  // controller -> PC (notify)
    public static readonly Guid HeadsetOutputChar   = Guid.Parse("cc483f51-9258-427d-a939-630c31f72b06");  // PC -> controller (write)

    // Commands
    public const byte CmdPair = 0x15;
    public const byte SubPairSetMac = 0x01;
    public const byte SubPairStep2 = 0x02;
    public const byte SubPairFinish = 0x03;
    public const byte SubPairStep1 = 0x04;
    public const byte CmdVibration = 0x0A;       // 0x0A/0x02 = play a built-in vibration sample
    public const byte CmdFeatureSelect = 0x0C;   // 0x0C/0x02 set mask, 0x04 enable, 0x05 disable (bit 2 = IMU)
    public const byte CmdLeds = 0x09;
    public const byte SubLedsSetPlayer = 0x07;

    // Fixed payloads of the host-registration handshake (values published by the community)
    public static readonly byte[] PairingStep1 =
    {
        0x00, 0xEA, 0xBD, 0x47, 0x13, 0x89, 0x35, 0x42, 0xC6,
        0x79, 0xEE, 0x07, 0xF2, 0x53, 0x2C, 0x6C, 0x31
    };
    public static readonly byte[] PairingStep2 =
    {
        0x00, 0x40, 0xB0, 0x8A, 0x5F, 0xCD, 0x1F, 0x9B, 0x41,
        0x12, 0x5C, 0xAC, 0xC6, 0x3F, 0x38, 0xA0, 0x73
    };

    /// <summary>Command frame: [cmd, 0x91, 0x01, sub, 0x00, len, 0x00, 0x00] + data.</summary>
    public static byte[] BuildFrame(byte cmd, byte sub, ReadOnlySpan<byte> data)
    {
        var frame = new byte[8 + data.Length];
        frame[0] = cmd; frame[1] = 0x91; frame[2] = 0x01; frame[3] = sub;
        frame[4] = 0x00; frame[5] = (byte)data.Length;
        data.CopyTo(frame.AsSpan(8));
        return frame;
    }
}
