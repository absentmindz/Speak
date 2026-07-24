using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MaxFlowWindows.Core;

public static class PasteGuard
{
    private static readonly HashSet<string> SensitiveProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "wsl", "windowsterminal",
        "mstsc", "remote",
        "keepass", "keepassxc", "1password", "bitwarden",
        "password", "login"
    };

    private static readonly HashSet<string> SensitiveWindowClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Edit", "RichEdit20W", "RichEdit50W",
    };

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(nint hWnd, System.Text.StringBuilder className, int maxCount);

    public static bool IsSafeToPaste()
    {
        try
        {
            nint foreground = GetForegroundWindow();
            if (foreground == nint.Zero)
                return true;

            if (GetWindowThreadProcessId(foreground, out uint pid) == 0)
                return true;

            using var process = Process.GetProcessById((int)pid);
            string name = process.ProcessName;

            if (SensitiveProcesses.Contains(name))
                return false;

            var sb = new System.Text.StringBuilder(256);
            if (GetClassName(foreground, sb, sb.Capacity) > 0)
            {
                string cls = sb.ToString();
                if (cls.Contains("Console", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("Terminal", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("Password", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
        catch
        {
            return true;
        }
    }

    public static string DescribeRisks(nint foregroundWindow)
    {
        try
        {
            if (GetWindowThreadProcessId(foregroundWindow, out uint pid) == 0)
                return "";

            using var process = Process.GetProcessById((int)pid);
            return $"Pasting into {process.ProcessName} — this may expose sensitive input.";
        }
        catch
        {
            return "";
        }
    }
}