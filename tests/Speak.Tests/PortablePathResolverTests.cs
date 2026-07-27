using MaxFlowWindows.Core;
using Xunit;

namespace Speak.Tests;

public sealed class PortablePathResolverTests
{
    [Fact]
    public void ValidMachineModelRootWinsOverInvalidCurrentUserPath()
    {
        using TestDirectory directory = new();
        string currentUserPath = directory.Combine("missing-user-models");
        string localMachinePath = directory.Combine("machine-models");
        string whisperDirectory = System.IO.Path.Combine(localMachinePath, "whisper");
        Directory.CreateDirectory(whisperDirectory);
        File.WriteAllText(System.IO.Path.Combine(whisperDirectory, "large-v3.pt"), "test");

        string selected = PortablePathResolver.SelectRegistryModelsRoot(
            currentUserPath,
            localMachinePath);

        Assert.Equal(
            System.IO.Path.GetFullPath(localMachinePath),
            selected,
            ignoreCase: true);
    }
}
