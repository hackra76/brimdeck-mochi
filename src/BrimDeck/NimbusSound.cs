using System.Collections.Concurrent;
using System.IO;
using System.Media;

namespace BrimDeck;

public enum NimbusSoundKind
{
    Greet,
    Poke,
    Slap,
    Dizzy,
    Approval,
    Finished,
    Error
}

/// <summary>
/// Lightweight procedural WAV synthesizer for Nimbus's subtle audio cues.
/// Generates and caches short 16-bit PCM chimes in memory without external asset files.
/// </summary>
public static class NimbusSound
{
    private static readonly ConcurrentDictionary<NimbusSoundKind, byte[]> Cache = new();
    public static bool Enabled { get; set; } = true;

    public static void Play(NimbusSoundKind kind)
    {
        if (!Enabled) return;
        try
        {
            byte[] wav = Cache.GetOrAdd(kind, BuildWav);
            // Play asynchronously on a background thread so UI frames never block.
            _ = Task.Run(() =>
            {
                try
                {
                    using var ms = new MemoryStream(wav, writable: false);
                    using var player = new SoundPlayer(ms);
                    player.Play();
                }
                catch
                {
                    // Ignore audio device errors (headless/RDP/muted sessions).
                }
            });
        }
        catch
        {
            // Ignore synthesis errors.
        }
    }

    private static byte[] BuildWav(NimbusSoundKind kind) => kind switch
    {
        NimbusSoundKind.Greet => Synthesize([(587.33, 0.07, 0.13), (783.99, 0.08, 0.13), (987.77, 0.13, 0.15)]),
        NimbusSoundKind.Poke => Synthesize([(740.0, 0.05, 0.11)]),
        NimbusSoundKind.Slap => Synthesize([(246.94, 0.055, 0.16), (164.81, 0.07, 0.11)]),
        NimbusSoundKind.Dizzy => Synthesize([(523.25, 0.065, 0.13), (440.0, 0.065, 0.13), (349.23, 0.11, 0.11)]),
        NimbusSoundKind.Approval => Synthesize([(659.25, 0.08, 0.15), (987.77, 0.13, 0.15)]),
        NimbusSoundKind.Finished => Synthesize([(587.33, 0.065, 0.11), (783.99, 0.065, 0.11), (1174.66, 0.15, 0.14)]),
        NimbusSoundKind.Error => Synthesize([(293.66, 0.09, 0.15), (220.0, 0.16, 0.15)]),
        _ => Synthesize([(523.25, 0.055, 0.10)])
    };

    private static byte[] Synthesize((double Freq, double DurationSec, double Gain)[] notes)
    {
        const int sampleRate = 22050;
        int totalSamples = 0;
        foreach (var (_, dur, _) in notes)
            totalSamples += (int)(dur * sampleRate);

        using var ms = new MemoryStream(44 + totalSamples * 2);
        using var bw = new BinaryWriter(ms);

        // RIFF header
        bw.Write("RIFF"u8);
        bw.Write(36 + totalSamples * 2);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16); // PCM chunk size
        bw.Write((short)1); // PCM format
        bw.Write((short)1); // Mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2); // Byte rate
        bw.Write((short)2); // Block align
        bw.Write((short)16); // Bits per sample
        bw.Write("data"u8);
        bw.Write(totalSamples * 2);

        foreach (var (freq, dur, gain) in notes)
        {
            int count = (int)(dur * sampleRate);
            int attack = Math.Max(1, (int)(0.008 * sampleRate));
            int release = Math.Max(1, (int)(0.03 * sampleRate));
            for (int i = 0; i < count; i++)
            {
                double env = 1.0;
                if (i < attack) env = i / (double)attack;
                else if (i > count - release) env = Math.Max(0.0, (count - i) / (double)release);
                double t = i / (double)sampleRate;
                double sample = Math.Sin(2.0 * Math.PI * freq * t) * env * gain;
                short pcm = (short)Math.Clamp(sample * 32767.0, short.MinValue, short.MaxValue);
                bw.Write(pcm);
            }
        }

        bw.Flush();
        return ms.ToArray();
    }
}
