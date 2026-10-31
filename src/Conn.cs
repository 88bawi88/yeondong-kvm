// 짝 맺은 PC와의 연결 관리. 메인과 서브가 똑같이 쓴다.
// 양쪽 모두 포트를 열어 기다리고, 양쪽 모두 상대에게 건다. 한쪽 PC의 방화벽이 막혀 있어도 다른 쪽에서 걸면 이어진다.
// 두 연결이 동시에 생기면 고유 번호(id)가 작은 PC가 건 연결 하나만 남긴다(양쪽이 같은 규칙이라 같은 것을 고른다).
// 같은 네트워크 안의 연동을 UDP 방송으로 찾는다(같은 포트). 찾은 주소는 짝 맺기 목록과 다시 걸 때 쓴다.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

class Found
{
    public string Id, Name, Role;
    public IPAddress Ip;
    public int Port;
    public DateTime Seen;
}

class Conn
{
    readonly Config cfg;
    readonly object gate = new object();
    readonly Dictionary<string, Link> links = new Dictionary<string, Link>();
    readonly Dictionary<string, Found> found = new Dictionary<string, Found>();
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    UdpClient udp;

    // 새 연결이 정해졌다(읽기를 시작하기 전). 여기서 link.OnMessage를 달면 된다. 연결 스레드에서 불린다.
    public Action<Link, Machine, Dictionary<string, object>> OnLink;
    public Action<Link, Machine> OnClosed;
    // 짝 맺기를 받는 중이면 숫자 확인 함수(설정 창이 열려 있을 때). 없으면 짝 맺기 요청을 거절한다.
    public volatile Func<string, string, bool> PairConfirm;
    public Action<Machine> OnPaired;

    public Conn(Config c) { cfg = c; }

    public void Start()
    {
        new Thread(Listen) { IsBackground = true, Name = "listen" }.Start();
        new Thread(Discover) { IsBackground = true, Name = "discover" }.Start();
        new Thread(DialLoop) { IsBackground = true, Name = "dial" }.Start();
    }

    public Link LinkOf(Machine m)
    {
        Link l;
        lock (gate) return links.TryGetValue(m.Host, out l) && !l.Closed ? l : null;
    }

    public List<Found> FoundList()
    {
        lock (gate) return found.Values.Where(f => (DateTime.Now - f.Seen).TotalSeconds < 10).OrderBy(f => f.Name).ToList();
    }

