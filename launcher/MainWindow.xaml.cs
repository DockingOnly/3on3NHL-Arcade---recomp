using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Shapes;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace Nhl3on3Launcher;

public partial class MainWindow : Window, IBuildReporter
{
    readonly KitInfo? _kit = KitInfo.Load();
    readonly LauncherSettings _settings = LauncherSettings.Load();
    GameSource? _source;
    ToolchainStatus? _tools;
    Task<ToolchainStatus>? _toolsTask;

    // Build state
    CancellationTokenSource? _cts;
    Task? _buildTask;
    bool _allowClose;
    readonly Stopwatch _buildClock = new();
    readonly Stopwatch _compileClock = new();
    readonly Dictionary<Stage, StageRow> _rows = [];
    readonly Dictionary<Stage, double> _fraction = [];
    readonly ConcurrentQueue<string> _pendingLog = new();
    readonly DispatcherTimer _tick;
    const int MaxLogChars = 400_000;

    static readonly (Stage Stage, string Title)[] StageTitles =
    [
        (Stage.Unpack, "Unpack game files"),
        (Stage.Tools, "Check build tools"),
        (Stage.Prepare, "Prepare the build"),
        (Stage.Recompile, "Recompile the Xbox 360 code to C++"),
        (Stage.Compile, "Compile the PC version"),
        (Stage.Finish, "Install and tidy up"),
    ];

