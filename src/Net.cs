// 메인 PC와 서브 PC가 주고받는 메시지. 암호화된 연결(TLS, Secure.cs) 하나 위에 [종류 1바이트][길이 int32][내용]을 이어 보낸다.
// 입력(마우스·키)은 급한 대기열로, 파일 내용은 느린 대기열로 보낸다. 큰 파일을 보내는 동안에도 마우스가 밀리지 않게.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

static class Msg
{
    public const byte Hello = 1;      // 연결 직후 양쪽: json {id, name, role, x, y, w, h} (보내는 PC의 가상 화면)
    public const byte Ping = 2;       // 양쪽: 1초마다. 5초 동안 아무것도 못 받으면 끊긴 것으로 본다
    public const byte Enter = 10;     // 메인→서브: [side 1][frac float] 서브 화면의 그 변에 커서를 놓고 입력을 받기 시작
    public const byte Leave = 11;     // 메인→서브: 입력 끝. 서브는 눌린 키·버튼을 모두 뗀다
    public const byte Move = 12;      // 메인→서브: [dx int32][dy int32] 메인 PC 가속이 적용된 이동량
    public const byte Button = 13;    // 메인→서브: [button 1][down 1]  0=왼 1=오른 2=가운데 3=X1 4=X2
    public const byte Wheel = 14;     // 메인→서브: [horizontal 1][delta int32]
    public const byte Key = 15;       // 메인→서브: [vk uint16][scan uint16][flags 1]  bit0=뗌 bit1=확장키
    public const byte Edge = 20;      // 서브→메인: [side 1][frac float] 서브 화면 그 변에서 바깥으로 밀었다
    public const byte ClipText = 30;  // 양쪽: UTF-8 글
    public const byte ClipImage = 31; // 양쪽: PNG
    public const byte FileOffer = 32; // 양쪽: json {id, entries}. 끌던 파일 목록(내용은 놓을 때 FileReq로 받는다, FileXfer.cs)
    public const byte FileReq = 33;   // 받는 쪽: [목록 id][항목 번호][요청 번호] 그 파일을 보내 달라
    public const byte FileData = 34;  // 보내는 쪽: [요청 번호][내용 일부]
    public const byte FileEnd = 35;   // 보내는 쪽: [요청 번호][0=끝까지 보냄, 1=실패]
    public const byte FileAck = 36;   // 받는 쪽: [요청 번호][지금까지 읽은 바이트 int64] 보내는 쪽이 너무 앞서 나가지 않게
    public const byte Info = 40;      // 메인→서브: UTF-8 글. 메인 PC 배치와 전환 기록을 서브 PC 로그에도 남긴다

    // 변 번호. 서브 화면 기준이다.
    public const byte Left = 0, Top = 1, Right = 2, Bottom = 3;
}

// 연결 하나. 보내기는 대기열에 넣고 바로 돌아온다(입력 훅에서 부르므로 막히면 안 된다).
class Link
{
    public readonly TcpClient Tcp;
    readonly Stream stream;
    readonly BlockingCollection<byte[]> fast = new BlockingCollection<byte[]>(4096);
    readonly BlockingCollection<byte[]> slow = new BlockingCollection<byte[]>(64);   // 파일 조각. 가득 차면 보내는 쪽이 기다린다
    public volatile bool Closed;
    public string PeerId = "", Name = "";
    public bool Dialed;                             // 이쪽에서 건 연결인가(같은 상대와 두 연결이 생기면 하나만 남길 때 쓴다)
    public Action<Link, byte, byte[]> OnMessage;   // 읽기 스레드에서 불린다
    public Action<Link> OnClosed;                   // 한 번만 불린다
    DateTime lastRecv = DateTime.UtcNow;
    int closedFlag;

    public Link(TcpClient tcp, Stream s)
    {
        Tcp = tcp;
        tcp.NoDelay = true;
        stream = s;
    }

