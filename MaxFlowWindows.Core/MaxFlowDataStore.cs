using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MaxFlowWindows.Core;

public sealed class MaxFlowDataStore
{
	private const int MaxBackupCopies = 3;
	private const int CurrentSchemaVersion = 1;

	private readonly object _saveSync = new object();

	private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};

	public string Root { get; }

	private string SettingsPath => Path.Combine(Root, "settings.json");

	private string VocabularyPath => Path.Combine(Root, "vocabulary.json");

	private string HistoryPath => Path.Combine(Root, "history.json");

	public MaxFlowDataStore()
		: this(ResolveDefaultRoot())
	{
	}

	public MaxFlowDataStore(string root)
	{
		Root = root;
	}

	private static string ResolveDefaultRoot()
	{
		string text = SpeakDataPaths.ResolveDataRoot();
		SpeakDataPaths.CopyLegacyLocalDataIfNeeded(text);
		return text;
	}

	public MaxFlowSettings LoadSettings()
	{
		return Load(SettingsPath, MaxFlowSettings.Default);
	}

	public void SaveSettings(MaxFlowSettings settings)
	{
		Save(SettingsPath, settings);
	}

	public void PurgeSettingsRecoveryCopies()
	{
		lock (_saveSync)
			DeleteRecoveryCopies(SettingsPath);
	}

	public List<VocabularyEntry> LoadVocabulary()
	{
		return Load(VocabularyPath, VocabularyEntry.Defaults.ToList());
	}

	public void SaveVocabulary(IEnumerable<VocabularyEntry> entries)
	{
		Save(VocabularyPath, entries.ToList());
	}

	public Task SaveVocabularyAsync(IEnumerable<VocabularyEntry> entries)
	{
		return SaveAsync(VocabularyPath, entries.ToList());
	}

	public List<TranscriptCard> LoadHistory()
	{
		return Load(HistoryPath, new List<TranscriptCard>());
	}

	public void SaveHistory(IEnumerable<TranscriptCard> cards)
	{
		Save(HistoryPath, cards.ToList());
	}

	public Task SaveHistoryAsync(IEnumerable<TranscriptCard> cards)
	{
		return SaveAsync(HistoryPath, cards.ToList());
	}

	public void ClearHistoryData(IEnumerable<string>? audioPaths = null)
	{
		lock (_saveSync)
		{
			DeleteDataFileFamily(HistoryPath);
			DeleteHistoryAudio(audioPaths ?? Array.Empty<string>());
		}
	}

	private T Load<T>(string path, T fallback)
	{
		Exception? firstFailure = null;
		foreach (string candidate in CandidatePaths(path))
		{
			if (!File.Exists(candidate))
			{
				continue;
			}
			try
			{
				string json = File.ReadAllText(candidate, Encoding.UTF8);
				T? value = JsonSerializer.Deserialize<T>(json, _jsonOptions);
				if (value != null)
				{
					if (!string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
						AppLog.Warn($"Recovered {Path.GetFileName(path)} from {Path.GetFileName(candidate)}.");
					return value;
				}
			}
			catch (Exception exception)
			{
				firstFailure ??= exception;
			}
		}

		if (firstFailure != null)
			AppLog.Warn("Could not load local data file or any backup: " + Path.GetFileName(path), firstFailure);
		return fallback;
	}

	private void Save<T>(string path, T value)
	{
		lock (_saveSync)
		{
			Directory.CreateDirectory(Root);
			string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				string contents = JsonSerializer.Serialize(value, _jsonOptions);
				using (FileStream stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
				using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
				{
					writer.Write(contents);
					writer.Flush();
					stream.Flush(flushToDisk: true);
				}

				RotateOlderBackups(path);
				if (File.Exists(path))
				{
					File.Replace(tempPath, path, path + ".bak1", ignoreMetadataErrors: true);
				}
				else
				{
					File.Move(tempPath, path);
				}
				WriteSchemaFile(path);
			}
			finally
			{
				try
				{
					if (File.Exists(tempPath))
						File.Delete(tempPath);
				}
				catch
				{
				}
			}
		}
	}

	private void WriteSchemaFile(string dataPath)
	{
		string schemaPath = dataPath + ".schema";
		try
		{
			File.WriteAllText(schemaPath, $"{{\"$schemaVersion\":{CurrentSchemaVersion}}}", Encoding.UTF8);
		}
		catch
		{
		}
	}

	private static IEnumerable<string> CandidatePaths(string path)
	{
		yield return path;
		for (int i = 1; i <= MaxBackupCopies; i++)
			yield return path + ".bak" + i;
	}

	private static void RotateOlderBackups(string path)
	{
		for (int i = MaxBackupCopies; i >= 2; i--)
		{
			string destination = path + ".bak" + i;
			string source = path + ".bak" + (i - 1);
			try
			{
				if (File.Exists(destination))
					File.Delete(destination);
				if (File.Exists(source))
					File.Move(source, destination);
			}
			catch (Exception exception)
			{
				AppLog.Warn($"Could not rotate backup {Path.GetFileName(source)}.", exception);
			}
		}
	}

	private Task SaveAsync<T>(string path, T value)
	{
		return Task.Run(delegate
		{
			Save(path, value);
		});
	}

	private static void DeleteDataFileFamily(string path)
	{
		var candidates = new List<string>
		{
			path,
			path + ".schema",
			path + ".tmp"
		};
		for (int i = 1; i <= MaxBackupCopies; i++)
			candidates.Add(path + ".bak" + i);

		string? directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
			candidates.AddRange(Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".*.tmp", SearchOption.TopDirectoryOnly));

		foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			try
			{
				if (File.Exists(candidate))
					File.Delete(candidate);
			}
			catch (Exception exception)
			{
				AppLog.Warn("Could not erase " + Path.GetFileName(candidate) + ".", exception);
			}
		}
	}

	private static void DeleteRecoveryCopies(string path)
	{
		var candidates = new List<string>
		{
			path + ".tmp"
		};
		for (int i = 1; i <= MaxBackupCopies; i++)
			candidates.Add(path + ".bak" + i);

		string? directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
			candidates.AddRange(Directory.EnumerateFiles(
				directory,
				Path.GetFileName(path) + ".*.tmp",
				SearchOption.TopDirectoryOnly));

		foreach (string candidate in candidates.Distinct(
			StringComparer.OrdinalIgnoreCase))
		{
			try
			{
				if (File.Exists(candidate))
					File.Delete(candidate);
			}
			catch (Exception exception)
			{
				AppLog.Warn(
					"Could not erase settings recovery copy " +
					Path.GetFileName(candidate) + ".",
					exception);
			}
		}
	}

	private void DeleteHistoryAudio(IEnumerable<string> audioPaths)
	{
		string dataRoot = Path.GetFullPath(Root);
		string recordingsRoot = Path.GetFullPath(Path.Combine(Root, "recordings"));
		string archiveRoot = Path.GetFullPath(Path.Combine(Root, "recordings-archive"));

		foreach (string audioPath in audioPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
		{
			try
			{
				string fullPath = Path.GetFullPath(audioPath);
				if (!IsSafeHistoryAudioPath(fullPath, dataRoot, recordingsRoot) &&
					!IsSafeHistoryAudioPath(fullPath, dataRoot, archiveRoot))
					continue;

				if (File.Exists(fullPath))
					File.Delete(fullPath);
			}
			catch (Exception exception)
			{
				AppLog.Warn("Could not erase history audio reference.", exception);
			}
		}
	}

	private static bool IsSafeHistoryAudioPath(
		string fullPath,
		string dataRoot,
		string allowedRoot)
	{
		string canonicalDataRoot = Path.GetFullPath(dataRoot)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string canonicalRoot = Path.GetFullPath(allowedRoot)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string rootPrefix = canonicalRoot + Path.DirectorySeparatorChar;
		if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
			return false;

		string relative = Path.GetRelativePath(canonicalRoot, fullPath);
		if (Path.IsPathRooted(relative) ||
			relative.Equals("..", StringComparison.Ordinal) ||
			relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
			relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
			return false;

		string current = canonicalDataRoot;
		if (!IsExistingPathFreeOfReparsePoint(current))
			return false;

		string pathFromDataRoot = Path.GetRelativePath(
			canonicalDataRoot,
			fullPath);
		foreach (string component in pathFromDataRoot.Split(
			new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
			StringSplitOptions.RemoveEmptyEntries))
		{
			current = Path.Combine(current, component);
			if (!IsExistingPathFreeOfReparsePoint(current))
				return false;
		}
		return true;
	}

	private static bool IsExistingPathFreeOfReparsePoint(string path)
	{
		if (!File.Exists(path) && !Directory.Exists(path))
			return true;
		try
		{
			return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
		}
		catch
		{
			return false;
		}
	}
}
