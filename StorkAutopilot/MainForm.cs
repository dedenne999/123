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
