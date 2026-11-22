using System.Text;

namespace AudioVibes.Decoder;

/// <summary>Minimal WAV reader/writer (mono float32 out; reads PCM16/PCM24/float32, any channels → mono).</summary>
public static class WavFile
{
    public static void WriteMonoFloat(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        int dataBytes = samples.Length * 4;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16);
        w.Write((short)3); w.Write((short)1); w.Write(sampleRate); w.Write(sampleRate * 4);
        w.Write((short)4); w.Write((short)32);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
    }

    public static (float[] Samples, int SampleRate) ReadMono(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") throw new InvalidDataException("not RIFF");
        r.ReadInt32();
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") throw new InvalidDataException("not WAVE");
        int fmt = 0, ch = 0, rate = 0, bits = 0;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            string id = Encoding.ASCII.GetString(r.ReadBytes(4));
            int len = r.ReadInt32();
            if (id == "fmt ")
            {
                fmt = r.ReadInt16(); ch = r.ReadInt16(); rate = r.ReadInt32();
                r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                if (len > 16)
                {
                    var ext = r.ReadBytes(len - 16);
                    if (fmt == unchecked((short)0xFFFE) || fmt == 0xFFFE) fmt = BitConverter.ToInt16(ext, 8); // subformat GUID first 2 bytes
                }
            }
            else if (id == "data")
            {
                int bps = bits / 8;
                int frames = len / (bps * ch);
                var outp = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    double acc = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        acc += (fmt, bits) switch
                        {
                            (3, 32) => r.ReadSingle(),
                            (1, 16) => r.ReadInt16() / 32768.0,
                            (1, 24) => ReadInt24(r) / 8388608.0,
                            (1, 32) => r.ReadInt32() / 2147483648.0,
                            _ => throw new NotSupportedException($"wav format {fmt}/{bits}"),
                        };
                    }
                    outp[i] = (float)(acc / ch);
                }
                return (outp, rate);
            }
            else r.BaseStream.Seek(len + (len & 1), SeekOrigin.Current);
        }
        throw new InvalidDataException("no data chunk");
    }

    private static int ReadInt24(BinaryReader r)
    {
        int b0 = r.ReadByte(), b1 = r.ReadByte(), b2 = (sbyte)r.ReadByte();
        return b0 | (b1 << 8) | (b2 << 16);
    }
}
