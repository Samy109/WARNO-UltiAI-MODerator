using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WarnoModerator.Core;

namespace WarnoModerator.App;

public partial class MainWindow : Window
{
    private readonly ModScanner _scanner = new();
    private readonly MergePlanner _planner = new(new SourceDeltaAnalyzer());
    private readonly ModFingerprintService _fingerprintService = new();
    private readonly CombinedModStateStore _stateStore = new();
    private readonly CombineService _combineService = new(new SourceDeltaAnalyzer(), new ProcessRunner());
    private WarnoPaths? _paths;
    private bool _settingName;
    private bool _busy;
    private int _selectionRevision;
    private CombinedModState? _existingCombination;
    private IReadOnlyList<SourceModFingerprint>? _currentFingerprints;
    private IReadOnlyList<string> _changedMods = [];
    private bool _legacyCombination;

    public MainWindow() => InitializeComponent();

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _paths = new WarnoLocator().Locate() ?? throw new CombineException("WARNO was not found in any Steam library.");
            WarnoPathBox.Text = _paths.WarnoRoot;
            RefreshMods();
        }
        catch (Exception ex)
        {
            Log("Automatic detection failed: " + ex.Message);
            WarnoPathBox.Text = @"C:\Program Files (x86)\Steam\steamapps\common\WARNO";
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the WARNO installation folder", InitialDirectory = WarnoPathBox.Text };
        if (dialog.ShowDialog() == true)
        {
            WarnoPathBox.Text = dialog.FolderName;
            RefreshMods();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshMods();

    private void RefreshMods()
    {
        try
        {
            _paths = new WarnoLocator().FromWarnoRoot(WarnoPathBox.Text.Trim());
            var mods = _scanner.Scan(_paths);
            OtherModBox.ItemsSource = mods.Where(m => !IsUlti(m)).ToList();
            var priorityMods = mods
                .Where(IsUlti)
                .OrderByDescending(m => m.Kind == ModKind.WorkshopCompiled)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            UltiModBox.ItemsSource = priorityMods;
            OtherModBox.SelectedIndex = OtherModBox.Items.Count > 0 ? 0 : -1;
            UltiModBox.SelectedIndex = UltiModBox.Items.Count > 0 ? 0 : -1;
            Log($"Found {mods.Count(m => m.Kind == ModKind.EditableSource)} editable and {mods.Count(m => m.Kind == ModKind.WorkshopCompiled)} Workshop mods.");
            if (priorityMods.Count > 0) Log("Priority choices: " + string.Join(", ", priorityMods.Select(m => m.Name)) + ".");
            if (UltiModBox.Items.Count == 0) Log("No installed UltiAI/UltiAIDEV Workshop or editable mod was found.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settingName || OtherModBox.SelectedItem is not ModDescriptor other || UltiModBox.SelectedItem is not ModDescriptor ulti) return;
        var revision = ++_selectionRevision;
        _existingCombination = _paths is null ? null : _stateStore.FindForSources(_paths, other, ulti);
        _legacyCombination = false;
        _currentFingerprints = null;
        _changedMods = [];
        var defaultOutputName = $"{other.Name} + {ulti.Name}";
        if (_existingCombination is null && _paths is not null)
        {
            var sourceOutput = Path.Combine(_paths.ModsRoot, defaultOutputName);
            var runtimeOutput = Path.Combine(_paths.SavedModsRoot, defaultOutputName);
            if (Directory.Exists(sourceOutput) || Directory.Exists(runtimeOutput))
            {
                _legacyCombination = true;
                _existingCombination = new CombinedModState(
                    CombinedModState.CurrentSchemaVersion,
                    defaultOutputName,
                    new SourceModFingerprint(other.Name, other.RootPath, string.Empty),
                    new SourceModFingerprint(ulti.Name, ulti.RootPath, string.Empty));
            }
        }
        _settingName = true;
        OutputNameBox.Text = _existingCombination?.OutputName ?? defaultOutputName;
        _settingName = false;
        PreviewGrid.ItemsSource = null;
        SummaryText.Text = string.Empty;

        if (_existingCombination is null)
        {
            UpdateActionStates();
            return;
        }

        try
        {
            SetBusy(true, "Checking source mods");
            var fingerprintProgress = new Progress<CombineProgress>(UpdateProgress);
            var fingerprints = await Task.Run(() => _fingerprintService.ComputeAsync(
                [other, ulti],
                fingerprintProgress));
            if (revision != _selectionRevision) return;

            _currentFingerprints = fingerprints;
            var changed = new List<string>();
            if (!CombinedModStateStore.FingerprintMatches(_existingCombination.OtherMod, fingerprints[0]))
                changed.Add(other.Name);
            if (!CombinedModStateStore.FingerprintMatches(_existingCombination.PriorityMod, fingerprints[1]))
                changed.Add(ulti.Name);
            _changedMods = changed;
        }
        catch (Exception ex)
        {
            if (revision == _selectionRevision) ShowError(ex);
        }
        finally
        {
            if (revision == _selectionRevision) SetBusy(false);
        }
    }

    private void OutputName_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_settingName)
        {
            PreviewGrid.ItemsSource = null;
            SummaryText.Text = string.Empty;
            UpdateActionStates();
        }
    }

    private CombineRequest GetRequest(bool allowExistingOutput = false)
    {
        if (_paths is null || OtherModBox.SelectedItem is not ModDescriptor other || UltiModBox.SelectedItem is not ModDescriptor ulti)
            throw new CombineException("Select both a mod and an UltiAI priority variant.");
        var preview = _planner.CreatePreview(_paths, other, ulti, OutputNameBox.Text.Trim(), allowExistingOutput);
        return new CombineRequest(_paths, other, ulti, OutputNameBox.Text.Trim(), preview);
    }

    private MergePreview Preview()
    {
        var preview = GetRequest(_existingCombination is not null).Preview;
        DisplayPreview(preview);
        return preview;
    }

    private void DisplayPreview(MergePreview preview)
    {
        PreviewGrid.ItemsSource = preview.Decisions;
        SummaryText.Text = $"{preview.Decisions.Count:N0} paths · {preview.OverrideCount:N0} UltiAI wins";
        foreach (var warning in preview.Warnings) Log("WARNING: " + warning);
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        try { Preview(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Combine_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var request = GetRequest();
            var preview = request.Preview;
            DisplayPreview(preview);
            if (MessageBox.Show($"Create '{request.OutputName}'?\n\n{preview.Decisions.Count:N0} paths will be composed. UltiAI wins {preview.OverrideCount:N0} collisions.", "Confirm merge", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            SetBusy(true, "Checking source mods");
            var fingerprintProgress = new Progress<CombineProgress>(progress => UpdateProgress(new CombineProgress(
                progress.Percent / 10,
                progress.Stage)));
            var fingerprints = await Task.Run(() => _fingerprintService.ComputeAsync(
                [request.OtherMod, request.UltiMod],
                fingerprintProgress));
            var result = await _combineService.CombineAsync(
                request,
                new Progress<string>(Log),
                operationProgress: new Progress<CombineProgress>(progress => UpdateProgress(new CombineProgress(
                    10 + (int)Math.Round(progress.Percent * 0.9),
                    progress.Stage))));
            var state = CreateState(request, fingerprints);
            _stateStore.Save(result.OutputSourcePath, state);
            SetCompletedState(state, fingerprints);
            Log($"DONE: {result.OutputSourcePath}");
            MessageBox.Show($"Combined mod created successfully.\n\n{result.OutputSourcePath}", "WARNO UltiAI MODerator", MessageBoxButton.OK, MessageBoxImage.Information);
            Process.Start(new ProcessStartInfo("explorer.exe", result.OutputSourcePath) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_existingCombination is null || _currentFingerprints is null || _changedMods.Count == 0)
        {
            return;
        }

        try
        {
            var request = GetRequest(true);
            DisplayPreview(request.Preview);
            var changeStatus = _legacyCombination ? "Needs initial tracked rebuild" : "Updated";
            var changedList = string.Join(Environment.NewLine, _changedMods.Select(name => $"• {name} — {changeStatus}"));
            if (MessageBox.Show(
                    $"Changes were detected in:{Environment.NewLine}{Environment.NewLine}{changedList}{Environment.NewLine}{Environment.NewLine}Update and rebuild '{request.OutputName}'?",
                    "Update and Rebuild",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }

            SetBusy(true, "Preparing rebuild");
            var result = await _combineService.RebuildAsync(
                request,
                new Progress<string>(Log),
                operationProgress: new Progress<CombineProgress>(UpdateProgress));
            var state = CreateState(request, _currentFingerprints);
            _stateStore.Save(result.OutputSourcePath, state);
            SetCompletedState(state, _currentFingerprints);
            Log($"DONE: {result.OutputSourcePath}");
            MessageBox.Show(
                $"Combined mod updated and rebuilt successfully.\n\n{result.OutputSourcePath}",
                "WARNO UltiAI MODerator",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Process.Start(new ProcessStartInfo("explorer.exe", result.OutputSourcePath) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private static CombinedModState CreateState(
        CombineRequest request,
        IReadOnlyList<SourceModFingerprint> fingerprints) =>
        new(
            CombinedModState.CurrentSchemaVersion,
            request.OutputName,
            fingerprints[0],
            fingerprints[1]);

    private void SetCompletedState(
        CombinedModState state,
        IReadOnlyList<SourceModFingerprint> fingerprints)
    {
        _existingCombination = state;
        _currentFingerprints = fingerprints;
        _changedMods = [];
        _legacyCombination = false;
    }

    private void SetBusy(bool busy, string stage = "Preparing")
    {
        _busy = busy;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) UpdateProgress(new CombineProgress(0, stage));
        BrowseButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        WarnoPathBox.IsEnabled = !busy;
        OtherModBox.IsEnabled = !busy;
        UltiModBox.IsEnabled = !busy;
        UpdateActionStates();
        System.Windows.Input.Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void UpdateActionStates()
    {
        var hasSelection = _paths is not null
            && OtherModBox.SelectedItem is ModDescriptor
            && UltiModBox.SelectedItem is ModDescriptor;
        var outputName = OutputNameBox.Text.Trim();
        var outputExists = _paths is not null
            && (Directory.Exists(Path.Combine(_paths.ModsRoot, outputName))
                || Directory.Exists(Path.Combine(_paths.SavedModsRoot, outputName)));

        PreviewButton.IsEnabled = !_busy && hasSelection;
        CombineButton.IsEnabled = !_busy && hasSelection && _existingCombination is null && !outputExists;
        UpdateButton.IsEnabled = !_busy && _existingCombination is not null && _changedMods.Count > 0;
        OutputNameBox.IsEnabled = !_busy && _existingCombination is null;

        CombineButton.ToolTip = _existingCombination is not null
            ? "This source-mod combination has already been created."
            : outputExists
                ? "An output with this name already exists."
                : "Create this source-mod combination.";
        if (_existingCombination is null)
            UpdateButton.ToolTip = "Create this combination before it can be updated.";
        else if (_legacyCombination)
            UpdateButton.ToolTip = "This existing combined mod needs one tracked rebuild.";
        else if (_changedMods.Count == 0)
            UpdateButton.ToolTip = "The source mods have not changed.";
        else
            UpdateButton.ToolTip = "Changed: " + string.Join(", ", _changedMods);
    }

    private void UpdateProgress(CombineProgress progress)
    {
        BusyBar.Value = progress.Percent;
        ProgressText.Text = $"{progress.Stage} · {progress.Percent}%";
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void ShowError(Exception ex)
    {
        Log("ERROR: " + ex.Message);
        MessageBox.Show(ex.Message, "WARNO UltiAI MODerator", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static bool IsUlti(ModDescriptor mod) =>
        mod.Name.Contains("UltiAI", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(mod.RootPath).Contains("UltiAI", StringComparison.OrdinalIgnoreCase);
}
