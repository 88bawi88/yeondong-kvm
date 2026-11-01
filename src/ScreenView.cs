// 상대 화면 창. 메인 PC가 서브 PC 화면을 창으로 보고, 창 위에서 마우스·키보드로 조작한다.
//   메인→서브 ViewStart [최대 너비]: 서브가 화면을 JPEG로 찍어 ViewFrame [가상 화면 x,y,w,h][JPEG]로 보낸다.
//   메인은 그림을 그린 뒤 ViewAck를 보낸다. 서브는 답을 못 받은 그림이 2장이면 기다린다(느린 연결에서 쌓이지 않게).
//   창 위에서 움직이면 ViewPoint [x][y](서브 가상 화면 안 비율)로 커서를 옮긴다. 단추·휠·키는 원래 입력 메시지를 쓴다.
//   창이 포커스를 잃으면 Leave를 보내 서브가 눌린 키·단추를 모두 뗀다.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// 서브 PC 쪽: 화면을 찍어 보낸다. 메인 PC는 하나라 보내는 곳도 하나다.
static class ScreenShare
{
    static readonly object gate = new object();
    static Link to;
    static int maxW, inFlight, gen;

    public static void Start(Link l, int width)
    {
        lock (gate)
        {
            to = l; maxW = Math.Max(320, Math.Min(3840, width)); inFlight = 0;
            int g = ++gen;
            new Thread(() => Loop(g)) { IsBackground = true, Name = "screen-share" }.Start();
            Monitor.PulseAll(gate);
        }
        Log.Write("화면 보내기 시작(" + maxW + "px)");
    }

    public static void Stop(Link l)
    {
        lock (gate)
        {
            if (l != null && to != l) return;
            if (to != null) Log.Write("화면 보내기 끝");
            to = null; gen++;
            Monitor.PulseAll(gate);
        }
    }

    public static void Ack()
    {
        lock (gate) { if (inFlight > 0) inFlight--; Monitor.PulseAll(gate); }
    }

    static void Loop(int g)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        int last = 0;
        while (true)
        {
            Link l; int w;
            lock (gate)
            {
                while (g == gen && to != null && inFlight >= 2) Monitor.Wait(gate, 1000);
                if (g != gen || to == null || to.Closed) return;
                l = to; w = maxW;
            }
            int wait = 66 - unchecked(Environment.TickCount - last);   // 1초에 15장까지
            if (wait > 0) Thread.Sleep(wait);
            last = Environment.TickCount;
            byte[] frame;
            try { frame = Capture(w, codec); }
            catch (Exception e) { Log.Write("화면 찍기 실패: " + e.Message); Thread.Sleep(500); continue; }   // 잠금 화면 등
            lock (gate) { if (g != gen) return; inFlight++; }
            if (!l.SendSlow(Msg.ViewFrame, frame)) return;
        }
    }

    static byte[] Capture(int maxW, ImageCodecInfo codec)
    {
        var vs = SystemInformation.VirtualScreen;
        using (var full = new Bitmap(vs.Width, vs.Height, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(full))
            {
                g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
                DrawCursor(g, vs);
            }
            Bitmap img = full;
            if (vs.Width > maxW)
            {
                img = new Bitmap(maxW, Math.Max(1, (int)((long)vs.Height * maxW / vs.Width)), PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(img))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(full, new Rectangle(0, 0, img.Width, img.Height));
                }
            }
            try
            {
                // 1MB(한 메시지 한도)를 넘지 않게 화질을 낮춰 가며 담는다
                foreach (long q in new long[] { 70, 50, 30 })
                {
                    var ms = new MemoryStream();
                    ms.Write(FileXfer.Ints(vs.X, vs.Y, vs.Width, vs.Height), 0, 16);
                    using (var p = new EncoderParameters(1))
                    {
                        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, q);
                        img.Save(ms, codec, p);
                    }
                    if (ms.Length < 1000000 || q == 30) return ms.ToArray();
                }
                return null;
            }
            finally { if (img != full) img.Dispose(); }
        }
    }

    // 마우스가 꽂힌 PC는 진짜 커서를 그려 넣는다. 마우스가 없으면 연동이 띄운 커서 창이 이미 찍힌다.
    static void DrawCursor(Graphics g, Rectangle vs)
    {
        if (!SystemInformation.MousePresent) return;
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
        if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
        try
        {
            var c = new Cursor(ci.hCursor);
            var hs = c.HotSpot;
            c.Draw(g, new Rectangle(ci.x - vs.Left - hs.X, ci.y - vs.Top - hs.Y, c.Size.Width, c.Size.Height));
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public int x; public int y; }
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
}

