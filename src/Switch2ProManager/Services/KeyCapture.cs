using System.Runtime.InteropServices;

namespace Switch2ProManager.Services;

/// <summary>
/// Captures a key or a combination for the remap table, for example Alt then Tab. A low-level keyboard hook is used
/// because the window never receives system combinations such as Alt+Tab. While it runs, the keys it consumes do not
/// reach other programs.
/// </summary>
public sealed class KeyCapture : IDisposable
{
    /// <summary>Modifiers first, then the key. An empty list means "clear the key".</summary>
    public event Action<List<int>>? Captured;
    public event Action? Cancelled;

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const uint LlkhfInjected = 0x10;      // keys sent by this app (remapped output) are ignored
    private const int VkBack = 0x08, VkEscape = 0x1B, VkDelete = 0x2E;

    // Left/right specific codes, in the order they are listed in a combination.
    private static readonly int[] Modifiers = { 0xA2, 0xA3, 0xA0, 0xA1, 0xA4, 0xA5, 0x5B, 0x5C };

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr extraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly HookProc _proc;             // kept in a field so the garbage collector does not free the callback
    private IntPtr _hook;
    private readonly HashSet<int> _down = new();
    private readonly HashSet<int> _consumedUp = new();   // key-up events to swallow for keys already used
    private bool _comboStarted;                  // a non-modifier key was pressed since the modifiers went down

    public KeyCapture() => _proc = Hook;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _down.Clear();
        _consumedUp.Clear();
        _comboStarted = false;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    public void Dispose() => Stop();

    private IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(_hook, code, wParam, lParam);

        var k = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        if ((k.flags & LlkhfInjected) != 0) return CallNextHookEx(_hook, code, wParam, lParam);

        int vk = (int)k.vkCode;
        int msg = wParam.ToInt32();
        bool down = msg is WmKeyDown or WmSysKeyDown;
        bool up = msg is WmKeyUp or WmSysKeyUp;
        bool modifier = Array.IndexOf(Modifiers, vk) >= 0;

        if (modifier)
        {
            if (down)
            {
                _down.Add(vk);
                return CallNextHookEx(_hook, code, wParam, lParam);
            }
            if (up)
            {
                bool alone = !_comboStarted;
                _down.Remove(vk);
                if (alone)
                {
                    Finish(new List<int> { vk });   // a single modifier, such as Alt on its own
                    return 1;
                }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (down)
        {
            var held = Modifiers.Where(_down.Contains).ToList();
            if (held.Count == 0 && vk == VkEscape) { Cancelled?.Invoke(); return 1; }
            if (held.Count == 0 && vk is VkDelete or VkBack) { Finish(new List<int>()); return 1; }

            _comboStarted = true;
            _consumedUp.Add(vk);
            held.Add(vk);
            Finish(held);
            return 1;
        }

        if (up && _consumedUp.Remove(vk)) return 1;
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    // Stops the hook first, so the owner can dispose this object from inside the event.
    private void Finish(List<int> keys)
    {
        Stop();
        Captured?.Invoke(keys);
    }
}
