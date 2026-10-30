using CommunityToolkit.Mvvm.ComponentModel;
using Switch2ProManager.Models;

namespace Switch2ProManager.ViewModels;

public partial class ButtonIndicator : ObservableObject
{
    public ButtonIndicator(string name, SwitchButtons flag) { Name = name; Flag = flag; }

    public string Name { get; }
    public SwitchButtons Flag { get; }

    [ObservableProperty] private bool _isPressed;
}
