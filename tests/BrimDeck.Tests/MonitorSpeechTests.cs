using BrimDeck.Core;

// What the GPU block says to a screen reader (MonitorSpeech.GpuBlock, which BlockGauge.Spoken returns): a usage while the adapter has
// a reading, and what the tooltip says, never a usage of zero, while it has none.
internal static class MonitorSpeechTests
{
    internal static void Run(Action<string, bool> check)
    {
        var language = Loc.Language;
        try
        {
            Loc.Use(Loc.English);
            check("GPU block reads its usage and memory", MonitorSpeech.GpuBlock(.12, .18, "x", "Switch to GPU primary") == "GPU block: usage 12% · VRAM 18%, Switch to GPU primary");
            check("GPU block reads a real zero as zero", MonitorSpeech.GpuBlock(0, null, "x", "Switch") == "GPU block: usage 0%, Switch");
            check("GPU block without a reading says why, not zero", MonitorSpeech.GpuBlock(null, null, "No reading: counter unavailable", "Switch") == "GPU block: No reading: counter unavailable, Switch");
            check("GPU block without a reading ignores a stale memory figure", !MonitorSpeech.GpuBlock(null, .5, "No reading", "Switch").Contains("VRAM"));
            check("GPU block without a reading or a note still says there is none", MonitorSpeech.GpuBlock(null, null, "", "Switch") == "GPU block: No reading, Switch");
        }
        finally { Loc.Use(language); }
    }
}
