using System;
using System.Collections.Generic;
using System.IO;

namespace MaxFlowWindows.Core;

public static class SpeakDataPaths
{
	private static string? _cachedDataRoot;
	private static readonly object _lock = new();

	public static string ResolveDataRoot()
	{
		lock (_lock)
		{
			if (_cachedDataRoot != null)
			{
				return _cachedDataRoot;
			}

			string environmentVariable = Environment.GetEnvironmentVariable("SPEAK_DATA_ROOT");
			if (!string.IsNullOrWhiteSpace(environmentVariable))
			{
				_cachedDataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(environmentVariable));
				return _cachedDataRoot;
			}

			try
			{
				var appConfig = AppConfig.Current;
				if (!string.IsNullOrWhiteSpace(appConfig.Paths.ModelsRoot))
				{
					string legacyRoot = Path.GetFullPath(Path.Combine(appConfig.Paths.ModelsRoot, "..", "OpenClawData", "Speak"));
					if (Directory.Exists(legacyRoot))
					{
						_cachedDataRoot = legacyRoot;
						return _cachedDataRoot;
					}
				}
			}
			catch
			{
			}

			string existingDDriveRoot = "D:\\OpenClawData\\Speak";
			if (Directory.Exists(existingDDriveRoot))
			{
				_cachedDataRoot = existingDDriveRoot;
				return _cachedDataRoot;
			}

			_cachedDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Speak");
			return _cachedDataRoot;
		}
	}

	public static void ResetCache()
	{
		lock (_lock)
		{
			_cachedDataRoot = null;
		}
	}

	public static void CopyLegacyLocalDataIfNeeded(string destinationRoot)
	{
		Directory.CreateDirectory(destinationRoot);
		foreach (string item in LegacyLocalRoots())
		{
			if (Directory.Exists(item) && !SamePath(item, destinationRoot))
			{
				string[] array = new string[4] { "settings.json", "vocabulary.json", "history.json", "keyboard-bridge.json" };
				foreach (string path in array)
				{
					CopyFileIfMissing(Path.Combine(item, path), Path.Combine(destinationRoot, path));
				}
				CopyDirectoryFilesIfMissing(Path.Combine(item, "recordings"), Path.Combine(destinationRoot, "recordings"));
				CopyDirectoryFilesIfMissing(Path.Combine(item, "logs"), Path.Combine(destinationRoot, "logs"));
			}
		}
	}

	private static IEnumerable<string> LegacyLocalRoots()
	{
		string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		yield return Path.Combine(localAppData, "Speak");
		yield return Path.Combine(localAppData, "MaxFlowWindows");
	}

	private static void CopyFileIfMissing(string source, string destination)
	{
		if (File.Exists(source) && !File.Exists(destination))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(destination));
			File.Copy(source, destination);
		}
	}

	private static void CopyDirectoryFilesIfMissing(string sourceRoot, string destinationRoot)
	{
		if (!Directory.Exists(sourceRoot))
		{
			return;
		}
		foreach (string item in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
		{
			string relativePath = Path.GetRelativePath(sourceRoot, item);
			string destination = Path.Combine(destinationRoot, relativePath);
			CopyFileIfMissing(item, destination);
		}
	}

	private static bool SamePath(string left, string right)
	{
		return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
	}
}
