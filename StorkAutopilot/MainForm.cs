using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace StorkAutopilot;

public sealed class MainForm : Form
{
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    const byte VK_LEFT = 0x25, VK_RIGHT = 0x27;
    const uint KEYUP = 0x0002;

    readonly Button pick = new() { Text = "① 게임 화면 선택", Width = 145 };
    readonly Button calibrate = new() { Text = "② 중립 자세 보정", Width = 145, Enabled = false };
    readonly Button start = new() { Text = "③ 자동 조작 시작", Width = 145, Enabled = false };
    readonly Button stop = new() { Text = "긴급 정지", Width = 110, Enabled = false };
    readonly Label regionLabel = new() { Text = "선택 안 됨", AutoSize = true };
    readonly Label status = new() { Text = "대기 중", AutoSize = true, Font = new Font("맑은 고딕", 10, FontStyle.Bold) };
    readonly Label metrics = new() { Text = "기울기: --   입력: 없음", AutoSize = true };
    readonly TrackBar sensitivity = new() { Minimum = 5, Maximum = 25, Value = 10, TickFrequency = 5, Width = 220 };
    readonly TrackBar threshold = new() { Minimum = 4, Maximum = 25, Value = 10, TickFrequency = 3, Width = 220 };
    readonly ComboBox forwardKey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    readonly ComboBox backwardKey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    Rectangle captureRect;
    double? baseline;
    DateTime lastPulse = DateTime.MinValue;
    DateTime startAfter;
    bool running;
    string? heldKey;

