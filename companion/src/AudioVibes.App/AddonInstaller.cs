using System.Security.Cryptography;
using Microsoft.Win32;
using Serilog;

namespace AudioVibes.App;

/// <summary>A WoW client folder (the one with Interface and WTF in it), e.g. ...\World of Warcraft\_forever_.</summary>
public sealed record WowClient(string Path, string Label)
{
    public string AddOnsDir => System.IO.Path.Combine(Path, "Interface", "AddOns");
    public string AddonDir => System.IO.Path.Combine(AddOnsDir, AddonInstaller.AddonName);
}

public enum AddonState
{
    NotInstalled,
    UpToDate,
    /// <summary>An older version: safe to update.</summary>
    Outdated,
    /// <summary>Same or newer version number but different files (e.g. a development copy).</summary>
    Different,
    /// <summary>The folder is a link (junction/symlink) to somewhere else, e.g. a developer's repo: never touched.</summary>
    Linked,
}

public sealed record AddonStatus(WowClient Client, AddonState State, string? InstalledVersion);

/// <summary>
/// Installs the copy of the addon that ships with the companion (addon\AudioVibes next to the exe) into WoW
/// clients' Interface\AddOns. Installs go to a staging folder first and are swapped in, so a failure never
/// leaves a half-copied addon. The player's Resolved.lua is kept.
/// </summary>
public static class AddonInstaller
{
    public const string AddonName = "AudioVibes";

