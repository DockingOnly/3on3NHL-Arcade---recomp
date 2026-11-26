using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Nhl3on3Launcher;

public enum Stage { Unpack, Tools, Prepare, Recompile, Compile, Finish }
public enum StageState { Pending, Active, Done, Skipped, Failed }

public interface IBuildReporter
{
    void SetStage(Stage stage, StageState state, string? detail = null);
    /// <summary>Progress within a stage, 0..1; a negative value means "working, unknown how far".</summary>
    void SetProgress(Stage stage, double fraction, string? detail = null);
    void Log(string line);
}

public sealed class BuildException(string message, string? hint = null) : Exception(message)
{
    public string? Hint { get; } = hint;
}

/// <summary>Unpacks the player's game, makes sure the C++ tools are there, then recompiles
/// default.xex to C++ with ReXGlue and compiles it into nhl3on3.exe next to the launcher.</summary>
public sealed partial class BuildPipeline(GameSource source, ToolchainStatus tools, KitInfo kit, IBuildReporter ui)
{
    /// <summary>How much of the whole run each stage takes, roughly by time on a typical PC.</summary>
    public static readonly IReadOnlyDictionary<Stage, double> Weights = new Dictionary<Stage, double>
    {
        [Stage.Unpack] = 0.03, [Stage.Tools] = 0.01, [Stage.Prepare] = 0.02,
        [Stage.Recompile] = 0.06, [Stage.Compile] = 0.86, [Stage.Finish] = 0.02,
    };

    /// <summary>Set after a run that looks like it ran out of memory; the next run uses fewer jobs.</summary>
    public static bool LowMemoryMode { get; private set; }

    public Stage Current { get; private set; } = Stage.Unpack;

    StreamWriter? _log;
    readonly List<string> _errors = [];
    bool _outOfMemory;

    string WorkSrc => Path.Combine(AppPaths.WorkDir, "src");
    string WorkBin => Path.Combine(AppPaths.WorkDir, "bin");
    string WorkState => Path.Combine(AppPaths.WorkDir, "state.json");

