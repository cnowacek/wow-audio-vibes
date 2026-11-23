using System.Text.Json;
using AudioVibes.Decoder;
using AudioVibes.Spotify;
using Serilog;

namespace AudioVibes.App;

public sealed class ResolvedPlaylist
{
    public string Uri { get; set; } = "";
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    public DateTime ResolvedAt { get; set; }
}

public sealed class SeenZone
{
    public string? Category { get; set; }
    public DateTime LastSeen { get; set; }
}

/// <summary>Where a playlist was left: its track and position, to pick up from there next time.</summary>
public sealed class ResumePoint
{
    public string TrackUri { get; set; } = "";
    public int PositionMs { get; set; }
    public DateTime At { get; set; }
}

/// <summary>Persistent companion state: resolved default playlists, visited zones, resume points.</summary>
public sealed class ZoneState
{
    /// <summary>Keyed "vibe|mode" (e.g. "city_horde|relaxed").</summary>
    public Dictionary<string, ResolvedPlaylist> VibeDefaults { get; set; } = new();
    public Dictionary<string, SeenZone> Seen { get; set; } = new();
    /// <summary>Keyed by playlist/album URI.</summary>
    public Dictionary<string, ResumePoint> Resume { get; set; } = new();

    public static readonly string FilePath = Path.Combine(AppConfig.Dir, "zone-state.json");
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static ZoneState Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<ZoneState>(File.ReadAllText(FilePath), Json) ?? new() : new(); }
        catch (Exception e) { Log.Warning(e, "zone-state.json unreadable; starting fresh"); return new(); }
    }

    public void Save()
    {
        try { Directory.CreateDirectory(AppConfig.Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); }
        catch (Exception e) { Log.Warning(e, "could not save zone-state.json"); }
    }
}

/// <summary>What will play for a zone, and why.</summary>
public sealed record ZoneChoice(string? ZoneKey, string ZoneName, string Vibe, string VibeName, string? Uri, string PlaylistLabel, string Source)
{
    public bool Shuffle { get; init; }
    public string Mode { get; init; } = Modes.Relaxed;
}

/// <summary>
/// Per-zone music with two vibe modes. The addon sends a zone id, the location's mode (MODE_RELAXED /
/// MODE_LOCKED) and a category event (EVT_ENTER_*); they're collected for a short window, then one
/// decision is made for that location in that mode:
///   in-game location playlist → companion zone playlist → in-game "every type" → companion vibe → default.
/// A mode signal on its own (player switched modes) re-decides the current location straight away.
/// The same playlist is never restarted; switching back to one picks up its last track and position.
/// </summary>
public sealed class ZoneMusic
{
    private readonly ZoneCatalog _catalog;
    private readonly SpotifyWebApi _web;
    private readonly Func<AppConfig> _config;
    private readonly Func<GameOverrides> _game;
    private readonly object _gate = new();
    private string? _lastZoneKey, _lastCategory;
    private DateTime _lastZoneAt;
    private string? _mode;                 // last mode the addon sent; null = not heard yet (Auto by type)
    private CancellationTokenSource? _temporary;
    private readonly SemaphoreSlim _resolveLock = new(1, 1);
    private ZoneState _state;
    private string? _pendingZone, _pendingCategory;
    private CancellationTokenSource? _decide;
    private string? _currentUri;
    private bool _warnedNoWebApi;

    /// <summary>Resume points older than this are ignored (the playlist starts from the top).</summary>
    public static readonly TimeSpan ResumeMaxAge = TimeSpan.FromHours(24);

    public ZoneCatalog Catalog => _catalog;
    public ZoneChoice? Current { get; private set; }
    public string? Mode { get { lock (_gate) return _mode; } }
    public event Action<ZoneChoice>? Changed;
    public event Action<string, bool>? Notify;
    /// <summary>A vibe's default playlist was looked up (so the addon's Resolved.lua should be rewritten).</summary>
    public event Action? DefaultsChanged;

    private readonly ILocalPlayer? _local;
    private readonly Func<Task>? _pauseSpotify;

