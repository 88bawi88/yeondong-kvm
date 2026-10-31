// 선택 설치 윈도우 서비스. 일반 사용자 권한 프로세스는 관리자 창, UAC 창, 잠금 화면에 입력을 넣을 수 없다.
// 서비스(SYSTEM)가 사용자 세션에 도우미(--agent)를 SYSTEM으로 띄우고, 도우미가 입력 데스크톱에서 SendInput을 한다.
// 앱은 이름 있는 파이프로 입력을 보내고, 파이프가 없으면 자기 SendInput으로 되돌아간다.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;

// ---- 공통 ----

static class SvcUtil
{
    public static string Exe()
    {
        return Process.GetCurrentProcess().MainModule.FileName;
    }

    public static string DataDir()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Yeondong");
    }

    public static void SetLog(string name)
    {
        try
        {
            var d = DataDir();
            Directory.CreateDirectory(d);
            Log.Path = Path.Combine(d, name);
        }
        catch { }
    }

    public static string PipeName(int session)
    {
        return "yeondong-agent-" + session;
    }

    // 같은 경로인지(대소문자 무시, 전체 경로)
    public static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // 프로세스 id → 실행 파일 전체 경로(실패하면 null)
    public static string ImagePath(uint pid)
    {
        IntPtr h = SvcNative.OpenProcess(SvcNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint n = (uint)sb.Capacity;
            if (!SvcNative.QueryFullProcessImageName(h, 0, sb, ref n)) return null;
            return sb.ToString();
        }
        finally { SvcNative.CloseHandle(h); }
    }
}

static class SvcNative
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_ALL_ACCESS = 0xF01FF;
    public const uint MAXIMUM_ALLOWED = 0x02000000;
    public const uint GENERIC_ALL = 0x10000000;
    public const int TokenSessionId = 12;
    public const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx; public int dy; public int mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion u; }

    [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int impersonationLevel, int tokenType, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool SetTokenInformation(IntPtr token, int cls, ref uint info, int len);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessAsUser(IntPtr token, string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr d);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetThreadDesktop(IntPtr d);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    public static extern bool GetUserObjectInformation(IntPtr h, int index, byte[] info, uint len, out uint needed);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetCursorPos(int x, int y);
}

// ---- 서비스 ----

class AgentService : ServiceBase
{
    readonly object gate = new object();
    readonly ManualResetEvent stop = new ManualResetEvent(false);
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    Thread worker;
    IntPtr hProc = IntPtr.Zero;      // 도우미 프로세스 핸들
    uint procSession;                // 도우미를 띄운 세션
    long lastLaunch;                 // 마지막으로 띄운 시각(ms)

    public AgentService()
    {
        ServiceName = "YeondongAgent";
        CanHandleSessionChangeEvent = true;
        CanStop = true;
        AutoLog = false;
    }

    public static void RunService()
    {
        SvcUtil.SetLog("agent.log");
        ServiceBase.Run(new AgentService());
    }

    protected override void OnStart(string[] args)
    {
        Log.Write("서비스 시작");
        try { Updater.Start(); } catch (Exception e) { Log.Write("업데이터 시작 실패: " + e.Message); }
        worker = new Thread(Loop) { IsBackground = true, Name = "agent-watch" };
        worker.Start();
    }

    protected override void OnStop()
    {
        Log.Write("서비스 중지");
        stop.Set();
        wake.Set();
        try { if (worker != null) worker.Join(5000); } catch { }
        lock (gate) { Kill(); }
    }

    protected override void OnSessionChange(SessionChangeDescription d)
    {
        Log.Write("세션 변경: " + d.Reason + " #" + d.SessionId);
        lock (gate) { Kill(); }   // 다음 점검에서 활성 콘솔 세션에 다시 띄운다
        wake.Set();
    }

    void Loop()
    {
        var sw = Stopwatch.StartNew();
        while (!stop.WaitOne(0))
        {
            try
            {
                lock (gate) { Ensure(sw.ElapsedMilliseconds); }
            }
            catch (Exception e) { Log.Write("점검 오류: " + e.Message); }
            WaitHandle.WaitAny(new WaitHandle[] { stop, wake }, 2000);
        }
    }

