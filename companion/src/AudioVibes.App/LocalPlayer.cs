using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;

namespace AudioVibes.App;

/// <summary>Plays music the companion handles itself: internet radio streams and folders of audio files.</summary>
public interface ILocalPlayer
{
    /// <summary>Something is loaded (playing or paused): transport keys go here instead of Spotify.</summary>
    bool IsActive { get; }
    string? Uri { get; }
    /// <summary>Current file name (folders), or null (radio).</summary>
    string? NowPlaying { get; }
    /// <summary>Current file and position, for resuming a folder later (null for radio).</summary>
    (string Track, int Ms)? Position { get; }
    Task PlayAsync(string uri, bool shuffle, string? resumeTrack = null, int resumeMs = 0);
    void Stop();
    bool TogglePause();
    bool Pause();
    bool Next();
    bool Previous();
    void StepVolume(float delta);
    Task DuckAsync(float level);
    Task UnduckAsync();
    event Action<string>? Error;
    event Action<string>? TrackChanged;
}

/// <summary>
/// NAudio-based player. One decode thread per track fills a few seconds of buffer (so a slow radio stream
/// or disk never stalls the audio device); the output mixes it at 48 kHz stereo with volume and ducking.
/// MP3, AAC/M4A, WMA, WAV and FLAC go through Windows Media Foundation, Ogg Vorbis through NVorbis.
/// Radio stream URLs (and .pls/.m3u playlist links) play the same way, without a buffer limit on length.
/// </summary>
public sealed class LocalPlayer : ILocalPlayer, IDisposable
{
    public static readonly string[] AudioExtensions = [".mp3", ".m4a", ".aac", ".flac", ".wav", ".wma", ".ogg"];
    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };

    private readonly object _gate = new();
    private readonly string? _settingsPath;
    private readonly Output _output;
    private WasapiOut? _device;
    private Track? _track;
    private string? _uri;
    private List<string> _files = [];
    private int _index;
    private bool _shuffle, _paused;
    private int _failures, _radioRetries;
    private CancellationTokenSource? _duckRamp;

    public event Action<string>? Error;
    public event Action<string>? TrackChanged;

    public LocalPlayer(string? settingsPath = null)
    {
        _settingsPath = settingsPath;
        _output = new Output(this);
        _output.Volume = LoadVolume();
    }

    public bool IsActive { get { lock (_gate) return _uri != null; } }
    public string? Uri { get { lock (_gate) return _uri; } }
    public string? NowPlaying { get { lock (_gate) return _track?.IsFile == true ? Path.GetFileNameWithoutExtension(_track.Source) : null; } }

    public (string Track, int Ms)? Position
    {
        get
        {
            lock (_gate)
            {
                if (_track is not { IsFile: true } t) return null;
                var pos = t.Reader.CurrentTime - t.Buffer.BufferedDuration;
                return (t.Source, (int)Math.Max(0, pos.TotalMilliseconds));
            }
        }
    }

    // ------------------------------------------------------------------ starting

    public async Task PlayAsync(string uri, bool shuffle, string? resumeTrack = null, int resumeMs = 0)
    {
        var kind = MusicUri.KindOf(uri);
        if (kind is not (MusicKind.Radio or MusicKind.Folder)) throw new ArgumentException("not a radio or folder uri", nameof(uri));
        var target = MusicUri.Target(uri);
        if (kind == MusicKind.Folder)
        {
            var files = await Task.Run(() => ListFolder(target));
            if (files.Count == 0) throw new InvalidOperationException($"No music files in {target}");
            int start = 0;
            if (shuffle) Shuffle(files);
            if (resumeTrack != null && files.FindIndex(f => string.Equals(f, resumeTrack, StringComparison.OrdinalIgnoreCase)) is >= 0 and var i)
                start = i;
            else resumeMs = 0;
            lock (_gate) { StopLocked(); _uri = uri; _files = files; _index = start; _shuffle = shuffle; _paused = false; _failures = 0; }
            await StartFileAsync(start, TimeSpan.FromMilliseconds(resumeMs));
        }
        else
        {
            var url = await ResolveStreamUrlAsync(target);
            lock (_gate) { StopLocked(); _uri = uri; _files = [url]; _index = 0; _paused = false; _radioRetries = 0; }
            await StartTrackAsync(url, isFile: false, TimeSpan.Zero);
        }
    }

    private async Task StartFileAsync(int index, TimeSpan from)
    {
        string file;
        lock (_gate) { if (_files.Count == 0) return; _index = (index % _files.Count + _files.Count) % _files.Count; file = _files[_index]; }
        await StartTrackAsync(file, isFile: true, from);
    }

    private async Task StartTrackAsync(string source, bool isFile, TimeSpan from)
    {
        Track track;
        try { track = await Task.Run(() => Track.Open(source, isFile, from)); }
        catch (Exception e)
        {
            Log.Warning(e, "can't play {Source}", source);
            if (isFile && Interlocked.Increment(ref _failures) < Math.Max(1, _files.Count)) { _ = Task.Run(() => AdvanceAsync(+1)); return; }
            throw new InvalidOperationException($"Can't play {(isFile ? Path.GetFileName(source) : source)}: {e.Message}", e);
        }
        lock (_gate)
        {
            if (_uri == null) { track.Dispose(); return; }   // stopped while opening
            _track?.Dispose();
            _track = track;
            if (isFile) _failures = 0;
            EnsureDevice();
        }
        Log.Information("local player: {Source}", source);
        TrackChanged?.Invoke(isFile ? Path.GetFileNameWithoutExtension(source) : source);
    }

    private void EnsureDevice()
    {
        if (_device != null) return;
        _device = new WasapiOut(AudioClientShareMode.Shared, 200);
        _device.PlaybackStopped += (_, e) => { if (e.Exception != null) Log.Warning(e.Exception, "audio output stopped"); };
        _device.Init(new SampleToWaveProvider(_output));
        _device.Play();
    }

    /// <summary>Called from the output when the current track has played out.</summary>
    private void OnTrackEnded(Track ended)
    {
        bool isFile;
        lock (_gate) { if (_track != ended || _uri == null) return; isFile = ended.IsFile; }
        if (isFile) { _ = AdvanceAsync(+1); return; }
        // A radio stream dropped: reconnect a few times before giving up.
        _ = Task.Run(async () =>
        {
            string? url; int attempt;
            lock (_gate) { url = _files.FirstOrDefault(); attempt = ++_radioRetries; }
            if (url == null) return;
            if (attempt > 3) { Error?.Invoke("The radio stream stopped and didn't come back."); Stop(); return; }
            Log.Information("radio stream ended; reconnecting ({Attempt}/3)", attempt);
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            try { await StartTrackAsync(url, false, TimeSpan.Zero); }
            catch (Exception e) { Log.Warning(e, "radio reconnect failed"); }
        });
    }

    private async Task AdvanceAsync(int step)
    {
        int next;
        lock (_gate)
        {
            if (_uri == null || _files.Count == 0) return;
            next = _index + step;
            if (next >= _files.Count && _shuffle) { Shuffle(_files); next = 0; }   // new order for each pass
        }
        try { await StartFileAsync(next, TimeSpan.Zero); }
        catch (Exception e) { Error?.Invoke(e.Message); Stop(); }
    }

    // ------------------------------------------------------------------ transport

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        _track?.Dispose(); _track = null;
        _uri = null; _files = []; _paused = false;
        if (_device != null) { try { _device.Stop(); _device.Dispose(); } catch { } _device = null; }
    }

    public bool TogglePause()
    {
        bool paused;
        lock (_gate) { if (_uri == null) return false; paused = _paused; }
        return paused ? Resume() : Pause();
    }

    public bool Pause()
    {
        lock (_gate)
        {
            if (_uri == null || _paused) return false;
            _paused = true;
            // A paused radio stream would fall behind (and the server may drop us): disconnect, rejoin live on play.
            if (_track is { IsFile: false }) { _track.Dispose(); _track = null; }
            return true;
        }
    }

    private bool Resume()
    {
        string? url = null;
        lock (_gate)
        {
            if (_uri == null) return false;
            _paused = false;
            if (_track == null && MusicUri.KindOf(_uri) == MusicKind.Radio) url = _files.FirstOrDefault();
        }
        if (url != null) _ = Task.Run(async () => { try { await StartTrackAsync(url, false, TimeSpan.Zero); } catch (Exception e) { Error?.Invoke(e.Message); } });
        return true;
    }

    public bool Next()
    {
        lock (_gate) if (_track is not { IsFile: true }) return false;
        _ = AdvanceAsync(+1);
        return true;
    }

    /// <summary>Back to the start of the track, or the previous one if we're within 3 s of the start.</summary>
    public bool Previous()
    {
        lock (_gate) if (_track is not { IsFile: true }) return false;
        var pos = Position;
        _ = pos is { Ms: > 3000 } ? StartFileAsync(_index, TimeSpan.Zero) : AdvanceAsync(-1);
        return true;
    }

    // ------------------------------------------------------------------ volume

    public float Volume => _output.Volume;

    public void StepVolume(float delta)
    {
        _output.Volume = Math.Clamp(_output.Volume + delta, 0f, 1f);
        SaveVolume(_output.Volume);
    }

    public Task DuckAsync(float level) => RampDuckAsync(Math.Clamp(level, 0f, 1f));
    public Task UnduckAsync() => RampDuckAsync(1f);

    private async Task RampDuckAsync(float to)
    {
        CancellationTokenSource cts;
        lock (_gate) { _duckRamp?.Cancel(); cts = _duckRamp = new CancellationTokenSource(); }
        float from = _output.Duck;
        const int steps = 15;
        try
        {
            for (int i = 1; i <= steps; i++)
            {
                _output.Duck = from + (to - from) * i / steps;
                await Task.Delay(20, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private float LoadVolume()
    {
        try
        {
            if (_settingsPath != null && File.Exists(_settingsPath)
                && JsonDocument.Parse(File.ReadAllText(_settingsPath)).RootElement.TryGetProperty("volume", out var v))
                return Math.Clamp(v.GetSingle(), 0f, 1f);
        }
        catch (Exception e) { Log.Debug(e, "player settings unreadable"); }
        return 0.6f;
    }

    private void SaveVolume(float v)
    {
        if (_settingsPath == null) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!); File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new { volume = v })); }
        catch (Exception e) { Log.Debug(e, "couldn't save player settings"); }
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------ helpers (public for tests)

    /// <summary>Audio files in a folder and its subfolders, in path order.</summary>
    public static List<string> ListFolder(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Folder not found: {folder}");
        return Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Shuffle(List<string> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>.pls / .m3u links are small text files pointing at the real stream: use its first entry.</summary>
    /// <summary>
    /// The address Media Foundation should open: follows redirects (MF gives up on some), and for .pls/.m3u
    /// playlists (by extension or content type) the first stream listed in them.
    /// </summary>
    public static async Task<string> ResolveStreamUrlAsync(string url, int depth = 0)
    {
        // Follow redirects by hand: .NET won't follow https → http ones, which some broadcasters use.
        HttpResponseMessage resp;
        var final = url;
        for (int hop = 0; ; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, final);
            req.Headers.UserAgent.ParseAdd("WoWAudioVibes/1.0");
            resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc && hop < 5)
            {
                final = (loc.IsAbsoluteUri ? loc : new System.Uri(new System.Uri(final), loc)).ToString();
                resp.Dispose();
                continue;
            }
            break;
        }
        using var _ = resp;
        resp.EnsureSuccessStatusCode();
        var type = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        var path = System.Uri.TryCreate(final, UriKind.Absolute, out var u) ? u.AbsolutePath.ToLowerInvariant() : "";
        bool playlist = path.EndsWith(".pls") || path.EndsWith(".m3u") || type is "audio/x-scpls" or "audio/x-mpegurl" or "audio/mpegurl";
        if (!playlist) return final;
        if (depth > 2) throw new InvalidOperationException("Too many nested playlists at " + url);
        var text = await resp.Content.ReadAsStringAsync();
        var inner = FirstStreamInPlaylist(text) ?? throw new InvalidOperationException("No stream address in " + url);
        return await ResolveStreamUrlAsync(inner, depth + 1);
    }

    public static string? FirstStreamInPlaylist(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("File", StringComparison.OrdinalIgnoreCase) && line.IndexOf('=') is > 0 and var eq) line = line[(eq + 1)..].Trim();
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return line;
        }
        return null;
    }

    /// <summary>
    /// Decodes a radio/folder source for a while without opening an audio device: returns the peak level and
    /// how much audio arrived. Used by the tests (and handy to check a stream URL).
    /// </summary>
    public static async Task<(float Peak, TimeSpan Decoded)> ProbeAsync(string uri, TimeSpan duration)
    {
        string source = MusicUri.KindOf(uri) switch
        {
            MusicKind.Radio => await ResolveStreamUrlAsync(MusicUri.Target(uri)),
            MusicKind.Folder => ListFolder(MusicUri.Target(uri)).FirstOrDefault() ?? throw new InvalidOperationException("no music files"),
            _ => throw new ArgumentException("not a radio or folder uri"),
        };
        var t = await Task.Run(() => Track.Open(source, MusicUri.KindOf(uri) == MusicKind.Folder, TimeSpan.Zero));
        try
        {
            var deadline = DateTime.UtcNow + duration + TimeSpan.FromSeconds(10);
            while (t.Buffer.BufferedDuration < duration && !t.Eof && DateTime.UtcNow < deadline) await Task.Delay(50);
            var got = t.Buffer.BufferedDuration;
            var bytes = new byte[t.Buffer.BufferedBytes];
            int n = t.Buffer.Read(bytes, 0, bytes.Length) / 4;
            float peak = 0;
            for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(bytes, i * 4)));
            return (peak, got);
        }
        finally { t.Close(TimeSpan.FromSeconds(5)); }
    }

    // ------------------------------------------------------------------ one track being decoded

    private sealed class Track : IDisposable
    {
        public required WaveStream Reader { get; init; }
        public required BufferedWaveProvider Buffer { get; init; }
        public required string Source { get; init; }
        public required bool IsFile { get; init; }
        public volatile bool Eof;
        public int EndedSignalled;
        private readonly CancellationTokenSource _cts = new();

        public static Track Open(string source, bool isFile, TimeSpan from)
        {
            WaveStream reader = isFile && source.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                ? new VorbisWaveReader(source)
                : new MediaFoundationReader(source, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
            try
            {
                if (from > TimeSpan.Zero && from < reader.TotalTime) reader.CurrentTime = from;
                ISampleProvider sp = reader.ToSampleProvider();
                if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
                else if (sp.WaveFormat.Channels != 2) throw new NotSupportedException($"{sp.WaveFormat.Channels}-channel audio isn't supported");
                if (sp.WaveFormat.SampleRate != Format.SampleRate) sp = new WdlResamplingSampleProvider(sp, Format.SampleRate);
                var t = new Track
                {
                    Reader = reader, Source = source, IsFile = isFile,
                    Buffer = new BufferedWaveProvider(Format) { BufferDuration = TimeSpan.FromSeconds(8), ReadFully = false, DiscardOnBufferOverflow = true },
                };
                t.StartDecoding(sp);
                return t;
            }
            catch { reader.Dispose(); throw; }
        }

        private void StartDecoding(ISampleProvider sp)
        {
            var ct = _cts.Token;
            var th = new Thread(() =>
            {
                var f = new float[Format.SampleRate / 10 * 2];   // 100 ms
                var b = new byte[f.Length * 4];
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (Buffer.BufferedDuration > TimeSpan.FromSeconds(4)) { Thread.Sleep(40); continue; }
                        int n = sp.Read(f, 0, f.Length);
                        if (n <= 0) break;
                        System.Buffer.BlockCopy(f, 0, b, 0, n * 4);
                        Buffer.AddSamples(b, 0, n * 4);
                    }
                }
                catch (Exception e) when (!ct.IsCancellationRequested) { Log.Information("decoding stopped: {Msg}", e.Message); }
                catch { }
                Eof = true;
                // The reader is only ever touched by this thread once decoding starts, so it closes it too.
                try { Reader.Dispose(); } catch { }
            }) { IsBackground = true, Name = "AudioVibes decode" };
            _thread = th;
            th.Start();
        }

        private Thread? _thread;

        /// <summary>Stops decoding; the decode thread closes the file/stream as it exits.</summary>
        public void Dispose() => _cts.Cancel();

        /// <summary>Stops decoding and waits until the file/stream is closed.</summary>
        public void Close(TimeSpan timeout)
        {
            _cts.Cancel();
            _thread?.Join(timeout);
        }
    }

    // ------------------------------------------------------------------ the output stream

    private sealed class Output(LocalPlayer player) : ISampleProvider
    {
        public WaveFormat WaveFormat => Format;
        public volatile float Volume = 0.6f;
        public volatile float Duck = 1f;
        private float _gain;
        private readonly byte[] _bytes = new byte[Format.AverageBytesPerSecond];

        public int Read(float[] buffer, int offset, int count)
        {
            Track? t;
            bool paused;
            lock (player._gate) { t = player._track; paused = player._paused; }
            int n = 0;
            if (t != null && !paused)
            {
                int want = Math.Min(count * 4, _bytes.Length);
                int got = t.Buffer.Read(_bytes, 0, want) / 4;
                System.Buffer.BlockCopy(_bytes, 0, buffer, offset * 4, got * 4);
                n = got;
                if (n == 0 && t.Eof && Interlocked.Exchange(ref t.EndedSignalled, 1) == 0)
                    ThreadPool.QueueUserWorkItem(_ => player.OnTrackEnded(t));
            }
            // Silence for the rest. Not Array.Clear: NAudio's SampleToWaveProvider passes the device's byte[]
            // disguised as a float[] (WaveBuffer), and Array.Clear goes by the real element size, so it cleared
            // only a quarter of the block and the rest replayed old audio: a loud buzz whenever music paused.
            for (int i = offset + n, end = offset + count; i < end; i++) buffer[i] = 0f;
            // ramp the gain over the block so volume/duck changes don't click
            float target = Volume * Duck, g = _gain, step = count > 0 ? (target - g) / count : 0;
            for (int i = 0; i < n; i++) { g += step; buffer[offset + i] *= g; }
            _gain = target;
            return count;
        }
    }
}