    public async Task RunAsync(CancellationToken cancel)
    {
        Directory.CreateDirectory(AppPaths.LogsDir);
        _log = new StreamWriter(AppPaths.BuildLog, append: false, Encoding.UTF8) { AutoFlush = true };
        try
        {
            Log($"NHL 3on3 Launcher {typeof(BuildPipeline).Assembly.GetName().Version} - build kit {kit.Version}");
            Log($"Started {DateTime.Now:yyyy-MM-dd HH:mm:ss}; game copy: {source.Kind} {source.Path}");
            Log($"default.xex SHA-256 {source.XexSha256}{(source.IsKnownVersion ? "" : " (not the expected version)")}");
            if (GameIsRunning())
                throw new BuildException("The game is running.", "Close 3 on 3 NHL Arcade, then try again.");

            await UnpackAsync(cancel);
            var (toolchain, env) = await ToolsAsync(cancel);
            await PrepareAsync(toolchain, env, cancel);
            await RecompileAsync(toolchain, env, cancel);
            await CompileAsync(toolchain, env, cancel);
            await FinishAsync(toolchain);
            Log($"Finished {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log($"FAILED in {Current}: {ex}");
            if (_outOfMemory) LowMemoryMode = true;
            if (ex is BuildException) throw;
            if (ex is GameSourceException gs) throw new BuildException(gs.Message, gs.Hint);
            throw new BuildException(ex.Message, "See the build log for details.");
        }
        finally
        {
            _log.Dispose();
            _log = null;
        }
    }

    void Log(string line)
    {
        _log?.WriteLine(line);
        ui.Log(line);
    }

    void Begin(Stage stage, string? detail = null)
    {
        Current = stage;
        ui.SetStage(stage, StageState.Active, detail);
        Log($"== {stage} ==");
    }

    // ---- 1. Unpack -------------------------------------------------------------------

    async Task UnpackAsync(CancellationToken cancel)
    {
        if (source.Kind == SourceKind.Installed)
        {
            ui.SetStage(Stage.Unpack, StageState.Skipped, "Already unpacked");
            return;
        }
        Begin(Stage.Unpack, "Reading your copy of the game");
        string staging = AppPaths.GameDir + ".partial";
        string old = AppPaths.GameDir + ".old";
        var progress = new Progress<(long Done, long Total)>(p =>
            ui.SetProgress(Stage.Unpack, p.Total == 0 ? 0 : (double)p.Done / p.Total,
                $"{p.Done >> 20} of {p.Total >> 20} MB"));
        await Task.Run(() =>
        {
            DeleteDirectory(staging);
            if (source.Kind == SourceKind.Package)
            {
                using var pkg = StfsPackage.TryOpen(source.Path)
                    ?? throw new BuildException("Your game package can no longer be read.", "Was it moved or deleted?");
                pkg.ExtractAll(staging, progress, cancel);
            }
            else
            {
                CopyDirectory(source.Path, staging, progress, cancel);
            }
            string xex = Path.Combine(staging, "default.xex");
            if (!File.Exists(xex) || !File.ReadAllBytes(xex).AsSpan(0, 4).SequenceEqual("XEX2"u8))
                throw new BuildException("The game unpacked, but default.xex is missing or damaged.");
            DeleteDirectory(old);
            if (Directory.Exists(AppPaths.GameDir)) Directory.Move(AppPaths.GameDir, old);
            Directory.Move(staging, AppPaths.GameDir);
            DeleteDirectory(old);
        }, cancel);
        Log($"Game files unpacked to {AppPaths.GameDir}");
        ui.SetStage(Stage.Unpack, StageState.Done, $"{source.Size >> 20} MB unpacked");
    }

    // ---- 2. Build tools --------------------------------------------------------------

    async Task<(Toolchain, Dictionary<string, string>)> ToolsAsync(CancellationToken cancel)
    {
        Begin(Stage.Tools, "Looking for Microsoft C++ Build Tools");
        var status = tools.Ready != null ? tools : await ToolchainLocator.DetectAsync();
        if (status.Ready == null)
        {
            Log("Missing: " + string.Join(", ", status.Missing));
            ui.SetProgress(Stage.Tools, 0, "Downloading Microsoft's installer");
            var download = new Progress<double>(f => ui.SetProgress(Stage.Tools, f * 0.1, "Downloading Microsoft's installer"));
            var install = ToolchainLocator.InstallAsync(status.ModifyTarget, Log, download, cancel);
            await Task.Delay(500, cancel);
            if (!install.IsCompleted)
                ui.SetProgress(Stage.Tools, -1, "Installing — follow along in Microsoft's installer window");
            string? error = await install;
            if (error != null) throw new BuildException(error);
            status = await ToolchainLocator.DetectAsync();
            if (status.Ready == null)
                throw new BuildException("The Build Tools were installed, but parts are still missing: "
                    + string.Join(", ", status.Missing) + ".",
                    "Restart your PC and open the launcher again. If it happens again, add them in the Visual Studio Installer.");
        }
        var toolchain = status.Ready;
        Log($"Using {toolchain.Description} at {toolchain.Instance.Path}");
        ui.SetProgress(Stage.Tools, -1, "Setting up the compiler environment");
        var env = await toolchain.CaptureEnvironmentAsync(cancel);
        ui.SetStage(Stage.Tools, StageState.Done, toolchain.Description);
        return (toolchain, env);
    }

    // ---- 3. Prepare ------------------------------------------------------------------

    async Task PrepareAsync(Toolchain toolchain, Dictionary<string, string> env, CancellationToken cancel)
    {
        Begin(Stage.Prepare, "Checking disk space");
        Directory.CreateDirectory(AppPaths.WorkDir);
        RequireFreeSpace(AppPaths.WorkDir, 4L << 30, "the build");
        RequireFreeSpace(AppPaths.BaseDir, 200L << 20, "the finished game");

        // A tree left by a cancelled or failed run with the same inputs is picked up where it
        // stopped; anything else is started fresh.
        string stamp = $"{kit.Version}|{source.XexSha256}|{toolchain.Instance.Path}|{toolchain.ClangMajor}";
        string? previous = File.Exists(WorkState) ? File.ReadAllText(WorkState) : null;
        if (previous != stamp)
        {
            if (previous != null) Log("Discarding an earlier, different build tree.");
            DeleteDirectory(AppPaths.WorkDir);
            Directory.CreateDirectory(AppPaths.WorkDir);
        }
        else
        {
            Log("Resuming the earlier build tree.");
        }
        File.WriteAllText(WorkState, stamp);

        ui.SetProgress(Stage.Prepare, 0.2, "Copying the port's source code");
        CopyDirectory(AppPaths.KitSource, WorkSrc, null, cancel, onlyIfChanged: true);
        WriteManifest();

        ui.SetProgress(Stage.Prepare, 0.5, "Configuring the build");
        await ConfigureAsync(toolchain, env, cancel);
        ui.SetStage(Stage.Prepare, StageState.Done);
    }

    void WriteManifest()
    {
        string path = Path.Combine(WorkSrc, "nhl3on3_manifest.toml");
        string game = AppPaths.GameDir.Replace('\\', '/').Replace("\"", "\\\"");
        string text = File.ReadAllText(path);
        text = Regex.Replace(text, @"(?m)^game_root\s*=.*$", $"game_root = \"{game}\"");
        text = Regex.Replace(text, @"(?m)^file_path\s*=.*$", $"file_path = \"{game}/default.xex\"");
        if (File.ReadAllText(path) != text) File.WriteAllText(path, text);
    }

    Task ConfigureAsync(Toolchain t, Dictionary<string, string> env, CancellationToken cancel) =>
        RunCheckedAsync(t.CMake,
        [
            "-S", WorkSrc, "-B", WorkBin, "-G", "Ninja",
            "-DCMAKE_BUILD_TYPE=Release",
            $"-DCMAKE_C_COMPILER={Slash(t.Clang)}",
            $"-DCMAKE_CXX_COMPILER={Slash(t.ClangXX)}",
            $"-DCMAKE_MAKE_PROGRAM={Slash(t.Ninja)}",
            $"-DCMAKE_PREFIX_PATH={Slash(AppPaths.KitSdk)}",
            "-Wno-dev",
        ], env, null, cancel, "Configuring the build failed.");

    // ---- 4. Recompile ----------------------------------------------------------------

    async Task RecompileAsync(Toolchain t, Dictionary<string, string> env, CancellationToken cancel)
    {
        Begin(Stage.Recompile, "Translating the Xbox 360 code to C++");
        // ReXGlue's output reaches us only when it finishes (Ninja buffers it), so the bar
        // eases toward the end over about a minute instead.
        var watch = Stopwatch.StartNew();
        using var timer = new Timer(_ =>
        {
            double s = watch.Elapsed.TotalSeconds;
            ui.SetProgress(Stage.Recompile, 0.9 * (1 - Math.Exp(-s / 30)),
                "Translating the Xbox 360 code to C++");
        }, null, 0, 500);
        await RunCheckedAsync(t.CMake, ["--build", WorkBin, "--target", "nhl3on3_codegen"], env, null, cancel,
            "Recompiling the game code failed.");
        timer.Dispose();
        ui.SetProgress(Stage.Recompile, 0.95, "Adding the generated code to the build");
        // The generated sources are only picked up by a fresh configure.
        await ConfigureAsync(t, env, cancel);
        ui.SetStage(Stage.Recompile, StageState.Done, $"Done in {watch.Elapsed.TotalSeconds:0} s");
    }

    // ---- 5. Compile ------------------------------------------------------------------

    [GeneratedRegex(@"^\[(\d+)/(\d+)\]\s*(.*)$")]
    private static partial Regex NinjaStatus();

    async Task CompileAsync(Toolchain t, Dictionary<string, string> env, CancellationToken cancel)
    {
        int jobs = Jobs();
        Begin(Stage.Compile, "Starting the compiler");
        Log($"Compiling with {jobs} parallel jobs ({Environment.ProcessorCount} CPU threads, " +
            $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 30} GB RAM{(LowMemoryMode ? ", low-memory mode" : "")})");
        await RunCheckedAsync(t.CMake, ["--build", WorkBin, "--parallel", jobs.ToString()], env, line =>
        {
            var m = NinjaStatus().Match(line);
            if (!m.Success) return;
            int done = int.Parse(m.Groups[1].Value), total = int.Parse(m.Groups[2].Value);
            string what = m.Groups[3].Value;
            string detail = what.StartsWith("Linking", StringComparison.Ordinal)
                ? "Linking nhl3on3.exe"
                : $"{done:N0} of {total:N0} files";
            ui.SetProgress(Stage.Compile, total == 0 ? 0 : (double)done / total, detail);
        }, cancel, "Compiling the game failed.");
        ui.SetStage(Stage.Compile, StageState.Done);
    }

