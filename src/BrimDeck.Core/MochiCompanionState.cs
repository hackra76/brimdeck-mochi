using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Core;

public enum MochiState
{
    Idle,
    Working,
    Thinking,
    Searching,
    Approval,
    Question,
    Error,
    Finished,
    RateLimit,
    Sleeping,
    Dizzy
}

public enum MochiEmote
{
    Love,
    Surprised,
    Proud,
    Wink,
    Yawn,
    Happy,
    Annoyed
}

public enum MochiOutfit
{
    Auto,
    None,
    SantaHat,
    PartyHat,
    WitchHat,
    Sunglasses,
    RoundGlasses,
    Bow,
    Scarf,
    Pumpkin
}

public sealed record MochiAgentTask(string SessionId, MochiState State, string? Detail, DateTimeOffset UpdatedUtc);

public enum MochiQuotaMood
{
    Energetic,
    Normal,
    Low,
    Critical,
    Depleted
}

public sealed record MochiStatusSnapshot(
    MochiState State,
    MochiQuotaMood QuotaMood,
    double? RemainingPercent,
    string StatusTitle,
    string DetailText);

/// <summary>
/// Combines BrimDeck's Antigravity quota snapshots with local Antigravity session
/// activity and optional Coucou-compatible hook events over a named pipe.
/// </summary>
public static class MochiCompanionLogic
{
    public static MochiQuotaMood EvaluateQuotaMood(double? remainingPercent)
    {
        if (remainingPercent is null) return MochiQuotaMood.Normal;
        double pct = Math.Clamp(remainingPercent.Value, 0, 100);
        if (pct <= 0.5) return MochiQuotaMood.Depleted;
        if (pct < 15) return MochiQuotaMood.Critical;
        if (pct < 30) return MochiQuotaMood.Low;
        if (pct >= 50) return MochiQuotaMood.Energetic;
        return MochiQuotaMood.Normal;
    }

    public static double? ExtractAntigravityRemainingPercent(IReadOnlyList<ProviderSnapshot> snapshots)
    {
        var ag = snapshots.FirstOrDefault(s => s.Id == ProviderId.Antigravity);
        if (ag is null || ag.Quotas.Count == 0) return null;

        double? minRemaining = null;
        foreach (var quota in ag.Quotas)
        {
            if (!double.IsFinite(quota.UsedPercent)) continue;
            double remaining = Math.Clamp(100 - quota.UsedPercent, 0, 100);
            if (minRemaining is null || remaining < minRemaining.Value)
                minRemaining = remaining;
        }
        return minRemaining;
    }

    /// <summary>
    /// Resolves Mochi's effective visual state from live agent state and quota mood.
    /// Active agent states take precedence; when idle, low/depleted quota shapes the resting mood.
    /// </summary>
    public static MochiStatusSnapshot Resolve(
        MochiState? hookAgentState,
        DateTimeOffset? lastConversationWriteUtc,
        DateTimeOffset nowUtc,
        double? remainingPercent,
        string? hookDetail = null)
    {
        var quotaMood = EvaluateQuotaMood(remainingPercent);
        string quotaLabel = remainingPercent is { } rem
            ? $"Antigravity quota: {rem:0}% left"
            : "Antigravity quota: waiting for desktop app";

        // 1. Explicit hook state (e.g. PreToolUse approval, thinking, working, error)
        if (hookAgentState is { } active && active != MochiState.Idle)
        {
            string title = active switch
            {
                MochiState.Approval => "Waiting for approval",
                MochiState.Thinking => "Thinking...",
                MochiState.Working => "Running tools",
                MochiState.Searching => "Searching",
                MochiState.Question => "Needs input",
                MochiState.Error => "Error encountered",
                MochiState.Finished => "Finished task",
                _ => active.ToString()
            };
            return new MochiStatusSnapshot(active, quotaMood, remainingPercent, title, hookDetail ?? quotaLabel);
        }

        // 2. Local SQLite / WAL activity fallback (~/.gemini/antigravity/conversations)
        if (lastConversationWriteUtc is { } lastWrite)
        {
            var age = nowUtc - lastWrite;
            if (age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(6))
                return new MochiStatusSnapshot(MochiState.Thinking, quotaMood, remainingPercent, "Antigravity active", quotaLabel);
            if (age >= TimeSpan.FromSeconds(6) && age < TimeSpan.FromSeconds(14))
                return new MochiStatusSnapshot(MochiState.Finished, quotaMood, remainingPercent, "Turn completed", quotaLabel);
        }

        // 3. Idle baseline shaped by quota mood
        var idleState = quotaMood switch
        {
            MochiQuotaMood.Depleted => MochiState.Sleeping,
            MochiQuotaMood.Critical => MochiState.RateLimit,
            MochiQuotaMood.Low => MochiState.RateLimit,
            _ => MochiState.Idle
        };

        string idleTitle = quotaMood switch
        {
            MochiQuotaMood.Depleted => "Quota depleted (sleeping)",
            MochiQuotaMood.Critical => "Quota critical!",
            MochiQuotaMood.Low => "Quota running low",
            MochiQuotaMood.Energetic => "Ready & energetic",
            _ => "Idle"
        };

        return new MochiStatusSnapshot(idleState, quotaMood, remainingPercent, idleTitle, quotaLabel);
    }

