using System.IO;
using Microsoft.Win32;
using BrimDeck.Core;

namespace BrimDeck.Native;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key is not null && IsEnabled(key);
    }
    internal static bool IsEnabled(RegistryKey key) => key.GetValue("BrimDeck") is string value && value.Length > 0;
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        SetEnabled(key, enabled, Environment.ProcessPath);
    }
    internal static void SetEnabled(RegistryKey key, bool enabled, string? executable)
    {
        if (!enabled) { key.DeleteValue("BrimDeck", false); return; }
        if (executable is null) throw new IOException("The application location could not be determined.");
        if (!string.Equals(Path.GetFileNameWithoutExtension(executable), "BrimDeck", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Start the app from BrimDeck.exe before setting it to start with Windows.");
        var command = '"' + executable + '"';
        if (command.Length > 260) throw new IOException("The application path is too long to register for startup.");
        key.SetValue("BrimDeck", command, RegistryValueKind.String);
    }
}
