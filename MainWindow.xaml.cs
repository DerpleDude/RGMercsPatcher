using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Threading;

namespace RGMercsPatcher;

public partial class MainWindow : Window
{
    private readonly PatcherSettings _settings;
    private readonly PatcherService _service = new();
    private readonly ObservableCollection<string> _logEntries = [];
    private CancellationTokenSource? _cts;
    private bool _patchComplete = false;
    private DispatcherTimer? _fightTimer;
    private int _fightFrame = 0;
    private static readonly string[] FightFrames =
    [
        "🤺      🧍",
        "🤺    🧍",
        "🤺  ⚔️🧍",
        "🤺⚔️🧍",
        "🧍💥🤺",
        "🧍💥🤺",
        "🧍    🤺",
        "🧍  ⚔️🤺",
        "🧍⚔️🤺",
        "🤺💥🧍",
        "🤺💥🧍",
        "🤺    🧍",
    ];

    public MainWindow()
    {
        InitializeComponent();
        _settings = PatcherSettings.Load();
        LuaDirTextBox.Text = _settings.LuaDirectory;
        LogItems.ItemsSource = _logEntries;
        var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        VersionText.Text = ver is { Major: > 0 } ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "";
        UpdateButtonState();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        PatchNotesText.Text = "Loading...";
        ActionButton.IsEnabled = false;

        var luaDir = _settings.LuaDirectory;
        if (string.IsNullOrWhiteSpace(luaDir))
        {
            PatchNotesText.Text = "No patch notes available — set your Lua folder first.";
            ActionButton.IsEnabled = Directory.Exists(luaDir);
            return;
        }

        var (hasNew, log) = await Task.Run(() => _service.FetchAndCheck(luaDir));

        PatchNotesText.Text = log;
        ActionButton.Content = hasNew ? "Update Now!" : "Recheck for Updates";
        ActionButton.IsEnabled = true;
        _patchComplete = !hasNew;
    }

    private void UpdateButtonState()
    {
        ActionButton.IsEnabled = Directory.Exists(_settings.LuaDirectory);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select your RGMercs Lua folder",
            SelectedPath = Directory.Exists(_settings.LuaDirectory) ? _settings.LuaDirectory : "",
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            LuaDirTextBox.Text = dlg.SelectedPath;
            _settings.LuaDirectory = dlg.SelectedPath;
            _settings.Save();
            _ = RefreshAsync();
        }
    }

    private void LuaDirTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _settings.LuaDirectory = LuaDirTextBox.Text;
        _settings.Save();
        _patchComplete = false;
        UpdateButtonState();
    }

    private void AppendLog(string message)
    {
        _logEntries.Add(message);
        LogScroller.ScrollToBottom();
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        ActionButton.IsEnabled = false;
        _logEntries.Clear();

        _fightFrame = 0;
        _fightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _fightTimer.Tick += (_, _) =>
        {
            ActionButton.Content = FightFrames[_fightFrame % FightFrames.Length];
            _fightFrame++;
        };
        _fightTimer.Start();

        var progress = new Progress<(double Percent, string Status)>(_ => { });

        var log = new Progress<string>(AppendLog);

        try
        {
            await _service.SyncAndPatch(_settings.LuaDirectory, progress, log, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog($"[error] {ex.Message}");
        }
        finally
        {
            _fightTimer?.Stop();
            _fightTimer = null;
            await RefreshAsync();
        }
    }

}
