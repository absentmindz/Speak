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

	public List<VocabularyEntry> LoadVocabulary()
	{
		List<VocabularyEntry> list = Load(VocabularyPath, VocabularyEntry.Defaults.ToList());
		if (list.Count != 0)
		{
			return list;
		}
		return VocabularyEntry.Defaults.ToList();
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

	private T Load<T>(string path, T fallback)
	{
		try
		{
			if (!File.Exists(path))
			{
				return fallback;
			}
			string json = File.ReadAllText(path, Encoding.UTF8);
			T val = JsonSerializer.Deserialize<T>(json, _jsonOptions);
			return (T)((val != null) ? ((object)val) : ((object)fallback));
		}
		catch (Exception exception)
		{
			string backupPath = path + ".bak1";
			if (File.Exists(backupPath))
			{
				try
				{
					AppLog.Warn($"Could not load {Path.GetFileName(path)}, trying backup...", exception);
					return Load<T>(backupPath, fallback);
				}
				catch
				{
				}
			}
			AppLog.Warn("Could not load local data file: " + Path.GetFileName(path), exception);
			return fallback;
		}
	}

	private void Save<T>(string path, T value)
	{
		Directory.CreateDirectory(Root);
		RotateBackups(path);
		string text = path + ".tmp";
		string contents = JsonSerializer.Serialize(value, _jsonOptions);
		File.WriteAllText(text, contents, Encoding.UTF8);
		File.Copy(text, path, overwrite: true);
		File.Delete(text);
		WriteSchemaFile(path);
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

	private void RotateBackups(string path)
	{
		for (int i = MaxBackupCopies - 1; i >= 1; i--)
		{
			string older = path + ".bak" + i;
			string newer = path + ".bak" + (i - 1);
			if (File.Exists(older))
			{
				try { File.Delete(older); } catch { }
			}
			if (File.Exists(newer))
			{
				try { File.Copy(newer, older, overwrite: true); } catch { }
			}
		}
		if (File.Exists(path))
		{
			try { File.Copy(path, path + ".bak1", overwrite: true); } catch { }
		}
	}

	private Task SaveAsync<T>(string path, T value)
	{
		return Task.Run(delegate
		{
			Save(path, value);
		});
	}
}
