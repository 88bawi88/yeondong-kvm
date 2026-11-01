// 설정 창과 트레이 공통 메뉴.
// 설정 창: 안내·동의, 이 PC 이름과 역할, 짝 맺기(찾은 PC 목록·주소 직접 입력), 짝 맺은 PC 목록, 관리자 기능 설치.
// 설정 창이 열려 있는 동안만 다른 PC의 짝 맺기 요청을 받는다. 두 PC 화면에 같은 6자리 숫자가 뜨면 양쪽에서 [맞습니다]를 누른다.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;

static class Tray
{
    static Config cfg;
    static Conn conn;
    static SetupForm setup;

    public static Icon AppIcon = SystemIcons.Application;

    public static void Init(Config c, Conn cn) { cfg = c; conn = cn; }

    public static void OpenSetup()
    {
        if (setup != null && !setup.IsDisposed) { setup.Activate(); return; }
        setup = new SetupForm(cfg, conn);
        setup.Show();
    }

    // 트레이 메뉴 아래쪽 공통 항목
    public static void Common(ContextMenuStrip m, Config c, Conn cn, Action quit)
    {
        m.Items.Add("설정 (짝 맺기·관리자 기능)", null, delegate { OpenSetup(); });
        var clipItem = new ToolStripMenuItem("클립보드 공유 (글·그림)") { Checked = c.Clipboard };
        clipItem.Click += delegate { c.Clipboard = !c.Clipboard; try { c.Save(); } catch { } Log.Write("클립보드 공유 " + (c.Clipboard ? "켬" : "끔")); };
        m.Items.Add(clipItem);
        var fileItem = new ToolStripMenuItem("파일 끌어 놓기") { Checked = c.Files };
        fileItem.Click += delegate { c.Files = !c.Files; try { c.Save(); } catch { } Log.Write("파일 끌어 놓기 " + (c.Files ? "켬" : "끔")); };
        m.Items.Add(fileItem);
        if (c.Role == "sub")
        {
            var viewItem = new ToolStripMenuItem("메인 PC가 이 화면 보기 허용") { Checked = c.ScreenShare };
            viewItem.Click += delegate
            {
                c.ScreenShare = !c.ScreenShare; try { c.Save(); } catch { }
                if (!c.ScreenShare) ScreenShare.Stop(null);
                Log.Write("화면 보기 허용 " + (c.ScreenShare ? "켬" : "끔"));
            };
            m.Items.Add(viewItem);
        }
        var share = new ToolStripMenuItem(c.SharePath == "" ? "공유 폴더: 없음" : "공유 폴더: " + c.SharePath);
        share.DropDownItems.Add("폴더 정하기...", null, delegate
        {
            using (var d = new FolderBrowserDialog { Description = "짝 맺은 PC가 둘러보고 파일을 주고받을 폴더", SelectedPath = c.SharePath })
                if (d.ShowDialog() == DialogResult.OK) { c.SharePath = d.SelectedPath; try { c.Save(); } catch { } Log.Write("공유 폴더: " + c.SharePath); }
        });
        if (c.SharePath != "")
            share.DropDownItems.Add("공유 끄기", null, delegate { c.SharePath = ""; try { c.Save(); } catch { } Log.Write("공유 폴더 끔"); });
        m.Items.Add(share);
        m.Items.Add("로그 폴더 열기", null, delegate { Process.Start(new ProcessStartInfo(Program.Home) { UseShellExecute = true }); });
        m.Items.Add(new ToolStripMenuItem("연동 " + Program.Version) { Enabled = false });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("종료", null, delegate { quit(); });
    }
}

class SetupForm : Form
{
    static readonly Color Bg = ColorTranslator.FromHtml("#202020"), Panel2 = ColorTranslator.FromHtml("#2b2b2b"),
                          Line = ColorTranslator.FromHtml("#3a3a3a"), Dim = ColorTranslator.FromHtml("#a0a0a0"),
                          Accent = ColorTranslator.FromHtml("#0078d4"), Warn = ColorTranslator.FromHtml("#ffb74d");

    readonly Config cfg;
    readonly Conn conn;   // 처음 실행(역할을 정하기 전)에는 null
    TextBox name;
    RadioButton asMain, asSub;
    CheckBox consent;
    ListBox foundList, pairedList;
    TextBox addr;
    Label status, adminStatus;
    Button pairBtn, addrBtn, unpairBtn, adminBtn, saveBtn;
    System.Windows.Forms.Timer refresh;
    volatile bool busy;

