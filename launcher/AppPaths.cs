using System.Text.Json;

namespace Nhl3on3Launcher;

/// <summary>Where the launcher keeps things. Everything the player keeps lives next to the
/// launcher; the temporary build tree lives in %LOCALAPPDATA% so long or odd release folder
/// paths never reach the compiler.</summary>
static class AppPaths
{
    public static readonly string BaseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');

    public static string GameDir => Path.Combine(BaseDir, "game");
    public static string GameExe => Path.Combine(BaseDir, "nhl3on3.exe");
    public static string GameSettings => Path.Combine(BaseDir, "nhl3on3.toml");
    public static string BuildInfoFile => Path.Combine(BaseDir, "nhl3on3.build.json");
    public static string LauncherSettingsFile => Path.Combine(BaseDir, "launcher.json");
    public static string LogsDir => Path.Combine(BaseDir, "logs");
    public static string BuildLog => Path.Combine(LogsDir, "build.log");

    public static string KitDir => Environment.GetEnvironmentVariable("NHL3ON3_BUILDKIT") is { Length: > 0 } dir
        ? dir : Path.Combine(BaseDir, "buildkit");
    public static string KitSource => Path.Combine(KitDir, "source");
    public static string KitSdk => Path.Combine(KitDir, "sdk");

    public static string WorkDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nhl3on3", "build");

    public static bool GameUnpacked => File.Exists(Path.Combine(GameDir, "default.xex"));
    public static bool GameBuilt => File.Exists(GameExe);

    /// <summary>True when the launcher can write next to itself (not in Program Files, say).</summary>
    public static bool BaseDirWritable()
    {
        try
        {
            string probe = Path.Combine(BaseDir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

static class Json
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static T? Load<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void Save<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
}

/// <summary>buildkit/kit.json: identifies the sources and SDK the release ships.</summary>
public sealed class KitInfo
{
    public string Version { get; set; } = "";
    public static KitInfo? Load()
    {
        if (!Directory.Exists(AppPaths.KitSource) || !Directory.Exists(AppPaths.KitSdk)) return null;
        return Json.Load<KitInfo>(Path.Combine(AppPaths.KitDir, "kit.json")) ?? new KitInfo { Version = "dev" };
    }
}

/// <summary>nhl3on3.build.json, written next to the game after a successful build.</summary>
public sealed class BuildInfo
{
    public string KitVersion { get; set; } = "";
    public string XexSha256 { get; set; } = "";
    public string Toolchain { get; set; } = "";
    public DateTime BuiltAt { get; set; }
    public static BuildInfo? Load() => Json.Load<BuildInfo>(AppPaths.BuildInfoFile);
}

public sealed class LauncherSettings
{
    public bool CloseOnPlay { get; set; } = true;
    public static LauncherSettings Load() => Json.Load<LauncherSettings>(AppPaths.LauncherSettingsFile) ?? new();
    public void Save()
    {
        try { Json.Save(AppPaths.LauncherSettingsFile, this); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
