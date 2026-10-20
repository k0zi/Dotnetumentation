using Avalonia.Controls;
using Dotnetumentation.App.ViewModels;
using KD.Avalonia.Rice.Controls;

namespace Dotnetumentation.App.Views;

public partial class MainWindow : RiceWindow
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly GenerateViewModel _generate;
    private readonly BrowseViewModel _browse = new();
    // Pages are created once and kept, so their state survives navigation.
    private readonly GeneratePage _generatePage;
    private readonly BrowsePage _browsePage;

    public MainWindow()
    {
        InitializeComponent();
        _generate = new GenerateViewModel(_settings);
        _generatePage = new GeneratePage { DataContext = _generate };
        _browsePage = new BrowsePage { DataContext = _browse };

        _browse.OutputDirectoryName = _settings.OutputDirectoryName;
        if (_settings.InputPath is { } last && File.Exists(last))
            _browse.Load(last);

        // After a successful run the new documentation is shown right away.
        _generate.Generated += (_, path) =>
        {
            _browse.OutputDirectoryName = _settings.OutputDirectoryName;
            _browse.Load(path);
            SideMenu.SelectedItem = BrowseItem;
        };

        SideMenu.SelectionChanged += (_, _) =>
        {
            if (SideMenu.SelectedItem is RiceSideMenuItem item)
                ShowPage(item);
        };
        ShowPage(GenerateItem);
    }

    private void ShowPage(RiceSideMenuItem item) =>
        PageHost.Content = item == BrowseItem ? _browsePage : (Control)_generatePage;
}
