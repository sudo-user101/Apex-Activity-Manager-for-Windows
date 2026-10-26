using Microsoft.UI.Xaml;

namespace Apex;

public partial class App : Application
{
    Window? window;

    public App()
    {
        InitializeComponent();
        // A crash leaves a note in %TEMP% (the build checks it after its test run).
        UnhandledException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
    }

    static void Log(Exception? e)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "apex-crash.txt"), $"{DateTime.Now:O}\n{e}\n\n"); } catch { }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // --selftest: check the process, efficiency and clipboard features, then quit (used by the build).
        if (Environment.GetCommandLineArgs().Contains("--selftest")) { Environment.Exit(await SelfTest.Run()); return; }
        window = new MainWindow();
        window.Activate();
    }
}
