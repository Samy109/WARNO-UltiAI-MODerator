using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using WarnoModerator.Core;

namespace WarnoModerator.App;

public partial class MainWindow : Window
{
    private readonly ModScanner _scanner = new();
    private readonly MergePlanner _planner = new(new SourceDeltaAnalyzer());
    private readonly ModFingerprintService _fingerprints = new();
    private readonly CombinedModStateStore _stateStore = new();
    private readonly CombineService _service = new(new SourceDeltaAnalyzer(), new ProcessRunner());
    private readonly CombinationHealth _health = new();
    private WarnoPaths? _paths;
    private bool _settingSelection;
    private bool _busy;
    private CancellationTokenSource? _operation;
    private CombinedModState? _existing;
    private IReadOnlyList<string> _changedMods = [];
    private MergePreview? _preview;
    private string _status = "Select two mods to combine.";

    public MainWindow() => InitializeComponent();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async token =>
        {
            var detected = await Task.Run(() => new WarnoLocator().Locate(), token);
            WarnoPathBox.Text = detected?.WarnoRoot ?? @"C:\Program Files (x86)\Steam\steamapps\common\WARNO";
            if (detected is null) { _status = "Choose the WARNO installation folder, then refresh mods."; return; }
            await RefreshModsAsync(token);
        }, "Finding WARNO");
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the WARNO installation folder", InitialDirectory = WarnoPathBox.Text };
        if (dialog.ShowDialog() != true) return;
        WarnoPathBox.Text = dialog.FolderName;
        await RunUiAsync(RefreshModsAsync, "Refreshing mods");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunUiAsync(RefreshModsAsync, "Refreshing mods");

    private async Task RefreshModsAsync(CancellationToken token)
    {
        var folder = WarnoPathBox.Text.Trim();
        var oldOther = (OtherModBox.SelectedItem as ModDescriptor)?.RootPath;
        var oldUlti = (UltiModBox.SelectedItem as ModDescriptor)?.RootPath;
        var paths = await Task.Run(() => new WarnoLocator().FromWarnoRoot(folder), token);
        var recovery = new BuildRecovery();
        var records = await Task.Run(() => recovery.Find(paths), token);
        foreach (var record in records)
        {
            token.ThrowIfCancellationRequested();
            if (!record.Committed && MessageBox.Show(
                $"An interrupted build of '{record.OutputName}' was found. Restore the previous outputs now? Incomplete output will be preserved separately.",
                "Recover interrupted build", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                Log($"Recovery pending for {record.OutputName}; rebuild remains blocked until it is recovered.");
                continue;
            }
            var log = new Progress<string>(Log);
            await Task.Run(() => recovery.Recover(paths, record, message => ((IProgress<string>)log).Report(message)), token);
        }
        var mods = await Task.Run(() => _scanner.Scan(paths, token), token);
        _paths = paths;
        _settingSelection = true;
        try
        {
            OtherModBox.ItemsSource = mods.Where(m => !IsUlti(m)).ToArray();
            UltiModBox.ItemsSource = mods.Where(IsUlti).OrderByDescending(m => m.Kind == ModKind.WorkshopCompiled)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            OtherModBox.SelectedItem = OtherModBox.Items.Cast<ModDescriptor>().FirstOrDefault(m => m.RootPath == oldOther);
            UltiModBox.SelectedItem = UltiModBox.Items.Cast<ModDescriptor>().FirstOrDefault(m => m.RootPath == oldUlti);
            if (OtherModBox.SelectedItem is null && OtherModBox.Items.Count > 0) OtherModBox.SelectedIndex = 0;
            if (UltiModBox.SelectedItem is null && UltiModBox.Items.Count > 0) UltiModBox.SelectedIndex = 0;
        }
        finally { _settingSelection = false; }
        Log($"Found {mods.Count(m => m.Kind == ModKind.EditableSource)} editable and {mods.Count(m => m.Kind == ModKind.WorkshopCompiled)} Workshop mods.");
        await CheckSelectionAsync(token);
    }

    private async void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingSelection && !_busy) await RunUiAsync(CheckSelectionAsync, "Checking selected mods");
    }

    private async Task CheckSelectionAsync(CancellationToken token)
    {
        _existing = null;
        _changedMods = [];
        ClearPreview();
        _status = "Select two mods to combine.";
        if (_paths is null || OtherModBox.SelectedItem is not ModDescriptor other || UltiModBox.SelectedItem is not ModDescriptor ulti) return;
        var paths = _paths;
        var choices = (await Task.Run(() => _stateStore.FindAllForSources(paths, other, ulti, token), token)).ToList();
        var defaultName = $"{other.Name} + {ulti.Name}";
        if (IsValidName(defaultName) && !choices.Any(x => x.OutputName.Equals(defaultName, StringComparison.OrdinalIgnoreCase))
            && CombinedModStateStore.OutputExists(paths, defaultName))
            choices.Add(new CombinedModState(CombinedModState.CurrentSchemaVersion, defaultName,
                new(other.Name, other.RootPath, ""), new(ulti.Name, ulti.RootPath, "")));
        var selectedName = (ExistingMergeBox.SelectedItem as CombinedModState)?.OutputName;
        _settingSelection = true;
        try
        {
            ExistingMergeBox.ItemsSource = choices.OrderBy(x => x.OutputName, StringComparer.OrdinalIgnoreCase).ToArray();
            ExistingMergeBox.SelectedItem = choices.FirstOrDefault(x => x.OutputName == selectedName) ?? choices.FirstOrDefault();
            OutputNameBox.Text = CombinedModStateStore.SuggestNewOutputName(paths, defaultName);
        }
        finally { _settingSelection = false; }
        await CheckExistingAsync(token);
    }

    private async void ExistingMerge_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingSelection && !_busy) await RunUiAsync(CheckExistingAsync, "Checking existing merge");
    }

    private async Task CheckExistingAsync(CancellationToken token)
    {
        _existing = ExistingMergeBox.SelectedItem as CombinedModState;
        _changedMods = [];
        ClearPreview();
        if (_existing is null || _paths is null || OtherModBox.SelectedItem is not ModDescriptor other
            || UltiModBox.SelectedItem is not ModDescriptor ulti)
        { _status = "Ready to create a new combination."; return; }
        var paths = _paths;        var current = await Task.Run(() => _fingerprints.ComputeAsync([other, ulti], ProgressReporter(), token), token);
        var changed = new List<string>();
        if (!CombinedModStateStore.FingerprintMatches(_existing.OtherMod, current[0])) changed.Add(other.Name);
        if (!CombinedModStateStore.FingerprintMatches(_existing.PriorityMod, current[1])) changed.Add(ulti.Name);
        _changedMods = changed;
        var state = _existing;
        var health = await Task.Run(() => _health.CheckAsync(paths, state, token), token);
        _status = (changed.Count > 0 ? "Changed: " + string.Join(", ", changed) + ". " : "Inputs match the last merge. ") + health;
    }

    private void OutputName_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_settingSelection && PreviewButton is not null) { ClearPreview(); UpdateActionStates(); }
    }

    private async Task<CombineRequest> GetRequestAsync(bool rebuild, CancellationToken token)
    {
        if (_paths is null || OtherModBox.SelectedItem is not ModDescriptor other || UltiModBox.SelectedItem is not ModDescriptor ulti)
            throw new CombineException("Select both a mod and an UltiAI priority variant.");
        var paths = _paths;
        if (!Path.GetFullPath(WarnoPathBox.Text.Trim()).Equals(paths.WarnoRoot, StringComparison.OrdinalIgnoreCase))
            throw new CombineException("The WARNO folder changed. Refresh mods first.");
        var name = rebuild ? _existing?.OutputName ?? throw new CombineException("Select an existing merge to rebuild.") : OutputNameBox.Text.Trim();
        return await Task.Run(() =>
        {
            var installed = _scanner.Scan(paths, token);
            var freshOther = installed.FirstOrDefault(m => m.RootPath.Equals(other.RootPath, StringComparison.OrdinalIgnoreCase))
                ?? throw new CombineException("The selected mod is no longer installed. Refresh mods.");
            var freshUlti = installed.FirstOrDefault(m => m.RootPath.Equals(ulti.RootPath, StringComparison.OrdinalIgnoreCase))
                ?? throw new CombineException("The selected priority mod is no longer installed. Refresh mods.");
            var plan = _planner.CreatePreview(paths, freshOther, freshUlti, name, rebuild, token);
            return new CombineRequest(paths, freshOther, freshUlti, name, plan);
        }, token);
    }

    private async void Preview_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async token =>
        DisplayPreview((await GetRequestAsync(_existing is not null, token)).Preview), "Planning merge");

    private async void Combine_Click(object sender, RoutedEventArgs e) => await RunUiAsync(token => BuildAsync(false, token), "Planning merge");
    private async void Update_Click(object sender, RoutedEventArgs e) => await RunUiAsync(token => BuildAsync(true, token), "Planning rebuild");

    private async Task BuildAsync(bool rebuild, CancellationToken token)
    {
        var request = await GetRequestAsync(rebuild, token);
        DisplayPreview(request.Preview);
        var action = rebuild ? "Rebuild" : "Create";
        if (MessageBox.Show($"{action} '{request.OutputName}'?\n\n{request.Preview.Decisions.Count:N0} preview paths; {request.Preview.OverrideCount:N0} differing files replaced by UltiAI."
            + (request.Preview.Provisional ? "\nEditable input will be regenerated; the final report will show the exact compiled conflicts." : ""),
            "Confirm merge", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var log = new Progress<string>(Log);
        var progress = ProgressReporter();
        var result = await Task.Run(() => rebuild
            ? _service.RebuildAsync(request, log, token, progress)
            : _service.CombineAsync(request, log, token, progress), token);
        _existing = result.State;
        _settingSelection = true;
        try
        {
            var choices = (ExistingMergeBox.ItemsSource as IEnumerable<CombinedModState> ?? [])
                .Where(x => !x.OutputName.Equals(result.State.OutputName, StringComparison.OrdinalIgnoreCase))
                .Append(result.State).OrderBy(x => x.OutputName, StringComparer.OrdinalIgnoreCase).ToArray();
            ExistingMergeBox.ItemsSource = choices;
            ExistingMergeBox.SelectedItem = result.State;
            OutputNameBox.Text = CombinedModStateStore.SuggestNewOutputName(request.Paths, $"{request.OtherMod.Name} + {request.UltiMod.Name}");
        }
        finally { _settingSelection = false; }
        _changedMods = [];
        _status = "Combined mod verified. The final merge report is available to inspect or export.";
        DisplayPreview(result.Plan);
        Log($"DONE: {result.OutputSourcePath}");
        MessageBox.Show($"Combined mod {(rebuild ? "rebuilt" : "created")} successfully.\n\n{result.OutputSourcePath}",
            "WARNO UltiAI MODerator", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task RunUiAsync(Func<CancellationToken, Task> action, string stage)
    {
        if (_busy) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        SetBusy(true, stage);
        try { await action(operation.Token); }
        catch (OperationCanceledException) { _status = "Operation cancelled. Any previous output was restored unless a recovery error was reported."; Log(_status); }
        catch (Exception ex) { _status = "Operation failed: " + ex.Message; ShowError(ex); }
        finally { _operation = null; SetBusy(false); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        CancelButton.IsEnabled = false;
        ProgressText.Text = "Cancelling; waiting for generation and recovery to finish…";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        Cancel_Click(this, new RoutedEventArgs());
    }

    private void DisplayPreview(MergePreview preview)
    {
        _preview = preview;
        ApplyPreviewFilter();
        foreach (var warning in preview.Warnings) Log("WARNING: " + warning);
    }

    private void ClearPreview() { _preview = null; PreviewGrid.ItemsSource = null; SummaryText.Text = ""; }
    private void Filter_Changed(object sender, RoutedEventArgs e) { if (PreviewGrid is not null) ApplyPreviewFilter(); }
    private void ApplyPreviewFilter()
    {
        if (_preview is null) return;
        var query = SearchBox.Text.Trim();
        var decisions = _preview.Decisions.Where(d =>
            (!ConflictsOnlyBox.IsChecked.GetValueOrDefault() || (d.IsCollision && !d.Identical))
            && (query.Length == 0 || d.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        PreviewGrid.ItemsSource = decisions;
        SummaryText.Text = $"{(_preview.Provisional ? "Provisional · " : "")}{decisions.Length:N0}/{_preview.Decisions.Count:N0} paths · {_preview.OverrideCount:N0} UltiAI replacements";
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_preview is null) return;
        var dialog = new SaveFileDialog { Title = "Export merge report", Filter = "JSON report|*.json", FileName = "merge-report.json" };
        if (dialog.ShowDialog() != true) return;
        var plan = _preview;
        var report = new { plan.OutputName, OtherMod = plan.OtherMod.Name, PriorityMod = plan.UltiMod.Name,
            plan.Provisional, plan.Warnings, Decisions = plan.Decisions.Select(d => new { d.RelativePath, Kind = d.Kind.ToString(), d.Winner, d.Detail, d.Identical, d.IsCollision }), Log = LogBox.Text };
        await RunUiAsync(async token =>
        {
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), token);
            Log($"Report exported to {dialog.FileName}");
        }, "Exporting report");
    }

    private IProgress<CombineProgress> ProgressReporter() => new Progress<CombineProgress>(p =>
    {
        if (!_busy || _operation?.IsCancellationRequested == true) return;
        BusyBar.Value = p.Percent;
        ProgressText.Text = $"{p.Stage} · {p.Percent}%";
    });

    private void SetBusy(bool busy, string stage = "Preparing")
    {
        _busy = busy;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = busy;
        if (busy) { BusyBar.Value = 0; ProgressText.Text = stage; }
        BrowseButton.IsEnabled = RefreshButton.IsEnabled = WarnoPathBox.IsEnabled = OtherModBox.IsEnabled = UltiModBox.IsEnabled = !busy;
        ExistingMergeBox.IsEnabled = !busy && ExistingMergeBox.Items.Count > 0;
        UpdateActionStates();
    }

    private void UpdateActionStates()
    {
        var selected = _paths is not null && OtherModBox.SelectedItem is ModDescriptor && UltiModBox.SelectedItem is ModDescriptor;
        var name = OutputNameBox.Text.Trim();
        var valid = IsValidName(name);
        var exists = valid && _paths is not null && (Directory.Exists(Path.Combine(_paths.ModsRoot, name)) || Directory.Exists(Path.Combine(_paths.SavedModsRoot, name)));
        PreviewButton.IsEnabled = !_busy && selected && valid;
        CombineButton.IsEnabled = !_busy && selected && valid && !exists;
        UpdateButton.IsEnabled = !_busy && selected && _existing is not null;
        UpdateButton.Content = _changedMods.Count > 0 ? "Update and Rebuild" : "Rebuild Existing";
        OutputNameBox.IsEnabled = !_busy;
        ExportButton.IsEnabled = !_busy && _preview is not null;
        StatusText.Text = _status;
        StatusText.Visibility = _busy ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool IsValidName(string name)
    { try { MergePlanner.ValidateOutputName(name); return true; } catch (CombineException) { return false; } }
    private void Log(string message) { LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"); LogBox.ScrollToEnd(); }
    private void ShowError(Exception ex) { Log("ERROR: " + ex.Message); MessageBox.Show(ex.Message, "WARNO UltiAI MODerator", MessageBoxButton.OK, MessageBoxImage.Error); }
    private static bool IsUlti(ModDescriptor mod) => mod.Name.Contains("UltiAI", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(mod.RootPath).Contains("UltiAI", StringComparison.OrdinalIgnoreCase);
}
