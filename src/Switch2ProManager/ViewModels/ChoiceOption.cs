using CommunityToolkit.Mvvm.ComponentModel;
using Switch2ProManager.Services;

namespace Switch2ProManager.ViewModels;

/// <summary>One entry of a dropdown. The name is either a translated key or a fixed text (such as "A" or "↑").</summary>
public sealed class ChoiceOption<T> : ObservableObject where T : notnull
{
    private readonly string? _key;
    private readonly string? _literal;

    public ChoiceOption(T value, string? key, string? literal)
    {
        Value = value;
        _key = key;
        _literal = literal;
    }

    public T Value { get; }
    public string Name => _key is null ? _literal ?? "" : AppText.Get(_key);

    public void Refresh() => OnPropertyChanged(nameof(Name));
}
