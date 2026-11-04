// 공유 폴더 동기화. 두 PC가 각자 공유 폴더를 정해 두면(트레이 메뉴), 연결된 동안 두 폴더를 같게 맞춘다.
//   연결되면 양쪽이 SyncList {files: [{rel, size, mtime}], dirs: [rel]}를 보낸다. 받은 쪽은 상대에게 없거나 자기 것이 더 새 파일을 보낸다.
//   폴더가 바뀌면(FileSystemWatcher) 그때그때 SyncOp를 보낸다: put {rel, size, mtime, id} / del {rel} / mkdir {rel} / move {rel, to}
//   put을 받은 쪽은 FileReq(FileXfer.cs)로 내용을 받아 임시 이름(.~yd…)으로 쓴 뒤 제자리로 옮긴다. 지울 때는 휴지통으로 보낸다.
//   떨어져 있는 동안 지운 것: 마지막으로 맞춘 목록(sync-<상대 id>.json)에 있었고 내 것은 그때 그대로인데 상대에게 없으면, 상대가 지운 것으로 보고 지운다.
//   메인 PC는 서브에서 받은 변경을 다른 서브에도 넘긴다.
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.VisualBasic.FileIO;

static class Share
{
    public static Config Cfg;
    static readonly object gate = new object();
    static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    const long Slack = 2 * TimeSpan.TicksPerSecond;   // 시각이 이만큼 안이면 같은 것으로 본다

    // ---- 상대가 받아 갈 파일(FileReq 목록 id) ----
    static int nextId = 1 << 30;   // 끌어 놓기 목록 번호와 겹치지 않게
    static readonly Dictionary<int, FileEntry> offers = new Dictionary<int, FileEntry>();
    static readonly Queue<int> order = new Queue<int>();

    public static FileEntry Entry(int id, int idx)
    {
        lock (gate) { FileEntry e; return idx == 0 && offers.TryGetValue(id, out e) ? e : null; }
    }

    static int Register(FileEntry e)
    {
        lock (gate)
        {
            int id = ++nextId;
            offers[id] = e;
            order.Enqueue(id);
            while (order.Count > 20000) offers.Remove(order.Dequeue());
            return id;
        }
    }

    // ---- 연결된 상대 ----
    class Peer
    {
        public Link L;
        public string Id;
        public readonly BlockingCollection<Action> Work = new BlockingCollection<Action>();   // 받은 일을 차례로(받기는 오래 걸린다)
        public Dictionary<string, long> Known;   // 마지막으로 양쪽에 같이 있던 파일 → 수정 시각
        public bool RecvOnly;                    // 상대는 받기만 한다(상대에 없는 것을 지우지 않고, 이미 준 것을 다시 주지 않는다)
    }
    static readonly List<Peer> peers = new List<Peer>();

    static string Root
    {
        get { var r = Cfg == null ? "" : Cfg.SharePath; return r != "" && Directory.Exists(r) ? Path.GetFullPath(r).TrimEnd('\\') : null; }
    }

    static bool RecvOnly { get { return Cfg != null && Cfg.ShareRecvOnly; } }

    public static void Attach(Link l, string peerId)
    {
        var p = new Peer { L = l, Id = peerId };
        p.Known = LoadKnown(peerId);
        lock (gate) { peers.RemoveAll(x => x.L == l); peers.Add(p); }
        new Thread(() => { foreach (var a in p.Work.GetConsumingEnumerable()) { try { a(); } catch (Exception e) { Log.Write("동기화 오류: " + e.Message); } } })
            { IsBackground = true, Name = "sync-in" }.Start();
        Watch();
        SendList(p);
    }

    public static void Detach(Link l)
    {
        lock (gate)
        {
            foreach (var p in peers.Where(x => x.L == l)) p.Work.CompleteAdding();
            peers.RemoveAll(x => x.L == l);
        }
    }

    // 공유 폴더를 바꾸거나 껐을 때
    public static void Restart()
    {
        Watch();
        List<Peer> ps; lock (gate) ps = peers.ToList();
        foreach (var p in ps) SendList(p);
    }

