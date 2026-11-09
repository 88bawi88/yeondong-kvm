// 서브 PC 쪽. 메인 PC에서 받은 입력을 SendInput으로 넣고, 서브 화면 바깥 변을 밀면 알리고, 클립보드를 맞춘다.
// 관리자 기능(Service.cs)이 설치돼 있으면 입력은 SYSTEM 도우미를 거쳐 넣는다. 관리자 권한 창·UAC 창·잠금 화면에도 들어간다.
// 프로세스가 모니터별 DPI 인식이라 모든 좌표는 실제 픽셀이다.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Sub
{
    static readonly object gate = new object();   // 입력 넣기와 상태를 함께 지킨다
    static Config cfg;
    static Conn conn;
    static Form form;
    static NotifyIcon tray;
    static Clip clip;
    static Link cur;                               // 지금 연결(없으면 null)
    static bool active;                            // 주 PC가 입력을 보내는 중
    static Point pos;
    static int lastEdge;
    static readonly HashSet<int> btns = new HashSet<int>();
    static readonly Dictionary<ushort, bool> keys = new Dictionary<ushort, bool>();   // vk → 확장키 여부
    static CursorOverlay overlay;
    static Point overlayPos;
    static int overlayPending;

    public static void Run(Config c, Conn cn)
    {
        cfg = c;
        conn = cn;

        form = new Form();
        form.ShowInTaskbar = false;
        var h = form.Handle;   // 핸들만 만들어 둔다(화면에는 띄우지 않는다)
        Program.Ui = form;

        tray = new NotifyIcon();
        tray.Icon = Tray.AppIcon;
        var menu = new ContextMenuStrip();
        menu.Opening += delegate
        {
            menu.Items.Clear();
            var mp = cfg.MainPeer;
            menu.Items.Add(new ToolStripMenuItem("서브 PC: " + cfg.Name) { Enabled = false });
            menu.Items.Add(new ToolStripMenuItem(mp == null ? "짝 맺은 메인 PC 없음" : "메인 PC " + mp.Name + ": " + (cur != null && !cur.Closed ? "연결됨" : "끊김")) { Enabled = false });
            menu.Items.Add(new ToolStripMenuItem("관리자 창·잠금 화면 조작: " + (AgentClient.Available ? "켜짐" : "꺼짐")) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            Tray.Common(menu, cfg, conn, Quit);
        };
        tray.ContextMenuStrip = menu;
        SetTray(false);
        tray.Visible = true;

        overlay = new CursorOverlay();
        clip = new Clip();
        clip.OnLocalChange = delegate(byte type, byte[] body)
        {
            var l = cur;
            if (cfg.Clipboard && l != null && !l.Closed) l.Send(type, body);
        };

        conn.OnLink = (l, m, hello) =>
        {
            if (m.Role != "main") { l.Close("메인 PC가 아님"); return; }
            l.OnMessage = OnMessage;
            Share.Attach(l, m.Host);
            var old = cur;
            cur = l;
            if (old != null && old != l) lock (gate) { active = false; ReleaseAll(); }
            Ui(delegate { SetTray(true); });
        };
        conn.OnClosed = (l, m) =>
        {
            FileXfer.Closed(l);
            ScreenShare.Stop(l);
            Share.Detach(l);
            if (cur != l) return;
            lock (gate) { cur = null; active = false; ReleaseAll(); }
            PostOverlay();
            Ui(delegate { SetTray(false); });
        };
        conn.OnPaired = m => Ui(delegate { SetTray(cur != null && !cur.Closed); });
        StartMoveThread();
        conn.Start();
        Application.Run();
    }

    static void Quit()
    {
        try { lock (gate) { ReleaseAll(); } } catch { }
        try { tray.Visible = false; tray.Dispose(); } catch { }
        try { clip.Dispose(); } catch { }
        Application.Exit();
    }

    // 트레이 글자. NotifyIcon.Text는 63자까지다.
    static void SetTray(bool connected)
    {
        var mp = cfg.MainPeer;
        string t = "연동 - " + cfg.Name + " / " + (mp == null ? "짝 맺은 메인 PC 없음" : mp.Name + (connected ? ": 연결됨" : ": 끊김"));
        if (t.Length > 63) t = t.Substring(0, 63);
        tray.Text = t;
    }

    static void Ui(Action a)
    {
        try { if (form.IsHandleCreated) form.BeginInvoke(a); }
        catch (Exception e) { Log.Write("화면 스레드 호출 실패: " + e.Message); }
    }

    // ---- 파일 끌어 놓기 ----
    // 서브에서 파일을 끈 채 주 PC 쪽 변을 밀면, 커서 밑에 잠깐 창을 띄워 끌던 파일 목록을 받고 서브 쪽 끌기는 Esc로 취소한다.
    // 목록을 주 PC에 보낸 뒤 Edge를 보내면 주 PC가 돌아가서 끌기를 이어 간다. 내용은 주 PC에서 놓을 때 FileReq로 달라고 한다.
    static readonly object offerGate = new object();
    static int myId, offerId;
    static List<FileEntry> myEntries = new List<FileEntry>();
    static bool probed, probing;   // 이번에 누른 왼쪽 단추로 이미 살펴봤나 / 살펴보는 중
    static Form probe;
    static bool probeDone;
    static Link probeLink;
    static byte probeSide;
    static float probeFrac;

    // gate 안에서 부른다. 왼쪽 단추를 누른 채 변을 밀었을 때 끌던 파일이 있나 먼저 본다. true면 Edge는 나중에 보낸다.
    static bool ProbeEdge(Link l, byte side, float frac)
    {
        if (probing) return true;
        if (!btns.Contains(0) || probed || !cfg.Files) return false;
        probed = probing = true;
        probeLink = l; probeSide = side; probeFrac = frac;
        Ui(StartProbe);
        return true;
    }

    static void StartProbe()
    {
        if (probe == null)
        {
            probe = new HiderForm
            {
                FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
                StartPosition = FormStartPosition.Manual, Size = new Size(48, 48), Opacity = 0.01, AllowDrop = true,
            };
            probe.DragEnter += ProbeEnter;
        }
        probeDone = false;
        Point p;
        lock (gate) p = pos;
        probe.Location = new Point(p.X - 24, p.Y - 24);
        probe.Show();
        // 끌기 중인 탐색기가 이 창을 알아채게 커서를 1픽셀 흔든다
        lock (gate) { MoveAbs(new Point(p.X + (p.X > 0 ? -1 : 1), p.Y)); MoveAbs(p); }
        var t = new System.Windows.Forms.Timer { Interval = 400 };
        t.Tick += delegate { t.Stop(); t.Dispose(); EndProbe(null); };
        t.Start();
    }

    static void ProbeEnter(object sender, DragEventArgs e)
    {
        e.Effect = DragDropEffects.None;
        if (probeDone) return;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (paths == null || paths.Length == 0) return;
        EndProbe(paths);
    }

    // 끌던 파일이 있으면 목록을 보내고, 어느 쪽이든 미뤄 둔 Edge를 보낸다.
    static void EndProbe(string[] paths)
    {
        if (probeDone) return;
        probeDone = true;
        probe.Hide();
        var l = probeLink; byte side = probeSide; float frac = probeFrac;
        if (paths == null) { lock (gate) probing = false; l.SendSideFrac(Msg.Edge, side, frac); return; }
        lock (gate)
        {
            N.Key(0x1B, 0x01, 0);   // 서브 쪽 끌기는 취소하고
            N.Key(0x1B, 0x01, 2);
        }
        DoButton(0, false);         // 왼쪽 단추도 뗀다(주 PC에서 이어서 누른 상태가 된다)
        new Thread(() =>
        {
            try
            {
                var list = FileXfer.List(paths);
                int id;
                lock (offerGate) { id = ++offerId; myId = id; myEntries = list; }
                l.Send(Msg.FileOffer, FileXfer.OfferJson(id, list));
                Log.Write("끌던 파일 " + list.Count + "개를 주 PC로 넘김");
            }
            catch (Exception e) { Log.Write("끌던 파일 목록 알리기 실패: " + e.Message); }
            lock (gate) probing = false;
            l.SendSideFrac(Msg.Edge, side, frac);
        }) { IsBackground = true }.Start();
    }

    static FileEntry MyEntry(int id, int idx)
    {
        lock (offerGate) if (id == myId && idx >= 0 && idx < myEntries.Count) return myEntries[idx];
        return Share.Entry(id, idx);
    }

    // 주 PC에서 끌고 넘어온 파일 목록. 이 자리에서 끌기를 이어 가고, 놓으면 탐색기가 그때 하나씩 받아 간다.
    static void TakeOffer(byte[] body)
    {
        int id;
        var list = FileXfer.ParseOffer(body, out id);
        var link = cur;
        var v = new VirtualFiles(list, idx => FileXfer.Open(link, id, idx));
        v.Say = m => Ui(() => Balloon(m));
        Point p;
        lock (gate) { if (!active) return; p = pos; }
        Log.Write("주 PC에서 끌고 온 파일 " + list.Count + "개");
        // 주 PC에서 누르고 온 왼쪽 단추를 서브에서 누른다. 주 PC에서 떼면 놓인다
        VirtualFiles.StartAt(p, v, () => DoButton(0, true));
    }

    static DateTime lastBalloon;

    static void Balloon(string text)
    {
        if ((DateTime.Now - lastBalloon).TotalSeconds < 3) return;   // 파일이 여러 개면 알림이 쏟아지지 않게
        lastBalloon = DateTime.Now;
        tray.ShowBalloonTip(3000, "연동", text, ToolTipIcon.Info);
    }

    // ---- 받은 메시지 ----

    // 이동은 읽기 스레드에서 바로 넣지 않고 쌓아 두었다가 넣기 스레드가 한꺼번에 넣는다.
    // 넣기가 잠깐 늦어져도(도우미 파이프, 화면 전환) 밀린 이동이 하나씩 재생되며 끌려가지 않고, 읽기도 막히지 않는다.
    static readonly object mv = new object();
    static int mvX, mvY;
    static Link mvLink;
    static readonly AutoResetEvent mvWake = new AutoResetEvent(false);

    static void QueueMove(Link l, int dx, int dy)
    {
        lock (mv) { mvX += dx; mvY += dy; mvLink = l; }
        mvWake.Set();
    }

    static void StartMoveThread()
    {
        new Thread(() => { while (true) { mvWake.WaitOne(); try { FlushMove(); } catch (Exception e) { Log.Write("이동 넣기 오류: " + e.Message); } } })
        { IsBackground = true, Name = "input-move", Priority = ThreadPriority.AboveNormal }.Start();
    }

    // 쌓인 이동을 넣는다. 단추·키를 넣기 전에도 불러서 순서를 지킨다(gate를 잡은 채 꺼내므로 앞선 이동이 먼저 들어간다).
    static void FlushMove()
    {
        lock (gate)
        {
            int dx, dy; Link l;
            lock (mv) { dx = mvX; dy = mvY; l = mvLink; mvX = mvY = 0; }
            if ((dx != 0 || dy != 0) && l != null) DoMove(l, dx, dy);
        }
    }

    static void OnMessage(Link l, byte type, byte[] b)
    {
        try
        {
            if (FileXfer.Handle(l, type, b) || Share.Handle(l, type, b)) return;
            if (type == Msg.Move) { QueueMove(l, BitConverter.ToInt32(b, 0), BitConverter.ToInt32(b, 4)); return; }
            if (type >= Msg.Enter && type <= Msg.Jump || type == Msg.ViewPoint) FlushMove();
            switch (type)
            {
                case Msg.ViewStart:
                    if (cfg.ScreenShare) ScreenShare.Start(l, BitConverter.ToInt32(b, 0));
                    else l.Send(Msg.ViewStop, new byte[] { 1 });
                    break;
                case Msg.ViewStop: ScreenShare.Stop(l); break;
                case Msg.ViewAck: ScreenShare.Ack(); break;
                case Msg.ViewPoint: DoViewPoint(BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4)); break;
                case Msg.Enter: DoEnter(b[0], Link.Frac(b)); break;
                case Msg.Jump: DoJump(); break;
                case Msg.Leave: lock (gate) { active = false; ReleaseAll(); } PostOverlay(); break;
                case Msg.Button: DoButton(b[0], b[1] != 0); break;
                case Msg.Wheel: DoWheel(b[0] != 0, BitConverter.ToInt32(b, 1)); break;
                case Msg.Key: DoKey(BitConverter.ToUInt16(b, 0), BitConverter.ToUInt16(b, 2), b[4]); break;
                case Msg.Info: Log.Write("[메인 PC] " + Encoding.UTF8.GetString(b)); break;
                case Msg.FileOffer:
                    if (!cfg.Files) break;
                    byte[] ob = b;
                    Ui(delegate { try { TakeOffer(ob); } catch (Exception e) { Log.Write("파일 목록 받기 실패: " + e.Message); } });
                    break;
                case Msg.FileReq: FileXfer.Serve(l, b, MyEntry); break;
                case Msg.ClipText:
                case Msg.ClipImage:
                    if (!cfg.Clipboard) break;
                    byte ct = type; byte[] cb = b;
                    Ui(delegate { try { clip.Apply(ct, cb); } catch (Exception e) { Log.Write("클립보드 적용 실패: " + e.Message); } });
                    break;
            }
        }
        catch (Exception e) { Log.Write("메시지 처리 오류(" + type + "): " + e.Message); }
    }

    static void DoEnter(byte side, float frac)
    {
        lock (gate)
        {
            var vs = SystemInformation.VirtualScreen;
            Point p;
            if (side == Msg.Left) p = new Point(vs.Left, vs.Top + (int)(frac * vs.Height));
            else if (side == Msg.Right) p = new Point(vs.Right - 1, vs.Top + (int)(frac * vs.Height));
            else if (side == Msg.Top) p = new Point(vs.Left + (int)(frac * vs.Width), vs.Top);
            else p = new Point(vs.Left + (int)(frac * vs.Width), vs.Bottom - 1);
            var sb = Screen.FromPoint(p).Bounds;   // 가장 가까운 모니터 안으로
            p.X = Math.Max(sb.Left, Math.Min(sb.Right - 1, p.X));
            p.Y = Math.Max(sb.Top, Math.Min(sb.Bottom - 1, p.Y));
            pos = p;
            active = true;
            MoveAbs(pos);
        }
    }

    // 단축키로 넘어왔다. 커서는 지금 자리(없으면 주 모니터 가운데)에 두고, 어디인지 잠깐 보여 준다.
    static void DoJump()
    {
        Point p;
        lock (gate)
        {
            p = pos;
            try { var c = Cursor.Position; if (OnAnyScreen(c.X, c.Y)) p = c; } catch { }
            if (!OnAnyScreen(p.X, p.Y))
            {
                var b = Screen.PrimaryScreen.Bounds;
                p = new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
            }
            pos = p;
            active = true;
            MoveAbs(pos);
        }
        Ui(delegate { Locator.Flash(p, cfg.Name); });
    }

    // 메인 PC의 화면 창 위 위치(가상 화면 비율). 음수면 커서는 두고 입력만 받기 시작한다.
    static void DoViewPoint(float fx, float fy)
    {
        lock (gate)
        {
            active = true;
            if (fx < 0 || fy < 0) return;
            var vs = SystemInformation.VirtualScreen;
            var p = new Point(vs.Left + (int)(fx * (vs.Width - 1) + 0.5f), vs.Top + (int)(fy * (vs.Height - 1) + 0.5f));
            if (!OnAnyScreen(p.X, p.Y))   // 모니터 사이 빈 곳이면 가장 가까운 모니터 안으로
            {
                var sb = Screen.FromPoint(p).Bounds;
                p.X = Math.Max(sb.Left, Math.Min(sb.Right - 1, p.X));
                p.Y = Math.Max(sb.Top, Math.Min(sb.Bottom - 1, p.Y));
            }
            if (p == pos) return;
            pos = p;
            MoveAbs(pos);
        }
    }

    static bool OnAnyScreen(int x, int y)
    {
        foreach (var s in Screen.AllScreens) if (s.Bounds.Contains(x, y)) return true;
        return false;
    }

    static void DoMove(Link l, int dx, int dy)
    {
        lock (gate)
        {
            if (!active) return;
            int sideX = -1, sideY = -1;
            var mb = Screen.FromPoint(pos).Bounds;
            int tx = pos.X + dx;
            if (!OnAnyScreen(tx, pos.Y))
            {
                tx = Math.Max(mb.Left, Math.Min(mb.Right - 1, tx));
                sideX = dx < 0 ? Msg.Left : Msg.Right;
            }
            pos.X = tx;
            mb = Screen.FromPoint(pos).Bounds;
            int ty = pos.Y + dy;
            if (!OnAnyScreen(pos.X, ty))
            {
                ty = Math.Max(mb.Top, Math.Min(mb.Bottom - 1, ty));
                sideY = dy < 0 ? Msg.Top : Msg.Bottom;
            }
            pos.Y = ty;
            MoveAbs(pos);

            int side = -1;
            if (sideX >= 0 && sideY >= 0) side = Math.Abs(dx) >= Math.Abs(dy) ? sideX : sideY;
            else if (sideX >= 0) side = sideX;
            else if (sideY >= 0) side = sideY;
            if (side < 0) return;
            int now = Environment.TickCount;
            if (unchecked(now - lastEdge) < 100) return;   // 100ms에 한 번만
            lastEdge = now;
            var vs0 = SystemInformation.VirtualScreen;
            float f0 = (side == Msg.Left || side == Msg.Right) ? (pos.Y - vs0.Top) / (float)vs0.Height : (pos.X - vs0.Left) / (float)vs0.Width;
            if (ProbeEdge(l, (byte)side, Math.Max(0f, Math.Min(1f, f0)))) return;
            var vs = SystemInformation.VirtualScreen;
            float frac = (side == Msg.Left || side == Msg.Right)
                ? (pos.Y - vs.Top) / (float)vs.Height
                : (pos.X - vs.Left) / (float)vs.Width;
            frac = Math.Max(0f, Math.Min(1f, frac));
            l.SendSideFrac(Msg.Edge, (byte)side, frac);
        }
    }

    static void DoButton(int btn, bool down)
    {
        lock (gate)
        {
            if (!active && !(!down && btns.Contains(btn))) return;   // 뗌은 눌렀던 것만 허용
            uint f; int data = 0;
            switch (btn)
            {
                case 0: f = down ? 0x2u : 0x4u; break;
                case 1: f = down ? 0x8u : 0x10u; break;
                case 2: f = down ? 0x20u : 0x40u; break;
                case 3: f = down ? 0x80u : 0x100u; data = 1; break;
                case 4: f = down ? 0x80u : 0x100u; data = 2; break;
                default: return;
            }
            if (down) btns.Add(btn); else btns.Remove(btn);
            if (btn == 0 && down) probed = false;
            N.Mouse(f, data, 0, 0);
        }
    }

    static void DoWheel(bool horizontal, int delta)
    {
        lock (gate)
        {
            if (!active) return;
            N.Mouse(horizontal ? 0x1000u : 0x800u, delta, 0, 0);
        }
    }

    static void DoKey(ushort vk, ushort scan, byte flags)
    {
        bool up = (flags & 1) != 0, ext = (flags & 2) != 0;
        lock (gate)
        {
            if (up) { if (!active && !keys.ContainsKey(vk)) return; keys.Remove(vk); }
            else { if (!active) return; keys[vk] = ext; }
            N.Key(vk, scan, (up ? 0x2u : 0u) | (ext ? 0x1u : 0u));
        }
    }

    // gate 안에서 부른다.
    static void MoveAbs(Point p)
    {
        var vs = SystemInformation.VirtualScreen;
        long w = Math.Max(1, vs.Width - 1), h = Math.Max(1, vs.Height - 1);
        int ax = (int)(((long)(p.X - vs.Left) * 65535 + w / 2) / w);
        int ay = (int)(((long)(p.Y - vs.Top) * 65535 + h / 2) / h);
        N.Mouse(0x1u | 0x8000u | 0x4000u, 0, ax, ay);
        N.SetCursorPos(p.X, p.Y);   // 위치를 정확히 맞춘다
        overlayPos = p;
        PostOverlay();
    }

    // 마우스가 꽂혀 있지 않은 PC는 Windows가 커서를 숨긴다. 그때는 커서 모양 창을 대신 띄운다.
    // 이동이 잦으므로 화면 스레드에 한 번만 걸어 두고 마지막 위치로 옮긴다.
    static void PostOverlay()
    {
        if (Interlocked.Exchange(ref overlayPending, 1) == 0) Ui(UpdateOverlay);
    }

    static void UpdateOverlay()
    {
        Interlocked.Exchange(ref overlayPending, 0);
        bool show = active && !SystemInformation.MousePresent;
        if (!show) { if (overlay.Visible) overlay.Hide(); return; }
        if (!overlay.Visible) overlay.Show();
        overlay.SetShape(ShapeAt(overlayPos));
        var hs = overlay.Shape.HotSpot;
        // 옮길 때마다 맨 위로 다시 올린다. 다른 "항상 위" 창(세션 패널 등)에 가려지지 않게.
        N.SetWindowPos(overlay.Handle, new IntPtr(-1), overlayPos.X - hs.X, overlayPos.Y - hs.Y, 0, 0, 0x0001 | 0x0010 | 0x0040);   // NOSIZE | NOACTIVATE | SHOWWINDOW
    }

    // 마우스가 없으면 Windows가 커서 모양도 알려 주지 않는다(GetCursorInfo가 비어 있음).
    // 그래서 커서 아래 창에 어디를 가리키는지 물어서(WM_NCHITTEST) 창 가장자리면 크기 조절 모양을 고른다. 나머지는 화살표다.
    static int lastShapeTick;
    static Cursor lastShape = Cursors.Arrow;

    static Cursor ShapeAt(Point p)
    {
        int now = Environment.TickCount;
        if (unchecked(now - lastShapeTick) < 40) return lastShape;   // 자주 묻지 않는다
        lastShapeTick = now;
        var h = N.WindowFromPoint(new N.POINT { X = p.X, Y = p.Y });
        IntPtr r;
        if (h == IntPtr.Zero || N.SendMessageTimeout(h, 0x0084, IntPtr.Zero, new IntPtr((p.Y << 16) | (p.X & 0xFFFF)), 0x0002, 50, out r) == IntPtr.Zero)
            return lastShape = Cursors.Arrow;
        switch ((int)r.ToInt64())
        {
            case 10: case 11: return lastShape = Cursors.SizeWE;     // 왼쪽·오른쪽 테두리
            case 12: case 15: return lastShape = Cursors.SizeNS;     // 위·아래 테두리
            case 13: case 17: return lastShape = Cursors.SizeNWSE;   // 왼쪽 위·오른쪽 아래 모서리
            case 14: case 16: return lastShape = Cursors.SizeNESW;   // 오른쪽 위·왼쪽 아래 모서리
            default: return lastShape = Cursors.Arrow;
        }
    }

    // 눌린 키와 버튼을 모두 뗀다. gate 안에서 부른다.
    static void ReleaseAll()
    {
        try
        {
            foreach (var k in new List<KeyValuePair<ushort, bool>>(keys))
                N.Key(k.Key, 0, 0x2u | (k.Value ? 0x1u : 0u));
            foreach (var b in new List<int>(btns))
            {
                switch (b)
                {
                    case 0: N.Mouse(0x4u, 0, 0, 0); break;
                    case 1: N.Mouse(0x10u, 0, 0, 0); break;
                    case 2: N.Mouse(0x40u, 0, 0, 0); break;
                    case 3: N.Mouse(0x100u, 1, 0, 0); break;
                    case 4: N.Mouse(0x100u, 2, 0, 0); break;
                }
            }
        }
        catch (Exception e) { Log.Write("눌린 입력 떼기 실패: " + e.Message); }
        keys.Clear();
        btns.Clear();
    }

    // ---- SendInput ----

    static class N
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx; public int dy; public int mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion u; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint n, INPUT[] inputs, int size);

        [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
        static extern bool SetCursorPosLocal(int x, int y);

        public static bool SetCursorPos(int x, int y) { return AgentClient.SetCursor(x, y) || SetCursorPosLocal(x, y); }

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT p);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr wp, IntPtr lp, uint flags, uint ms, out IntPtr result);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public static void Mouse(uint flags, int data, int dx, int dy)
        {
            if (AgentClient.Mouse(flags, data, dx, dy)) return;
            var i = new INPUT();
            i.type = 0;
            i.u.mi.dx = dx; i.u.mi.dy = dy; i.u.mi.mouseData = data; i.u.mi.dwFlags = flags;
            Send(i);
        }

        public static void Key(ushort vk, ushort scan, uint flags)
        {
            if (AgentClient.Key(vk, scan, flags)) return;
            var i = new INPUT();
            i.type = 1;
            i.u.ki.wVk = vk; i.u.ki.wScan = scan; i.u.ki.dwFlags = flags;
            Send(i);
        }

        static void Send(INPUT i)
        {
            if (SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT))) == 0)
                Log.Write("SendInput 실패: " + Marshal.GetLastWin32Error());
        }
    }
}

// 커서 모양만 그리는 창. 클릭이 아래 창으로 그대로 지나가고 포커스도 가져가지 않는다.
class CursorOverlay : Form
{
    public CursorOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(32, 32);
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
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

    public Cursor Shape = Cursors.Arrow;

    public void SetShape(Cursor c)
    {
        if (c == Shape) return;
        Shape = c;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Shape.Draw(e.Graphics, new Rectangle(0, 0, 32, 32));
    }
}
