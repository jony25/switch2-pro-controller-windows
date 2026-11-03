using System.Windows;
using System.Windows.Controls;
using Switch2ProManager.Services;
using Switch2ProManager.ViewModels;

namespace Switch2ProManager.Views;

public partial class MainWindow : Window
{
    private readonly TrayService _tray = new();
    private readonly MainViewModel _vm;
    private bool _exiting;       // true only when the user chooses Exit (or Windows is shutting down)
    private bool _hintShown;     // the "still running" balloon is shown once per session

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        _tray.ShowRequested += RestoreFromTray;
        _tray.ConnectRequested += () => { if (_vm.ConnectCommand.CanExecute(null)) _vm.ConnectCommand.Execute(null); };
        _tray.DisconnectRequested += () => { if (_vm.DisconnectCommand.CanExecute(null)) _vm.DisconnectCommand.Execute(null); };
        _tray.ExitRequested += () => { _exiting = true; Application.Current.Shutdown(); };
        _vm.Notification += message => { if (_vm.TrayNotifications) _tray.ShowMessage(message); };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Status) or nameof(MainViewModel.StatusText) or nameof(MainViewModel.SelectedDevice))
                RefreshTray();
        };
        RefreshTray();

        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && _vm.MinimizeToTray) HideToTray(); };
        Closing += (_, e) =>
        {
            if (_exiting || !_vm.CloseToTray) return;
            e.Cancel = true;
            HideToTray();
        };
        Application.Current.SessionEnding += (_, _) => _exiting = true;
        Closed += (_, _) => { _vm.Dispose(); _tray.Dispose(); };
    }

    // Called by the second instance of the app (see App.xaml.cs).
    public void BringToFront()
    {
        RestoreFromTray();
        Topmost = true;      // briefly topmost so Windows raises the window above the others
        Topmost = false;
    }

    private void RefreshTray() =>
        _tray.Update(_vm.StatusText, _vm.Status == ConnectionStatus.Connected, _vm.ConnectCommand.CanExecute(null));

    private void HideToTray()
    {
        Hide();
        if (_hintShown || !_vm.TrayNotifications) return;
        _hintShown = true;
        _tray.ShowMessage(AppText.Get("Tray.Hint"));
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // Accordion: opening one section closes its siblings in the same panel.
    private void OnAccordionExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander opened || opened.Parent is not Panel panel) return;
        foreach (var other in panel.Children.OfType<Expander>())
            if (other != opened) other.IsExpanded = false;
    }
}
