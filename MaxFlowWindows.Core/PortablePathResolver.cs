using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace MaxFlowWindows.Core;

public static class PortablePathResolver
{
	private const string RegistryPath = @"SOFTWARE\Speak";
	private const string ModelsRootValue = "ModelsRoot";

	public static string ResolveModelsRoot(string configuredPath)
	{
		string environmentPath = ExpandPath(Environment.GetEnvironmentVariable("SPEAK_MODELS_ROOT") ?? "");
		if (!string.IsNullOrWhiteSpace(environmentPath))
		{
			return environmentPath;
		}

		string configured = !string.IsNullOrWhiteSpace(configuredPath)
			&& configuredPath.Contains("{ModelsRoot}", StringComparison.OrdinalIgnoreCase)
			? ""
			: ExpandPath(configuredPath);
		if (HasKnownModelLayout(configured))
		{
			return configured;
		}

		string registryPath = SelectRegistryModelsRoot(
			ReadRegistryModelsRoot(Registry.CurrentUser),
			ReadRegistryModelsRoot(Registry.LocalMachine));
		if (HasKnownModelLayout(registryPath))
		{
			return registryPath;
		}

		foreach (string candidate in ModelRootCandidates())
		{
			if (HasKnownModelLayout(candidate))
			{
				return candidate;
			}
		}

		if (!string.IsNullOrWhiteSpace(registryPath))
		{
			return registryPath;
		}

		return !string.IsNullOrWhiteSpace(configured)
			? configured
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Speak", "Models");
	}

	internal static string SelectRegistryModelsRoot(string currentUserPath, string localMachinePath)
	{
		string currentUser = ExpandPath(currentUserPath);
		string localMachine = ExpandPath(localMachinePath);

		// A stale or empty per-user installer path must not hide a valid model
		// location written by an earlier machine-wide installer.
		if (HasKnownModelLayout(currentUser))
		{
			return currentUser;
		}

		if (HasKnownModelLayout(localMachine))
		{
			return localMachine;
		}

		return FirstNonEmpty(currentUser, localMachine);
	}

	public static string ExpandPath(string value, string modelsRoot = "")
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "";
		}

		string appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string expanded = Environment.ExpandEnvironmentVariables(value.Trim())
			.Replace("{AppDir}", appDirectory, StringComparison.OrdinalIgnoreCase)
			.Replace("{LocalAppData}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase)
			.Replace("{CommonAppData}", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase)
			.Replace("{UserProfile}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase);

		if (!string.IsNullOrWhiteSpace(modelsRoot))
		{
			expanded = expanded.Replace("{ModelsRoot}", modelsRoot, StringComparison.OrdinalIgnoreCase);
		}

		try
		{
			return Path.GetFullPath(expanded, appDirectory);
		}
		catch
		{
			return expanded;
		}
	}

	public static bool HasKnownModelLayout(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
		{
			return false;
		}

		return File.Exists(Path.Combine(path, "whisper", "large-v3.pt"))
			|| HasQwenModel(path, "Qwen3-TTS-12Hz-1.7B-CustomVoice")
			|| HasQwenModel(path, "Qwen3-TTS-12Hz-1.7B-Base");
	}

	private static bool HasQwenModel(string root, string directoryName)
	{
		string modelDirectory = Path.Combine(root, directoryName);
		return File.Exists(Path.Combine(modelDirectory, "config.json"))
			&& (File.Exists(Path.Combine(modelDirectory, "model.safetensors"))
				|| File.Exists(Path.Combine(modelDirectory, "model.safetensors.index.json")));
	}

	private static IEnumerable<string> ModelRootCandidates()
	{
		yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Speak", "Models");
		yield return Path.Combine(AppContext.BaseDirectory, "Models");

		DriveInfo[] drives = Array.Empty<DriveInfo>();
		try
		{
			drives = DriveInfo.GetDrives();
		}
		catch
		{
		}

		foreach (DriveInfo drive in drives)
		{
			if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
			{
				continue;
			}

			yield return Path.Combine(drive.RootDirectory.FullName, "Models");
			yield return Path.Combine(drive.RootDirectory.FullName, "Speak", "Models");
		}
	}

	private static string ReadRegistryModelsRoot(RegistryKey hive)
	{
		try
		{
			using RegistryKey key = hive.OpenSubKey(RegistryPath);
			return key?.GetValue(ModelsRootValue) as string ?? "";
		}
		catch
		{
			return "";
		}
	}

	private static string FirstNonEmpty(params string[] values)
	{
		foreach (string value in values)
		{
			if (!string.IsNullOrWhiteSpace(value))
			{
				return value;
			}
		}

		return "";
	}
}