    public SetupForm(Config c, Conn cn)
    {
        cfg = c;
        conn = cn;
        Text = "연동 설정";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Bg;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(600, 400);
        Icon = Tray.AppIcon;

        int y = 16;
        y = Section("안내", y);
        var info = Lbl("연동은 이 PC의 키보드·마우스 입력, 클립보드(글·그림), 끌어 놓은 파일을 짝 맺은 PC로 보냅니다. " +
                       "모두 암호화되어 같은 네트워크(사설 주소) 안에서만 오가고, 외부 서버로는 보내지 않습니다. " +
                       "인터넷 연결은 새 판 확인(GitHub)에만 씁니다.", 20, y, 560, 58);
        info.ForeColor = Dim;
        y += 62;
        consent = new CheckBox { Text = "위 내용을 확인했고 동의합니다", Left = 20, Top = y, Width = 400, Checked = cfg.Consent, ForeColor = Color.White };
        Controls.Add(consent);
        y += 40;

        y = Section("이 PC", y);
        Controls.Add(Lbl("이름", 20, y + 3, 60, 22));
        name = new TextBox { Left = 90, Top = y, Width = 260, Text = cfg.Name, BackColor = Panel2, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(name);
        y += 36;
        asMain = new RadioButton { Text = "메인 PC (키보드·마우스가 연결된 PC)", Left = 20, Top = y, Width = 520, Checked = cfg.Role == "main", ForeColor = Color.White };
        asSub = new RadioButton { Text = "서브 PC (메인 PC의 키보드·마우스로 조작할 PC)", Left = 20, Top = y + 28, Width = 520, Checked = cfg.Role == "sub", ForeColor = Color.White };
        Controls.Add(asMain); Controls.Add(asSub);
        y += 64;
        saveBtn = Btn(conn == null ? "저장하고 시작" : "저장", 20, y, 140, true);
        saveBtn.Click += delegate { Save(); };
        status = Lbl("", 176, y + 6, 400, 22);
        status.ForeColor = Warn;
        y += 50;

        if (conn != null)
        {
            y = Section("짝 맺기", y);
            var hint = Lbl(cfg.Role == "main"
                ? "서브 PC에서 연동 설정을 열고 이 PC를 고르면, 두 PC에 같은 숫자가 뜹니다. 이 창을 열어 둔 동안만 받습니다."
                : "같은 네트워크에서 찾은 메인 PC입니다. 고르고 [짝 맺기]를 누르세요. 메인 PC에서도 연동 설정 창을 열어 두어야 합니다.", 20, y, 560, 40);
            hint.ForeColor = Dim;
            y += 44;
            foundList = List(20, y, 400, 92);
            pairBtn = Btn("짝 맺기", 430, y, 150, true);
            pairBtn.Click += delegate { var f = foundList.SelectedItem as FoundItem; if (f != null) Pair(f.F.Ip, f.F.Port); };
            y += 100;
            Controls.Add(Lbl("주소 직접 입력", 20, y + 3, 110, 22));
            addr = new TextBox { Left = 134, Top = y, Width = 286, BackColor = Panel2, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(addr);
            addrBtn = Btn("이 주소로 짝 맺기", 430, y - 2, 150, false);
            addrBtn.Click += delegate { PairAddr(); };
            y += 44;

            y = Section("짝 맺은 PC", y);
            pairedList = List(20, y, 400, 70);
            unpairBtn = Btn("짝 풀기", 430, y, 150, false);
            unpairBtn.Click += delegate { Unpair(); };
            y += 82;

            y = Section("관리자 기능", y);
            var adm = Lbl("방화벽 허용, 관리자 권한 창·UAC 창·잠금 화면 조작(서브 PC), 자동 업데이트를 Windows 서비스로 켭니다. 관리자 권한이 필요합니다.", 20, y, 560, 40);
            adm.ForeColor = Dim;
            y += 44;
            adminBtn = Btn("", 20, y, 200, false);
            adminBtn.Click += delegate { ToggleAdmin(); };
            adminStatus = Lbl("", 232, y + 6, 350, 22);
            adminStatus.ForeColor = Dim;
            UpdateAdmin();
            y += 44;

            conn.Scan = true;
            if (cfg.Role != "") conn.PairConfirm = Confirm;
            refresh = new System.Windows.Forms.Timer { Interval = 1000 };
            refresh.Tick += delegate { RefreshLists(); };
            refresh.Start();
            RefreshLists();
        }
        ClientSize = new Size(600, y + 12);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (refresh != null) refresh.Stop();
        if (conn != null) { conn.Scan = false; conn.PairConfirm = null; }
        base.OnFormClosed(e);
    }

    // ---------- 화면 만들기 ----------
    int Section(string title, int y)
    {
        var l = new Label { Text = title, Left = 20, Top = y, AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), ForeColor = Color.White };
        Controls.Add(l);
        Controls.Add(new Label { Left = 20, Top = y + 26, Width = 560, Height = 1, BackColor = Line });
        return y + 36;
    }

    Label Lbl(string text, int x, int y, int w, int h)
    {
        var l = new Label { Text = text, Left = x, Top = y, Width = w, Height = h, ForeColor = Color.White };
        Controls.Add(l);
        return l;
    }

    Button Btn(string text, int x, int y, int w, bool primary)
    {
        var b = new Button { Text = text, Left = x, Top = y, Width = w, Height = 32, FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
                             BackColor = primary ? Accent : Panel2, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = primary ? Accent : Line;
        // 누를 수 없을 때는 회색으로(평면 단추는 흐려지지 않는다)
        b.EnabledChanged += delegate { b.BackColor = !b.Enabled ? Panel2 : primary ? Accent : Panel2; b.ForeColor = b.Enabled ? Color.White : Dim; };
        Controls.Add(b);
        return b;
    }

    ListBox List(int x, int y, int w, int h)
    {
        var l = new ListBox { Left = x, Top = y, Width = w, Height = h, BackColor = Panel2, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
        Controls.Add(l);
        return l;
    }

    class FoundItem
    {
        public Found F;
        public override string ToString() { return F.Name + "  (" + (F.Role == "main" ? "메인" : F.Role == "sub" ? "서브" : "역할 미정") + ", " + F.Ip + ")"; }
    }

    class PeerItem
    {
        public Machine M; public bool On;
        public override string ToString() { return M.Name + "  (" + (M.Role == "main" ? "메인" : "서브") + ", " + (On ? "연결됨" : "끊김") + ", " + M.Addr + ")"; }
    }

    void RefreshLists()
    {
        if (busy) return;
        var want = cfg.Role == "main" ? "sub" : "main";
        var f = conn.FoundList().Where(x => x.Role == want && cfg.FindPeer(x.Id) == null).ToList();
        Sync(foundList, f.Select(x => (object)new FoundItem { F = x }).ToList(), (a, b) => ((FoundItem)a).F.Id == ((FoundItem)b).F.Id);
        Sync(pairedList, cfg.Peers.ToList().Select(m => (object)new PeerItem { M = m, On = conn.LinkOf(m) != null }).ToList(),
             (a, b) => ((PeerItem)a).M == ((PeerItem)b).M && ((PeerItem)a).On == ((PeerItem)b).On);
        pairBtn.Enabled = cfg.Role != "" && foundList.SelectedItem != null;
        addrBtn.Enabled = cfg.Role != "";
        unpairBtn.Enabled = pairedList.SelectedItem != null;
    }

    // 고른 항목이 풀리지 않게, 달라졌을 때만 목록을 바꾼다
    static void Sync(ListBox box, System.Collections.Generic.List<object> items, Func<object, object, bool> same)
    {
        var cur = box.Items.Cast<object>().ToList();
        if (cur.Count == items.Count && cur.Zip(items, same).All(x => x)) return;
        int sel = box.SelectedIndex;
        box.BeginUpdate();
        box.Items.Clear();
        foreach (var i in items) box.Items.Add(i);
        if (sel >= 0 && sel < box.Items.Count) box.SelectedIndex = sel;
        box.EndUpdate();
    }

    // ---------- 저장 ----------
    void Save()
    {
        if (!consent.Checked) { status.Text = "안내에 동의해 주셔야 쓸 수 있습니다."; return; }
        if (!asMain.Checked && !asSub.Checked) { status.Text = "메인 PC인지 서브 PC인지 골라 주세요."; return; }
        var n = name.Text.Trim();
        if (n == "") { status.Text = "이름을 적어 주세요."; return; }
        var role = asMain.Checked ? "main" : "sub";
        bool restart = conn != null && (role != cfg.Role || n != cfg.Name);
        if (conn != null && role != cfg.Role && cfg.Peers.Count > 0)
        {
            if (MessageBox.Show(this, "역할을 바꾸면 짝 맺은 PC 목록을 지웁니다. 계속할까요?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            cfg.Peers.Clear();
        }
        cfg.Consent = true; cfg.Name = n; cfg.Role = role;
        try { cfg.Save(); }
        catch (Exception e) { status.Text = "저장하지 못했습니다: " + e.Message; return; }
        Log.Write("설정 저장: " + n + ", " + role);
        if (conn == null) { Close(); return; }
        if (restart) { Program.Relaunch(); return; }
        status.ForeColor = Dim;
        status.Text = "저장했습니다.";
    }

    // ---------- 짝 맺기 ----------
    // 다른 스레드에서 불린다. 사람이 답할 때까지 기다린다.
    bool Confirm(string sas, string peerName)
    {
        bool ok = false;
        Invoke((Action)(() =>
        {
            Activate();
            ok = MessageBox.Show(this, peerName + " 와(과) 짝을 맺습니다.\n\n두 PC 화면에 같은 숫자가 보이면 [예]를 누르세요.\n\n        " + sas + "\n\n숫자가 다르면 [아니요]를 누르세요(다른 PC가 끼어들었을 수 있습니다).",
                                 "연동 - 짝 맺기", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }));
        return ok;
    }

    void PairAddr()
    {
        IPAddress ip;
        var t = addr.Text.Trim();
        int port = cfg.Port;
        int c = t.LastIndexOf(':');
        if (c > 0 && int.TryParse(t.Substring(c + 1), out port)) t = t.Substring(0, c); else port = cfg.Port;
        if (!IPAddress.TryParse(t, out ip)) { MessageBox.Show(this, "IPv4 주소를 적어 주세요(예: 192.168.0.12).", Text); return; }
        Pair(ip, port);
    }

    void Pair(IPAddress ip, int port)
    {
        if (busy) return;
        busy = true;
        pairBtn.Enabled = addrBtn.Enabled = false;
        Cursor = Cursors.WaitCursor;
        new Thread(() =>
        {
            string msg;
            try { var m = conn.PairWith(ip, port, Confirm); msg = m.Name + " 와(과) 짝을 맺었습니다." + (cfg.Role == "main" ? "\n트레이 메뉴의 [모니터 배치 정하기]로 서브 모니터 자리를 정하세요." : ""); }
            catch (Exception e) { msg = "짝을 맺지 못했습니다.\n" + e.Message; Log.Write("짝 맺기 실패(" + ip + "): " + e.Message); }
            BeginInvoke((Action)(() => { busy = false; Cursor = Cursors.Default; MessageBox.Show(this, msg, Text); RefreshLists(); }));
        }) { IsBackground = true, Name = "pair" }.Start();
    }

    void Unpair()
    {
        var p = pairedList.SelectedItem as PeerItem;
        if (p == null) return;
        if (MessageBox.Show(this, p.M.Name + " 와(과) 짝을 풀까요? 다시 쓰려면 짝을 새로 맺어야 합니다.", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        conn.Unpair(p.M);
        RefreshLists();
        if (cfg.Role == "main") Program.Relaunch();   // 서브 목록을 새로 읽는다
    }

    // ---------- 관리자 기능 ----------
    void UpdateAdmin()
    {
        bool on = Elevated.IsServiceInstalled();
        adminBtn.Text = on ? "관리자 기능 제거" : "관리자 기능 설치";
        adminStatus.Text = on ? (Program.MachineWide ? "켜짐 (Program Files 설치본 실행 중)" : "설치됨, 설치본으로 다시 시작해야 합니다") : "꺼짐";
    }

    void ToggleAdmin()
    {
        bool on = Elevated.IsServiceInstalled();
        Cursor = Cursors.WaitCursor;
        bool ok = Elevated.RunElevated(on ? "--uninstall" : "--install");
        Cursor = Cursors.Default;
        if (!ok) { MessageBox.Show(this, "관리자 권한으로 실행하지 못했거나 중간에 실패했습니다.\n" + Path.Combine(SvcUtil.DataDir(), "install.log") + " 를 확인해 주세요.", Text); UpdateAdmin(); return; }
        if (!on)
        {
            // Program Files 설치본으로 갈아탄다(자동 시작도 그쪽으로 바뀐다)
            var exe = Path.Combine(Program.MachineDir, "link.exe");
            MessageBox.Show(this, "관리자 기능을 켰습니다. 설치본으로 다시 시작합니다.", Text);
            try { Process.Start(new ProcessStartInfo(exe, "--replace") { UseShellExecute = true }); } catch (Exception e) { Log.Write("설치본 실행 실패: " + e.Message); }
        }
        else
        {
            MessageBox.Show(this, "관리자 기능을 껐습니다. 사용자 설치본으로 다시 시작합니다.", Text);
            var exe = Path.Combine(Program.Home, "link.exe");
            if (!File.Exists(exe)) try { File.Copy(Program.ExePath, exe, true); } catch { }
            try { Process.Start(new ProcessStartInfo(exe, "--replace") { UseShellExecute = true }); } catch (Exception e) { Log.Write("사용자 설치본 실행 실패: " + e.Message); }
        }
        UpdateAdmin();
    }
}
