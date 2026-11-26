using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Win32;

namespace Nhl3on3Launcher;

public sealed record VsInstance(string Path, Version Version, string Name, string ProductId)
{
    public bool IsBuildTools => ProductId.EndsWith(".BuildTools", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A Visual Studio install with everything the build needs.</summary>
public sealed class Toolchain
{
    public required VsInstance Instance { get; init; }
    public required string VcVars { get; init; }
    public required string LlvmBin { get; init; }
    public required string CMake { get; init; }
    public required string Ninja { get; init; }
    public required int ClangMajor { get; init; }

    public string Description => $"{Instance.Name} {Instance.Version.Major}.{Instance.Version.Minor} · Clang {ClangMajor}";
    public string Clang => Path.Combine(LlvmBin, "clang.exe");
    public string ClangXX => Path.Combine(LlvmBin, "clang++.exe");

    /// <summary>The environment of an x64 Developer Command Prompt, with Visual Studio's
    /// Clang, CMake and Ninja first on PATH.</summary>
    public async Task<Dictionary<string, string>> CaptureEnvironmentAsync(CancellationToken cancel)
    {
        string args = $"/d /s /c \"\"{VcVars}\" >nul 2>&1 && set\"";
        var (code, output) = await ProcessRunner.CaptureAsync("cmd.exe", args, cancel,
            new Dictionary<string, string> { ["VSCMD_SKIP_SENDTELEMETRY"] = "1" });
        if (code != 0)
            throw new InvalidOperationException($"Setting up the Visual Studio environment failed (vcvars64.bat exited with {code}).");
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in output.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            env[line[..eq]] = line[(eq + 1)..].TrimEnd('\r');
        }
        if (!env.ContainsKey("VCINSTALLDIR") || !env.ContainsKey("INCLUDE"))
            throw new InvalidOperationException("Setting up the Visual Studio environment failed (vcvars64.bat set nothing).");
        env.TryGetValue("PATH", out string? path);
        env["PATH"] = string.Join(';', LlvmBin, Path.GetDirectoryName(CMake)!, Path.GetDirectoryName(Ninja)!, path ?? "");
        return env;
    }
}

/// <summary>What <see cref="ToolchainLocator.Detect"/> found.</summary>
public sealed class ToolchainStatus
{
    public Toolchain? Ready { get; init; }
    /// <summary>An existing Build Tools 2026+ install to add the missing pieces to, if any.</summary>
    public VsInstance? ModifyTarget { get; init; }
    public IReadOnlyList<string> Missing { get; init; } = [];
}

static class ToolchainLocator
{
    public const int MinClangMajor = 20;   // the generated code needs Clang 20+
    public const int MinVsMajor = 18;      // Visual Studio 2026 ships Clang 20+; 2022 has 19
    public const string BootstrapperUrl = "https://aka.ms/vs/18/stable/vs_buildtools.exe";
    public const string InfoUrl = "https://visualstudio.microsoft.com/visual-cpp-build-tools/";
    public const string ApproxDownload = "a few GB to download, about 10 GB on disk";

    static readonly string[] Components =
    [
        "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
        "Microsoft.VisualStudio.Component.VC.CMake.Project",
        "Microsoft.VisualStudio.Component.VC.Llvm.Clang",
        "Microsoft.VisualStudio.Component.Windows11SDK.26100",
    ];

    static string VsWhere => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Microsoft Visual Studio", "Installer", "vswhere.exe");

    public static async Task<ToolchainStatus> DetectAsync()
    {
        var instances = await QueryInstancesAsync();
        bool sdk = HasWindowsSdk();
        VsInstance? modify = null;
        List<string>? modifyMissing = null;
        List<string>? anyMissing = null;
        foreach (var inst in instances.OrderByDescending(i => i.Version))
        {
            var (toolchain, missing) = Inspect(inst, sdk);
            if (toolchain != null) return new ToolchainStatus { Ready = toolchain };
            anyMissing ??= missing;
            if (modify == null && inst.IsBuildTools && inst.Version.Major >= MinVsMajor)
            {
                modify = inst;
                modifyMissing = missing;
            }
        }
        return new ToolchainStatus
        {
            ModifyTarget = modify,
            Missing = modifyMissing ?? anyMissing ?? ["Microsoft C++ Build Tools"],
        };
    }

    static (Toolchain?, List<string>) Inspect(VsInstance inst, bool windowsSdk)
    {
        var missing = new List<string>();
        string vc = Path.Combine(inst.Path, "VC");
        string vcvars = Path.Combine(vc, "Auxiliary", "Build", "vcvars64.bat");
        bool msvc = File.Exists(vcvars) && Directory.Exists(Path.Combine(vc, "Tools", "MSVC"))
            && Directory.EnumerateDirectories(Path.Combine(vc, "Tools", "MSVC"))
                .Any(d => File.Exists(Path.Combine(d, "include", "vector")));
        if (!msvc) missing.Add("MSVC C++ x64 build tools");

        string llvmBin = Path.Combine(vc, "Tools", "Llvm", "x64", "bin");
        int clangMajor = 0;
        string clangLib = Path.Combine(vc, "Tools", "Llvm", "x64", "lib", "clang");
        if (File.Exists(Path.Combine(llvmBin, "clang++.exe")) && Directory.Exists(clangLib))
        {
            foreach (string d in Directory.EnumerateDirectories(clangLib))
                if (int.TryParse(Path.GetFileName(d).Split('.')[0], out int major))
                    clangMajor = Math.Max(clangMajor, major);
        }
        if (clangMajor == 0) missing.Add("C++ Clang compiler for Windows");
        else if (clangMajor < MinClangMajor) missing.Add($"Clang {MinClangMajor} or newer (this install has Clang {clangMajor})");

        string cmakeRoot = Path.Combine(inst.Path, "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake");
        string cmake = Path.Combine(cmakeRoot, "CMake", "bin", "cmake.exe");
        string ninja = Path.Combine(cmakeRoot, "Ninja", "ninja.exe");
        if (!File.Exists(cmake) || !File.Exists(ninja)) missing.Add("C++ CMake tools for Windows");
        if (!windowsSdk) missing.Add("Windows SDK");

        if (missing.Count > 0) return (null, missing);
        return (new Toolchain
        {
            Instance = inst, VcVars = vcvars, LlvmBin = llvmBin, CMake = cmake, Ninja = ninja, ClangMajor = clangMajor,
        }, missing);
    }

