// 메인 PC(키보드·마우스가 붙은 PC) 쪽.
// 평소에는 입력 훅을 걸지 않는다(게임 중 지연을 만들지 않으려고). 50ms마다 커서 위치만 보고,
// 커서가 서브 모니터 자리(zone)와 맞닿은 가장자리에 붙어 있을 때만 원시 입력(WM_INPUT)을 받아 바깥으로 미는 양을 잰다.
// 넘어가면 커서를 그 모니터 가운데에 숨겨 두고 저수준 훅으로 입력을 막아 서브 PC로 보낸다.
// 서브 PC가 자기 화면 가장자리에서 밀었다고 알려 오면, 그 변이 주 PC 모니터와 맞닿은 곳일 때만 돌아온다.
// 비상 복귀: Ctrl+Alt+Home. 연결이 끊겨도 곧바로 돌아온다.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

class SubState
{
    public Machine M;
    public Link Link;
    public Rectangle Zone;
    public bool Connected { get { var l = Link; return l != null && !l.Closed; } }
}

class MainSide : Form
{
    Config cfg;
    Conn conn;
    Rectangle[] mons;
    readonly List<SubState> subs = new List<SubState>();
    NotifyIcon tray;
    Clip clip;
    System.Windows.Forms.Timer poll;

    public static volatile bool Busy;   // 서브 PC 조작 중
    SubState remote;               // 지금 입력을 보내는 서브 PC. null이면 주 PC
    Point park, back;              // 숨겨 둔 커서 자리, 끊기면 돌아올 자리
    bool rawOn;
    int pushSum;
    IntPtr mouseHook, keyHook;
    W.HookProc mouseProc, keyProc; // 가비지 수집되지 않게 붙잡아 둔다
    readonly HashSet<int> localDown = new HashSet<int>();  // 넘어갈 때 이미 눌려 있던 키: 뗌은 주 PC로 흘려보낸다
    bool ctrl, alt;
    Form hider;
    readonly Dictionary<SubState, ViewerForm> viewers = new Dictionary<SubState, ViewerForm>();   // 화면 창(ScreenView.cs)
    readonly Dictionary<SubState, ShareForm> shares = new Dictionary<SubState, ShareForm>();      // 공유 폴더 창(Share.cs)

    public static void Run(Config c, Conn conn)
    {
        Application.Run(new MainSide(c, conn));
    }

    MainSide(Config c, Conn cn)
    {
        cfg = c;
        conn = cn;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;
        foreach (var m in cfg.Subs) subs.Add(new SubState { M = m });
        RefreshLayout();
        conn.OnLink = (l, m, hello) => { l.OnMessage = OnMessage; BeginInvoke((Action)(() => Register(l, m, hello))); };
        conn.OnClosed = (l, m) => { FileXfer.Closed(l); BeginInvoke((Action)(() => OnClosed(l))); };
        conn.OnPaired = m => BeginInvoke((Action)(() => { if (m.Role == "sub" && !subs.Any(x => x.M == m)) { subs.RemoveAll(x => x.M.Host == m.Host); subs.Add(new SubState { M = m }); RefreshLayout(); } UpdateTray(); }));
        SystemEvents.DisplaySettingsChanged += delegate { BeginInvoke((Action)RefreshLayout); };

        tray = new NotifyIcon { Icon = Tray.AppIcon, Visible = true };
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Opening += delegate { BuildMenu(); };
        tray.DoubleClick += delegate { OpenEditor(); };
        UpdateTray();

        mouseProc = MouseHook;
        keyProc = KeyHook;
        hider = MakeHider();
        hider.DragEnter += HiderDragEnter;

        poll = new System.Windows.Forms.Timer { Interval = 50 };
        poll.Tick += delegate { Poll(); };
        poll.Start();
    }