    // gate 안에서 부른다
    void Ensure(long now)
    {
        uint sid = SvcNative.WTSGetActiveConsoleSessionId();
        if (sid == 0xFFFFFFFF) return;

        if (hProc != IntPtr.Zero)
        {
            if (SvcNative.WaitForSingleObject(hProc, 0) == 0)
            {
                Log.Write("도우미 종료됨");
                SvcNative.CloseHandle(hProc); hProc = IntPtr.Zero;
            }
            else if (procSession != sid) Kill();
        }
        if (hProc != IntPtr.Zero) return;
        if (lastLaunch != 0 && now - lastLaunch < 3000) return;   // 곧바로 죽는 경우 연속 실행을 막는다
        lastLaunch = now;
        Launch(sid);
    }

    void Kill()
    {
        if (hProc == IntPtr.Zero) return;
        SvcNative.TerminateProcess(hProc, 0);
        SvcNative.CloseHandle(hProc);
        hProc = IntPtr.Zero;
    }

    // 서비스 토큰을 복제해 세션 번호만 바꾸고, 그 세션의 기본 데스크톱에 SYSTEM으로 띄운다
    void Launch(uint sid)
    {
        IntPtr tok = IntPtr.Zero, dup = IntPtr.Zero;
        try
        {
            if (!SvcNative.OpenProcessToken(SvcNative.GetCurrentProcess(), SvcNative.TOKEN_ALL_ACCESS, out tok))
            { Log.Write("OpenProcessToken 실패: " + Marshal.GetLastWin32Error()); return; }
            if (!SvcNative.DuplicateTokenEx(tok, SvcNative.MAXIMUM_ALLOWED, IntPtr.Zero, 2, 1, out dup))
            { Log.Write("DuplicateTokenEx 실패: " + Marshal.GetLastWin32Error()); return; }
            uint s = sid;
            if (!SvcNative.SetTokenInformation(dup, SvcNative.TokenSessionId, ref s, 4))
            { Log.Write("SetTokenInformation 실패: " + Marshal.GetLastWin32Error()); return; }

            string exe = SvcUtil.Exe();
            var si = new SvcNative.STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(SvcNative.STARTUPINFO));
            si.lpDesktop = "winsta0\\default";
            SvcNative.PROCESS_INFORMATION pi;
            var cmd = new StringBuilder("\"" + exe + "\" --agent");
            if (!SvcNative.CreateProcessAsUser(dup, null, cmd, IntPtr.Zero, IntPtr.Zero, false, SvcNative.CREATE_NO_WINDOW,
                IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out pi))
            { Log.Write("CreateProcessAsUser 실패: " + Marshal.GetLastWin32Error()); return; }
            SvcNative.CloseHandle(pi.hThread);
            hProc = pi.hProcess;
            procSession = sid;
            Log.Write("도우미 시작: pid " + pi.dwProcessId + ", 세션 " + sid);
        }
        finally
        {
            if (dup != IntPtr.Zero) SvcNative.CloseHandle(dup);
            if (tok != IntPtr.Zero) SvcNative.CloseHandle(tok);
        }
    }
}

// ---- 도우미(--agent) ----

static class Agent
{
    static readonly Dictionary<string, long> lastLog = new Dictionary<string, long>();
    static Mutex single;
    static string self;
    static IntPtr curDesk = IntPtr.Zero;   // 지금 스레드가 쓰는 데스크톱
    static string curName;

