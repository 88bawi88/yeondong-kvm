// 파일 끌어 놓기의 목록과 내용 주고받기. 끌고 넘어갈 때는 목록(이름·크기)만 보내고,
// 상대 PC에서 놓으면 탐색기가 항목마다 내용을 달라고 한다(VirtualFiles.cs). 그때 같은 연결로 FileReq를 보내 받아 온다.
//   받는 쪽: FileReq [목록 id][항목 번호][요청 번호] → 보내는 쪽: FileData [요청 번호][조각]... FileEnd [요청 번호][0|1]
//   받는 쪽은 1MB 읽을 때마다 FileAck [요청 번호][읽은 바이트]를 보낸다. 보내는 쪽은 8MB보다 앞서 나가지 않는다.
// 파일 조각은 느린 대기열로 보내므로(Net.cs) 큰 파일을 받는 동안에도 마우스·키 입력이 밀리지 않는다.
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

class FileEntry { public string Rel, Full; public long Size; }   // Size -1은 폴더. Full은 보내는 쪽에만 있다

static class FileXfer
{
    // 보낼 항목 목록(폴더는 안쪽까지). 위쪽 항목의 이름이 상대 경로의 맨 앞이 된다.
    public static List<FileEntry> List(string[] paths)
    {
        var r = new List<FileEntry>();
        foreach (var p in paths)
        {
            var root = p.TrimEnd('\\');
            var name = Path.GetFileName(root);
            if (Directory.Exists(root))
            {
                r.Add(new FileEntry { Rel = name, Size = -1 });
                foreach (var d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
                    r.Add(new FileEntry { Rel = name + "\\" + d.Substring(root.Length + 1), Size = -1 });
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    r.Add(new FileEntry { Rel = name + "\\" + f.Substring(root.Length + 1), Full = f, Size = new FileInfo(f).Length });
            }
            else if (File.Exists(root))
                r.Add(new FileEntry { Rel = name, Full = root, Size = new FileInfo(root).Length });
        }
        return r;
    }

    // 목록 알림(FileOffer) 내용: {"id": n, "entries": [{"rel": .., "size": ..}, ..]}
    public static byte[] OfferJson(int id, List<FileEntry> list)
    {
        var d = new Dictionary<string, object> {
            { "id", id },
            { "entries", list.Select(e => new Dictionary<string, object> { { "rel", e.Rel }, { "size", e.Size } }).ToArray() } };
        return Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(d));
    }

    public static List<FileEntry> ParseOffer(byte[] body, out int id)
    {
        var d = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(Encoding.UTF8.GetString(body));
        id = Convert.ToInt32(d["id"]);
        var r = new List<FileEntry>();
        foreach (Dictionary<string, object> e in (IEnumerable)d["entries"])
        {
            var rel = Convert.ToString(e["rel"]);
            // 받은 쪽 폴더 밖을 가리키는 이름(..\ 등)은 버린다
            if (Path.IsPathRooted(rel) || rel.Split('\\').Any(x => x == ".." || x == "")) continue;
            r.Add(new FileEntry { Rel = rel, Size = Convert.ToInt64(e["size"]) });
        }
        return r;
    }

    public static string Mb(long n) { return n < 1 << 20 ? (n / 1024 + 1) + "KB" : (n >> 20) + "MB"; }