    // 창은 보이지 않게 두고, 핸들이 생긴 뒤에 클립보드 감시와 접속 대기를 시작한다(BeginInvoke에 핸들이 필요하다).
    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated)
        {
            CreateHandle();
            Program.Ui = this;
            clip = new Clip();
            clip.OnLocalChange = (t, b) => { if (!cfg.Clipboard) return; foreach (var s in subs) if (s.Connected) s.Link.Send(t, b); };
            conn.Start();
        }
        base.SetVisibleCore(false);
    }

    void RefreshLayout()
    {
        mons = Config.Monitors();
        var done = new List<Rectangle>();
        foreach (var s in subs) { s.Zone = Config.ZoneOf(s.M, mons, done); if (!s.Zone.IsEmpty) done.Add(s.Zone); }
        Log.Write("모니터: " + string.Join(" ", mons.Select(R).ToArray()) + " / " +
                  string.Join(" ", subs.Select(s => s.M.Name + "=" + (s.Zone.IsEmpty ? "자리 없음" : R(s.Zone))).ToArray()));
    }

    public static string R(Rectangle r) { return "[" + r.X + "," + r.Y + "," + r.Width + "," + r.Height + "]"; }

    // ---------- 파일 끌어 놓기 (FileXfer.cs, VirtualFiles.cs) ----------
    // 주 PC에서 파일을 끈 채 서브로 넘어가면, 커서를 숨겨 둔 창이 끌던 파일 목록을 받아 서브에 알리고 주 PC 쪽 끌기는 Esc로 취소한다.
    // 서브는 그 목록으로 끌기를 이어 가고, 놓으면 탐색기가 그때 내용을 받아 간다.
    // 서브에서 끈 채 넘어오면 서브가 목록을 먼저 보내고, 주 PC는 돌아오자마자 그 목록으로 끌기를 이어 간다.
    readonly object offerGate = new object();
    int myId, offerId;
    List<FileEntry> myEntries = new List<FileEntry>();
    bool probing;                      // 왼쪽 단추를 누른 채 넘어갔다. 숨김 창에 끌던 것이 들어오나 본다
    SubState dragFrom;                 // 서브에서 끌고 오는 파일 목록
    List<FileEntry> dragList;
    int dragId;
    DateTime dragAt;

    void HiderDragEnter(object sender, DragEventArgs e)
    {
        e.Effect = DragDropEffects.None;
        if (!probing || remote == null || !cfg.Files) return;
        probing = false;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (paths == null || paths.Length == 0) return;
        keybd_event(0x1B, 0x01, 0, UIntPtr.Zero);   // 주 PC 쪽 끌기는 취소한다
        keybd_event(0x1B, 0x01, 2, UIntPtr.Zero);
        var to = remote;
        new Thread(() =>
        {
            try
            {
                var list = FileXfer.List(paths);
                int id;
                lock (offerGate) { id = ++offerId; myId = id; myEntries = list; }
                if (to.Connected) to.Link.Send(Msg.FileOffer, FileXfer.OfferJson(id, list));
                Log.Write("끌던 파일 " + list.Count + "개를 " + to.M.Name + "(으)로 넘김");
            }
            catch (Exception x) { Log.Write("끌던 파일 목록 알리기 실패: " + x.Message); }
        }) { IsBackground = true }.Start();
    }

    // 서브가 끌고 오는 파일 목록. 곧 Edge가 와서 주 PC로 돌아온다.
    void TakeOffer(SubState s, byte[] body)
    {
        dragList = FileXfer.ParseOffer(body, out dragId);
        dragFrom = s;
        dragAt = DateTime.Now;
    }

    // 돌아온 자리에서 끌기를 이어 간다. 놓을 때까지 여기서 기다린다(그동안에도 메시지는 돈다).
    void ContinueDrag()
    {
        var s = dragFrom; var list = dragList; int id = dragId;
        dragFrom = null;
        var v = new VirtualFiles(list, idx => Request(s, id, idx));
        v.Say = m => BeginInvoke((Action)(() => Balloon(m)));
        Log.Write(s.M.Name + "에서 끌고 온 파일 " + list.Count + "개");
        // 서브에서 누르고 온 왼쪽 단추를 주 PC에서 다시 누른다. 실제로 떼면 놓인다
        VirtualFiles.StartAt(Cursor.Position, v, () => mouse_event(0x2, 0, 0, 0, UIntPtr.Zero));
    }

    System.IO.Stream Request(SubState s, int id, int idx)
    {
        return FileXfer.Open(s.Link, id, idx);
    }

    FileEntry MyEntry(int id, int idx)
    {
        lock (offerGate) if (id == myId && idx >= 0 && idx < myEntries.Count) return myEntries[idx];
        return Share.Entry(id, idx);
    }

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

    DateTime lastBalloon;

    void Balloon(string text)
    {
        if ((DateTime.Now - lastBalloon).TotalSeconds < 3) return;   // 파일이 여러 개면 알림이 쏟아지지 않게
        lastBalloon = DateTime.Now;
        tray.ShowBalloonTip(3000, "연동", text, ToolTipIcon.Info);
    }

    void OnMessage(Link k, byte type, byte[] body)
    {
        if (FileXfer.Handle(k, type, body) || Share.Handle(k, type, body)) return;
        var s = subs.FirstOrDefault(x => x.Link == k);
        if (s == null) return;
        if (type == Msg.FileReq) { FileXfer.Serve(k, body, MyEntry); return; }
        if (type == Msg.ViewFrame || type == Msg.ViewStop)
        {
            ViewerForm v;
            lock (viewers) viewers.TryGetValue(s, out v);
            if (v == null) { if (type == Msg.ViewFrame) k.Send(Msg.ViewStop, null); return; }
            if (type == Msg.ViewFrame) v.OnFrame(body); else v.OnStop(body.Length > 0 ? body[0] : (byte)0);
            return;
        }
        if (type == Msg.Edge) { var side = body[0]; var f = Link.Frac(body); BeginInvoke((Action)(() => OnEdge(s, side, f))); }
        else if (type == Msg.FileOffer && cfg.Files) BeginInvoke((Action)(() => { try { TakeOffer(s, body); } catch (Exception e) { Log.Write("파일 목록 받기 실패: " + e.Message); } }));
        else if ((type == Msg.ClipText || type == Msg.ClipImage) && cfg.Clipboard)
            BeginInvoke((Action)(() =>
            {
                clip.Apply(type, body);
                foreach (var o in subs) if (o != s && o.Connected) o.Link.Send(type, body);
            }));
    }

    void Register(Link k, Machine m, Dictionary<string, object> hello)
    {
        var s = subs.FirstOrDefault(x => x.M == m);
        if (s == null) { k.Close("서브 PC가 아님"); return; }
        if (s.Connected && s.Link != k && remote == s) GoLocal(back);
        s.Link = k;
        try
        {
            var sz = new[] { Convert.ToInt32(hello["w"]), Convert.ToInt32(hello["h"]) };
            if (m.Screen == null || m.Screen[0] != sz[0] || m.Screen[1] != sz[1]) { m.Screen = sz; cfg.Save(); RefreshLayout(); }
        }
        catch (Exception e) { Log.Write("화면 크기 저장 실패: " + e.Message); }
        Log.Write(s.M.Name + " 연결됨, 자리 " + (s.Zone.IsEmpty ? "없음" : R(s.Zone)));
        ViewerForm v;
        lock (viewers) viewers.TryGetValue(s, out v);
        if (v != null) v.Restart();
        Tell(Program.LayoutText(cfg).Replace("\r\n", " | "));
        UpdateTray();
    }

    // 서브 PC들 로그에도 남긴다(주 PC 로그는 원격에서 보기 어렵다).
    void Tell(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        foreach (var x in subs) if (x.Connected) x.Link.Send(Msg.Info, b);
    }

    void OnClosed(Link k)
    {
        var s = subs.FirstOrDefault(x => x.Link == k);
        if (s == null) return;
        if (remote == s) GoLocal(back);
        ViewerForm v;
        lock (viewers) viewers.TryGetValue(s, out v);
        if (v != null) v.Closed2();
        UpdateTray();
    }

    void OpenViewer(SubState s)
    {
        ViewerForm v;
        lock (viewers) viewers.TryGetValue(s, out v);
        if (v != null) { v.Activate(); return; }
        v = new ViewerForm(s);
        lock (viewers) viewers[s] = v;
        v.FormClosed += delegate { lock (viewers) viewers.Remove(s); };
        v.Show();
    }

    void OpenShare(SubState s)
    {
        ShareForm f;
        if (shares.TryGetValue(s, out f)) { f.Activate(); return; }
        f = new ShareForm(s.M.Name, () => s.Link);
        shares[s] = f;
        f.FormClosed += delegate { shares.Remove(s); };
        f.Show();
    }

    // ---------- 넘어가기 판정 (주 PC에 있을 때) ----------
    bool OnMonitor(Point p) { return mons.Any(m => m.Contains(p)); }

    // 커서 p에서 (dx, dy) 방향으로 밀 때 들어갈 서브 PC. side는 서브 화면에서 나타날 변, frac은 그 변 위 위치(0~1).
    SubState Target(Point p, int dx, int dy, out byte side, out float frac)
    {
        side = 0; frac = 0;
        foreach (var m in mons)
        {
            if (!m.Contains(p)) continue;
            Point q; byte sd;
            if (dx < 0 && p.X == m.Left) { q = new Point(p.X - 1, p.Y); sd = Msg.Right; }
            else if (dx > 0 && p.X == m.Right - 1) { q = new Point(m.Right, p.Y); sd = Msg.Left; }
            else if (dy < 0 && p.Y == m.Top) { q = new Point(p.X, p.Y - 1); sd = Msg.Bottom; }
            else if (dy > 0 && p.Y == m.Bottom - 1) { q = new Point(p.X, m.Bottom); sd = Msg.Top; }
            else continue;
            if (OnMonitor(q)) continue;
            foreach (var s in subs)
            {
                if (!s.Connected || s.Zone.IsEmpty || !s.Zone.Contains(q)) continue;
                side = sd;
                frac = sd == Msg.Left || sd == Msg.Right
                    ? (p.Y - s.Zone.Top) / (float)s.Zone.Height
                    : (p.X - s.Zone.Left) / (float)s.Zone.Width;
                return s;
            }
        }
        return null;
    }

    bool NearTrigger(Point p)
    {
        byte sd; float f;
        return Target(p, -1, 0, out sd, out f) != null || Target(p, 1, 0, out sd, out f) != null
            || Target(p, 0, -1, out sd, out f) != null || Target(p, 0, 1, out sd, out f) != null;
    }

    void Poll()
    {
        if (remote != null || !subs.Any(s => s.Connected)) { SetRaw(false); return; }
        W.POINT p;
        if (!W.GetCursorPos(out p)) return;
        SetRaw(NearTrigger(new Point(p.X, p.Y)));
    }

    void SetRaw(bool on)
    {
        if (on == rawOn) return;
        var d = new W.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = on ? 0x100u : 0x1u, hwndTarget = on ? Handle : IntPtr.Zero };
        if (W.RegisterRawInputDevices(new[] { d }, 1, (uint)Marshal.SizeOf(typeof(W.RAWINPUTDEVICE)))) rawOn = on;
        pushSum = 0;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x00FF && rawOn && remote == null) OnRaw(m.LParam);  // WM_INPUT
        base.WndProc(ref m);
    }

    void OnRaw(IntPtr hRaw)
    {
        uint size = 0, head = (uint)(8 + 2 * IntPtr.Size);
        W.GetRawInputData(hRaw, 0x10000003, IntPtr.Zero, ref size, head);
        if (size == 0) return;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (W.GetRawInputData(hRaw, 0x10000003, buf, ref size, head) != size) return;
            if (Marshal.ReadInt32(buf) != 0) return;                       // 마우스가 아님
            int o = (int)head;
            if ((Marshal.ReadInt16(buf, o) & 1) != 0) return;               // 절대 좌표 장치(원격 데스크톱 등)
            int dx = Marshal.ReadInt32(buf, o + 12), dy = Marshal.ReadInt32(buf, o + 16);
            if (dx == 0 && dy == 0) return;
            W.POINT cp; W.GetCursorPos(out cp);
            var p = new Point(cp.X, cp.Y);
            byte side; float frac;
            // 더 크게 민 방향을 먼저 본다
            var s = Math.Abs(dx) >= Math.Abs(dy)
                ? (Target(p, dx, 0, out side, out frac) ?? Target(p, 0, dy, out side, out frac))
                : (Target(p, 0, dy, out side, out frac) ?? Target(p, dx, 0, out side, out frac));
            if (s == null) { pushSum = 0; return; }
            pushSum += side == Msg.Left || side == Msg.Right ? Math.Abs(dx) : Math.Abs(dy);
            if (pushSum < cfg.Push) return;
            if (CursorClipped()) { pushSum = 0; return; }                   // 게임이 커서를 가둬 둔 중이면 넘어가지 않는다
            GoRemote(s, side, frac, p);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static bool CursorClipped()
    {
        W.RECT r;
        if (!W.GetClipCursor(out r)) return false;
        var v = SystemInformation.VirtualScreen;
        return r.Left > v.Left || r.Top > v.Top || r.Right < v.Right || r.Bottom < v.Bottom;
    }

    // ---------- 전환 ----------
    void GoRemote(SubState s, byte side, float frac, Point from)
    {
        SetRaw(false);
        remote = s;
        Busy = true;
        back = from;
        var mb = mons.First(m => m.Contains(from));
        park = new Point(mb.Left + mb.Width / 2, mb.Top + mb.Height / 2);
        localDown.Clear();
        for (int vk = 1; vk < 255; vk++) if ((W.GetAsyncKeyState(vk) & 0x8000) != 0) localDown.Add(vk);
        ctrl = alt = false;
        var hm = W.GetModuleHandle(null);
        mouseHook = W.SetWindowsHookEx(14, mouseProc, hm, 0);   // WH_MOUSE_LL
        keyHook = W.SetWindowsHookEx(13, keyProc, hm, 0);       // WH_KEYBOARD_LL
        W.SetCursorPos(park.X, park.Y);
        hider.Location = new Point(park.X - 32, park.Y - 32);
        hider.Show();
        s.Link.SendSideFrac(Msg.Enter, side, frac);
        // 왼쪽 단추를 누른 채 넘어갔으면 파일을 끌고 있을 수 있다. 끌기 중인 탐색기가 숨김 창을 알아채게 커서를 1픽셀 흔든다.
        probing = localDown.Contains(1);
        if (probing) { mouse_event(0x1, 1, 0, 0, UIntPtr.Zero); mouse_event(0x1, -1, 0, 0, UIntPtr.Zero); }
        Log.Write(s.M.Name + "(으)로 넘어감, 변 " + side + " 위치 " + frac.ToString("0.000"));
        Tell("넘어옴, 변 " + side + " 위치 " + frac.ToString("0.000") + " (주 PC " + from.X + "," + from.Y + ")");
        UpdateTray();
    }

    void GoLocal(Point to)
    {
        if (remote == null) return;
        var s = remote;
        remote = null;
        Busy = false;
        probing = false;
        if (mouseHook != IntPtr.Zero) { W.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        if (keyHook != IntPtr.Zero) { W.UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
        hider.Hide();
        if (s.Connected) s.Link.Send(Msg.Leave, null);
        // 모니터 밖 좌표면 가장 가까운 모니터 안으로 넣는다
        var mb = Screen.FromPoint(to).Bounds;
        to = new Point(Math.Max(mb.Left, Math.Min(mb.Right - 1, to.X)), Math.Max(mb.Top, Math.Min(mb.Bottom - 1, to.Y)));
        W.SetCursorPos(to.X, to.Y);
        Log.Write(s.M.Name + "에서 돌아옴");
        Tell("주 PC로 돌아감 (" + to.X + "," + to.Y + ")");
        UpdateTray();
    }

    // 서브 PC가 자기 화면 side 변에서 바깥으로 밀었다. 그 변이 주 PC 모니터와 맞닿은 곳이면 돌아온다.
    void OnEdge(SubState s, byte side, float frac)
    {
        if (remote != s) return;
        var z = s.Zone;
        frac = Math.Max(0f, Math.Min(1f, frac));
        Point q;
        if (side == Msg.Right) q = new Point(z.Right, z.Top + (int)(frac * (z.Height - 1)));
        else if (side == Msg.Left) q = new Point(z.Left - 1, z.Top + (int)(frac * (z.Height - 1)));
        else if (side == Msg.Top) q = new Point(z.Left + (int)(frac * (z.Width - 1)), z.Top - 1);
        else q = new Point(z.Left + (int)(frac * (z.Width - 1)), z.Bottom);
        if (!OnMonitor(q)) return;
        GoLocal(q);
        if (dragFrom == s && (DateTime.Now - dragAt).TotalSeconds < 5) ContinueDrag();
    }

    // ---------- 훅 (서브 PC에 있을 때만 걸려 있다) ----------
    IntPtr MouseHook(int code, IntPtr wp, IntPtr lp)
    {
        if (code < 0 || remote == null) return W.CallNextHookEx(IntPtr.Zero, code, wp, lp);
        var h = (W.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lp, typeof(W.MSLLHOOKSTRUCT));
        if ((h.flags & 1) != 0) return W.CallNextHookEx(IntPtr.Zero, code, wp, lp);   // 다른 프로그램이 넣은 입력
        var l = remote.Link;
        int msg = wp.ToInt32();
        switch (msg)
        {
            case 0x200:
                int dx = h.pt.X - park.X, dy = h.pt.Y - park.Y;
                if (dx != 0 || dy != 0)
                {
                    var b = new byte[8];
                    BitConverter.GetBytes(dx).CopyTo(b, 0);
                    BitConverter.GetBytes(dy).CopyTo(b, 4);
                    l.Send(Msg.Move, b);
                }
                break;
            // 넘어가기 전부터 누르고 있던 단추를 떼면 주 PC에도 뗌을 넘긴다(안 그러면 주 PC에서 눌린 채로 남는다)
            case 0x201: case 0x202:
                l.Send(Msg.Button, new byte[] { 0, (byte)(msg == 0x201 ? 1 : 0) });
                if (msg == 0x202 && localDown.Remove(1)) return W.CallNextHookEx(IntPtr.Zero, code, wp, lp);
                break;
            case 0x204: case 0x205:
                l.Send(Msg.Button, new byte[] { 1, (byte)(msg == 0x204 ? 1 : 0) });
                if (msg == 0x205 && localDown.Remove(2)) return W.CallNextHookEx(IntPtr.Zero, code, wp, lp);
                break;
            case 0x207: case 0x208:
                l.Send(Msg.Button, new byte[] { 2, (byte)(msg == 0x207 ? 1 : 0) });
                if (msg == 0x208 && localDown.Remove(4)) return W.CallNextHookEx(IntPtr.Zero, code, wp, lp);
                break;
            case 0x20B: case 0x20C:
                l.Send(Msg.Button, new byte[] { (byte)((h.mouseData >> 16) == 2 ? 4 : 3), (byte)(msg == 0x20B ? 1 : 0) });
                break;
            case 0x20A: case 0x20E:
                var w = new byte[5];
                w[0] = (byte)(msg == 0x20E ? 1 : 0);
                BitConverter.GetBytes((int)(short)(h.mouseData >> 16)).CopyTo(w, 1);
                l.Send(Msg.Wheel, w);
                break;
        }
        return (IntPtr)1;
    }

    IntPtr KeyHook(int code, IntPtr wp, IntPtr lp)
    {
        if (code < 0 || remote == null) return W.CallNextHookEx(IntPtr.Zero, code, wp, lp);
        var h = (W.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lp, typeof(W.KBDLLHOOKSTRUCT));
        if ((h.flags & 0x10) != 0) return W.CallNextHookEx(IntPtr.Zero, code, wp, lp);  // 주입된 입력
        bool up = (h.flags & 0x80) != 0;
        int vk = (int)h.vkCode;
        if (up && localDown.Remove(vk)) return W.CallNextHookEx(IntPtr.Zero, code, wp, lp);
        if (vk == 0x11 || vk == 0xA2 || vk == 0xA3) ctrl = !up;
        if (vk == 0x12 || vk == 0xA4 || vk == 0xA5) alt = !up;
        if (!up && vk == 0x24 && ctrl && alt)   // Ctrl+Alt+Home: 비상 복귀
        {
            BeginInvoke((Action)(() => GoLocal(back)));
            return (IntPtr)1;
        }
        var b = new byte[5];
        BitConverter.GetBytes((ushort)vk).CopyTo(b, 0);
        BitConverter.GetBytes((ushort)h.scanCode).CopyTo(b, 2);
        b[4] = (byte)((up ? 1 : 0) | ((h.flags & 1) != 0 ? 2 : 0));
        remote.Link.Send(Msg.Key, b);
        return (IntPtr)1;
    }

    // 숨겨 둔 커서 아래에 까는 거의 투명한 창. 이 창의 커서가 빈 모양이라 주 PC 화면에서 커서가 안 보인다.
    static Form MakeHider()
    {
        var f = new HiderForm
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
            StartPosition = FormStartPosition.Manual, Size = new Size(64, 64), Opacity = 0.01,
            AllowDrop = true,   // 끌던 파일 목록을 받는다(HiderDragEnter)
        };
        var and = Enumerable.Repeat((byte)0xFF, 128).ToArray();
        var xor = new byte[128];
        f.Cursor = new Cursor(W.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, xor));
        var unused = f.Handle;
        return f;
    }

    // ---------- 트레이 ----------
    void UpdateTray()
    {
        var t = "연동 - " + cfg.Name + (remote != null ? " → " + remote.M.Name : "") + " / " +
                (subs.Count == 0 ? "짝 맺은 서브 없음" : string.Join(", ", subs.Select(s => s.M.Name + (s.Connected ? " 연결됨" : " 끊김")).ToArray()));
        tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
    }

    LayoutEditor editor;

    void OpenEditor()
    {
        if (editor != null && !editor.IsDisposed) { editor.Activate(); return; }
        if (subs.Count == 0) { MessageBox.Show("먼저 서브 PC와 짝을 맺어 주세요(트레이 메뉴 → 설정).", "연동"); return; }
        editor = new LayoutEditor(cfg, () => { RefreshLayout(); Tell(Program.LayoutText(cfg).Replace(Environment.NewLine, " | ")); });
        editor.Show();
    }

    void BuildMenu()
    {
        var m = tray.ContextMenuStrip;
        m.Items.Clear();
        m.Items.Add(new ToolStripMenuItem("메인 PC: " + cfg.Name) { Enabled = false });
        foreach (var s in subs)
            m.Items.Add(new ToolStripMenuItem(s.M.Name + ": " + (s.Connected ? "연결됨" : "끊김") +
                (remote == s ? ", 조작 중" : "")) { Enabled = false });
        m.Items.Add(new ToolStripSeparator());
        foreach (var s in subs.Where(x => x.Connected))
        {
            var t = s;
            m.Items.Add(t.M.Name + " 화면 보기", null, delegate { OpenViewer(t); });
            m.Items.Add(t.M.Name + " 공유 폴더 열기", null, delegate { OpenShare(t); });
        }
        m.Items.Add("모니터 배치 정하기", null, delegate { OpenEditor(); });
        Tray.Common(m, cfg, conn, () => { GoLocal(back); tray.Visible = false; Application.Exit(); });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        GoLocal(back);
        tray.Visible = false;
        base.OnFormClosed(e);
    }
}

// 띄워도 포커스를 가져가지 않는 창(게임·작업 중인 창의 포커스를 지킨다)
class HiderForm : Form
{
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST. 맨 위는 만들 때 줘야 한다(뒤에서 도는 프로그램이 나중에 TopMost를 켜면 무시된다)
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80 | 0x8; return p; }
    }
}

static class W
{
    public delegate IntPtr HookProc(int code, IntPtr wp, IntPtr lp);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wp, IntPtr lp);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetClipCursor(out RECT r);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] public static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint headSize);
    [DllImport("kernel32.dll")] public static extern bool SetHandleInformation(IntPtr h, uint mask, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr CreateCursor(IntPtr inst, int x, int y, int w, int h, byte[] and, byte[] xor);
}