    static int Jobs()
    {
        long ramGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 30;
        // The generated files are large; give each compiler about 2 GB.
        int jobs = Math.Clamp(Math.Min(Environment.ProcessorCount, (int)Math.Max(1, ramGb / 2)), 1, 64);
        return LowMemoryMode ? Math.Max(1, jobs / 2) : jobs;
    }

    // ---- 6. Finish -------------------------------------------------------------------

    async Task FinishAsync(Toolchain t)
    {
        Begin(Stage.Finish, "Installing the game");
        var files = new List<string> { Path.Combine(WorkBin, "nhl3on3.exe") };
        files.AddRange(Directory.EnumerateFiles(WorkBin, "*.dll"));
        if (!File.Exists(files[0])) throw new BuildException("The build finished, but nhl3on3.exe is missing.");
        foreach (string file in files)
        {
            string dest = Path.Combine(AppPaths.BaseDir, Path.GetFileName(file));
            File.Copy(file, dest + ".new", true);
            File.Move(dest + ".new", dest, true);
            Log($"Installed {Path.GetFileName(file)}");
        }
        // Keep the player's settings if they have some already.
        string toml = Path.Combine(WorkSrc, "nhl3on3.toml");
        if (!File.Exists(AppPaths.GameSettings) && File.Exists(toml)) File.Copy(toml, AppPaths.GameSettings);

        Json.Save(AppPaths.BuildInfoFile, new BuildInfo
        {
            KitVersion = kit.Version,
            XexSha256 = source.XexSha256,
            Toolchain = t.Description,
            BuiltAt = DateTime.Now,
        });

        ui.SetProgress(Stage.Finish, 0.5, "Cleaning up");
        await Task.Run(() =>
        {
            try { DeleteDirectory(AppPaths.WorkDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"Could not remove the build folder {AppPaths.WorkDir}: {ex.Message}");
            }
        });
        ui.SetStage(Stage.Finish, StageState.Done);
    }