    public MainForm()
    {
        Text = "Stork Autopilot";
        ClientSize = new System.Drawing.Size(480, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("맑은 고딕", 9);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 10 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        var title = new Label { Text = "황새 오래 걷기 자동 균형 보정", Font = new Font("맑은 고딕", 14, FontStyle.Bold), AutoSize = true };
        layout.Controls.Add(title); layout.SetColumnSpan(title, 2);
        layout.Controls.Add(pick); layout.Controls.Add(regionLabel);
        layout.Controls.Add(new Label { Text = "보정 민감도", AutoSize = true }); layout.Controls.Add(sensitivity);
        layout.Controls.Add(new Label { Text = "반응 임계값", AutoSize = true }); layout.Controls.Add(threshold);
        layout.Controls.Add(new Label { Text = "앞으로 기울면 누를 키", AutoSize = true });
        forwardKey.Items.AddRange(new object[] { "LEFT", "RIGHT" }); forwardKey.SelectedIndex = 0; layout.Controls.Add(forwardKey);
        layout.Controls.Add(new Label { Text = "뒤로 기울면 누를 키", AutoSize = true });
        backwardKey.Items.AddRange(new object[] { "LEFT", "RIGHT" }); backwardKey.SelectedIndex = 1; layout.Controls.Add(backwardKey);
        layout.Controls.Add(calibrate); layout.Controls.Add(start);
        layout.Controls.Add(stop); layout.Controls.Add(status);
        layout.Controls.Add(metrics); layout.SetColumnSpan(metrics, 2);
        var note = new Label {
            Text = "사용법: 게임 화면 선택 → 중립 자세 보정 → 자동 조작 시작. 시작 후 7초 안에 게임 화면을 클릭하세요. ESC로 정지합니다.",
            AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Color.DarkGoldenrod
        };
        layout.Controls.Add(note); layout.SetColumnSpan(note, 2);

        pick.Click += (_, _) => PickRegion();
        calibrate.Click += (_, _) => Calibrate();
        start.Click += (_, _) => StartPilot();
        stop.Click += (_, _) => StopPilot();
        timer.Tick += (_, _) => TickPilot();
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) StopPilot(); };
        FormClosing += (_, _) => StopPilot();
    }

    void PickRegion()
    {
        StopPilot();
        using var selector = new RegionSelector();
        if (selector.ShowDialog(this) == DialogResult.OK && selector.Selected.Width > 100 && selector.Selected.Height > 100)
        {
            captureRect = selector.Selected;
            regionLabel.Text = $"{captureRect.Width} × {captureRect.Height}";
            baseline = null;
            calibrate.Enabled = start.Enabled = true;
            status.Text = "영역 선택 완료. 황새가 똑바로 선 상태에서 보정하세요.";
        }
    }

    Bitmap Capture()
    {
        var bmp = new Bitmap(captureRect.Width, captureRect.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(captureRect.Location, Point.Empty, captureRect.Size);
        return bmp;
    }

    (double Lean, System.Drawing.Point Head, double FootX)? Detect(Bitmap bitmap)
    {
        using var mat = BitmapConverter.ToMat(bitmap);
        using var gray = new Mat();
        using var blur = new Mat();
        Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, blur, new OpenCvSharp.Size(9, 9), 2);
        var h = gray.Rows; var w = gray.Cols;
        var circles = Cv2.HoughCircles(blur, HoughModes.Gradient, 1.2, Math.Max(25, h * .06),
            90, 24, Math.Max(10, (int)(h * .025)), Math.Max(22, (int)(h * .075)));
        double bestScore = double.NegativeInfinity;
        System.Drawing.Point head = System.Drawing.Point.Empty;
        foreach (var c in circles)
        {
            if (c.Y < h * .20 || c.Y > h * .82 || c.Radius < 10 || c.Radius > h * .055) continue;
            using var maskInner = new Mat(h, w, MatType.CV_8UC1, Scalar.Black);
            using var maskRing = new Mat(h, w, MatType.CV_8UC1, Scalar.Black);
            Cv2.Circle(maskInner, new OpenCvSharp.Point((int)c.X, (int)c.Y), Math.Max(2, (int)(c.Radius * .48)), Scalar.White, -1);
            Cv2.Circle(maskRing, new OpenCvSharp.Point((int)c.X, (int)c.Y), Math.Max(3, (int)(c.Radius * 1.05)), Scalar.White, -1);
            Cv2.Circle(maskRing, new OpenCvSharp.Point((int)c.X, (int)c.Y), Math.Max(2, (int)(c.Radius * .75)), Scalar.Black, -1);
            var innerMean = Cv2.Mean(gray, maskInner).Val0;
            var ringMean = Cv2.Mean(gray, maskRing).Val0;
            using var dark = new Mat();
            Cv2.Threshold(gray, dark, 75, 255, ThresholdTypes.BinaryInv);
            using var eyeMask = new Mat();
            Cv2.BitwiseAnd(dark, maskInner, eyeMask);
            var eyeFraction = Cv2.CountNonZero(eyeMask) / (double)Math.Max(1, Cv2.CountNonZero(maskInner));
            var contrast = innerMean - ringMean;
            if (eyeFraction < .003 || eyeFraction > .085 || contrast < 35) continue;
            var score = contrast + Math.Min(eyeFraction, .06) * 250;
            if (score > bestScore) { bestScore = score; head = new System.Drawing.Point((int)c.X, (int)c.Y); }
        }
        if (bestScore == double.NegativeInfinity) return null;

        int y0 = (int)(h * .86), y1 = (int)(h * .99);
        var footXs = new System.Collections.Generic.List<int>();
        using var darkPixels = new Mat();
        Cv2.Threshold(gray.RowRange(y0, y1), darkPixels, 75, 255, ThresholdTypes.BinaryInv);
        for (int y = 0; y < darkPixels.Rows; y++)
            for (int x = 0; x < darkPixels.Cols; x++)
                if (darkPixels.At<byte>(y, x) != 0 && Math.Abs(x - head.X) < w * .28) footXs.Add(x);
        if (footXs.Count < 5)
        {
            footXs.Clear();
            for (int y = 0; y < darkPixels.Rows; y++)
                for (int x = 0; x < darkPixels.Cols; x++)
                    if (darkPixels.At<byte>(y, x) != 0) footXs.Add(x);
        }
        if (footXs.Count < 5) return null;
        footXs.Sort();
        double footX = footXs[footXs.Count / 2];
        double lean = (head.X - footX) / Math.Max(1.0, h * .32);
        return (lean, head, footX);
    }

    void Calibrate()
    {
        try
        {
            using var bmp = Capture();
            var result = Detect(bmp);
            if (result is null) { MessageBox.Show("황새 머리 인식에 실패했습니다. 게임 영역을 다시 선택해 주세요."); return; }
            baseline = result.Value.Lean;
            status.Text = $"중립 자세 보정 완료 ({baseline:+0.000;-0.000;0.000})";
            metrics.Text = "기울기: 0.000   입력: 없음";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "보정 오류"); }
    }

    void StartPilot()
    {
        if (baseline is null) { MessageBox.Show("먼저 중립 자세 보정을 해주세요."); return; }
        running = true;
        startAfter = DateTime.Now.AddSeconds(7);
        start.Enabled = false; stop.Enabled = true;
        status.Text = "7초 안에 브라우저 게임을 클릭하세요.";
        timer.Start();
    }

    void TickPilot()
    {
        if (!running || DateTime.Now < startAfter) return;
        try
        {
            using var bmp = Capture();
            var result = Detect(bmp);
            if (result is null) { status.Text = "황새 인식 실패"; ReleaseKeys(); return; }
            double error = (result.Value.Lean - baseline!.Value) * (sensitivity.Value / 10.0);
            double dead = threshold.Value / 100.0;
            string? key = error > dead ? (string)forwardKey.SelectedItem! :
                          error < -dead ? (string)backwardKey.SelectedItem! : null;
            if (key != null && (DateTime.Now - lastPulse).TotalMilliseconds > 115)
            {
                ReleaseKeys();
                byte vk = key == "LEFT" ? VK_LEFT : VK_RIGHT;
                keybd_event(vk, 0, 0, UIntPtr.Zero);
                keybd_event(vk, 0, KEYUP, UIntPtr.Zero);
                heldKey = key;
                lastPulse = DateTime.Now;
            }
            metrics.Text = $"기울기: {error:+0.000;-0.000;0.000}   입력: {key ?? "없음"}";
            status.Text = "자동 조작 중";
        }
        catch (Exception ex) { status.Text = "오류: " + ex.Message; ReleaseKeys(); }
    }

    void ReleaseKeys()
    {
        keybd_event(VK_LEFT, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_RIGHT, 0, KEYUP, UIntPtr.Zero);
        heldKey = null;
    }

    void StopPilot()
    {
        running = false;
        timer.Stop();
        ReleaseKeys();
        if (!IsDisposed)
        {
            start.Enabled = captureRect.Width > 0 && baseline is not null;
            stop.Enabled = false;
            status.Text = "정지됨";
        }
    }
}

