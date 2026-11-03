using Switch2ProManager.Models;

namespace Switch2ProManager.Services;

/// <summary>
/// Holds keys and mouse buttons down while the mapping asks for them. Only the changes are sent to Windows.
/// Keys of a combination go down in order (modifiers first) and come up in reverse order.
/// Works without the virtual controller, so a remapped key still works when the Xbox pad is off.
/// </summary>
public sealed class KeyMouseOutput
{
    private static readonly MouseOutput[] Buttons = { MouseOutput.Left, MouseOutput.Right, MouseOutput.Middle, MouseOutput.X1, MouseOutput.X2 };

    private readonly object _gate = new();
    private readonly List<int> _held = new();
    private MouseOutput _mouse;

    public void Apply(List<int> keys, MouseOutput mouse)
    {
        lock (_gate)
        {
            for (int i = _held.Count - 1; i >= 0; i--)
                if (!keys.Contains(_held[i])) { KeyboardInjector.SendKey(_held[i], false); _held.RemoveAt(i); }
            foreach (var k in keys)
                if (!_held.Contains(k)) { KeyboardInjector.SendKey(k, true); _held.Add(k); }

            foreach (var b in Buttons)
            {
                bool was = _mouse.HasFlag(b), now = mouse.HasFlag(b);
                if (was != now) KeyboardInjector.SendMouse(b, now);
            }
            _mouse = mouse;
        }
    }

    public void ReleaseAll() => Apply(new List<int>(), MouseOutput.None);
}