    public static byte[] Ints(params int[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(b, i * 4);
        return b;
    }

    // ---------- 보내는 쪽 ----------
    const int Chunk = 64 * 1024;
    const long Window = 8L << 20;
    static readonly Dictionary<string, long> acked = new Dictionary<string, long>();   // 연결+요청 번호 → 상대가 읽은 바이트
    static readonly object ackGate = new object();

    static string Key(Link l, int req) { return l.GetHashCode() + ":" + req; }

    // FileReq를 받았다. lookup(목록 id, 항목 번호)은 보낼 항목(없으면 null).
    public static void Serve(Link l, byte[] body, Func<int, int, FileEntry> lookup)
    {
        int id = BitConverter.ToInt32(body, 0), idx = BitConverter.ToInt32(body, 4), req = BitConverter.ToInt32(body, 8);
        new Thread(() =>
        {
            var key = Key(l, req);
            lock (ackGate) acked[key] = 0;
            byte status = 1;
            try
            {
                var e = lookup(id, idx);
                if (e == null || e.Size < 0) throw new IOException("없는 항목");
                var buf = new byte[4 + Chunk];
                BitConverter.GetBytes(req).CopyTo(buf, 0);
                long sent = 0;
                using (var f = new FileStream(e.Full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    while (sent < e.Size)
                    {
                        lock (ackGate)
                            while (sent - acked[key] > Window && !l.Closed) Monitor.Wait(ackGate, 1000);
                        int n = f.Read(buf, 4, (int)Math.Min(Chunk, e.Size - sent));
                        if (n <= 0) throw new IOException("파일이 보내는 중에 줄어듦");
                        var part = new byte[4 + n];
                        Array.Copy(buf, part, part.Length);
                        if (!l.SendSlow(Msg.FileData, part)) throw new IOException("연결 끊김");
                        sent += n;
                    }
                }
                status = 0;
            }
            catch (Exception x) { Log.Write("파일 보내기 실패: " + x.Message); }
            finally { lock (ackGate) acked.Remove(key); }
            var end = new byte[5];
            BitConverter.GetBytes(req).CopyTo(end, 0);
            end[4] = status;
            l.SendSlow(Msg.FileEnd, end);
        }) { IsBackground = true, Name = "file-send" }.Start();
    }

    // ---------- 받는 쪽 ----------
    static readonly Dictionary<string, Incoming> incoming = new Dictionary<string, Incoming>();
    static int reqId;

    // 상대 PC의 그 항목을 읽는 스트림. 처음 Read할 때 막히지 않게 요청은 바로 보낸다.
    public static Stream Open(Link l, int id, int idx)
    {
        if (l == null || l.Closed) throw new IOException("상대 PC와 연결돼 있지 않음");
        int req = Interlocked.Increment(ref reqId);
        var s = new Incoming(l, req);
        lock (incoming) incoming[Key(l, req)] = s;
        l.Send(Msg.FileReq, Ints(id, idx, req));
        return s;
    }

    // 연결의 읽기 스레드에서 FileData·FileEnd·FileAck를 넘겨받는다. 처리했으면 true.
    public static bool Handle(Link l, byte type, byte[] b)
    {
        if (type == Msg.FileAck)
        {
            var key = Key(l, BitConverter.ToInt32(b, 0));
            lock (ackGate) { if (acked.ContainsKey(key)) acked[key] = BitConverter.ToInt64(b, 4); Monitor.PulseAll(ackGate); }
            return true;
        }
        if (type != Msg.FileData && type != Msg.FileEnd) return false;
        Incoming s;
        lock (incoming) incoming.TryGetValue(Key(l, BitConverter.ToInt32(b, 0)), out s);
        if (s == null) return true;
        if (type == Msg.FileData) s.Push(b);
        else s.End(b[4] == 0);
        return true;
    }

    // 연결이 끊기면 그 연결로 받던 것을 모두 실패시킨다.
    public static void Closed(Link l)
    {
        List<Incoming> list;
        lock (incoming) list = incoming.Values.Where(x => x.Link == l).ToList();
        foreach (var s in list) s.End(false);
        lock (ackGate) Monitor.PulseAll(ackGate);
    }

    class Incoming : Stream
    {
        public readonly Link Link;
        readonly int req;
        readonly BlockingCollection<byte[]> q = new BlockingCollection<byte[]>();
        byte[] cur; int curPos;
        long read, ackedAt;
        volatile bool failed;

        public Incoming(Link l, int req) { Link = l; this.req = req; }

        public void Push(byte[] b) { try { q.Add(b); } catch (InvalidOperationException) { } }
        public void End(bool ok) { if (!ok) failed = true; try { q.CompleteAdding(); } catch { } }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (cur == null || curPos >= cur.Length)
            {
                byte[] next;
                if (!q.TryTake(out next, 60000))
                {
                    if (q.IsCompleted) { if (failed) throw new IOException("상대 PC가 파일을 보내지 못함"); return 0; }
                    throw new IOException("60초 동안 파일 내용이 오지 않음");
                }
                cur = next; curPos = 4;   // 앞 4바이트는 요청 번호
            }
            int n = Math.Min(count, cur.Length - curPos);
            Array.Copy(cur, curPos, buffer, offset, n);
            curPos += n;
            read += n;
            if (read - ackedAt >= (1 << 20))
            {
                ackedAt = read;
                var a = new byte[12];
                BitConverter.GetBytes(req).CopyTo(a, 0);
                BitConverter.GetBytes(read).CopyTo(a, 4);
                Link.Send(Msg.FileAck, a);
            }
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            lock (incoming) incoming.Remove(Key(Link, req));
            base.Dispose(disposing);
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) { throw new NotSupportedException(); }
        public override void SetLength(long v) { throw new NotSupportedException(); }
        public override void Write(byte[] b, int o, int n) { throw new NotSupportedException(); }
    }
}
