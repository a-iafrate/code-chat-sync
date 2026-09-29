using CodeChatSync.Core;
using Microsoft.Win32;

namespace CodeChatSync.App.Services;

/// <summary>
/// Stores auto-start entries under the current user's Windows <c>Run</c> key.
/// </summary>
/// <remarks>
/// Per-user so it never needs elevation. Unlike an MSIX StartupTask it is not
/// removed automatically when the app is uninstalled, so the app removes it itself
/// when the user turns auto-start off.
/// </remarks>
public sealed class RegistryStartupEntryStore : IStartupEntryStore
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(name) as string;
    }

    public void Write(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public void Remove(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
