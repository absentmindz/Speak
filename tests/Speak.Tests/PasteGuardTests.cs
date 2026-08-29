using MaxFlowWindows.Core;
using Xunit;

namespace Speak.Tests;

public sealed class PasteGuardTests
{
    [Fact]
    public void TerminalPasteIsDisabledByDefault()
    {
        Assert.False(MaxFlowSettings.Default.AllowTerminalPaste);
        Assert.True(PasteGuard.IsTargetBlocked("cmd", "ConsoleWindowClass"));
    }

    [Theory]
    [InlineData("cmd", "ConsoleWindowClass")]
    [InlineData("powershell", "ConsoleWindowClass")]
    [InlineData("pwsh", "ConsoleWindowClass")]
    [InlineData("wsl", "ConsoleWindowClass")]
    [InlineData("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS")]
    [InlineData("custom-shell", "CustomTerminalWindow")]
    public void TerminalOverrideAllowsOnlyTerminalTargets(
        string processName,
        string windowClassName)
    {
        Assert.True(PasteGuard.IsTargetBlocked(processName, windowClassName));
        Assert.False(PasteGuard.IsTargetBlocked(
            processName,
            windowClassName,
            allowTerminalPaste: true));
    }

    [Theory]
    [InlineData("mstsc")]
    [InlineData("keepass")]
    [InlineData("keepassxc")]
    [InlineData("1password")]
    [InlineData("bitwarden")]
    public void TerminalOverrideNeverAllowsSensitiveProcesses(string processName)
    {
        Assert.True(PasteGuard.IsTargetBlocked(
            processName,
            "OrdinaryWindow",
            allowTerminalPaste: true));
    }

    [Theory]
    [InlineData("password-helper", "OrdinaryWindow")]
    [InlineData("credential-broker", "OrdinaryWindow")]
    [InlineData("login-ui", "OrdinaryWindow")]
    [InlineData("remote-desktop", "OrdinaryWindow")]
    [InlineData("pwsh", "PasswordDialog")]
    [InlineData("pwsh", "CredentialPrompt")]
    [InlineData("pwsh", "LoginWindow")]
    [InlineData("pwsh", "RemoteDesktopClass")]
    public void TerminalOverrideNeverAllowsSensitiveNames(
        string processName,
        string windowClassName)
    {
        Assert.True(PasteGuard.IsTargetBlocked(
            processName,
            windowClassName,
            allowTerminalPaste: true));
    }

    [Fact]
    public void OrdinaryTargetRemainsAllowed()
    {
        Assert.False(PasteGuard.IsTargetBlocked("notepad", "Notepad"));
        Assert.False(PasteGuard.IsTargetBlocked(
            "notepad",
            "Notepad",
            allowTerminalPaste: true));
    }
}