    /// <param name="local">Player for radio streams and music folders (null: those can't play, e.g. in tests).</param>
    /// <param name="pauseSpotify">Pauses Spotify when local music takes over.</param>
    public ZoneMusic(ZoneCatalog catalog, SpotifyWebApi web, Func<AppConfig> config, Func<GameOverrides>? game = null,
        ILocalPlayer? local = null, Func<Task>? pauseSpotify = null)
    {
        _catalog = catalog; _web = web; _config = config; _game = game ?? (() => GameOverrides.Empty);
        _local = local; _pauseSpotify = pauseSpotify;
        _state = ZoneState.Load();
    }

    /// <summary>Spotify Web API set up and logged in: Spotify playlists can be started.</summary>
    public bool SpotifyReady
    {
        get
        {
            var c = _config();
            return c.Spotify.UseWebApi && !string.IsNullOrWhiteSpace(c.Spotify.ClientId) && _web.HasRefreshToken;
        }
    }

    /// <summary>Where defaults come from right now: "spotify", "radio" or "off" (see zoneMusic.defaultSource).</summary>
    public string DefaultSource => _config().ZoneMusic.DefaultSource?.Trim().ToLowerInvariant() switch
    {
        "spotify" => "spotify",
        "radio" => "radio",
        "off" or "none" => "off",
        _ => SpotifyReady ? "spotify" : "radio",
    };

    /// <summary>Every location key the companion knows: built-in list, visited, and learned in game.</summary>
    public IEnumerable<string> AllKeys() =>
        _catalog.Zones.Keys.Union(Seen.Keys).Union(_game().Seen.Keys).Distinct();

    /// <summary>Name for a zone key: built-in list, else the name the addon learned in game.</summary>
    public string NameOf(string key) =>
        _catalog.NameOf(key, _game().Seen.TryGetValue(key, out var s) ? s.Name : null);

    /// <summary>city | zone | dungeon | raid | pvp</summary>
    public string TypeOf(string key, string? category = null)
    {
        if (_catalog.Zones.TryGetValue(key, out var z)) return z.Type;
        if (_game().Seen.TryGetValue(key, out var s)) return s.Type;
        return ZoneCatalog.TypeFromCategory(category ?? Seen.GetValueOrDefault(key)?.Category);
    }

    public IReadOnlyDictionary<string, SeenZone> Seen { get { lock (_gate) return new Dictionary<string, SeenZone>(_state.Seen); } }

    private static string DefaultKey(string vibe, string mode) => vibe + "|" + mode;

    /// <summary>Cached search result for a vibe's mood, if it still matches the current search text.</summary>
    public ResolvedPlaylist? CachedDefault(string vibe, string mode)
    {
        var query = _catalog.Vibes.GetValueOrDefault(vibe)?.For(mode).Search;
        lock (_gate)
            return _state.VibeDefaults.TryGetValue(DefaultKey(vibe, mode), out var r) && r.Query == query ? r : null;
    }

    // ------------------------------------------------------------------ signals from the game

    public void OnZone(DecodedZone z)
    {
        lock (_gate)
        {
            _pendingZone = z.Key;
            _lastZoneKey = z.Key;
            _lastZoneAt = DateTime.Now;
            _state.Seen[z.Key] = new SeenZone { Category = _state.Seen.GetValueOrDefault(z.Key)?.Category, LastSeen = DateTime.Now };
        }
        Schedule();
    }

    public void OnCategory(string evt)
    {
        lock (_gate)
        {
            _pendingCategory = evt;
            if (_pendingZone != null && _state.Seen.TryGetValue(_pendingZone, out var s)) s.Category = evt;
        }
        Schedule();
    }

