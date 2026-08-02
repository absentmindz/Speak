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
        Assert.Equal("whisper-local", config.Transcription.DefaultEngine);
        Assert.Equal("whisper-base", config.Transcription.DefaultModel);
        Assert.Equal("auto", config.Transcription.DefaultDevice);
        Assert.Equal("{AppDir}\\runtime\\whisper\\python.exe", config.Transcription.WhisperPythonPath);
        Assert.Equal("{AppDir}\\tools\\speak_worker.py", config.Transcription.WhisperWrapperPath);
        Assert.Equal("{ModelsRoot}\\whisper\\base.pt", config.Transcription.WhisperModelPath);
        Assert.False(config.UI.AutoLearnCorrections);
        Assert.StartsWith("https://", config.Commercial.WebsiteUrl);
        Assert.StartsWith("https://", config.Commercial.PricingUrl);
        Assert.StartsWith("https://", config.Commercial.CheckoutUrl);
        Assert.StartsWith("https://", config.Commercial.SupportUrl);
        Assert.Empty(config.Commercial.LicensePortalUrl);
        Assert.Empty(config.TTS.EdgeTtsExecutablePath);

        string[] configuredPaths =
        {
            config.Paths.ToolsRoot,
            config.Paths.ModelsRoot,
            config.Paths.WorkspaceRoot,
            config.Paths.CacheRoot,
            config.Transcription.WhisperPythonPath,
            config.Transcription.WhisperWrapperPath,
            config.Transcription.WhisperModelPath,
            config.Transcription.WhisperServerScriptPath,
            config.TTS.ComfyUIPythonPath,
            config.TTS.EdgeTtsExecutablePath,
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
        string[] configurationPaths =
        {
            Path.Combine(repositoryRoot, "appsettings.template.json"),
            Path.Combine(repositoryRoot, "packaging", "appsettings.portable.json"),
            Path.Combine(repositoryRoot, "packaging", "complete", "appsettings.complete.json")
        };

        foreach (string configurationPath in configurationPaths)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configurationPath));
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
    }

    [Fact]
    public void CommercialLinksCanBeOverriddenWithoutEmbeddingCredentials()
    {
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            "Speak-AppConfigCommercialTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            File.WriteAllText(
                Path.Combine(testRoot, "appsettings.json"),
                """
                {
                  "Commercial": {
                    "WebsiteUrl": "https://example.com/speak",
                    "PricingUrl": "https://example.com/speak/pricing",
                    "CheckoutUrl": "https://checkout.example.com/speak",
                    "SupportUrl": "https://example.com/speak/support",
                    "LicensePortalUrl": "https://example.com/speak/licenses"
                  }
                }
                """);

            AppConfig config = AppConfig.Load(testRoot);

            Assert.Equal("https://example.com/speak", config.Commercial.WebsiteUrl);
            Assert.Equal("https://checkout.example.com/speak", config.Commercial.CheckoutUrl);
            Assert.Equal("https://example.com/speak/licenses", config.Commercial.LicensePortalUrl);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void ExistingWhisperConfigurationIsPreservedWhenLoaded()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(
            directory.Combine("appsettings.json"),
            """
            {
              "Transcription": {
                "DefaultEngine": "whisper-local",
                "DefaultModel": "whisper-large-v3",
                "DefaultDevice": "cuda",
                "WhisperPythonPath": "legacy-python.exe",
                "WhisperWrapperPath": "legacy-worker.py",
                "WhisperModelPath": "legacy-model.pt"
              }
            }
            """);

        AppConfig config = AppConfig.Load(directory.Path);

        Assert.Equal("whisper-local", config.Transcription.DefaultEngine);
        Assert.Equal("whisper-large-v3", config.Transcription.DefaultModel);
        Assert.Equal("cuda", config.Transcription.DefaultDevice);
        Assert.EndsWith("legacy-python.exe", config.Transcription.WhisperPythonPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("legacy-worker.py", config.Transcription.WhisperWrapperPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("legacy-model.pt", config.Transcription.WhisperModelPath, StringComparison.OrdinalIgnoreCase);
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
            Assert.Equal("whisper-base", config.Transcription.DefaultModel);
            Assert.Equal("auto", config.Transcription.DefaultDevice);
            Assert.NotEmpty(config.Transcription.WhisperPythonPath);
            Assert.NotEmpty(config.Transcription.WhisperWrapperPath);
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

    [Fact]
    public void PortableInstallConfigurationsDefaultToLocalWhisperBaseOnAuto()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] configurationPaths =
        {
            Path.Combine(repositoryRoot, "appsettings.template.json"),
            Path.Combine(repositoryRoot, "packaging", "appsettings.portable.json"),
            Path.Combine(repositoryRoot, "packaging", "complete", "appsettings.complete.json")
        };

        foreach (string configurationPath in configurationPaths)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configurationPath));
            JsonElement transcription = document.RootElement.GetProperty("Transcription");

            Assert.Equal("whisper-local", transcription.GetProperty("DefaultEngine").GetString());
            Assert.Equal("whisper-base", transcription.GetProperty("DefaultModel").GetString());
            Assert.Equal("auto", transcription.GetProperty("DefaultDevice").GetString());
            Assert.Equal("{AppDir}\\runtime\\whisper\\python.exe", transcription.GetProperty("WhisperPythonPath").GetString(), ignoreCase: true);
            Assert.Equal("{AppDir}\\tools\\speak_worker.py", transcription.GetProperty("WhisperWrapperPath").GetString(), ignoreCase: true);
            Assert.Equal("{ModelsRoot}\\whisper\\base.pt", transcription.GetProperty("WhisperModelPath").GetString(), ignoreCase: true);
            Assert.Equal("", document.RootElement.GetProperty("TTS").GetProperty("EdgeTtsExecutablePath").GetString());
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