    // ---- helpers ---------------------------------------------------------------------

    async Task RunCheckedAsync(string exe, string[] args, Dictionary<string, string> env, Action<string>? onLine,
        CancellationToken cancel, string failure)
    {
        Log("> " + Path.GetFileName(exe) + " " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        _errors.Clear();
        int code = await ProcessRunner.RunAsync(exe, args, AppPaths.WorkDir, env, line =>
        {
            Log(line);
            if (_errors.Count < 8 && (line.Contains("error:", StringComparison.OrdinalIgnoreCase)
                                      || line.StartsWith("FAILED:", StringComparison.Ordinal)
                                      || line.StartsWith("CMake Error", StringComparison.Ordinal)))
                _errors.Add(line.Trim());
            if (line.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
                || line.Contains("bad_alloc", StringComparison.Ordinal)
                || line.Contains("not enough space", StringComparison.OrdinalIgnoreCase)
                || line.Contains("paging file", StringComparison.OrdinalIgnoreCase))
                _outOfMemory = true;
            onLine?.Invoke(line);
        }, cancel);
        if (code == 0) return;
        Log($"Exit code {code}");
        if (_outOfMemory || code == unchecked((int)0xC0000017) || code == unchecked((int)0xC00000FD))
        {
            _outOfMemory = true;
            throw new BuildException(failure + " Your PC ran out of memory.",
                "Close other programs and click Try again: the next attempt uses fewer parallel jobs and picks up where this one stopped.");
        }
        string firstError = _errors.FirstOrDefault(e => !e.StartsWith("FAILED:", StringComparison.Ordinal))
                            ?? _errors.FirstOrDefault() ?? $"exit code {code}";
        throw new BuildException(failure, Shorten(firstError, 300));
    }

    /// <summary>True when the copy of the game this build replaces is running. Other copies
    /// (a developer's build elsewhere, say) don't matter.</summary>
    static bool GameIsRunning()
    {
        foreach (var p in Process.GetProcessesByName("nhl3on3"))
        {
            using (p)
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, AppPaths.GameExe, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        return false;
    }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    static string Slash(string path) => path.Replace('\\', '/');

    static void RequireFreeSpace(string path, long bytes, string what)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        if (drive.AvailableFreeSpace < bytes)
            throw new BuildException($"Not enough free space on {drive.Name.TrimEnd('\\')} for {what}.",
                $"It needs about {Math.Ceiling(bytes / (double)(1 << 30)):0} GB free; there is {drive.AvailableFreeSpace / (double)(1 << 30):0.0} GB.");
    }

    static void CopyDirectory(string from, string to, IProgress<(long, long)>? progress, CancellationToken cancel,
        bool onlyIfChanged = false)
    {
        var files = new DirectoryInfo(from).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        long total = files.Sum(f => f.Length), done = 0;
        foreach (var file in files)
        {
            cancel.ThrowIfCancellationRequested();
            string dest = Path.Combine(to, Path.GetRelativePath(from, file.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!onlyIfChanged || !SameFile(file, dest)) file.CopyTo(dest, true);
            done += file.Length;
            progress?.Report((done, total));
        }
    }

    static bool SameFile(FileInfo a, string b)
    {
        var fb = new FileInfo(b);
        if (!fb.Exists || fb.Length != a.Length) return false;
        return File.ReadAllBytes(a.FullName).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    static void DeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            if (f.IsReadOnly) f.IsReadOnly = false;
        Directory.Delete(dir, true);
    }
}
