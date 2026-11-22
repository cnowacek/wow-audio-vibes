namespace AudioVibes.Decoder;

public static class Goertzel
{
    /// <summary>Amplitude of <paramref name="freq"/> in <paramref name="x"/>[offset..offset+n),
    /// normalised so a full-scale sine reads 1.0. <paramref name="window"/> may be null (rectangular).</summary>
    public static double Amplitude(ReadOnlySpan<float> x, double freq, int sampleRate, float[]? window, double windowSum)
    {
        double w = 2.0 * Math.PI * freq / sampleRate;
        double coeff = 2.0 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        if (window is null)
        {
            for (int i = 0; i < x.Length; i++)
            {
                double s0 = x[i] + coeff * s1 - s2;
                s2 = s1; s1 = s0;
            }
        }
        else
        {
            for (int i = 0; i < x.Length; i++)
            {
                double s0 = x[i] * window[i] + coeff * s1 - s2;
                s2 = s1; s1 = s0;
            }
        }
        // |X|^2 for non-integer bins: use the exact final-step form.
        double re = s1 - s2 * Math.Cos(w);
        double im = s2 * Math.Sin(w);
        double mag = Math.Sqrt(re * re + im * im);
        return 2.0 * mag / windowSum;
    }

    public static float[] Hann(int n)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++) w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
        return w;
    }

    public static double ToDb(double amp) => 20.0 * Math.Log10(Math.Max(amp, 1e-15));
}