    /// <summary>
    /// MODE_RELAXED / MODE_LOCKED. Right after a location id it just says which of that location's two
    /// playlists to use; on its own it means the player switched modes, so the current location re-plays.
    /// </summary>
    public void OnMode(string mode)
    {
        bool attached;
        string? key, cat;
        lock (_gate)
        {
            if (Modes.Parse(mode) is not { } m) return;
            bool changed = _mode != m;
            _mode = m;
            attached = (DateTime.Now - _lastZoneAt).TotalSeconds < 1.5 || _pendingZone != null;
            key = Current?.ZoneKey ?? _lastZoneKey;
            cat = _lastCategory;
            if (!changed) return;
        }
        Log.Information("vibe mode → {Mode}{How}", mode, attached ? " (with location)" : "");
        if (attached || key == null) return;
        _ = Task.Run(async () =>
        {
            try { await DecideAsync(key, cat); }
            catch (Exception e) { Log.Warning(e, "mode switch failed"); }
        });
    }

    private void Schedule()
    {
        CancellationTokenSource cts;
        lock (_gate) { _decide?.Cancel(); cts = _decide = new CancellationTokenSource(); }
        int delay = Math.Max(0, _config().ZoneMusic.DecisionDelayMs);
        _ = Task.Delay(delay, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            string? zone, cat;
            lock (_gate) { zone = _pendingZone; cat = _pendingCategory; _pendingZone = null; _pendingCategory = null; _state.Save(); }
            try { await DecideAsync(zone, cat); }
            catch (Exception e) { Log.Warning(e, "zone music decision failed"); }
        }, TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ choosing

    /// <summary>Mode for a location: what the addon last said, else Auto by type (also the game's fixed mode for
    /// that location when we're only looking, e.g. for Resolved.lua).</summary>
    public string ModeFor(string? zoneKey, string type, string? explicitMode = null)
    {
        if (Modes.Parse(explicitMode) is { } m) return m;
        lock (_gate) if (_mode != null) return _mode;
        if (zoneKey != null && _game().LocationModes.TryGetValue(zoneKey, out var fixedMode)) return fixedMode;
        return Modes.Auto(type);
    }

    /// <summary>Works out the playlist for a zone in a mode without playing it (also used by the Zone playlists window).</summary>
    public async Task<ZoneChoice> ChooseAsync(string? zoneKey, string? category, bool resolveSearch = true,
        bool includeGame = true, string? mode = null)
    {
        var cfg = _config();
        var game = includeGame ? _game() : GameOverrides.Empty;
        string? cat = category ?? (zoneKey != null ? Seen.GetValueOrDefault(zoneKey)?.Category : null);
        string type = zoneKey != null ? TypeOf(zoneKey, cat) : ZoneCatalog.TypeFromCategory(cat);
        mode = ModeFor(zoneKey, type, mode);
        string vibe = _catalog.VibeFor(zoneKey, cat);
        string vibeName = _catalog.VibeName(vibe, mode);
        string zoneName = zoneKey != null ? NameOf(zoneKey) : vibeName;
        bool globalShuffle = game.Shuffle;
        string m = mode;
        ZoneChoice Make(string? uri, string label, string source, bool shuffle = false) =>
            new(zoneKey, zoneName, vibe, vibeName, uri, label, source) { Shuffle = shuffle || globalShuffle, Mode = m };

        // 1. in-game location  2. companion zone  3. in-game "every <type>"  4. companion vibe  5. default
        if (zoneKey != null && game.Locations.TryGetValue(zoneKey, out var gl) && gl.For(mode) is { } glp)
            return Make(glp.Uri, glp.Name, "your in-game playlist", glp.Shuffle);
        if (zoneKey != null && cfg.ZoneOverride(zoneKey, mode) is { } zo)
        {
            if (SpotifyUri.IsOff(zo)) return Make(null, "(off)", "zone override: off");
            return Make(MusicUri.Normalize(zo), Labeled(zo), "your zone playlist");
        }
        if (game.Types.TryGetValue(type, out var gt) && gt.For(mode) is { } gtp)
            return Make(gtp.Uri, gtp.Name, $"your in-game playlist for every {type}", gtp.Shuffle);
        if (cfg.VibeOverride(vibe, mode) is { } vo)
        {
            if (SpotifyUri.IsOff(vo)) return Make(null, "(off)", "vibe override: off");
            return Make(MusicUri.Normalize(vo), Labeled(vo), $"your '{vibeName}' playlist");
        }

        // 5. defaults: Spotify (pinned or search) when Spotify is in use, else the vibe's free radio station
        var source = DefaultSource;
        if (source == "off") return Make(null, "(no default: zoneMusic.defaultSource is off)", "default off");
        var mood = _catalog.Vibes.GetValueOrDefault(vibe)?.For(mode);
        ZoneChoice Radio(string why) => _catalog.RadioFor(vibe, mode) is { } st
            ? Make(MusicUri.RadioPrefix + st.Url, st.Name, why)
            : Make(null, "(no radio station for this vibe)", why);
        if (source == "radio") return Radio("default radio");

        if (SpotifyUri.Normalize(mood?.Uri) is { } pinned) return Make(pinned, mood!.Name, "default");
        var cached = CachedDefault(vibe, mode);
        if (cached == null && resolveSearch)
        {
            try { cached = await ResolveDefaultAsync(vibe, mode); }
            catch (Exception e) { Log.Warning(e, "default search failed for {Vibe}/{Mode}", vibe, mode); }
            // Spotify was meant to supply this but couldn't: free radio beats silence (only in auto mode).
            if (cached == null && IsAuto(cfg)) return Radio("default radio (Spotify search found nothing)");
        }
        return cached != null
            ? Make(cached.Uri, cached.Name, $"default (search \"{cached.Query}\")")
            : Make(null, $"search \"{mood?.Search}\" (not resolved yet)", "default");
    }

    private static bool IsAuto(AppConfig cfg) => cfg.ZoneMusic.DefaultSource?.Trim().ToLowerInvariant() is null or "" or "auto";

    private static string Labeled(string raw) => MusicUri.Normalize(raw) is { } u ? (MusicUri.IsLocal(u) ? MusicUri.Label(u) : u) : raw;

    /// <summary>Runs a vibe mood's default search once and caches the result.</summary>
    public async Task<ResolvedPlaylist?> ResolveDefaultAsync(string vibe, string mode, bool refresh = false)
    {
        var mood = _catalog.Vibes.GetValueOrDefault(vibe)?.For(mode);
        if (mood == null || string.IsNullOrWhiteSpace(mood.Search)) return null;
        await _resolveLock.WaitAsync();
        try
        {
            if (!refresh && CachedDefault(vibe, mode) is { } c) return c;
            var hit = await _web.SearchPlaylistAsync(mood.Search);
            if (hit == null) { Log.Warning("no playlist found for {Vibe}/{Mode} search {Query}", vibe, mode, mood.Search); return null; }
            var r = new ResolvedPlaylist { Uri = hit.Uri, Name = hit.Name, Query = mood.Search, ResolvedAt = DateTime.Now };
            lock (_gate) { _state.VibeDefaults[DefaultKey(vibe, mode)] = r; _state.Save(); }
            Log.Information("vibe {Vibe}/{Mode} default → {Name} ({Uri})", vibe, mode, r.Name, r.Uri);
            DefaultsChanged?.Invoke();
            return r;
        }
        finally { _resolveLock.Release(); }
    }

    /// <summary>Looks up every vibe mood default that isn't cached yet (needs the Web API).</summary>
    public async Task ResolveMissingDefaultsAsync()
    {
        if (DefaultSource != "spotify") return;
        foreach (var (vibe, def) in _catalog.Vibes)
            foreach (var mode in Modes.All)
                if (CachedDefault(vibe, mode) == null && SpotifyUri.Normalize(def.For(mode).Uri) == null)
                    await ResolveDefaultAsync(vibe, mode);
    }

    // ------------------------------------------------------------------ playing

    /// <summary>
    /// Plays a playlist. Before switching away from the one we started, remembers its track and position;
    /// when switching back to a playlist with a recent resume point, starts from there.
    /// </summary>
    internal async Task PlayAsync(string uri, bool shuffle)
    {
        bool resume = _game().Resume;
        if (resume) await RememberPositionAsync();
        ResumePoint? rp = null;
        if (resume) lock (_gate) rp = _state.Resume.GetValueOrDefault(uri);
        if (rp != null && DateTime.Now - rp.At >= ResumeMaxAge) rp = null;

        if (MusicUri.IsLocal(uri))
        {
            if (_local == null) throw new InvalidOperationException("Radio and music folders aren't available here.");
            // local music takes over: pause Spotify if it was ours (or is playing anyway)
            if (MusicUri.KindOf(_currentUri) == MusicKind.Spotify || _currentUri == null)
                try { if (_pauseSpotify != null) await _pauseSpotify(); } catch (Exception e) { Log.Debug(e, "pausing Spotify failed"); }
            try { await _local.PlayAsync(uri, shuffle, rp?.TrackUri, rp?.PositionMs ?? 0); }
            catch (Exception e) when (_catalog.BackupFor(uri) is { } backup)
            {
                // A built-in station is down (they're free streams; some move): play its backup instead.
                Log.Warning(e, "radio {Uri} failed; playing its backup {Backup}", uri, backup.Name);
                await _local.PlayAsync(MusicUri.RadioPrefix + backup.Url, shuffle, null, 0);
            }
            _currentUri = uri;
            if (rp != null) Log.Information("resumed {Uri} at {Track} {Pos}s", uri, rp.TrackUri, rp.PositionMs / 1000);
            return;
        }

        if (MusicUri.KindOf(uri) == MusicKind.Spotify && !SpotifyReady)
            throw new SpotifyNotConfiguredException("This playlist is on Spotify: connect Spotify in the companion (tray → Connect Spotify), or pick a radio station or music folder instead.");
        _local?.Stop();   // Spotify takes over from radio/folder music
        if (rp != null)
        {
            try
            {
                await _web.PlayContextAsync(uri, shuffle, rp.TrackUri, rp.PositionMs);
                _currentUri = uri;
                Log.Information("resumed {Uri} at {Track} {Pos}s", uri, rp.TrackUri, rp.PositionMs / 1000);
                return;
            }
            catch (HttpRequestException e)
            {
                // e.g. the track was removed from the playlist: start it normally
                Log.Information("could not resume {Uri} ({Msg}); starting from the top", uri, e.Message);
            }
        }
        await _web.PlayContextAsync(uri, shuffle);
        _currentUri = uri;
    }

    /// <summary>If the playlist we started is still playing, store where it is.</summary>
    private async Task RememberPositionAsync()
    {
        if (_currentUri == null) return;
        if (MusicUri.IsLocal(_currentUri))
        {
            // folders resume by file + position; a radio stream is live, so there's nothing to resume
            if (_local?.Uri == _currentUri && _local.Position is { } p)
                lock (_gate) { _state.Resume[_currentUri] = new ResumePoint { TrackUri = p.Track, PositionMs = p.Ms, At = DateTime.Now }; _state.Save(); }
            return;
        }
        if (!SpotifyReady) return;
        try
        {
            var pb = await _web.GetPlaybackAsync();
            if (pb?.ContextUri == null || pb.TrackUri == null || pb.ContextUri != _currentUri) return;
            lock (_gate)
            {
                _state.Resume[pb.ContextUri] = new ResumePoint { TrackUri = pb.TrackUri, PositionMs = pb.ProgressMs, At = DateTime.Now };
                foreach (var old in _state.Resume.Where(kv => DateTime.Now - kv.Value.At > ResumeMaxAge).Select(kv => kv.Key).ToList())
                    _state.Resume.Remove(old);
                _state.Save();
            }
        }
        catch (Exception e) { Log.Debug(e, "couldn't read playback position"); }
    }

    /// <summary>"Play now" from the game (zone id + mode + PLAY_LOCATION): play that location's playlist immediately.</summary>
    public async Task PlayNowAsync()
    {
        string? key;
        lock (_gate)
        {
            key = (DateTime.Now - _lastZoneAt).TotalSeconds < 3 ? _lastZoneKey : null;
            _decide?.Cancel();
            _pendingZone = null; _pendingCategory = null;
        }
        if (key == null) { Log.Information("PLAY_LOCATION without a recent location id"); return; }
        _currentUri = null;   // play even if it's the same playlist
        await DecideAsync(key, null, force: true);
    }

    /// <summary>The Hype playlist chosen in game, or false if there isn't one (caller falls back to eventMap).</summary>
    public async Task<bool> PlayHypeAsync()
    {
        var h = _game().Hype;
        if (h == null) return false;
        await PlayAsync(h.Uri, h.Shuffle || _game().Shuffle);
        return true;
    }

    /// <summary>Plays a vibe's playlist for a while (level-up fanfare), then goes back to the location playlist.</summary>
    public async Task PlayForAWhileAsync(string vibe, TimeSpan duration)
    {
        var choice = await ChooseVibeAsync(vibe, Modes.Locked);
        string? uri = choice;
        if (uri == null) return;
        var back = Current;
        CancellationTokenSource cts;
        lock (_gate) { _temporary?.Cancel(); cts = _temporary = new CancellationTokenSource(); }
        await PlayAsync(uri, false);
        try { await Task.Delay(duration, cts.Token); } catch (OperationCanceledException) { return; }
        if (back?.Uri != null && _currentUri == uri) await PlayAsync(back.Uri, back.Shuffle);
    }

    /// <summary>A vibe's playlist on its own (no location): companion vibe override, else the default.</summary>
    private async Task<string?> ChooseVibeAsync(string vibe, string mode)
    {
        if (MusicUri.Normalize(_config().VibeOverride(vibe, mode)) is { } o) return o;
        var source = DefaultSource;
        if (source == "off") return null;
        if (source == "spotify")
        {
            var pinned = SpotifyUri.Normalize(_catalog.Vibes.GetValueOrDefault(vibe)?.For(mode).Uri);
            var found = pinned ?? (await ResolveDefaultAsync(vibe, mode))?.Uri;
            if (found != null || !IsAuto(_config())) return found;
        }
        return _catalog.RadioFor(vibe, mode) is { } st ? MusicUri.RadioPrefix + st.Url : null;
    }

    /// <summary>A manual pick (playlist slot): plays it; the next zone change may start its own playlist again.</summary>
    public async Task PlayManualAsync(string uri, bool shuffle = false)
    {
        await PlayAsync(uri, shuffle);
        ForgetCurrent();
    }

    private async Task DecideAsync(string? zoneKey, string? category, bool force = false)
    {
        var cfg = _config();
        if ((!cfg.ZoneMusic.Enabled && !force) || (zoneKey == null && category == null)) return;
        lock (_gate) { _temporary?.Cancel(); _temporary = null; if (category != null) _lastCategory = category; }
        var choice = await ChooseAsync(zoneKey, category ?? _lastCategory);
        if (MusicUri.KindOf(choice.Uri) == MusicKind.Spotify && !SpotifyReady)
        {
            if (!_warnedNoWebApi)
            {
                _warnedNoWebApi = true;
                Notify?.Invoke("A Spotify playlist is set for this location, but Spotify isn't connected (tray → Connect Spotify).", true);
            }
            return;
        }
        Current = choice;
        Changed?.Invoke(choice);
        Log.Information("zone {Zone} ({Key}) {Mode} vibe {Vibe} → {Playlist} [{Source}]",
            choice.ZoneName, choice.ZoneKey, choice.Mode, choice.Vibe, choice.PlaylistLabel, choice.Source);
        if (choice.Uri == null) return;
        if (choice.Uri == _currentUri) return; // same playlist already playing: don't restart it
        try { await PlayAsync(choice.Uri, choice.Shuffle); }
        catch (SpotifyNotConfiguredException e) { Notify?.Invoke(e.Message, true); }
        catch (Exception e)
        {
            Log.Warning(e, "could not start zone playlist {Uri}", choice.Uri);
            Notify?.Invoke($"Couldn't play the {choice.ZoneName} playlist: {e.Message}", true);
        }
    }

    /// <summary>Forget the last started playlist (e.g. after the user picked music manually).</summary>
    public void ForgetCurrent() => _currentUri = null;
}