    // 같은 내용 로그는 10초에 한 번만
    static void LogLim(string s)
    {
        long now = Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000);
        long last;
        if (lastLog.TryGetValue(s, out last) && now - last < 10000) return;
        lastLog[s] = now;
        Log.Write(s);
    }

    public static void Run()
    {
        SvcUtil.SetLog("agent.log");
        self = SvcUtil.Exe();
        int sid = Process.GetCurrentProcess().SessionId;
        bool created;
        single = new Mutex(true, "Local\\yeondong-agent-" + sid, out created);
        if (!created) { Log.Write("도우미가 이미 실행 중이라 종료"); return; }
        Log.Write("도우미 시작: 세션 " + sid + ", " + self);

        // 창을 만들지 않는 스레드 하나가 파이프 읽기와 입력 넣기를 모두 한다
        var t = new Thread(delegate() { Loop(sid); }) { Name = "agent-inject" };
        t.Start();
        t.Join();
    }

    static void Loop(int sid)
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

        NamedPipeServerStream srv = null;
        while (true)
        {
            try
            {
                // 인스턴스 하나를 계속 쓴다(끊고 다시 기다림). 새 인스턴스는 만들지 않는다.
                if (srv == null)
                    srv = new NamedPipeServerStream(SvcUtil.PipeName(sid), PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None, 4096, 0, ps);
                srv.WaitForConnection();
                if (Verify(srv)) Serve(srv);
            }
            catch (Exception e)
            {
                LogLim("파이프 오류: " + e.Message);
                try { if (srv != null) srv.Dispose(); } catch { }
                srv = null;
                Thread.Sleep(500);
                continue;
            }
            // 클라이언트가 먼저 닫아도 Disconnect를 해야 같은 인스턴스를 다시 기다릴 수 있다. 안 되면 새로 만든다.
            try { srv.Disconnect(); }
            catch { try { srv.Dispose(); } catch { } srv = null; }
        }
    }

    // 접속한 프로세스의 실행 파일이 도우미와 같은 파일일 때만 받는다
    static bool Verify(NamedPipeServerStream s)
    {
        uint pid;
        if (!SvcNative.GetNamedPipeClientProcessId(s.SafePipeHandle.DangerousGetHandle(), out pid))
        { LogLim("접속 거부: 클라이언트 pid 확인 실패 " + Marshal.GetLastWin32Error()); return false; }
        string img = SvcUtil.ImagePath(pid);
        if (img == null) { LogLim("접속 거부: 경로 확인 실패 pid " + pid); return false; }
        if (!SvcUtil.SamePath(img, self)) { LogLim("접속 거부: 다른 실행 파일 " + img + " (pid " + pid + ")"); return false; }
        return true;
    }

    static bool ReadExact(Stream s, byte[] b, int n)
    {
        int got = 0;
        while (got < n)
        {
            int r;
            try { r = s.Read(b, got, n - got); }
            catch (IOException) { return false; }   // 클라이언트가 닫으면 파이프가 끊어졌다는 예외가 난다
            if (r <= 0) return false;
            got += r;
        }
        return true;
    }

    static void Serve(Stream s)
    {
        var b = new byte[16];
        while (ReadExact(s, b, 1))
        {
            switch (b[0])
            {
                case 1:   // 마우스 [flags u32][data i32][dx i32][dy i32]
                    if (!ReadExact(s, b, 16)) return;
                    DoMouse(BitConverter.ToUInt32(b, 0), BitConverter.ToInt32(b, 4), BitConverter.ToInt32(b, 8), BitConverter.ToInt32(b, 12));
                    break;
                case 2:   // 키 [vk u16][scan u16][flags u32]
                    if (!ReadExact(s, b, 8)) return;
                    DoKey(BitConverter.ToUInt16(b, 0), BitConverter.ToUInt16(b, 2), BitConverter.ToUInt32(b, 4));
                    break;
                case 3:   // 커서 위치 [x i32][y i32]
                    if (!ReadExact(s, b, 8)) return;
                    DoCursor(BitConverter.ToInt32(b, 0), BitConverter.ToInt32(b, 4));
                    break;
                default:
                    LogLim("알 수 없는 메시지 종류 " + b[0] + ", 연결을 끊음");
                    return;
            }
        }
    }

    // 입력이 가는 데스크톱(UAC, 잠금 화면 포함)이 바뀌었으면 스레드를 거기로 옮긴다
    static void EnsureDesktop()
    {
        IntPtr d = SvcNative.OpenInputDesktop(0, false, SvcNative.GENERIC_ALL);
        if (d == IntPtr.Zero) { LogLim("OpenInputDesktop 실패: " + Marshal.GetLastWin32Error()); return; }
        string n = DeskName(d);
        if (n != null && n == curName) { SvcNative.CloseDesktop(d); return; }
        if (SvcNative.SetThreadDesktop(d))
        {
            if (curDesk != IntPtr.Zero) SvcNative.CloseDesktop(curDesk);
            curDesk = d;
            curName = n;
            Log.Write("입력 데스크톱: " + n);
        }
        else
        {
            LogLim("SetThreadDesktop 실패: " + Marshal.GetLastWin32Error() + " (" + n + ")");
            SvcNative.CloseDesktop(d);
        }
    }

    static string DeskName(IntPtr d)
    {
        var buf = new byte[512];
        uint need;
        if (!SvcNative.GetUserObjectInformation(d, 2, buf, (uint)buf.Length, out need)) return null;   // UOI_NAME
        int len = 0;
        while (len + 1 < buf.Length && (buf[len] != 0 || buf[len + 1] != 0)) len += 2;
        return Encoding.Unicode.GetString(buf, 0, len);
    }

    static void Send(SvcNative.INPUT i)
    {
        if (SvcNative.SendInput(1, new[] { i }, Marshal.SizeOf(typeof(SvcNative.INPUT))) == 0)
            LogLim("SendInput 실패: " + Marshal.GetLastWin32Error());
    }

    static void DoMouse(uint flags, int data, int dx, int dy)
    {
        EnsureDesktop();
        var i = new SvcNative.INPUT();
        i.type = 0;
        i.u.mi.dx = dx; i.u.mi.dy = dy; i.u.mi.mouseData = data; i.u.mi.dwFlags = flags;
        Send(i);
    }

    static void DoKey(ushort vk, ushort scan, uint flags)
    {
        EnsureDesktop();
        var i = new SvcNative.INPUT();
        i.type = 1;
        i.u.ki.wVk = vk; i.u.ki.wScan = scan; i.u.ki.dwFlags = flags;
        Send(i);
    }

    static void DoCursor(int x, int y)
    {
        EnsureDesktop();
        if (!SvcNative.SetCursorPos(x, y)) LogLim("SetCursorPos 실패: " + Marshal.GetLastWin32Error());
    }
}

