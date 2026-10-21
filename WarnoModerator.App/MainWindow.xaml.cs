using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private string _healthMessage = "";
    private string? _previewError;
    private MergePreview? _preview;
    private string _lastLoggedPreviewKey = "";
    private string _status = "Select a Base Mod and a Priority Mod (UltiAI) to combine.";
    private string _statusBadge = "READY";
    private int _logEntries;

    private static readonly (SolidColorBrush Bg, SolidColorBrush Border, SolidColorBrush Fg) ThemeWarn =
        (Brush("#4D3C19"), Brush("#9E7B32"), Brush("#FFE4A3"));
    private static readonly (SolidColorBrush Bg, SolidColorBrush Border, SolidColorBrush Fg) ThemeError =
        (Brush("#4A2323"), Brush("#944646"), Brush("#F7C1C1"));
    private static readonly (SolidColorBrush Bg, SolidColorBrush Border, SolidColorBrush Fg) ThemeVerified =
        (Brush("#263D21"), Brush("#5E8C52"), Brush("#D4F2C9"));
    private static readonly (SolidColorBrush Bg, SolidColorBrush Border, SolidColorBrush Fg) ThemeReady =
        (Brush("#2E3B28"), Brush("#657853"), Brush("#CFE0A9"));

    public MainWindow() => InitializeComponent();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RunUiAsync(async token =>
        {
            Log("[INIT] WARNO UltiAI MODerator v1.5.0 started. Detecting Steam & WARNO directories…");
            var detected = await Task.Run(() => new WarnoLocator().Locate(), token);
            WarnoPathBox.Text = detected?.WarnoRoot ?? @"C:\Program Files (x86)\Steam\steamapps\common\WARNO";
            if (detected is null)
            {
                _statusBadge = "SETUP";
                _status = "WARNO installation was not auto-detected. Click 'Browse…' to select your WARNO folder, then click 'Refresh Mods'.";
                EnvironmentSummaryText.Text = "WARNO installation not found automatically — select your steamapps\\common\\WARNO folder above.";
                Log("[WARN] Could not auto-detect WARNO from Steam library folders. Waiting for manual folder selection.");
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
        var oldOther = (OtherModBox.SelectedItem as ModDescriptor)?.RootPath;
        var oldUlti = (UltiModBox.SelectedItem as ModDescriptor)?.RootPath;
        var paths = await Task.Run(() => new WarnoLocator().FromWarnoRoot(folder), token);
        Log($"[ENV] WARNO root: {paths.WarnoRoot}");
        Log($"[ENV] Workshop content (1611600): {paths.WorkshopRoot}");
        Log($"[ENV] Saved Games runtime mods: {paths.SavedModsRoot}");

        var recovery = new BuildRecovery();
        var records = await Task.Run(() => recovery.Find(paths), token);
        foreach (var record in records)
        {
            token.ThrowIfCancellationRequested();
            if (!record.Committed && MessageBox.Show(
                $"An interrupted build of '{record.OutputName}' was found. Restore the previous outputs now? Incomplete output will be preserved separately.",
                "Recover interrupted build", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                Log($"[RECOVERY] Recovery deferred for '{record.OutputName}'; rebuild remains blocked until recovered.");
                continue;
            }
            var log = new Progress<string>(Log);
            await Task.Run(() => recovery.Recover(paths, record, message => ((IProgress<string>)log).Report("[RECOVERY] " + message)), token);
        }

        var mods = await Task.Run(() => _scanner.Scan(paths, token), token);
        _paths = paths;
        var baseMods = mods.Where(m => !IsUlti(m)).ToArray();
        var ultiMods = mods.Where(IsUlti).OrderByDescending(m => m.Kind == ModKind.WorkshopCompiled)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var workshopCount = mods.Count(m => m.Kind == ModKind.WorkshopCompiled);
        var editableCount = mods.Count(m => m.Kind == ModKind.EditableSource);

        EnvironmentSummaryText.Text = $"Discovered {workshopCount} Workshop mod(s) and {editableCount} editable SDK mod(s)  ·  "
            + $"{baseMods.Length} Base Mod choice(s), {ultiMods.Length} UltiAI Priority variant(s)  ·  Workshop dir: {paths.WorkshopRoot}";

        _settingSelection = true;
        try
        {
            OtherModBox.ItemsSource = baseMods;
            UltiModBox.ItemsSource = ultiMods;
            OtherModBox.SelectedItem = OtherModBox.Items.Cast<ModDescriptor>().FirstOrDefault(m => m.RootPath == oldOther);
            UltiModBox.SelectedItem = UltiModBox.Items.Cast<ModDescriptor>().FirstOrDefault(m => m.RootPath == oldUlti);
            if (OtherModBox.SelectedItem is null && OtherModBox.Items.Count > 0) OtherModBox.SelectedIndex = 0;
            if (UltiModBox.SelectedItem is null && UltiModBox.Items.Count > 0) UltiModBox.SelectedIndex = 0;
        }
        finally { _settingSelection = false; }

        Log($"[SCAN] Found {workshopCount} Workshop compiled mod(s) and {editableCount} editable SDK source mod(s).");
        if (ultiMods.Length > 0)
            Log($"[SCAN] Priority UltiAI variant(s): {string.Join(", ", ultiMods.Select(m => $"{m.Name} ({m.KindLabel}, {m.ModGenLabel})"))}");
        else
            Log("[WARN] No installed UltiAI or UltiAIDEV mod was found. Subscribe to UltiAI on the Steam Workshop and click 'Refresh Mods'.");

        await CheckSelectionAsync(token);
    }

    private async void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingSelection && !_busy) await RunUiAsync(CheckSelectionAsync, "Inspecting selected mods");
    }

    private async Task CheckSelectionAsync(CancellationToken token)
    {
        _existing = null;
        _changedMods = [];
        _healthMessage = "";
        _previewError = null;
        ClearPreview();
        UpdateModMetadataLabels();

        if (_paths is null || OtherModBox.SelectedItem is not ModDescriptor other || UltiModBox.SelectedItem is not ModDescriptor ulti)
        {
            _statusBadge = "SELECT MODS";
            _status = UltiModBox.Items.Count == 0
                ? "No UltiAI mod found. Subscribe to UltiAI in the Steam Workshop and click 'Refresh Mods'."
                : "Select both a Base Mod and a Priority Mod (UltiAI) to preview and combine.";
            return;
        }

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
        await TryGeneratePreviewAsync(token);
    }

    private async void ExistingMerge_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingSelection && !_busy)
        {
            await RunUiAsync(async token =>
            {
                await CheckExistingAsync(token);
                if (_preview is null) await TryGeneratePreviewAsync(token);
            }, "Checking existing combined mod");
        }
    }

    private async Task CheckExistingAsync(CancellationToken token)
    {
        _existing = ExistingMergeBox.SelectedItem as CombinedModState;
        _changedMods = [];
        _healthMessage = "";
        if (_existing is null || _paths is null || OtherModBox.SelectedItem is not ModDescriptor other
            || UltiModBox.SelectedItem is not ModDescriptor ulti)
        {
            _statusBadge = "READY";
            _status = "No existing combined mod for this pair yet. Review the merge plan below and click 'Create as New'.";
            return;
        }
        var paths = _paths;
        var progress = ProgressReporter();
        var current = await Task.Run(() => _fingerprints.ComputeAsync([other, ulti], progress, token), token);
        var changed = new List<string>();
        if (!CombinedModStateStore.FingerprintMatches(_existing.OtherMod, current[0])) changed.Add(other.Name);
        if (!CombinedModStateStore.FingerprintMatches(_existing.PriorityMod, current[1])) changed.Add(ulti.Name);
        _changedMods = changed;
        var state = _existing;
        var health = await Task.Run(() => _health.CheckAsync(paths, state, token), token);
        _healthMessage = health;
        var legacy = string.IsNullOrEmpty(state.OtherMod.Fingerprint) || state.RuntimeFingerprint is null;
        var needsRebuild = changed.Count > 0 || legacy || !health.StartsWith("Combined output matches", StringComparison.OrdinalIgnoreCase);

        _statusBadge = changed.Count > 0 ? "UPDATE AVAILABLE" : needsRebuild ? "REBUILD NEEDED" : "VERIFIED";
        var sourceSummary = legacy
            ? $"Existing merge '{state.OutputName}' is from an older untracked build. "
            : changed.Count > 0
                ? $"Source update detected in: {string.Join(", ", changed)}. "
                : $"Source mods match the last build of '{state.OutputName}'. ";
        _status = sourceSummary + health;
        Log($"[CHECK] Existing merge '{state.OutputName}': {sourceSummary.Trim()} | {health}");
    }

    private async Task TryGeneratePreviewAsync(CancellationToken token)
    {
        if (_paths is null || OtherModBox.SelectedItem is not ModDescriptor other || UltiModBox.SelectedItem is not ModDescriptor ulti)
            return;
        var paths = _paths;
        var candidateName = OutputNameBox.Text.Trim();
        if (!IsValidName(candidateName))
            candidateName = _existing?.OutputName ?? CombinedModStateStore.SuggestNewOutputName(paths, $"{other.Name} + {ulti.Name}");

        try
        {
            var preview = await Task.Run(() => _planner.CreatePreview(paths, other, ulti, candidateName, allowExistingOutput: true, token), token);
            _previewError = null;
            DisplayPreview(preview);
        }
        catch (CombineException ex)
        {
            _previewError = ex.Message;
            ClearPreview();
            _statusBadge = "ATTENTION";
            _status = $"Cannot combine selected mods: {ex.Message}";
            Log($"[WARN] Compatibility check blocked preview: {ex.Message}");
        }
    }

    private void OutputName_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_settingSelection && PreviewButton is not null) UpdateActionStates();
    }

    private async Task<CombineRequest> GetRequestAsync(bool rebuild, CancellationToken token, bool previewOnly = false)
    {
        if (_paths is null || OtherModBox.SelectedItem is not ModDescriptor other || UltiModBox.SelectedItem is not ModDescriptor ulti)
            throw new CombineException("Select both a Base Mod and an UltiAI Priority Mod.");
        var paths = _paths;
        if (!Path.GetFullPath(WarnoPathBox.Text.Trim()).Equals(paths.WarnoRoot, StringComparison.OrdinalIgnoreCase))
            throw new CombineException("The WARNO folder path changed. Click 'Refresh Mods' first.");
        var name = rebuild
            ? _existing?.OutputName ?? throw new CombineException("Select an Existing Combined Mod to rebuild.")
            : OutputNameBox.Text.Trim();
        if (previewOnly && !IsValidName(name))
            name = _existing?.OutputName ?? CombinedModStateStore.SuggestNewOutputName(paths, $"{other.Name} + {ulti.Name}");

        return await Task.Run(() =>
        {
            var installed = _scanner.Scan(paths, token);
            var freshOther = installed.FirstOrDefault(m => m.RootPath.Equals(other.RootPath, StringComparison.OrdinalIgnoreCase))
                ?? throw new CombineException("The selected Base Mod is no longer installed. Click 'Refresh Mods'.");
            var freshUlti = installed.FirstOrDefault(m => m.RootPath.Equals(ulti.RootPath, StringComparison.OrdinalIgnoreCase))
                ?? throw new CombineException("The selected Priority Mod is no longer installed. Click 'Refresh Mods'.");
            var plan = _planner.CreatePreview(paths, freshOther, freshUlti, name, rebuild || previewOnly, token);
            return new CombineRequest(paths, freshOther, freshUlti, name, plan);
        }, token);
    }

    private async void Preview_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async token =>
    {
        var request = await GetRequestAsync(_existing is not null, token, previewOnly: true);
        _previewError = null;
        DisplayPreview(request.Preview, forceLog: true);
        if (_existing is null)
        {
            _statusBadge = "READY";
            _status = $"Preview refreshed ({request.Preview.Decisions.Count:N0} files). Ready to create '{OutputNameBox.Text.Trim()}'.";
        }
    }, "Planning merge");

    private async void Combine_Click(object sender, RoutedEventArgs e) => await RunUiAsync(token => BuildAsync(false, token), "Planning merge");
    private async void Update_Click(object sender, RoutedEventArgs e) => await RunUiAsync(token => BuildAsync(true, token), "Planning rebuild");

    private async Task BuildAsync(bool rebuild, CancellationToken token)
    {
        var request = await GetRequestAsync(rebuild, token);
        _previewError = null;
        DisplayPreview(request.Preview);

        var actionTitle = rebuild
            ? (_changedMods.Count > 0 ? "Update and Rebuild Combined Mod" : "Rebuild Existing Combined Mod")
            : "Create New Combined Mod";
        var changedSection = rebuild && _changedMods.Count > 0
            ? $"\nUpdated source mod(s): {string.Join(", ", _changedMods)}\n"
            : "";
        var summaryMessage =
            $"{(rebuild ? "Rebuild" : "Create")} '{request.OutputName}'?\n"
            + changedSection
            + $"\n• Base Mod: {request.OtherMod.Name} ({request.OtherMod.KindLabel}, {request.OtherMod.ModGenLabel})"
            + $"\n• Priority Mod: {request.UltiMod.Name} ({request.UltiMod.KindLabel}, {request.UltiMod.ModGenLabel})"
            + $"\n\nMerge Plan Breakdown ({request.Preview.Decisions.Count:N0} total files):"
            + $"\n• {request.Preview.UltiAppliedCount:N0} UltiAI files applied ({request.Preview.OverrideCount:N0} override Base Mod, {request.Preview.UltiAddedCount:N0} added)"
            + $"\n• {request.Preview.BaseUiPreservedCount:N0} Base Mod UI database(s) preserved"
            + $"\n• {request.Preview.BaseKeptCount:N0} Base Mod-only files kept"
            + $"\n• {request.Preview.IdenticalCount:N0} identical overlapping files"
            + (request.Preview.Provisional ? "\n\nNote: Editable SDK source input will be compiled by WARNO during the build before final verification." : "");

        if (MessageBox.Show(summaryMessage, actionTitle, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

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
        _healthMessage = "Combined output matches the last merge. You can rebuild again.";
        _statusBadge = "VERIFIED";
        _status = $"'{result.State.OutputName}' {(rebuild ? "rebuilt" : "created")} and SHA-256 verified ({result.Plan.Decisions.Count:N0} files). Enable it in WARNO's Mod Manager!";
        DisplayPreview(result.Plan, forceLog: true);
        Log($"[DONE] Source output: {result.OutputSourcePath}");
        Log($"[DONE] Runtime output: {result.OutputRuntimePath}");
        MessageBox.Show(
            $"Combined mod '{result.State.OutputName}' {(rebuild ? "rebuilt" : "created")} and verified successfully.\n\n"
            + $"Local Mod Source:\n{result.OutputSourcePath}\n\n"
            + $"WARNO Runtime Mod:\n{result.OutputRuntimePath}",
            "WARNO UltiAI MODerator", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task RunUiAsync(Func<CancellationToken, Task> action, string stage)
    {
        if (_busy) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        SetBusy(true, stage);
        try { await action(operation.Token); }
        catch (OperationCanceledException)
        {
            _statusBadge = "CANCELLED";
            _status = "Operation cancelled. Any previous combined mod output was restored automatically.";
            Log("[CANCEL] " + _status);
        }
        catch (Exception ex)
        {
            _statusBadge = "ERROR";
            _status = "Operation failed: " + ex.Message;
            ShowError(ex);
        }
        finally { _operation = null; SetBusy(false); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        CancelButton.IsEnabled = false;
        ProgressText.Text = "Cancelling; waiting for SDK process and rollback recovery to finish…";
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
        ProvisionalBadge.Visibility = preview.Provisional ? Visibility.Visible : Visibility.Collapsed;
        StatTotalText.Text = $"Total: {preview.Decisions.Count:N0}";
        StatOverrideText.Text = $"UltiAI Overrides: {preview.OverrideCount:N0}";
        StatAddedText.Text = $"UltiAI Added: {preview.UltiAddedCount:N0}";
        StatUiKeptText.Text = $"Base UI Kept: {preview.BaseUiPreservedCount:N0}";
        StatBaseKeptText.Text = $"Base Mod Kept: {preview.BaseKeptCount:N0}";
        StatIdenticalText.Text = $"Identical: {preview.IdenticalCount:N0}";

        if (preview.Warnings.Count > 0)
        {
            WarningsBorder.Visibility = Visibility.Visible;
            WarningsText.Text = string.Join(Environment.NewLine, preview.Warnings.Select(w => "• " + w));
        }
        else
        {
            WarningsBorder.Visibility = Visibility.Collapsed;
            WarningsText.Text = "";
        }

        ApplyPreviewFilter();

        var previewKey = $"{preview.OtherMod.RootPath}|{preview.UltiMod.RootPath}|{preview.Decisions.Count}|{preview.Provisional}";
        if (forceLog || !string.Equals(_lastLoggedPreviewKey, previewKey, StringComparison.OrdinalIgnoreCase))
        {
            _lastLoggedPreviewKey = previewKey;
            Log($"[PLAN] '{preview.OtherMod.Name}' + '{preview.UltiMod.Name}': {preview.Decisions.Count:N0} files "
                + $"({preview.OverrideCount:N0} UltiAI overrides, {preview.UltiAddedCount:N0} UltiAI added, "
                + $"{preview.BaseUiPreservedCount:N0} Base UI kept, {preview.BaseKeptCount:N0} Base Mod kept, {preview.IdenticalCount:N0} identical).");
            foreach (var warning in preview.Warnings) Log("[NOTE] " + warning);
        }
    }

    private void ClearPreview()
    {
        _preview = null;
        PreviewGrid.ItemsSource = null;
        SummaryText.Text = "";
        ProvisionalBadge.Visibility = Visibility.Collapsed;
        WarningsBorder.Visibility = Visibility.Collapsed;
        WarningsText.Text = "";
        StatTotalText.Text = "Total: 0";
        StatOverrideText.Text = "UltiAI Overrides: 0";
        StatAddedText.Text = "UltiAI Added: 0";
        StatUiKeptText.Text = "Base UI Kept: 0";
        StatBaseKeptText.Text = "Base Mod Kept: 0";
        StatIdenticalText.Text = "Identical: 0";
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (PreviewGrid is not null) ApplyPreviewFilter();
    }

    private void ApplyPreviewFilter()
    {
        if (_preview is null) return;
        var query = SearchBox.Text.Trim();
        var conflictsOnly = ConflictsOnlyBox.IsChecked.GetValueOrDefault();
        var ultiOnly = UltiOnlyFilterBox.IsChecked.GetValueOrDefault();
        var decisions = _preview.Decisions.Where(d =>
            (!conflictsOnly || (d.IsCollision && !d.Identical))
            && (!ultiOnly || d.IsUltiChange)
            && (query.Length == 0
                || d.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase)
                || d.ActionLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || d.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                || d.Winner.Contains(query, StringComparison.OrdinalIgnoreCase)
                || d.Detail.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        PreviewGrid.ItemsSource = decisions;
        SummaryText.Text = $"Showing {decisions.Length:N0} of {_preview.Decisions.Count:N0} files";
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_preview is null) return;
        var dialog = new SaveFileDialog { Title = "Export merge report", Filter = "JSON report|*.json", FileName = "merge-report.json" };
        if (dialog.ShowDialog() != true) return;
        var plan = _preview;
        var report = new
        {
            plan.OutputName,
            OtherMod = plan.OtherMod.Name,
            PriorityMod = plan.UltiMod.Name,
            BaseModDetails = new { plan.OtherMod.Name, Kind = plan.OtherMod.Kind.ToString(), plan.OtherMod.WorkshopId, plan.OtherMod.ModGenVersion },
            PriorityModDetails = new { plan.UltiMod.Name, Kind = plan.UltiMod.Kind.ToString(), plan.UltiMod.WorkshopId, plan.UltiMod.ModGenVersion },
            plan.Provisional,
            Summary = new
            {
                TotalFiles = plan.Decisions.Count,
                plan.UltiAppliedCount,
                plan.OverrideCount,
                plan.UltiAddedCount,
                plan.BaseUiPreservedCount,
                plan.BaseKeptCount,
                plan.IdenticalCount
            },
            plan.Warnings,
            Decisions = plan.Decisions.Select(d => new
            {
                d.RelativePath,
                d.Category,
                Kind = d.Kind.ToString(),
                Action = d.ActionLabel,
                d.Winner,
                d.Detail,
                d.Identical,
                d.IsCollision
            }),
            Log = LogBox.Text
        };
        await RunUiAsync(async token =>
        {
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), token);
            Log($"[EXPORT] Merge report saved to {dialog.FileName}");
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

    private void UpdateModMetadataLabels()
    {
        if (OtherModBox.SelectedItem is ModDescriptor other)
            OtherModMetaText.Text = $"{other.KindLabel}  ·  {other.ModGenLabel}  ·  {(other.ConfigKeys.Count > 0 ? $"{other.ConfigKeys.Count} NDF db(s)" : "SDK source delta")}";
        else
            OtherModMetaText.Text = "Select a gameplay, division, or UI mod";

        if (UltiModBox.SelectedItem is ModDescriptor ulti)
            UltiModMetaText.Text = $"{ulti.KindLabel}  ·  {ulti.ModGenLabel}  ·  Priority on AI & gameplay";
        else
            UltiModMetaText.Text = "Select an UltiAI variant";
    }

    private void UpdateActionStates()
    {
        UpdateModMetadataLabels();
        var selected = _paths is not null && OtherModBox.SelectedItem is ModDescriptor && UltiModBox.SelectedItem is ModDescriptor;
        var name = OutputNameBox.Text.Trim();
        var valid = IsValidName(name);
        var exists = valid && _paths is not null && (Directory.Exists(Path.Combine(_paths.ModsRoot, name)) || Directory.Exists(Path.Combine(_paths.SavedModsRoot, name)));
        var compatible = _previewError is null;

        PreviewButton.IsEnabled = !_busy && selected;
        CombineButton.IsEnabled = !_busy && selected && valid && !exists && compatible;
        UpdateButton.IsEnabled = !_busy && selected && _existing is not null && compatible;
        UpdateButton.Content = _changedMods.Count > 0 ? "Update and Rebuild" : "Rebuild Existing";
        OutputNameBox.IsEnabled = !_busy;
        ExportButton.IsEnabled = !_busy && _preview is not null;
        OpenOutputButton.IsEnabled = !_busy && _paths is not null && _existing is not null;

        var existingCount = ExistingMergeBox.Items.Count;
        if (_existing is not null)
        {
            var rebuildNeeded = _changedMods.Count > 0
                || string.IsNullOrEmpty(_existing.OtherMod.Fingerprint)
                || _existing.RuntimeFingerprint is null
                || (!string.IsNullOrEmpty(_healthMessage) && !_healthMessage.StartsWith("Combined output matches", StringComparison.OrdinalIgnoreCase));
            var stateDetail = _changedMods.Count > 0
                ? "Source update available"
                : rebuildNeeded
                    ? "Rebuild recommended"
                    : "Verified & intact";
            ExistingMergeMetaText.Text = $"{existingCount} existing merge(s)  ·  {stateDetail}";
        }
        else
            ExistingMergeMetaText.Text = "None created yet for this mod pair";

        if (string.IsNullOrWhiteSpace(name))
            OutputNameMetaText.Text = "Enter a name for 'Create as New'";
        else if (!valid)
            OutputNameMetaText.Text = "Invalid Windows folder name";
        else if (exists)
            OutputNameMetaText.Text = "Folder already exists — rename or use Rebuild";
        else
            OutputNameMetaText.Text = "Available for 'Create as New'";

        PreviewButton.ToolTip = !selected
            ? "Select both a Base Mod and a Priority Mod first."
            : "Rescan selected mods and refresh the file-by-file merge plan.";

        CombineButton.ToolTip = !selected
            ? "Select both a Base Mod and a Priority Mod first."
            : !compatible
                ? _previewError
                : !valid
                    ? "Enter a valid Windows folder name in 'New Combined Mod Name'."
                    : exists
                        ? $"A mod folder named '{name}' already exists. Enter a new name or use '{UpdateButton.Content}' on the existing mod."
                        : $"Create a new combined mod named '{name}'.";

        UpdateButton.ToolTip = _existing is null
            ? "No existing combined mod is selected for this pair. Use 'Create as New' first."
            : !compatible
                ? _previewError
                : _changedMods.Count > 0
                    ? $"Source mod(s) changed ({string.Join(", ", _changedMods)}). Click to update and rebuild '{_existing.OutputName}'."
                    : $"Rebuild and re-verify '{_existing.OutputName}' in place (previous output is backed up during the build).";

        ApplyStatusBadgeTheme();
        StatusText.Text = _status;
        StatusBorder.Visibility = _busy ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyStatusBadgeTheme()
    {
        StatusBadgeText.Text = _statusBadge;
        var (bg, border, fg) = _statusBadge switch
        {
            "UPDATE AVAILABLE" or "REBUILD NEEDED" or "SETUP" => ThemeWarn,
            "ERROR" or "ATTENTION" or "CANCELLED" => ThemeError,
            "VERIFIED" => ThemeVerified,
            _ => ThemeReady
        };
        StatusBadgeBorder.Background = bg;
        StatusBadgeBorder.BorderBrush = border;
        StatusBadgeText.Foreground = fg;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = _paths?.ModsRoot ?? Path.Combine(WarnoPathBox.Text.Trim(), "Mods");
        OpenFolderInExplorer(path);
    }

    private void OpenSavedMods_Click(object sender, RoutedEventArgs e)
    {
        var path = _paths?.SavedModsRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "EugenSystems", "WARNO", "mod");
        OpenFolderInExplorer(path);
    }

    private void OpenSelectedOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_paths is null || _existing is null) return;
        var source = Path.Combine(_paths.ModsRoot, _existing.OutputName);
        var runtime = Path.Combine(_paths.SavedModsRoot, _existing.OutputName);
        OpenFolderInExplorer(Directory.Exists(source) ? source : runtime);
    }

    private void OpenFolderInExplorer(string path)
    {
        if (!Directory.Exists(path))
        {
            Log($"[WARN] Folder does not exist yet: {path}");
            MessageBox.Show($"The folder does not exist yet:\n{path}", "WARNO UltiAI MODerator", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(LogBox.Text)) return;
        Clipboard.SetText(LogBox.Text);
        LogCountText.Text = $" · {_logEntries} entries (Copied to clipboard)";
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        _logEntries = 0;
        _lastLoggedPreviewKey = "";
        LogCountText.Text = " · Cleared";
    }

    private static bool IsValidName(string name)
    { try { MergePlanner.ValidateOutputName(name); return true; } catch (CombineException) { return false; } }

    private void Log(string message)
    {
        _logEntries++;
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
        if (LogCountText is not null) LogCountText.Text = $" · {_logEntries} entries";
    }

    private void ShowError(Exception ex)
    {
        Log("[ERROR] " + ex.Message);
        MessageBox.Show(ex.Message, "WARNO UltiAI MODerator", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static bool IsUlti(ModDescriptor mod) => mod.Name.Contains("UltiAI", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(mod.RootPath).Contains("UltiAI", StringComparison.OrdinalIgnoreCase);
}
