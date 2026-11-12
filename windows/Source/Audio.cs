using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace RoboDust;

/// Everything you hear is synthesised here, sample by sample, and played through waveOut.
public sealed unsafe class Synth : ISimAudio
{
    struct Voice { public int Kind; public float T, Dur, F0, F1, Amp, Delay, Lp; }

    readonly object gate = new();
    readonly List<Voice> pending = new();
    volatile float motorTarget, loadTarget, roarTarget;
    public volatile bool Muted;
    const float Master = 0.55f;

    // Audio-thread state.
    readonly List<Voice> voices = new();
    float motor, load, roar, phase, phase2, phase3, lpA, lpB, lpC, flutter;
    uint noise = 0x12345;
    const int SampleRate = 48000, Frames = 1024, Buffers = 4;
    readonly Random rnd = new();

    Thread thread;
    volatile bool running;

    public void StartLive()
    {
        if (running) return;
        running = true;
        thread = new Thread(Pump) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "RoboDust audio" };
        thread.Start();
    }
    public void StopLive() { running = false; thread?.Join(500); thread = null; }

    void Enqueue(params Voice[] v) { lock (gate) pending.AddRange(v); }

    public void SetMotor(float level, float load) { motorTarget = level; loadTarget = load; }
    public void SetRoar(float level) { roarTarget = level; }

    /// One robot per display, one speaker: each robot gets a channel, and the motor and blower you
    /// hear are the loudest of them. Every chirp, gulp and thud plays as it is.
    public sealed class Channel : ISimAudio
    {
        readonly Synth s;
        public float Motor, Load, Roar;
        public Channel(Synth s) { this.s = s; lock (s.channels) s.channels.Add(this); }
        public void SetMotor(float level, float load) { Motor = level; Load = load; s.Mix(); }
        public void SetRoar(float level) { Roar = level; s.Mix(); }
        public void Ingest(float mass) => s.Ingest(mass);
        public void Tick() => s.Tick();
        public void Chirp(params (float f, float d)[] notes) => s.Chirp(notes);
        public void Thud(float amount) => s.Thud(amount);
        public void Flame(float seconds) => s.Flame(seconds);
        public void Pop() => s.Pop();
        public void Popper() => s.Popper();
    }

    readonly List<Channel> channels = new();
    public Channel NewChannel() => new(this);
    public void ClearChannels() { lock (channels) channels.Clear(); }
    void Mix()
    {
        lock (channels)
        {
            float m = 0, l = 0, r = 0;
            foreach (var c in channels) { m = MathF.Max(m, c.Motor); l = MathF.Max(l, c.Load); r = MathF.Max(r, c.Roar); }
            motorTarget = m; loadTarget = l; roarTarget = r;
        }
    }
    float R(float a, float b) { lock (rnd) return a + (b - a) * (float)rnd.NextDouble(); }
    public void Ingest(float mass)
    {
        var k = M.Clamp((mass - 1) / 5, 0, 1);
        var v = new List<Voice> { new() { Kind = 1, Dur = 0.1f + 0.55f * k, F0 = 3200 - 2200 * k, F1 = 500 - 200 * k, Amp = 0.30f + 0.28f * k } };
        // Debris rattling up the tube.
        var rattles = 2 + (int)(k * 7);
        for (int i = 0; i < rattles; i++) v.Add(new Voice { Kind = 2, Dur = 0.03f, F0 = R(900, 3400), Amp = 0.10f, Delay = 0.05f + i * R(0.03f, 0.07f) });
        Enqueue(v.ToArray());
    }
    public void Tick() => Enqueue(new Voice { Kind = 2, Dur = 0.025f, F0 = R(1500, 3800), Amp = 0.09f });
    public void Chirp(params (float f, float d)[] notes)
    {
        float at = 0; var v = new List<Voice>();
        foreach (var (f, d) in notes) { v.Add(new Voice { Kind = 0, Dur = d + 0.12f, F0 = f, F1 = f, Amp = 0.15f, Delay = at }); at += d; }
        Enqueue(v.ToArray());
    }
    public void Thud(float a) => Enqueue(new Voice { Kind = 3, Dur = 0.26f, F0 = 118, F1 = 46, Amp = 0.85f * a }, new Voice { Kind = 1, Dur = 0.09f, F0 = 1400, F1 = 300, Amp = 0.26f * a });
    public void Flame(float seconds) => Enqueue(new Voice { Kind = 4, Dur = seconds + 0.15f, Amp = 0.32f });
    public void Pop() => Enqueue(new Voice { Kind = 5, Dur = 0.06f, F0 = R(1400, 2400), Amp = 0.07f });
    /// A party popper: a crack, a hiss of paper, and a little fanfare.
    public void Popper()
    {
        Enqueue(new Voice { Kind = 3, Dur = 0.12f, F0 = 300, F1 = 90, Amp = 0.6f }, new Voice { Kind = 1, Dur = 0.35f, F0 = 7000, F1 = 1500, Amp = 0.32f });
        Chirp((523, 0.1f), (659, 0.1f), (784, 0.1f), (1047, 0.35f));
    }

    float White() { noise ^= noise << 13; noise ^= noise >> 17; noise ^= noise << 5; return (int)noise / 2147483648f; }

    void Render(short* outp, int frames)
    {
        if (Monitor.TryEnter(gate))
        {
            if (pending.Count > 0) { voices.AddRange(pending); pending.Clear(); }
            if (voices.Count > 40) voices.RemoveRange(0, voices.Count - 40);
            Monitor.Exit(gate);
        }
        const float dt = 1f / SampleRate, twoPi = 2 * MathF.PI;
        float mT = motorTarget, lT = loadTarget, rT = roarTarget;
        var g = Muted ? 0 : Master;
        for (int i = 0; i < frames; i++)
        {
            motor += (mT - motor) * 0.00006f * 6;
            load += (lT - load) * 0.0004f;
            roar += (rT - roar) * 0.00012f;

            // Suction motor: a whine that sags when a heavy file jams the tube.
            var m = motor;
            flutter += dt * 7.1f;
            var drift = 1 + 0.006f * MathF.Sin(flutter * twoPi);
            var f = (96 + 34 * m - 26 * load) * drift;
            phase += f * dt; if (phase > 1) phase -= 1;
            phase2 += f * 2.02f * dt; if (phase2 > 1) phase2 -= 1;
            phase3 += f * 5.97f * dt; if (phase3 > 1) phase3 -= 1;
            var hum = MathF.Sin(phase * twoPi) * 0.40f + MathF.Sin(phase2 * twoPi) * 0.26f + MathF.Sin(phase3 * twoPi) * 0.06f;

            // Moving air: filtered noise, brighter when flowing freely, duller under load.
            var n = White();
            var cutA = 0.035f + 0.07f * m - 0.05f * load;
            lpA += (n - lpA) * MathF.Max(cutA, 0.005f);
            lpB += (lpA - lpB) * 0.5f;
            var air = lpB * 1.6f;

            // Self-empty blower: a much bigger, lower, rising rush.
            lpC += (n - lpC) * (0.03f + 0.10f * roar);
            var roarS = lpC * 2.6f * roar + MathF.Sin(phase * twoPi * 0.5f) * 0.25f * roar;

            var s = (hum * 0.14f + air * 0.42f) * m + roarS * 0.62f;

            int j = 0;
            while (j < voices.Count)
            {
                var v = voices[j];
                if (v.Delay > 0) { v.Delay -= dt; voices[j] = v; j++; continue; }
                var p = v.T / v.Dur;
                var pc = MathF.Min(p, 1);
                switch (v.Kind)
                {
                    case 0: // chime
                    {
                        var env = MathF.Min(1, v.T * 180) * MathF.Pow(1 - pc, 2.2f);
                        s += MathF.Sin(v.T * v.F0 * twoPi) * env * v.Amp;
                        s += MathF.Sin(v.T * v.F0 * 2 * twoPi) * env * v.Amp * 0.14f;
                        break;
                    }
                    case 1: // gulp: a noise burst closing down
                    {
                        var cf = v.F0 + (v.F1 - v.F0) * p;
                        var c = MathF.Min(0.9f, cf * twoPi / SampleRate);
                        v.Lp += (n - v.Lp) * c;
                        var env = MathF.Min(1, v.T * 90) * MathF.Pow(1 - pc, 1.5f);
                        s += v.Lp * env * v.Amp * 1.6f;
                        break;
                    }
                    case 2: // tick: debris against plastic
                        s += MathF.Sin(v.T * v.F0 * twoPi) * MathF.Pow(1 - pc, 3) * v.Amp;
                        break;
                    case 3: // thud
                    {
                        var ff = v.F1 + (v.F0 - v.F1) * MathF.Pow(1 - pc, 3);
                        v.Lp += ff * dt;
                        s += MathF.Sin(v.Lp * twoPi) * MathF.Pow(1 - pc, 2) * v.Amp;
                        break;
                    }
                    case 4: // flame: a roar of low noise with crackles on top
                    {
                        v.Lp += (n - v.Lp) * 0.06f;
                        var env = MathF.Min(1, v.T * 25) * MathF.Min(1, (1 - pc) * 6);
                        var crackle = White() > 0.985f ? White() * 1.4f : 0;
                        s += (v.Lp * 2.6f + crackle) * env * v.Amp;
                        break;
                    }
                    case 5: // pop: a tiny bright blip falling in pitch
                    {
                        var fr = v.F0 * (1 - 0.5f * pc);
                        v.Lp += fr * dt;
                        s += MathF.Sin(v.Lp * twoPi) * MathF.Pow(1 - pc, 3) * v.Amp;
                        break;
                    }
                }
                v.T += dt;
                if (v.T >= v.Dur) voices.RemoveAt(j); else { voices[j] = v; j++; }
            }
            var o = MathF.Tanh(s * 1.1f) * g;
            var sample = (short)(M.Clamp(o, -1, 1) * 32000);
            outp[i * 2] = sample; outp[i * 2 + 1] = sample;
        }
    }

    // ---------- waveOut ----------
    [StructLayout(LayoutKind.Sequential)] struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
    [StructLayout(LayoutKind.Sequential)] struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }
    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, WAVEHDR* hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, WAVEHDR* hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, WAVEHDR* hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr CreateEvent(IntPtr sa, bool manual, bool initial, string name);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    const uint CALLBACK_EVENT = 0x50000, WHDR_DONE = 1;

    void Pump()
    {
        var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = SampleRate, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = SampleRate * 4 };
        var ev = CreateEvent(IntPtr.Zero, false, false, null);
        if (waveOutOpen(out var h, -1, ref fmt, ev, IntPtr.Zero, CALLBACK_EVENT) != 0) { CloseHandle(ev); running = false; return; }
        var size = Frames * 4;
        var hdrs = (WAVEHDR*)Marshal.AllocHGlobal(sizeof(WAVEHDR) * Buffers);
        var data = new IntPtr[Buffers];
        try
        {
            for (int i = 0; i < Buffers; i++)
            {
                data[i] = Marshal.AllocHGlobal(size);
                hdrs[i] = new WAVEHDR { lpData = data[i], dwBufferLength = (uint)size };
                waveOutPrepareHeader(h, &hdrs[i], sizeof(WAVEHDR));
                Render((short*)data[i], Frames);
                waveOutWrite(h, &hdrs[i], sizeof(WAVEHDR));
            }
            while (running)
            {
                WaitForSingleObject(ev, 50);
                for (int i = 0; i < Buffers; i++)
                {
                    if ((hdrs[i].dwFlags & WHDR_DONE) == 0) continue;
                    Render((short*)data[i], Frames);
                    waveOutWrite(h, &hdrs[i], sizeof(WAVEHDR));
                }
            }
            waveOutReset(h);
            for (int i = 0; i < Buffers; i++) waveOutUnprepareHeader(h, &hdrs[i], sizeof(WAVEHDR));
        }
        finally
        {
            waveOutClose(h);
            for (int i = 0; i < Buffers; i++) if (data[i] != IntPtr.Zero) Marshal.FreeHGlobal(data[i]);
            Marshal.FreeHGlobal((IntPtr)hdrs);
            CloseHandle(ev);
        }
    }
}
