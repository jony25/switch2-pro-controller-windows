using System.Runtime.InteropServices;

namespace Switch2ProManager.Services;

/// <summary>
/// Simulates key presses with SendInput. Used so the controller's Capture button presses F12
/// (Steam's default screenshot key). Does not work on elevated windows (UIPI) unless this app is elevated too,
/// and Steam must have F12 assigned to its screenshot shortcut.
/// </summary>
public static class KeyboardInjector
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const ushort VkF12 = 0x7B;
    private const ushort ScanF12 = 0x58;

    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeybdInput { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct HardwareInput { public uint uMsg; public ushort wParamL, wParamH; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput mi;
        [FieldOffset(0)] public KeybdInput ki;
        [FieldOffset(0)] public HardwareInput hi;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    private static void Send(ushort vk, ushort scan, bool up)
    {
        var input = new Input
        {
            type = InputKeyboard,
            u = new InputUnion { ki = new KeybdInput { wVk = vk, wScan = scan, dwFlags = up ? KeyUp : 0 } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    /// <summary>Presses and releases F12 (40 ms hold so Steam registers it).</summary>
    public static void TapF12() => _ = Task.Run(async () =>
    {
        Send(VkF12, ScanF12, up: false);
        await Task.Delay(40);
        Send(VkF12, ScanF12, up: true);
    });
}
