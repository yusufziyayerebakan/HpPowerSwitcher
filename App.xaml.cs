using Microsoft.UI.Xaml;

namespace HpPowerSwitcher;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        UnhandledException += (_, e) => LogError(e.Exception);
        try { InitializeComponent(); }
        catch (Exception ex) { LogError(ex); throw; }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex) { LogError(ex); throw; }
    }

    private static void LogError(Exception exception)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HpPowerSwitcher");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "last-error.txt"), $"{DateTimeOffset.Now:O}\n{exception}");
        }
        catch { /* Error reporting must not replace the original exception. */ }
    }
}
