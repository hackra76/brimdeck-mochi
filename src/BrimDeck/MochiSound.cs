using System.Collections.Concurrent;
using System.IO;
using System.Media;

namespace BrimDeck;

public enum MochiSoundKind
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
/// Lightweight procedural WAV synthesizer for Mochi's subtle audio cues.
/// Generates and caches short 16-bit PCM chimes in memory without external asset files.
/// </summary>
public static class MochiSound
{
    private static readonly ConcurrentDictionary<MochiSoundKind, byte[]> Cache = new();
    public static bool Enabled { get; set; } = true;

    public static void Play(MochiSoundKind kind)
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

    private static byte[] BuildWav(MochiSoundKind kind) => kind switch
    {
        MochiSoundKind.Greet => Synthesize([(523.25, 0.08, 0.14), (659.25, 0.09, 0.14), (783.99, 0.14, 0.16)]),
        MochiSoundKind.Poke => Synthesize([(620.0, 0.055, 0.12)]),
        MochiSoundKind.Slap => Synthesize([(210.0, 0.06, 0.18), (140.0, 0.07, 0.12)]),
        MochiSoundKind.Dizzy => Synthesize([(480.0, 0.07, 0.14), (390.0, 0.07, 0.14), (310.0, 0.12, 0.12)]),
        MochiSoundKind.Approval => Synthesize([(587.33, 0.09, 0.16), (880.0, 0.14, 0.16)]),
        MochiSoundKind.Finished => Synthesize([(523.25, 0.07, 0.12), (659.25, 0.07, 0.12), (1046.5, 0.16, 0.15)]),
        MochiSoundKind.Error => Synthesize([(260.0, 0.10, 0.16), (196.0, 0.18, 0.16)]),
        _ => Synthesize([(440.0, 0.06, 0.10)])
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