    public MainWindow()
    {
        InitializeComponent();
        TaskbarItemInfo = new TaskbarItemInfo();
        var v = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = _kit is { Version.Length: > 0 } && _kit.Version != "dev" ? $"v{_kit.Version}" : $"launcher {v?.ToString(3)}";
        CloseOnPlay.IsChecked = _settings.CloseOnPlay;
        BuildSteps();
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => OnTick(), Dispatcher);
        Loaded += (_, _) => Start();
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    void Start()
    {
        _toolsTask = ToolchainLocator.DetectAsync();
        // A package or folder dropped onto the launcher's icon arrives as an argument.
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && (File.Exists(args[1]) || Directory.Exists(args[1])))
        {
            ShowSetup(autoFind: false);
            _ = ResolveAsync(args[1]);
        }
        else if (AppPaths.GameBuilt && AppPaths.GameUnpacked) ShowReady();
        else ShowSetup(autoFind: true);
    }

    // ================================================================== screens

    void ShowPanel(FrameworkElement panel, int step)
    {
        foreach (var p in new FrameworkElement[] { SetupPanel, BuildPanel, ReadyPanel })
        {
            if (p == panel)
            {
                p.Visibility = Visibility.Visible;
                p.Opacity = 0;
                p.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
                var shift = new TranslateTransform(0, 10);
                p.RenderTransform = shift;
                shift.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            else p.Visibility = Visibility.Collapsed;
        }
        PlayButton.IsDefault = panel == ReadyPanel;
        SetStep(step);
    }

    // ------------------------------------------------------------------ setup

    async void ShowSetup(bool autoFind)
    {
        ShowPanel(SetupPanel, 0);
        HideSetupError();
        RefreshSource();

        if (!AppPaths.BaseDirWritable())
        {
            ShowSetupError("The launcher can't save files in its folder.",
                $"Move the whole “{IOPath.GetFileName(AppPaths.BaseDir)}” folder somewhere you can write to, such as Documents or another drive, then open it again.");
            BuildButton.IsEnabled = false;
            return;
        }
        if (_kit == null)
        {
            ShowSetupError("The buildkit folder is missing.",
                "It comes with the release download and is needed to build the game. Download the release again and unzip all of it.");
        }

        _ = UpdateToolsCardAsync();

        if (autoFind && _source == null)
        {
            if (AppPaths.GameUnpacked)
            {
                await ResolveAsync(AppPaths.GameDir, quiet: true);
            }
            else
            {
                SetDropState(busy: true, "Looking for your game next to the launcher…");
                string? found = await Task.Run(() => GameSource.FindPackage(AppPaths.BaseDir, 4,
                    [AppPaths.GameDir, AppPaths.KitDir, AppPaths.LogsDir, IOPath.Combine(AppPaths.BaseDir, "licenses")]));
                SetDropState(busy: false);
                if (found != null && _source == null) await ResolveAsync(found, quiet: true);
            }
        }
    }

    async Task UpdateToolsCardAsync()
    {
        ToolsIconHost.Content = MakeSpinner(16);
        ToolsTitle.Text = "Looking for build tools…";
        ToolsDetail.Visibility = Visibility.Collapsed;
        _tools = await (_toolsTask ??= ToolchainLocator.DetectAsync());
        if (_tools.Ready != null)
        {
            ToolsIconHost.Content = MakeStatusIcon(StageState.Done, 18);
            ToolsTitle.Text = "Build tools are ready";
            ToolsDetail.Text = _tools.Ready.Description;
        }
        else
        {
            ToolsIconHost.Content = MakeGlyph("", (Brush)FindResource("AccentBrush"), 16);
            ToolsTitle.Text = _tools.ModifyTarget != null
                ? "A few Build Tools parts will be added"
                : "Microsoft C++ Build Tools will be installed";
            ToolsDetail.Text = _tools.ModifyTarget != null
                ? $"Needed: {string.Join(", ", _tools.Missing)}. The launcher adds them for you with Microsoft's installer."
                : $"The game is compiled with Microsoft's free Build Tools ({ToolchainLocator.ApproxDownload}). The launcher installs them for you.";
        }
        ToolsDetail.Visibility = Visibility.Visible;
        RefreshBuildButton();
    }

    void RefreshBuildButton() =>
        BuildButton.IsEnabled = _source != null && _kit != null && _tools != null && _buildTask == null;

    async Task ResolveAsync(string path, bool quiet = false)
    {
        HideSetupError();
        SetDropState(busy: true, "Checking your copy…");
        try
        {
            _source = await Task.Run(() => GameSource.Resolve(path));
        }
        catch (GameSourceException ex)
        {
            if (!quiet) ShowSetupError(ex.Message, ex.Hint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!quiet) ShowSetupError("That couldn't be read.", ex.Message);
        }
        SetDropState(busy: false);
        RefreshSource();
    }

    void RefreshSource()
    {
        var s = _source;
        DropEmpty.Visibility = s == null ? Visibility.Visible : Visibility.Collapsed;
        DropChosen.Visibility = s == null ? Visibility.Collapsed : Visibility.Visible;
        DropBorder.StrokeDashArray = s == null ? new DoubleCollection([5, 4]) : null;
        DropBorder.Stroke = (Brush)new BrushConverter().ConvertFrom(s == null ? "#3A5583" : "#2D4670")!;
        if (s != null)
        {
            GameName.Text = s.DisplayName;
            GamePath.Text = s.Kind == SourceKind.Installed ? "Already unpacked in the launcher's game folder" : s.Path;
            GamePath.ToolTip = s.Path;
            GameSize.Text = $"{s.Size / (1024.0 * 1024):0} MB · {(s.Kind == SourceKind.Package ? "XBLA package" : "unpacked files")}";
            GameThumb.Source = LoadImage(s.Thumbnail) ?? LoadImage(IOPath.Combine(s.Path, "icon.png"));
            bool known = s.IsKnownVersion;
            var color = (Brush)FindResource(known ? "SuccessBrush" : "WarningBrush");
            GameBadgeIcon.Text = known ? "" : "";
            GameBadgeIcon.Foreground = GameBadgeText.Foreground = color;
            GameBadgeText.Text = known ? "Verified copy of 3 on 3 NHL Arcade" : "Unrecognized version";
            GameBadge.ToolTip = known ? null : "This copy's game executable differs from the one the port was made for. The build may fail or the game may misbehave.";
            GameBadge.Background = new SolidColorBrush(Color.FromArgb(0x1A, ((SolidColorBrush)color).Color.R, ((SolidColorBrush)color).Color.G, ((SolidColorBrush)color).Color.B));
            GameBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, ((SolidColorBrush)color).Color.R, ((SolidColorBrush)color).Color.G, ((SolidColorBrush)color).Color.B));
        }
        RefreshBuildButton();
    }

    void SetDropState(bool busy, string? text = null)
    {
        DropBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            DropEmpty.Visibility = DropChosen.Visibility = Visibility.Collapsed;
            DropSpinnerHost.Content = MakeSpinner(28);
            DropBusyText.Text = text ?? "";
        }
        else
        {
            DropSpinnerHost.Content = null;
            RefreshSource();
        }
    }

    void ShowSetupError(string message, string? hint)
    {
        SetupErrorText.Text = message;
        SetupErrorHint.Text = hint ?? "";
        SetupErrorHint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        SetupError.Visibility = Visibility.Visible;
    }

    void HideSetupError() => SetupError.Visibility = Visibility.Collapsed;

    // drag & drop
    void DropZone_DragEnter(object sender, DragEventArgs e) => DropZone_DragOver(sender, e);

    void DropZone_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) && _buildTask == null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (ok)
        {
            DropBorder.Stroke = (Brush)FindResource("AccentBrush");
            DropBorder.Fill = new SolidColorBrush(Color.FromArgb(0x1C, 0x4D, 0xB8, 0xFF));
        }
    }

    void DropZone_DragLeave(object sender, DragEventArgs e) => ResetDropHighlight();

    void ResetDropHighlight()
    {
        DropBorder.Fill = new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF));
        RefreshSource();
    }

    async void DropZone_Drop(object sender, DragEventArgs e)
    {
        ResetDropHighlight();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            await ResolveAsync(paths[0]);
    }

    async void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select your 3 on 3 NHL Arcade package (inside 58410975\\000D0000)",
            Filter = "Xbox 360 package or default.xex|*.*",
        };
        if (dialog.ShowDialog(this) == true) await ResolveAsync(dialog.FileName);
    }

    async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the 58410975 folder, or an unpacked copy of the game" };
        if (dialog.ShowDialog(this) == true) await ResolveAsync(dialog.FolderName);
    }

    void ChangeGame_Click(object sender, RoutedEventArgs e)
    {
        _source = null;
        HideSetupError();
        RefreshSource();
    }

    async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (_source == null || _kit == null || _tools == null) return;
        if (_tools.Ready == null)
        {
            bool install = await ShowDialogAsync("", "Install Microsoft C++ Build Tools?",
                "The game is compiled with Microsoft's free C++ Build Tools (Visual Studio Build Tools 2026 with Clang). " +
                $"The launcher downloads Microsoft's installer and sets them up: {ToolchainLocator.ApproxDownload}, about 10–20 minutes.\n\n" +
                "Windows will ask for permission, and Microsoft's installer window shows its progress. " +
                "By continuing you accept the Visual Studio Build Tools license terms.",
                "Install and build", "Not now", "Build Tools and license terms", ToolchainLocator.InfoUrl);
            if (!install) return;
        }
        if (!_source.IsKnownVersion)
        {
            bool go = await ShowDialogAsync("", "Build an unrecognized version?",
                "This copy's game executable isn't the one the port was made for. It may be a different release of the game. " +
                "The build may fail, or the game may not work properly.",
                "Build anyway", "Cancel");
            if (!go) return;
        }
        StartBuild();
    }

    // ------------------------------------------------------------------ build

    void StartBuild()
    {
        if (_source == null || _kit == null || _tools == null) return;
        ShowPanel(BuildPanel, 1);
        BuildHeading.Text = "Building your game";
        BuildError.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = BackButton.Visibility = OpenLogButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        LogBox.Clear();
        while (_pendingLog.TryDequeue(out _)) { }
        BuildStageRows();
        OverallBar.IsIndeterminate = false;
        OverallBar.Value = 0;
        BuildPercent.Text = "0%";
        _buildClock.Restart();
        _compileClock.Reset();
        _tick.Start();
        TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal;

        _cts = new CancellationTokenSource();
        var pipeline = new BuildPipeline(_source, _tools, _kit, this);
        _buildTask = RunBuildAsync(pipeline, _cts.Token);
        RefreshBuildButton();
    }

    async Task RunBuildAsync(BuildPipeline pipeline, CancellationToken cancel)
    {
        try
        {
            await Task.Run(() => pipeline.RunAsync(cancel), CancellationToken.None);
            OnTick();
            OnBuildSucceeded();
        }
        catch (OperationCanceledException)
        {
            OnTick();
            OnBuildStopped(pipeline.Current, "Build cancelled.",
                "Nothing is lost: building again picks up where it stopped.", cancelled: true);
        }
        catch (BuildException ex)
        {
            OnTick();
            OnBuildStopped(pipeline.Current, ex.Message, ex.Hint, cancelled: false);
        }
        catch (Exception ex)
        {
            OnTick();
            OnBuildStopped(pipeline.Current, "Something went wrong: " + ex.Message, "See the build log for details.", cancelled: false);
        }
        finally
        {
            _buildClock.Stop();
            _compileClock.Stop();
            _tick.Stop();
            _buildTask = null;
            _cts?.Dispose();
            _cts = null;
            _tools = null;
            _toolsTask = ToolchainLocator.DetectAsync();  // the build may have installed them
            if (_allowClose) _ = Dispatcher.BeginInvoke(Close);
        }
    }

    void OnBuildSucceeded()
    {
        BuildHeading.Text = "Your game is ready!";
        BuildPercent.Text = "100%";
        OverallBar.Value = 1;
        BuildSubtitle.Text = $"Built in {FormatDuration(_buildClock.Elapsed)}.";
        CancelButton.Visibility = Visibility.Collapsed;
        TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
        Flash();
        _source = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        timer.Tick += (_, _) => { timer.Stop(); ShowReady(); };
        timer.Start();
    }

    void OnBuildStopped(Stage stage, string message, string? hint, bool cancelled)
    {
        if (_rows.TryGetValue(stage, out var row) && row.State == StageState.Active)
            SetStage(stage, cancelled ? StageState.Pending : StageState.Failed, cancelled ? "Stopped" : "Failed");
        BuildHeading.Text = cancelled ? "Build paused" : "The build stopped";
        BuildSubtitle.Text = $"After {FormatDuration(_buildClock.Elapsed)}.";
        BuildErrorIcon.Text = cancelled ? "" : "";
        BuildErrorIcon.Foreground = (Brush)FindResource(cancelled ? "WarningBrush" : "ErrorBrush");
        BuildError.Background = new SolidColorBrush(cancelled ? Color.FromArgb(0x1A, 0xFF, 0xC8, 0x57) : Color.FromArgb(0x1A, 0xFF, 0x6B, 0x7A));
        BuildError.BorderBrush = new SolidColorBrush(cancelled ? Color.FromArgb(0x55, 0xFF, 0xC8, 0x57) : Color.FromArgb(0x55, 0xFF, 0x6B, 0x7A));
        BuildErrorText.Text = message;
        BuildErrorHint.Text = hint ?? "";
        BuildErrorHint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        BuildError.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = BackButton.Visibility = Visibility.Visible;
        OpenLogButton.Visibility = File.Exists(AppPaths.BuildLog) ? Visibility.Visible : Visibility.Collapsed;
        TaskbarItemInfo.ProgressState = cancelled ? TaskbarItemProgressState.Paused : TaskbarItemProgressState.Error;
        if (!cancelled) Flash();
    }

    async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cts == null) return;
        bool stop = await ShowDialogAsync("", "Stop building?",
            "You can build again later and it picks up where it stopped.", "Stop", "Keep building");
        if (stop && _cts != null)
        {
            CancelButton.IsEnabled = false;
            _cts.Cancel();
        }
    }

    async void Retry_Click(object sender, RoutedEventArgs e)
    {
        _source ??= await TryInstalledSourceAsync();
        if (_source == null) { ShowSetup(autoFind: true); return; }
        _tools = await (_toolsTask ??= ToolchainLocator.DetectAsync());
        StartBuild();
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (AppPaths.GameBuilt && AppPaths.GameUnpacked && BuildInfo.Load() != null) ShowReady();
        else ShowSetup(autoFind: true);
    }

    void OpenLog_Click(object sender, RoutedEventArgs e) => Shell("notepad.exe", AppPaths.BuildLog);

    void Details_Click(object sender, RoutedEventArgs e)
    {
        bool show = LogBox.Visibility != Visibility.Visible;
        LogBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DetailsText.Text = show ? "Hide details" : "Show details";
        DetailsChevron.Text = show ? "" : "";
        if (show) LogBox.ScrollToEnd();
    }

    // IBuildReporter: called from the build thread.
    public void SetStage(Stage stage, StageState state, string? detail = null) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (!_rows.TryGetValue(stage, out var row)) return;
            row.State = state;
            row.Icon.Content = state == StageState.Active ? MakeSpinner(18) : MakeStatusIcon(state, 20);
            row.Title.Foreground = (Brush)FindResource(state == StageState.Pending ? "MutedBrush" : "TextBrush");
            row.Title.FontWeight = state == StageState.Active ? FontWeights.SemiBold : FontWeights.Normal;
            if (detail != null) row.Detail.Text = detail;
            else if (state is StageState.Done or StageState.Skipped) row.Detail.Text = "";
            row.Detail.Foreground = (Brush)FindResource(state == StageState.Failed ? "ErrorBrush" : "MutedBrush");
            if (state is StageState.Done or StageState.Skipped) _fraction[stage] = 1;
            if (stage == Stage.Compile && state == StageState.Active) _compileClock.Start();
            if (stage == Stage.Compile && state != StageState.Active) _compileClock.Stop();
            UpdateOverall();
        });

    public void SetProgress(Stage stage, double fraction, string? detail = null) =>
        Dispatcher.BeginInvoke(() =>
        {
            // Updates can trail the stage's own Done; drop those so the final text stays.
            if (!_rows.TryGetValue(stage, out var row) || row.State != StageState.Active) return;
            if (fraction >= 0) _fraction[stage] = Math.Clamp(fraction, 0, 1);
            if (detail != null) row.Detail.Text = detail;
            UpdateOverall();
        });

    public void Log(string line) => _pendingLog.Enqueue(line);

    void UpdateOverall()
    {
        double total = 0;
        foreach (var (stage, weight) in BuildPipeline.Weights)
            total += weight * (_fraction.TryGetValue(stage, out double f) ? f : 0);
        total = Math.Clamp(total, 0, 1);
        if (total < OverallBar.Value - 0.0001 && _buildTask != null) return;  // never go backwards
        OverallBar.Value = total;
        BuildPercent.Text = $"{Math.Floor(total * 100):0}%";
        TaskbarItemInfo.ProgressValue = total;
    }

    void OnTick()
    {
        if (!_pendingLog.IsEmpty)
        {
            var sb = new System.Text.StringBuilder();
            while (_pendingLog.TryDequeue(out string? line)) sb.Append(line).Append('\n');
            LogBox.AppendText(sb.ToString());
            if (LogBox.Text.Length > MaxLogChars)
                LogBox.Text = LogBox.Text[^(MaxLogChars / 2)..];
            if (LogBox.Visibility == Visibility.Visible) LogBox.ScrollToEnd();
        }
        if (_buildTask == null) return;
        string text = $"Elapsed {FormatDuration(_buildClock.Elapsed)}";
        double f = _fraction.TryGetValue(Stage.Compile, out double cf) ? cf : 0;
        if (_compileClock.IsRunning && f > 0.04 && f < 1)
        {
            var left = TimeSpan.FromSeconds(_compileClock.Elapsed.TotalSeconds / f * (1 - f));
            text += left.TotalMinutes >= 1.5 ? $" · about {Math.Ceiling(left.TotalMinutes):0} min left" : " · almost done";
        }
        else if (!_compileClock.IsRunning && f == 0)
        {
            text += " · usually 5–30 minutes in total";
        }
        BuildSubtitle.Text = text;
    }

    sealed class StageRow
    {
        public required ContentControl Icon { get; init; }
        public required TextBlock Title { get; init; }
        public required TextBlock Detail { get; init; }
        public StageState State { get; set; }
    }

    void BuildStageRows()
    {
        StageList.Children.Clear();
        _rows.Clear();
        _fraction.Clear();
        foreach (var (stage, title) in StageTitles)
        {
            var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new ContentControl { Content = MakeStatusIcon(StageState.Pending, 20), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            var titleText = new TextBlock { Text = title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("MutedBrush") };
            var detail = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 330, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(titleText, 1);
            Grid.SetColumn(detail, 2);
            grid.Children.Add(icon);
            grid.Children.Add(titleText);
            grid.Children.Add(detail);
            StageList.Children.Add(grid);
            _rows[stage] = new StageRow { Icon = icon, Title = titleText, Detail = detail, State = StageState.Pending };
        }
    }

    // ------------------------------------------------------------------ ready

    void ShowReady()
    {
        ShowPanel(ReadyPanel, 2);
        PlayError.Visibility = Visibility.Collapsed;
        var banner = LoadImage(IOPath.Combine(AppPaths.GameDir, "banner.png"));
        Banner.Source = banner;
        BannerFallback.Visibility = banner == null ? Visibility.Visible : Visibility.Collapsed;

        var info = BuildInfo.Load();
        ReadyDetail.Text = info != null
            ? $"Built {info.BuiltAt:d MMMM yyyy} on this PC."
            : "Installed.";
        if (_kit != null && info != null && _kit.Version != info.KitVersion)
        {
            UpdateNoticeText.Text = $"This release has a newer version of the port ({_kit.Version}). Rebuilding takes 5–30 minutes.";
            UpdateNotice.Visibility = Visibility.Visible;
        }
        else UpdateNotice.Visibility = Visibility.Collapsed;
    }

    void Play_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.GameExe) { WorkingDirectory = AppPaths.BaseDir, UseShellExecute = false });
            if (CloseOnPlay.IsChecked == true) Close();
            else WindowState = WindowState.Minimized;
        }
        catch (Exception ex)
        {
            PlayError.Text = "The game didn't start: " + ex.Message;
            PlayError.Visibility = Visibility.Visible;
        }
    }

    void CloseOnPlay_Changed(object sender, RoutedEventArgs e)
    {
        _settings.CloseOnPlay = CloseOnPlay.IsChecked == true;
        if (IsLoaded) _settings.Save();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e) => Shell("explorer.exe", AppPaths.BaseDir);

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(AppPaths.GameSettings)) Shell("notepad.exe", AppPaths.GameSettings);
    }

    async void Rebuild_Click(object sender, RoutedEventArgs e)
    {
        if (_kit == null)
        {
            await ShowDialogAsync("", "Can't rebuild", "The buildkit folder is missing. Download the release again and unzip all of it.", "OK", null);
            return;
        }
        bool go = await ShowDialogAsync("", "Rebuild the game?",
            "The launcher compiles the game again from your copy. It takes 5–30 minutes, and the current build stays in place until the new one is ready.",
            "Rebuild", "Cancel");
        if (!go) return;
        _source = await TryInstalledSourceAsync();
        if (_source == null) { ShowSetup(autoFind: false); return; }
        _tools = await (_toolsTask ??= ToolchainLocator.DetectAsync());
        if (_tools.Ready == null) { ShowSetup(autoFind: false); return; }  // let the player see and accept the install
        StartBuild();
    }

    void AnotherCopy_Click(object sender, RoutedEventArgs e)
    {
        _source = null;
        ShowSetup(autoFind: false);
    }

    static async Task<GameSource?> TryInstalledSourceAsync()
    {
        if (!AppPaths.GameUnpacked) return null;
        try { return await Task.Run(() => GameSource.FromFolder(AppPaths.GameDir)); }
        catch (Exception) { return null; }
    }

    // ================================================================== dialog

    TaskCompletionSource<bool>? _dialog;
    string? _dialogUrl;

    Task<bool> ShowDialogAsync(string icon, string title, string body, string primary, string? secondary,
        string? linkText = null, string? linkUrl = null)
    {
        _dialog?.TrySetResult(false);
        _dialog = new TaskCompletionSource<bool>();
        DialogIcon.Text = icon;
        DialogTitle.Text = title;
        DialogBody.Text = body;
        DialogPrimary.Content = primary;
        DialogSecondary.Content = secondary;
        DialogSecondary.Visibility = secondary == null ? Visibility.Collapsed : Visibility.Visible;
        _dialogUrl = linkUrl;
        DialogLink.Content = linkText;
        DialogLink.Visibility = linkText == null ? Visibility.Collapsed : Visibility.Visible;
        DialogOverlay.Visibility = Visibility.Visible;
        DialogOverlay.Opacity = 0;
        DialogOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        DialogPrimary.Focus();
        return _dialog.Task;
    }

    void CloseDialog(bool result)
    {
        DialogOverlay.Visibility = Visibility.Collapsed;
        var d = _dialog;
        _dialog = null;
        d?.TrySetResult(result);
    }

    void DialogPrimary_Click(object sender, RoutedEventArgs e) => CloseDialog(true);
    void DialogSecondary_Click(object sender, RoutedEventArgs e) => CloseDialog(false);
    void DialogLink_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogUrl != null) Process.Start(new ProcessStartInfo(_dialogUrl) { UseShellExecute = true });
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DialogOverlay.Visibility != Visibility.Visible) return;
        if (e.Key is Key.Escape or Key.Enter)
        {
            // Enter answers the dialog itself, never the button behind it (Play is the default).
            CloseDialog(e.Key == Key.Enter && !DialogSecondary.IsFocused);
            e.Handled = true;
        }
    }

    // ================================================================== window

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_buildTask == null || _allowClose) return;
        e.Cancel = true;
        bool stop = await ShowDialogAsync("", "Stop building and close?",
            "You can build again later and it picks up where it stopped.", "Stop and close", "Keep building");
        if (!stop) return;
        _allowClose = true;
        if (_cts != null) _cts.Cancel();
        else Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = 2, dark = 1;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));  // DWMWA_WINDOW_CORNER_PREFERENCE: round
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
        int border = 0x00402A1F;                                    // COLORREF (BGR) for #1F2A40
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int)); // DWMWA_BORDER_COLOR
    }

    void Flash()
    {
        if (IsActive) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new WindowInteropHelper(this).Handle,
            dwFlags = 3 | 12,  // FLASHW_ALL | FLASHW_TIMERNOFG
        };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO info);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ================================================================== visuals

    void BuildSteps()
    {
        Steps.Children.Clear();
        string[] names = ["Choose your game", "Build", "Play"];
        for (int i = 0; i < names.Length; i++)
        {
            if (i > 0)
                Steps.Children.Add(new Rectangle { Width = 44, Height = 2, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Fill = new SolidColorBrush(Color.FromRgb(0x23, 0x34, 0x5A)), RadiusX = 1, RadiusY = 1 });
            var circle = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.5) };
            circle.Child = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = names[i], FontSize = 13, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var item = new StackPanel { Orientation = Orientation.Horizontal, Tag = i };
            item.Children.Add(circle);
            item.Children.Add(label);
            Steps.Children.Add(item);
        }
    }

    void SetStep(int current)
    {
        var accent = (Brush)FindResource("AccentBrush");
        foreach (var item in Steps.Children.OfType<StackPanel>())
        {
            int i = (int)item.Tag;
            var circle = (Border)item.Children[0];
            var text = (TextBlock)circle.Child;
            var label = (TextBlock)item.Children[1];
            if (i < current)
            {
                circle.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x4D, 0xB8, 0xFF));
                circle.BorderBrush = accent;
                text.Text = "";
                text.FontFamily = (FontFamily)FindResource("IconFont");
                text.FontSize = 11;
                text.Foreground = accent;
                label.Foreground = (Brush)FindResource("MutedBrush");
                label.FontWeight = FontWeights.Normal;
            }
            else if (i == current)
            {
                circle.Background = (Brush)FindResource("AccentGradient");
                circle.BorderBrush = Brushes.Transparent;
                text.Text = (i + 1).ToString();
                text.FontFamily = (FontFamily)FindResource("UiFont");
                text.FontSize = 12;
                text.Foreground = Brushes.White;
                label.Foreground = (Brush)FindResource("TextBrush");
                label.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                circle.Background = Brushes.Transparent;
                circle.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x42, 0x6A));
                text.Text = (i + 1).ToString();
                text.FontFamily = (FontFamily)FindResource("UiFont");
                text.FontSize = 12;
                text.Foreground = (Brush)FindResource("DimBrush");
                label.Foreground = (Brush)FindResource("DimBrush");
                label.FontWeight = FontWeights.Normal;
            }
        }
    }

    FrameworkElement MakeSpinner(double size)
    {
        const double thickness = 2.4;
        double circumference = Math.PI * (size - thickness);
        var track = new Ellipse { Width = size, Height = size, Stroke = new SolidColorBrush(Color.FromArgb(0x33, 0x4D, 0xB8, 0xFF)), StrokeThickness = thickness };
        var rotate = new RotateTransform();
        var arc = new Ellipse
        {
            Width = size, Height = size, StrokeThickness = thickness, Stroke = (Brush)FindResource("AccentBrush"),
            StrokeDashArray = [circumference * 0.3 / thickness, 1000],
            StrokeDashCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rotate,
        };
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(track);
        grid.Children.Add(arc);
        return grid;
    }

    FrameworkElement MakeStatusIcon(StageState state, double size)
    {
        var grid = new Grid { Width = size, Height = size };
        switch (state)
        {
            case StageState.Done:
            case StageState.Skipped:
                var color = state == StageState.Done ? Color.FromRgb(0x3D, 0xDC, 0x97) : Color.FromRgb(0x5F, 0x73, 0x95);
                grid.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0x2A, color.R, color.G, color.B)) });
                grid.Children.Add(MakeGlyph("", new SolidColorBrush(color), size * 0.5));
                break;
            case StageState.Failed:
                grid.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0x6B, 0x7A)) });
                grid.Children.Add(MakeGlyph("", (Brush)FindResource("ErrorBrush"), size * 0.45));
                break;
            default:
                grid.Children.Add(new Ellipse { Stroke = new SolidColorBrush(Color.FromRgb(0x2E, 0x42, 0x6A)), StrokeThickness = 1.5, Margin = new Thickness(size * 0.15) });
                break;
        }
        return grid;
    }

    TextBlock MakeGlyph(string glyph, Brush brush, double size) => new()
    {
        Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = size, Foreground = brush,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    // ================================================================== helpers

    static BitmapImage? LoadImage(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }
    }

    static BitmapImage? LoadImage(string path)
    {
        try { return File.Exists(path) ? LoadImage(File.ReadAllBytes(path)) : null; }
        catch (Exception) { return null; }
    }

    static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    static void Shell(string exe, string arg)
    {
        try { Process.Start(new ProcessStartInfo(exe) { ArgumentList = { arg }, UseShellExecute = false }); }
        catch (Exception) { }
    }
}
