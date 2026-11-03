using System.Runtime.InteropServices;
using Switch2ProManager.Models;

namespace Switch2ProManager.Services;

/// <summary>
/// Sends keys and mouse buttons with SendInput, for the remap table. Does not work on elevated windows (UIPI)
/// unless this app is elevated too.
/// </summary>
public static class KeyboardInjector
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;

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

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    private const uint KeyExtended = 0x0001;
    private const uint InputMouse = 0;
    private const uint MouseLeftDown = 0x0002, MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008, MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020, MouseMiddleUp = 0x0040;
    private const uint MouseXDown = 0x0080, MouseXUp = 0x0100;

    /// <summary>Presses or releases a key by virtual-key code. The scan code is filled in for games that read it.</summary>
    public static void SendKey(int vk, bool down)
    {
        uint scan = MapVirtualKey((uint)vk, 0);
        uint flags = (down ? 0u : KeyUp) | (IsExtendedKey(vk) ? KeyExtended : 0u);
        var input = new Input
        {
            type = InputKeyboard,
            u = new InputUnion { ki = new KeybdInput { wVk = (ushort)vk, wScan = (ushort)scan, dwFlags = flags } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    /// <summary>Presses or releases a mouse button. Only one button per call.</summary>
    public static void SendMouse(MouseOutput button, bool down)
    {
        var (flag, data) = button switch
        {
            MouseOutput.Left => (down ? MouseLeftDown : MouseLeftUp, 0u),
            MouseOutput.Right => (down ? MouseRightDown : MouseRightUp, 0u),
            MouseOutput.Middle => (down ? MouseMiddleDown : MouseMiddleUp, 0u),
            MouseOutput.X1 => (down ? MouseXDown : MouseXUp, 1u),
            MouseOutput.X2 => (down ? MouseXDown : MouseXUp, 2u),
            _ => (0u, 0u),
        };
        if (flag == 0) return;
        var input = new Input
        {
            type = InputMouse,
            u = new InputUnion { mi = new MouseInput { dwFlags = flag, mouseData = data } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    // Arrows, Insert, Delete, Home, End, Page Up/Down, right Ctrl/Alt and the Windows keys need the extended-key flag.
    private static bool IsExtendedKey(int vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0xA3 or 0xA5;
}
