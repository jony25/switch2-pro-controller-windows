namespace Switch2ProManager.Services;

public enum BatteryAlertKind { Low, Critical }

/// <summary>
/// Raises one alert per threshold when the controller battery drops, and re-arms after charging or recovery.
/// The controller reports 10 steps (level 0-9). The thresholds map to level 2 (22 %, "20 %") and level 1 (11 %, "10 %"):
/// these percentages are estimates from the step count, not measured values.
/// </summary>
public sealed class BatteryAlerts
{
    private const int LowLevel = 2;
    private const int CriticalLevel = 1;

    private bool _lowArmed = true;
    private bool _criticalArmed = true;

    public event Action<BatteryAlertKind, int>? Alert;

    public void Update(int level, bool onExternalPower)
    {
        if (onExternalPower || level > LowLevel)
        {
            _lowArmed = _criticalArmed = true;
            return;
        }

        if (level <= CriticalLevel && _criticalArmed)
        {
            _criticalArmed = _lowArmed = false;
            Alert?.Invoke(BatteryAlertKind.Critical, level);
        }
        else if (_lowArmed)
        {
            _lowArmed = false;
            Alert?.Invoke(BatteryAlertKind.Low, level);
        }
    }

    public void Reset()
    {
        _lowArmed = _criticalArmed = true;
    }
}
