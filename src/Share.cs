// 공유 폴더. 각 PC가 폴더 하나를 공유로 정해 두면(트레이 메뉴), 짝 맺은 상대 PC가 그 안을 창에서 둘러보고 파일을 받거나 올린다.
// 요청과 답은 ShareReq·ShareResp(json, tag로 짝을 맞춘다). 파일 내용은 끌어 놓기와 같은 FileReq 흐름으로 옮긴다(FileXfer.cs).
//   list {path}              → {items: [{name, size}]}   size -1은 폴더
//   get  {path, names}       → {id, entries}             보는 쪽이 FileReq로 항목마다 받는다
//   put  {path, id, entries} → {count}                   공유한 쪽이 보는 쪽에 FileReq로 받아 그 폴더에 쓴다
// 실패하면 {error: 글}. 공유 폴더 바깥(..\ 등)은 열지 않는다.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Share
{
    static readonly object gate = new object();
    static int nextId = 1 << 30;   // 끌어 놓기 목록 번호와 겹치지 않게
    static readonly Dictionary<int, List<FileEntry>> offers = new Dictionary<int, List<FileEntry>>();
    static readonly Queue<int> order = new Queue<int>();
    static int nextTag;
    static readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending = new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
    static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    public static Config Cfg;

    // FileReq를 받았을 때 끌어 놓기 목록에 없으면 여기서 찾는다
    public static FileEntry Entry(int id, int idx)
    {
        lock (gate)
        {
            List<FileEntry> l;
            return offers.TryGetValue(id, out l) && idx >= 0 && idx < l.Count ? l[idx] : null;
        }
    }

    public static int Register(List<FileEntry> list)
    {
        lock (gate)
        {
            int id = ++nextId;
            offers[id] = list;
            order.Enqueue(id);
            while (order.Count > 32) offers.Remove(order.Dequeue());
            return id;
        }
    }

    static byte[] Json(Dictionary<string, object> d) { return Encoding.UTF8.GetBytes(json.Serialize(d)); }

    // ---- 보는 쪽 ----
    public static Dictionary<string, object> Ask(Link l, Dictionary<string, object> req, int timeoutMs = 15000)
    {
        if (l == null || l.Closed) throw new IOException("상대 PC와 연결돼 있지 않음");
        var t = new TaskCompletionSource<Dictionary<string, object>>();
        int tag;
        lock (gate) { tag = ++nextTag; pending[tag] = t; }
        req["tag"] = tag;
        l.Send(Msg.ShareReq, Json(req));
        try
        {
            if (!t.Task.Wait(timeoutMs)) throw new IOException("상대 PC가 답하지 않음");
            var r = t.Task.Result;
            if (r.ContainsKey("error")) throw new IOException(Convert.ToString(r["error"]));
            return r;
        }
        finally { lock (gate) pending.Remove(tag); }
    }

    // 연결의 읽기 스레드에서 부른다. 처리했으면 true.
    public static bool Handle(Link l, byte type, byte[] body)
    {
        if (type == Msg.ShareResp)
        {
            var d = (Dictionary<string, object>)json.DeserializeObject(Encoding.UTF8.GetString(body));
            TaskCompletionSource<Dictionary<string, object>> t;
            lock (gate) pending.TryGetValue(Convert.ToInt32(d["tag"]), out t);
            if (t != null) t.TrySetResult(d);
            return true;
        }
        if (type != Msg.ShareReq) return false;
        new Thread(() => Serve(l, body)) { IsBackground = true, Name = "share" }.Start();
        return true;
    }

    // ---- 공유한 쪽 ----
    static void Serve(Link l, byte[] body)
    {
        object tag = 0;
        var r = new Dictionary<string, object>();
        try
        {
            var d = (Dictionary<string, object>)json.DeserializeObject(Encoding.UTF8.GetString(body));
            tag = d["tag"];
            string root = Cfg.SharePath;
            if (root == "" || !Directory.Exists(root)) throw new IOException("이 PC는 공유 폴더를 정해 두지 않았습니다");
            string dir = Resolve(root, d.ContainsKey("path") ? Convert.ToString(d["path"]) : "");
            if (!Directory.Exists(dir)) throw new IOException("없는 폴더입니다");
            switch (Convert.ToString(d["op"]))
            {
                case "list":
                    r["items"] = new DirectoryInfo(dir).GetFileSystemInfos()
                        .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                        .OrderBy(f => f is FileInfo).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                        .Select(f => new Dictionary<string, object> { { "name", f.Name }, { "size", f is FileInfo ? ((FileInfo)f).Length : -1L } })
                        .ToArray();
                    break;
                case "get":
                    var paths = ((IEnumerable)d["names"]).Cast<object>().Select(n => Resolve(dir, Convert.ToString(n))).ToArray();
                    var list = FileXfer.List(paths);
                    int id = Register(list);
                    r = (Dictionary<string, object>)json.DeserializeObject(Encoding.UTF8.GetString(FileXfer.OfferJson(id, list)));
                    Log.Write("공유 폴더에서 " + list.Count(e => e.Size >= 0) + "개 보냄: " + dir);
                    break;
                case "put":
                    int pid;
                    var entries = FileXfer.ParseOffer(Encoding.UTF8.GetBytes(json.Serialize(d)), out pid);
                    r["count"] = Receive(l, pid, entries, dir, null);
                    Log.Write("공유 폴더로 " + r["count"] + "개 받음: " + dir);
                    break;
                default: throw new IOException("모르는 요청");
            }
        }
        catch (Exception e) { r = new Dictionary<string, object> { { "error", e.Message } }; }
        r["tag"] = tag;
        l.Send(Msg.ShareResp, Json(r));
    }

    // 공유 폴더 안의 경로만 돌려준다
    static string Resolve(string root, string rel)
    {
        rel = (rel ?? "").Replace('/', '\\').Trim('\\');
        if (Path.IsPathRooted(rel) || rel.Split('\\').Any(x => x == "..")) throw new IOException("잘못된 경로");
        var full = Path.GetFullPath(rel == "" ? root : Path.Combine(root, rel));
        var r = Path.GetFullPath(root).TrimEnd('\\');
        if (!full.Equals(r, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("공유 폴더 바깥입니다");
        return full;
    }

    // 상대 PC 목록(id)의 항목을 dest 폴더에 받는다. 같은 이름이 있으면 "이름 (2)"로 둔다. 받은 파일 수를 돌려준다.
    public static int Receive(Link l, int id, List<FileEntry> entries, string dest, Action<string> progress)
    {
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // 맨 위 이름 → 실제로 쓴 이름
        int count = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var parts = e.Rel.Split('\\');
            string top;
            if (!renamed.TryGetValue(parts[0], out top)) renamed[parts[0]] = top = Free(dest, parts[0], e.Size < 0);
            parts[0] = top;
            var path = Path.Combine(dest, string.Join("\\", parts));
            if (e.Size < 0) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (progress != null) progress(e.Rel + " (" + FileXfer.Mb(e.Size) + ")");
            using (var src = FileXfer.Open(l, id, i))
            using (var dst = new FileStream(path, FileMode.CreateNew))
                src.CopyTo(dst, 1 << 16);
            count++;
        }
        return count;
    }

    static string Free(string dir, string name, bool folder)
    {
        string stem = folder ? name : Path.GetFileNameWithoutExtension(name), ext = folder ? "" : Path.GetExtension(name);
        var p = name;
        for (int n = 2; File.Exists(Path.Combine(dir, p)) || Directory.Exists(Path.Combine(dir, p)); n++) p = stem + " (" + n + ")" + ext;
        return p;
    }
}

// 상대 PC 공유 폴더를 둘러보는 창. 더블클릭으로 폴더에 들어가고, [받기]로 내려받고, 파일을 끌어다 놓으면 올린다.
class ShareForm : Form
{
    static readonly Color Bg = ColorTranslator.FromHtml("#202020"), Panel2 = ColorTranslator.FromHtml("#2b2b2b"),
                          Dim = ColorTranslator.FromHtml("#a0a0a0"), Accent = ColorTranslator.FromHtml("#0078d4");
    readonly Func<Link> link;
    readonly string peer;
    string path = "";
    readonly ListView list;
    readonly Label where, status;
    readonly Button up, get, refresh;
    volatile bool busy;

    public ShareForm(string peerName, Func<Link> linkOf)
    {
        peer = peerName;
        link = linkOf;
        Text = peerName + " 공유 폴더 - 연동";
        Icon = Tray.AppIcon;
        BackColor = Bg; ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5f);
        Size = new Size(720, 520);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 8, 8, 0), BackColor = Bg };
        up = Btn("위로", delegate { if (path != "") Go(path.Contains("\\") ? path.Substring(0, path.LastIndexOf('\\')) : ""); });
        refresh = Btn("새로고침", delegate { Go(path); });
        get = Btn("받기", delegate { Download(); });
        get.BackColor = Accent;
        top.Controls.AddRange(new Control[] { up, refresh, get });
        where = new Label { Dock = DockStyle.Top, Height = 24, Padding = new Padding(10, 4, 10, 0), ForeColor = Dim };
        status = new Label { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(10, 6, 10, 0), ForeColor = Dim,
            Text = "파일을 이 창에 끌어다 놓으면 지금 폴더로 올립니다." };
        list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BackColor = Panel2, ForeColor = Color.White,
            BorderStyle = BorderStyle.None, HideSelection = false };
        list.Columns.Add("이름", 480);
        list.Columns.Add("크기", 120, HorizontalAlignment.Right);
        list.DoubleClick += delegate
        {
            if (list.SelectedItems.Count == 1 && (long)list.SelectedItems[0].Tag < 0)
                Go(path == "" ? list.SelectedItems[0].Text : path + "\\" + list.SelectedItems[0].Text);
        };
        Controls.Add(list);
        Controls.Add(status);
        Controls.Add(where);
        Controls.Add(top);
        DragEnter += (s, e) => e.Effect = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (s, e) => Upload(e.Data.GetData(DataFormats.FileDrop) as string[]);
        Shown += delegate { Go(""); };
    }

    Button Btn(string text, EventHandler click)
    {
        var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Color.White, Margin = new Padding(0, 0, 8, 0) };
        b.FlatAppearance.BorderSize = 0;
        b.Click += click;
        return b;
    }

    void Say(string t) { try { BeginInvoke((Action)(() => status.Text = t)); } catch { } }

    // 일은 뒤에서 하고, 끝나면 단추를 다시 켠 뒤 after(화면 스레드)를 부른다
    void Run(Action work, Action after = null)
    {
        if (busy) return;
        busy = true;
        up.Enabled = get.Enabled = refresh.Enabled = false;
        new Thread(() =>
        {
            bool ok = false;
            try { work(); ok = true; }
            catch (Exception e) { Say("실패: " + e.Message); }
            finally
            {
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        busy = false;
                        up.Enabled = get.Enabled = refresh.Enabled = true;
                        if (ok && after != null) after();
                    }));
                }
                catch { busy = false; }
            }
        }) { IsBackground = true, Name = "share-ui" }.Start();
    }

    void Go(string p)
    {
        Run(() =>
        {
            var r = Share.Ask(link(), new Dictionary<string, object> { { "op", "list" }, { "path", p } });
            var items = ((IEnumerable)r["items"]).Cast<Dictionary<string, object>>().ToList();
            BeginInvoke((Action)(() =>
            {
                path = p;
                where.Text = peer + " \\ " + p;
                list.BeginUpdate();
                list.Items.Clear();
                foreach (var it in items)
                {
                    long size = Convert.ToInt64(it["size"]);
                    var row = new ListViewItem(new[] { Convert.ToString(it["name"]), size < 0 ? "폴더" : FileXfer.Mb(size) }) { Tag = size };
                    list.Items.Add(row);
                }
                list.EndUpdate();
                status.Text = items.Count + "개";
            }));
        });
    }

    void Download()
    {
        var names = list.SelectedItems.Cast<ListViewItem>().Select(i => i.Text).ToArray();
        if (names.Length == 0) { status.Text = "받을 것을 먼저 고르세요."; return; }
        string dest;
        using (var d = new FolderBrowserDialog { Description = "받을 폴더", SelectedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            dest = d.SelectedPath;
        }
        string from = path;
        Run(() =>
        {
            var l = link();
            var r = Share.Ask(l, new Dictionary<string, object> { { "op", "get" }, { "path", from }, { "names", names } });
            int id;
            var entries = FileXfer.ParseOffer(Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(r)), out id);
            int n = Share.Receive(l, id, entries, dest, t => Say("받는 중: " + t));
            Say(n + "개 받음: " + dest);
            Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true });
        });
    }

    void Upload(string[] paths)
    {
        if (paths == null || paths.Length == 0) return;
        string to = path;
        Run(() =>
        {
            var entries = FileXfer.List(paths);
            int id = Share.Register(entries);
            var req = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                .DeserializeObject(Encoding.UTF8.GetString(FileXfer.OfferJson(id, entries)));
            req["op"] = "put"; req["path"] = to;
            Say("올리는 중: " + entries.Count(e => e.Size >= 0) + "개 (" + FileXfer.Mb(entries.Where(e => e.Size > 0).Sum(e => e.Size)) + ")");
            var r = Share.Ask(link(), req, 24 * 3600 * 1000);   // 큰 파일은 오래 걸린다. 끊기면 그쪽 받기가 실패해 답이 온다
            Say(Convert.ToInt32(r["count"]) + "개 올림");
        }, () => Go(to));
    }
}
