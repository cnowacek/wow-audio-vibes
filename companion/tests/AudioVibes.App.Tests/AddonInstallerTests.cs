using System.Diagnostics;
using AudioVibes.App;
using Xunit;

namespace AudioVibes.App.Tests;

[Collection("AddonInstaller")]   // BundledDir is static
public class AddonInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "av-wow-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundle;
    private readonly string _savedBundle = AddonInstaller.BundledDir;

    public AddonInstallerTests()
    {
        // a fake WoW install with two clients, and a small fake addon bundle at version 1.3.0
        Directory.CreateDirectory(Path.Combine(_root, "World of Warcraft", "_forever_", "Interface", "AddOns"));
        Directory.CreateDirectory(Path.Combine(_root, "World of Warcraft", "_retail_", "WTF"));
        Directory.CreateDirectory(Path.Combine(_root, "World of Warcraft", "_notaclient_"));
        _bundle = Path.Combine(_root, "bundle", "AudioVibes");
        WriteAddon(_bundle, "1.3.0", "new code");
        AddonInstaller.BundledDir = _bundle;
    }

    public void Dispose()
    {
        AddonInstaller.BundledDir = _savedBundle;
        try { Directory.Delete(_root, true); } catch { }
    }

    private static void WriteAddon(string dir, string version, string code)
    {
        Directory.CreateDirectory(Path.Combine(dir, "sounds"));
        File.WriteAllText(Path.Combine(dir, "AudioVibes.toc"), $"## Interface: 16001\n## Title: Audio Vibes\n## Version: {version}\nCore.lua\n");
        File.WriteAllText(Path.Combine(dir, "Core.lua"), code);
        File.WriteAllBytes(Path.Combine(dir, "sounds", "cmd_01.ogg"), [1, 2, 3]);
    }

    private List<WowClient> Clients() => AddonInstaller.FindClients(roots: [Path.Combine(_root, "World of Warcraft")]);

    [Fact]
    public void FindsClientFolders()
    {
        var c = Clients();
        Assert.Equal(["WoW Forever", "Retail"], c.Select(x => x.Label));
        Directory.CreateDirectory(Path.Combine(_root, "World of Warcraft", "_classic_beta_", "WTF"));
        Assert.Contains("Classic Beta", Clients().Select(x => x.Label));
        // a client folder given directly works too
        Assert.Single(AddonInstaller.FindClients(roots: [], extraFolders: [Path.Combine(_root, "World of Warcraft", "_retail_")]));
    }

    [Fact]
    public void InstallsUpdatesAndKeepsResolvedLua()
    {
        var forever = Clients()[0];
        Assert.Equal(AddonState.NotInstalled, AddonInstaller.Status(forever).State);

        AddonInstaller.Install(forever);
        Assert.Equal(AddonState.UpToDate, AddonInstaller.Status(forever).State);
        Assert.True(File.Exists(Path.Combine(forever.AddonDir, "Resolved.lua")));   // fresh install gets the stub
        Assert.True(File.Exists(Path.Combine(forever.AddonDir, "sounds", "cmd_01.ogg")));

        // an older version installed, with this player's own Resolved.lua
        Directory.Delete(forever.AddonDir, true);
        WriteAddon(forever.AddonDir, "1.2.0", "old code");
        File.WriteAllText(Path.Combine(forever.AddonDir, "Resolved.lua"), "AudioVibesResolved = { mine = true }");
        var st = AddonInstaller.Status(forever);
        Assert.Equal(AddonState.Outdated, st.State);
        Assert.Equal("1.2.0", st.InstalledVersion);

        Assert.Equal(["WoW Forever"], AddonInstaller.UpdateOutdated(Clients()));   // retail has none: not installed there
        Assert.Equal("new code", File.ReadAllText(Path.Combine(forever.AddonDir, "Core.lua")));
        Assert.Contains("mine = true", File.ReadAllText(Path.Combine(forever.AddonDir, "Resolved.lua")));
        Assert.False(Directory.Exists(Path.Combine(_root, "World of Warcraft", "_retail_", "Interface", "AddOns", "AudioVibes")));
        Assert.Equal(["AudioVibes"], Directory.GetDirectories(forever.AddOnsDir).Select(Path.GetFileName));   // no staging/old folders left
    }

    [Fact]
    public void SameVersionWithOtherFilesIsDifferentNotOutdated()
    {
        var forever = Clients()[0];
        WriteAddon(forever.AddonDir, "1.3.0", "edited by hand");
        Assert.Equal(AddonState.Different, AddonInstaller.Status(forever).State);
        Assert.Empty(AddonInstaller.UpdateOutdated(Clients()));   // never replaced automatically
    }

    [Fact]
    public void NeverTouchesALinkedDevFolder()
    {
        var forever = Clients()[0];
        var repoCopy = Path.Combine(_root, "repo", "AudioVibes");
        WriteAddon(repoCopy, "1.0.0", "my dev code");
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{forever.AddonDir}\" \"{repoCopy}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(AddonInstaller.IsLink(forever.AddonDir), "junction wasn't created");

        Assert.Equal(AddonState.Linked, AddonInstaller.Status(forever).State);
        Assert.Throws<InvalidOperationException>(() => AddonInstaller.Install(forever));
        Assert.Empty(AddonInstaller.UpdateOutdated(Clients()));
        Assert.Equal("my dev code", File.ReadAllText(Path.Combine(repoCopy, "Core.lua")));
        Directory.Delete(forever.AddonDir);   // remove just the junction
    }
}
