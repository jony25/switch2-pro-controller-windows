using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Switch2ProManager.Models;
using Switch2ProManager.Services;

namespace Switch2ProManager.ViewModels;

/// <summary>Deadzones and curve of one stick, with the graph points and the live dot. Every change raises Changed.</summary>
public sealed partial class StickEditor : ObservableObject
{
    public const double GraphSize = 180;   // pixels, the graph is square
    private const double DotRadius = 5;

    public event Action? Changed;

    [ObservableProperty] private double _inner = 5;        // percent, 0 to 30
    [ObservableProperty] private double _outer;            // percent, 0 to 20
    [ObservableProperty] private StickCurve _curve = StickCurve.Linear;
    [ObservableProperty] private double _exponent = 2.0;   // 1.0 to 3.0
    [ObservableProperty] private PointCollection _curvePoints = new();

    // Top-left corner of the live dot inside the graph: x = input magnitude, y = output magnitude.
    [ObservableProperty] private double _liveX = -DotRadius;
    [ObservableProperty] private double _liveY = GraphSize - DotRadius;

    partial void OnInnerChanged(double value) => Rebuild();
    partial void OnOuterChanged(double value) => Rebuild();
    partial void OnCurveChanged(StickCurve value) => Rebuild();
    partial void OnExponentChanged(double value) => Rebuild();

    [RelayCommand]
    private void Reset() => LoadFrom(new StickSettings());

    public StickSettings ToModel() => new()
    {
        Inner = Inner / 100.0,
        Outer = Outer / 100.0,
        Curve = Curve,
        Exponent = Exponent,
    };

    public void LoadFrom(StickSettings m)
    {
        Inner = m.Inner * 100;
        Outer = m.Outer * 100;
        Curve = m.Curve;
        Exponent = m.Exponent;
    }

    /// <summary>Moves the live dot: raw input on the x axis, output after the deadzones and curve on the y axis.</summary>
    public void UpdateLive(float rawX, float rawY, float outX, float outY)
    {
        LiveX = Math.Min(1, Math.Sqrt(rawX * rawX + rawY * rawY)) * GraphSize - DotRadius;
        LiveY = (1 - Math.Min(1, Math.Sqrt(outX * outX + outY * outY))) * GraphSize - DotRadius;
    }

    public void ResetLive()
    {
        LiveX = -DotRadius;
        LiveY = GraphSize - DotRadius;
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Inner));
        OnPropertyChanged(nameof(Outer));
        OnPropertyChanged(nameof(Exponent));
    }

    private void Rebuild()
    {
        var model = ToModel();
        var points = new PointCollection();
        for (int i = 0; i <= 40; i++)
        {
            double x = i / 40.0;
            double y = InputMapper.Transfer(x, model);
            points.Add(new Point(x * GraphSize, (1 - y) * GraphSize));
        }
        CurvePoints = points;
        Changed?.Invoke();
    }
}
