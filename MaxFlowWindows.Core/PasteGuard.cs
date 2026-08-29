using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace MaxFlowWindows.Core;

public static class PasteGuard
{
	private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
	{
		"cmd", "powershell", "pwsh", "wsl", "windowsterminal"
	};

	private static readonly HashSet<string> AlwaysBlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
	{
		"mstsc", "keepass", "keepassxc", "1password", "bitwarden"
	};

	private static readonly string[] TerminalNameFragments =
	{
		"terminal", "console"
	};

	private static readonly string[] AlwaysBlockedNameFragments =
	{
		"password", "credential", "login", "remote"
	};

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(nint hWnd, [Out] char[] className, int maxCount);

	public static bool IsSafeToPaste(nint targetWindow, bool requireForeground = false, bool allowTerminalPaste = false)
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

			char[] className = new char[256];
			int classNameLength = GetClassName(targetWindow, className, className.Length);
			if (classNameLength <= 0)
				return false;
			if (IsTargetBlocked(
				name,
				new string(className, 0, classNameLength),
				allowTerminalPaste))
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

	internal static bool IsTargetBlocked(
		string processName,
		string windowClassName,
		bool allowTerminalPaste = false)
	{
		if (AlwaysBlockedProcesses.Contains(processName) ||
			ContainsAnyFragment(processName, AlwaysBlockedNameFragments) ||
			ContainsAnyFragment(windowClassName, AlwaysBlockedNameFragments))
			return true;

		return !allowTerminalPaste &&
			(TerminalProcesses.Contains(processName) ||
			ContainsAnyFragment(processName, TerminalNameFragments) ||
			ContainsAnyFragment(windowClassName, TerminalNameFragments));
	}

	private static bool ContainsAnyFragment(string value, string[] fragments)
	{
		foreach (string fragment in fragments)
		{
			if (value.Contains(fragment, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}
}
