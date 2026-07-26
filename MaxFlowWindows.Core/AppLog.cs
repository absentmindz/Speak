using System;
using System.IO;

namespace MaxFlowWindows.Core;

public static class AppLog
{
	private static readonly object Sync = new object();
	private const long MaxLogBytes = 5 * 1024 * 1024;
	private const int MaxLogFiles = 5;

	private static string LogPath => Path.Combine(Path.Combine(SpeakDataPaths.ResolveDataRoot(), "logs"), "speak.log");

	public static void Info(string message)
	{
		Write("INFO", message);
	}

	public static void Warn(string message, Exception? exception = null)
	{
		Write("WARN", message, exception);
	}

	public static void Error(string message, Exception? exception = null)
	{
		Write("ERROR", message, exception);
	}

	private static void Write(string level, string message, Exception? exception = null)
	{
		try
		{
			lock (Sync)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
				RotateIfNeeded();
				string text = $"{DateTimeOffset.Now:O} [{level}] {message}";
				if (exception != null)
				{
					text = text + " " + exception.GetType().Name + ": " + exception.Message;
				}
				File.AppendAllText(LogPath, text + Environment.NewLine);
			}
		}
		catch
		{
		}
	}

	private static void RotateIfNeeded()
	{
		try
		{
			if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < MaxLogBytes)
				return;

			for (int index = MaxLogFiles - 1; index >= 1; index--)
			{
				string source = index == 1
					? LogPath
					: Path.Combine(Path.GetDirectoryName(LogPath)!, $"speak.{index - 1}.log");
				string destination = Path.Combine(Path.GetDirectoryName(LogPath)!, $"speak.{index}.log");
				if (File.Exists(destination))
					File.Delete(destination);
				if (File.Exists(source))
					File.Move(source, destination);
			}
		}
		catch
		{
			// Logging must never prevent the app from continuing.
		}
	}
}
