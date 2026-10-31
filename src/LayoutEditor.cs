// 모니터 배치 창. 윈도우 설정 > 디스플레이처럼 메인 PC 모니터를 상자로 그리고, 서브 PC 모니터 상자를 끌어다 붙인다.
// 상자는 메인 PC 모니터나 다른 상자와 겹치지 않으며, 가장자리 근처에서 자석처럼 달라붙는다.
// 저장하면 config.json의 서브 PC 항목(zone)에 쓰고, "자동으로 되돌리기"는 그 값을 지워 자동 자리로 돌아간다.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Windows.Forms;

class LayoutEditor : Form
{
    class Box { public Machine M; public Rectangle R; }

    class Canvas : Panel
    {
        public Canvas() { DoubleBuffered = true; ResizeRedraw = true; }
    }

    static readonly Color BgForm = ColorTranslator.FromHtml("#202020"), BgCanvas = ColorTranslator.FromHtml("#191919");
    static readonly Color MonFill = ColorTranslator.FromHtml("#2d2d2d"), MonLine = ColorTranslator.FromHtml("#3a3a3a");
    static readonly Color Accent = Color.FromArgb(217, 0, 120, 212), Warn = ColorTranslator.FromHtml("#ffb74d");

    readonly Config cfg;
    readonly Action onSaved;
    readonly Rectangle[] mons;
    readonly List<Box> boxes = new List<Box>();
    readonly Canvas canvas = new Canvas();
    readonly Label status = new Label();
    readonly Button btnSave = new Button(), btnReset = new Button(), btnCancel = new Button();
    Box sel;
    float scale = 0.1f;
    PointF origin;              // 메인 PC 좌표 (0,0)이 캔버스 어디에 놓이는지
    bool dragging;
    Point dragStart;            // 끌기 시작한 마우스 위치(화면 px)
    Rectangle dragRect;         // 끌기 시작할 때 상자 자리

    public LayoutEditor(Config cfg, Action onSaved)
    {
        this.cfg = cfg;
        this.onSaved = onSaved;
        mons = Config.Monitors();

        Text = "연동 - 모니터 배치";
        ClientSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgForm;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9f);
        DoubleBuffered = true;

        var done = new List<Rectangle>();
        foreach (var m in cfg.Subs)
        {
            Rectangle zone = Config.ZoneOf(m, mons, done);
            if (!zone.IsEmpty) done.Add(zone);
            Size sz = m.Screen != null && m.Screen.Length == 2 && m.Screen[0] > 0 && m.Screen[1] > 0
                ? new Size(m.Screen[0], m.Screen[1]) : zone.IsEmpty ? new Size(1920, 1080) : zone.Size;
            boxes.Add(new Box { M = m, R = zone.IsEmpty ? Rectangle.Empty : new Rectangle(zone.Location, sz) });
        }
        foreach (var b in boxes) if (b.R.IsEmpty) PlaceFree(b);
        if (boxes.Count > 0) sel = boxes[0];