// ---- 앱 쪽 클라이언트 ----

static class AgentClient
{
    static readonly object gate = new object();
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static NamedPipeClientStream pipe;
    static long nextTry;   // 이 시각(ms)까지는 다시 접속하지 않는다

    // false면 도우미에 못 보냈다는 뜻이다. 호출한 쪽이 자기 SendInput으로 넣는다.
    public static bool Mouse(uint flags, int data, int dx, int dy)
    {
        var b = new byte[17];
        b[0] = 1;
        Put(b, 1, flags); Put(b, 5, (uint)data); Put(b, 9, (uint)dx); Put(b, 13, (uint)dy);
        return Write(b);
    }

    public static bool Key(ushort vk, ushort scan, uint flags)
    {
        var b = new byte[9];
        b[0] = 2;
        b[1] = (byte)vk; b[2] = (byte)(vk >> 8);
        b[3] = (byte)scan; b[4] = (byte)(scan >> 8);
        Put(b, 5, flags);
        return Write(b);
    }

    public static bool SetCursor(int x, int y)
    {
        var b = new byte[9];
        b[0] = 3;
        Put(b, 1, (uint)x); Put(b, 5, (uint)y);
        return Write(b);
    }

    static void Put(byte[] b, int o, uint v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }

    // 관리자 기능이 설치돼 있고 이 앱이 그 설치본일 때만 켠다(Program.cs가 시작할 때 정한다).
    public static volatile bool Enabled;
    public static bool Available { get { return pipe != null; } }
    static int connecting;

