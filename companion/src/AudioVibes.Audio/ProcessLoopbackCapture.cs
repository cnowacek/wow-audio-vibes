using System.Runtime.InteropServices;

namespace AudioVibes.Audio;

/// <summary>
/// Captures only one process tree's audio (WASAPI process loopback, Windows 10 20348+ / Windows 11).
/// Raises <see cref="SamplesAvailable"/> with mono float blocks at <see cref="SampleRate"/>.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    public const int MinBuild = 20348;
    public int SampleRate { get; }
    public int ProcessId { get; }
    public string FormatDescription { get; private set; } = "";

    public event Action<float[]>? SamplesAvailable;
    public event Action<Exception>? Faulted;

    private Thread? _thread;
    private volatile bool _stop;

    public ProcessLoopbackCapture(int processId, int sampleRate = 48000)
    {
        ProcessId = processId;
        SampleRate = sampleRate;
    }

    public static bool IsSupported => Environment.OSVersion.Version.Build >= MinBuild;

    public static string SupportMessage =>
        $"Process loopback capture needs Windows 10 build {MinBuild}+ or Windows 11 (this is build {Environment.OSVersion.Version.Build}).";

    /// <summary>Starts capture. Throws if activation/initialisation fails.</summary>
    public void Start()
    {
        if (!IsSupported) throw new PlatformNotSupportedException(SupportMessage);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = $"Audio Vibes capture {ProcessId}" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        ready.Task.GetAwaiter().GetResult();
    }

    private static IAudioClient Activate(int pid)
    {
        IntPtr pParams = Marshal.AllocHGlobal(12);
        IntPtr pVariant = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(pParams, 0, Native.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK);
            Marshal.WriteInt32(pParams, 4, pid);
            Marshal.WriteInt32(pParams, 8, Native.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE);
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(pVariant, i, 0);
            Marshal.WriteInt16(pVariant, 0, (short)Native.VT_BLOB);   // PROPVARIANT.vt
            Marshal.WriteInt32(pVariant, 8, 12);                       // blob.cbSize
            Marshal.WriteIntPtr(pVariant, 16, pParams);                // blob.pBlobData (x64 layout)

            var handler = new ActivationHandler();
            Native.ActivateAudioInterfaceAsync(Native.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                Native.IID_IAudioClient, pVariant, handler, out _);
            if (!handler.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("ActivateAudioInterfaceAsync did not complete");
            return handler.Task.Result;
        }
        finally
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(pParams);
        }
    }

    private (IAudioClient client, int channels, bool isFloat) Initialize(IntPtr evt)
    {
        // GetMixFormat isn't supported for process loopback: pass an explicit format.
        (ushort tag, ushort bits, string name)[] formats =
        [
            (Native.WAVE_FORMAT_IEEE_FLOAT, 32, "32-bit float"),
            (Native.WAVE_FORMAT_PCM, 16, "16-bit PCM"),
        ];
        Exception? last = null;
        foreach (var f in formats)
        {
            var client = Activate(ProcessId); // a failed Initialize poisons the client; re-activate each try
            IntPtr fmt = Native.AllocWaveFormat(f.tag, 2, SampleRate, f.bits);
            try
            {
                int hr = client.Initialize(Native.AUDCLNT_SHAREMODE_SHARED,
                    Native.AUDCLNT_STREAMFLAGS_LOOPBACK | Native.AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                    Native.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | Native.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                    200_000 /* 20 ms */, 0, fmt, IntPtr.Zero);
                if (hr < 0) { last = Marshal.GetExceptionForHR(hr); Marshal.ReleaseComObject(client); continue; }
                Marshal.ThrowExceptionForHR(client.SetEventHandle(evt));
                FormatDescription = $"{SampleRate} Hz stereo {f.name}";
                return (client, 2, f.tag == Native.WAVE_FORMAT_IEEE_FLOAT);
            }
            finally { Marshal.FreeHGlobal(fmt); }
        }
        throw new InvalidOperationException("IAudioClient.Initialize failed for float and PCM16", last);
    }

    private void Run(TaskCompletionSource ready)
    {
        using var evt = new AutoResetEvent(false);
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        try
        {
            (client, int channels, bool isFloat) = Initialize(evt.SafeWaitHandle.DangerousGetHandle());
            Marshal.ThrowExceptionForHR(client.GetService(Native.IID_IAudioCaptureClient, out object svc));
            capture = (IAudioCaptureClient)svc;
            Marshal.ThrowExceptionForHR(client.Start());
            ready.TrySetResult();

            while (!_stop)
            {
                evt.WaitOne(100);
                while (!_stop)
                {
                    Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out uint packet));
                    if (packet == 0) break;
                    Marshal.ThrowExceptionForHR(capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _));
                    var mono = new float[frames];
                    if ((flags & Native.AUDCLNT_BUFFERFLAGS_SILENT) == 0) Downmix(data, frames, channels, isFloat, mono);
                    capture.ReleaseBuffer(frames);
                    if (frames > 0) SamplesAvailable?.Invoke(mono);
                }
            }
            client.Stop();
        }
        catch (Exception e)
        {
            if (!ready.TrySetException(e)) Faulted?.Invoke(e);
        }
        finally
        {
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
        }
    }

    private static unsafe void Downmix(IntPtr data, uint frames, int channels, bool isFloat, float[] mono)
    {
        if (isFloat)
        {
            float* p = (float*)data;
            for (int i = 0; i < frames; i++)
            {
                float acc = 0;
                for (int c = 0; c < channels; c++) acc += p[i * channels + c];
                mono[i] = acc / channels;
            }
        }
        else
        {
            short* p = (short*)data;
            for (int i = 0; i < frames; i++)
            {
                int acc = 0;
                for (int c = 0; c < channels; c++) acc += p[i * channels + c];
                mono[i] = acc / (32768f * channels);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(1000);
    }
}
