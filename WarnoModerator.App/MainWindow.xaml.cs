using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using WarnoModerator.Core;

namespace WarnoModerator.App;

public sealed record OrderedMod(int Index, ModDescriptor Mod)
{
    public string Rank => $"{Index + 1}.";
    public Visibility PrimaryVisibility => Index == 0 ? Visibility.Visible : Visibility.Collapsed;
}

public partial class MainWindow : Window
{
    private readonly ModScanner _scanner = new();
    private readonly MergePlanner _planner = new(new SourceDeltaAnalyzer());
    private readonly ModFingerprintService _fingerprints = new();
    private readonly CombinedModStateStore _stateStore = new();
    private readonly CombineService _service = new(new SourceDeltaAnalyzer(), new ProcessRunner());
    private readonly CombinationHealth _health = new();
    private WarnoPaths? _paths;
    private IReadOnlyList<ModDescriptor> _installed = [];
    private readonly List<ModDescriptor> _order = [];
    private bool _settingSelection;
    private bool _busy;
    private CancellationTokenSource? _operation;
    private CombinedModState? _existing;
    private IReadOnlyList<string> _changedMods = [];
    private string? _previewError;
    private MergePreview? _preview;
    private string _lastLoggedPreviewKey = "";
    private string _status = "Add at least two mods to combine.";