    static void SendList(Peer p)
    {
        var root = Root;
        if (root == null || p.L.Closed) return;
        var files = new List<object>(); var dirs = new List<object>();
        foreach (var kv in Scan(root, dirs)) files.Add(new Dictionary<string, object> { { "rel", kv.Key }, { "size", kv.Value.Length }, { "mtime", kv.Value.LastWriteTimeUtc.Ticks } });
        p.L.Send(Msg.SyncList, Encoding.UTF8.GetBytes(json.Serialize(new Dictionary<string, object> { { "files", files }, { "dirs", dirs }, { "recvOnly", RecvOnly } })));
        Log.Write("동기화 목록 보냄(" + p.L.Name + "): 파일 " + files.Count + "개");
    }

    static Dictionary<string, FileInfo> Scan(string root, List<object> dirs)
    {
        var r = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in new DirectoryInfo(root).EnumerateDirectories("*", System.IO.SearchOption.AllDirectories))
            if (!Skip(d.Name) && dirs != null) dirs.Add(d.FullName.Substring(root.Length + 1));
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", System.IO.SearchOption.AllDirectories))
            if (!Skip(f.Name)) r[f.FullName.Substring(root.Length + 1)] = f;
        return r;
    }

    static bool Skip(string name)
    {
        // 연동 임시 파일, 바꿔치기할 때 Windows가 잠깐 만드는 이름(~RF….TMP), 오피스 잠금 파일(~$…), 브라우저가 받는 중인 파일은 맞추지 않는다
        return name.StartsWith(".~yd")
            || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".download", StringComparison.OrdinalIgnoreCase) || name.StartsWith("~$") || (name.Contains("~RF") && name.EndsWith(".TMP", StringComparison.OrdinalIgnoreCase))
            || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);
    }

    // 연결의 읽기 스레드에서 부른다. 처리했으면 true.
    public static bool Handle(Link l, byte type, byte[] body)
    {
        if (type != Msg.SyncList && type != Msg.SyncOp) return false;
        Peer p; lock (gate) p = peers.FirstOrDefault(x => x.L == l);
        if (p == null || Root == null) return true;
        var d = (Dictionary<string, object>)json.DeserializeObject(Encoding.UTF8.GetString(body));
        try { p.Work.Add(() => { if (type == Msg.SyncList) Reconcile(p, d); else Apply(p, d); }); } catch (InvalidOperationException) { }
        return true;
    }

    // ---- 처음 맞추기 ----
    static void Reconcile(Peer p, Dictionary<string, object> d)
    {
        var root = Root; if (root == null) return;
        p.RecvOnly = d.ContainsKey("recvOnly") && Convert.ToBoolean(d["recvOnly"]);
        if (RecvOnly) return;   // 받기만 하니 상대 목록에 맞춰 보내거나 지우지 않는다
        var remote = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, object> f in (IEnumerable)d["files"]) remote[Convert.ToString(f["rel"])] = Convert.ToInt64(f["mtime"]);
        foreach (var rel in p.RecvOnly ? Enumerable.Empty<string>() : ((IEnumerable)d["dirs"]).Cast<object>().Select(Convert.ToString))
        {
            var path = Resolve(root, rel);
            if (path != null && !Directory.Exists(path) && !File.Exists(path)) { Quiet(rel); Directory.CreateDirectory(path); }
        }
        int sent = 0, gone = 0;
        foreach (var kv in Scan(root, null))
        {
            long mine = kv.Value.LastWriteTimeUtc.Ticks, theirs, known;
            if (remote.TryGetValue(kv.Key, out theirs))
            {
                if (mine > theirs + Slack) { Put(p, kv.Key); sent++; }
                else if (Math.Abs(mine - theirs) <= Slack) p.Known[kv.Key] = mine;
            }
            else if (p.Known.TryGetValue(kv.Key, out known) && Math.Abs(known - mine) <= Slack)
            {
                if (p.RecvOnly) continue;   // 이미 준 것을 상대가 지웠다. 다시 주지도, 여기서 지우지도 않는다
                Delete(kv.Key); p.Known.Remove(kv.Key); gone++;
            }
            else { Put(p, kv.Key); sent++; }
        }
        SaveKnown(p);
        Log.Write("동기화 맞춤(" + p.L.Name + "): 보냄 " + sent + ", 상대에서 지워져 지움 " + gone);
    }

    // ---- 바뀐 것 받기 ----
    static void Apply(Peer p, Dictionary<string, object> d)
    {
        var root = Root; if (root == null) return;
        string op = Convert.ToString(d["op"]), rel = Convert.ToString(d["rel"]);
        var path = Resolve(root, rel);
        if (path == null) return;
        switch (op)
        {
            case "put":
                long mtime = Convert.ToInt64(d["mtime"]), size = Convert.ToInt64(d["size"]);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length == size && Math.Abs(fi.LastWriteTimeUtc.Ticks - mtime) <= Slack) { p.Known[rel] = mtime; break; }
                if (fi.Exists && fi.LastWriteTimeUtc.Ticks > mtime + Slack) break;   // 내 것이 더 새것(곧 상대에게 간다)
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = Path.Combine(Path.GetDirectoryName(path), ".~yd" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".part");
                Quiet(rel);
                try
                {
                    using (var src = FileXfer.Open(p.L, Convert.ToInt32(d["id"]), 0))
                    using (var dst = new FileStream(tmp, FileMode.CreateNew))
                        src.CopyTo(dst, 1 << 16);
                    File.SetLastWriteTimeUtc(tmp, new DateTime(mtime, DateTimeKind.Utc));
                    if (File.Exists(path)) { if (RecvOnly) Delete(rel); else File.Delete(path); }   // 받기만 하는 폴더(다운로드 등)에 있던 같은 이름 파일은 휴지통으로
                    File.Move(tmp, path);
                }
                finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } Quiet(rel); }
                p.Known[rel] = mtime;
                Log.Write("동기화 받음: " + rel + " (" + FileXfer.Mb(size) + ")");
                Relay(p, delegate(Peer o) { Put(o, rel); });
                break;
            case "del":
                if (RecvOnly) break;   // 받은 것은 상대가 지워도 남겨 둔다
                if (!File.Exists(path) && !Directory.Exists(path)) break;
                Delete(rel);
                ForgetUnder(p, rel);
                Log.Write("동기화 지움: " + rel);
                Relay(p, delegate(Peer o) { Send(o, "del", rel); });
                break;
            case "mkdir":
                if (!Directory.Exists(path)) { Quiet(rel); Directory.CreateDirectory(path); }
                Relay(p, delegate(Peer o) { Send(o, "mkdir", rel); });
                break;
            case "move":
                string to = Convert.ToString(d["to"]);
                var dst2 = Resolve(root, to);
                if (dst2 == null || File.Exists(dst2) || Directory.Exists(dst2)) break;
                Quiet(rel); Quiet(to);
                Directory.CreateDirectory(Path.GetDirectoryName(dst2));
                if (File.Exists(path)) File.Move(path, dst2);
                else if (Directory.Exists(path)) Directory.Move(path, dst2);
                else break;
                Rekey(p, rel, to);
                Log.Write("동기화 옮김: " + rel + " → " + to);
                Relay(p, delegate(Peer o) { Send(o, "move", rel, to); });
                break;
        }
        SaveKnown(p);
    }

    // 메인 PC가 서브에서 받은 변경을 다른 서브에 넘긴다(서브는 메인 하나뿐이라 넘길 곳이 없다)
    static void Relay(Peer from, Action<Peer> a)
    {
        List<Peer> ps; lock (gate) ps = peers.Where(x => x != from && !x.L.Closed).ToList();
        foreach (var o in ps) a(o);
    }

    static void Put(Peer p, string rel)
    {
        var root = Root; if (root == null) return;
        var fi = new FileInfo(Path.Combine(root, rel));
        if (!fi.Exists) return;
        if (Busy(fi)) { lock (dirty) dirty[rel] = DateTime.UtcNow; return; }   // 쓰기가 끝나면 Flush가 보낸다
        int id = Register(new FileEntry { Rel = rel, Full = fi.FullName, Size = fi.Length });
        p.Known[rel] = fi.LastWriteTimeUtc.Ticks;
        p.L.Send(Msg.SyncOp, Encoding.UTF8.GetBytes(json.Serialize(new Dictionary<string, object> {
            { "op", "put" }, { "rel", rel }, { "size", fi.Length }, { "mtime", fi.LastWriteTimeUtc.Ticks }, { "id", id } })));
    }

    static void Send(Peer p, string op, string rel, string to = null)
    {
        var d = new Dictionary<string, object> { { "op", op }, { "rel", rel } };
        if (to != null) d["to"] = to;
        if (op == "del") ForgetUnder(p, rel);
        if (op == "move") Rekey(p, rel, to);
        p.L.Send(Msg.SyncOp, Encoding.UTF8.GetBytes(json.Serialize(d)));
    }

    static void Delete(string rel)
    {
        var path = Path.Combine(Root, rel);
        Quiet(rel);
        if (File.Exists(path)) FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
        else if (Directory.Exists(path)) FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
        Quiet(rel);
    }

    static void ForgetUnder(Peer p, string rel)
    {
        foreach (var k in p.Known.Keys.Where(k => k.Equals(rel, StringComparison.OrdinalIgnoreCase) || k.StartsWith(rel + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            p.Known.Remove(k);
    }

    static void Rekey(Peer p, string from, string to)
    {
        foreach (var k in p.Known.Keys.Where(k => k.Equals(from, StringComparison.OrdinalIgnoreCase) || k.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            long v = p.Known[k];
            p.Known.Remove(k);
            p.Known[to + k.Substring(from.Length)] = v;
        }
    }

    // 공유 폴더 안의 경로만 돌려준다(..\ 등은 null)
    static string Resolve(string root, string rel)
    {
        rel = (rel ?? "").Replace('/', '\\').Trim('\\');
        if (rel == "" || Path.IsPathRooted(rel) || rel.Split('\\').Any(x => x == ".." || x == "." || x == "")) return null;
        var full = Path.GetFullPath(Path.Combine(root, rel));
        return full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    // ---- 마지막으로 맞춘 목록 ----
    static string KnownPath(string peerId) { return Path.Combine(Config.Dir, "sync-" + peerId + ".json"); }

    static Dictionary<string, long> LoadKnown(string peerId)
    {
        var r = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(KnownPath(peerId)))
                foreach (var kv in (Dictionary<string, object>)json.DeserializeObject(File.ReadAllText(KnownPath(peerId))))
                    r[kv.Key] = Convert.ToInt64(kv.Value);
        }
        catch (Exception e) { Log.Write("동기화 목록 읽기 실패: " + e.Message); }
        return r;
    }

    static void SaveKnown(Peer p)
    {
        try { lock (p.Known) File.WriteAllText(KnownPath(p.Id), json.Serialize(p.Known.ToDictionary(x => x.Key, x => (object)x.Value))); }
        catch (Exception e) { Log.Write("동기화 목록 저장 실패: " + e.Message); }
    }

    // ---- 이 PC 폴더 지켜보기 ----
    static FileSystemWatcher watcher;
    static string watching;
    static readonly Dictionary<string, DateTime> quiet = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);   // 받아서 바꾸는 중인 것(되돌려 보내지 않게)
    static readonly Dictionary<string, DateTime> dirty = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);   // 바뀐 것 → 마지막으로 바뀐 때
    static Thread flusher;

    static void Quiet(string rel) { lock (quiet) quiet[rel] = DateTime.UtcNow.AddSeconds(3); }

    static bool IsQuiet(string rel)
    {
        lock (quiet)
        {
            foreach (var kv in quiet.ToList())
            {
                if (kv.Value < DateTime.UtcNow) { quiet.Remove(kv.Key); continue; }
                if (rel.Equals(kv.Key, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(kv.Key + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    static void Watch()
    {
        var root = Root;
        bool any; lock (gate) any = peers.Count > 0;
        if (!any || RecvOnly) root = null;
        if (root == watching) return;
        if (watcher != null) { watcher.Dispose(); watcher = null; }
        watching = root;
        if (root == null) return;
        watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        FileSystemEventHandler mark = (s, e) => Mark(root, e.FullPath);
        watcher.Created += mark; watcher.Changed += mark; watcher.Deleted += mark;
        watcher.Renamed += (s, e) => Renamed(root, e.OldFullPath, e.FullPath);
        watcher.Error += (s, e) => { Log.Write("폴더 감시 놓침, 다시 맞춤: " + e.GetException().Message); Restart(); };
        watcher.EnableRaisingEvents = true;
        if (flusher == null) { flusher = new Thread(Flush) { IsBackground = true, Name = "sync-watch" }; flusher.Start(); }
        Log.Write("공유 폴더 동기화 시작: " + root);
    }

    static void Mark(string root, string full)
    {
        if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) || Skip(Path.GetFileName(full))) return;
        var rel = full.Substring(root.Length + 1);
        if (IsQuiet(rel)) return;
        lock (dirty) dirty[rel] = DateTime.UtcNow;
    }

    static void Renamed(string root, string oldFull, string newFull)
    {
        if (!newFull.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return;
        string from = oldFull.Substring(root.Length + 1), to = newFull.Substring(root.Length + 1);
        if (Skip(Path.GetFileName(newFull))) return;
        if (IsQuiet(from) || IsQuiet(to)) return;   // 받아서 바꾸는 중
        if (Skip(Path.GetFileName(oldFull))) { Mark(root, newFull); return; }   // 임시 이름에서 바뀐 것은 새 파일로
        lock (dirty) dirty.Remove(from);
        List<Peer> ps; lock (gate) ps = peers.Where(x => !x.L.Closed).ToList();
        foreach (var p in ps) Send(p, "move", from, to);
        Log.Write("동기화 보냄(옮김): " + from + " → " + to);
    }

    // 다른 프로그램이 쓰기로 열어 두었거나 2초 안에 바뀐 파일. 다 받기 전에 보내면 커질 때마다 처음부터 다시 보내게 된다.
    static bool Busy(FileInfo fi)
    {
        fi.Refresh();
        var age = (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalSeconds;
        if (age >= 0 && age < 2) return true;
        try { using (new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.Read)) { } return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // 0.8초 넘게 조용해진 것만 보낸다. 아직 쓰는 중인 파일은 다음에.
    static void Flush()
    {
        while (true)
        {
            Thread.Sleep(500);
            var root = watching; if (root == null) continue;
            List<string> ready;
            lock (dirty) ready = dirty.Where(kv => (DateTime.UtcNow - kv.Value).TotalMilliseconds > 800).Select(kv => kv.Key).ToList();
            foreach (var rel in ready)
            {
                var full = Path.Combine(root, rel);
                if (File.Exists(full) && Busy(new FileInfo(full))) continue;   // 아직 쓰는 중
                lock (dirty) dirty.Remove(rel);
                List<Peer> ps; lock (gate) ps = peers.Where(x => !x.L.Closed).ToList();
                try
                {
                    if (File.Exists(full)) foreach (var p in ps) Put(p, rel);
                    else if (Directory.Exists(full))
                        foreach (var p in ps)
                        {
                            Send(p, "mkdir", rel);
                            foreach (var f in Directory.GetFiles(full, "*", System.IO.SearchOption.AllDirectories))
                                if (!Skip(Path.GetFileName(f))) Put(p, f.Substring(root.Length + 1));
                        }
                    else foreach (var p in ps) Send(p, "del", rel);
                    foreach (var p in ps) SaveKnown(p);
                    Log.Write("동기화 보냄: " + rel + (File.Exists(full) ? "" : Directory.Exists(full) ? " (폴더)" : " (지움)"));
                }
                catch (Exception e) { Log.Write("동기화 보내기 실패(" + rel + "): " + e.Message); }
            }
        }
    }
}