    public static DateTimeOffset? LatestConversationWriteUtc(IReadOnlyList<string> folders)
    {
        DateTimeOffset? latest = null;
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.db*"))
                {
                    var write = new FileInfo(file).LastWriteTimeUtc;
                    if (write.Year < 2000) continue;
                    var dto = new DateTimeOffset(write, TimeSpan.Zero);
                    if (latest is null || dto > latest.Value) latest = dto;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ignore transient file access errors while SQLite is writing.
            }
        }
        return latest;
    }

    public static MochiState? ParseHookEvent(string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine)) return null;
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            string? kind = root.TryGetProperty("coucou_kind", out var k) ? k.GetString()
                : root.TryGetProperty("event", out var e) ? e.GetString()
                : root.TryGetProperty("state", out var s) ? s.GetString()
                : null;
            return kind?.ToLowerInvariant() switch
            {
                "agy_pre_tool" or "approval" or "permission_request" => MochiState.Approval,
                "thinking" => MochiState.Thinking,
                "working" or "pre_tool_use" or "tool_use" => MochiState.Working,
                "searching" => MochiState.Searching,
                "question" => MochiState.Question,
                "error" => MochiState.Error,
                "finished" or "stop" or "done" => MochiState.Finished,
                "idle" => MochiState.Idle,
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Lightweight named-pipe listener (\\.\pipe\brimdeck-mochi-agent) so external hooks
/// can push live Antigravity agent state transitions directly to Mochi.
/// </summary>
public sealed class MochiAgentPipeListener : IDisposable
{
    public const string PipeName = "brimdeck-mochi-agent";
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<MochiState, string?, string?> _onState;

    public MochiAgentPipeListener(Action<MochiState, string?> onState)
        : this((state, detail, _) => onState(state, detail)) { }

    public MochiAgentPipeListener(Action<MochiState, string?, string?> onState)
    {
        _onState = onState;
        _ = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (MochiCompanionLogic.ParseHookEvent(line) is { } state)
                    {
                        string? detail = null;
                        string? sessionId = null;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            if (doc.RootElement.TryGetProperty("tool_name", out var t))
                                detail = t.GetString();
                            if (doc.RootElement.TryGetProperty("session_id", out var sid))
                                sessionId = sid.GetString();
                            else if (doc.RootElement.TryGetProperty("conversation_id", out var cid))
                                sessionId = cid.GetString();
                        }
                        catch (JsonException) { }
                        _onState(state, detail, sessionId);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { await Task.Delay(500, ct).ConfigureAwait(false); }
        }
    }

    public void Dispose() => _cts.Cancel();
}
