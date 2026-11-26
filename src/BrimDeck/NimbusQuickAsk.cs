using System.Diagnostics;
using System.IO;
using System.Text;

namespace BrimDeck;

internal sealed record QuickAskBackend(string Name, string Executable, Func<string, string> BuildArgs);

internal static class NimbusQuickAsk
{
    private static readonly QuickAskBackend[] Candidates =
    [
        new("Claude CLI", "claude", p => $"-p {QuoteArg(p)}"),
        new("Gemini CLI", "gemini", p => $"-p {QuoteArg(p)}"),
        new("Codex CLI", "codex", p => $"exec {QuoteArg(p)}"),
        new("Antigravity CLI", "agy", p => $"-p {QuoteArg(p)} --mode plan --disable-slash-commands")
    ];

    private static IReadOnlyList<(QuickAskBackend Backend, string ResolvedPath)>? _cached;

    public static IReadOnlyList<(QuickAskBackend Backend, string ResolvedPath)> DetectBackends(bool refresh = false)
    {
        if (!refresh && _cached is { Count: > 0 }) return _cached;
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
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(psi);
        if (process is null)
            return (false, $"Failed to start {backend.Name}.");

        process.StandardInput.Close();

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
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1500);
                if (proc.ExitCode == 0)
                {
                    foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = line.Trim();
                        if (IsWindowsExecutable(trimmed) && File.Exists(trimmed)) return trimmed;
                    }
                }
            }
        }
        catch { }

        foreach (var candidate in FallbackPaths(command))
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsWindowsExecutable(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".com", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> FallbackPaths(string command)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return Path.Combine(localAppData, "agy", "bin", $"{command}.exe");
            yield return Path.Combine(localAppData, "Programs", "Antigravity", "bin", $"{command}.exe");
        }
        if (!string.IsNullOrEmpty(appData))
            yield return Path.Combine(appData, "npm", $"{command}.cmd");
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return Path.Combine(localAppData, "Volta", "bin", $"{command}.exe");
            yield return Path.Combine(localAppData, "pnpm", $"{command}.cmd");
        }
        if (!string.IsNullOrEmpty(userProfile))
        {
            yield return Path.Combine(userProfile, ".bun", "bin", $"{command}.exe");
            yield return Path.Combine(userProfile, ".local", "bin", $"{command}.exe");
        }
    }

    private static string QuoteArg(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