    public MainWindow() => InitializeComponent();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async token =>
        {
            var detected = await Task.Run(() => new WarnoLocator().Locate(), token);
            WarnoPathBox.Text = detected?.WarnoRoot ?? @"C:\Program Files (x86)\Steam\steamapps\common\WARNO";
            if (detected is null)
            {
                _status = "Choose the WARNO installation folder, then refresh mods.";
                return;
            }
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
        var previousOrder = _order.Select(m => m.RootPath).ToArray();
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
                Log($"[RECOVERY] Recovery pending for {record.OutputName}; rebuild remains blocked until it is recovered.");
                continue;
            }
            var log = new Progress<string>(Log);
            await Task.Run(() => recovery.Recover(paths, record, message => ((IProgress<string>)log).Report("[RECOVERY] " + message)), token);
        }

        var mods = await Task.Run(() => _scanner.Scan(paths, token), token);
        var existingStates = await Task.Run(() => _stateStore.FindAll(paths, token), token);
        _paths = paths;
        // UltiAI is always listed first; everything else follows alphabetically.
        _installed = mods.OrderByDescending(IsUlti).ThenByDescending(m => IsUlti(m) && m.Kind == ModKind.WorkshopCompiled)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        var restored = previousOrder.Select(Find).OfType<ModDescriptor>().ToList();
        if (restored.Count == 0)
        {
            var matched = existingStates.FirstOrDefault(s => s.Sources.All(source => Find(source.RootPath) is not null));
            if (matched is not null) restored = matched.Sources.Select(source => Find(source.RootPath)!).ToList();
        }
        if (restored.Count == 0 && _installed.FirstOrDefault(IsUlti) is { } ulti) restored.Add(ulti);
        _order.Clear();
        _order.AddRange(restored);
        RefreshModLists();

        Log($"[SCAN] Found {mods.Count(m => m.Kind == ModKind.WorkshopCompiled)} Workshop and {mods.Count(m => m.Kind == ModKind.EditableSource)} editable SDK mods.");
        await CheckSelectionAsync(token);
    }

    private ModDescriptor? Find(string? root) => _installed.FirstOrDefault(m => SamePath(m.RootPath, root));

    private void RefreshModLists(ModDescriptor? select = null)
    {
        _settingSelection = true;
        try
        {
            MergeOrderList.ItemsSource = _order.Select((m, i) => new OrderedMod(i, m)).ToArray();
            if (select is not null) MergeOrderList.SelectedIndex = _order.IndexOf(select);
            AvailableModBox.ItemsSource = _installed.Where(m => !_order.Any(o => SamePath(o.RootPath, m.RootPath))).ToArray();
            AvailableModBox.SelectedIndex = AvailableModBox.Items.Count > 0 ? 0 : -1;
        }
        finally { _settingSelection = false; }
        UpdateOrderButtons();
    }

    private ModDescriptor? SelectedOrderedMod => (MergeOrderList.SelectedItem as OrderedMod)?.Mod;

    private async Task ChangeOrderAsync(Action change, ModDescriptor? select)
    {
        change();
        RefreshModLists(select);
        await RunUiAsync(CheckSelectionAsync, "Checking selected mods");
    }

    private async void AddMod_Click(object sender, RoutedEventArgs e)
    {
        if (AvailableModBox.SelectedItem is not ModDescriptor mod) return;
        await ChangeOrderAsync(() => _order.Add(mod), mod);
    }

    private async void MakePrimary_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedOrderedMod is not { } mod) return;
        await ChangeOrderAsync(() => { _order.Remove(mod); _order.Insert(0, mod); }, mod);
    }

    private async void MoveUp_Click(object sender, RoutedEventArgs e) => await MoveAsync(-1);
    private async void MoveDown_Click(object sender, RoutedEventArgs e) => await MoveAsync(1);

    private async Task MoveAsync(int delta)
    {
        if (SelectedOrderedMod is not { } mod) return;
        var index = _order.IndexOf(mod);
        var target = index + delta;
        if (target < 0 || target >= _order.Count) return;
        await ChangeOrderAsync(() => { _order.RemoveAt(index); _order.Insert(target, mod); }, mod);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedOrderedMod is not { } mod) return;
        await ChangeOrderAsync(() => _order.Remove(mod), null);
    }

    private void MergeOrder_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateOrderButtons();

    private void UpdateOrderButtons()
    {
        if (PrimaryButton is null) return;
        var index = SelectedOrderedMod is { } mod ? _order.IndexOf(mod) : -1;
        PrimaryButton.IsEnabled = !_busy && index > 0;
        UpButton.IsEnabled = !_busy && index > 0;
        DownButton.IsEnabled = !_busy && index >= 0 && index < _order.Count - 1;
        RemoveButton.IsEnabled = !_busy && index >= 0;
        AddModButton.IsEnabled = !_busy && AvailableModBox.SelectedItem is not null;
    }

    private string DefaultOutputName => string.Join(" + ", _order.Select(m => m.Name));

    private async Task CheckSelectionAsync(CancellationToken token)
    {
        _existing = null;
        _changedMods = [];
        _previewError = null;
        ClearPreview();
        _status = "Add at least two mods to combine.";
        _settingSelection = true;
        try { ExistingMergeBox.ItemsSource = null; }
        finally { _settingSelection = false; }
        if (_paths is null || _order.Count < 2) return;

        var paths = _paths;
        var mods = _order.ToArray();
        var choices = (await Task.Run(() => _stateStore.FindAllForSources(paths, mods, token), token)).ToList();
        var defaultName = DefaultOutputName;
        if (IsValidName(defaultName) && !choices.Any(x => x.OutputName.Equals(defaultName, StringComparison.OrdinalIgnoreCase))
            && CombinedModStateStore.OutputExists(paths, defaultName))
            choices.Add(new CombinedModState(CombinedModState.CurrentSchemaVersion, defaultName,
                mods.Select(m => new SourceModFingerprint(m.Name, m.RootPath, "")).ToArray()));
        var selectedName = _existing?.OutputName;
        _settingSelection = true;
        try
        {
            ExistingMergeBox.ItemsSource = choices.OrderBy(x => x.OutputName, StringComparer.OrdinalIgnoreCase).ToArray();
            ExistingMergeBox.SelectedItem = choices.FirstOrDefault(x => x.OutputName == selectedName) ?? choices.FirstOrDefault();
            _existing = ExistingMergeBox.SelectedItem as CombinedModState;
            OutputNameBox.Text = CombinedModStateStore.SuggestNewOutputName(paths, defaultName);
        }
        finally { _settingSelection = false; }

        await TryGeneratePreviewAsync(token);
        await Task.Yield();
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
        if (_existing is null || _paths is null || _order.Count < 2)
        {
            if (_previewError is null && _order.Count >= 2) _status = "Ready to create a new combination.";
            return;
        }
        var paths = _paths;
        var state = _existing;
        var mods = _order.ToArray();
        int sourcePct = 0, outputPct = 0;
        var uiProgress = ProgressReporter();
        void ReportCombined()
        {
            var combined = (Volatile.Read(ref sourcePct) + Volatile.Read(ref outputPct)) / 2;
            uiProgress.Report(new CombineProgress(combined, $"Checking '{state.OutputName}'"));
        }
        var sourceProgress = new Progress<CombineProgress>(p => { Interlocked.Exchange(ref sourcePct, p.Percent); ReportCombined(); });
        var outputProgress = new Progress<CombineProgress>(p => { Interlocked.Exchange(ref outputPct, p.Percent); ReportCombined(); });

        var currentTask = Task.Run(() => _fingerprints.ComputeAsync(mods, sourceProgress, token), token);
        var healthTask = Task.Run(() => _health.CheckAsync(paths, state, token, outputProgress), token);
        await Task.WhenAll(currentTask, healthTask);

        var current = await currentTask;
        var health = await healthTask;
        var changed = current.Where(fingerprint => !state.Sources.Any(stored => CombinedModStateStore.FingerprintMatches(stored, fingerprint)))
            .Select(fingerprint => fingerprint.Name).ToList();
        var reordered = !state.Sources.Select(s => s.RootPath).SequenceEqual(mods.Select(m => m.RootPath), StringComparer.OrdinalIgnoreCase);
        _changedMods = changed;
        _status = (changed.Count > 0 ? "Changed: " + string.Join(", ", changed) + ". " : "Inputs match the last merge. ")
            + (reordered ? "Priority order differs from the last build. " : "") + health;
        Log($"[CHECK] '{state.OutputName}': {_status}");
    }

    private async Task TryGeneratePreviewAsync(CancellationToken token)
    {
        if (_paths is null || _order.Count < 2) return;
        var paths = _paths;
        var mods = _order.ToArray();
        var candidateName = OutputNameBox.Text.Trim();
        if (!IsValidName(candidateName))
            candidateName = _existing?.OutputName ?? CombinedModStateStore.SuggestNewOutputName(paths, DefaultOutputName);

        try
        {
            ProgressReporter().Report(new CombineProgress(50, "Delta merging databases for preview"));
            var preview = await Task.Run(() => _planner.CreatePreview(paths, mods, candidateName, allowExistingOutput: true, token), token);
            _previewError = null;
            DisplayPreview(preview);
        }
        catch (CombineException ex)
        {
            _previewError = ex.Message;
            ClearPreview();
            _status = ex.Message;
            Log($"[WARN] {ex.Message}");
        }
    }

    private void OutputName_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_settingSelection && CombineButton is not null) UpdateActionStates();
    }

    private async Task<CombineRequest> GetRequestAsync(bool rebuild, CancellationToken token)
    {
        if (_paths is null || _order.Count < 2) throw new CombineException("Add at least two mods to combine.");
        var paths = _paths;
        if (!Path.GetFullPath(WarnoPathBox.Text.Trim()).Equals(paths.WarnoRoot, StringComparison.OrdinalIgnoreCase))
            throw new CombineException("The WARNO folder changed. Refresh mods first.");
        var name = rebuild ? _existing?.OutputName ?? throw new CombineException("Select an existing merge to rebuild.") : OutputNameBox.Text.Trim();
        var selected = _order.ToArray();
        return await Task.Run(() =>
        {
            var installed = _scanner.Scan(paths, token);
            var fresh = selected.Select(mod => installed.FirstOrDefault(m => m.RootPath.Equals(mod.RootPath, StringComparison.OrdinalIgnoreCase))
                ?? throw new CombineException($"{mod.Name} is no longer installed. Refresh mods.")).ToArray();
            var plan = _planner.CreatePreview(paths, fresh, name, rebuild, token);
            return new CombineRequest(paths, fresh, name, plan);
        }, token);
    }

    private async void Combine_Click(object sender, RoutedEventArgs e) => await RunUiAsync(token => BuildAsync(false, token), "Planning merge");
    private async void Update_Click(object sender, RoutedEventArgs e) => await RunUiAsync(token => BuildAsync(true, token), "Planning rebuild");

    private async Task BuildAsync(bool rebuild, CancellationToken token)
    {
        var request = await GetRequestAsync(rebuild, token);
        _previewError = null;
        DisplayPreview(request.Preview);
        var action = rebuild ? "Rebuild" : "Create";
        var changedLine = rebuild && _changedMods.Count > 0
            ? $"Updated source mod(s): {string.Join(", ", _changedMods)}\n\n"
            : "";
        if (MessageBox.Show($"{action} '{request.OutputName}'?\n\n{changedLine}Priority: {string.Join(" > ", request.Mods.Select(m => m.Name))}\n\n"
            + request.Preview.Summary + "."
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
            OutputNameBox.Text = CombinedModStateStore.SuggestNewOutputName(request.Paths, DefaultOutputName);
        }
        finally { _settingSelection = false; }
        _changedMods = [];
        _status = "Combined mod verified. The final merge report is available to inspect or export.";
        DisplayPreview(result.Plan, forceLog: true);
        Log($"[DONE] {result.OutputSourcePath}");
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
        catch (OperationCanceledException) { _status = "Operation cancelled. Any previous output was restored unless a recovery error was reported."; Log("[CANCEL] " + _status); }
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

    private void DisplayPreview(MergePreview preview, bool forceLog = false)
    {
        _preview = preview;
        ApplyPreviewFilter();
        var previewKey = $"{string.Join("|", preview.Mods.Select(m => m.RootPath))}|{preview.Decisions.Count}|{preview.Conflicts.Count}|{preview.Provisional}";
        if (forceLog || !string.Equals(_lastLoggedPreviewKey, previewKey, StringComparison.OrdinalIgnoreCase))
        {
            _lastLoggedPreviewKey = previewKey;
            Log($"[PLAN] {string.Join(" > ", preview.Mods.Select(m => $"'{m.Name}'"))}: {preview.Summary}.");
            foreach (var warning in preview.Warnings) Log("[NOTE] " + warning);
        }
    }

    private void ClearPreview() { _preview = null; PreviewGrid.ItemsSource = null; SummaryText.Text = ""; }
    private void Filter_Changed(object sender, RoutedEventArgs e) { if (PreviewGrid is not null) ApplyPreviewFilter(); }
    private void ApplyPreviewFilter()
    {
        if (_preview is null) return;
        var query = SearchBox.Text.Trim();
        var all = _preview.Decisions.Concat(_preview.Conflicts).ToArray();
        var decisions = all.Where(d =>
            (!ConflictsOnlyBox.IsChecked.GetValueOrDefault() || (d.IsCollision && !d.Identical))
            && (query.Length == 0
                || d.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase)
                || d.ActionLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || d.Winner.Contains(query, StringComparison.OrdinalIgnoreCase)
                || d.Detail.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        PreviewGrid.ItemsSource = decisions;
        SummaryText.Text = $"{(_preview.Provisional ? "Provisional · " : "")}{decisions.Length:N0}/{all.Length:N0} rows · {_preview.Summary}";
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_preview is null) return;
        var dialog = new SaveFileDialog { Title = "Export merge report", Filter = "JSON report|*.json", FileName = "merge-report.json" };
        if (dialog.ShowDialog() != true) return;
        var plan = _preview;
        static object Row(MergeDecision d) => new
        {
            d.RelativePath,
            Kind = d.Kind.ToString(),
            Action = d.ActionLabel,
            d.Winner,
            d.Detail,
            d.Identical,
            d.IsCollision
        };
        var report = new
        {
            plan.OutputName,
            PriorityOrder = plan.Mods.Select(m => m.Name),
            Primary = plan.Primary.Name,
            plan.Provisional,
            Summary = new
            {
                TotalFiles = plan.Decisions.Count,
                plan.MergedCount,
                plan.ValueConflictCount,
                plan.OverrideCount,
                plan.SingleCount,
                plan.IdenticalCount
            },
            plan.Warnings,
            Decisions = plan.Decisions.Select(Row),
            ValueConflicts = plan.Conflicts.Select(Row),
            Log = LogBox.Text
        };
        await RunUiAsync(async token =>
        {
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), token);
            Log($"[EXPORT] Report exported to {dialog.FileName}");
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
        BrowseButton.IsEnabled = RefreshButton.IsEnabled = WarnoPathBox.IsEnabled = AvailableModBox.IsEnabled = MergeOrderList.IsEnabled = !busy;
        ExistingMergeBox.IsEnabled = !busy && ExistingMergeBox.Items.Count > 0;
        UpdateOrderButtons();
        UpdateActionStates();
    }

    private void UpdateActionStates()
    {
        var selected = _paths is not null && _order.Count >= 2;
        var name = OutputNameBox.Text.Trim();
        var valid = IsValidName(name);
        var exists = valid && _paths is not null && (Directory.Exists(Path.Combine(_paths.ModsRoot, name)) || Directory.Exists(Path.Combine(_paths.SavedModsRoot, name)));
        var compatible = _previewError is null;

        CombineButton.IsEnabled = !_busy && selected && valid && !exists && compatible;
        UpdateButton.IsEnabled = !_busy && selected && _existing is not null && compatible;
        UpdateButton.Content = _changedMods.Count > 0 ? "Update and Rebuild" : "Rebuild Existing";
        OutputNameBox.IsEnabled = !_busy;
        ExportButton.IsEnabled = !_busy && _preview is not null;

        CombineButton.ToolTip = !selected
            ? "Add at least two mods to combine."
            : !compatible
                ? _previewError
                : !valid
                    ? "Enter a valid Windows folder name."
                    : exists
                        ? $"An output named '{name}' already exists."
                        : $"Create a new combined mod named '{name}'.";

        UpdateButton.ToolTip = _existing is null
            ? "No existing merge for these mods yet. Use 'Create as New' first."
            : !compatible
                ? _previewError
                : _changedMods.Count > 0
                    ? "Changed: " + string.Join(", ", _changedMods)
                    : $"Rebuild and verify '{_existing.OutputName}' in place using the current priority order.";

        StatusText.Text = _status;
        StatusText.Visibility = _busy ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool SamePath(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool IsValidName(string name)
    { try { MergePlanner.ValidateOutputName(name); return true; } catch (CombineException) { return false; } }
    private void Log(string message) { LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"); LogBox.ScrollToEnd(); }
    private void ShowError(Exception ex) { Log("[ERROR] " + ex.Message); MessageBox.Show(ex.Message, "WARNO UltiAI MODerator", MessageBoxButton.OK, MessageBoxImage.Error); }
    private static bool IsUlti(ModDescriptor mod) => mod.Name.Contains("UltiAI", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(mod.RootPath).Contains("UltiAI", StringComparison.OrdinalIgnoreCase);
}
