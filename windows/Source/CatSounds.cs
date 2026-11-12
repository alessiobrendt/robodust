using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RoboDust;

/// The cat's voice. Purring, hissing, growling and paw hits are synthesised, the same way as on the
/// Mac; the meows are a real cat. Called from the UI thread, mixed on the audio thread.
public sealed class CatSounds : ICatAudio
{
    struct Voice { public int Kind; public float T, Dur, F0, F1, Amp, Delay, Lp, Lp2, A; }

    const float SampleRate = 48000;
    readonly object gate = new();
    readonly List<Voice> pending = new();
    volatile bool hasPending;
    volatile float purrTarget;
    readonly Random rnd = new();

    // Audio-thread state.
    readonly List<Voice> voices = new();
    float purr, purrPhase, purrBreath, purrLP;

    /// Four meows from one cat: Elix, recorded by Joseph Sardin for BigSoundBank (CC0).
    readonly float[][] meows = new float[4][];
    /// How far one output sample moves through each recording at normal pitch.
    readonly float[] meowStep = new float[4];

    public CatSounds()
    {
        for (int n = 0; n < 4; n++)
        {
            var (samples, rate) = LoadWav($"RoboDust.Sounds.meow-{n + 1}.wav");
            meows[n] = samples;
            meowStep[n] = rate > 0 ? rate / SampleRate : 1;
        }
    }

    void Enqueue(params Voice[] v) { lock (gate) { pending.AddRange(v); hasPending = true; } }
    float R(float a, float b) { lock (rnd) return a + (b - a) * (float)rnd.NextDouble(); }

    public void SetPurr(float level) => purrTarget = level;
    // The cat's other sounds are still made here: a hiss is shaped noise, a growl an uneven buzz.
    public void Hiss(float seconds) => Enqueue(new Voice { Kind = 4, Dur = seconds, Amp = 0.62f });
    public void Growl(float seconds) => Enqueue(new Voice { Kind = 5, Dur = seconds, F0 = 82, Amp = 0.42f });
    public void Meow(int which, float rate, float amp)
    {
        if (which < 0 || which >= meows.Length) return;
        var m = meows[which];
        if (m.Length == 0 || rate <= 0) return;
        var step = rate * meowStep[which];
        Enqueue(new Voice { Kind = 6, Dur = m.Length / SampleRate / step, F0 = step, F1 = which, Amp = amp });
    }
    public void Swat()
    {
        // A soft paw on hard plastic: a low pat with a small click on top.
        Enqueue(new Voice { Kind = 3, Dur = 0.1f, F0 = 210, F1 = 90, Amp = 0.5f },
                new Voice { Kind = 1, Dur = 0.045f, F0 = 3000, F1 = 900, Amp = 0.2f },
                new Voice { Kind = 2, Dur = 0.018f, F0 = R(1500, 2300), Amp = 0.1f });
    }

