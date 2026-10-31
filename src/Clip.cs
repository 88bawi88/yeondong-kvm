// 클립보드 동기화. 로컬 클립보드가 바뀌면 알리고(OnLocalChange), 상대에게서 받은 내용을 클립보드에 쓴다(Apply).
// 네트워크는 여기서 다루지 않는다. UI(STA) 스레드에서 만들고 불러야 한다.
// WinForms Clipboard(OLE)가 일부 PC에서 실패해서 Win32 API로 직접 읽고 쓴다.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class Clip : NativeWindow
{
    const int WM_CLIPBOARDUPDATE = 0x031D;
    const int MaxBytes = 32 * 1024 * 1024;
    const uint CF_UNICODETEXT = 13;
    const uint CF_DIB = 8;
    const uint CF_HDROP = 15;
    const uint GMEM_MOVEABLE = 2;
    const uint GMEM_ZEROINIT = 0x40;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetClipboardData(uint format, IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern UIntPtr GlobalSize(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GlobalFree(IntPtr h);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder buf, uint cch);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint RegisterClipboardFormat(string name);

    public Action<byte, byte[]> OnLocalChange;   // UI 스레드에서 불린다
    string lastHash = "";
    DateTime ignoreUntil = DateTime.MinValue;    // 우리가 쓴 직후의 변경 알림은 무시한다(되돌림 방지)

    public Clip()
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });   // HWND_MESSAGE
        if (!AddClipboardFormatListener(Handle))
            Log.Write("클립보드 감시 등록 실패, Win32 오류 " + Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }

    protected override void WndProc(ref Message m)
    {
        // 바뀐 직후에는 바로 열지 않고 잠깐 기다린다. 바꾼 쪽(탐색기 붙여넣기 등)이 클립보드를 다시 열 때 부딪히면
        // 그쪽 작업이 소리 없이 실패한다(붙여넣기를 눌러도 아무 일도 안 생겼다).
        if (m.Msg == WM_CLIPBOARDUPDATE && DateTime.UtcNow >= ignoreUntil)
        {
            if (settle == null)
            {
                settle = new System.Windows.Forms.Timer { Interval = 400 };
                settle.Tick += delegate { settle.Stop(); Settled(); };
            }
            settle.Stop();
            settle.Start();
        }
        base.WndProc(ref m);
    }

    System.Windows.Forms.Timer settle;
    static readonly uint PasteSucceeded = RegisterClipboardFormat("Paste Succeeded");
    static readonly uint PerformedDropEffect = RegisterClipboardFormat("Performed DropEffect");

    void Settled()
    {
        if (DateTime.UtcNow < ignoreUntil) return;
        // 붙여넣은 쪽이 결과를 적어 넣은 변경이면 내용은 그대로라 읽지 않는다
        if (IsClipboardFormatAvailable(PasteSucceeded) || IsClipboardFormatAvailable(PerformedDropEffect)) return;
        uint pid;
        GetWindowThreadProcessId(GetClipboardOwner(), out pid);
        if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return;   // 우리가 올린 것
        try { ReadLocal(); }
        catch (Exception e) { Log.Write("클립보드 읽기 오류: " + e.Message); }
    }

    [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    // 다른 앱이 클립보드를 잡고 있으면 실패한다. 30ms 간격으로 10번까지 다시 해 본다.
    bool OpenRetry()
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(Handle)) return true;
            Thread.Sleep(30);
        }
        Log.Write("클립보드 열기 실패, Win32 오류 " + Marshal.GetLastWin32Error());
        return false;
    }

    void ReadLocal()
    {
        byte type = 0;
        byte[] payload = null;
        byte[] dib = null;
        if (!OpenRetry()) return;
        try
        {
            // 파일 복사(CF_HDROP)는 보내지 않는다. 파일은 끌어 놓기로만 옮긴다
            if (IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                IntPtr h = GetClipboardData(CF_UNICODETEXT);
                if (h == IntPtr.Zero) { Log.Write("클립보드 글자 가져오기 실패, Win32 오류 " + Marshal.GetLastWin32Error()); return; }
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) { Log.Write("클립보드 글자 잠금 실패, Win32 오류 " + Marshal.GetLastWin32Error()); return; }
                string s;
                try { s = Marshal.PtrToStringUni(p); }
                finally { GlobalUnlock(h); }
                if (!string.IsNullOrEmpty(s))
                {
                    type = Msg.ClipText;
                    payload = Encoding.UTF8.GetBytes(s);
                }
            }
            else if (IsClipboardFormatAvailable(CF_DIB))
            {
                IntPtr h = GetClipboardData(CF_DIB);
                if (h == IntPtr.Zero) { Log.Write("클립보드 그림 가져오기 실패, Win32 오류 " + Marshal.GetLastWin32Error()); return; }
                long size = (long)GlobalSize(h).ToUInt64();
                if (size > MaxBytes) { Log.Write("클립보드 내용이 32MB를 넘어 보내지 않습니다"); return; }
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) { Log.Write("클립보드 그림 잠금 실패, Win32 오류 " + Marshal.GetLastWin32Error()); return; }
                try
                {
                    dib = new byte[size];
                    Marshal.Copy(p, dib, 0, (int)size);
                }
                finally { GlobalUnlock(h); }
            }
        }
        finally { CloseClipboard(); }

        if (dib != null)
        {
            payload = DibToPng(dib);
            if (payload != null) type = Msg.ClipImage;
        }
        if (payload == null || payload.Length == 0) return;
        if (payload.Length > MaxBytes) { Log.Write("클립보드 내용이 32MB를 넘어 보내지 않습니다"); return; }

        string hash = Hash(type, payload);
        if (hash == lastHash) return;
        lastHash = hash;
        var cb = OnLocalChange;
        if (cb == null) return;
        try { cb(type, payload); }
        catch (Exception e) { Log.Write("클립보드 전송 오류: " + e.Message); }
    }

    // CF_DIB 바이트에 BMP 파일 머리를 붙여 PNG로 바꾼다.
    static byte[] DibToPng(byte[] dib)
    {
        try
        {
            if (dib.Length < 40) return null;
            int biSize = BitConverter.ToInt32(dib, 0);
            int bitCount = BitConverter.ToInt16(dib, 14);
            int comp = BitConverter.ToInt32(dib, 16);
            int clrUsed = BitConverter.ToInt32(dib, 32);
            int pal = clrUsed;
            if (pal == 0 && bitCount <= 8) pal = 1 << bitCount;
            int off = 14 + biSize + pal * 4;
            if (comp == 3 && biSize == 40) off += 12;
            var bmp = new byte[14 + dib.Length];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            Array.Copy(BitConverter.GetBytes(bmp.Length), 0, bmp, 2, 4);
            Array.Copy(BitConverter.GetBytes(off), 0, bmp, 10, 4);
            Array.Copy(dib, 0, bmp, 14, dib.Length);
            using (var ms = new MemoryStream(bmp))
            using (var img = new Bitmap(ms))
            using (var outMs = new MemoryStream())
            {
                img.Save(outMs, ImageFormat.Png);
                return outMs.ToArray();
            }
        }
        catch (Exception e) { Log.Write("클립보드 그림 변환 오류: " + e.Message); return null; }
    }

    public void Apply(byte type, byte[] body)
    {
        try
        {
            if (body == null) return;
            string h = Hash(type, body);
            if (h == lastHash) return;
            lastHash = h;
            ignoreUntil = DateTime.UtcNow.AddMilliseconds(500);   // 쓰기 전에 먼저 건다
            if (type == Msg.ClipText)
            {
                string s = Encoding.UTF8.GetString(body);
                if (s.Length > 0) WriteData(CF_UNICODETEXT, TextBytes(s));
            }
            else if (type == Msg.ClipImage)
            {
                byte[] dib = PngToDib(body);
                if (dib != null) WriteData(CF_DIB, dib);
            }
            ignoreUntil = DateTime.UtcNow.AddMilliseconds(500);
        }
        catch (Exception e) { Log.Write("클립보드 쓰기 오류: " + e.Message); }
    }

    // 열린 클립보드에 data를 해당 형식으로 올린다(비우지 않는다).
    static void SetMem(uint format, byte[] data)
    {
        IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, new UIntPtr((uint)data.Length));
        if (hMem == IntPtr.Zero) { Log.Write("클립보드 메모리 할당 실패, Win32 오류 " + Marshal.GetLastWin32Error()); return; }
        IntPtr p = GlobalLock(hMem);
        if (p == IntPtr.Zero)
        {
            Log.Write("클립보드 메모리 잠금 실패, Win32 오류 " + Marshal.GetLastWin32Error());
            GlobalFree(hMem);
            return;
        }
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(hMem);
        if (SetClipboardData(format, hMem) == IntPtr.Zero)
        {
            Log.Write("클립보드 쓰기 실패, Win32 오류 " + Marshal.GetLastWin32Error());
            GlobalFree(hMem);
        }
    }

    // UTF-16 글자 + 끝 널 문자
    static byte[] TextBytes(string s)
    {
        var b = new byte[(s.Length + 1) * 2];
        Encoding.Unicode.GetBytes(s, 0, s.Length, b, 0);
        return b;
    }

    void WriteData(uint format, byte[] data)
    {
        if (!OpenRetry()) return;
        try
        {
            EmptyClipboard();
            IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, new UIntPtr((uint)data.Length));
            if (hMem == IntPtr.Zero) { Log.Write("클립보드 메모리 할당 실패, Win32 오류 " + Marshal.GetLastWin32Error()); return; }
            IntPtr p = GlobalLock(hMem);
            if (p == IntPtr.Zero)
            {
                Log.Write("클립보드 메모리 잠금 실패, Win32 오류 " + Marshal.GetLastWin32Error());
                GlobalFree(hMem);
                return;
            }
            Marshal.Copy(data, 0, p, data.Length);
            GlobalUnlock(hMem);
            if (SetClipboardData(format, hMem) == IntPtr.Zero)
            {
                Log.Write("클립보드 쓰기 실패, Win32 오류 " + Marshal.GetLastWin32Error());
                GlobalFree(hMem);   // 성공하면 소유권이 시스템으로 넘어간다
            }
        }
        finally { CloseClipboard(); }
    }

    // PNG를 32비트 CF_DIB(아래에서 위로 쌓인 BGRA)로 바꾼다.
    static byte[] PngToDib(byte[] png)
    {
        try
        {
            using (var ms = new MemoryStream(png))
            using (var img = Image.FromStream(ms))
            using (var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                    g.DrawImage(img, new Rectangle(0, 0, bmp.Width, bmp.Height));
                int w = bmp.Width, hh = bmp.Height, row = w * 4;
                var dib = new byte[40 + row * hh];
                Array.Copy(BitConverter.GetBytes(40), 0, dib, 0, 4);
                Array.Copy(BitConverter.GetBytes(w), 0, dib, 4, 4);
                Array.Copy(BitConverter.GetBytes(hh), 0, dib, 8, 4);
                Array.Copy(BitConverter.GetBytes((short)1), 0, dib, 12, 2);
                Array.Copy(BitConverter.GetBytes((short)32), 0, dib, 14, 2);
                Array.Copy(BitConverter.GetBytes(row * hh), 0, dib, 20, 4);
                var data = bmp.LockBits(new Rectangle(0, 0, w, hh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < hh; y++)
                    {
                        IntPtr src = new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride);
                        Marshal.Copy(src, dib, 40 + (hh - 1 - y) * row, row);
                    }
                }
                finally { bmp.UnlockBits(data); }
                return dib;
            }
        }
        catch (Exception e) { Log.Write("클립보드 그림 변환 오류: " + e.Message); return null; }
    }

    static string Hash(byte type, byte[] body)
    {
        using (var sha = SHA1.Create())
        {
            sha.TransformBlock(new byte[] { type }, 0, 1, null, 0);
            sha.TransformFinalBlock(body, 0, body.Length);
            return Convert.ToBase64String(sha.Hash);
        }
    }
}
