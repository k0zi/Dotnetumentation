using Avalonia;
using KD.Avalonia.Rice;
using KD.Avalonia.Rice.Options;
using KD.Avalonia.Rice.Theming;

namespace Dotnetumentation.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => Configure(AppBuilder.Configure<App>().UsePlatformDetect());

    /// <summary>Everything but the platform, so a headless builder can share it.</summary>
    public static AppBuilder Configure(AppBuilder builder) =>
        builder
            .WithInterFont()
            .LogToTrace()
            .UseRice(options =>
            {
                options.AppInfo = new RiceAppInfo
                {
                    AppId = AppSettings.AppId,
                    Name = "Dotnetumentation",
                    Author = "k0zi",
                    Description = "Static UML (PlantUML) documentation of .NET solutions, made with Roslyn.",
                    IconUri = "avares://Dotnetumentation.App/Assets/icon.png",
                };
                options.SupportedThemes = BuiltInThemes.All;
                options.DefaultThemeId = BuiltInThemes.Ubuntu.Id;
            });
}
