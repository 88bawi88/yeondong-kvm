// 암호화와 짝 맺기.
// 각 PC는 처음 실행할 때 자기 인증서(ECDSA P-256, 자체 서명)를 만들어 Windows 사용자 인증서 저장소에 둔다.
// 연결은 늘 TLS(양쪽 인증서)로 하고, 상대 인증서 지문(SHA-256)이 짝 맺을 때 적어 둔 것과 같아야 받는다.
// 짝 맺기: 처음 한 번은 지문을 모르므로 아무 인증서나 받고, 두 PC가 같은 6자리 숫자를 화면에 띄워 사람이 맞는지 본다.
//   숫자 = SHA-256(건 쪽 지문 | 받은 쪽 지문 | 건 쪽 난수 | 받은 쪽 난수)에서 뽑는다.
//   건 쪽은 자기 난수를 해시로 먼저 약속하고(commit) 받은 쪽 난수를 본 뒤에 밝힌다. 그래서 가운데서 가로채는 쪽이
//   숫자를 맞추려고 난수를 바꿔 가며 시도할 수 없고, 가로챘다면 두 화면의 숫자가 달라진다.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web.Script.Serialization;

static class Identity
{
    public static X509Certificate2 Cert;
    public static string Thumb = "";

    // 이 PC 인증서를 찾거나 만든다. id는 config.json의 고유 번호.
    public static void Load(string id)
    {
        var tag = "Yeondong " + id;
        using (var st = new X509Store(StoreName.My, StoreLocation.CurrentUser))
        {
            st.Open(OpenFlags.ReadWrite);
            foreach (var c in st.Certificates)
                if (c.FriendlyName == tag && c.HasPrivateKey && c.NotAfter > DateTime.Now) { Cert = c; break; }
            if (Cert == null)
            {
                using (var k = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    var req = new CertificateRequest("CN=Yeondong-" + id, k, HashAlgorithmName.SHA256);
                    var tmp = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30));
                    // SChannel(TLS)은 저장된 키만 쓸 수 있어 PFX로 내보냈다가 사용자 키 저장소에 다시 넣는다
                    var pw = Convert.ToBase64String(Rand(16));
                    Cert = new X509Certificate2(tmp.Export(X509ContentType.Pfx, pw), pw, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
                    Cert.FriendlyName = tag;
                    st.Add(Cert);
                    Log.Write("이 PC 인증서를 새로 만듦");
                }
            }
        }
        Thumb = ThumbOf(Cert);
    }

    public static string ThumbOf(X509Certificate c)
    {
        using (var h = SHA256.Create()) return Hex(h.ComputeHash(c.GetRawCertData()));
    }

    public static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", ""); }

    public static byte[] Rand(int n)
    {
        var b = new byte[n];
        using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
        return b;
    }
}

static class Tls
{
    const SslProtocols Protocols = SslProtocols.Tls12 | (SslProtocols)12288;   // TLS 1.2, 1.3

    // 받은 연결. 상대 인증서를 요구하고 지문을 돌려준다(맞는 상대인지는 부른 쪽이 판단한다).
    public static SslStream Server(TcpClient t, out string peerThumb)
    {
        string seen = null;
        var s = new SslStream(t.GetStream(), false, (o, cert, chain, err) => { if (cert != null) seen = Identity.ThumbOf(cert); return cert != null; });
        t.ReceiveTimeout = 10000;
        s.AuthenticateAsServer(Identity.Cert, true, Protocols, false);
        t.ReceiveTimeout = 0;
        peerThumb = seen;
        if (seen == null) { s.Dispose(); throw new AuthenticationException("상대 인증서 없음"); }
        return s;
    }

    // 건 연결.
    public static SslStream Client(TcpClient t, out string peerThumb)
    {
        string seen = null;
        var s = new SslStream(t.GetStream(), false,
            (o, cert, chain, err) => { if (cert != null) seen = Identity.ThumbOf(cert); return cert != null; },
            (o, host, certs, remote, issuers) => Identity.Cert);
        t.ReceiveTimeout = 10000;
        s.AuthenticateAsClient("yeondong", new X509CertificateCollection { Identity.Cert }, Protocols, false);
        t.ReceiveTimeout = 0;
        peerThumb = seen;
        if (seen == null) { s.Dispose(); throw new AuthenticationException("상대 인증서 없음"); }
        return s;
    }
}

// 짝 맺기 메시지. 일반 연결의 첫 프레임은 Msg.Hello이고, 짝 맺기는 아래 번호로 시작한다.
static class Pairing
{
    public const byte Start = 50;    // 건 쪽: json {id, name, role, port, commit}
    public const byte Nonce = 51;    // 받은 쪽: json {id, name, role, port, nonce}
    public const byte Reveal = 52;   // 건 쪽: json {nonce}
    public const byte Result = 53;   // 양쪽: [1=맞음 0=아님]

    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    static byte[] Enc(Dictionary<string, object> d) { return Encoding.UTF8.GetBytes(Json.Serialize(d)); }
    static Dictionary<string, object> Dec(byte[] b) { return (Dictionary<string, object>)Json.DeserializeObject(Encoding.UTF8.GetString(b)); }
    static string S(Dictionary<string, object> d, string k) { return d.ContainsKey(k) ? Convert.ToString(d[k]) : ""; }