    static bool Write(byte[] msg)
    {
        if (!Enabled) return false;
        if (pipe == null)
        {
            // 입력 경로를 막지 않게 접속은 따로 한다. 그동안 들어온 입력은 앱이 직접 넣는다
            if (Interlocked.Exchange(ref connecting, 1) == 0)
                new Thread(() => { lock (gate) { if (pipe == null) Connect(); } Interlocked.Exchange(ref connecting, 0); }) { IsBackground = true }.Start();
            return false;
        }
        lock (gate)
        {
            if (pipe == null) return false;
            try
            {
                pipe.Write(msg, 0, msg.Length);
                return true;
            }
            catch
            {
                Drop();
                return false;
            }
        }
    }

    // gate 안에서 부른다. 최대 약 100ms.
    static bool Connect()
    {
        if (clock.ElapsedMilliseconds < nextTry) return false;
        NamedPipeClientStream p = null;
        try
        {
            p = new NamedPipeClientStream(".", SvcUtil.PipeName(Process.GetCurrentProcess().SessionId), PipeDirection.Out, PipeOptions.None);
            p.Connect(100);
            // 파이프를 먼저 만들어 둔 가짜 서버에 입력을 보내지 않도록 서버도 같은 실행 파일인지 본다
            uint spid;
            if (!SvcNative.GetNamedPipeServerProcessId(p.SafePipeHandle.DangerousGetHandle(), out spid)) throw new IOException("서버 pid 확인 실패");
            string img = SvcUtil.ImagePath(spid);
            if (img == null || !SvcUtil.SamePath(img, SvcUtil.Exe())) throw new IOException("서버 실행 파일이 다름: " + img);
            pipe = p;
            return true;
        }
        catch (Exception e)
        {
            try { if (p != null) p.Dispose(); } catch { }
            nextTry = clock.ElapsedMilliseconds + 5000;
            if (!(e is System.TimeoutException)) Log.Write("도우미 접속 실패: " + e.Message);
            return false;
        }
    }

    static void Drop()
    {
        try { if (pipe != null) pipe.Dispose(); } catch { }
        pipe = null;
    }
}

// ---- 설치/제거(관리자 권한으로 뜬 link.exe --install 등) ----

static class Elevated
{
    const string SvcName = "YeondongAgent";
    const string FwName = "연동 (Yeondong)";