    public static bool IsLan(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    [DllImport("kernel32.dll")] static extern bool SetHandleInformation(IntPtr h, uint mask, uint flags);

    // ---------- 받기 ----------
    void Listen()
    {
        TcpListener l = null;
        for (int i = 0; l == null; i++)
        {
            try
            {
                l = new TcpListener(IPAddress.Any, cfg.Port);
                l.Start();
                SetHandleInformation(l.Server.Handle, 1, 0);   // 띄우는 프로세스에 대기 소켓이 넘어가지 않게
            }
            catch (Exception e)
            {
                if (i == 0) Log.Write("포트 " + cfg.Port + " 열기 실패, 다시 해 봄: " + e.Message);
                l = null;
                Thread.Sleep(2000);
            }
        }
        Log.Write("연결 대기 시작, 포트 " + cfg.Port);
        while (true)
        {
            TcpClient t;
            try { t = l.AcceptTcpClient(); } catch { Thread.Sleep(500); continue; }
            var ip = ((IPEndPoint)t.Client.RemoteEndPoint).Address;
            if (!IsLan(ip)) { Log.Write("같은 네트워크 밖 주소라 끊음: " + ip); t.Close(); continue; }
            new Thread(() => Accepted(t, ip)) { IsBackground = true, Name = "accept" }.Start();
        }
    }

    void Accepted(TcpClient t, IPAddress ip)
    {
        try
        {
            t.NoDelay = true;
            string thumb;
            var s = Tls.Server(t, out thumb);
            t.ReceiveTimeout = 10000;
            byte type;
            var first = Link.ReadFrame(s, out type);
            if (type == Pairing.Start)
            {
                var m = Pairing.Accept(cfg, t, s, thumb, first, PairConfirm);
                s.Dispose(); t.Close();
                if (m != null) Paired(m);
                return;
            }
            var peer = cfg.Peers.FirstOrDefault(p => p.Thumb == thumb);
            if (type != Msg.Hello || peer == null) { Log.Write("짝 맺지 않은 PC라 끊음: " + ip); s.Dispose(); t.Close(); return; }
            var hello = Dec(first);
            if (Convert.ToString(hello["id"]) != peer.Host) { Log.Write("고유 번호가 맞지 않아 끊음: " + ip); s.Dispose(); t.Close(); return; }
            Link.WriteFrame(s, Msg.Hello, Hello());
            t.ReceiveTimeout = 0;
            Adopt(new Link(t, s) { Dialed = false }, peer, hello, ip);
        }
        catch (Exception e) { Log.Write("받은 연결 처리 실패(" + ip + "): " + e.Message); try { t.Close(); } catch { } }
    }

    // ---------- 걸기 ----------
    void DialLoop()
    {
        while (true)
        {
            foreach (var p in cfg.Peers.ToList())
            {
                if (LinkOf(p) != null) continue;
                foreach (var ip in Addresses(p))
                {
                    if (Dial(p, ip)) break;
                }
            }
            Thread.Sleep(3000);
        }
    }

    IEnumerable<IPAddress> Addresses(Machine p)
    {
        var r = new List<IPAddress>();
        Found f;
        lock (gate) if (found.TryGetValue(p.Host, out f)) r.Add(f.Ip);
        IPAddress a;
        if (IPAddress.TryParse(p.Addr, out a) && !r.Contains(a)) r.Add(a);
        return r;
    }

    int PortOf(Machine p)
    {
        Found f;
        lock (gate) if (found.TryGetValue(p.Host, out f) && f.Port > 0) return f.Port;
        return p.Port > 0 ? p.Port : cfg.Port;
    }

    bool Dial(Machine p, IPAddress ip)
    {
        var t = new TcpClient();
        try
        {
            var ar = t.BeginConnect(ip, PortOf(p), null, null);
            if (!ar.AsyncWaitHandle.WaitOne(3000)) { t.Close(); return false; }
            t.EndConnect(ar);
            t.NoDelay = true;
            string thumb;
            var s = Tls.Client(t, out thumb);
            if (thumb != p.Thumb) { Log.Write(p.Name + " 주소(" + ip + ")의 인증서가 짝 맺을 때와 다름, 끊음"); s.Dispose(); t.Close(); return false; }
            Link.WriteFrame(s, Msg.Hello, Hello());
            t.ReceiveTimeout = 10000;
            byte type;
            var b = Link.ReadFrame(s, out type);
            t.ReceiveTimeout = 0;
            if (type != Msg.Hello) { s.Dispose(); t.Close(); return false; }
            Adopt(new Link(t, s) { Dialed = true }, p, Dec(b), ip);
            return true;
        }
        catch (Exception) { try { t.Close(); } catch { } return false; }
    }

    // ---------- 연결 정하기 ----------
    string DialerOf(Link l, Machine p) { return l.Dialed ? cfg.Id : p.Host; }

    void Adopt(Link l, Machine p, Dictionary<string, object> hello, IPAddress ip)
    {
        l.PeerId = p.Host;
        l.Name = p.Name;
        Link old = null;
        lock (gate)
        {
            Link cur;
            if (links.TryGetValue(p.Host, out cur) && !cur.Closed)
            {
                string a = DialerOf(l, p), b = DialerOf(cur, p);
                // 같은 쪽이 다시 건 것이면 새 것을, 아니면 id가 작은 쪽이 건 것을 남긴다
                bool keepNew = a == b || string.CompareOrdinal(a, b) < 0;
                if (!keepNew) { l.Close("같은 PC와 이미 연결돼 있음"); return; }
                old = cur;
            }
            links[p.Host] = l;
        }
        if (old != null) old.Close("새 연결로 바꿈");
        var addr = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
        if (p.Addr != addr) { p.Addr = addr; Save(); }
        var cb = OnClosed;
        l.OnClosed = k =>
        {
            lock (gate) { Link cur; if (links.TryGetValue(p.Host, out cur) && cur == k) links.Remove(p.Host); }
            if (cb != null) cb(k, p);
        };
        Log.Write(p.Name + " 연결됨 (" + addr + (l.Dialed ? ", 이쪽에서 걺" : ", 상대가 걺") + ")");
        if (OnLink != null) OnLink(l, p, hello);
        l.Start();
    }

    void Save() { try { cfg.Save(); } catch (Exception e) { Log.Write("설정 저장 실패: " + e.Message); } }

    void Paired(Machine m)
    {
        lock (gate)
        {
            cfg.Peers.RemoveAll(p => p.Host == m.Host);
            if (cfg.Role == "sub") cfg.Peers.RemoveAll(p => p.Role == "main");   // 서브는 메인 하나만 둔다
            cfg.Peers.Add(m);
        }
        Save();
        Log.Write("짝 맺음: " + m.Name + " (" + m.Role + ", " + m.Addr + ")");
        if (OnPaired != null) OnPaired(m);
    }

    // 설정 창에서 이쪽이 짝 맺기를 건다(다른 스레드에서 부른다).
    public Machine PairWith(IPAddress ip, int port, Func<string, string, bool> confirm)
    {
        var m = Pairing.Initiate(cfg, ip, port, confirm);
        Paired(m);
        return m;
    }

    public void Unpair(Machine m)
    {
        Link l;
        lock (gate)
        {
            cfg.Peers.Remove(m);
            if (links.TryGetValue(m.Host, out l)) links.Remove(m.Host);
        }
        Save();
        if (l != null) l.Close("짝 풂");
        Log.Write("짝 풂: " + m.Name);
    }

    byte[] Hello()
    {
        var d = new Dictionary<string, object> { { "id", cfg.Id }, { "name", cfg.Name }, { "role", cfg.Role } };
        var vs = SystemInformation.VirtualScreen;
        d["x"] = vs.X; d["y"] = vs.Y; d["w"] = vs.Width; d["h"] = vs.Height;
        return Encoding.UTF8.GetBytes(Json.Serialize(d));
    }

    static Dictionary<string, object> Dec(byte[] b) { return (Dictionary<string, object>)Json.DeserializeObject(Encoding.UTF8.GetString(b)); }

    // ---------- 찾기(UDP) ----------
    // 묻기: "YD2?"+json{id}  답하기: "YD2!"+json{id, name, role, port}
    public volatile bool Scan;   // 설정 창이 열려 있으면 자주 묻는다

    void Discover()
    {
        for (int i = 0; udp == null; i++)
        {
            try
            {
                udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, cfg.Port));
                udp.EnableBroadcast = true;
                SetHandleInformation(udp.Client.Handle, 1, 0);
            }
            catch (Exception e)
            {
                if (i == 0) Log.Write("찾기용 UDP 포트 열기 실패, 다시 해 봄: " + e.Message);
                try { udp.Close(); } catch { }
                udp = null;
                Thread.Sleep(2000);
            }
        }
        new Thread(Ask) { IsBackground = true, Name = "discover-ask" }.Start();
        while (true)
        {
            try
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                var b = udp.Receive(ref from);
                if (!IsLan(from.Address) || b.Length < 5) continue;
                var head = Encoding.ASCII.GetString(b, 0, 4);
                var d = (Dictionary<string, object>)Json.DeserializeObject(Encoding.UTF8.GetString(b, 4, b.Length - 4));
                var id = Convert.ToString(d["id"]);
                if (id == cfg.Id) continue;
                if (head == "YD2?")
                {
                    var me = Encoding.UTF8.GetBytes("YD2!" + Json.Serialize(new Dictionary<string, object> {
                        { "id", cfg.Id }, { "name", cfg.Name }, { "role", cfg.Role }, { "port", cfg.Port } }));
                    udp.Send(me, me.Length, from);
                }
                else if (head == "YD2!")
                {
                    var f = new Found { Id = id, Name = Convert.ToString(d["name"]), Role = Convert.ToString(d["role"]),
                                        Ip = from.Address, Port = Convert.ToInt32(d["port"]), Seen = DateTime.Now };
                    lock (gate) found[id] = f;
                }
            }
            catch (Exception) { Thread.Sleep(200); }
        }
    }

    void Ask()
    {
        var q = Encoding.UTF8.GetBytes("YD2?" + Json.Serialize(new Dictionary<string, object> { { "id", cfg.Id } }));
        int n = 0;
        while (true)
        {
            bool need = Scan || cfg.Peers.Any(p => LinkOf(p) == null);
            if (need || n % 20 == 0)
            {
                foreach (var target in Broadcasts())
                    try { udp.Send(q, q.Length, new IPEndPoint(target, cfg.Port)); } catch { }
            }
            n++;
            Thread.Sleep(Scan ? 1000 : 3000);
        }
    }

    // 255.255.255.255와 각 네트워크의 방송 주소(여러 네트워크 카드가 있을 때 모두로 보낸다)
    static List<IPAddress> Broadcasts()
    {
        var r = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                    var a = u.Address.GetAddressBytes(); var m = u.IPv4Mask.GetAddressBytes();
                    for (int i = 0; i < 4; i++) a[i] = (byte)(a[i] | ~m[i]);
                    var b = new IPAddress(a);
                    if (!r.Contains(b)) r.Add(b);
                }
            }
        }
        catch { }
        return r;
    }
}
