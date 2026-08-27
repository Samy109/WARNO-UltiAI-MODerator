using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace WarnoModerator.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            ShowFatalStartupError(ex);
            Shutdown(1);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatalStartupError(e.Exception);
        e.Handled = true;
        Current.Shutdown(1);
    }

    private static void ShowFatalStartupError(Exception exception)
    {
        var reportPath = WriteCrashReport(exception);
        MessageBox.Show(
            $"WARNO UltiAI MODerator could not start.\n\n{exception.Message}\n\nA diagnostic report was saved to:\n{reportPath}",
            "WARNO UltiAI MODerator",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static string WriteCrashReport(Exception exception)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WARNO UltiAI MODerator");
        var reportPath = Path.Combine(folder, "startup-error.log");

        try
        {
            Directory.CreateDirectory(folder);
            var report = new StringBuilder()
                .AppendLine($"UTC: {DateTime.UtcNow:O}")
                .AppendLine($"OS: {Environment.OSVersion}")
                .AppendLine($"64-bit OS/process: {Environment.Is64BitOperatingSystem}/{Environment.Is64BitProcess}")
                .AppendLine($"Runtime: {Environment.Version}")
                .AppendLine($"Executable: {Environment.ProcessPath}")
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();
            File.AppendAllText(reportPath, report);
            return reportPath;
        }
        catch
        {
            return "The diagnostic report could not be written.";
        }
    }
}
