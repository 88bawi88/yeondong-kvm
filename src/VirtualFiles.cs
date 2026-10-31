// 상대 PC 파일을 "가상 파일"로 클립보드(또는 끌어 놓기)에 올리는 데이터 개체.
// 탐색기는 FileGroupDescriptorW로 이름·크기를 보고, 붙여넣을 때 FileContents로 항목마다 스트림을 달라고 한다.
// 그때 open(번호)로 상대 PC에서 받아 오는 연결을 열고, 탐색기가 Read를 부를 때마다 연결에서 그대로 읽어 넘긴다.
// .NET이 만들어 주는 COM 래퍼는 어느 스레드에서든 불리므로(자유 스레드) 받는 동안 화면 스레드가 멈추지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using STATSTG = System.Runtime.InteropServices.ComTypes.STATSTG;

class VirtualFiles : System.Runtime.InteropServices.ComTypes.IDataObject
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM m);

    public static readonly short CfDescriptor = (short)RegisterClipboardFormat("FileGroupDescriptorW");
    public static readonly short CfContents = (short)RegisterClipboardFormat("FileContents");
    public static readonly short CfDropEffect = (short)RegisterClipboardFormat("Preferred DropEffect");
    const int S_OK = 0, DV_E_FORMATETC = unchecked((int)0x80040064), E_NOTIMPL = unchecked((int)0x80004001),
              OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003), DV_E_LINDEX = unchecked((int)0x80040068);

    readonly List<FileEntry> entries;
    readonly Func<int, Stream> open;
    public Action<string> Say;   // 받기 시작·끝 알림(어느 스레드에서든 불린다)

    public VirtualFiles(List<FileEntry> entries, Func<int, Stream> open)
    {
        this.entries = entries;
        this.open = open;
    }

    // ---------- IDataObject ----------
    public void GetData(ref FORMATETC f, out STGMEDIUM m)
    {
        m = new STGMEDIUM();
        if (f.cfFormat == CfDescriptor && (f.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            m.tymed = TYMED.TYMED_HGLOBAL;
            m.unionmember = Descriptor();
        }
        else if (f.cfFormat == CfDropEffect && (f.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            m.tymed = TYMED.TYMED_HGLOBAL;
            m.unionmember = Global(BitConverter.GetBytes(1));   // 복사
        }
        else if (f.cfFormat == CfContents && (f.tymed & TYMED.TYMED_ISTREAM) != 0)
        {
            int i = f.lindex;
            if (i < 0 || i >= entries.Count || entries[i].Size < 0) throw new COMException("항목 번호가 이상함", DV_E_LINDEX);
            m.tymed = TYMED.TYMED_ISTREAM;
            m.unionmember = Marshal.GetComInterfaceForObject(new NetStream(this, i, entries[i]), typeof(IStream));
        }
        else throw new COMException("형식 없음", DV_E_FORMATETC);
    }

    public void GetDataHere(ref FORMATETC f, ref STGMEDIUM m) { throw new COMException("", E_NOTIMPL); }

    public int QueryGetData(ref FORMATETC f)
    {
        if (f.cfFormat == CfDescriptor || f.cfFormat == CfDropEffect) return (f.tymed & TYMED.TYMED_HGLOBAL) != 0 ? S_OK : DV_E_FORMATETC;
        if (f.cfFormat == CfContents) return (f.tymed & TYMED.TYMED_ISTREAM) != 0 ? S_OK : DV_E_FORMATETC;
        return DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC i, out FORMATETC o) { o = i; o.ptd = IntPtr.Zero; return 0x00040130; }   // DATA_S_SAMEFORMATETC

    // 탐색기가 붙여넣은 결과(Performed DropEffect 등)를 적어 넣는다. 받아만 두고 버린다.
    public void SetData(ref FORMATETC f, ref STGMEDIUM m, bool release) { if (release) ReleaseStgMedium(ref m); }

    public IEnumFORMATETC EnumFormatEtc(DATADIR dir)
    {
        if (dir != DATADIR.DATADIR_GET) throw new COMException("", E_NOTIMPL);
        return new FormatEnum(new[] {
            Fmt(CfDescriptor, TYMED.TYMED_HGLOBAL, -1), Fmt(CfContents, TYMED.TYMED_ISTREAM, -1), Fmt(CfDropEffect, TYMED.TYMED_HGLOBAL, -1) }, 0);
    }

    public int DAdvise(ref FORMATETC f, ADVF a, IAdviseSink s, out int c) { c = 0; return OLE_E_ADVISENOTSUPPORTED; }
    public void DUnadvise(int c) { throw new COMException("", OLE_E_ADVISENOTSUPPORTED); }
    public int EnumDAdvise(out IEnumSTATDATA e) { e = null; return OLE_E_ADVISENOTSUPPORTED; }

    static FORMATETC Fmt(short cf, TYMED t, int lindex)
    {
        return new FORMATETC { cfFormat = cf, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = lindex, ptd = IntPtr.Zero, tymed = t };
    }

    // FILEGROUPDESCRIPTORW: 항목 수(4바이트) + FILEDESCRIPTORW(592바이트) × 항목 수
    IntPtr Descriptor()
    {
        const int D = 592;
        var b = new byte[4 + D * entries.Count];
        BitConverter.GetBytes(entries.Count).CopyTo(b, 0);
        for (int i = 0; i < entries.Count; i++)
        {
            int o = 4 + D * i;
            var e = entries[i];
            BitConverter.GetBytes(0x4 | 0x40 | 0x4000).CopyTo(b, o);                    // FD_ATTRIBUTES | FD_FILESIZE | FD_PROGRESSUI
            BitConverter.GetBytes(e.Size < 0 ? 0x10 : 0x80).CopyTo(b, o + 36);          // 폴더 / 보통 파일
            long size = Math.Max(0, e.Size);
            BitConverter.GetBytes((uint)(size >> 32)).CopyTo(b, o + 64);
            BitConverter.GetBytes((uint)(size & 0xFFFFFFFF)).CopyTo(b, o + 68);
            var name = Encoding.Unicode.GetBytes(e.Rel.Length > 259 ? e.Rel.Substring(0, 259) : e.Rel);
            Array.Copy(name, 0, b, o + 72, name.Length);
        }
        return Global(b);
    }

    static IntPtr Global(byte[] data)
    {
        var h = GlobalAlloc(0x0042, new UIntPtr((uint)data.Length));   // GMEM_MOVEABLE | GMEM_ZEROINIT
        var p = GlobalLockPtr(h);
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlockPtr(h);
        return h;
    }

    [DllImport("kernel32.dll", EntryPoint = "GlobalLock")] static extern IntPtr GlobalLockPtr(IntPtr h);
    [DllImport("kernel32.dll", EntryPoint = "GlobalUnlock")] static extern bool GlobalUnlockPtr(IntPtr h);

    // ---------- 끌기 ----------
    [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag(int escapePressed, int keyState);
        [PreserveSig] int GiveFeedback(int effect);
    }

    class DropSource : IDropSource
    {
        // Esc면 취소, 왼쪽 단추를 떼면 놓기
        public int QueryContinueDrag(int esc, int keys) { return esc != 0 ? 0x00040101 : (keys & 1) == 0 ? 0x00040100 : 0; }
        public int GiveFeedback(int effect) { return 0x00040102; }   // DRAGDROP_S_USEDEFAULTCURSORS
    }

    [DllImport("ole32.dll")] static extern int DoDragDrop(System.Runtime.InteropServices.ComTypes.IDataObject d, IDropSource s, int ok, out int effect);

    // 뒤에서 도는 프로그램이 그냥 DoDragDrop을 부르면 마우스 입력이 이쪽으로 오지 않아 끌기가 멈춘다.
    // 그래서 커서 밑에 거의 투명한 창을 띄우고 pressDown으로 왼쪽 단추를 누르게 해, 그 창이 눌림을 받은 순간 끌기를 시작한다.
    // 화면 스레드에서 부른다. 끌기는 놓을 때까지 그 창의 MouseDown 안에서 돈다(그동안에도 메시지는 돈다).
    public static void StartAt(System.Drawing.Point p, VirtualFiles v, Action pressDown)
    {
        if (starter == null)
        {
            starter = new StartForm();
            starter.MouseDown += delegate
            {
                var cur = pending;
                pending = null;
                if (cur == null) return;
                System.Windows.Forms.Application.OleRequired();
                int effect;
                int hr = DoDragDrop(cur, new DropSource(), 1, out effect);   // 복사만
                starter.Hide();
                Log.Write(hr == 0x00040100 ? "끌어 놓기 끝" : "끌기가 놓이지 않고 끝남, 0x" + hr.ToString("X8"));
            };
        }
        pending = v;
        starter.Location = new System.Drawing.Point(p.X - 24, p.Y - 24);
        starter.Show();
        starter.Update();
        pressDown();
        // 눌림이 다른 데로 가서 끌기가 시작되지 않았으면 창을 치운다
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += delegate { t.Stop(); t.Dispose(); if (pending == v) { pending = null; starter.Hide(); Log.Write("끌기를 시작하지 못함"); } };
        t.Start();
    }

    static StartForm starter;
    static VirtualFiles pending;

    class StartForm : System.Windows.Forms.Form
    {
        public StartForm()
        {
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            Size = new System.Drawing.Size(48, 48);
            MinimumSize = Size;
            Opacity = 0.01;
        }
        // WS_EX_TOOLWINDOW | WS_EX_TOPMOST. 맨 위는 만들 때 줘야 한다
        protected override System.Windows.Forms.CreateParams CreateParams
        {
            get { var c = base.CreateParams; c.ExStyle |= 0x80 | 0x8; return c; }
        }
    }

    // ---------- 형식 목록 ----------
    class FormatEnum : IEnumFORMATETC
    {
        readonly FORMATETC[] list;
        int pos;
        public FormatEnum(FORMATETC[] list, int pos) { this.list = list; this.pos = pos; }

        public int Next(int celt, FORMATETC[] rgelt, int[] fetched)
        {
            int n = 0;
            while (n < celt && pos < list.Length) rgelt[n++] = list[pos++];
            if (fetched != null && fetched.Length > 0) fetched[0] = n;
            return n == celt ? 0 : 1;
        }
        public int Skip(int celt) { pos = Math.Min(list.Length, pos + celt); return pos < list.Length ? 0 : 1; }
        public int Reset() { pos = 0; return 0; }
        public void Clone(out IEnumFORMATETC e) { e = new FormatEnum(list, pos); }
    }

    // ---------- 받아 오며 읽는 스트림 ----------
    class NetStream : IStream
    {
        readonly VirtualFiles owner;
        readonly int index;
        readonly FileEntry entry;
        Stream net;
        long pos;

        public NetStream(VirtualFiles owner, int index, FileEntry entry) { this.owner = owner; this.index = index; this.entry = entry; }

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int got = 0;
            try
            {
                if (net == null && entry.Size > 0)
                {
                    net = owner.open(index);
                    if (owner.Say != null) owner.Say("받는 중: " + entry.Rel + " (" + FileXfer.Mb(entry.Size) + ")");
                }
                long want = Math.Min(cb, entry.Size - pos);
                while (got < want)
                {
                    int n = net.Read(pv, got, (int)(want - got));
                    if (n <= 0) throw new IOException("받는 중에 연결이 끊김");
                    got += n;
                }
                pos += got;
                if (pos >= entry.Size && net != null) { net.Dispose(); net = null; }
            }
            catch (Exception e)
            {
                Log.Write("파일 받기 실패(" + entry.Rel + "): " + e.Message);
                if (net != null) { net.Dispose(); net = null; }
                throw new COMException(e.Message, unchecked((int)0x80004005));
            }
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, got);
        }

        public void Stat(out STATSTG s, int flag)
        {
            s = new STATSTG { type = 2, cbSize = entry.Size, pwcsName = flag == 1 ? null : Path.GetFileName(entry.Rel) };   // STGTY_STREAM
        }

        // 지금 위치를 묻는 것(0만큼 이동)만 받는다
        public void Seek(long move, int origin, IntPtr newPos)
        {
            long p = origin == 0 ? move : origin == 1 ? pos + move : entry.Size + move;
            if (p != pos) throw new COMException("되감기는 안 됨", unchecked((int)0x80030019));   // STG_E_INVALIDFUNCTION
            if (newPos != IntPtr.Zero) Marshal.WriteInt64(newPos, pos);
        }

        public void Write(byte[] pv, int cb, IntPtr written) { throw new COMException("", unchecked((int)0x80030005)); }
        public void SetSize(long size) { throw new COMException("", E_NOTIMPL); }
        public void CopyTo(IStream s, long cb, IntPtr read, IntPtr written) { throw new COMException("", E_NOTIMPL); }
        public void Commit(int flags) { }
        public void Revert() { }
        public void LockRegion(long o, long cb, int t) { throw new COMException("", E_NOTIMPL); }
        public void UnlockRegion(long o, long cb, int t) { throw new COMException("", E_NOTIMPL); }
        public void Clone(out IStream s) { throw new COMException("", E_NOTIMPL); }
    }
}