        canvas.Dock = DockStyle.Fill;
        canvas.BackColor = BgCanvas;
        canvas.Paint += OnPaintCanvas;
        canvas.Resize += delegate { Fit(); };
        canvas.MouseDown += OnDown;
        canvas.MouseMove += OnMove;
        canvas.MouseUp += OnUp;

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = BgForm };
        var btns = new Panel { Dock = DockStyle.Right, Width = 400, BackColor = BgForm };
        SetupButton(btnSave, "저장", 12, 100);
        SetupButton(btnReset, "자동으로 되돌리기", 120, 170);
        SetupButton(btnCancel, "취소", 298, 90);
        btnSave.BackColor = Color.FromArgb(0, 120, 212);
        btnSave.Click += delegate { Save(); };
        btnReset.Click += delegate { Reset(); };
        btnCancel.Click += delegate { Close(); };
        btns.Controls.AddRange(new Control[] { btnSave, btnReset, btnCancel });
        status.Dock = DockStyle.Fill;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.Padding = new Padding(12, 0, 0, 0);
        bottom.Controls.Add(status);
        bottom.Controls.Add(btns);
        Controls.Add(canvas);
        Controls.Add(bottom);
        AcceptButton = null;
        if (boxes.Count == 0) { btnSave.Enabled = false; btnReset.Enabled = false; }
        Fit();
        UpdateStatus();
    }

    static void SetupButton(Button b, string text, int x, int w)
    {
        b.Text = text;
        b.SetBounds(x, 10, w, 36);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = MonLine;
        b.BackColor = ColorTranslator.FromHtml("#2d2d2d");
        b.ForeColor = Color.White;
        b.UseVisualStyleBackColor = false;
    }

    // 자리를 못 정한 상자: 가장 아래 모니터 밑, 가장 왼쪽 모니터에 맞춰 놓고 겹치면 오른쪽으로 민다.
    void PlaceFree(Box b)
    {
        var size = b.R.IsEmpty ? new Size(1920, 1080) : b.R.Size;
        int x = mons.Length > 0 ? mons.Min(r => r.Left) : 0, y = mons.Length > 0 ? mons.Max(r => r.Bottom) : 0;
        var r2 = new Rectangle(x, y, size.Width, size.Height);
        for (int i = 0; i < 20 && !Valid(r2, b); i++) r2.X += size.Width;
        b.R = r2;
    }

    // 모니터·다른 상자와 겹치지 않는지(맞닿는 것은 괜찮다)
    bool Valid(Rectangle r, Box self)
    {
        foreach (var m in mons) if (r.IntersectsWith(m)) return false;
        foreach (var o in boxes) if (o != self && !o.R.IsEmpty && r.IntersectsWith(o.R)) return false;
        return true;
    }

    // 캔버스 안에 모니터와 상자가 모두 들어오게 배율을 다시 잡는다(15% 여백).
    void Fit()
    {
        var all = new List<Rectangle>(mons);
        foreach (var b in boxes) if (!b.R.IsEmpty) all.Add(b.R);
        if (all.Count == 0 || canvas.Width < 10 || canvas.Height < 10) return;
        int l = all.Min(r => r.Left), t = all.Min(r => r.Top), rr = all.Max(r => r.Right), bb = all.Max(r => r.Bottom);
        float w = (rr - l) * 1.3f, h = (bb - t) * 1.3f;
        scale = Math.Min(canvas.Width / w, canvas.Height / h);
        origin = new PointF((canvas.Width - (rr - l) * scale) / 2 - l * scale, (canvas.Height - (bb - t) * scale) / 2 - t * scale);
        canvas.Invalidate();
    }

    RectangleF ToScr(Rectangle r)
    {
        return new RectangleF(origin.X + r.X * scale, origin.Y + r.Y * scale, r.Width * scale, r.Height * scale);
    }

    void OnPaintCanvas(object s, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        if (boxes.Count == 0)
        {
            using (var f = new Font("Segoe UI", 12f))
                g.DrawString("짝 맺은 서브 PC가 없습니다", f, Brushes.Gray, canvas.ClientRectangle,
                    new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far });
        }
        var screens = Screen.AllScreens;
        for (int i = 0; i < mons.Length; i++)
            DrawBox(g, ToScr(mons[i]), MonFill, MonLine, 1, false, (i + 1).ToString(), mons[i].Size,
                    i < screens.Length && screens[i].Primary);
        foreach (var b in boxes)
            DrawBox(g, ToScr(b.R), Accent, b == sel ? Color.White : Color.Transparent, b == sel ? 2 : 0, true, b.M.Name, b.R.Size, false);
    }

    void DrawBox(Graphics g, RectangleF r, Color fill, Color line, float lw, bool sub, string title, Size px, bool primary)
    {
        using (var br = new SolidBrush(fill)) g.FillRectangle(br, r);
        if (lw > 0) using (var p = new Pen(line, lw)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        float fs = Math.Max(9f, Math.Min(sub ? 16f : 24f, r.Height / 5f));
        using (var f1 = new Font("Segoe UI", fs, FontStyle.Bold))
        using (var f2 = new Font("Segoe UI", 9f))
        {
            var top = new RectangleF(r.X, r.Y, r.Width, r.Height * 0.6f);
            var low = new RectangleF(r.X, r.Y + r.Height * 0.55f, r.Width, r.Height * 0.3f);
            g.DrawString(title, f1, Brushes.White, top, sf);
            g.DrawString(px.Width + " x " + px.Height + (primary ? " (주)" : ""), f2, sub ? Brushes.White : Brushes.Silver, low, sf);
        }
    }

    Box HitTest(Point p)
    {
        for (int i = boxes.Count - 1; i >= 0; i--) if (ToScr(boxes[i].R).Contains(p)) return boxes[i];
        return null;
    }

    void OnDown(object s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var hit = HitTest(e.Location);
        if (hit != null) { sel = hit; dragging = true; dragStart = e.Location; dragRect = hit.R; }
        canvas.Invalidate();
        UpdateStatus();
    }

    void OnMove(object s, MouseEventArgs e)
    {
        if (!dragging || sel == null) { canvas.Cursor = HitTest(e.Location) != null ? Cursors.SizeAll : Cursors.Default; return; }
        var want = dragRect;
        want.X += (int)Math.Round((e.X - dragStart.X) / scale);
        want.Y += (int)Math.Round((e.Y - dragStart.Y) / scale);
        var snapped = Snap(want);
        if (Valid(snapped, sel)) { sel.R = snapped; canvas.Invalidate(); UpdateStatus(); }   // 겹치면 마지막 자리를 유지
    }

    void OnUp(object s, MouseEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        Fit();
    }

    // 모니터 가장자리 근처에서 붙이고, 붙은 변이면 위·아래/왼·오른쪽 끝도 맞춘다.
    Rectangle Snap(Rectangle r)
    {
        int S = Math.Max(40, (int)(12 / scale));
        foreach (var m in mons)
        {
            bool nearX = r.Left < m.Right + S && r.Right > m.Left - S, nearY = r.Top < m.Bottom + S && r.Bottom > m.Top - S;
            if (nearY)
            {
                if (Math.Abs(r.Left - m.Right) <= S) r.X = m.Right;
                else if (Math.Abs(r.Right - m.Left) <= S) r.X = m.Left - r.Width;
            }
            if (nearX)
            {
                if (Math.Abs(r.Top - m.Bottom) <= S) r.Y = m.Bottom;
                else if (Math.Abs(r.Bottom - m.Top) <= S) r.Y = m.Top - r.Height;
            }
            if (r.Right == m.Left || r.Left == m.Right)   // 옆에 붙었으면 위아래 끝을 맞춘다
            {
                if (Math.Abs(r.Top - m.Top) <= S) r.Y = m.Top;
                else if (Math.Abs(r.Bottom - m.Bottom) <= S) r.Y = m.Bottom - r.Height;
            }
            if (r.Top == m.Bottom || r.Bottom == m.Top)   // 위아래에 붙었으면 좌우 끝을 맞춘다
            {
                if (Math.Abs(r.Left - m.Left) <= S) r.X = m.Left;
                else if (Math.Abs(r.Right - m.Right) <= S) r.X = m.Right - r.Width;
            }
        }
        return r;
    }

    // 화살표로 1px(Shift면 10px) 옮긴다. 겹치면 움직이지 않는다.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys k = keyData & Keys.KeyCode;
        if (sel != null && !dragging && (k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down))
        {
            int d = (keyData & Keys.Shift) != 0 ? 10 : 1;
            var r = sel.R;
            if (k == Keys.Left) r.X -= d; else if (k == Keys.Right) r.X += d;
            else if (k == Keys.Up) r.Y -= d; else r.Y += d;
            if (Valid(r, sel)) { sel.R = r; Fit(); UpdateStatus(); }
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // 고른 상자가 어느 모니터의 어느 쪽과 맞닿았는지 보여 준다.
    void UpdateStatus()
    {
        if (sel == null) { status.Text = ""; return; }
        var r = sel.R;
        var parts = new List<string>();
        for (int i = 0; i < mons.Length; i++)
        {
            var m = mons[i];
            bool vOv = r.Top < m.Bottom && r.Bottom > m.Top, hOv = r.Left < m.Right && r.Right > m.Left;
            string side = null;
            if (r.Left == m.Right && vOv) side = "오른쪽";
            else if (r.Right == m.Left && vOv) side = "왼쪽";
            else if (r.Top == m.Bottom && hOv) side = "아래";
            else if (r.Bottom == m.Top && hOv) side = "위";
            if (side != null) parts.Add((i + 1) + "번 모니터 " + side);
        }
        bool ok = parts.Count > 0;
        status.ForeColor = ok ? Color.White : Warn;
        status.Text = ok ? sel.M.Name + ": " + string.Join(", ", parts.ToArray()) + "과 맞닿음"
                         : sel.M.Name + ": 어느 모니터와도 맞닿지 않음 — 이대로면 넘어갈 수 없습니다";
    }

    void Save()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var b in boxes)
            {
                b.M.ZoneRect = new[] { b.R.X, b.R.Y, b.R.Width, b.R.Height };
                sb.Append(b.M.Name + "=" + b.R.X + "," + b.R.Y + "," + b.R.Width + "," + b.R.Height + " ");
            }
            cfg.Save();
            Log.Write("배치 저장: " + sb.ToString().TrimEnd());
            if (onSaved != null) onSaved();
            Close();
        }
        catch (Exception ex) { MessageBox.Show(this, "저장하지 못했습니다: " + ex.Message, Text); }
    }

    void Reset()
    {
        try
        {
            foreach (var b in boxes) b.M.ZoneRect = null;
            cfg.Save();
            Log.Write("배치 초기화: 자동 자리로 되돌림");
            if (onSaved != null) onSaved();
            Close();
        }
        catch (Exception ex) { MessageBox.Show(this, "되돌리지 못했습니다: " + ex.Message, Text); }
    }
}
