using BrimDeck.Core;

namespace BrimDeck.Native;

// Why a reading is missing, worded in the interface language when the reading is taken.
internal static class Why
{
    public static string Waiting => "waiting for a second reading";
    public static string NoCounters => "the system's performance counters are unavailable";
    public static string NoFrequency => "the processor reports no frequency";
    public static string NoMemory => "the memory status could not be read";
    public static string NoNetwork => "the network adapters could not be read";
    public static string NoAdapter => "no physical network adapter was found";
    public static string NoGpu => "the graphics driver provides no GPU counters";
    public static string NoGpuMemory => "the graphics driver reports no memory in use";
    public static string NoProcesses => "the process list could not be read";
}
