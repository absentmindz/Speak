using System;
using System.Collections.Generic;
using System.IO;

namespace MaxFlowWindows.Core;

public static class SpeakDataPaths
{
	internal const string LegacyMigrationMarkerFileName = ".legacy-import-complete-v2";
	internal const string PreviousLegacyMigrationMarkerFileName = ".legacy-import-complete-v1";

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
		CopyLegacyLocalDataIfNeeded(
			destinationRoot,
			FormerOpenClawDataRoots(),
			FallbackLocalRoots(),
			!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPEAK_DATA_ROOT")));
	}

	internal static void CopyLegacyLocalDataIfNeeded(
		string destinationRoot,
		IEnumerable<string> formerOpenClawRoots,
		IEnumerable<string> fallbackLocalRoots,
		bool explicitDataRoot)
	{
		Directory.CreateDirectory(destinationRoot);

		// An explicit data root is an isolation boundary. Importing files from the
		// user's normal LocalAppData directory into that location would be
		// surprising and can copy private history, recordings, and logs into a
		// portable, test, or shared directory without consent.
		if (explicitDataRoot ||
			File.Exists(Path.Combine(destinationRoot, LegacyMigrationMarkerFileName)))
		{
			return;
		}

		// The previous completion marker is still authoritative. Converting it to
		// the current marker without importing anything prevents data deliberately
		// cleared after that migration from reappearing.
		if (File.Exists(Path.Combine(
			destinationRoot,
			PreviousLegacyMigrationMarkerFileName)))
		{
			WriteLegacyMigrationMarker(destinationRoot);
			return;
		}

		// Speak 0.5 selected the first existing OpenClawData root before falling
		// back to LocalAppData. Import exactly that source even when a newer
		// destination already has partial data, otherwise an upgrade can hide the
		// settings/history the previous executable actually displayed.
		foreach (string root in formerOpenClawRoots)
		{
			if (Directory.Exists(root) && !SamePath(root, destinationRoot))
			{
				CopyLegacyRoot(root, destinationRoot, overwriteExisting: true);
				WriteLegacyMigrationMarker(destinationRoot);
				return;
			}
		}

		// With no former OpenClawData root, existing destination data means this
		// installation has already crossed the legacy boundary. Record completion
		// without refilling files that the user may have deliberately cleared.
		if (HasCurrentSpeakData(destinationRoot))
		{
			WriteLegacyMigrationMarker(destinationRoot);
			return;
		}

		foreach (string root in fallbackLocalRoots)
		{
			if (Directory.Exists(root) && !SamePath(root, destinationRoot))
			{
				CopyLegacyRoot(root, destinationRoot, overwriteExisting: false);
			}
		}

		WriteLegacyMigrationMarker(destinationRoot);
	}

	internal static bool ShouldMigrateLegacyLocalData(string destinationRoot)
	{
		return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPEAK_DATA_ROOT"))
			&& !File.Exists(Path.Combine(destinationRoot, LegacyMigrationMarkerFileName))
			&& !File.Exists(Path.Combine(
				destinationRoot,
				PreviousLegacyMigrationMarkerFileName));
	}

	private static bool HasCurrentSpeakData(string root)
	{
		string[] currentFiles = new string[5]
		{
			".onboarded",
			"settings.json",
			"vocabulary.json",
			"history.json",
			"keyboard-bridge.json"
		};
		foreach (string fileName in currentFiles)
		{
			if (File.Exists(Path.Combine(root, fileName)))
			{
				return true;
			}
		}

		return Directory.Exists(Path.Combine(root, "recordings"))
			|| Directory.Exists(Path.Combine(root, "logs"));
	}

	private static void WriteLegacyMigrationMarker(string destinationRoot)
	{
		// Keep the marker outside every data-file family that users can clear.
		// Without it, clearing history could resurrect private legacy data on the
		// next launch because the importer copies files only when they are missing.
		File.WriteAllText(
			Path.Combine(destinationRoot, LegacyMigrationMarkerFileName),
			"Legacy data import completed.\n");
	}

	private static IReadOnlyList<string> FormerOpenClawDataRoots()
	{
		string modelsRoot = "";
		try
		{
			modelsRoot = AppConfig.Current.Paths.ModelsRoot;
		}
		catch
		{
			// A malformed optional configuration must not prevent the fixed legacy
			// root from being considered.
		}

		return BuildFormerOpenClawDataRoots(modelsRoot, @"D:\OpenClawData\Speak");
	}

	private static IReadOnlyList<string> FallbackLocalRoots()
	{
		return BuildFallbackLocalRoots(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
	}

	internal static IReadOnlyList<string> BuildFormerOpenClawDataRoots(
		string modelsRoot,
		string fixedDDriveRoot)
	{
		var roots = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		if (!string.IsNullOrWhiteSpace(modelsRoot))
		{
			AddLegacyRoot(
				roots,
				seen,
				Path.Combine(modelsRoot, "..", "OpenClawData", "Speak"));
		}

		AddLegacyRoot(roots, seen, fixedDDriveRoot);
		return roots;
	}

	internal static IReadOnlyList<string> BuildFallbackLocalRoots(string localAppData)
	{
		var roots = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		AddLegacyRoot(roots, seen, Path.Combine(localAppData, "Speak"));
		AddLegacyRoot(roots, seen, Path.Combine(localAppData, "MaxFlowWindows"));
		return roots;
	}

	private static void AddLegacyRoot(
		List<string> roots,
		HashSet<string> seen,
		string candidate)
	{
		if (string.IsNullOrWhiteSpace(candidate))
		{
			return;
		}

		string fullPath = Path.GetFullPath(candidate);
		if (seen.Add(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
		{
			roots.Add(fullPath);
		}
	}

	private static void CopyLegacyRoot(
		string sourceRoot,
		string destinationRoot,
		bool overwriteExisting)
	{
		string[] fileNames = new string[4]
		{
			"settings.json",
			"vocabulary.json",
			"history.json",
			"keyboard-bridge.json"
		};
		foreach (string fileName in fileNames)
		{
			CopyFile(
				Path.Combine(sourceRoot, fileName),
				Path.Combine(destinationRoot, fileName),
				overwriteExisting);
		}
		CopyDirectoryFiles(
			Path.Combine(sourceRoot, "recordings"),
			Path.Combine(destinationRoot, "recordings"),
			overwriteExisting);
		CopyDirectoryFiles(
			Path.Combine(sourceRoot, "logs"),
			Path.Combine(destinationRoot, "logs"),
			overwriteExisting);
	}

	private static void CopyFile(
		string source,
		string destination,
		bool overwriteExisting)
	{
		if (!File.Exists(source) || (!overwriteExisting && File.Exists(destination)))
		{
			return;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(destination));
		string temporary = destination + ".speak-migration-" +
			Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.Copy(source, temporary, overwrite: false);
			File.Move(temporary, destination, overwriteExisting);
		}
		finally
		{
			if (File.Exists(temporary))
			{
				File.Delete(temporary);
			}
		}
	}

	private static void CopyDirectoryFiles(
		string sourceRoot,
		string destinationRoot,
		bool overwriteExisting)
	{
		if (!Directory.Exists(sourceRoot))
		{
			return;
		}
		foreach (string item in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
		{
			string relativePath = Path.GetRelativePath(sourceRoot, item);
			string destination = Path.Combine(destinationRoot, relativePath);
			CopyFile(item, destination, overwriteExisting);
		}
	}

	private static bool SamePath(string left, string right)
	{
		return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
	}
}
