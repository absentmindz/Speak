using System;

namespace MaxFlowWindows.Core;

// Health is exposed only through the opt-in, token-authenticated RestApiServer.
// Keeping the DTO in its historical file avoids a data-contract migration while
// removing the second unauthenticated listener.
public sealed class HealthReport
{
	public string Status { get; set; } = "ok";
	public string Version { get; set; } = "";
	public DateTimeOffset Uptime { get; set; }
	public string Timespan { get; set; } = "";
	public bool WhisperServerRunning { get; set; }
	public bool WhisperModelLoaded { get; set; }
	public bool TtsWorkerRunning { get; set; }
	public string AudioInputDevice { get; set; } = "";
	public string SelectedModel { get; set; } = "";
	public string StorageUsedMb { get; set; } = "";
	public int HistoryCount { get; set; }
	public int VocabularyCount { get; set; }
	public string Error { get; set; } = "";
}
