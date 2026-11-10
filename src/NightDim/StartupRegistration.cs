using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace NightDim;

internal sealed class StartupRegistration(string executablePath)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NightDim";
    private readonly string _command = $"\"{executablePath}\" --autostart";

    internal bool? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return IsCurrentEntry(key);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return null;
        }
    }

    internal bool Set(bool enable)
    {
        try
        {
            if (enable)
            {
                if (_command.Length > 260)
                    return false;
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                key.SetValue(ValueName, _command, RegistryValueKind.String);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (IsCurrentEntry(key))
                    key!.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }

    private bool IsCurrentEntry(RegistryKey? key) => string.Equals(
        key?.GetValue(ValueName) as string, _command, StringComparison.OrdinalIgnoreCase);
}
