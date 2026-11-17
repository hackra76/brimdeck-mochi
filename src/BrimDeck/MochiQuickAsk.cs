using System.Diagnostics;
using System.IO;
using System.Text;

namespace BrimDeck;

internal sealed record QuickAskBackend(string Name, string Executable, Func<string, string> BuildArgs);

internal static class MochiQuickAsk
{
    private static readonly QuickAskBackend[] Candidates =
    [
        new("Claude CLI", "claude", p => $"-p {QuoteArg(p)}"),
        new("Gemini CLI", "gemini", p => $"-p {QuoteArg(p)}"),
        new("Codex CLI", "codex", p => $"exec {QuoteArg(p)}"),
        new("Antigravity CLI", "agy", p => $"chat {QuoteArg(p)}")
    ];

    private static IReadOnlyList<(QuickAskBackend Backend, string ResolvedPath)>? _cached;

    public static IReadOnlyList<(QuickAskBackend Backend, string ResolvedPath)> DetectBackends()
    {
        if (_cached is not null) return _cached;
        var found = new List<(QuickAskBackend, string)>();
        foreach (var candidate in Candidates)
        {
            if (ResolveOnPath(candidate.Executable) is { } path)
                found.Add((candidate, path));
        }
        _cached = found;
        return found;
    }

    public static async Task<(bool Ok, string Output)> AskAsync(
        QuickAskBackend backend,
        string resolvedPath,
        string prompt,
        CancellationToken ct)
    {
        bool isCmd = resolvedPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || resolvedPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        string args = backend.BuildArgs(prompt);
        var psi = new ProcessStartInfo
        {
            FileName = isCmd ? "cmd.exe" : resolvedPath,
            Arguments = isCmd ? $"/c \"\"{resolvedPath}\" {args}\"" : args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(psi);
        if (process is null)
            return (false, $"Failed to start {backend.Name}.");

        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        string stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        string stderr = (await stderrTask.ConfigureAwait(false)).Trim();

        if (process.ExitCode == 0 && stdout.Length > 0)
            return (true, stdout);
        if (stdout.Length > 0)
            return (true, stdout);
        return (false, stderr.Length > 0 ? stderr : $"{backend.Name} exited with code {process.ExitCode}.");
    }

    private static string? ResolveOnPath(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = command,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(1500);
            if (proc.ExitCode != 0) return null;
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (File.Exists(trimmed)) return trimmed;
            }
        }
        catch { }
        return null;
    }

    private static string QuoteArg(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
