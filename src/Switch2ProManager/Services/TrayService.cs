namespace Switch2ProManager.Services;

/// <summary>
/// Notification-area icon with a context menu. Uses WinForms NotifyIcon (part of the framework, no extra package).
/// Fully qualified names are used on purpose: WinForms and WPF share type names such as Button or Application.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon = new();
    private readonly System.Windows.Forms.ToolStripMenuItem _showItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _connectItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _disconnectItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _exitItem;

    public event Action? ShowRequested;
    public event Action? ConnectRequested;
    public event Action? DisconnectRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        _showItem = new System.Windows.Forms.ToolStripMenuItem(null, null, (_, _) => ShowRequested?.Invoke());
        _connectItem = new System.Windows.Forms.ToolStripMenuItem(null, null, (_, _) => ConnectRequested?.Invoke());
        _disconnectItem = new System.Windows.Forms.ToolStripMenuItem(null, null, (_, _) => DisconnectRequested?.Invoke());
        _exitItem = new System.Windows.Forms.ToolStripMenuItem(null, null, (_, _) => ExitRequested?.Invoke());

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(_showItem);
        menu.Items.Add(_connectItem);
        menu.Items.Add(_disconnectItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_exitItem);

        ApplyTexts();
        AppText.Changed += ApplyTexts;

        _icon.Icon = LoadIcon();
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
        _icon.Text = "Switch 2 Pro Manager";
        _icon.Visible = true;
    }

    private void ApplyTexts()
    {
        _showItem.Text = AppText.Get("Tray.Show");
        _connectItem.Text = AppText.Get("Tray.Connect");
        _disconnectItem.Text = AppText.Get("Tray.Disconnect");
        _exitItem.Text = AppText.Get("Tray.Exit");
    }

    /// <summary>The app icon from the embedded resource, sized for the notification area; falls back to a system icon.</summary>
    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info is not null)
            {
                using var stream = info.Stream;
                return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
        }
        catch { /* use the fallback below */ }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>Refreshes the tooltip and enables only the menu actions that apply now.</summary>
    public void Update(string statusText, bool connected, bool canConnect)
    {
        _connectItem.Enabled = canConnect && !connected;
        _disconnectItem.Enabled = connected;

        const int MaxTooltip = 63;   // NotifyIcon.Text limit
        var tooltip = $"Switch 2 Pro Manager · {statusText}";
        _icon.Text = tooltip.Length <= MaxTooltip ? tooltip : tooltip[..MaxTooltip];
    }

    public void ShowMessage(string text) =>
        _icon.ShowBalloonTip(3000, "Switch 2 Pro Manager", text, System.Windows.Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        AppText.Changed -= ApplyTexts;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
