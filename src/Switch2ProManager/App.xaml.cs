using System.Threading;
using System.Windows;
using Switch2ProManager.Services;
using Switch2ProManager.Views;

namespace Switch2ProManager;

public partial class App : Application
{
    private const string ShowSignalName = @"Local\Switch2ProManager.Show";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Unhandled errors are written to app.log before the app closes, so a crash leaves a trace.
        DispatcherUnhandledException += (_, args) =>
        {
            var xaml = args.Exception as System.Windows.Markup.XamlParseException;
            FileLog.Write($"UNHANDLED (XAML line {xaml?.LineNumber}, column {xaml?.LinePosition}): {args.Exception}");
        };

        // Single instance: if the app is already running, ask that instance to show its window and exit this one.
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName, out bool firstInstance);
        if (!firstInstance)
        {
            signal.Set();
            signal.Dispose();
            Shutdown();
            return;
        }

        AppText.Apply(SettingsStore.Load().Language);   // before the window, so every {DynamicResource} resolves
        var window = new MainWindow();
        MainWindow = window;

        var listener = new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(window.BringToFront);
        }) { IsBackground = true, Name = "show-signal" };
        listener.Start();

        // Started by the Run key: stay in the notification area instead of showing the window.
        if (!e.Args.Contains(StartupService.MinimizedArg, StringComparer.OrdinalIgnoreCase))
            window.Show();
    }
}
