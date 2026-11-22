using AudioVibes.Decoder;

namespace AudioVibes.App;

/// <summary>Live Goertzel bin levels + noise floor, decoded/rejected log, and a 60 s WAV recorder.</summary>
public sealed class DebugForm : Form
{
    private readonly ProtocolSpec _p;
    private readonly LevelsPanel _levels;
    private readonly ListBox _log;
    private readonly Label _status;

    public DebugForm(ProtocolSpec p, Action record)
    {
        _p = p;
        Text = "WoW Audio Vibes – debug";
        Width = 760; Height = 560;
        Font = new Font("Segoe UI", 9);

        _status = new Label { Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
        _levels = new LevelsPanel(p) { Dock = DockStyle.Top, Height = 260 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(4) };
        var rec = new Button { Text = "Record 60 s of WoW audio to WAV", AutoSize = true };
        rec.Click += (_, _) => record();
        var clear = new Button { Text = "Clear log", AutoSize = true };
        buttons.Controls.Add(rec);
        buttons.Controls.Add(clear);
        _log = new ListBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 9), IntegralHeight = false };
        clear.Click += (_, _) => _log.Items.Clear();

        Controls.Add(_log);
        Controls.Add(buttons);
        Controls.Add(_levels);
        Controls.Add(_status);
    }

    public void SetStatus(string s) => _status.Text = s;

    public void AddLog(string line)
    {
        _log.Items.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {line}");
        while (_log.Items.Count > 500) _log.Items.RemoveAt(_log.Items.Count - 1);
    }

    /// <summary>Called on the audio thread; the panel keeps the latest/peak values and repaints on a timer.</summary>
    public void PushLevels(HopLevels l) => _levels.Push(l);

    private sealed class LevelsPanel : Panel
    {
        private readonly string[] _labels;
        private readonly double[] _cur = new double[N];
        private readonly double[] _peak = new double[N];
        private double _floor = -130;
        private bool _hit;
        private readonly object _gate = new();
        private readonly System.Windows.Forms.Timer _timer;
        private const double MinDb = -140, MaxDb = -20;
        private const int N = FrameDecoder.LevelCount;

        public LevelsPanel(ProtocolSpec p)
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(24, 24, 28);
            _labels = FrameDecoder.LabelsFor(p);
            Array.Fill(_cur, MinDb); Array.Fill(_peak, MinDb);
            _timer = new System.Windows.Forms.Timer { Interval = 50 };
            _timer.Tick += (_, _) =>
            {
                lock (_gate) for (int i = 0; i < N; i++) _peak[i] = Math.Max(_peak[i] - 1.5, _cur[i]);
                Invalidate();
            };
            _timer.Start();
        }

        public void Push(HopLevels l)
        {
            lock (_gate)
            {
                for (int i = 0; i < N; i++) { _cur[i] = l.LevelsDb[i]; _peak[i] = Math.Max(_peak[i], l.LevelsDb[i]); }
                _floor = l.NoiseFloorDb;
                _hit |= l.PreambleHit;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            double[] cur, peak; double floor; bool hit;
            lock (_gate) { cur = (double[])_cur.Clone(); peak = (double[])_peak.Clone(); floor = _floor; hit = _hit; _hit = false; }
            int left = 46, top = 10, bottom = Height - 36, w = Width - left - 10;
            float Y(double db) => (float)(bottom - (Math.Clamp(db, MinDb, MaxDb) - MinDb) / (MaxDb - MinDb) * (bottom - top));
            using var grid = new Pen(Color.FromArgb(50, 255, 255, 255));
            using var txt = new SolidBrush(Color.Gainsboro);
            using var small = new Font("Segoe UI", 7.5f);
            for (double db = MinDb; db <= MaxDb; db += 20)
            {
                g.DrawLine(grid, left, Y(db), left + w, Y(db));
                g.DrawString($"{db:0}", small, txt, 4, Y(db) - 7);
            }
            float bw = w / (float)N;
            for (int i = 0; i < N; i++)
            {
                var color = i < 8 ? Color.DeepSkyBlue : i < 12 ? (hit ? Color.LimeGreen : (i < 10 ? Color.Orange : Color.MediumOrchid)) : Color.Gray;
                using var b = new SolidBrush(color);
                float x = left + i * bw + 4;
                g.FillRectangle(b, x, Y(cur[i]), bw - 8, bottom - Y(cur[i]));
                using var pk = new Pen(Color.White, 2);
                g.DrawLine(pk, x, Y(peak[i]), x + bw - 8, Y(peak[i]));
                g.DrawString(_labels[i], small, txt, x, bottom + 2);
            }
            using var fl = new Pen(Color.Red, 1.5f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawLine(fl, left, Y(floor), left + w, Y(floor));
            g.DrawString($"noise floor {floor:0.0} dB", small, Brushes.Red, left + 4, Y(floor) - 14);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
