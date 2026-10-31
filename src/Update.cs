// 자동 업데이트. GitHub 최신 릴리스를 6시간마다 확인하고, 서명이 맞으면 실행 중인 exe를 새 파일로 바꿔 둔다.
// 프로세스를 다시 띄우는 일은 여기서 하지 않는다(exe의 수정 시각을 보는 다른 코드가 한다).
using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;

static class Updater
{
    const string Repo = "88bawi88/yeondong-kvm";
    // 릴리스 서명 공개키. ECDSA P-256 CNG EccPublicBlob의 base64. 실제 값은 나중에 채운다.
    public const string PublicKey = "RUNTMSAAAAD31Kns0kbCGMFYlPG2HLSWLDV36MswhUGkqgeLn06k8yNpBF8BIK869+tMXoPUH+4hjfLbyr1eip58lW7OUvNO";

    public static void Start()
    {
        new Thread(Loop) { IsBackground = true, Name = "updater" }.Start();
    }

    static void Loop()
    {
        Thread.Sleep(60 * 1000);
        while (true)
        {
            try
            {
                string msg;
                CheckOnce(out msg);
            }
            catch (Exception e) { Log.Write("업데이트 확인 실패: " + e.Message); }
            Thread.Sleep(TimeSpan.FromHours(6));
        }
    }

    // 새 버전을 받아 exe를 바꿨으면 true.
    public static bool CheckOnce(out string message)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var json = HttpGetString("https://api.github.com/repos/" + Repo + "/releases/latest");
        var root = new JavaScriptSerializer().DeserializeObject(json) as IDictionary;
        if (root == null || !root.Contains("tag_name")) { message = "릴리스 정보 없음"; return false; }

        string tag = Convert.ToString(root["tag_name"]);
        Version latest = ParseVersion(tag), cur = ParseVersion(Program.Version);
        if (latest == null || cur == null) { message = "버전 해석 실패 " + tag; Log.Write("업데이트: " + message); return false; }
        if (latest <= cur) { message = "최신 버전 " + Program.Version; return false; }

        string exeUrl = null, sigUrl = null;
        var assets = root["assets"] as IEnumerable;
        if (assets != null)
            foreach (object o in assets)
            {
                var a = o as IDictionary;
                if (a == null) continue;
                string name = Convert.ToString(a["name"]), url = Convert.ToString(a["browser_download_url"]);
                if (name == "link.exe") exeUrl = url;
                else if (name == "link.exe.sig") sigUrl = url;
            }
        if (exeUrl == null || sigUrl == null) { message = tag + " 에 link.exe 또는 link.exe.sig 없음"; Log.Write("업데이트: " + message); return false; }


        // 임시 폴더에 받고, 서명을 확인한 뒤에만 바꾼다.
        string dir = Path.Combine(Path.GetTempPath(), "yeondong-update");
        Directory.CreateDirectory(dir);
        byte[] exe = HttpGetBytes(exeUrl), sig = HttpGetBytes(sigUrl);
        File.WriteAllBytes(Path.Combine(dir, "link.exe"), exe);
        File.WriteAllBytes(Path.Combine(dir, "link.exe.sig"), sig);

        if (!Verify(exe, sig)) { message = tag + " 서명 확인 실패"; Log.Write("업데이트: " + message); return false; }

        Replace(exe);
        message = Program.Version + " -> " + tag + " 교체 완료";
        Log.Write("업데이트: " + message);
        return true;
    }

    static bool Verify(byte[] exe, byte[] sig)
    {
        try
        {
            using (var key = CngKey.Import(Convert.FromBase64String(PublicKey), CngKeyBlobFormat.EccPublicBlob))
            using (var ec = new ECDsaCng(key) { HashAlgorithm = CngAlgorithm.Sha256 })
                return ec.VerifyData(exe, sig);
        }
        catch (Exception e) { Log.Write("서명 확인 오류: " + e.Message); return false; }
    }

    // 실행 중인 exe도 이름은 바꿀 수 있다. 이름을 바꾸고 새 파일을 원래 자리에 쓴다.
    static void Replace(byte[] exe)
    {
        string path = Program.ExePath, dir = Path.GetDirectoryName(path);

        // 지난 업데이트가 남긴 옛 파일. 실행 중이라 잠긴 것은 지워지지 않으니 그냥 둔다.
        foreach (string f in Directory.GetFiles(dir, "link.old*.exe"))
        {
            try { File.Delete(f); } catch { }
        }

        string old = Path.Combine(dir, "link.old.exe");
        try { File.Move(path, old); }
        catch
        {
            old = Path.Combine(dir, "link.old-" + DateTime.UtcNow.Ticks + ".exe");
            File.Move(path, old);
        }
        try
        {
            File.WriteAllBytes(path, exe);
        }
        catch
        {
            // 쓰기 실패: 부분 파일을 치우고 이름을 되돌린다.
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            File.Move(old, path);
            throw;
        }
    }

    // "v0.5.0", "0.5.0.0" 같은 글을 Version으로. 빠진 자리는 0.
    static Version ParseVersion(string s)
    {
        if (s == null) return null;
        s = s.Trim();
        if (s.StartsWith("v") || s.StartsWith("V")) s = s.Substring(1);
        int dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s.Substring(0, dash);
        string[] p = s.Split('.');
        int[] n = new int[4];
        for (int i = 0; i < p.Length && i < 4; i++)
            if (!int.TryParse(p[i], out n[i])) return null;
        return new Version(n[0], n[1], n[2], n[3]);
    }

    static HttpWebRequest NewRequest(string url)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "yeondong";
        req.Timeout = 30000;
        req.ReadWriteTimeout = 60000;
        return req;
    }

    static string HttpGetString(string url)
    {
        using (var resp = NewRequest(url).GetResponse())
        using (var r = new StreamReader(resp.GetResponseStream(), System.Text.Encoding.UTF8))
            return r.ReadToEnd();
    }

    static byte[] HttpGetBytes(string url)
    {
        using (var resp = NewRequest(url).GetResponse())
        using (var s = resp.GetResponseStream())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
