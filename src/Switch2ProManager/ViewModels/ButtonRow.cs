using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Switch2ProManager.Models;
using Switch2ProManager.Services;

namespace Switch2ProManager.ViewModels;

/// <summary>One row of the remap table. Every change raises Changed, so the mapping is rebuilt.</summary>
public sealed partial class ButtonRow : ObservableObject
{
    public ButtonRow(SwitchButtons input) => Input = input;

    public event Action? Changed;

    public SwitchButtons Input { get; }

    public string Label => Input switch
    {
        SwitchButtons.Minus => "−",
        SwitchButtons.Plus => "+",
        SwitchButtons.Capture => AppText.Get("Button.Capture"),
        SwitchButtons.C => "C",
        SwitchButtons.LThumb => "L3",
        SwitchButtons.RThumb => "R3",
        SwitchButtons.DpadUp => "↑",
        SwitchButtons.DpadDown => "↓",
        SwitchButtons.DpadLeft => "←",
        SwitchButtons.DpadRight => "→",
        _ => Input.ToString(),
    };

    public string KeyText => Keys.Count == 0 ? "—" : string.Join(" + ", Keys.Select(k => KeyInterop.KeyFromVirtualKey(k).ToString()));

    [ObservableProperty] private PadTarget _target = PadTarget.Default;
    [ObservableProperty] private List<int> _keys = new();
    [ObservableProperty] private MouseOutput _mouse;
    [ObservableProperty] private bool _isCapturing;

    partial void OnTargetChanged(PadTarget value) => Changed?.Invoke();
    partial void OnKeysChanged(List<int> value)
    {
        OnPropertyChanged(nameof(KeyText));
        Changed?.Invoke();
    }
    partial void OnMouseChanged(MouseOutput value) => Changed?.Invoke();

    public void Refresh()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(KeyText));
    }

    public void LoadFrom(ButtonBinding b)
    {
        Target = b.Target;
        Keys = new List<int>(b.Keys);
        Mouse = b.Mouse;
    }

    public ButtonBinding ToModel() => new() { Input = Input, Target = Target, Keys = new List<int>(Keys), Mouse = Mouse };
}