    /// One sample of the cat, at 48 kHz, to add to the mix before the limiter. white: the synth's
    /// own noise sample for this instant, in -1...1.
    public float Sample(float white)
    {
        if (hasPending && Monitor.TryEnter(gate))
        {
            voices.AddRange(pending); pending.Clear(); hasPending = false;
            if (voices.Count > 24) voices.RemoveRange(0, voices.Count - 24);
            Monitor.Exit(gate);
        }
        const float dt = 1f / SampleRate, twoPi = 2 * MathF.PI;
        var n = white;
        float s = 0;

        // Purring: a soft rattle about 25 times a second, a little faster breathing out.
        purr += (purrTarget - purr) * 0.0002f;
        if (purr > 0.002f)
        {
            purrBreath += dt / 2.5f; if (purrBreath > 1) purrBreath -= 1;
            var breathingOut = purrBreath < 0.58f;
            purrPhase += (breathingOut ? 27 : 23) * dt; if (purrPhase > 1) purrPhase -= 1;
            var pulse = MathF.Pow(MathF.Max(0, MathF.Sin(purrPhase * twoPi)), 3);
            purrLP += (n * pulse - purrLP) * 0.075f;
            var breath = MathF.Sin(purrBreath * twoPi * (breathingOut ? 0.86f : 1.2f)) * 0.5f + 0.5f;
            s += (purrLP * 2.4f + MathF.Sin(purrPhase * twoPi * 3) * pulse * 0.1f) * purr * (0.5f + 0.5f * breath) * 0.6f;
        }

        int j = 0;
        while (j < voices.Count)
        {
            var v = voices[j];
            if (v.Delay > 0) { v.Delay -= dt; voices[j] = v; j++; continue; }
            var p = v.T / v.Dur;
            var pc = MathF.Min(p, 1);
            switch (v.Kind)
            {
                case 1: // gulp: a noise burst closing down
                {
                    var cf = v.F0 + (v.F1 - v.F0) * p;
                    var c = MathF.Min(0.9f, cf * twoPi / SampleRate);
                    v.Lp += (n - v.Lp) * c;
                    var env = MathF.Min(1, v.T * 90) * MathF.Pow(1 - pc, 1.5f);
                    s += v.Lp * env * v.Amp * 1.6f;
                    break;
                }
                case 2: // tick: a claw against plastic
                    s += MathF.Sin(v.T * v.F0 * twoPi) * MathF.Pow(1 - pc, 3) * v.Amp;
                    break;
                case 3: // pat: a low thump falling in pitch
                {
                    var ff = v.F1 + (v.F0 - v.F1) * MathF.Pow(1 - pc, 3);
                    v.Lp += ff * dt;
                    s += MathF.Sin(v.Lp * twoPi) * MathF.Pow(1 - pc, 2) * v.Amp;
                    break;
                }
                case 4: // hiss: air through teeth, with a spit at the start
                {
                    v.Lp += (n - v.Lp) * 0.62f;
                    v.Lp2 += (v.Lp - v.Lp2) * 0.2f;
                    var t = v.T;
                    var spit = t < 0.04f ? 1.7f : 1;
                    var env = MathF.Min(1, t * 50) * MathF.Pow(1 - pc, 0.55f) * (1 + 0.22f * MathF.Sin(t * 37 * twoPi));
                    s += (v.Lp - v.Lp2) * env * spit * v.Amp * 1.8f;
                    break;
                }
                case 5: // growl: a low, uneven buzz
                {
                    v.A += v.F0 * (1 + 0.5f * p) * (1 + n * 0.28f) * dt;
                    if (v.A > 1) v.A -= 1;
                    var buzz = MathF.Pow(1 - v.A, 4) * 2 - 0.4f;
                    v.Lp += (buzz - v.Lp) * 0.09f;
                    var flut = 0.72f + 0.28f * MathF.Sin(v.T * 13 * twoPi);
                    var env = MathF.Min(1, v.T * 5) * MathF.Min(1, (1 - p) * 8);
                    s += v.Lp * flut * env * v.Amp * 2;
                    break;
                }
                case 6: // meow: a recording, played a little faster or slower to change its pitch
                {
                    var m = meows[(int)v.F1];
                    var at = v.A; var k = (int)at;
                    if (k + 1 < m.Length) s += (m[k] + (m[k + 1] - m[k]) * (at - k)) * v.Amp * 2;
                    v.A += v.F0;
                    break;
                }
            }
            v.T += dt;
            if (v.T >= v.Dur) voices.RemoveAt(j); else { voices[j] = v; j++; }
        }
        return s;
    }

    /// A PCM or float WAV from the app's embedded resources, as mono samples, plus its sample rate.
    static (float[] samples, float rate) LoadWav(string name)
    {
        try
        {
            using var st = typeof(CatSounds).Assembly.GetManifestResourceStream(name);
            if (st == null) return (Array.Empty<float>(), 0);
            using var ms = new MemoryStream();
            st.CopyTo(ms);
            return DecodeWav(ms.ToArray());
        }
        catch { return (Array.Empty<float>(), 0); }
    }

    static (float[] samples, float rate) DecodeWav(byte[] b)
    {
        if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
            return (Array.Empty<float>(), 0);
        int format = 0, channels = 0, rate = 0, bits = 0, dataAt = -1, dataLen = 0;
        int i = 12;
        while (i + 8 <= b.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(b, i, 4);
            var len = BitConverter.ToInt32(b, i + 4);
            var body = i + 8;
            if (len < 0) break;
            if (id == "fmt " && len >= 16)
            {
                format = BitConverter.ToUInt16(b, body);
                channels = BitConverter.ToUInt16(b, body + 2);
                rate = BitConverter.ToInt32(b, body + 4);
                bits = BitConverter.ToUInt16(b, body + 14);
                // WAVE_FORMAT_EXTENSIBLE: the real format is the first two bytes of the sub-format GUID.
                if (format == 0xFFFE && len >= 26) format = BitConverter.ToUInt16(b, body + 24);
            }
            else if (id == "data")
            {
                dataAt = body;
                dataLen = Math.Min(len, b.Length - body);
            }
            i = body + len + (len & 1);
        }
        if (dataAt < 0 || channels <= 0 || bits <= 0) return (Array.Empty<float>(), 0);
        var bytesPer = bits / 8;
        var frame = bytesPer * channels;
        if (frame <= 0) return (Array.Empty<float>(), 0);
        var frames = dataLen / frame;
        var o = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                var at = dataAt + f * frame + c * bytesPer;
                sum += format switch
                {
                    3 when bits == 32 => BitConverter.ToSingle(b, at),
                    3 when bits == 64 => (float)BitConverter.ToDouble(b, at),
                    _ => bits switch
                    {
                        8 => (b[at] - 128) / 128f,
                        16 => BitConverter.ToInt16(b, at) / 32768f,
                        24 => ((b[at] | (b[at + 1] << 8) | (b[at + 2] << 16)) << 8 >> 8) / 8388608f,
                        32 => BitConverter.ToInt32(b, at) / 2147483648f,
                        _ => 0,
                    },
                };
            }
            o[f] = sum / channels;
        }
        return (o, rate);
    }
}
