// 단축키로 PC를 오간 뒤 커서가 어디 있는지 잠깐 보여 준다. 커서 둘레에서 줄어드는 고리와 그 PC 이름을 1초쯤 띄운다.
// 클릭은 아래 창으로 그대로 지나가고 포커스도 가져가지 않는다. 반투명 가장자리를 깨끗하게 그리려고 UpdateLayeredWindow를 쓴다.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class Locator : Form
{
    static Locator one;

    // 화면 스레드에서 부른다.
    public static void Flash(Point at, string name)
    {
        try
        {
            if (one == null || one.IsDisposed) one = new Locator();
            one.Start(at, name);
        }
        catch (Exception e) { Log.Write("위치 표시 실패: " + e.Message); }
    }

    const int Ms = 1000;
    static readonly Color Accent = Color.FromArgb(94, 106, 210);
    readonly Timer timer = new Timer { Interval = 15 };
    Point at;
    string name;
    int t0;
    float s;          // 화면 크기에 맞춘 배율
    Size size;
    Point origin;     // 창 왼쪽 위(화면 좌표)
    Point center;     // 창 안에서 커서 자리
    Font font;

    Locator()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        timer.Tick += delegate { Frame(); };
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

    void Start(Point p, string n)
    {
        at = p;
        name = string.IsNullOrEmpty(n) ? "?" : n;
        s = Math.Max(1f, Screen.FromPoint(p).Bounds.Height / 1080f);
        if (font != null) font.Dispose();
        font = new Font("Malgun Gothic", 17 * s, FontStyle.Bold, GraphicsUnit.Pixel);
        Size text = TextRenderer.MeasureText(name, font);
        int r0 = (int)(72 * s), pad = (int)(8 * s);
        int w = Math.Max(2 * r0, text.Width + (int)(28 * s)) + 2 * pad;
        int h = 2 * r0 + 2 * pad;
        size = new Size(w, h);
        origin = new Point(p.X - w / 2, p.Y - h / 2);
        center = new Point(w / 2, h / 2);
        t0 = Environment.TickCount;
        if (!Visible) Show();
        SetWindowPos(Handle, new IntPtr(-1), origin.X, origin.Y, w, h, 0x0010 | 0x0040);   // 맨 위, NOACTIVATE | SHOWWINDOW
        Frame();
        timer.Start();
    }

    void Frame()
    {
        float t = unchecked(Environment.TickCount - t0) / (float)Ms;
        if (t >= 1f) { timer.Stop(); Hide(); return; }
        float k = Math.Min(1f, t / 0.45f);
        k = 1 - (1 - k) * (1 - k) * (1 - k);                       // 처음에 빠르게, 끝에 천천히 줄어든다
        float r = (72 - (72 - 24) * k) * s;
        byte alpha = (byte)(t < 0.65f ? 255 : 255 * (1 - (t - 0.65f) / 0.35f));

        using (var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var ring = new RectangleF(center.X - r, center.Y - r, 2 * r, 2 * r);
                using (var shade = new Pen(Color.FromArgb(110, 0, 0, 0), 8 * s)) g.DrawEllipse(shade, ring);
                using (var pen = new Pen(Accent, 4 * s)) g.DrawEllipse(pen, ring);

                // 이름표는 다 줄어든 고리 바로 아래에 둔다
                var ts = g.MeasureString(name, font);
                float bw = ts.Width + 20 * s, bh = ts.Height + 8 * s;
                var box = new RectangleF(center.X - bw / 2, center.Y + 24 * s + 10 * s, bw, bh);
                using (var path = Rounded(box, 6 * s))
                using (var fill = new SolidBrush(Color.FromArgb(235, 16, 17, 20)))
                using (var edge = new Pen(Color.FromArgb(200, Accent), 1.5f * s))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(edge, path);
                }
                using (var tb = new SolidBrush(Color.White))
                    g.DrawString(name, font, tb, box.X + (bw - ts.Width) / 2, box.Y + (bh - ts.Height) / 2);
            }
            Blit(bmp, alpha);
        }
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    void Blit(Bitmap bmp, byte alpha)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hb = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(mem, hb);
            var sz = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var dst = new POINT { x = origin.X, y = origin.Y };
            var bf = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };   // AC_SRC_ALPHA
            UpdateLayeredWindow(Handle, screen, ref dst, ref sz, mem, ref src, 0, ref bf, 2);                         // ULW_ALPHA
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
