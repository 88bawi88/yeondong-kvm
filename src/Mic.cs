// 마이크 넘기기: 커서가 서브 PC에 있는 동안 메인 PC 기본 마이크 소리를 서브 PC로 보내고,
// 서브 PC는 그 소리를 VB-CABLE("CABLE Input")로 흘려 넣는다. 서브 PC 프로그램은 "CABLE Output"을 마이크로 고르면 된다.
// 24kHz 16비트 모노, 20ms 조각. Windows 기본 winmm(waveIn/waveOut)만 쓴다.
// 서브 PC에 VB-CABLE이 있을 때만 서브가 MicReady를 보내고, 메인은 그걸 받은 연결에만 소리를 보낸다.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

static class Wave
{
    public const int Rate = 24000, Chunk = 960;   // 20ms = 480샘플 × 2바이트
    public const int WHDR_DONE = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct FORMAT { public short tag, ch; public int rate, bytesPerSec; public short align, bits, extra; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HDR { public IntPtr data; public int len, recorded; public IntPtr user; public int flags, loops; public IntPtr next, reserved; }

    public static readonly int HdrSize = Marshal.SizeOf(typeof(HDR));
    public static readonly int FlagsOff = (int)Marshal.OffsetOf(typeof(HDR), "flags");
    public static readonly int RecOff = (int)Marshal.OffsetOf(typeof(HDR), "recorded");

    public static FORMAT Pcm() { return new FORMAT { tag = 1, ch = 1, rate = Rate, bytesPerSec = Rate * 2, align = 2, bits = 16 }; }

    // 데이터 버퍼까지 붙은 헤더 하나를 비관리 메모리에 만든다(GC가 옮기지 못하게).
    public static IntPtr NewHdr(int size)
    {
        var h = Marshal.AllocHGlobal(HdrSize);
        var hd = new HDR { data = Marshal.AllocHGlobal(size), len = size };
        Marshal.StructureToPtr(hd, h, false);
        return h;
    }
    public static void FreeHdr(IntPtr h)
    {
        Marshal.FreeHGlobal(((HDR)Marshal.PtrToStructure(h, typeof(HDR))).data);
        Marshal.FreeHGlobal(h);
    }
    public static IntPtr Data(IntPtr h) { return Marshal.ReadIntPtr(h); }
    public static bool Done(IntPtr h) { return (Marshal.ReadInt32(h, FlagsOff) & WHDR_DONE) != 0; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct OUTCAPS { public short mid, pid; public int ver; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name; public int formats; public short ch, res; public int support; }

    [DllImport("winmm.dll")] public static extern int waveInOpen(out IntPtr h, int dev, ref FORMAT f, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] public static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] public static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] public static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] public static extern int waveInStart(IntPtr h);
    [DllImport("winmm.dll")] public static extern int waveInReset(IntPtr h);
    [DllImport("winmm.dll")] public static extern int waveInClose(IntPtr h);
    [DllImport("winmm.dll")] public static extern int waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern int waveOutGetDevCapsW(IntPtr id, out OUTCAPS c, int size);
    [DllImport("winmm.dll")] public static extern int waveOutOpen(out IntPtr h, int dev, ref FORMAT f, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] public static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] public static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] public static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] public static extern int waveOutPause(IntPtr h);
    [DllImport("winmm.dll")] public static extern int waveOutRestart(IntPtr h);
    [DllImport("winmm.dll")] public static extern int waveOutReset(IntPtr h);
    [DllImport("winmm.dll")] public static extern int waveOutClose(IntPtr h);
}

// 메인 PC: 기본 녹음 장치에서 받아 보낸다. Start/Stop은 화면 스레드에서 부른다.
static class MicSend
{
    static int gen;   // Start·Stop마다 늘린다. 녹음 스레드는 자기 번호가 아니게 되면 끝낸다

    public static void Start(Link l)
    {
        int my = Interlocked.Increment(ref gen);
        new Thread(() => Loop(l, my)) { IsBackground = true, Name = "mic-send" }.Start();
    }

    public static void Stop() { Interlocked.Increment(ref gen); }

