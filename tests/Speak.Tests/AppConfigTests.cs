using System.Text.Json;
using System.Text.RegularExpressions;
using MaxFlowWindows.Core;
using Xunit;

namespace Speak.Tests;

public sealed class AppConfigTests
{
    private static readonly Regex AbsoluteWindowsPath =
        new(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant);

    [Fact]
    public void DefaultsArePortableAndPrivacyPreserving()
    {
        var config = new AppConfig();

        Assert.Equal("{AppDir}", config.Paths.ToolsRoot);
        Assert.Empty(config.Paths.ModelsRoot);
        Assert.Empty(config.Paths.WorkspaceRoot);
        Assert.False(config.UI.AutoLearnCorrections);

        string[] configuredPaths =
        {
            config.Paths.ToolsRoot,
            config.Paths.ModelsRoot,
            config.Paths.WorkspaceRoot,
            config.Paths.CacheRoot,
            config.Transcription.WhisperPythonPath,
            config.Transcription.WhisperModelPath,
            config.TTS.ComfyUIPythonPath,
            config.TTS.QwenTtsCustomVoiceModelPath,
            config.TTS.QwenTtsBaseModelPath,
            config.TTS.TortoiseModelDir
        };

        Assert.All(configuredPaths, path => Assert.DoesNotMatch(AbsoluteWindowsPath, path));
    }

    [Fact]
    public void PortableTokensResolveWithoutDeveloperSpecificPaths()
    {
        string resolved = PortablePathResolver.ExpandPath("{AppDir}\\Tools");
        string expected = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "Tools"));

        Assert.Equal(expected, resolved, ignoreCase: true);
        Assert.DoesNotContain(@"C:\Users\", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublishedTemplateContainsNoAbsolutePathsOrSecretValues()
    {
        string repositoryRoot = FindRepositoryRoot();
        string templatePath = Path.Combine(repositoryRoot, "appsettings.template.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(templatePath));

        foreach (JsonProperty property in EnumerateProperties(document.RootElement))
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string value = property.Value.GetString() ?? "";
            Assert.DoesNotMatch(AbsoluteWindowsPath, value);

            if (Regex.IsMatch(
                    property.Name,
                    "(?i)(api.?key|token|secret|password|credential)"))
            {
                Assert.True(
                    string.IsNullOrEmpty(value) || value.EndsWith("_API_KEY", StringComparison.Ordinal),
                    $"Configuration property '{property.Name}' must not contain a credential.");
            }
        }
    }

    [Fact]
    public void DocumentationTemplateIsNeverLoadedAsRuntimeConfiguration()
    {
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            "Speak-AppConfigTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            File.WriteAllText(
                Path.Combine(testRoot, "appsettings.template.json"),
                """
                {
                  "Paths": { "ToolsRoot": "", "CacheRoot": "" },
                  "Transcription": {
                    "DefaultEngine": "",
                    "DefaultModel": "",
                    "DefaultDevice": "",
                    "WhisperModelPath": ""
                  },
                  "TTS": {
                    "DefaultEngine": "",
                    "DefaultVoice": "",
                    "DefaultLanguage": ""
                  }
                }
                """);

            AppConfig config = AppConfig.Load(testRoot);

            Assert.Equal("whisper-local", config.Transcription.DefaultEngine);
            Assert.Equal("whisper-large-v3", config.Transcription.DefaultModel);
            Assert.Equal("cuda", config.Transcription.DefaultDevice);
            Assert.NotEmpty(config.Transcription.WhisperModelPath);
            Assert.Equal("qwen3-customvoice-1.7b", config.TTS.DefaultEngine);
            Assert.Equal("Aiden", config.TTS.DefaultVoice);
            Assert.NotEmpty(config.Paths.CacheRoot);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static IEnumerable<JsonProperty> EnumerateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                yield return property;
                foreach (JsonProperty descendant in EnumerateProperties(property.Value))
                {
                    yield return descendant;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                foreach (JsonProperty descendant in EnumerateProperties(item))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "appsettings.template.json"))
                && File.Exists(Path.Combine(current.FullName, "Speak.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Speak repository root.");
    }
}