    static async Task<List<VsInstance>> QueryInstancesAsync()
    {
        var list = new List<VsInstance>();
        if (!File.Exists(VsWhere)) return list;
        try
        {
            var (code, json) = await ProcessRunner.CaptureAsync(VsWhere,
                "-all -prerelease -products * -format json -utf8");
            if (code != 0) return list;
            using var doc = JsonDocument.Parse(json);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string? path = e.TryGetProperty("installationPath", out var p) ? p.GetString() : null;
                string? ver = e.TryGetProperty("installationVersion", out var v) ? v.GetString() : null;
                string name = e.TryGetProperty("displayName", out var n) ? n.GetString() ?? "Visual Studio" : "Visual Studio";
                string product = e.TryGetProperty("productId", out var pid) ? pid.GetString() ?? "" : "";
                if (path == null || !Version.TryParse(ver, out var version) || !Directory.Exists(path)) continue;
                list.Add(new VsInstance(path, version, name, product));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or Win32Exception or InvalidOperationException) { }
        return list;
    }

    static bool HasWindowsSdk()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
                if (key?.GetValue("KitsRoot10") is not string root) continue;
                string lib = Path.Combine(root, "Lib");
                if (!Directory.Exists(lib)) continue;
                if (Directory.EnumerateDirectories(lib).Any(d =>
                        File.Exists(Path.Combine(d, "um", "x64", "kernel32.lib")) &&
                        File.Exists(Path.Combine(d, "ucrt", "x64", "ucrt.lib"))))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return false;
    }

    /// <summary>Downloads Microsoft's Build Tools bootstrapper, checks its signature, and runs it
    /// (elevated, in passive mode: Microsoft's own progress window, no questions) to install or
    /// complete Build Tools 2026. Returns a message for the player when it did not work.</summary>
    public static async Task<string?> InstallAsync(VsInstance? modify, Action<string> log,
        IProgress<double> downloadProgress, CancellationToken cancel)
    {
        string dir = Path.Combine(Path.GetTempPath(), "nhl3on3-launcher");
        Directory.CreateDirectory(dir);
        string bootstrapper = Path.Combine(dir, "vs_buildtools.exe");

        log($"Downloading {BootstrapperUrl}");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        using (var response = await http.GetAsync(BootstrapperUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(cancel);
            await using var output = File.Create(bootstrapper);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, cancel)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), cancel);
                done += n;
                if (total > 0) downloadProgress.Report((double)done / total);
            }
        }

        if (!IsSignedByMicrosoft(bootstrapper))
        {
            File.Delete(bootstrapper);
            return "The downloaded Build Tools installer isn't signed by Microsoft, so it was not run.";
        }

        var args = new List<string>();
        if (modify != null)
        {
            args.AddRange(["modify", "--installPath", modify.Path]);
        }
        else
        {
            args.AddRange(["--add", "Microsoft.VisualStudio.Workload.VCTools"]);
        }
        foreach (string c in Components) args.AddRange(["--add", c]);
        args.AddRange(["--passive", "--wait", "--norestart"]);
        log($"Running vs_buildtools.exe {string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");

        var psi = new ProcessStartInfo(bootstrapper) { UseShellExecute = true, Verb = "runas" };
        foreach (string a in args) psi.ArgumentList.Add(a);
        Process? process;
        try { process = Process.Start(psi); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Windows didn't get permission to install the Build Tools.";
        }
        if (process == null) return "The Build Tools installer didn't start.";
        using (process)
        {
            await process.WaitForExitAsync(cancel);
            int code = process.ExitCode;
            log($"Build Tools installer exited with {code}");
            return code switch
            {
                0 or 3010 => null,  // 3010: done, a restart is recommended
                1602 or 1223 =>"The Build Tools installation was cancelled.",
                5007 => "The Build Tools installation was blocked by policy on this PC.",
                8006 => "Close Visual Studio and the Visual Studio Installer, then try again.",
                1641 => "Windows needs to restart to finish installing the Build Tools. Restart, then open the launcher again.",
                _ => $"The Build Tools installer stopped with code {code}. Its log is in %TEMP% (files named dd_*.log).",
            };
        }
    }

    static bool IsSignedByMicrosoft(string file)
    {
        if (!AuthenticodeValid(file)) return false;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            return cert.Subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal);
        }
        catch (Exception) { return false; }
    }

    // --- WinVerifyTrust ---------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WinTrustFileInfo
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

    static bool AuthenticodeValid(string file)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");  // WINTRUST_ACTION_GENERIC_VERIFY_V2
        var fileInfo = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = file };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 1,       // WTD_STATEACTION_VERIFY
            };
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.dwStateAction = 2;      // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}
