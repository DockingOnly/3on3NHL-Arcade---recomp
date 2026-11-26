using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Nhl3on3Launcher;

public enum SourceKind
{
    /// <summary>An Xbox 360 STFS package (the file inside 58410975\000D0000).</summary>
    Package,
    /// <summary>A folder holding an already unpacked copy (with default.xex).</summary>
    Folder,
    /// <summary>The game folder next to the launcher, unpacked earlier.</summary>
    Installed,
}

/// <summary>The player's copy of the game, checked to be 3 on 3 NHL Arcade.</summary>
public sealed class GameSource
{
    public const uint TitleId = 0x58410975;

    /// <summary>SHA-256 of the default.xex this port's function list was made for.</summary>
    public const string KnownXexSha256 = "3ece385503278a11bc32dd38eb738a0cab7904f37dc6bf5682c918e7c510a71b";

    public required SourceKind Kind { get; init; }
    public required string Path { get; init; }
    public required string DisplayName { get; init; }
    public required long Size { get; init; }
    public required string XexSha256 { get; init; }
    public byte[]? Thumbnail { get; init; }

    public bool IsKnownVersion => XexSha256 == KnownXexSha256;

    /// <summary>Works out what the player pointed at: a package, a folder with a package
    /// somewhere inside it, or an unpacked copy. Throws <see cref="GameSourceException"/>
    /// with a message for the player when it is none of those.</summary>
    public static GameSource Resolve(string path)
    {
        if (File.Exists(path))
        {
            if (string.Equals(System.IO.Path.GetFileName(path), "default.xex", StringComparison.OrdinalIgnoreCase))
                return FromFolder(System.IO.Path.GetDirectoryName(path)!);
            return FromPackage(path);
        }
        if (Directory.Exists(path))
        {
            if (File.Exists(System.IO.Path.Combine(path, "default.xex")))
                return FromFolder(path);
            string? package = FindPackage(path, maxDepth: 4)
                ?? throw new GameSourceException(
                    "No 3 on 3 NHL Arcade package was found in that folder.",
                    "Pick the 58410975 folder, or the file inside 58410975\\000D0000.");
            return FromPackage(package);
        }
        throw new GameSourceException("That file or folder no longer exists.");
    }

    public static GameSource FromPackage(string file)
    {
        using var pkg = StfsPackage.TryOpen(file)
            ?? throw new GameSourceException("That file isn't an Xbox 360 package.",
                "Pick the file inside 58410975\\000D0000 (its name is a long string of letters and digits).");
        if (pkg.TitleId != TitleId)
            throw new GameSourceException($"That package is “{pkg.DisplayName}”, not 3 on 3 NHL Arcade.",
                $"Its title ID is {pkg.TitleId:X8}; this port needs 58410975.");

        List<StfsPackage.Entry> entries;
        try { entries = pkg.ReadEntries(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or EndOfStreamException)
        {
            throw new GameSourceException("The package looks damaged and can't be read.", ex.Message);
        }
        var xexEntry = entries.FirstOrDefault(e => !e.IsDirectory
            && string.Equals(e.Path, "default.xex", StringComparison.OrdinalIgnoreCase))
            ?? throw new GameSourceException("The package doesn't contain the game (default.xex is missing).",
                "This may be a title update or other add-on rather than the game itself.");
        byte[] xex = pkg.ReadFile(xexEntry);
        CheckXex(xex);
        return new GameSource
        {
            Kind = SourceKind.Package,
            Path = file,
            DisplayName = string.IsNullOrWhiteSpace(pkg.DisplayName) ? "3 on 3 NHL Arcade" : pkg.DisplayName,
            Size = new FileInfo(file).Length,
            XexSha256 = Convert.ToHexString(SHA256.HashData(xex)).ToLowerInvariant(),
            Thumbnail = pkg.Thumbnail,
        };
    }

    public static GameSource FromFolder(string folder)
    {
        string full = System.IO.Path.GetFullPath(folder);
        byte[] xex = File.ReadAllBytes(System.IO.Path.Combine(full, "default.xex"));
        CheckXex(xex);
        bool installed = string.Equals(full.TrimEnd('\\'), AppPaths.GameDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        long size = new DirectoryInfo(full).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        return new GameSource
        {
            Kind = installed ? SourceKind.Installed : SourceKind.Folder,
            Path = full,
            DisplayName = "3 on 3 NHL Arcade",
            Size = size,
            XexSha256 = Convert.ToHexString(SHA256.HashData(xex)).ToLowerInvariant(),
        };
    }

    /// <summary>Checks the XEX2 magic and the title ID in its execution info header.</summary>
    static void CheckXex(byte[] xex)
    {
        if (xex.Length < 0x20 || !xex.AsSpan(0, 4).SequenceEqual("XEX2"u8))
            throw new GameSourceException("default.xex is damaged or not an Xbox 360 executable.");
        uint count = BinaryPrimitives.ReadUInt32BigEndian(xex.AsSpan(0x14));
        for (uint i = 0; i < count && 0x18 + i * 8 + 8 <= xex.Length; i++)
        {
            int at = (int)(0x18 + i * 8);
            uint key = BinaryPrimitives.ReadUInt32BigEndian(xex.AsSpan(at));
            if (key != 0x00040006) continue;  // execution info
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(xex.AsSpan(at + 4));
            if (offset + 16 > xex.Length) break;
            uint title = BinaryPrimitives.ReadUInt32BigEndian(xex.AsSpan((int)offset + 12));
            if (title != TitleId)
                throw new GameSourceException("That's a different game, not 3 on 3 NHL Arcade.",
                    $"Its title ID is {title:X8}; this port needs 58410975.");
            return;
        }
    }

    /// <summary>Looks for the game's package in a folder and a few levels below it.</summary>
    public static string? FindPackage(string root, int maxDepth, IEnumerable<string>? skipDirs = null)
    {
        var skip = new HashSet<string>(skipDirs ?? [], StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = maxDepth, IgnoreInaccessible = true };
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", options); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        foreach (string file in files)
        {
            if (skip.Any(d => file.StartsWith(d + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
            if (System.IO.Path.GetExtension(file).Length > 0) continue;  // packages have no extension
            try
            {
                if (new FileInfo(file).Length < 1 << 20) continue;
                using var pkg = StfsPackage.TryOpen(file);
                if (pkg?.TitleId == TitleId) return file;
            }
            catch (IOException) { }
        }
        return null;
    }
}

public sealed class GameSourceException(string message, string? hint = null) : Exception(message)
{
    public string? Hint { get; } = hint;
}
