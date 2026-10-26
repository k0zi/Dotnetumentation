using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Dotnetumentation.App.ViewModels;
using KD.Avalonia.Rice.Controls.Uml;

namespace Dotnetumentation.App.Views;

public partial class BrowsePage : UserControl
{
    private BrowseViewModel? _viewModel;

    public BrowsePage()
    {
        InitializeComponent();
        SourceToggle.IsCheckedChanged += (_, _) => UpdateSource();

        // Keep reference pages and UML links in the documentation browser.
        MarkdownView.LinkClicked += (_, e) =>
        {
            if (_viewModel is not null && e.Uri is { } uri && _viewModel.OpenLink(uri))
                e.Handled = true;
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = DataContext as BrowseViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(BrowseViewModel.DiagramSource) or nameof(BrowseViewModel.Markdown)))
            return;
        UpdateSource();
        if (e.PropertyName == nameof(BrowseViewModel.DiagramSource) && _viewModel?.DiagramSource is not null)
        {
            DiagramView.SelectedElement = null;
            // Fit after the new diagram was laid out.
            DispatcherTimer.RunOnce(() =>
            {
                DiagramView.ZoomToFit();
                ShowDiagnostics();
            }, TimeSpan.FromMilliseconds(1));
        }
    }

    private void ShowDiagnostics()
    {
        var problems = DiagramView.Diagnostics.Where(d => d.Severity != UmlDiagnosticSeverity.Info).ToList();
        StatusText.Text = problems.Count == 0
            ? $"{DiagramView.Diagram?.Kind} diagram"
            : $"{DiagramView.Diagram?.Kind} diagram · {problems.Count} problem(s): {problems[0]}";
    }

    /// <summary>The source box replaces the rendered view while the toggle is on.</summary>
    private void UpdateSource()
    {
        var show = SourceToggle.IsChecked == true && _viewModel is { ShowsNothing: false };
        SourceBox.Text = show ? _viewModel!.DiagramSource ?? _viewModel.Markdown : null;
        SourceBox.IsVisible = show;
        DiagramView.Opacity = MarkdownView.Opacity = show ? 0 : 1;
        DiagramView.IsHitTestVisible = MarkdownView.IsHitTestVisible = !show;
    }

    private async void OnOpenFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || _viewModel is null)
            return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open documentation folder" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            _viewModel.Load(path);
    }
}
