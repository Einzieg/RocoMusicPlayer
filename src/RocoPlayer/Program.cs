using System.Windows;
using RocoPlayer.Music;

namespace RocoPlayer;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        using var instance = new Mutex(true, @"Local\RocoPlayer.CSharp", out bool first);
        if (!first)
        {
            MessageBox.Show("RocoPlayer 已在运行。", "RocoPlayer");
            return;
        }
        try
        {
            var data = Environment.GetEnvironmentVariable("ROCK_MUSIC_DATA_DIR");
            if (string.IsNullOrWhiteSpace(data)) data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.rockmusic.workspace-player");
            var home = Environment.GetEnvironmentVariable("ROCK_MUSIC_HOME") ?? Environment.CurrentDirectory;
            var library = new ScoreLibrary(data, Path.Combine(home, "music_settings.json"));
            var app = new Application();
            app.Run(new MainWindow(library, data));
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "RocoPlayer 启动失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
