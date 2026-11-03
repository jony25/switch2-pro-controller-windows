using CommunityToolkit.Mvvm.ComponentModel;
using Switch2ProManager.Models;
using Switch2ProManager.Services;

namespace Switch2ProManager.ViewModels;

public partial class ButtonIndicator : ObservableObject
{
    public ButtonIndicator(string id, SwitchButtons flag) { Id = id; Flag = flag; }

    /// <summary>Identifier used in code. Labels that are words are translated in <see cref="Name"/>.</summary>
    public string Id { get; }
    public string Name => Id switch { "Captura" => AppText.Get("Button.Capture"), _ => Id };
    public SwitchButtons Flag { get; }

    [ObservableProperty] private bool _isPressed;

    public void Refresh() => OnPropertyChanged(nameof(Name));
}