    static string Dir()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Yeondong");
    }

    public static bool IsAdmin()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    public static bool IsServiceInstalled()
    {
        try
        {
            foreach (var s in ServiceController.GetServices())
            {
                try { if (string.Equals(s.ServiceName, SvcName, StringComparison.OrdinalIgnoreCase)) return true; }
                finally { s.Dispose(); }
            }
        }
        catch { }
        return false;
    }

    // 현재 실행 파일을 관리자 권한으로 다시 띄우고 끝날 때까지 기다린다. UAC를 취소하면 false.
    public static bool RunElevated(string args)
    {
        try
        {
            var psi = new ProcessStartInfo(SvcUtil.Exe(), args);
            psi.Verb = "runas";
            psi.UseShellExecute = true;
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode == 0;
            }
        }
        catch (Win32Exception e)
        {
            if (e.NativeErrorCode != 1223) Log.Write("관리자 권한 실행 실패: " + e.Message);
            return false;
        }
        catch (Exception e) { Log.Write("관리자 권한 실행 실패: " + e.Message); return false; }
    }

    // 명령을 실행해 종료 코드를 돌려주고 한 줄 로그를 남긴다
    static int Exec(string file, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (var p = Process.Start(psi))
            {
                p.OutputDataReceived += delegate { };
                p.ErrorDataReceived += delegate { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } Log.Write(file + " " + args + " -> 시간 초과"); return -1; }
                p.WaitForExit();
                Log.Write(file + " " + args + " -> " + p.ExitCode);
                return p.ExitCode;
            }
        }
        catch (Exception e) { Log.Write(file + " " + args + " -> 예외 " + e.Message); return -2; }
    }

    static string Sc { get { return Path.Combine(Environment.SystemDirectory, "sc.exe"); } }
    static string Netsh { get { return Path.Combine(Environment.SystemDirectory, "netsh.exe"); } }

    static void StopService()
    {
        if (!IsServiceInstalled()) return;
        Exec(Sc, "stop " + SvcName, 15000);
        try
        {
            using (var c = new ServiceController(SvcName))
                c.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
        }
        catch (Exception e) { Log.Write("서비스 중지 대기: " + e.Message); }
    }

    public static int Install(string srcExe)
    {
        SvcUtil.SetLog("install.log");
        try
        {
            if (!IsAdmin()) { Log.Write("설치 실패: 관리자 권한이 아님"); return 10; }
            string dir = Dir();
            string dst = Path.Combine(dir, "link.exe");
            Log.Write("설치 시작: " + srcExe + " -> " + dst);

            bool existed = IsServiceInstalled();
            StopService();

            Directory.CreateDirectory(dir);
            if (!SvcUtil.SamePath(srcExe, dst))
            {
                try { File.Copy(srcExe, dst, true); }
                catch (IOException)
                {
                    // 잠겨 있으면 옛 파일을 이름만 바꿔 비키고 복사한다
                    File.Move(dst, Path.Combine(dir, "link.old-" + DateTime.Now.Ticks + ".exe"));
                    File.Copy(srcExe, dst, true);
                }
                catch (UnauthorizedAccessException)
                {
                    File.Move(dst, Path.Combine(dir, "link.old-" + DateTime.Now.Ticks + ".exe"));
                    File.Copy(srcExe, dst, true);
                }
            }
            Log.Write("파일 복사됨");

            string bin = "binPath= \"\\\"" + dst + "\\\" --service\" start= auto DisplayName= \"연동 입력 도우미\"";
            int rc = Exec(Sc, (existed ? "config " : "create ") + SvcName + " " + bin, 15000);
            if (rc != 0) return 11;
            Exec(Sc, "description " + SvcName + " \"연동의 키보드, 마우스 입력을 관리자 창과 잠금 화면에도 넣어 주는 도우미\"", 15000);
            Exec(Sc, "failure " + SvcName + " reset= 86400 actions= restart/5000/restart/5000/restart/5000", 15000);
            rc = Exec(Sc, "start " + SvcName, 30000);
            if (rc != 0 && rc != 1056) return 12;   // 1056: 이미 실행 중

            if (AddFirewall(dst) != 0) return 13;
            Log.Write("설치 완료");
            return 0;
        }
        catch (Exception e) { Log.Write("설치 예외: " + e); return 19; }
    }

    public static int Uninstall()
    {
        SvcUtil.SetLog("install.log");
        try
        {
            if (!IsAdmin()) { Log.Write("제거 실패: 관리자 권한이 아님"); return 10; }
            StopService();
            int rc = 0;
            if (IsServiceInstalled())
            {
                rc = Exec(Sc, "delete " + SvcName, 15000);
                if (rc != 0 && rc != 1060) rc = 21; else rc = 0;
            }
            Exec(Netsh, "advfirewall firewall delete rule name=\"" + FwName + "\"", 30000);
            try { Directory.Delete(Dir(), true); }
            catch (Exception e)
            {
                // 실행 중인 파일은 지우지 못할 수 있다. 지울 수 있는 것만 지운다.
                Log.Write("폴더 삭제 일부 실패: " + e.Message);
                try { foreach (var f in Directory.GetFiles(Dir())) { try { File.Delete(f); } catch { } } } catch { }
            }
            Log.Write("제거 완료: " + rc);
            return rc;
        }
        catch (Exception e) { Log.Write("제거 예외: " + e); return 29; }
    }

    public static int AddFirewall(string exe)
    {
        if (Log.Path == null) SvcUtil.SetLog("install.log");
        Exec(Netsh, "advfirewall firewall delete rule name=\"" + FwName + "\" program=\"" + exe + "\"", 30000);
        int a = Exec(Netsh, "advfirewall firewall add rule name=\"" + FwName + "\" dir=in action=allow program=\"" + exe + "\" protocol=TCP profile=private,domain enable=yes", 30000);
        int b = Exec(Netsh, "advfirewall firewall add rule name=\"" + FwName + "\" dir=in action=allow program=\"" + exe + "\" protocol=UDP profile=private,domain enable=yes", 30000);
        return (a == 0 && b == 0) ? 0 : 1;
    }
}
