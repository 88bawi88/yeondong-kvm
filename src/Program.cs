// 연동: 메인 PC 키보드·마우스로 서브 PC를 조작하고, 클립보드(글·그림)를 맞추고, 파일을 끌어 놓아 옮기는 프로그램.
// 처음 실행하면 설정 창에서 역할(메인/서브)을 고르고, 같은 네트워크의 상대 PC와 6자리 숫자로 짝을 맺는다.
//   link.exe                 실행(이미 돌고 있으면 설정 창을 띄운다). 내려받은 exe를 실행하면 %LOCALAPPDATA%\Yeondong\으로 복사해 그쪽을 띄운다
//   link.exe --install       (관리자) 관리자 기능 설치: Program Files로 복사, 입력 도우미 서비스, 방화벽 허용 (Service.cs)
//   link.exe --uninstall     (관리자) 관리자 기능 제거
//   link.exe --firewall <exe>  (관리자) 그 exe의 방화벽 허용만 넣는다
//   link.exe --service / --agent   (내부용) 서비스와 SYSTEM 입력 도우미
//   link.exe --profile <이름>  (시험용) 설정·인증서·로그를 따로 두고 한 PC에서 둘을 띄운다
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("연동")]
[assembly: System.Reflection.AssemblyProduct("Yeondong")]
[assembly: System.Reflection.AssemblyVersion("0.7.1.0")]

