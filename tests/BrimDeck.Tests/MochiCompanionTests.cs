using BrimDeck.Core;

namespace BrimDeck.Tests;

internal static class MochiCompanionTests
{
    public static void Run(Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

        // Quota mood thresholds
        check("Mochi quota mood: high (>50%) is Energetic", MochiCompanionLogic.EvaluateQuotaMood(82) == MochiQuotaMood.Energetic);
        check("Mochi quota mood: mid (30-49%) is Normal", MochiCompanionLogic.EvaluateQuotaMood(40) == MochiQuotaMood.Normal);
        check("Mochi quota mood: low (<30%) is Low", MochiCompanionLogic.EvaluateQuotaMood(20) == MochiQuotaMood.Low);
        check("Mochi quota mood: critical (<15%) is Critical", MochiCompanionLogic.EvaluateQuotaMood(8) == MochiQuotaMood.Critical);
        check("Mochi quota mood: 0% is Depleted", MochiCompanionLogic.EvaluateQuotaMood(0) == MochiQuotaMood.Depleted);

        // Idle state reacts to quota
        var depleted = MochiCompanionLogic.Resolve(null, null, now, 0);
        check("Mochi depleted quota sleeps when idle", depleted.State == MochiState.Sleeping);

        var low = MochiCompanionLogic.Resolve(null, null, now, 12);
        check("Mochi low quota shows RateLimit when idle", low.State == MochiState.RateLimit);

        // Active agent state overrides quota mood
        var approval = MochiCompanionLogic.Resolve(MochiState.Approval, null, now, 12, "run_command");
        check("Mochi Approval overrides low quota", approval.State == MochiState.Approval && approval.DetailText == "run_command");

        // Local WAL write activity detection
        var activeWal = MochiCompanionLogic.Resolve(null, now.AddSeconds(-2), now, 80);
        check("Mochi recent WAL write triggers Thinking", activeWal.State == MochiState.Thinking);

        var finishedWal = MochiCompanionLogic.Resolve(null, now.AddSeconds(-9), now, 80);
        check("Mochi 9s-old WAL write triggers Finished", finishedWal.State == MochiState.Finished);

        // Hook event parsing
        check("Mochi parses agy_pre_tool as Approval",
            MochiCompanionLogic.ParseHookEvent("""{"coucou_kind":"agy_pre_tool","tool_name":"run_command"}""") == MochiState.Approval);
        check("Mochi parses thinking event",
            MochiCompanionLogic.ParseHookEvent("""{"event":"thinking"}""") == MochiState.Thinking);

        // Snapshot extraction
        var agSnap = new ProviderSnapshot(ProviderId.Antigravity)
        {
            Quotas = [new Quota("Model 1", 25, null, 100), new Quota("Model 2", 75, null, 100)]
        };
        check("Mochi extracts min remaining Antigravity quota (25%)",
            MochiCompanionLogic.ExtractAntigravityRemainingPercent([agSnap]) == 25);
    }
}