// 메인 PC 쪽: 서브 화면을 보여 주고, 그 위의 입력을 서브로 보낸다.
class ViewerForm : Form
{
    readonly SubState sub;
    Image frame;
    Rectangle remote;        // 서브 가상 화면(그 PC 좌표)
    string note = "화면을 받는 중...";
    bool inputOn;            // 서브가 이 창 입력을 받는 중(Leave를 보내야 한다)

    public ViewerForm(SubState s)
    {
        sub = s;
        Text = s.M.Name + " 화면 - 연동";
        Icon = Tray.AppIcon;
        BackColor = Color.FromArgb(0x20, 0x20, 0x20);
        ForeColor = Color.White;
        DoubleBuffered = true;
        KeyPreview = false;
        ImeMode = ImeMode.Disable;   // 한글 입력기가 키를 가로채지 않게(조합은 서브 PC 입력기가 한다)
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(wa.Width * 2 / 3, wa.Height * 2 / 3);
        Location = new Point(wa.Left + wa.Width / 6, wa.Top + wa.Height / 6);
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); Restart(); }

    // 연결이 새로 생기면 다시 보내 달라고 한다
    public void Restart()
    {
        var l = sub.Link;
        if (l == null || l.Closed) { note = "연결 끊김"; Invalidate(); return; }
        int w = Screen.FromControl(this).Bounds.Width;
        l.Send(Msg.ViewStart, FileXfer.Ints(w));
    }

    public void Closed2() { note = "연결 끊김"; inputOn = false; Invalidate(); }

    // 연결의 읽기 스레드에서 부른다
    public void OnFrame(byte[] b)
    {
        Image img;
        Rectangle r;
        try
        {
            r = new Rectangle(BitConverter.ToInt32(b, 0), BitConverter.ToInt32(b, 4), BitConverter.ToInt32(b, 8), BitConverter.ToInt32(b, 12));
            img = Image.FromStream(new MemoryStream(b, 16, b.Length - 16));
        }
        catch (Exception e) { Log.Write("화면 그림 풀기 실패: " + e.Message); Ack(); return; }
        try
        {
            BeginInvoke((Action)(() =>
            {
                var old = frame;
                frame = img; remote = r; note = null;
                if (old != null) old.Dispose();
                Invalidate();
                Update();
                Ack();
            }));
        }
        catch { img.Dispose(); }
    }

    public void OnStop(byte reason)
    {
        try { BeginInvoke((Action)(() => { note = reason == 1 ? "서브 PC에서 화면 보기를 꺼 두었습니다" : "화면 보내기가 끝났습니다"; Invalidate(); })); } catch { }
    }

    void Ack() { var l = sub.Link; if (l != null && !l.Closed) l.Send(Msg.ViewAck, null); }

    Rectangle ImageRect()
    {
        if (frame == null) return Rectangle.Empty;
        var c = ClientRectangle;
        float k = Math.Min(c.Width / (float)frame.Width, c.Height / (float)frame.Height);
        int w = (int)(frame.Width * k), h = (int)(frame.Height * k);
        return new Rectangle(c.Left + (c.Width - w) / 2, c.Top + (c.Height - h) / 2, w, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (frame != null)
        {
            e.Graphics.InterpolationMode = InterpolationMode.Bilinear;
            e.Graphics.DrawImage(frame, ImageRect());
        }
        if (note != null)
            TextRenderer.DrawText(e.Graphics, note, Font, ClientRectangle, Color.FromArgb(0xa0, 0xa0, 0xa0),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

    // ---- 입력 ----
    void Send(byte type, byte[] body)
    {
        var l = sub.Link;
        if (l != null && !l.Closed && frame != null) l.Send(type, body);
    }

    void MoveTo(Point p)
    {
        var r = ImageRect();
        if (r.Width <= 0) return;
        float fx = Math.Max(0f, Math.Min(1f, (p.X - r.Left) / (float)r.Width));
        float fy = Math.Max(0f, Math.Min(1f, (p.Y - r.Top) / (float)r.Height));
        var b = new byte[8];
        BitConverter.GetBytes(fx).CopyTo(b, 0);
        BitConverter.GetBytes(fy).CopyTo(b, 4);
        Send(Msg.ViewPoint, b);
        inputOn = true;
    }

    static int ButtonOf(MouseButtons b)
    {
        switch (b)
        {
            case MouseButtons.Left: return 0;
            case MouseButtons.Right: return 1;
            case MouseButtons.Middle: return 2;
            case MouseButtons.XButton1: return 3;
            case MouseButtons.XButton2: return 4;
            default: return -1;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (ContainsFocus) MoveTo(e.Location); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int b = ButtonOf(e.Button);
        if (b < 0) return;
        MoveTo(e.Location);
        Send(Msg.Button, new byte[] { (byte)b, 1 });
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int b = ButtonOf(e.Button);
        if (b >= 0) Send(Msg.Button, new byte[] { (byte)b, 0 });
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var b = new byte[5];
        BitConverter.GetBytes(e.Delta).CopyTo(b, 1);
        Send(Msg.Wheel, b);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        var b = new byte[8];
        BitConverter.GetBytes(-1f).CopyTo(b, 0);
        BitConverter.GetBytes(-1f).CopyTo(b, 4);
        Send(Msg.ViewPoint, b);   // 커서는 두고 키 입력만 받기 시작
        inputOn = true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        StopInput();
    }

    void StopInput()
    {
        if (!inputOn) return;
        inputOn = false;
        var l = sub.Link;
        if (l != null && !l.Closed) l.Send(Msg.Leave, null);
    }

    // 키는 Tab·화살표·Alt까지 모두 창 메시지에서 받아 서브로 보낸다(이 PC에서는 처리하지 않는다).
    public override bool PreProcessMessage(ref Message m)
    {
        if (m.Msg >= 0x100 && m.Msg <= 0x109) return false;
        return base.PreProcessMessage(ref m);
    }

    protected override void WndProc(ref Message m)
    {
        int msg = m.Msg;
        if (msg == 0x100 || msg == 0x101 || msg == 0x104 || msg == 0x105)   // WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP
        {
            long lp = m.LParam.ToInt64();
            ushort vk = (ushort)m.WParam.ToInt64();
            ushort scan = (ushort)((lp >> 16) & 0xFF);
            byte flags = (byte)((msg == 0x101 || msg == 0x105 ? 1 : 0) | (int)((lp >> 24) & 1) << 1);
            var b = new byte[5];
            BitConverter.GetBytes(vk).CopyTo(b, 0);
            BitConverter.GetBytes(scan).CopyTo(b, 2);
            b[4] = flags;
            Send(Msg.Key, b);
            return;
        }
        if (msg == 0x102 || msg == 0x106) return;   // WM_CHAR, WM_SYSCHAR: 글자는 서브 PC가 만든다
        if (msg == 0x020E)   // WM_MOUSEHWHEEL
        {
            var b = new byte[5];
            b[0] = 1;
            BitConverter.GetBytes((int)(short)((m.WParam.ToInt64() >> 16) & 0xFFFF)).CopyTo(b, 1);
            Send(Msg.Wheel, b);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopInput();
        var l = sub.Link;
        if (l != null && !l.Closed) l.Send(Msg.ViewStop, null);
        if (frame != null) frame.Dispose();
        base.OnFormClosed(e);
    }
}
