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
    private WarnoPaths? _paths;
    private bool _settingName;

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

    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settingName || OtherModBox.SelectedItem is not ModDescriptor other || UltiModBox.SelectedItem is not ModDescriptor ulti) return;
        _settingName = true;
        OutputNameBox.Text = $"{other.Name} + {ulti.Name}";
        _settingName = false;
        PreviewGrid.ItemsSource = null;
        SummaryText.Text = string.Empty;
    }

    private void OutputName_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_settingName) { PreviewGrid.ItemsSource = null; SummaryText.Text = string.Empty; }
    }

    private CombineRequest GetRequest()
    {
        if (_paths is null || OtherModBox.SelectedItem is not ModDescriptor other || UltiModBox.SelectedItem is not ModDescriptor ulti)
            throw new CombineException("Select both a mod and an UltiAI priority variant.");
        var preview = _planner.CreatePreview(_paths, other, ulti, OutputNameBox.Text.Trim());
        return new CombineRequest(_paths, other, ulti, OutputNameBox.Text.Trim(), preview);
    }

    private MergePreview Preview()
    {
        var preview = GetRequest().Preview;
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

            SetBusy(true);
            var result = await new CombineService(new SourceDeltaAnalyzer(), new ProcessRunner()).CombineAsync(request, new Progress<string>(Log));
            Log($"DONE: {result.OutputSourcePath}");
            MessageBox.Show($"Combined mod created successfully.\n\n{result.OutputSourcePath}", "WARNO UltiAI MODerator", MessageBoxButton.OK, MessageBoxImage.Information);
            Process.Start(new ProcessStartInfo("explorer.exe", result.OutputSourcePath) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CombineButton.IsEnabled = !busy;
        PreviewButton.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        WarnoPathBox.IsEnabled = !busy;
        OtherModBox.IsEnabled = !busy;
        UltiModBox.IsEnabled = !busy;
        OutputNameBox.IsEnabled = !busy;
        System.Windows.Input.Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null;
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
