using System.Diagnostics;
using AudioVibes.Decoder;

namespace AudioVibes.Audio;

public enum SourceState { WaitingForWow, Attached, Error }

/// <summary>Polls for the WoW process every 3 s, attaches process-loopback capture when it appears,
/// and detaches/retries when it exits. Also hosts the WAV fixture recorder.</summary>
public sealed class WowAudioSource : IDisposable
{
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private ProcessLoopbackCapture? _capture;
    private Process? _proc;
    private IReadOnlyList<string> _names;
    private readonly int _sampleRate;

    private List<float>? _recording;
    private int _recordTarget;
    private string? _recordPath;

    public SourceState State { get; private set; } = SourceState.WaitingForWow;
    /// <summary>Folder of the attached game's exe (e.g. ...\World of Warcraft\_forever_), when known.</summary>
    public string? ClientDir { get; private set; }
    public string Status { get; private set; } = "waiting for WoW";
    public event Action<float[]>? SamplesAvailable;
    public event Action<SourceState, string>? StateChanged;
    public event Action<string>? RecordingFinished;

    public WowAudioSource(IEnumerable<string> processNames, int sampleRate = 48000)
    {
        _names = processNames.ToList();
        _sampleRate = sampleRate;
        _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(0, 3000);

    public void SetProcessNames(IEnumerable<string> names) { lock (_gate) _names = names.ToList(); }

    private void SetState(SourceState s, string status)
    {
        State = s; Status = status;
        StateChanged?.Invoke(s, status);
    }

    private void Poll()
    {
        lock (_gate)
        {
            if (_proc != null)
            {
                bool exited;
                try { exited = _proc.HasExited; } catch { exited = true; }
                if (!exited && _capture != null) return;
                Detach();
                SetState(SourceState.WaitingForWow, "WoW exited; waiting");
            }
            if (!ProcessLoopbackCapture.IsSupported)
            {
                SetState(SourceState.Error, ProcessLoopbackCapture.SupportMessage);
                return;
            }
            foreach (var n in _names)
            {
                var name = Path.GetFileNameWithoutExtension(n);
                var p = Process.GetProcessesByName(name).FirstOrDefault();
                if (p == null) continue;
                try
                {
                    var cap = new ProcessLoopbackCapture(p.Id, _sampleRate);
                    cap.SamplesAvailable += OnSamples;
                    cap.Faulted += e => { lock (_gate) { Detach(); SetState(SourceState.Error, "capture failed: " + e.Message); } };
                    cap.Start();
                    _capture = cap; _proc = p;
                    try { ClientDir = Path.GetDirectoryName(p.MainModule?.FileName); } catch { ClientDir = null; }
                    SetState(SourceState.Attached, $"attached to {name}.exe (pid {p.Id}, {cap.FormatDescription})");
                }
                catch (Exception e)
                {
                    SetState(SourceState.Error, $"attach to {name}.exe failed: {e.Message}");
                }
                return;
            }
            if (State != SourceState.WaitingForWow) SetState(SourceState.WaitingForWow, "waiting for WoW");
        }
    }

    private void Detach()
    {
        _capture?.Dispose();
        _capture = null;
        _proc = null;
    }

    private void OnSamples(float[] block)
    {
        SamplesAvailable?.Invoke(block);
        List<float>? rec;
        lock (_gate) rec = _recording;
        if (rec == null) return;
        rec.AddRange(block);
        if (rec.Count >= _recordTarget)
        {
            string path;
            lock (_gate) { path = _recordPath!; _recording = null; }
            WavFile.WriteMonoFloat(path, rec.GetRange(0, _recordTarget).ToArray(), _sampleRate);
            RecordingFinished?.Invoke(path);
        }
    }

    /// <summary>Record the next N seconds of captured WoW audio (mono 48 kHz float WAV) for test fixtures.</summary>
    public void Record(string path, TimeSpan duration)
    {
        lock (_gate)
        {
            _recordPath = path;
            _recordTarget = (int)(duration.TotalSeconds * _sampleRate);
            _recording = new List<float>(_recordTarget + _sampleRate);
        }
    }

    public bool IsRecording { get { lock (_gate) return _recording != null; } }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate) Detach();
    }
}
