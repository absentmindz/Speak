using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MaxFlowWindows.Core;

namespace MaxFlowWindows;

public class App : Application
{
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.27.0")]
	public void InitializeBackgroundExceptionHandling()
	{
		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
		base.DispatcherUnhandledException += OnDispatcherUnhandledException;
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.27.0")]
	public void InitializeComponent()
	{
		base.StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
	}

	[STAThread]
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.27.0")]
	public static void Main()
	{
		App app = new App();
		app.InitializeBackgroundExceptionHandling();
		app.InitializeComponent();
		app.Run();
	}

	private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		string detail = (e.ExceptionObject as Exception)?.ToString() ?? "Unknown fatal error";
		WriteCrashReport("FATAL", detail);
	}

	private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		string detail = e.Exception?.ToString() ?? "Unknown dispatcher exception";
		WriteCrashReport("DISPATCHER", detail);
		e.Handled = true;
	}

	private static void WriteCrashReport(string category, string detail)
	{
		try
		{
			string crashDir = Path.Combine(
				SpeakDataPaths.ResolveDataRoot(), "crashes");
			Directory.CreateDirectory(crashDir);

			string timestamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
			string path = Path.Combine(crashDir, $"crash_{timestamp}_{category}.txt");

			var sb = new StringBuilder();
			sb.AppendLine($"Speak Crash Report");
			sb.AppendLine($"Category: {category}");
			sb.AppendLine($"Timestamp: {DateTimeOffset.Now:O}");
			sb.AppendLine($"Version: {typeof(App).Assembly.GetName().Version}");
			sb.AppendLine($"OS: {Environment.OSVersion}");
			sb.AppendLine($"Process: {Environment.ProcessPath}");
			sb.AppendLine($"Working Set: {Environment.WorkingSet / 1024 / 1024} MB");
			sb.AppendLine();
			sb.AppendLine("=== Exception Details ===");
			sb.AppendLine(detail);
			sb.AppendLine();
			sb.AppendLine("=== Environment StackTrace ===");
			sb.AppendLine(Environment.StackTrace);

			File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
			AppLog.Info($"Crash report written to {path}");
		}
		catch
		{
		}
	}
}
