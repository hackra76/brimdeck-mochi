using BrimDeck.Core;

namespace BrimDeck.Tests;

internal static class NimbusCompanionTests
{
    public static void Run(Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

        // Quota mood thresholds
        check("Nimbus quota mood: high (>50%) is Energetic", NimbusCompanionLogic.EvaluateQuotaMood(82) == NimbusQuotaMood.Energetic);
        check("Nimbus quota mood: mid (30-49%) is Normal", NimbusCompanionLogic.EvaluateQuotaMood(40) == NimbusQuotaMood.Normal);
        check("Nimbus quota mood: low (<30%) is Low", NimbusCompanionLogic.EvaluateQuotaMood(20) == NimbusQuotaMood.Low);
        check("Nimbus quota mood: critical (<15%) is Critical", NimbusCompanionLogic.EvaluateQuotaMood(8) == NimbusQuotaMood.Critical);
        check("Nimbus quota mood: 0% is Depleted", NimbusCompanionLogic.EvaluateQuotaMood(0) == NimbusQuotaMood.Depleted);

        // Idle state reacts to quota
        var depleted = NimbusCompanionLogic.Resolve(null, null, now, 0);
        check("Nimbus depleted quota sleeps when idle", depleted.State == NimbusState.Sleeping);

        var low = NimbusCompanionLogic.Resolve(null, null, now, 12);
        check("Nimbus low quota shows RateLimit when idle", low.State == NimbusState.RateLimit);

        // Active agent state overrides quota mood
        var approval = NimbusCompanionLogic.Resolve(NimbusState.Approval, null, now, 12, "run_command");
        check("Nimbus Approval overrides low quota", approval.State == NimbusState.Approval && approval.DetailText == "run_command");

        // Local WAL write activity detection
        var activeWal = NimbusCompanionLogic.Resolve(null, now.AddSeconds(-2), now, 80);
        check("Nimbus recent WAL write triggers Thinking", activeWal.State == NimbusState.Thinking);

        var finishedWal = NimbusCompanionLogic.Resolve(null, now.AddSeconds(-9), now, 80);
        check("Nimbus 9s-old WAL write triggers Finished", finishedWal.State == NimbusState.Finished);

        // Hook event parsing
        check("Nimbus parses agy_pre_tool as Approval",
            NimbusCompanionLogic.ParseHookEvent("""{"coucou_kind":"agy_pre_tool","tool_name":"run_command"}""") == NimbusState.Approval);
        check("Nimbus parses thinking event",
            NimbusCompanionLogic.ParseHookEvent("""{"event":"thinking"}""") == NimbusState.Thinking);

        // Snapshot extraction
        var agSnap = new ProviderSnapshot(ProviderId.Antigravity)
        {
            Quotas = [new Quota("Model 1", 25, null, 100), new Quota("Model 2", 75, null, 100)]
        };
        check("Nimbus extracts best remaining Antigravity quota (75%)",
            NimbusCompanionLogic.ExtractAntigravityRemainingPercent([agSnap]) == 75);

        // One exhausted pool must not put Nimbus to sleep while another pool is full
        var mixedSnap = new ProviderSnapshot(ProviderId.Antigravity)
        {
            Quotas = [new Quota("Claude", 100, null, 100), new Quota("Gemini", 0, null, 100)]
        };
        double? mixedBest = NimbusCompanionLogic.ExtractAntigravityRemainingPercent([mixedSnap]);
        var exhausted = NimbusCompanionLogic.ExtractExhaustedAntigravityPools([mixedSnap]);
        check("Nimbus best-pool quota is 100% when only one pool is exhausted", mixedBest == 100);
        check("Nimbus names the exhausted pool", exhausted.Count == 1 && exhausted[0] == "Claude");
        var mixed = NimbusCompanionLogic.Resolve(null, null, now, mixedBest, null, exhausted);
        check("Nimbus stays awake when one pool is exhausted but another is full", mixed.State == NimbusState.Idle);
        check("Nimbus detail mentions the depleted pool", mixed.DetailText.Contains("Claude depleted"));
        var allGone = new ProviderSnapshot(ProviderId.Antigravity)
        {
            Quotas = [new Quota("Claude", 100, null, 100), new Quota("Gemini", 100, null, 100)]
        };
        check("Nimbus sleeps only when every pool is exhausted",
            NimbusCompanionLogic.Resolve(null, null, now, NimbusCompanionLogic.ExtractAntigravityRemainingPercent([allGone])).State == NimbusState.Sleeping);

        // NimbusQuickAsk backend detection & Antigravity CLI args
        var backends = BrimDeck.NimbusQuickAsk.DetectBackends(refresh: true);
        check("NimbusQuickAsk.DetectBackends succeeds without throwing", backends is not null);
        var agy = backends?.FirstOrDefault(b => b.Backend.Executable == "agy");
        if (agy is { Backend: not null })
        {
            check("NimbusQuickAsk builds non-interactive plan args for Antigravity CLI",
                agy.Value.Backend.BuildArgs("hello") == "-p \"hello\" --mode plan --disable-slash-commands");
        }
    }
}
