using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Dotnetumentation.App.ViewModels;

namespace Dotnetumentation.App.Views;

public partial class GeneratePage : UserControl
{
    private static readonly FilePickerFileType SolutionFiles = new("Solutions and projects")
    {
        Patterns = ["*.sln", "*.slnx", "*.csproj"],
    };

    public GeneratePage()
    {
        InitializeComponent();
        // The newest log line stays visible.
        LogList.Items.CollectionChanged += (_, _) =>
        {
            if (LogList.ItemCount > 0)
                LogList.ScrollIntoView(LogList.ItemCount - 1);
        };
    }

    private async void OnBrowseClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || DataContext is not GenerateViewModel vm)
            return;
        var start = vm.InputPath is { } path && Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)
            ? await storage.TryGetFolderFromPathAsync(dir)
            : null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open solution or project",
            FileTypeFilter = [SolutionFiles, FilePickerFileTypes.All],
            SuggestedStartLocation = start,
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } local)
            vm.InputPath = local;
    }
}
