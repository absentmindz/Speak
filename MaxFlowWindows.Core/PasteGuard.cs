using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace MaxFlowWindows.Core;

public static class PasteGuard
{
	private static readonly HashSet<string> SensitiveProcesses = new(StringComparer.OrdinalIgnoreCase)
	{
		"cmd", "powershell", "pwsh", "wsl", "windowsterminal",
		"mstsc", "keepass", "keepassxc", "1password", "bitwarden"
	};

	private static readonly string[] SensitiveNameFragments =
	{
		"terminal", "console", "password", "credential", "login", "remote"
	};

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(nint hWnd, StringBuilder className, int maxCount);

	public static bool IsSafeToPaste(nint targetWindow, bool requireForeground = false)
	{
		// Paste is an externally visible action. If the target cannot be
		// positively identified, fail closed and leave the text on the clipboard.
		if (targetWindow == nint.Zero)
			return false;

		try
		{
			if (requireForeground && GetForegroundWindow() != targetWindow)
				return false;

			if (GetWindowThreadProcessId(targetWindow, out uint pid) == 0 || pid == 0)
				return false;

			using Process process = Process.GetProcessById((int)pid);
			string name = process.ProcessName;
			if (SensitiveProcesses.Contains(name) || ContainsSensitiveFragment(name))
				return false;

			var className = new StringBuilder(256);
			if (GetClassName(targetWindow, className, className.Capacity) <= 0)
				return false;
			if (ContainsSensitiveFragment(className.ToString()))
				return false;

			if (requireForeground)
			{
				AutomationElement? focused = AutomationElement.FocusedElement;
				if (focused == null)
					return false;

				AutomationElement.AutomationElementInformation current = focused.Current;
				if (current.ProcessId != (int)pid || current.IsPassword)
					return false;
			}

			return true;
		}
		catch
		{
			return false;
		}
	}

	public static string DescribeRisks(nint targetWindow)
	{
		try
		{
			if (GetWindowThreadProcessId(targetWindow, out uint pid) == 0 || pid == 0)
				return "The paste target could not be verified.";

			using Process process = Process.GetProcessById((int)pid);
			return $"Pasting into {process.ProcessName} may expose sensitive input.";
		}
		catch
		{
			return "The paste target could not be verified.";
		}
	}

	private static bool ContainsSensitiveFragment(string value)
	{
		foreach (string fragment in SensitiveNameFragments)
		{
			if (value.Contains(fragment, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}
}
