// 단축키로 PC를 오간 뒤 어디로 왔는지 잠깐 보여 준다(1.4초).
// 커서가 있는 모니터 둘레에 색 테두리를 켜고, 그 모니터 위쪽 가운데에 "<PC 이름>로 넘어옴"을 띄우고, 커서에서 고리 두 개가 퍼져 나간다.
// 모든 창은 클릭이 아래로 지나가고 포커스를 가져가지 않는다. 반투명 가장자리를 깨끗하게 그리려고 UpdateLayeredWindow를 쓴다.
// 모니터 전체를 덮는 큰 창 하나 대신 테두리 네 줄·이름표·물결을 작은 창으로 따로 띄운다(큰 화면에서 매 프레임 그리는 부담을 피한다).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Locator
{
    const int Ms = 1400;
    static readonly Color Accent = Color.FromArgb(235, 60, 60);   // 빨강(2026-11-10 바위님 지시: 눈에 잘 띄게)
    static readonly Timer timer = new Timer { Interval = 15 };
    static readonly List<LayerWin> edges = new List<LayerWin>();
    static LayerWin pill, ripple;
    static float s;
    static int t0;
    static bool hooked;

    // 화면 스레드에서 부른다.
    public static void Flash(Point p, string name)
    {
        try { Start(p, string.IsNullOrEmpty(name) ? "?" : name); }
        catch (Exception e) { Log.Write("위치 표시 실패: " + e.Message); }
    }

    static void Start(Point p, string name)
    {
        if (!hooked) { timer.Tick += delegate { Frame(); }; hooked = true; }
        var mon = Screen.FromPoint(p).Bounds;
        s = Math.Max(1f, mon.Height / 1080f);

        // 테두리: 모니터 안쪽 가장자리 네 줄
        int th = (int)Math.Round(18 * s);   // 2026-11-10 바위님 지시: 18px, 100%
        var rects = new[]
        {
            new Rectangle(mon.Left, mon.Top, mon.Width, th),
            new Rectangle(mon.Left, mon.Bottom - th, mon.Width, th),
            new Rectangle(mon.Left, mon.Top + th, th, mon.Height - 2 * th),
            new Rectangle(mon.Right - th, mon.Top + th, th, mon.Height - 2 * th),
        };
        while (edges.Count < 4) edges.Add(new LayerWin());
        for (int i = 0; i < 4; i++)
        {
            var b = new Bitmap(rects[i].Width, rects[i].Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b)) g.Clear(Accent);
            edges[i].Set(b, rects[i].Location);
        }

        // 이름표: 모니터 위쪽 가운데
        if (pill == null) pill = new LayerWin();
        pill.Set(Pill(name + Josa(name) + " 넘어옴"), Point.Empty);
        pill.Origin = new Point(mon.Left + (mon.Width - pill.Bmp.Width) / 2, mon.Top + th + (int)(18 * s));

        // 물결: 커서 둘레
        if (ripple == null) ripple = new LayerWin();
        int box = (int)(300 * s);   // 2026-11-10 바위님 지시: 퍼지는 너비 2배
        ripple.Set(new Bitmap(box, box, PixelFormat.Format32bppArgb), new Point(p.X - box / 2, p.Y - box / 2));

        t0 = Environment.TickCount;
        Frame();
        timer.Start();
    }

    static void Frame()
    {
        int ms = unchecked(Environment.TickCount - t0);
        if (ms >= Ms)
        {
            timer.Stop();
            foreach (var e in edges) e.Hide();
            pill.Hide();
            ripple.Hide();
            return;
        }
        // 테두리·이름표: 0.2초에 걸쳐 켜지고, 1초까지 그대로 있다가, 끝까지 서서히 사라진다
        float a = ms < 200 ? ms / 200f : ms < 1000 ? 1f : 1 - (ms - 1000) / 400f;
        foreach (var e in edges) e.Blit((byte)(255 * a));
        pill.Blit((byte)(255 * a));

        // 물결: 고리 두 개가 0.25초 간격으로 퍼진다
        var bmp = ripple.Bmp;
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float c = bmp.Width / 2f;
            for (int i = 0; i < 2; i++)
            {
                float k = (ms - i * 250) / 1000f;
                if (k <= 0 || k >= 1) continue;
                float e = 1 - (1 - k) * (1 - k);              // 처음에 빠르고 끝에 느리다
                float r = (6 + (c - 10 * s - 6) * e);
                // 2026-11-10 바위님 지시: 고리 12px, 퍼지는 동안 흐려지는 정도는 절반
                int al = (int)(255 * (1 - k * 0.5f));
                using (var shade = new Pen(Color.FromArgb(al * 3 / 5, 0, 0, 0), 16 * s)) g.DrawEllipse(shade, c - r, c - r, 2 * r, 2 * r);
                using (var pen = new Pen(Color.FromArgb(al, Accent), 12 * s)) g.DrawEllipse(pen, c - r, c - r, 2 * r, 2 * r);
            }
        }
        ripple.Blit(255);
    }

    static Bitmap Pill(string text)
    {
        using (var font = new Font("Malgun Gothic", 17 * s, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            SizeF ts;
            using (var tmp = new Bitmap(1, 1)) using (var g0 = Graphics.FromImage(tmp)) ts = g0.MeasureString(text, font);
            float padX = 18 * s, padY = 8 * s, m = 2 * s;
            var b = new Bitmap((int)(ts.Width + 2 * padX + 2 * m), (int)(ts.Height + 2 * padY + 2 * m), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var box = new RectangleF(m, m, b.Width - 2 * m, b.Height - 2 * m);
                using (var path = Rounded(box, box.Height / 2))
                using (var fill = new SolidBrush(Color.FromArgb(240, 16, 17, 20)))
                using (var edge = new Pen(Accent, 1.5f * s))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(edge, path);
                }
                using (var tb = new SolidBrush(Color.White))
                    g.DrawString(text, font, tb, box.X + (box.Width - ts.Width) / 2, box.Y + (box.Height - ts.Height) / 2);
            }
            return b;
        }
    }

    // 이름이 한글로 끝나면 받침을 보고 "로"/"으로"를 고른다(ㄹ 받침은 "로"). 영문·숫자는 "로"로 둔다.
    static string Josa(string name)
    {
        char c = name[name.Length - 1];
        if (c < 0xAC00 || c > 0xD7A3) return "로";
        int jong = (c - 0xAC00) % 28;
        return jong == 0 || jong == 8 ? "로" : "으로";
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// 그림 한 장을 화면 좌표 Origin에 투명도 alpha로 띄우는 창.
class LayerWin : Form
{
    public Bitmap Bmp;
    public Point Origin;

    public LayerWin()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8;   // LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE | TOPMOST
            return p;
        }
    }

    public void Set(Bitmap b, Point origin)
    {
        if (Bmp != null) Bmp.Dispose();
        Bmp = b;
        Origin = origin;
    }

    public void Blit(byte alpha)
    {
        if (!Visible) Show();
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hb = Bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(mem, hb);
            var sz = new SIZE { cx = Bmp.Width, cy = Bmp.Height };
            var src = new POINT();
            var dst = new POINT { x = Origin.X, y = Origin.Y };
            var bf = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };   // AC_SRC_ALPHA
            UpdateLayeredWindow(Handle, screen, ref dst, ref sz, mem, ref src, 0, ref bf, 2);                         // ULW_ALPHA
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);                               // 맨 위로, NOSIZE | NOMOVE | NOACTIVATE
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (hb != IntPtr.Zero) DeleteObject(hb);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLENDFUNCTION bf, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