static class Program
{
    public static string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yeondong");
    public static readonly string ExePath = Process.GetCurrentProcess().MainModule.FileName;
    public static readonly string Version = typeof(Program).Assembly.GetName().Version.ToString();
    public static readonly string MachineDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Yeondong");
    public static bool MachineWide { get { return string.Equals(Path.GetDirectoryName(ExePath), MachineDir, StringComparison.OrdinalIgnoreCase); } }
    public static Control Ui;   // 화면 스레드로 넘길 때 쓰는 창(MainSide 또는 Sub의 숨은 창)
    public static string Profile = "";
    // 이름에 2를 붙여 예전 개인용 판(yeondong-run 등)과 겹치지 않게 한다
    static string QuitEvent { get { return "Local\\Yeondong2-quit" + Profile; } }
    static string ShowEvent { get { return "Local\\Yeondong2-show" + Profile; } }
    static string Single { get { return "Local\\Yeondong2-run" + Profile; } }

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);

    [STAThread]
    static int Main(string[] args)
    {
        try { if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware(); }   // 모니터별 DPI 인식
        catch { SetProcessDPIAware(); }

        if (args.Contains("--service")) { WatchSelf(() => Environment.Exit(1)); AgentService.RunService(); return 0; }   // 바뀌면 서비스 관리자가 다시 띄운다
        if (args.Contains("--agent")) { WatchSelf(() => Environment.Exit(0)); Agent.Run(); return 0; }
        if (args.Contains("--install")) { SvcUtil.SetLog("install.log"); return Elevated.Install(ExePath); }
        if (args.Contains("--uninstall")) { SvcUtil.SetLog("install.log"); return Elevated.Uninstall(); }
        int fi = Array.IndexOf(args, "--firewall");
        if (fi >= 0 && fi + 1 < args.Length) { SvcUtil.SetLog("install.log"); return Elevated.AddFirewall(args[fi + 1]); }

        int pi = Array.IndexOf(args, "--profile");
        if (pi >= 0 && pi + 1 < args.Length)
        {
            Profile = args[pi + 1];
            Home = Path.Combine(Home, Profile);
            Config.Dir = Path.Combine(Config.Dir, Profile);
        }
        Directory.CreateDirectory(Home);
        Log.Path = Path.Combine(Home, "link.log");
        Application.EnableVisualStyles();

        // 내려받은 곳에서 실행했으면 사용자 폴더로 복사해 그쪽을 띄운다(자동 업데이트가 그 파일을 바꾼다)
        var home = Path.Combine(Home, "link.exe");
        if (!MachineWide && Profile == "" && !string.Equals(ExePath, home, StringComparison.OrdinalIgnoreCase))
            return CopyAndRun(home, args);

        bool first;
        using (var single = new Mutex(true, Single, out first))
        {
            if (!first)
            {
                if (!args.Contains("--replace")) { Signal(ShowEvent); return 0; }   // 이미 돌고 있으면 그쪽 설정 창을 띄운다
                Signal(QuitEvent);
                try { first = single.WaitOne(10000); } catch (AbandonedMutexException) { first = true; }
                if (!first) return 1;
            }
            try { return Run(args); }
            finally { try { single.ReleaseMutex(); } catch { } }
        }
    }

    static int Run(string[] args)
    {
        var cfg = Config.Load();
        Share.Cfg = cfg;
        try { Identity.Load(cfg.Id); }
        catch (Exception e) { MessageBox.Show("인증서를 만들지 못했습니다: " + e.Message, "연동"); return 1; }
        cfg.Save();
        Log.Write("시작 " + Version + ", " + cfg.Name + " 역할 " + (cfg.Role == "" ? "미정" : cfg.Role) + (MachineWide ? ", 관리자 설치본" : ""));

        bool openSetup = false;
        if (cfg.Role == "" || !cfg.Consent)
        {
            Application.Run(new SetupForm(cfg, null));
            if (cfg.Role == "" || !cfg.Consent) return 0;
            openSetup = true;
        }

        AutoStart();
        AgentClient.Enabled = MachineWide && Elevated.IsServiceInstalled();
        if (!MachineWide && Profile == "") Updater.Start();   // 관리자 설치본은 서비스가 업데이트한다
        WatchSelf(Relaunch);
        WatchEvents();

        var conn = new Conn(cfg);
        Tray.Init(cfg, conn);
        if (openSetup)
        {
            // 처음 실행: 역할을 정한 뒤 짝 맺기를 하도록 설정 창을 다시 띄운다(화면 스레드가 돌기 시작하면)
            var t = new System.Windows.Forms.Timer { Interval = 500 };
            t.Tick += delegate { t.Stop(); t.Dispose(); Tray.OpenSetup(); };
            t.Start();
        }
        if (cfg.Role == "main") MainSide.Run(cfg, conn); else Sub.Run(cfg, conn);
        Log.Write("종료");
        return 0;
    }

    // 실행 중인 다른 복사본을 끄고 dst로 복사해 띄운다
    static int CopyAndRun(string dst, string[] args)
    {
        Signal(QuitEvent);
        for (int i = 0; i < 50 && !MutexFree(); i++) Thread.Sleep(100);
        for (int i = 0; ; i++)
        {
            try
            {
                if (File.Exists(dst))
                {
                    var old = dst + ".old";
                    try { if (File.Exists(old)) File.Delete(old); } catch { old = dst + ".old-" + DateTime.Now.Ticks; }
                    File.Move(dst, old);
                }
                File.Copy(ExePath, dst, true);
                break;
            }
            catch (IOException) { if (i >= 30) throw; Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { if (i >= 30) throw; Thread.Sleep(200); }
        }
        Process.Start(new ProcessStartInfo(dst, string.Join(" ", args.Select(a => "\"" + a + "\"").ToArray())) { UseShellExecute = true });
        return 0;
    }

    static void Signal(string name)
    {
        try { using (var e = EventWaitHandle.OpenExisting(name)) e.Set(); } catch { }
    }

    static bool MutexFree()
    {
        using (var m = new Mutex(false, Single))
        {
            try { if (!m.WaitOne(0)) return false; }
            catch (AbandonedMutexException) { }
            m.ReleaseMutex();
            return true;
        }
    }

    // 로그인할 때 이 exe가 뜨게 등록한다.
    static void AutoStart()
    {
        if (Profile != "") return;
        try
        {
            var cmd = "\"" + ExePath + "\"";
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                if ((k.GetValue("Yeondong") as string) != cmd) { k.SetValue("Yeondong", cmd); Log.Write("로그인 자동 시작 등록: " + cmd); }
        }
        catch (Exception e) { Log.Write("자동 시작 등록 실패: " + e.Message); }
    }

    // 종료 신호(새 복사본이 뜰 때)와 설정 창 열기 신호를 기다린다.
    static void WatchEvents()
    {
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEvent);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        new Thread(() =>
        {
            while (true)
            {
                int i = WaitHandle.WaitAny(new WaitHandle[] { quit, show });
                if (i == 0) { Exit(); return; }
                if (Ui != null && Ui.IsHandleCreated) Ui.BeginInvoke((Action)(() => Tray.OpenSetup()));
            }
        }) { IsBackground = true, Name = "events" }.Start();
    }

    // 이 exe 파일이 바뀌면(업데이트) onChange를 부른다. 서브 PC 조작 중에는 기다린다.
    static void WatchSelf(Action onChange)
    {
        var stamp = File.GetLastWriteTimeUtc(ExePath);
        new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(5000);
                try
                {
                    if (!File.Exists(ExePath) || File.GetLastWriteTimeUtc(ExePath) == stamp) continue;
                    if (MainSide.Busy) continue;
                    Thread.Sleep(2000);   // 쓰기가 끝나기를 잠깐 기다린다
                    Log.Write("새 판으로 바뀐 것을 찾음");
                    onChange();
                    return;
                }
                catch (Exception e) { Log.Write("새 판 확인 실패: " + e.Message); }
            }
        }) { IsBackground = true, Name = "watch-self" }.Start();
    }

    // 같은 경로의 새 exe로 다시 띄운다(새 프로세스는 이 프로세스가 끝나기를 기다린다).
    public static void Relaunch()
    {
        try { Process.Start(new ProcessStartInfo(ExePath, "--replace" + (Profile != "" ? " --profile \"" + Profile + "\"" : "")) { UseShellExecute = true }); }
        catch (Exception e) { Log.Write("다시 띄우기 실패: " + e.Message); return; }
        Exit();
    }

    static void Exit()
    {
        if (Ui != null && Ui.IsHandleCreated) Ui.BeginInvoke((Action)Application.Exit);
        else Application.Exit();
    }

    public static string LayoutText(Config cfg)
    {
        var mons = Config.Monitors();
        var sb = new StringBuilder();
        sb.AppendLine("이 PC: " + cfg.Name + " (연동 " + Version + ")");
        int i = 1;
        foreach (var s in Screen.AllScreens)
            sb.AppendLine("모니터 " + (i++) + " " + s.DeviceName + " " + MainSide.R(s.Bounds) + (s.Primary ? " 주 모니터" : ""));
        var done = new System.Collections.Generic.List<System.Drawing.Rectangle>();
        foreach (var m in cfg.Subs)
        {
            var z = Config.ZoneOf(m, mons, done);
            if (!z.IsEmpty) done.Add(z);
            sb.AppendLine("서브 " + m.Name + ": " + (z.IsEmpty ? "자리 없음" : MainSide.R(z)) + (m.ZoneRect == null ? " [자동]" : ""));
        }
        sb.AppendLine("좌표는 [x, y, 너비, 높이] 실제 픽셀입니다.");
        Log.Write(sb.ToString().Replace("\r\n", " | "));
        return sb.ToString();
    }
}