    public void Start()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = "link-read" }.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = "link-write" }.Start();
        new Thread(PingLoop) { IsBackground = true, Name = "link-ping" }.Start();
    }

    static byte[] Frame(byte type, byte[] body)
    {
        body = body ?? new byte[0];
        var f = new byte[5 + body.Length];
        f[0] = type;
        BitConverter.GetBytes(body.Length).CopyTo(f, 1);
        body.CopyTo(f, 5);
        return f;
    }

    public void Send(byte type, byte[] body)
    {
        if (Closed) return;
        try { if (!fast.TryAdd(Frame(type, body))) Close("보내기 대기열이 가득 참"); }
        catch (InvalidOperationException) { }   // 닫히는 중
    }

    // 파일 조각처럼 큰 것. 대기열이 비기를 기다린다(입력 훅에서는 부르지 않는다). 끊겼으면 false.
    public bool SendSlow(byte type, byte[] body)
    {
        if (Closed) return false;
        try { slow.Add(Frame(type, body)); return true; }
        catch (InvalidOperationException) { return false; }
    }

    public void SendSideFrac(byte type, byte side, float frac)
    {
        var b = new byte[5];
        b[0] = side;
        BitConverter.GetBytes(frac).CopyTo(b, 1);
        Send(type, b);
    }

    public void Close(string why)
    {
        if (Interlocked.Exchange(ref closedFlag, 1) == 1) return;
        Closed = true;
        Log.Write("연결 끊김(" + (Name != "" ? Name : PeerId) + "): " + why);
        try { stream.Dispose(); } catch { }
        try { Tcp.Close(); } catch { }
        fast.CompleteAdding();
        slow.CompleteAdding();
        if (OnClosed != null) OnClosed(this);
    }

    void ReadLoop()
    {
        try
        {
            var head = new byte[5];
            while (!Closed)
            {
                ReadFull(stream, head, 5);
                int len = BitConverter.ToInt32(head, 1);
                if (len < 0 || len > 64 * 1024 * 1024) throw new IOException("길이가 이상함: " + len);
                var body = new byte[len];
                ReadFull(stream, body, len);
                lastRecv = DateTime.UtcNow;
                if (head[0] != Msg.Ping && OnMessage != null) OnMessage(this, head[0], body);
            }
        }
        catch (Exception e) { Close("읽기: " + e.Message); }
    }

    public static void ReadFull(Stream s, byte[] buf, int n)
    {
        int got = 0;
        while (got < n)
        {
            int r = s.Read(buf, got, n - got);
            if (r <= 0) throw new IOException("상대가 닫음");
            got += r;
        }
    }

    // 급한 대기열을 먼저 비운다. TakeFromAny는 앞 대기열부터 본다.
    void WriteLoop()
    {
        var qs = new[] { fast, slow };
        try
        {
            while (true)
            {
                byte[] f;
                if (BlockingCollection<byte[]>.TakeFromAny(qs, out f) < 0) break;
                stream.Write(f, 0, f.Length);
                if (fast.Count == 0) stream.Flush();
            }
        }
        catch (ArgumentException) { }            // 둘 다 닫힘
        catch (InvalidOperationException) { }
        catch (Exception e) { Close("쓰기: " + e.Message); }
    }

    void PingLoop()
    {
        while (!Closed)
        {
            Thread.Sleep(1000);
            if ((DateTime.UtcNow - lastRecv).TotalSeconds > 5) { Close("5초 동안 응답 없음"); return; }
            Send(Msg.Ping, null);
        }
    }

    public static float Frac(byte[] b) { return BitConverter.ToSingle(b, 1); }

    // 연결 직후 한 번 주고받는 프레임(Link를 띄우기 전, 짝 맺기 등)
    public static void WriteFrame(Stream s, byte type, byte[] body)
    {
        var f = Frame(type, body);
        s.Write(f, 0, f.Length);
        s.Flush();
    }

    public static byte[] ReadFrame(Stream s, out byte type)
    {
        var h = new byte[5];
        ReadFull(s, h, 5);
        type = h[0];
        int len = BitConverter.ToInt32(h, 1);
        if (len < 0 || len > 1024 * 1024) throw new IOException("길이가 이상함: " + len);
        var b = new byte[len];
        ReadFull(s, b, len);
        return b;
    }
}

static class Log
{
    static readonly object gate = new object();
    public static string Path;

    public static void Write(string s)
    {
        try
        {
            lock (gate)
            {
                if (Path == null) return;
                var fi = new FileInfo(Path);
                if (fi.Exists && fi.Length > 512 * 1024) File.Copy(Path, Path + ".1", true);
                if (fi.Exists && fi.Length > 512 * 1024) File.Delete(Path);
                File.AppendAllText(Path, DateTime.Now.ToString("MM-dd HH:mm:ss.fff ") + s + "\r\n", Encoding.UTF8);
            }
        }
        catch { }
    }
}
