using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Globalization;
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
		// A dispatcher exception may leave application state inconsistent.
		// Record it, then let WPF terminate instead of keeping a broken process
		// alive and making health checks appear successful.
		e.Handled = false;
	}

	private static void WriteCrashReport(string category, string detail)
	{
		try
		{
			string crashDir = Path.Combine(
				SpeakDataPaths.ResolveDataRoot(), "crashes");
			Directory.CreateDirectory(crashDir);

			string timestamp = DateTimeOffset.Now.ToString(
				"yyyyMMdd_HHmmss",
				CultureInfo.InvariantCulture);
			string path = Path.Combine(crashDir, $"crash_{timestamp}_{category}.txt");

			var sb = new StringBuilder();
			sb.AppendLine("Speak Crash Report");
			sb.Append("Category: ").AppendLine(category);
			sb.Append("Timestamp: ").AppendLine(
				DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
			sb.Append("Version: ").AppendLine(
				typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown");
			sb.Append("OS: ").AppendLine(Environment.OSVersion.ToString());
			sb.Append("Process: ").AppendLine(Environment.ProcessPath);
			sb.Append("Working Set: ").Append(
				(Environment.WorkingSet / 1024 / 1024).ToString(
					CultureInfo.InvariantCulture)).AppendLine(" MB");
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
