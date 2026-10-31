// 설정 파일 %APPDATA%\Yeondong\config.json. 이 PC의 역할(메인/서브)과 이름, 짝 맺은 PC 목록을 둔다.
// 짝 맺은 PC마다 상대 인증서 지문(thumb)을 적어 두고, 연결할 때 그 지문이 맞는 상대만 받는다(Secure.cs).
// 메인 PC에서는 서브 PC마다 모니터 자리(zone, 메인 가상 화면 좌표의 빈자리)도 둔다. 비어 있으면 자동으로 정한다.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// 짝 맺은 상대 PC. Host에는 상대 고유 번호(id)를 둔다(배치 창·자리 저장에서 열쇠로 쓴다).
class Machine
{
    public string Host = "", Name = "", Thumb = "", Role = "", Addr = "";
    public int Port;         // 상대가 쓰는 포트(0이면 이 PC와 같은 포트)
    public int[] ZoneRect;   // 메인 PC에서 이 서브 모니터 자리 [x, y, w, h]. 없으면 자동
    public int[] Screen;     // 상대가 마지막으로 알려 준 화면 크기 [w, h]
}

class Config
{
    public static string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yeondong");
    public static string Path { get { return System.IO.Path.Combine(Dir, "config.json"); } }
    static readonly object gate = new object();

    public string Id = "", Name = "", Role = "";   // Role: "main", "sub", 아직 안 정했으면 ""
    public int Port = 61220;
    public int Push = 1;                  // 가장자리에서 바깥으로 이만큼(원시 이동량 합) 밀면 넘어간다
    public bool Clipboard = true;         // 클립보드(글·그림) 공유
    public bool Consent;                  // 처음 실행 때 안내에 동의했나
    public List<Machine> Peers = new List<Machine>();

    public IEnumerable<Machine> Subs { get { return Peers.Where(p => p.Role == "sub"); } }
    public Machine MainPeer { get { return Peers.FirstOrDefault(p => p.Role == "main"); } }
    public Machine FindPeer(string id) { return Peers.FirstOrDefault(p => p.Host == id); }

    public static Config Load()
    {
        var c = new Config();
        try
        {
            if (File.Exists(Path))
            {
                var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path));
                c.Id = Str(d, "id"); c.Name = Str(d, "name"); c.Role = Str(d, "role");
                if (d.ContainsKey("port")) c.Port = Convert.ToInt32(d["port"]);
                if (d.ContainsKey("push")) c.Push = Math.Max(1, Convert.ToInt32(d["push"]));
                if (d.ContainsKey("clipboard")) c.Clipboard = Convert.ToBoolean(d["clipboard"]);
                if (d.ContainsKey("consent")) c.Consent = Convert.ToBoolean(d["consent"]);
                if (d.ContainsKey("peers"))
                    foreach (Dictionary<string, object> p in (IEnumerable)d["peers"])
                        c.Peers.Add(new Machine
                        {
                            Host = Str(p, "id"), Name = Str(p, "name"), Thumb = Str(p, "thumb"), Role = Str(p, "role"), Addr = Str(p, "addr"),
                            Port = p.ContainsKey("port") ? Convert.ToInt32(p["port"]) : 0,
                            ZoneRect = Ints(p, "zone"), Screen = Ints(p, "screen"),
                        });
            }
        }
        catch (Exception e) { Log.Write("config.json 읽기 실패, 새로 만듦: " + e.Message); }
        if (c.Id == "") c.Id = Guid.NewGuid().ToString("N");
        if (c.Name == "") c.Name = Environment.MachineName;
        return c;
    }

    static string Str(Dictionary<string, object> d, string k) { return d.ContainsKey(k) && d[k] != null ? Convert.ToString(d[k]) : ""; }

    static int[] Ints(Dictionary<string, object> d, string k)
    {
        var a = d.ContainsKey(k) ? d[k] as IEnumerable : null;
        return a == null || d[k] is string ? null : a.Cast<object>().Select(Convert.ToInt32).ToArray();
    }

    public void Save()
    {
        lock (gate)
        {
            var d = new Dictionary<string, object>
            {
                { "id", Id }, { "name", Name }, { "role", Role }, { "port", Port }, { "push", Push },
                { "clipboard", Clipboard }, { "consent", Consent },
                { "peers", Peers.Select(p => new Dictionary<string, object> {
                    { "id", p.Host }, { "name", p.Name }, { "thumb", p.Thumb }, { "role", p.Role }, { "addr", p.Addr }, { "port", p.Port },
                    { "zone", p.ZoneRect }, { "screen", p.Screen } }).ToArray() },
            };
            Directory.CreateDirectory(Dir);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(d));
            if (File.Exists(Path)) File.Replace(tmp, Path, null); else File.Move(tmp, Path);
        }
    }

    // 서브 모니터 자리를 메인 PC 좌표로 계산한다. 정해 둔 자리가 없으면 가장 오른쪽 모니터의 오른쪽에 붙인다.
    // 서브가 여럿이면 앞 서브 자리의 오른쪽에 차례로 붙인다(others: 이미 정한 자리들).
    public static Rectangle ZoneOf(Machine m, Rectangle[] mons, IEnumerable<Rectangle> others)
    {
        if (m.ZoneRect != null && m.ZoneRect.Length == 4)
            return new Rectangle(m.ZoneRect[0], m.ZoneRect[1], m.ZoneRect[2], m.ZoneRect[3]);
        if (mons.Length == 0) return Rectangle.Empty;
        var size = m.Screen != null && m.Screen.Length == 2 ? new Size(m.Screen[0], m.Screen[1]) : new Size(1920, 1080);
        var edge = mons.Concat(others ?? new Rectangle[0]).OrderByDescending(r => r.Right).First();
        return new Rectangle(edge.Right, edge.Top, size.Width, size.Height);
    }

    public static Rectangle[] Monitors() { return System.Windows.Forms.Screen.AllScreens.Select(s => s.Bounds).ToArray(); }
}
