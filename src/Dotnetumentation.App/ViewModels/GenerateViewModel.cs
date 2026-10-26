using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dotnetumentation.Core;
using Dotnetumentation.Core.Model;

namespace Dotnetumentation.App.ViewModels;

public sealed partial class GenerateViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private string? _inputPath;

    [ObservableProperty] private bool _classDiagrams;
    [ObservableProperty] private bool _componentDiagrams;
    [ObservableProperty] private bool _sequenceDiagrams;
    [ObservableProperty] private bool _activityDiagrams;
    [ObservableProperty] private bool _graph;
    [ObservableProperty] private Visibility _minVisibility;
    [ObservableProperty] private bool _followAllCalls;
    [ObservableProperty] private decimal _sequenceDepth;
    [ObservableProperty] private decimal _maxSequenceMessages;
    [ObservableProperty] private string _include;
    [ObservableProperty] private string _exclude;
    [ObservableProperty] private string _outputDirectoryName;
    [ObservableProperty] private bool _clean;
    [ObservableProperty] private bool _incremental;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private bool _isRunning;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate = true;
    [ObservableProperty] private string _status = "Choose a solution and start the generation.";

    public GenerateViewModel(AppSettings settings)
    {
        _settings = settings;
        _inputPath = settings.InputPath;
        _classDiagrams = settings.Diagrams.HasFlag(DiagramKinds.Class);
        _componentDiagrams = settings.Diagrams.HasFlag(DiagramKinds.Component);
        _sequenceDiagrams = settings.Diagrams.HasFlag(DiagramKinds.Sequence);
        _activityDiagrams = settings.Diagrams.HasFlag(DiagramKinds.Activity);
        _graph = settings.Graph;
        _minVisibility = settings.MinVisibility;
        _followAllCalls = settings.SequenceDepth is null;
        _sequenceDepth = settings.SequenceDepth ?? 3;
        _maxSequenceMessages = settings.MaxSequenceMessages;
        _include = settings.Include;
        _exclude = settings.Exclude;
        _outputDirectoryName = settings.OutputDirectoryName;
        _incremental = settings.Incremental;
    }

    // An incremental update keeps the earlier files, deleting them first would defeat it.
    partial void OnCleanChanged(bool value)
    {
        if (value)
            Incremental = false;
    }

    partial void OnIncrementalChanged(bool value)
    {
        if (value)
            Clean = false;
    }

    public IReadOnlyList<Visibility> Visibilities { get; } = Enum.GetValues<Visibility>().Reverse().ToList();

    public ObservableCollection<string> Log { get; } = [];

    /// <summary>Raised after a successful run with the documented solution, so the browser can show it.</summary>
    public event EventHandler<string>? Generated;

    private bool CanGenerate() => !IsRunning && !string.IsNullOrWhiteSpace(InputPath);

    [RelayCommand(CanExecute = nameof(CanGenerate), IncludeCancelCommand = true)]
    private async Task GenerateAsync(CancellationToken cancellationToken)
    {
        var options = CreateOptions();
        SaveSettings(options);
        Log.Clear();
        IsRunning = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        Status = "Generating…";

        // Created on the UI thread: the reports arrive there.
        var progress = new Progress<GenerationProgress>(p =>
        {
            Log.Add(p.Message);
            if (p.Total > 0)
            {
                IsProgressIndeterminate = false;
                Progress = 100.0 * p.Completed / p.Total;
            }
        });

        try
        {
            // The loading and analysis is CPU heavy and partly synchronous: keep it off the UI thread.
            var result = await Task.Run(() => new DocumentationGenerator().GenerateAsync(options, progress, cancellationToken), cancellationToken);
            foreach (var warning in result.Warnings)
                Log.Add("⚠ " + warning);
            var files = result.Projects.Sum(p => p.Files.Count);
            var skipped = result.Projects.Count(p => p.Mode == GenerationMode.Skipped);
            Status = $"{result.Projects.Count} project(s){(skipped > 0 ? $" ({skipped} up to date)" : "")}, {files} files in {result.Elapsed.TotalSeconds:0.0} s"
                     + (result.Warnings.Count > 0 ? $", {result.Warnings.Count} warning(s)." : ".");
            if (result.Projects.Count > 0)
                Generated?.Invoke(this, options.InputPath);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception e)
        {
            Status = "Error: " + e.Message;
            Log.Add(e.ToString());
        }
        finally
        {
            IsRunning = false;
            IsProgressIndeterminate = false;
        }
    }

    private GeneratorOptions CreateOptions()
    {
        var diagrams = DiagramKinds.None;
        if (ClassDiagrams)
            diagrams |= DiagramKinds.Class;
        if (ComponentDiagrams)
            diagrams |= DiagramKinds.Component;
        if (SequenceDiagrams)
            diagrams |= DiagramKinds.Sequence;
        if (ActivityDiagrams)
            diagrams |= DiagramKinds.Activity;

        return new GeneratorOptions
        {
            InputPath = InputPath!.Trim(),
            Diagrams = diagrams,
            MinVisibility = MinVisibility,
            SequenceDepth = FollowAllCalls ? null : (int)SequenceDepth,
            MaxSequenceMessages = (int)MaxSequenceMessages,
            Include = Patterns(Include),
            Exclude = Patterns(Exclude),
            OutputDirectoryName = string.IsNullOrWhiteSpace(OutputDirectoryName)
                ? GeneratorOptions.DefaultOutputDirectoryName
                : OutputDirectoryName.Trim(),
            Clean = Clean,
            Incremental = Incremental,
            Graph = Graph,
        };
    }

    private void SaveSettings(GeneratorOptions options)
    {
        _settings.InputPath = options.InputPath;
        _settings.Diagrams = options.Diagrams;
        _settings.MinVisibility = options.MinVisibility;
        _settings.SequenceDepth = options.SequenceDepth;
        _settings.MaxSequenceMessages = options.MaxSequenceMessages;
        _settings.Include = Include;
        _settings.Exclude = Exclude;
        _settings.OutputDirectoryName = options.OutputDirectoryName;
        _settings.Incremental = options.Incremental;
        _settings.Graph = options.Graph;
        _settings.Save();
    }

    private static string[] Patterns(string text) =>
        text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