sealed class RegionSelector : Form
{
    System.Drawing.Point start;
    Rectangle selection;
    public Rectangle Selected { get; private set; }

    public RegionSelector()
    {
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        TopMost = true;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
        Opacity = .28;
        MouseDown += (_, e) => { start = e.Location; selection = Rectangle.Empty; };
        MouseMove += (_, e) => { if (e.Button == MouseButtons.Left) { selection = MakeRect(start, e.Location); Invalidate(); } };
        MouseUp += (_, _) =>
        {
            if (selection.Width > 100 && selection.Height > 100)
            {
                Selected = new Rectangle(selection.X + Bounds.X, selection.Y + Bounds.Y, selection.Width, selection.Height);
                DialogResult = DialogResult.OK; Close();
            }
        };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        Paint += (_, e) =>
        {
            using var pen = new Pen(Color.Lime, 3);
            e.Graphics.DrawRectangle(pen, selection);
            e.Graphics.DrawString("드래그하여 게임 화면 전체를 선택하세요 (ESC 취소)", Font, Brushes.White, 20, 20);
        };
    }
    static Rectangle MakeRect(System.Drawing.Point a, System.Drawing.Point b) =>
        new(Math.Min(a.X,b.X), Math.Min(a.Y,b.Y), Math.Abs(a.X-b.X), Math.Abs(a.Y-b.Y));
}