    public static string BundledDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "addon", AddonName);
    public static bool HasBundle => File.Exists(Path.Combine(BundledDir, AddonName + ".toc"));
    public static string? BundledVersion => TocVersion(BundledDir);

    private static readonly Dictionary<string, string> FlavorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_forever_"] = "WoW Forever", ["_retail_"] = "Retail", ["_classic_"] = "Classic",
        ["_classic_era_"] = "Classic Era", ["_anniversary_"] = "Anniversary", ["_ptr_"] = "Retail PTR",
        ["_beta_"] = "Beta", ["_xptr_"] = "Retail XPTR", ["_classic_ptr_"] = "Classic PTR",
    };

    // ------------------------------------------------------------------ finding WoW

    /// <summary>WoW clients on this PC: Blizzard's registry entries, the usual install folders on every drive, and any extra folders given.</summary>
    public static List<WowClient> FindClients(IEnumerable<string>? extraFolders = null, IEnumerable<string>? roots = null)
    {
        var found = new Dictionary<string, WowClient>(StringComparer.OrdinalIgnoreCase);
        void AddClient(string dir)
        {
            dir = Path.GetFullPath(dir).TrimEnd('\\');
            if (found.ContainsKey(dir) || !LooksLikeClient(dir)) return;
            var name = Path.GetFileName(dir);
            found[dir] = new WowClient(dir, FlavorNames.GetValueOrDefault(name) ?? Pretty(name));
        }
        void AddRoot(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                if (LooksLikeClient(root)) { AddClient(root); return; }   // a client folder itself
                foreach (var sub in Directory.EnumerateDirectories(root, "_*_")) AddClient(sub);
            }
            catch (Exception e) { Log.Debug(e, "couldn't look in {Root}", root); }
        }

        foreach (var r in roots ?? DefaultRoots()) AddRoot(r);
        foreach (var f in extraFolders ?? []) if (!string.IsNullOrWhiteSpace(f)) AddRoot(f);
        return found.Values.OrderBy(c => c.Label == "WoW Forever" ? 0 : 1).ThenBy(c => c.Label).ToList();
    }

    /// <summary>"_classic_beta_" → "Classic Beta", "_ptr_" handled above, a plain folder name stays as it is.</summary>
    private static string Pretty(string folder)
    {
        var words = folder.Trim('_').Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length <= 3 ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]);
        var s = string.Join(' ', words);
        return s.Length > 0 ? s : folder;
    }

    private static bool LooksLikeClient(string dir) =>
        Directory.Exists(Path.Combine(dir, "Interface")) || Directory.Exists(Path.Combine(dir, "WTF"))
        || Directory.EnumerateFiles(dir, "Wow*.exe").Any();

    private static IEnumerable<string> DefaultRoots()
    {
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft", @"SOFTWARE\Blizzard Entertainment\World of Warcraft" })
        {
            string? path = null;
            try { using var k = Registry.LocalMachine.OpenSubKey(key); path = k?.GetValue("InstallPath") as string; } catch { }
            if (!string.IsNullOrWhiteSpace(path))
            {
                var p = path.TrimEnd('\\');
                yield return p;
                if (Path.GetFileName(p).StartsWith('_')) yield return Path.GetDirectoryName(p)!;   // InstallPath is often ...\_retail_
            }
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            foreach (var sub in new[] { @"Program Files (x86)\World of Warcraft", @"Program Files\World of Warcraft", "World of Warcraft",
                                        @"Games\World of Warcraft", @"Battle.net\World of Warcraft" })
                yield return Path.Combine(drive.RootDirectory.FullName, sub);
    }

    // ------------------------------------------------------------------ what's installed

    public static string? TocVersion(string addonDir)
    {
        try
        {
            var toc = Path.Combine(addonDir, AddonName + ".toc");
            if (!File.Exists(toc)) return null;
            foreach (var line in File.ReadLines(toc))
                if (line.StartsWith("## Version:", StringComparison.OrdinalIgnoreCase)) return line[11..].Trim();
        }
        catch (Exception e) { Log.Debug(e, "couldn't read the addon version in {Dir}", addonDir); }
        return null;
    }

    public static bool IsLink(string dir)
    {
        try { return Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch { return false; }
    }

    public static AddonStatus Status(WowClient c)
    {
        var dir = c.AddonDir;
        if (!Directory.Exists(dir)) return new(c, AddonState.NotInstalled, null);
        var installed = TocVersion(dir);
        if (IsLink(dir)) return new(c, AddonState.Linked, installed);
        if (SameFiles(BundledDir, dir)) return new(c, AddonState.UpToDate, installed);
        bool older = installed == null || (Version.TryParse(installed, out var i) && Version.TryParse(BundledVersion, out var b) && i < b);
        return new(c, older ? AddonState.Outdated : AddonState.Different, installed);
    }

    /// <summary>Same files with the same contents (ignoring the per-machine Resolved.lua).</summary>
    public static bool SameFiles(string a, string b)
    {
        static Dictionary<string, long> List(string root) =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !Ignored(f))
                .ToDictionary(f => Path.GetRelativePath(root, f), f => new FileInfo(f).Length, StringComparer.OrdinalIgnoreCase);
        try
        {
            var la = List(a); var lb = List(b);
            if (la.Count != lb.Count || la.Any(kv => !lb.TryGetValue(kv.Key, out var len) || len != kv.Value)) return false;
            foreach (var rel in la.Keys)
                if (!Hash(Path.Combine(a, rel)).AsSpan().SequenceEqual(Hash(Path.Combine(b, rel)))) return false;
            return true;
        }
        catch { return false; }
    }

    private static byte[] Hash(string file) { using var s = File.OpenRead(file); return SHA256.HashData(s); }

    private static bool Ignored(string file) =>
        Path.GetFileName(file).Equals("Resolved.lua", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ installing

    public const string ResolvedStub =
        "-- Written by the WoW Audio Vibes companion app (the playlist it has chosen for each location).\n" +
        "AudioVibesResolved = { locations = {}, types = {} }\n";

    /// <summary>Installs or updates the addon in a client. Throws with a readable message on failure.</summary>
    public static void Install(WowClient c)
    {
        if (!HasBundle) throw new InvalidOperationException("This copy of the companion doesn't include the addon files.");
        var target = c.AddonDir;
        if (IsLink(target))
            throw new InvalidOperationException($"{target} is linked to another folder (a development setup), so it's left alone.");
        try
        {
            Directory.CreateDirectory(c.AddOnsDir);
            CleanLeftovers(c);
            var staging = Path.Combine(c.AddOnsDir, AddonName + ".installing");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            CopyTree(BundledDir, staging);
            // keep this player's Resolved.lua (the companion's playlist choices); a fresh install gets the stub
            var oldResolved = Path.Combine(target, "Resolved.lua");
            if (File.Exists(oldResolved)) File.Copy(oldResolved, Path.Combine(staging, "Resolved.lua"), true);
            else File.WriteAllText(Path.Combine(staging, "Resolved.lua"), ResolvedStub);

            if (Directory.Exists(target))
            {
                var old = Path.Combine(c.AddOnsDir, $"{AddonName}.old-{DateTime.Now:yyyyMMddHHmmss}");
                Directory.Move(target, old);
                Directory.Move(staging, target);
                try { Directory.Delete(old, true); } catch (Exception e) { Log.Information("old addon copy left at {Dir}: {Msg}", old, e.Message); }
            }
            else Directory.Move(staging, target);
            Log.Information("installed the addon {Version} into {Dir}", BundledVersion, target);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new InvalidOperationException($"Windows didn't allow writing to {c.AddOnsDir}. Try running the companion as administrator once, or install the addon by hand.", e);
        }
        catch (IOException e)
        {
            throw new InvalidOperationException($"Couldn't install into {c.AddOnsDir}: {e.Message}", e);
        }
    }

    /// <summary>Folders from an earlier install that couldn't be removed then (WoW had a file open).</summary>
    private static void CleanLeftovers(WowClient c)
    {
        // filter by name ourselves: Windows' wildcard rules can match more than the pattern suggests
        foreach (var d in Directory.EnumerateDirectories(c.AddOnsDir))
            if (Path.GetFileName(d).StartsWith(AddonName + ".old-", StringComparison.OrdinalIgnoreCase) && !IsLink(d))
                try { Directory.Delete(d, true); } catch { }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (Ignored(f)) continue;
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }

    /// <summary>Updates every client that has an older version installed (never installs where it isn't, never touches links).
    /// Returns the labels of the clients updated.</summary>
    public static List<string> UpdateOutdated(IEnumerable<WowClient> clients)
    {
        var done = new List<string>();
        if (!HasBundle) return done;
        foreach (var c in clients)
        {
            if (Status(c).State != AddonState.Outdated) continue;
            try { Install(c); done.Add(c.Label); }
            catch (Exception e) { Log.Warning(e, "couldn't update the addon in {Client}", c.Path); }
        }
        return done;
    }
}