    static void Loop(Link l, int my)
    {
        IntPtr h = IntPtr.Zero;
        var bufs = new IntPtr[6];
        var ev = new AutoResetEvent(false);
        try
        {
            var f = Wave.Pcm();
            int r = Wave.waveInOpen(out h, -1, ref f, ev.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x50000);   // WAVE_MAPPER, CALLBACK_EVENT
            if (r != 0) { Log.Write("마이크 열기 실패: " + r); return; }
            for (int i = 0; i < bufs.Length; i++)
            {
                bufs[i] = Wave.NewHdr(Wave.Chunk);
                Wave.waveInPrepareHeader(h, bufs[i], Wave.HdrSize);
                Wave.waveInAddBuffer(h, bufs[i], Wave.HdrSize);
            }
            Wave.waveInStart(h);
            Log.Write("마이크 보내기 시작(" + l.Name + ")");
            int next = 0;
            while (gen == my && !l.Closed)
            {
                ev.WaitOne(100);
                // 버퍼는 넣은 순서대로 찬다
                while (gen == my && Wave.Done(bufs[next]))
                {
                    int n = Marshal.ReadInt32(bufs[next], Wave.RecOff);
                    if (n > 0)
                    {
                        var b = new byte[n];
                        Marshal.Copy(Wave.Data(bufs[next]), b, 0, n);
                        l.Send(Msg.MicData, b);
                    }
                    Wave.waveInAddBuffer(h, bufs[next], Wave.HdrSize);
                    next = (next + 1) % bufs.Length;
                }
            }
            if (!l.Closed) l.Send(Msg.MicStop, null);
            Log.Write("마이크 보내기 끝");
        }
        catch (Exception e) { Log.Write("마이크 보내기 오류: " + e.Message); }
        finally
        {
            if (h != IntPtr.Zero)
            {
                Wave.waveInReset(h);
                foreach (var b in bufs) if (b != IntPtr.Zero) Wave.waveInUnprepareHeader(h, b, Wave.HdrSize);
                Wave.waveInClose(h);
            }
            foreach (var b in bufs) if (b != IntPtr.Zero) Wave.FreeHdr(b);
            ev.Close();
        }
    }
}

// 서브 PC: 받은 소리를 CABLE Input으로 재생한다. 연결 읽기 스레드와 끊김 처리에서 부르므로 잠근다.
static class MicPlay
{
    const int Prebuffer = 3, MaxQueue = 15;   // 60ms 모이면 재생 시작, 300ms 넘게 밀리면 버린다
    static readonly object gate = new object();
    static readonly List<IntPtr> queued = new List<IntPtr>();
    static IntPtr h;
    static bool playing;

    // VB-CABLE 재생 장치 번호. 없으면 -1.
    public static int Device()
    {
        int n = Wave.waveOutGetNumDevs();
        for (int i = 0; i < n; i++)
        {
            Wave.OUTCAPS c;
            if (Wave.waveOutGetDevCapsW(new IntPtr(i), out c, Marshal.SizeOf(typeof(Wave.OUTCAPS))) == 0 && c.name != null && c.name.StartsWith("CABLE Input"))
                return i;
        }
        return -1;
    }

    public static void Data(byte[] b)
    {
        lock (gate)
        {
            if (h == IntPtr.Zero)
            {
                int dev = Device();
                if (dev < 0) return;
                var f = Wave.Pcm();
                int r = Wave.waveOutOpen(out h, dev, ref f, IntPtr.Zero, IntPtr.Zero, 0);
                if (r != 0) { h = IntPtr.Zero; Log.Write("CABLE Input 열기 실패: " + r); return; }
                Wave.waveOutPause(h);
                playing = false;
                Log.Write("마이크 받기 시작");
            }
            Reclaim();
            if (playing && queued.Count == 0) { Wave.waveOutPause(h); playing = false; }   // 끊겼으면 다시 조금 모은다
            if (queued.Count >= MaxQueue) return;
            var hd = Wave.NewHdr(b.Length);
            Marshal.Copy(b, 0, Wave.Data(hd), b.Length);
            Wave.waveOutPrepareHeader(h, hd, Wave.HdrSize);
            Wave.waveOutWrite(h, hd, Wave.HdrSize);
            queued.Add(hd);
            if (!playing && queued.Count >= Prebuffer) { Wave.waveOutRestart(h); playing = true; }
        }
    }

    static void Reclaim()
    {
        for (int i = 0; i < queued.Count; )
        {
            if (!Wave.Done(queued[i])) { i++; continue; }
            Wave.waveOutUnprepareHeader(h, queued[i], Wave.HdrSize);
            Wave.FreeHdr(queued[i]);
            queued.RemoveAt(i);
        }
    }

    public static void Stop()
    {
        lock (gate)
        {
            if (h == IntPtr.Zero) return;
            Wave.waveOutReset(h);
            foreach (var q in queued) { Wave.waveOutUnprepareHeader(h, q, Wave.HdrSize); Wave.FreeHdr(q); }
            queued.Clear();
            Wave.waveOutClose(h);
            h = IntPtr.Zero;
            Log.Write("마이크 받기 끝");
        }
    }
}
