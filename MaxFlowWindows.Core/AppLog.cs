using System;
using System.IO;

namespace MaxFlowWindows.Core;

public static class AppLog
{
	private static readonly object Sync = new object();

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
}