    // 두 화면에 띄울 6자리 숫자("123 456")
    public static string Sas(string thumbI, string thumbA, string nonceI, string nonceA)
    {
        using (var h = SHA256.Create())
        {
            var b = h.ComputeHash(Encoding.ASCII.GetBytes(thumbI + "|" + thumbA + "|" + nonceI + "|" + nonceA));
            uint v = BitConverter.ToUInt32(b, 0) % 1000000;
            var s = v.ToString("D6");
            return s.Substring(0, 3) + " " + s.Substring(3);
        }
    }

    static string H(string s)
    {
        using (var h = SHA256.Create()) return Identity.Hex(h.ComputeHash(Encoding.ASCII.GetBytes(s)));
    }

    // 이쪽에서 짝 맺기를 건다. confirm(숫자, 상대 이름)은 사람이 두 화면 숫자가 같은지 답할 때까지 기다린다(다른 스레드에서 불린다).
    // 성공하면 상대 정보를 돌려준다(설정 저장은 부른 쪽이 한다). 실패하면 예외.
    public static Machine Initiate(Config cfg, IPAddress ip, int port, Func<string, string, bool> confirm)
    {
        using (var t = new TcpClient())
        {
            var ar = t.BeginConnect(ip, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(3000)) throw new Exception(ip + " 에 연결할 수 없습니다");
            t.EndConnect(ar);
            t.NoDelay = true;
            string peerThumb;
            using (var s = Tls.Client(t, out peerThumb))
            {
                var nonceI = Identity.Hex(Identity.Rand(16));
                Link.WriteFrame(s, Start, Enc(new Dictionary<string, object> {
                    { "id", cfg.Id }, { "name", cfg.Name }, { "role", cfg.Role }, { "port", cfg.Port }, { "commit", H(nonceI) } }));
                t.ReceiveTimeout = 120000;   // 상대 PC에서 사람이 확인할 시간을 둔다
                byte type;
                var b = Link.ReadFrame(s, out type);
                if (type == Result) throw new Exception("상대 PC가 지금 짝 맺기를 받지 않습니다(상대 PC에서 연동 설정 창을 열어 두세요)");
                if (type != Nonce) throw new Exception("상대 응답이 이상합니다");
                var a = Dec(b);
                Link.WriteFrame(s, Reveal, Enc(new Dictionary<string, object> { { "nonce", nonceI } }));
                var sas = Sas(Identity.Thumb, peerThumb, nonceI, S(a, "nonce"));
                bool ok = confirm(sas, S(a, "name"));
                Link.WriteFrame(s, Result, new[] { (byte)(ok ? 1 : 0) });
                var r = Link.ReadFrame(s, out type);
                if (!ok) throw new Exception("숫자가 다르다고 하셔서 짝을 맺지 않았습니다");
                if (type != Result || r.Length < 1 || r[0] != 1) throw new Exception("상대 PC에서 짝 맺기를 거절했습니다");
                return new Machine { Host = S(a, "id"), Name = S(a, "name"), Role = S(a, "role"), Thumb = peerThumb, Addr = ip.ToString(),
                                     Port = ToPort(S(a, "port"), port) };
            }
        }
    }

    // 받은 연결의 첫 프레임이 Start였다. confirm이 null이면(짝 맺기를 받는 중이 아니면) 거절한다.
    public static Machine Accept(Config cfg, TcpClient t, System.IO.Stream s, string peerThumb, byte[] first, Func<string, string, bool> confirm)
    {
        var a = Dec(first);
        if (confirm == null || S(a, "role") == cfg.Role || cfg.Role == "")
        {
            Link.WriteFrame(s, Result, new byte[] { 0 });
            Log.Write("짝 맺기 요청을 받지 않음(" + S(a, "name") + ", " + S(a, "role") + ")");
            return null;
        }
        var nonceA = Identity.Hex(Identity.Rand(16));
        Link.WriteFrame(s, Nonce, Enc(new Dictionary<string, object> {
            { "id", cfg.Id }, { "name", cfg.Name }, { "role", cfg.Role }, { "port", cfg.Port }, { "nonce", nonceA } }));
        t.ReceiveTimeout = 120000;
        byte type;
        var rv = Dec(Link.ReadFrame(s, out type));
        if (type != Reveal || H(S(rv, "nonce")) != S(a, "commit")) throw new Exception("짝 맺기 약속 값이 맞지 않음(가로채기 의심)");
        var sas = Sas(peerThumb, Identity.Thumb, S(rv, "nonce"), nonceA);
        bool ok = confirm(sas, S(a, "name"));
        Link.WriteFrame(s, Result, new[] { (byte)(ok ? 1 : 0) });
        var r = Link.ReadFrame(s, out type);
        if (!ok || type != Result || r.Length < 1 || r[0] != 1) return null;
        var ip = ((IPEndPoint)t.Client.RemoteEndPoint).Address;
        return new Machine { Host = S(a, "id"), Name = S(a, "name"), Role = S(a, "role"), Thumb = peerThumb, Addr = ip.ToString(),
                             Port = ToPort(S(a, "port"), cfg.Port) };
    }

    static int ToPort(string s, int dflt) { int p; return int.TryParse(s, out p) && p > 0 && p < 65536 ? p : dflt; }
}
