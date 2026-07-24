using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using MaxFlowWindows.Core;
using Microsoft.Win32;
using NAudio.Wave;

namespace MaxFlowWindows;

public class MainWindow : Window, IComponentConnector
{
	private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

	private struct KeyboardHookStruct
	{
		public int VkCode;

		public int ScanCode;

		public int Flags;

		public int Time;

		public nint ExtraInfo;
	}

	private struct Input
	{
		public int Type;

		public InputUnion U;
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct InputUnion
	{
		[FieldOffset(0)]
		public KeyboardInput Ki;
	}

	private struct KeyboardInput
	{
		public ushort Vk;

		public ushort Scan;

		public uint Flags;

		public uint Time;

		public nint ExtraInfo;
	}

	private sealed record DeliveryCommand(string Text, string? OutputDestinationId, bool PressEnterAfterPaste);

	private sealed record ModelDiscoveryCacheEntry(DateTimeOffset CapturedAt, LlmModelDiscoveryResult Result);

	private sealed class WhisperTranscribeRequest
	{
		public string AudioPath { get; set; } = "";

		public string Model { get; set; } = "";

		public string ModelDir { get; set; } = "";

		public string Language { get; set; } = "";

		public string Device { get; set; } = "auto";

		public int KeepAliveMinutes { get; set; } = 10;
	}

	private sealed class WhisperTranscribeResponse
	{
		public string Text { get; set; } = "";

		public string Model { get; set; } = "";

		public string Device { get; set; } = "";

		public bool CudaAvailable { get; set; }

		public string Torch { get; set; } = "";

		public double ElapsedSeconds { get; set; }
	}

	private sealed class WhisperHealthResponse
	{
		public bool Ok { get; set; }

		public bool ModelLoaded { get; set; }

		public string Model { get; set; } = "";

		public string Device { get; set; } = "";

		public bool CudaAvailable { get; set; }

		public string Torch { get; set; } = "";

		public double IdleTimeoutSeconds { get; set; }

		public double IdleForSeconds { get; set; }

		public bool IsTranscribing { get; set; }
	}

	private readonly MaxFlowDataStore _store = new MaxFlowDataStore();

	private readonly LocalTextFormatter _formatter = new LocalTextFormatter();

	private readonly CloudSpeechTranscriber _cloudSpeechTranscriber = new CloudSpeechTranscriber();

	private readonly LlmTextPolisher _llmPolisher = new LlmTextPolisher();

	private readonly LlmModelDiscovery _llmModelDiscovery = new LlmModelDiscovery();

	private readonly LocalTtsSynthesizer _ttsSynthesizer = new LocalTtsSynthesizer();

	private readonly ClipboardHistory _clipboardHistory = new ClipboardHistory(10);
	private readonly Lazy<RestApiServer> _restApiServer = new Lazy<RestApiServer>(() => new RestApiServer(19876));
	private BackgroundJanitor? _janitor;
	private ProcessJob? _processJob;

	private readonly ObservableCollection<VocabularyEntry> _vocabulary;

	private readonly ObservableCollection<VocabularyEntry> _learnedCorrections = new ObservableCollection<VocabularyEntry>();

	private readonly ObservableCollection<TranscriptCard> _history;

	private readonly ObservableCollection<AudioInputDeviceOption> _audioDevices = new ObservableCollection<AudioInputDeviceOption>();

	private readonly ObservableCollection<string> _llmPolishModels = new ObservableCollection<string>();

	private readonly ObservableCollection<string> _cloudSttModels = new ObservableCollection<string>();

	private readonly ObservableCollection<TtsEngineOption> _ttsEngines = new ObservableCollection<TtsEngineOption>(TtsEngineOption.Presets);

	private readonly ObservableCollection<TtsVoiceOption> _ttsVoices = new ObservableCollection<TtsVoiceOption>();

	private readonly Dictionary<string, string> _cloneVoiceRefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private readonly IReadOnlyList<TranscriptionModelOption> _transcriptionModels;

	private readonly ICollectionView _historyView;

	private readonly HttpClient _whisperClient = new HttpClient
	{
		Timeout = TimeSpan.FromMinutes(45.0)
	};

	private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	private readonly DispatcherTimer _recordingTimer = new DispatcherTimer
	{
		Interval = TimeSpan.FromSeconds(1.0)
	};

	private readonly DispatcherTimer _historySearchRefreshTimer = new DispatcherTimer
	{
		Interval = TimeSpan.FromMilliseconds(180.0)
	};

	private readonly Dictionary<string, SolidColorBrush> _resourceBrushCache = new Dictionary<string, SolidColorBrush>(StringComparer.Ordinal);

	private readonly Dictionary<string, ModelDiscoveryCacheEntry> _modelDiscoveryCache = new Dictionary<string, ModelDiscoveryCacheEntry>(StringComparer.OrdinalIgnoreCase);

	private readonly SemaphoreSlim _historySaveGate = new SemaphoreSlim(1, 1);

	private readonly SemaphoreSlim _vocabularySaveGate = new SemaphoreSlim(1, 1);

	private readonly MediaPlayer _ttsPreviewPlayer = new MediaPlayer();

	private bool _dictionaryDeletionActionsInstalled;

	private bool _editorialMotionApplied;

	private System.Windows.Controls.Button _dictionaryDeleteSelectedButton;

	private bool _ttsPanelInstalled;

	private System.Windows.Controls.ComboBox? _ttsEngineComboBox;

	private System.Windows.Controls.ComboBox? _ttsVoiceComboBox;

	private System.Windows.Controls.TextBox? _ttsSampleTextBox;

	private TextBlock? _ttsStatusTextBlock;

	private TextBlock? _ttsOutputTextBlock;

	private System.Windows.Controls.Button? _ttsSpeakCurrentButton;

	private System.Windows.Controls.Button? _ttsGenerateSampleButton;

	private System.Windows.Controls.Button? _ttsOpenLastButton;

	private System.Windows.Controls.Button? _ttsPlayLastButton;

	private System.Windows.Controls.Button? _ttsStopPlaybackButton;

	private System.Windows.Controls.Button? _ttsWarmModelButton;

	private TextBlock? _ttsWarmStatusTextBlock;

	private CancellationTokenSource? _ttsWarmCts;

	private bool _isWarming;

	private System.Windows.Controls.Button? _audioTabButton;

	private Grid? _audioPage;

	private System.Windows.Controls.ComboBox? _audioSttEngineComboBox;

	private System.Windows.Controls.ComboBox? _audioTranscriptionModelComboBox;

	private System.Windows.Controls.ComboBox? _audioCloudSttProviderComboBox;

	private System.Windows.Controls.ComboBox? _audioCloudSttModelComboBox;

	private System.Windows.Controls.ComboBox? _audioWhisperDeviceComboBox;

	private System.Windows.Controls.ComboBox? _audioModelKeepAliveComboBox;

	private System.Windows.Controls.ComboBox? _audioInputComboBox;

	private TextBlock? _audioTranscribeStatusTextBlock;

	private System.Windows.Controls.TextBox? _audioCloneReferenceTextBox;

	private System.Windows.Controls.TextBox? _audioCloneNameTextBox;

	private System.Windows.Controls.ComboBox? _audioCloneEngineComboBox;

	private System.Windows.Controls.TextBox? _ttsCloneTextTextBox;

	private System.Windows.Controls.Button? _ttsCloneGenerateButton;

	private TextBlock? _audioCloneStatusTextBlock;

	private System.Windows.Controls.TextBox? _audioDesignPromptTextBox;

	private TextBlock? _audioDesignStatusTextBlock;

	private TextBlock? _audioPlaybackStatusTextBlock;

	private MaxFlowSettings _settings;

	private DictationMode _selectedMode = DictationMode.Presets[0];

	private bool _isLoading;

	private bool _isRecording;

	private bool _isTranscribing;

	private bool? _lastFeedbackRecordingState;

	private string _activeTab = "dictate";

	private string? _recordingPath;

	private DateTimeOffset? _recordingStartedAt;

	private WaveInEvent? _waveIn;

	private WaveFileWriter? _waveWriter;

	private TaskCompletionSource? _recordingStopped;

	private Exception? _recordingException;

	private Process? _whisperServerProcess;

	private string _whisperServerLastError = "";

	private ShortcutWidgetWindow? _shortcutWidget;

	private LearningToastWindow? _learningToast;

	private ShortcutGesture _shortcutGesture = ShortcutGesture.Default;

	private bool _shortcutIsLatched;

	private bool _shortcutToggleInFlight;

	private bool _isCapturingShortcut;

	private LowLevelKeyboardProc? _keyboardHookProc;

	private nint _keyboardHookId;

	private HwndSource? _hotkeySource;

	private bool _nativeHotkeyRegistered;

	private string _shortcutStatusDetail = "";

	private nint _deliveryTargetWindow;

	private string _deliveryTargetProcessName = "";

	private NotifyIcon? _trayIcon;

	private ContextMenuStrip? _trayMenu;

	private string _lastRetryRawText = "";

	private string _lastRetryFormattedText = "";

	private string _lastRetrySourceLabel = "";

	private CancellationTokenSource? _externalEditLearningCts;

	private CancellationTokenSource? _settingsScrollCts;

	private CancellationTokenSource? _ttsGenerationCts;

	private TranscriptStats? _cachedTranscriptStats;

	private VoiceProfileStats? _cachedVoiceProfileStats;

	private int _statsVersion;

	private int _cachedTranscriptStatsVersion = -1;

	private int _cachedVoiceProfileStatsVersion = -1;

	private int _historySaveVersion;

	private int _vocabularySaveVersion;

	private const string WhisperServerBaseUrl = "http://127.0.0.1:39731";

	private static readonly TimeSpan ModelDiscoveryCacheDuration = TimeSpan.FromMinutes(5.0);

	private const string StartupRunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

	private const string StartupValueName = "Speak";

	private const int DwmUseImmersiveDarkMode = 20;

	private const int DwmUseImmersiveDarkModeBefore20H1 = 19;

	private const int WH_KEYBOARD_LL = 13;

	private const int WM_KEYDOWN = 256;

	private const int WM_KEYUP = 257;

	private const int WM_SYSKEYDOWN = 260;

	private const int WM_SYSKEYUP = 261;

	private const int WM_HOTKEY = 786;

	private const int DictationHotkeyId = 19782;

	private const int INPUT_KEYBOARD = 1;

	private const int SW_RESTORE = 9;

	private const uint KEYEVENTF_KEYUP = 2u;

	private const ushort VK_CONTROL = 17;

	private const ushort VK_RETURN = 13;

	private const ushort VK_V = 86;

	private const int ExternalEditLearningPolls = 30;

	private const int ExternalEditLearningInitialDelayMs = 1200;

	private const int ExternalEditLearningPollDelayMs = 1200;

	private const int ExternalEditStableReadsRequired = 2;

	private const int MaxFocusedEditTextLength = 12000;

	internal System.Windows.Controls.Button DictateTabButton;

	internal System.Windows.Controls.Button HistoryTabButton;

	internal System.Windows.Controls.Button ProfileTabButton;

	internal System.Windows.Controls.Button DictionaryTabButton;

	internal System.Windows.Controls.Button SettingsTabButton;

	internal TextBlock StorePathTextBlock;

	internal TextBlock HeaderTitleTextBlock;

	internal TextBlock HeaderSubtitleTextBlock;

	internal TextBlock ModePillTextBlock;

	internal TextBlock StatusTextBlock;

	internal Grid DictatePage;

	internal TextBlock RecordingStatusTextBlock;

	internal System.Windows.Controls.Button OpenAudioButton;

	internal TextBlock RuntimeDevicePillTextBlock;

	internal TextBlock RuntimeModelPillTextBlock;

	internal TextBlock TranscriptionModelStatusTextBlock;

	internal Border DictateWordCounterPanel;

	internal TextBlock DictateWordsSpokenTextBlock;

	internal TextBlock DictateTodayWordsTextBlock;

	internal TextBlock DictateVoiceStatsTextBlock;

	internal Ellipse AmbientMicHalo;

	internal Ellipse RecordRipple1;

	internal ScaleTransform RippleScale1;

	internal Ellipse RecordRipple2;

	internal ScaleTransform RippleScale2;

	internal System.Windows.Controls.Button RecordButton;

	internal TextBlock RecordButtonGlyph;

	internal StackPanel RecordActivityPanel;

	internal Border RecordBar1;

	internal Border RecordBar2;

	internal Border RecordBar3;

	internal Border RecordBar4;

	internal Border RecordBar5;

	internal UniformGrid ModesPanel;

	internal TextBlock ModeInstructionTextBlock;

	internal System.Windows.Controls.TextBox RawTranscriptTextBox;

	internal System.Windows.Controls.TextBox FormattedOutputTextBox;

	internal Border ActionBarPanel;

	internal Grid HistoryPage;

	internal TextBlock HistoryCountTextBlock;

	internal TextBlock WordsSpokenTextBlock;

	internal TextBlock VoiceStatsTextBlock;

	internal System.Windows.Controls.TextBox HistorySearchTextBox;

	internal System.Windows.Controls.ListBox HistoryListBox;

	internal System.Windows.Controls.TextBox HistorySelectedTextBox;

	internal System.Windows.Controls.TextBox HistoryRawTextBox;

	internal System.Windows.Controls.TextBox HistoryComparisonTextBox;

	internal System.Windows.Controls.TextBox HistoryTagsTextBox;

	internal Grid VoiceProfilePage;

	internal TextBlock ProfileWordsSpokenTextBlock;

	internal TextBlock ProfileTodayWordsTextBlock;

	internal TextBlock ProfileSavedCorrectionsTextBlock;

	internal TextBlock ProfileAutoLearnedTextBlock;

	internal TextBlock ProfileAccuracyTextBlock;

	internal TextBlock ProfileSessionsTextBlock;

	internal TextBlock ProfileAverageTextBlock;

	internal TextBlock ProfileStreakTextBlock;

	internal TextBlock ProfileLearningTextBlock;

	internal Grid DictionaryPage;

	internal TextBlock DictionaryCountTextBlock;

	internal StackPanel DictionaryTabsPanel;

	internal Border DictionaryHeroPanel;

	internal DataGrid VocabularyGrid;

	internal System.Windows.Controls.CheckBox DictionaryAutoLearnCheckBox;

	internal System.Windows.Controls.TextBox CorrectionSpokenTextBox;

	internal System.Windows.Controls.TextBox CorrectionWrittenTextBox;

	internal TextBlock LearnedCorrectionsSummaryTextBlock;

	internal System.Windows.Controls.ListBox LearnedCorrectionsListBox;

	internal Grid SettingsPage;

	internal ScrollViewer SettingsScrollViewer;

	internal System.Windows.Controls.ComboBox LocaleComboBox;

	internal System.Windows.Controls.ComboBox EngineComboBox;

	internal System.Windows.Controls.ComboBox TranscriptionModelComboBox;

	internal System.Windows.Controls.ComboBox CloudSttProviderComboBox;

	internal TextBlock CloudSttStatusTextBlock;

	internal System.Windows.Controls.ComboBox CloudSttModelComboBox;

	internal System.Windows.Controls.TextBox CloudSttEndpointTextBox;

	internal System.Windows.Controls.TextBox CloudSttApiKeyEnvTextBox;

	internal System.Windows.Controls.ComboBox WhisperDeviceComboBox;

	internal System.Windows.Controls.ComboBox ModelKeepAliveComboBox;

	internal System.Windows.Controls.ComboBox AudioInputComboBox;

	internal System.Windows.Controls.ComboBox OutputDestinationComboBox;

	internal System.Windows.Controls.ComboBox LlmPolishProviderComboBox;

	internal TextBlock LlmPolishStatusTextBlock;

	internal System.Windows.Controls.ComboBox LlmPolishModelComboBox;

	internal System.Windows.Controls.TextBox LlmPolishEndpointTextBox;

	internal System.Windows.Controls.TextBox LlmPolishApiKeyEnvTextBox;

	internal System.Windows.Controls.ComboBox LlmPolishTimeoutComboBox;

	internal System.Windows.Controls.ComboBox RecordingRetentionComboBox;

	internal System.Windows.Controls.ComboBox ThemeComboBox;

	internal System.Windows.Controls.CheckBox KeepHistoryCheckBox;

	internal System.Windows.Controls.CheckBox ShowCompletionToastCheckBox;

	internal System.Windows.Controls.CheckBox AutoLearnCorrectionsCheckBox;

	internal System.Windows.Controls.Button ShortcutCaptureButton;

	internal TextBlock ShortcutStatusTextBlock;

	internal System.Windows.Controls.CheckBox ShowWidgetCheckBox;

	internal System.Windows.Controls.CheckBox MinimizeToTrayCheckBox;

	internal System.Windows.Controls.CheckBox StartWithWindowsCheckBox;

	internal TextBlock WhisperModelPathTextBlock;

	internal TextBlock WhisperWrapperPathTextBlock;

	internal TextBlock WhisperRuntimeStatusTextBlock;

	internal TextBlock LlmPolishRuntimeTextBlock;

	internal System.Windows.Controls.Button StopLoadedModelButton;

	private bool _contentLoaded;

	private TimeSpan RecordingElapsed
	{
		get
		{
			DateTimeOffset? recordingStartedAt = _recordingStartedAt;
			if (recordingStartedAt.HasValue)
			{
				DateTimeOffset valueOrDefault = recordingStartedAt.GetValueOrDefault();
				if (_isRecording)
				{
					return DateTimeOffset.Now - valueOrDefault;
				}
			}
			return TimeSpan.Zero;
		}
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnhookWindowsHookEx(nint hhk);

	[DllImport("user32.dll")]
	private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int vKey);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool RegisterHotKey(nint hWnd, int id, int fsModifiers, int vk);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnregisterHotKey(nint hWnd, int id);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetForegroundWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool ShowWindowAsync(nint hWnd, int nCmdShow);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool BringWindowToTop(nint hWnd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

	[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	private static extern nint GetModuleHandle(string? lpModuleName);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	public MainWindow()
	{
		InitializeComponent();
		_settings = _store.LoadSettings();
		_transcriptionModels = TranscriptionModelOption.LoadAvailableLocalModels();
		_settings = NormalizeSettings(_settings, _transcriptionModels);
		_store.SaveSettings(_settings);
		_shortcutGesture = ShortcutGesture.Parse(_settings.DictationShortcut);
		_vocabulary = new ObservableCollection<VocabularyEntry>(_store.LoadVocabulary());
		_history = new ObservableCollection<TranscriptCard>(from card in _store.LoadHistory()
			orderby card.CreatedAt descending
			select card);
		_historyView = CollectionViewSource.GetDefaultView(_history);
		_historyView.Filter = HistoryFilter;
		NormalizeVocabularyIds();
		StorePathTextBlock.Text = _store.Root;
		VocabularyGrid.ItemsSource = _vocabulary;
		LearnedCorrectionsListBox.ItemsSource = _learnedCorrections;
		HistoryListBox.ItemsSource = _historyView;
		InstallDictionaryDeletionActions();
		_recordingTimer.Tick += delegate
		{
			UpdateRecordingElapsedUi();
		};
		_historySearchRefreshTimer.Tick += delegate
		{
			_historySearchRefreshTimer.Stop();
			RefreshHistorySearchNow();
		};
		InitializeSettings();
		InstallAudioWorkspacePage();
		RenderModes();
		SelectMode(DictationMode.Presets.First());
		SetActiveTab("dictate");
		ApplyTheme();
		ApplyPremiumRuntimePolish();
		UpdateTranscriptionStatus();
		SetRecordButtonState(isRecording: false);
		AppLog.Info("Speak window initialized.");
		TryShowOnboarding();
	}

	private void TryShowOnboarding()
	{
		try
		{
			string flagPath = System.IO.Path.Combine(SpeakDataPaths.ResolveDataRoot(), ".onboarded");
			if (!System.IO.File.Exists(flagPath))
			{
				base.Dispatcher.BeginInvoke((Action)(() =>
				{
					var onboarding = new FirstRunWindow();
					onboarding.ShowDialog();
					try
					{
						System.IO.File.WriteAllText(flagPath, DateTimeOffset.Now.ToString("O"));
					}
					catch { }
				}), DispatcherPriority.Background);
			}
		}
		catch { }
	}

	protected override void OnStateChanged(EventArgs e)
	{
		base.OnStateChanged(e);
		if (base.WindowState == WindowState.Minimized && _settings.MinimizeToTray)
		{
			Hide();
			StatusTextBlock.Text = "Speak is running in the tray";
		}
		UpdateShortcutWidgetState();
	}

	protected override void OnActivated(EventArgs e)
	{
		base.OnActivated(e);
		UpdateShortcutWidgetState();
	}

	protected override void OnDeactivated(EventArgs e)
	{
		base.OnDeactivated(e);
		UpdateShortcutWidgetState();
	}

	protected override void OnClosed(EventArgs e)
	{
		UnregisterNativeHotkey();
		UninstallKeyboardShortcutHook();
		_hotkeySource?.RemoveHook(WindowMessageHook);
		_hotkeySource = null;
		_trayIcon?.Dispose();
		_trayMenu?.Dispose();
		_trayIcon = null;
		_trayMenu = null;
		_shortcutWidget?.Close();
		_shortcutWidget = null;
		_learningToast?.Close();
		_learningToast = null;
		_externalEditLearningCts?.Cancel();
		_externalEditLearningCts?.Dispose();
		_externalEditLearningCts = null;
		_ttsGenerationCts?.Cancel();
		_ttsGenerationCts?.Dispose();
		_ttsGenerationCts = null;
		_ttsWarmCts?.Cancel();
		_ttsWarmCts?.Dispose();
		_ttsWarmCts = null;
		_ttsSynthesizer.StopWarmEngineAsync().GetAwaiter().GetResult();
		_janitor?.Dispose();
		_janitor = null;
		_restApiServer.Value?.Stop();
		_processJob?.Dispose();
		_processJob = null;
		_historySearchRefreshTimer.Stop();
		FlushLocalDataBeforeClose();
		_recordingTimer.Stop();
		DisposeRecording();
		StopWhisperServerForShutdown();
		_historySaveGate.Dispose();
		_vocabularySaveGate.Dispose();
		_whisperClient.Dispose();
		_cloudSpeechTranscriber.Dispose();
		_llmPolisher.Dispose();
		_llmModelDiscovery.Dispose();
		base.OnClosed(e);
	}

	private void FlushLocalDataBeforeClose()
	{
		try
		{
			_store.SaveHistory(_history);
			_store.SaveVocabulary(_vocabulary);
		}
		catch (Exception exception)
		{
			AppLog.Warn("Final local data flush failed.", exception);
		}
	}

	private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e)
	{
		base.WindowState = WindowState.Minimized;
	}

	private void MaximizeWindowButton_Click(object sender, RoutedEventArgs e)
	{
		base.WindowState = ((base.WindowState != WindowState.Maximized) ? WindowState.Maximized : WindowState.Normal);
	}

	private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
		ApplyWorkAreaWindowBounds();
		ApplyWindowChromeTheme();
		UpdateModeButtons();
		UpdateTabButton(DictateTabButton, "dictate");
		UpdateTabButton(HistoryTabButton, "history");
		UpdateTabButton(ProfileTabButton, "profile");
		UpdateTabButton(DictionaryTabButton, "dictionary");
		UpdateTabButton(_audioTabButton, "audio");
		UpdateTabButton(SettingsTabButton, "settings");
		UpdateTranscriptionStatus();
		UpdateLibraryStats();
		UpdateHistorySelectionDetails();
		AnimatePageIn(DictatePage);
		ConfigureShortcutHandling();
		EnsureShortcutWidget();
		UpdateShortcutUi();
		UpdateShortcutWidgetState();
		EnsureTrayIcon();
		ApplyStartupRegistration();
		ArchiveOldRecordings();
		ScheduleProviderRefresh();
		ApplyPremiumRuntimePolish();
		InitializeBackgroundInfrastructure();
		ScaleTransform scaleTransform = EnsureScaleTransform(RecordButton);
		scaleTransform.ScaleX = 0.94;
		scaleTransform.ScaleY = 0.94;
		AnimateScale(RecordButton, 1.0, 320);
		base.Dispatcher.BeginInvoke((Action)ApplyHeaderEditorialVisual, DispatcherPriority.ContextIdle);
	}

	private void Window_StateChanged(object sender, EventArgs e)
	{
		ApplyWorkAreaWindowBounds();
	}

	private void ApplyWorkAreaWindowBounds()
	{
		if (!base.IsLoaded)
		{
			return;
		}
		nint handle = new WindowInteropHelper(this).Handle;
		if (handle != IntPtr.Zero)
		{
			System.Drawing.Rectangle workingArea = Screen.FromHandle(handle).WorkingArea;
			Matrix matrix = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
			System.Windows.Point point = matrix.Transform(new System.Windows.Point(workingArea.Left, workingArea.Top));
			System.Windows.Point point2 = matrix.Transform(new System.Windows.Point(workingArea.Width, workingArea.Height));
			base.MaxWidth = point2.X;
			base.MaxHeight = point2.Y;
			if (base.WindowState == WindowState.Maximized)
			{
				base.Left = point.X;
				base.Top = point.Y;
				base.Width = point2.X;
				base.Height = point2.Y;
			}
		}
	}

	private void InitializeBackgroundInfrastructure()
	{
		try
		{
			_processJob = new ProcessJob();

			_janitor = new BackgroundJanitor(SpeakDataPaths.ResolveDataRoot(), _settings.RecordingRetentionDays);
			_janitor.Start();

			RegisterRestApiRoutes();
			if (_restApiServer.Value != null && !_restApiServer.Value.IsRunning)
			{
				_restApiServer.Value.Start();
			}
		}
		catch (Exception ex)
		{
			AppLog.Warn("Background infrastructure initialization failed.", ex);
		}
	}

	private void RegisterRestApiRoutes()
	{
		RestApiServer api = _restApiServer.Value;

		api.RegisterRoute("GET", "/health", BuildHealthReport);
		api.RegisterRoute("GET", "/status", BuildHealthReport);

		api.RegisterRoute("GET", "/history", request =>
		{
			int skip = request.Query.GetValueOrDefault("skip") is string s && int.TryParse(s, out int si) ? si : 0;
			int take = request.Query.GetValueOrDefault("take") is string t && int.TryParse(t, out int ti) ? ti : 50;
			take = Math.Clamp(take, 1, 200);
			return UiGet(() =>
			{
				var items = _history.Skip(skip).Take(take).Select(c => new
				{
					id = c.Id,
					createdAt = c.CreatedAt,
					rawText = c.RawText,
					formattedText = c.FormattedText,
					modeId = c.ModeId,
					sourceLabel = c.SourceLabel,
					preview = c.Preview,
					audioAvailable = !string.IsNullOrWhiteSpace(c.AudioPath),
					engineId = c.EngineId,
					transcriptionModel = c.TranscriptionModelId
				}).ToList();
				return new { count = _history.Count, items };
			});
		});

		api.RegisterRoute("GET", "/history/{id}", request =>
		{
			if (!Guid.TryParse(request.PathParams.GetValueOrDefault("id"), out Guid guid))
				return new { error = "Invalid id format", statusCode = 400 };
			return UiGet(() =>
			{
				TranscriptCard? card = _history.FirstOrDefault(c => c.Id == guid);
				if (card == null)
					return (object)new { error = "Not found", statusCode = 404 };
				return (object)new
				{
					id = card.Id,
					createdAt = card.CreatedAt,
					rawText = card.RawText,
					formattedText = card.FormattedText,
					modeId = card.ModeId,
					engineId = card.EngineId,
					sourceLabel = card.SourceLabel,
					preview = card.Preview,
					audioAvailable = !string.IsNullOrWhiteSpace(card.AudioPath),
					transcriptionModel = card.TranscriptionModelId
				};
			});
		});

		api.RegisterRoute("POST", "/dictate/start", request =>
		{
			return UiGet(() =>
			{
				if (_isTranscribing)
					return (object)new { error = "Currently transcribing", statusCode = 409 };
				if (_isRecording)
					return (object)new { error = "Already recording", statusCode = 409 };
				StartRecording();
				return (object)new { status = "recording" };
			});
		});

		api.RegisterRoute("POST", "/dictate/stop", request =>
		{
			return UiGet(() =>
			{
				if (!_isRecording && !_isTranscribing)
					return (object)new { error = "Not recording", statusCode = 409 };
				if (_isTranscribing)
					return (object)new { error = "Already transcribing", statusCode = 409 };
				StopRecordingAndTranscribeAsync().GetAwaiter().GetResult();
				return (object)new { status = "transcribed" };
			});
		});

		api.RegisterRoute("POST", "/dictate/toggle", request =>
		{
			return UiGet(() =>
			{
				ToggleRecordingFromShortcutAsync().GetAwaiter().GetResult();
				return (object)new { status = _isRecording ? "recording" : _isTranscribing ? "transcribing" : "idle" };
			});
		});

		api.RegisterRoute("GET", "/modes", request =>
		{
			return UiGet(() =>
			{
				var modes = DictationMode.Presets.Select(m => new
				{
					id = m.Id,
					name = m.Name,
					instruction = m.Instruction,
					isSelected = string.Equals(m.Id, _selectedMode.Id, StringComparison.OrdinalIgnoreCase)
				}).ToList();
				return (object)new { selectedId = _selectedMode.Id, modes };
			});
		});

		api.RegisterRoute("POST", "/modes/switch", request =>
		{
			string? modeId = null;
			try
			{
				var parsed = JsonSerializer.Deserialize<JsonElement>(request.Body);
				if (parsed.TryGetProperty("id", out JsonElement idEl))
					modeId = idEl.GetString();
			}
			catch { }

			if (string.IsNullOrWhiteSpace(modeId))
				return new { error = "Missing 'id' in body", statusCode = 400 };

			DictationMode? mode = DictationMode.Presets.FirstOrDefault(m =>
				string.Equals(m.Id, modeId, StringComparison.OrdinalIgnoreCase));
			if (mode == null)
				return new { error = $"Mode '{modeId}' not found", statusCode = 404 };

			Dispatcher.Invoke(() => SelectMode(mode));
			return new { status = "switched", id = mode.Id, name = mode.Name };
		});

		api.RegisterRoute("POST", "/paste", request =>
		{
			string text = "";
			try
			{
				var parsed = JsonSerializer.Deserialize<JsonElement>(request.Body);
				if (parsed.TryGetProperty("text", out JsonElement textEl))
					text = textEl.GetString() ?? "";
			}
			catch { }

			return UiGet(() =>
			{
				string textToPaste = string.IsNullOrWhiteSpace(text)
					? RawTranscriptTextBox.Text.Trim()
					: text;
				if (string.IsNullOrWhiteSpace(textToPaste))
					return (object)new { error = "No text to paste", statusCode = 400 };
				DeliverTranscriptionOutputAsync(textToPaste).GetAwaiter().GetResult();
				return (object)new { status = "pasted", text = textToPaste };
			});
		});

		api.RegisterRoute("GET", "/settings", _ =>
		{
			return new
			{
				localeId = _settings.LocaleId,
				engineId = _settings.EngineId,
				transcriptionModelId = _settings.TranscriptionModelId,
				whisperDeviceId = _settings.WhisperDeviceId,
				audioInputDeviceNumber = _settings.AudioInputDeviceNumber,
				themeId = _settings.ThemeId,
				keepHistory = _settings.KeepHistory,
				dictationShortcut = _settings.DictationShortcut,
				showShortcutWidget = _settings.ShowShortcutWidget,
				minimizeToTray = _settings.MinimizeToTray,
				startWithWindows = _settings.StartWithWindows,
				outputDestinationId = _settings.OutputDestinationId,
				llmPolishProviderId = _settings.LlmPolishProviderId,
				llmPolishModel = _settings.LlmPolishModel,
				recordingRetentionDays = _settings.RecordingRetentionDays,
				ttsEngineId = _settings.TtsEngineId,
				ttsVoiceId = _settings.TtsVoiceId
			};
		});

		api.RegisterRoute("GET", "/clipboard", request =>
		{
			return UiGet(() =>
			{
				var items = _clipboardHistory.Entries.Select((s, i) => new { index = i, text = s }).ToList();
				return (object)new { count = items.Count, items };
			});
		});

		AppLog.Info("REST API routes registered");
	}

	private object UiGet(Func<object> action)
	{
		if (Dispatcher.CheckAccess())
			return action();
		return Dispatcher.Invoke(action);
	}

	private object BuildHealthReport(HttpRequest _)
	{
		return UiGet(() =>
		{
			var proc = _whisperServerProcess;
			bool whisperRunning = proc != null && !proc.HasExited;
			return new HealthReport
			{
				Status = "ok",
				Version = typeof(MainWindow).Assembly.GetName()?.Version?.ToString() ?? "",
				Uptime = default,
				Timespan = "",
				WhisperServerRunning = whisperRunning,
				WhisperModelLoaded = whisperRunning,
				TtsWorkerRunning = false,
				AudioInputDevice = "",
				SelectedModel = _settings.TranscriptionModelId,
				StorageUsedMb = "",
				HistoryCount = _history.Count,
				VocabularyCount = _vocabulary.Count
			};
		});
	}

	private void EnsureShortcutWidget()
	{
		if (_shortcutWidget == null)
		{
			_shortcutWidget = new ShortcutWidgetWindow();
			_shortcutWidget.SetTransitionDurations(320, 210);
			_shortcutWidget.ToggleRecordingRequested += async delegate
			{
				await ToggleRecordingFromShortcutAsync();
			};
			_shortcutWidget.OpenMainWindowRequested += delegate
			{
				ShowMainWindow("dictate");
			};
			_shortcutWidget.OpenSettingsRequested += delegate
			{
				ShowMainWindow("settings");
			};
			_shortcutWidget.CycleModeRequested += delegate
			{
				SelectNextMode();
			};
		}
		_shortcutWidget.SetState(_selectedMode.Name, _shortcutGesture.ToDisplayString(), _isRecording, _isTranscribing, RecordingElapsed);
		if (!_isRecording)
		{
			_shortcutWidget.SetMicActivity(0.0);
		}
		if (ShouldShowShortcutWidget())
		{
			_shortcutWidget.PlaceNearTaskbar();
			if (!_shortcutWidget.IsVisible)
			{
				_shortcutWidget.Show();
			}
		}
		else if (_shortcutWidget.IsVisible)
		{
			_shortcutWidget.Hide();
		}
	}

	private void UpdateShortcutWidgetState()
	{
		if (_shortcutWidget != null)
		{
			_shortcutWidget.SetState(_selectedMode.Name, _shortcutGesture.ToDisplayString(), _isRecording, _isTranscribing, RecordingElapsed);
			if (!_isRecording)
			{
				_shortcutWidget.SetMicActivity(0.0);
			}
			if (ShouldShowShortcutWidget() && !_shortcutWidget.IsVisible)
			{
				_shortcutWidget.Show();
			}
			else if (!ShouldShowShortcutWidget() && _shortcutWidget.IsVisible)
			{
				_shortcutWidget.Hide();
			}
		}
	}

	private bool ShouldShowShortcutWidget()
	{
		if (!_settings.ShowShortcutWidget)
		{
			return false;
		}
		if (base.IsVisible && base.IsActive)
		{
			return base.WindowState == WindowState.Minimized;
		}
		return true;
	}

	private void UpdateRecordingElapsedUi()
	{
		if (_isRecording)
		{
			RecordingStatusTextBlock.Text = "Listening " + FormatElapsed(RecordingElapsed);
			UpdateShortcutWidgetState();
		}
	}

	private static string FormatElapsed(TimeSpan elapsed)
	{
		elapsed = ((elapsed < TimeSpan.Zero) ? TimeSpan.Zero : elapsed);
		if (elapsed.TotalHours >= 1.0)
		{
			return $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
		}
		return $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
	}

	private void ShowMainWindow(string tab)
	{
		Show();
		if (base.WindowState == WindowState.Minimized)
		{
			base.WindowState = WindowState.Normal;
		}
		Activate();
		SetActiveTab(tab);
		UpdateShortcutWidgetState();
	}

	private void EnsureTrayIcon()
	{
		if (_trayIcon == null)
		{
			_trayIcon = new NotifyIcon
			{
				Text = "Speak",
				Icon = LoadTrayIcon(),
				Visible = true
			};
			_trayIcon.DoubleClick += delegate
			{
				RunOnUi(delegate
				{
					ShowMainWindow("dictate");
				});
			};
		}
		RefreshTrayMenu();
	}

	private static Icon LoadTrayIcon()
	{
		string text = System.IO.Path.Combine(AppContext.BaseDirectory, "Speak.ico");
		if (File.Exists(text))
		{
			return new Icon(text);
		}
		string text2 = ResolveExecutablePath();
		if (File.Exists(text2))
		{
			return System.Drawing.Icon.ExtractAssociatedIcon(text2) ?? SystemIcons.Application;
		}
		return SystemIcons.Application;
	}

	private void RefreshTrayMenu()
	{
		if (_trayIcon == null)
		{
			return;
		}
		_trayMenu?.Dispose();
		_trayMenu = new ContextMenuStrip();
		_trayMenu.Items.Add("Open Speak", null, delegate
		{
			RunOnUi(delegate
			{
				ShowMainWindow("dictate");
			});
		});
		_trayMenu.Items.Add(_isRecording ? "Stop recording" : "Start recording", null, delegate
		{
			ToggleRecordingFromTray();
		});
		ToolStripMenuItem toolStripMenuItem = new ToolStripMenuItem("Quick mode");
		foreach (DictationMode mode in DictationMode.Presets)
		{
			ToolStripMenuItem toolStripMenuItem2 = new ToolStripMenuItem(mode.Name)
			{
				Checked = mode.Id.Equals(_selectedMode.Id, StringComparison.OrdinalIgnoreCase)
			};
			toolStripMenuItem2.Click += delegate
			{
				RunOnUi(delegate
				{
					SelectMode(mode);
				});
			};
			toolStripMenuItem.DropDownItems.Add(toolStripMenuItem2);
		}
		_trayMenu.Items.Add(toolStripMenuItem);
		ToolStripMenuItem clipboardMenu = new ToolStripMenuItem("Clipboard history");
		int count = 0;
		foreach (string entry in _clipboardHistory.Entries)
		{
			if (count >= 5)
				break;
			string preview = entry.Length > 50 ? entry.Substring(0, 50) + "..." : entry;
			ToolStripMenuItem item = new ToolStripMenuItem(preview);
			string captured = entry;
			item.Click += delegate
			{
				RunOnUi(delegate
				{
					TrySetClipboardText(captured);
					StatusTextBlock.Text = "Copied to clipboard from history";
				});
			};
			clipboardMenu.DropDownItems.Add(item);
			count++;
		}
		if (count == 0)
		{
			clipboardMenu.Enabled = false;
			clipboardMenu.Text = "Clipboard history (empty)";
		}
		_trayMenu.Items.Add(clipboardMenu);
		_trayMenu.Items.Add("Settings", null, delegate
		{
			RunOnUi(delegate
			{
				ShowMainWindow("settings");
			});
		});
		_trayMenu.Items.Add(new ToolStripSeparator());
		_trayMenu.Items.Add("Quit Speak", null, delegate
		{
			RunOnUi(base.Close);
		});
		_trayIcon.ContextMenuStrip = _trayMenu;
	}

	private void ToggleRecordingFromTray()
	{
		base.Dispatcher.BeginInvoke((Action)async delegate
		{
			await ToggleRecordingFromShortcutAsync();
		});
	}

	private void RunOnUi(Action action)
	{
		if (base.Dispatcher.CheckAccess())
		{
			action();
		}
		else
		{
			base.Dispatcher.BeginInvoke(action);
		}
	}

	private void ApplyStartupRegistration()
	{
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true) ?? Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
			if (registryKey != null)
			{
				if (_settings.StartWithWindows)
				{
					registryKey.SetValue("Speak", "\"" + ResolveExecutablePath() + "\"");
				}
				else
				{
					registryKey.DeleteValue("Speak", throwOnMissingValue: false);
				}
			}
		}
		catch (Exception exception)
		{
			AppLog.Warn("Could not update Windows startup registration.", exception);
		}
	}

	private static string ResolveExecutablePath()
	{
		if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
		{
			return Environment.ProcessPath;
		}
		try
		{
			return Process.GetCurrentProcess().MainModule?.FileName ?? "";
		}
		catch (Exception exception)
		{
			AppLog.Warn("Could not resolve executable path.", exception);
			return "";
		}
	}

	private void SelectNextMode()
	{
		IReadOnlyList<DictationMode> presets = DictationMode.Presets;
		int num = presets.Select((DictationMode mode, int index) => new { mode, index }).FirstOrDefault(item => item.mode.Id.Equals(_selectedMode.Id, StringComparison.OrdinalIgnoreCase))?.index ?? 0;
		SelectMode(presets[(num + 1) % presets.Count]);
	}

	private void InitializeSettings()
	{
		_isLoading = true;
		LocaleComboBox.ItemsSource = RecognitionLocaleOption.Presets;
		LocaleComboBox.DisplayMemberPath = "Name";
		LocaleComboBox.SelectedValuePath = "Id";
		LocaleComboBox.SelectedValue = _settings.LocaleId;
		EngineComboBox.ItemsSource = EngineProfile.Presets;
		EngineComboBox.DisplayMemberPath = "Name";
		EngineComboBox.SelectedValuePath = "Id";
		EngineComboBox.SelectedValue = _settings.EngineId;
		TranscriptionModelComboBox.ItemsSource = _transcriptionModels;
		TranscriptionModelComboBox.DisplayMemberPath = "Name";
		TranscriptionModelComboBox.SelectedValuePath = "Id";
		TranscriptionModelComboBox.SelectedValue = _settings.TranscriptionModelId;
		CloudSttProviderComboBox.ItemsSource = CloudSttProviderOption.Presets;
		CloudSttProviderComboBox.DisplayMemberPath = "Name";
		CloudSttProviderComboBox.SelectedValuePath = "Id";
		CloudSttProviderComboBox.SelectedValue = _settings.SttCloudProviderId;
		CloudSttModelComboBox.ItemsSource = _cloudSttModels;
		SeedCloudSttModels(_settings.SttCloudModel);
		CloudSttModelComboBox.Text = _settings.SttCloudModel;
		CloudSttEndpointTextBox.Text = _settings.SttCloudEndpoint;
		CloudSttApiKeyEnvTextBox.Text = _settings.SttCloudApiKeyEnvironmentVariable;
		WhisperDeviceComboBox.ItemsSource = WhisperDeviceOption.Presets;
		WhisperDeviceComboBox.DisplayMemberPath = "Name";
		WhisperDeviceComboBox.SelectedValuePath = "Id";
		WhisperDeviceComboBox.SelectedValue = _settings.WhisperDeviceId;
		ModelKeepAliveComboBox.ItemsSource = ModelKeepAliveOption.Presets;
		ModelKeepAliveComboBox.DisplayMemberPath = "Name";
		ModelKeepAliveComboBox.SelectedValuePath = "Minutes";
		ModelKeepAliveComboBox.SelectedValue = _settings.ModelKeepAliveMinutes;
		LoadAudioDevices();
		AudioInputComboBox.ItemsSource = _audioDevices;
		AudioInputComboBox.DisplayMemberPath = "DisplayName";
		AudioInputComboBox.SelectedValuePath = "DeviceNumber";
		AudioInputComboBox.SelectedValue = _settings.AudioInputDeviceNumber;
		OutputDestinationComboBox.ItemsSource = OutputDestinationOption.Presets;
		OutputDestinationComboBox.DisplayMemberPath = "Name";
		OutputDestinationComboBox.SelectedValuePath = "Id";
		OutputDestinationComboBox.SelectedValue = _settings.OutputDestinationId;
		LlmPolishProviderComboBox.ItemsSource = LlmPolishProviderOption.Presets;
		LlmPolishProviderComboBox.DisplayMemberPath = "Name";
		LlmPolishProviderComboBox.SelectedValuePath = "Id";
		LlmPolishProviderComboBox.SelectedValue = _settings.LlmPolishProviderId;
		LlmPolishModelComboBox.ItemsSource = _llmPolishModels;
		SeedLlmPolishModels(_settings.LlmPolishModel);
		LlmPolishModelComboBox.Text = _settings.LlmPolishModel;
		LlmPolishEndpointTextBox.Text = _settings.LlmPolishEndpoint;
		LlmPolishApiKeyEnvTextBox.Text = _settings.LlmPolishApiKeyEnvironmentVariable;
		LlmPolishTimeoutComboBox.ItemsSource = LlmPolishTimeoutOption.Presets;
		LlmPolishTimeoutComboBox.DisplayMemberPath = "Name";
		LlmPolishTimeoutComboBox.SelectedValuePath = "Seconds";
		LlmPolishTimeoutComboBox.SelectedValue = _settings.LlmPolishTimeoutSeconds;
		RecordingRetentionComboBox.ItemsSource = RecordingRetentionOption.Presets;
		RecordingRetentionComboBox.DisplayMemberPath = "Name";
		RecordingRetentionComboBox.SelectedValuePath = "Days";
		RecordingRetentionComboBox.SelectedValue = _settings.RecordingRetentionDays;
		ThemeComboBox.ItemsSource = AppearanceTheme.Presets;
		ThemeComboBox.DisplayMemberPath = "Name";
		ThemeComboBox.SelectedValuePath = "Id";
		ThemeComboBox.SelectedValue = _settings.ThemeId;
		KeepHistoryCheckBox.IsChecked = _settings.KeepHistory;
		ShowCompletionToastCheckBox.IsChecked = _settings.ShowCompletionToast;
		SyncAutoLearnCorrectionControls(_settings.AutoLearnCorrections);
		ShowWidgetCheckBox.IsChecked = _settings.ShowShortcutWidget;
		MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTray;
		StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
		UpdateShortcutUi();
		_isLoading = false;
	}

	private void LoadAudioDevices()
	{
		_audioDevices.Clear();
		try
		{
			for (int i = 0; i < WaveIn.DeviceCount; i++)
			{
				WaveInCapabilities capabilities = WaveIn.GetCapabilities(i);
				_audioDevices.Add(new AudioInputDeviceOption
				{
					DeviceNumber = i,
					Name = capabilities.ProductName
				});
			}
		}
		catch (Exception exception)
		{
			AppLog.Warn("Audio device scan failed.", exception);
			_audioDevices.Clear();
		}
		if (_audioDevices.Count == 0)
		{
			_audioDevices.Add(new AudioInputDeviceOption
			{
				DeviceNumber = -1,
				Name = "No microphone found"
			});
		}
		if (_audioDevices.All((AudioInputDeviceOption device) => device.DeviceNumber != _settings.AudioInputDeviceNumber))
		{
			_settings.AudioInputDeviceNumber = _audioDevices.First().DeviceNumber;
		}
	}

	private void RenderModes()
	{
		ModesPanel.Children.Clear();
		foreach (DictationMode preset in DictationMode.Presets)
		{
			System.Windows.Controls.Button button = new System.Windows.Controls.Button
			{
				Tag = preset,
				Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
				Padding = new Thickness(12.0, 7.0, 12.0, 7.0),
				HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
				VerticalContentAlignment = VerticalAlignment.Center,
				Style = (Style)FindResource("RoundedButton"),
				MinHeight = 46.0,
				MaxHeight = 48.0,
				Content = CreateModeButtonContent(preset)
			};
			button.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
			button.RenderTransform = new ScaleTransform(1.0, 1.0);
			button.Click += ModeButton_Click;
			button.MouseEnter += ModeButton_MouseEnter;
			button.MouseLeave += ModeButton_MouseLeave;
			ModesPanel.Children.Add(button);
		}
		UpdateModeButtons();
		ApplyModeCardFinishing();
	}

	private UIElement CreateModeButtonContent(DictationMode mode)
	{
		Grid obj = new Grid
		{
			MinHeight = 28.0,
			ColumnDefinitions = 
			{
				new ColumnDefinition
				{
					Width = GridLength.Auto
				},
				new ColumnDefinition
				{
					Width = new GridLength(1.0, GridUnitType.Star)
				}
			},
			RowDefinitions = 
			{
				new RowDefinition
				{
					Height = GridLength.Auto
				},
				new RowDefinition
				{
					Height = GridLength.Auto
				}
			}
		};
		Border border = new Border
		{
			Width = 22.0,
			Height = 22.0,
			CornerRadius = new CornerRadius(6.0),
			Background = ResourceBrush("SoftBrush"),
			BorderBrush = ResourceBrush("LineBrush"),
			BorderThickness = new Thickness(1.0),
			Margin = new Thickness(0.0, 0.0, 9.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center,
			Effect = new DropShadowEffect { BlurRadius = 2.0, ShadowDepth = 0.0, Opacity = 0.04 }
		};
		border.Child = new TextBlock
		{
			Text = (string.IsNullOrWhiteSpace(mode.IconGlyph) ? "\ue8d4" : mode.IconGlyph),
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 12.0,
			Foreground = ResourceBrush("MutedBrush"),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetColumn(border, 0);
		obj.Children.Add(border);
		TextBlock element = new TextBlock
		{
			Text = mode.Name,
			FontWeight = FontWeights.SemiBold,
			FontSize = 12.5,
			TextWrapping = TextWrapping.NoWrap,
			TextTrimming = TextTrimming.CharacterEllipsis,
			LineHeight = 16.0,
			Foreground = ResourceBrush("InkBrush")
		};
		new Border
		{
			Background = ResourceBrush("SoftBrush"),
			BorderBrush = ResourceBrush("LineBrush"),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(7.0),
			Padding = new Thickness(7.0, 1.0, 7.0, 1.0),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0),
			Child = new TextBlock
			{
				Text = mode.Badge,
				FontSize = 9.0,
				FontWeight = FontWeights.Bold,
				Foreground = ResourceBrush("MutedBrush")
			}
		};
		StackPanel stackPanel = new StackPanel
		{
			VerticalAlignment = VerticalAlignment.Center
		};
		stackPanel.Children.Add(element);
		Grid.SetColumn(stackPanel, 1);
		obj.Children.Add(stackPanel);
		return obj;
	}

	private void ModeButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button { Tag: DictationMode tag })
		{
			SelectMode(tag);
		}
	}

	private void ModeButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (sender is System.Windows.Controls.Button button)
		{
			button.Background = ResourceBrush("ElevatedBrush");
			button.BorderBrush = ResourceBrush("GoldBrush");
			AnimateScale(button, 1.0, 135);
		}
	}

	private void InstallAudioWorkspacePage()
	{
		if (_audioPage != null)
		{
			return;
		}
		InstallAudioTabButton();
		System.Windows.Controls.Panel? host = SettingsPage.Parent as System.Windows.Controls.Panel;
		if (host == null)
		{
			AppLog.Warn("Could not install Audio page because SettingsPage parent is not a panel.");
			return;
		}
		_audioPage = new Grid
		{
			Visibility = Visibility.Collapsed,
			Margin = SettingsPage.Margin
		};
		Grid.SetRow(_audioPage, Grid.GetRow(SettingsPage));
		Grid.SetColumn(_audioPage, Grid.GetColumn(SettingsPage));
		Grid.SetRowSpan(_audioPage, Grid.GetRowSpan(SettingsPage));
		Grid.SetColumnSpan(_audioPage, Grid.GetColumnSpan(SettingsPage));
		ScrollViewer scrollViewer = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Padding = new Thickness(2.0, 0.0, 12.0, 4.0),
			PanningMode = PanningMode.VerticalOnly,
			CanContentScroll = false
		};
		scrollViewer.Content = CreateAudioWorkbench();
		_audioPage.Children.Add(scrollViewer);
		host.Children.Add(_audioPage);

		RefreshTtsVoiceChoices(preserveSelection: true);
		SyncAudioControlsFromSettings();
		UpdateAudioWorkspaceStatus();
	}

	private void InstallAudioTabButton()
	{
		if (_audioTabButton != null)
		{
			return;
		}
		if (SettingsTabButton.Parent is not System.Windows.Controls.Panel tabsHost)
		{
			return;
		}
		_audioTabButton = new System.Windows.Controls.Button
		{
			Content = CreateAudioSidebarLogoContent(isActive: false),
			Tag = "audio",
			Style = SettingsTabButton.Style,
			MinHeight = SettingsTabButton.MinHeight,
			MinWidth = SettingsTabButton.MinWidth,
			Padding = SettingsTabButton.Padding,
			Margin = SettingsTabButton.Margin,
			BorderThickness = SettingsTabButton.BorderThickness,
			HorizontalContentAlignment = SettingsTabButton.HorizontalContentAlignment,
			VerticalContentAlignment = SettingsTabButton.VerticalContentAlignment,
			ToolTip = "Audio Studio"
		};
		_audioTabButton.Click += TabButton_Click;
		int index = tabsHost.Children.IndexOf(SettingsTabButton);
		if (index >= 0)
		{
			tabsHost.Children.Insert(index, _audioTabButton);
			return;
		}
		tabsHost.Children.Add(_audioTabButton);
	}

	private UIElement CreateAudioWorkbench()
	{
		Grid workbench = new Grid
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
		};
		workbench.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		workbench.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		UIElement hero = CreateAudioHeroCard();
		Grid.SetRow(hero, 0);
		workbench.Children.Add(hero);
		Grid body = new Grid
		{
			Margin = new Thickness(0.0, 16.0, 0.0, 0.0)
		};
		body.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.65, GridUnitType.Star)
		});
		body.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(18.0)
		});
		body.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(0.95, GridUnitType.Star),
			MinWidth = 290.0
		});
		StackPanel productionLane = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical
		};
		productionLane.Children.Add(CreateAudioTranscribeCard());
		productionLane.Children.Add(CreateAudioSpeakCard());
		productionLane.Children.Add(CreateAudioVoiceLabPanel());
		Grid.SetColumn(productionLane, 0);
		body.Children.Add(productionLane);
		UIElement outputRail = CreateAudioOutputRail();
		Grid.SetColumn(outputRail, 2);
		body.Children.Add(outputRail);
		Grid.SetRow(body, 1);
		workbench.Children.Add(body);
		return workbench;
	}

	private UIElement CreateAudioHeroCard()
	{
		Border hero = new Border
		{
			CornerRadius = new CornerRadius(12.0),
			BorderBrush = ResourceBrush("LineBrush"),
			BorderThickness = new Thickness(1.0),
			Background = ResourceBrush("PanelBrush"),
			Padding = new Thickness(24.0, 20.0, 24.0, 20.0),
			Effect = new DropShadowEffect
			{
				BlurRadius = 8.0,
				ShadowDepth = 0.0,
				Opacity = 0.08
			}
		};
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		hero.Child = grid;
		StackPanel copy = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical
		};
		StackPanel titleRow = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		titleRow.Children.Add(CreateAudioLogoMark(42.0));
		titleRow.Children.Add(new TextBlock
		{
			Text = "🎙️ Audio Studio",
			FontSize = 26.0,
			FontWeight = FontWeights.SemiBold,
			Foreground = ResourceBrush("InkBrush"),
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(12.0, 0.0, 0.0, 0.0)
		});
		copy.Children.Add(titleRow);
		copy.Children.Add(new TextBlock
		{
			Text = "A GPU-first voice console for generating, previewing, and shaping local audio.",
			TextWrapping = TextWrapping.Wrap,
			Foreground = ResourceBrush("MutedBrush"),
			Margin = new Thickness(0.0, 5.0, 0.0, 0.0)
		});
		Grid.SetColumn(copy, 0);
		grid.Children.Add(copy);
		WrapPanel status = new WrapPanel
		{
			HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Center
		};
		status.Children.Add(CreateAudioPill("Local Whisper", _settings.TranscriptionModelId));
		status.Children.Add(CreateAudioPill("TTS", TtsEngineOption.Find(_settings.TtsEngineId).Name));
		status.Children.Add(CreateAudioPill("Data", "D: drive"));
		Grid.SetColumn(status, 1);
		grid.Children.Add(status);
		return hero;
	}

	private UIElement CreateAudioTranscribeCard()
	{
		StackPanel body = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical
		};
		_audioSttEngineComboBox = CreateAudioComboBox(EngineProfile.Presets, "Name", "Id", _settings.EngineId);
		_audioTranscriptionModelComboBox = CreateAudioComboBox(_transcriptionModels, "Name", "Id", _settings.TranscriptionModelId);
		_audioCloudSttProviderComboBox = CreateAudioComboBox(CloudSttProviderOption.Presets, "Name", "Id", _settings.SttCloudProviderId);
		_audioCloudSttModelComboBox = CreateAudioComboBox(_cloudSttModels, null, null, _settings.SttCloudModel);
		_audioCloudSttModelComboBox.IsEditable = true;
		_audioCloudSttModelComboBox.Text = _settings.SttCloudModel;
		_audioWhisperDeviceComboBox = CreateAudioComboBox(WhisperDeviceOption.Presets, "Name", "Id", _settings.WhisperDeviceId);
		_audioModelKeepAliveComboBox = CreateAudioComboBox(ModelKeepAliveOption.Presets, "Name", "Minutes", _settings.ModelKeepAliveMinutes);
		_audioInputComboBox = CreateAudioComboBox(_audioDevices, "DisplayName", "DeviceNumber", _settings.AudioInputDeviceNumber);
		foreach (System.Windows.Controls.ComboBox comboBox in new[]
		{
			_audioSttEngineComboBox,
			_audioTranscriptionModelComboBox,
			_audioCloudSttProviderComboBox,
			_audioCloudSttModelComboBox,
			_audioWhisperDeviceComboBox,
			_audioModelKeepAliveComboBox,
			_audioInputComboBox
		})
		{
			comboBox.SelectionChanged += AudioTranscribeControl_Changed;
		}
		_audioCloudSttModelComboBox.LostFocus += AudioTranscribeControl_Changed;
		Grid options = CreateAudioOptionGrid();
		AddAudioOption(options, "Engine", _audioSttEngineComboBox);
		AddAudioOption(options, "Model", _audioTranscriptionModelComboBox);
		AddAudioOption(options, "Cloud", _audioCloudSttProviderComboBox);
		AddAudioOption(options, "Cloud model", _audioCloudSttModelComboBox);
		AddAudioOption(options, "Device", _audioWhisperDeviceComboBox);
		AddAudioOption(options, "Keep hot", _audioModelKeepAliveComboBox);
		AddAudioOption(options, "Mic", _audioInputComboBox);
		body.Children.Add(options);
		WrapPanel actions = new WrapPanel
		{
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left
		};
		actions.Children.Add(CreateTtsButton("Record", AudioRecordNowButton_Click, isPrimary: true));
		actions.Children.Add(CreateTtsButton("Import Audio", OpenAudioButton_Click, isPrimary: false));
		actions.Children.Add(CreateTtsButton("Retry Selected", RetrySelectedHistoryButton_Click, isPrimary: false));
		actions.Children.Add(CreateTtsButton("Open Recordings", AudioOpenRecordingsButton_Click, isPrimary: false));
		body.Children.Add(actions);
		_audioTranscribeStatusTextBlock = CreateAudioStatusText();
		body.Children.Add(_audioTranscribeStatusTextBlock);
		return CreateAudioStudioPanel("01", "Transcribe", "Capture and convert audio without leaving the workspace.", body);
	}

	private UIElement CreateAudioSpeakCard()
	{
		StackPanel body = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical
		};
		_ttsEngineComboBox = new System.Windows.Controls.ComboBox
		{
			ItemsSource = _ttsEngines,
			DisplayMemberPath = "Name",
			SelectedValuePath = "Id",
			SelectedValue = _settings.TtsEngineId,
			MinHeight = 34.0
		};
		_ttsEngineComboBox.SelectionChanged += TtsControl_Changed;
		_ttsVoiceComboBox = new System.Windows.Controls.ComboBox
		{
			ItemsSource = _ttsVoices,
			DisplayMemberPath = "Name",
			SelectedValuePath = "Id",
			MinHeight = 34.0
		};
		_ttsVoiceComboBox.SelectionChanged += TtsControl_Changed;
		Grid voiceOptions = CreateAudioOptionGrid();
		AddAudioOption(voiceOptions, "Engine", _ttsEngineComboBox);
		AddAudioOption(voiceOptions, "Voice", _ttsVoiceComboBox);
		body.Children.Add(voiceOptions);
		_ttsSampleTextBox = new System.Windows.Controls.TextBox
		{
			Text = "Hamza, Speak local TTS is ready.",
			MinHeight = 88.0,
			TextWrapping = TextWrapping.Wrap,
			AcceptsReturn = true,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Background = ResourceBrush("ElevatedBrush"),
			Foreground = ResourceBrush("InkBrush"),
			BorderBrush = ResourceBrush("LineBrush"),
			Padding = new Thickness(10.0)
		};
		body.Children.Add(CreateAudioField("Script", _ttsSampleTextBox));
		WrapPanel actions = new WrapPanel
		{
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left
		};
		_ttsSpeakCurrentButton = CreateTtsButton("Generate from Transcript", TtsSpeakCurrentButton_Click, isPrimary: true);
		_ttsGenerateSampleButton = CreateTtsButton("Generate Sample", TtsGenerateSampleButton_Click, isPrimary: false);
		actions.Children.Add(_ttsSpeakCurrentButton);
		actions.Children.Add(_ttsGenerateSampleButton);
		body.Children.Add(actions);
		_ttsStatusTextBlock = CreateAudioStatusText();
		body.Children.Add(_ttsStatusTextBlock);
		WrapPanel warmActions = new WrapPanel
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left
		};
		_ttsWarmModelButton = CreateTtsButton("Warm Model", TtsWarmModelButton_Click, isPrimary: false);
		warmActions.Children.Add(_ttsWarmModelButton);
		body.Children.Add(warmActions);
		_ttsWarmStatusTextBlock = CreateAudioStatusText();
		body.Children.Add(_ttsWarmStatusTextBlock);
		return CreateAudioStudioPanel("02", "Speak", "Generate local voice audio and preview it in-app.", body);
	}

	private UIElement CreateAudioVoiceLabPanel()
	{
		StackPanel lab = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical
		};
		lab.Children.Add(CreateAudioCloneCard());
		return CreateAudioStudioPanel("03", "Voice Lab", "Prepare clone profiles with the installed Qwen3 Base model.", lab);
	}

	private UIElement CreateAudioCloneCard()
	{
		StackPanel body = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical
		};
		TextBlock modelStatus = CreateAudioStatusText();
		modelStatus.Text = _ttsSynthesizer.DescribeAvailability("qwen3-base-1.7b");
		body.Children.Add(modelStatus);
		_audioCloneReferenceTextBox = CreateAudioTextBox(_settings.VoiceCloneReferenceAudioPath, 34.0, acceptsReturn: false);
		_audioCloneReferenceTextBox.TextChanged += AudioCloneControl_Changed;
		body.Children.Add(CreateAudioField("Reference audio", _audioCloneReferenceTextBox));
		_audioCloneNameTextBox = CreateAudioTextBox(_settings.VoiceCloneProfileName, 34.0, acceptsReturn: false);
		_audioCloneNameTextBox.TextChanged += AudioCloneControl_Changed;
		body.Children.Add(CreateAudioField("Clone name", _audioCloneNameTextBox));
		_audioCloneEngineComboBox = new System.Windows.Controls.ComboBox
		{
			MinWidth = 200.0,
			DisplayMemberPath = "Name",
			SelectedValuePath = "Id"
		};
		_audioCloneEngineComboBox.Items.Add(new { Name = "Qwen3 1.7B Base", Id = "qwen3-base-1.7b" });
		_audioCloneEngineComboBox.Items.Add(new { Name = "Tortoise TTS", Id = "tortoise-ultra-fast" });
		_audioCloneEngineComboBox.SelectedValue = _settings.VoiceCloneEngineId;
		body.Children.Add(CreateAudioField("Clone engine", _audioCloneEngineComboBox));
		WrapPanel actions = new WrapPanel
		{
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left
		};
		actions.Children.Add(CreateTtsButton("Choose Audio", AudioChooseCloneReferenceButton_Click, isPrimary: false));
		actions.Children.Add(CreateTtsButton("Prepare Clone Profile", AudioPrepareCloneProfileButton_Click, isPrimary: true));
		actions.Children.Add(CreateTtsButton("Open Clone Folder", AudioOpenCloneFolderButton_Click, isPrimary: false));
		body.Children.Add(actions);
		_ttsCloneTextTextBox = CreateAudioTextBox("", 34.0, acceptsReturn: true);
		_ttsCloneTextTextBox.AcceptsReturn = true;
		_ttsCloneTextTextBox.TextWrapping = TextWrapping.Wrap;
		_ttsCloneTextTextBox.MinHeight = 60.0;
		body.Children.Add(CreateAudioField("Text to speak with cloned voice", _ttsCloneTextTextBox));
		WrapPanel cloneActions = new WrapPanel
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left
		};
		_ttsCloneGenerateButton = CreateTtsButton("Generate Cloned Voice", AudioGenerateCloneButton_Click, isPrimary: true);
		cloneActions.Children.Add(_ttsCloneGenerateButton);
		body.Children.Add(cloneActions);
		_audioCloneStatusTextBlock = CreateAudioStatusText();
		body.Children.Add(_audioCloneStatusTextBlock);
		return CreateAudioSubPanel("Clone", body);
	}

	private UIElement CreateAudioDesignCard()
	{
		StackPanel body = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical
		};
		TextBlock modelStatus = CreateAudioStatusText();
		modelStatus.Text = "VoiceDesign is not installed in D:\\Models.";
		body.Children.Add(modelStatus);
		_audioDesignPromptTextBox = CreateAudioTextBox(_settings.VoiceDesignPrompt, 92.0, acceptsReturn: true);
		_audioDesignPromptTextBox.TextChanged += AudioDesignControl_Changed;
		body.Children.Add(CreateAudioField("Design brief", _audioDesignPromptTextBox));
		WrapPanel actions = new WrapPanel
		{
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left
		};
		actions.Children.Add(CreateTtsButton("Save Design Brief", AudioSaveDesignBriefButton_Click, isPrimary: true));
		actions.Children.Add(CreateTtsButton("Open Design Folder", AudioOpenDesignFolderButton_Click, isPrimary: false));
		body.Children.Add(actions);
		_audioDesignStatusTextBlock = CreateAudioStatusText();
		body.Children.Add(_audioDesignStatusTextBlock);
		return CreateAudioSubPanel("Design", body);
	}

	private UIElement CreateAudioOutputRail()
	{
		Border rail = new Border
		{
			CornerRadius = new CornerRadius(8.0),
			BorderBrush = ResourceBrush("LineBrush"),
			BorderThickness = new Thickness(1.0),
			Background = ResourceBrush("PanelBrush"),
			Padding = new Thickness(16.0)
		};
		StackPanel stack = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical
		};
		rail.Child = stack;
		stack.Children.Add(CreateAudioStageLabel("Output"));
		stack.Children.Add(new TextBlock
		{
			Text = "Generated Audio",
			FontSize = 18.0,
			FontWeight = FontWeights.SemiBold,
			Foreground = ResourceBrush("InkBrush"),
			Margin = new Thickness(0.0, 4.0, 0.0, 4.0)
		});
		_ttsOutputTextBlock = CreateAudioStatusText();
		_ttsOutputTextBlock.Margin = new Thickness(0.0, 0.0, 0.0, 12.0);
		stack.Children.Add(_ttsOutputTextBlock);
		Border player = new Border
		{
			CornerRadius = new CornerRadius(8.0),
			BorderBrush = ResourceBrush("LineBrush"),
			BorderThickness = new Thickness(1.0),
			Background = ResourceBrush("ElevatedBrush"),
			Padding = new Thickness(14.0),
			Margin = new Thickness(0.0, 0.0, 0.0, 14.0)
		};
		StackPanel playerStack = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical
		};
		player.Child = playerStack;
		playerStack.Children.Add(new TextBlock
		{
			Text = "Preview",
			FontWeight = FontWeights.SemiBold,
			Foreground = ResourceBrush("InkBrush")
		});
		_audioPlaybackStatusTextBlock = CreateAudioStatusText();
		_audioPlaybackStatusTextBlock.Margin = new Thickness(0.0, 4.0, 0.0, 10.0);
		playerStack.Children.Add(_audioPlaybackStatusTextBlock);
		WrapPanel playbackActions = new WrapPanel
		{
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left
		};
		_ttsPlayLastButton = CreateTtsButton("Play Last", TtsPlayLastButton_Click, isPrimary: true);
		_ttsStopPlaybackButton = CreateTtsButton("Stop", TtsStopPlaybackButton_Click, isPrimary: false);
		_ttsOpenLastButton = CreateTtsButton("Open File", TtsOpenLastButton_Click, isPrimary: false);
		playbackActions.Children.Add(_ttsPlayLastButton);
		playbackActions.Children.Add(_ttsStopPlaybackButton);
		playbackActions.Children.Add(_ttsOpenLastButton);
		playerStack.Children.Add(playbackActions);
		stack.Children.Add(player);
		stack.Children.Add(CreateAudioRailButton("Open Output Folder", TtsOpenFolderButton_Click));
		stack.Children.Add(CreateAudioRailDivider());
		stack.Children.Add(CreateAudioStageLabel("Model stack"));
		stack.Children.Add(CreateAudioCapabilityRow("Transcribe", _settings.TranscriptionModelId, SelectedWhisperDeviceName()));
		stack.Children.Add(CreateAudioCapabilityRow("Speak", TtsEngineOption.Find(_settings.TtsEngineId).Name, SelectedTtsVoiceId()));
		stack.Children.Add(CreateAudioCapabilityRow("Clone", "Qwen3 Base 1.7B", File.Exists(_settings.VoiceCloneReferenceAudioPath) ? "reference ready" : "choose reference"));
		return rail;
	}

	private UIElement CreateAudioStudioPanel(string number, string title, string subtitle, UIElement body)
	{
		Border card = new Border
		{
			CornerRadius = new CornerRadius(8.0),
			BorderBrush = ResourceBrush("LineBrush"),
			BorderThickness = new Thickness(1.0),
			Background = ResourceBrush("PanelBrush"),
			Padding = new Thickness(18.0),
			Margin = new Thickness(0.0, 0.0, 0.0, 16.0)
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical
		};
		card.Child = stackPanel;
		Grid header = new Grid
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 12.0)
		};
		header.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		header.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		Border badge = new Border
		{
			CornerRadius = new CornerRadius(6.0),
			Background = ResourceBrush("ElevatedBrush"),
			BorderBrush = ResourceBrush("LineBrush"),
			BorderThickness = new Thickness(1.0),
			Padding = new Thickness(9.0, 5.0, 9.0, 5.0),
			Margin = new Thickness(0.0, 0.0, 12.0, 0.0),
			VerticalAlignment = VerticalAlignment.Top
		};
		badge.Child = new TextBlock
		{
			Text = number,
			FontWeight = FontWeights.SemiBold,
			Foreground = ResourceBrush("AccentBrush")
		};
		Grid.SetColumn(badge, 0);
		header.Children.Add(badge);
		StackPanel copy = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical
		};
		copy.Children.Add(new TextBlock
		{
			Text = title,
			FontSize = 17.0,
			FontWeight = FontWeights.SemiBold,
			Foreground = ResourceBrush("InkBrush")
		});
		copy.Children.Add(new TextBlock
		{
			Text = subtitle,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 3.0, 0.0, 0.0),
			Foreground = ResourceBrush("MutedBrush")
		});
		Grid.SetColumn(copy, 1);
		header.Children.Add(copy);
		stackPanel.Children.Add(header);
		stackPanel.Children.Add(body);
		return card;
	}

	private UIElement CreateAudioSubPanel(string title, UIElement body)
	{
		Border panel = new Border
		{
			CornerRadius = new CornerRadius(8.0),
			BorderBrush = ResourceBrush("LineBrush"),
			BorderThickness = new Thickness(1.0),
			Background = ResourceBrush("ElevatedBrush"),
			Padding = new Thickness(14.0)
		};
		StackPanel stack = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical
		};
		panel.Child = stack;
		stack.Children.Add(new TextBlock
		{
			Text = title,
			FontSize = 15.0,
			FontWeight = FontWeights.SemiBold,
			Foreground = ResourceBrush("InkBrush"),
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		});
		stack.Children.Add(body);
		return panel;
	}

	private UIElement CreateAudioLogoMark()
	{
		return CreateAudioLogoMark(34.0);
	}

	private UIElement CreateAudioLogoMark(double size)
	{
		Grid mark = new Grid
		{
			Width = size,
			Height = size,
			VerticalAlignment = VerticalAlignment.Center
		};
		double scale = size / 34.0;
		mark.Children.Add(new Ellipse
		{
			Width = size,
			Height = size,
			Fill = ThemeBrushWithOpacity("AccentBrush", 0.14),
			Stroke = ThemeBrushWithOpacity("GoldBrush", 0.44),
			StrokeThickness = 1.0
		});
		mark.Children.Add(new Ellipse
		{
			Width = size * 0.50,
			Height = size * 0.50,
			Fill = ThemeBrushWithOpacity("GoldBrush", 0.10),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		});
		Polyline wave = new Polyline
		{
			Stroke = ResourceBrush("AccentBrush"),
			StrokeThickness = Math.Max(1.6, 1.8 * scale),
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round,
			StrokeLineJoin = PenLineJoin.Round,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center
		};
		wave.Points.Add(new System.Windows.Point(7.0 * scale, 18.0 * scale));
		wave.Points.Add(new System.Windows.Point(11.0 * scale, 14.0 * scale));
		wave.Points.Add(new System.Windows.Point(15.0 * scale, 21.0 * scale));
		wave.Points.Add(new System.Windows.Point(19.0 * scale, 10.0 * scale));
		wave.Points.Add(new System.Windows.Point(23.0 * scale, 20.0 * scale));
		wave.Points.Add(new System.Windows.Point(27.0 * scale, 15.0 * scale));
		mark.Children.Add(wave);
		return mark;
	}

	private UIElement CreateAudioSidebarLogoContent(bool isActive)
	{
		Grid mark = new Grid
		{
			Width = 28.0,
			Height = 28.0
		};
		mark.Children.Add(CreateAudioLogoMark(26.0));
		if (isActive)
		{
			mark.Children.Add(new Ellipse
			{
				Width = 6.0,
				Height = 6.0,
				Fill = ResourceBrush("GoldBrush"),
				HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Bottom
			});
		}
		return mark;
	}

	private Border CreateAudioPill(string label, string value)
	{
		Border pill = new Border
		{
			CornerRadius = new CornerRadius(8.0),
			BorderBrush = ResourceBrush("LineBrush"),
			BorderThickness = new Thickness(1.0),
			Background = ResourceBrush("ElevatedBrush"),
			Padding = new Thickness(10.0, 7.0, 10.0, 7.0),
			Margin = new Thickness(8.0, 0.0, 0.0, 8.0)
		};
		StackPanel stack = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical
		};
		pill.Child = stack;
		stack.Children.Add(new TextBlock
		{
			Text = label,
			FontSize = 10.0,
			FontWeight = FontWeights.SemiBold,
			Foreground = ResourceBrush("MutedBrush")
		});
		stack.Children.Add(new TextBlock
		{
			Text = value,
			FontSize = 12.0,
			FontWeight = FontWeights.SemiBold,
			Foreground = ResourceBrush("InkBrush")
		});
		return pill;
	}

	private TextBlock CreateAudioStageLabel(string text)
	{
		return new TextBlock
		{
			Text = text.ToUpperInvariant(),
			FontSize = 10.0,
			FontWeight = FontWeights.SemiBold,
			Foreground = ResourceBrush("MutedBrush")
		};
	}

	private Grid CreateAudioOptionGrid()
	{
		Grid grid = new Grid
		{
			Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
		};
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(12.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		return grid;
	}

	private void AddAudioOption(Grid grid, string label, UIElement control)
	{
		int index = grid.Children.Count;
		int row = index / 2;
		int column = (index % 2) * 2;
		if (grid.RowDefinitions.Count <= row)
		{
			grid.RowDefinitions.Add(new RowDefinition
			{
				Height = GridLength.Auto
			});
		}
		UIElement field = CreateAudioField(label, control);
		Grid.SetRow(field, row);
		Grid.SetColumn(field, column);
		grid.Children.Add(field);
	}

	private UIElement CreateAudioField(string label, UIElement control)
	{
		StackPanel field = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical,
			Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
		};
		field.Children.Add(new TextBlock
		{
			Text = label,
			FontSize = 11.0,
			FontWeight = FontWeights.SemiBold,
			Foreground = ResourceBrush("MutedBrush"),
			Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
		});
		field.Children.Add(control);
		return field;
	}

	private UIElement CreateAudioRailButton(string label, RoutedEventHandler clickHandler)
	{
		System.Windows.Controls.Button button = CreateTtsButton(label, clickHandler, isPrimary: false);
		button.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
		button.Margin = new Thickness(0.0, 0.0, 0.0, 12.0);
		return button;
	}

	private UIElement CreateAudioRailDivider()
	{
		return new Border
		{
			Height = 1.0,
			Background = ResourceBrush("LineBrush"),
			Margin = new Thickness(0.0, 4.0, 0.0, 14.0)
		};
	}

	private UIElement CreateAudioCapabilityRow(string label, string value, string detail)
	{
		Grid row = new Grid
		{
			Margin = new Thickness(0.0, 10.0, 0.0, 0.0)
		};
		row.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(78.0)
		});
		row.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		row.Children.Add(new TextBlock
		{
			Text = label,
			FontWeight = FontWeights.SemiBold,
			Foreground = ResourceBrush("MutedBrush"),
			VerticalAlignment = VerticalAlignment.Top
		});
		StackPanel stack = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical
		};
		stack.Children.Add(new TextBlock
		{
			Text = value,
			TextWrapping = TextWrapping.Wrap,
			Foreground = ResourceBrush("InkBrush"),
			FontWeight = FontWeights.SemiBold
		});
		stack.Children.Add(new TextBlock
		{
			Text = detail,
			TextWrapping = TextWrapping.Wrap,
			Foreground = ResourceBrush("MutedBrush"),
			Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
		});
		Grid.SetColumn(stack, 1);
		row.Children.Add(stack);
		return row;
	}

	private System.Windows.Controls.ComboBox CreateAudioComboBox(System.Collections.IEnumerable itemsSource, string displayMemberPath, string selectedValuePath, object selectedValue)
	{
		System.Windows.Controls.ComboBox comboBox = new System.Windows.Controls.ComboBox
		{
			ItemsSource = itemsSource,
			MinHeight = 34.0
		};
		if (!string.IsNullOrWhiteSpace(displayMemberPath))
		{
			comboBox.DisplayMemberPath = displayMemberPath;
		}
		if (!string.IsNullOrWhiteSpace(selectedValuePath))
		{
			comboBox.SelectedValuePath = selectedValuePath;
			comboBox.SelectedValue = selectedValue;
		}
		else
		{
			comboBox.Text = selectedValue?.ToString() ?? "";
		}
		return comboBox;
	}

	private System.Windows.Controls.TextBox CreateAudioTextBox(string text, double minHeight, bool acceptsReturn)
	{
		return new System.Windows.Controls.TextBox
		{
			Text = text,
			MinHeight = minHeight,
			TextWrapping = TextWrapping.Wrap,
			AcceptsReturn = acceptsReturn,
			VerticalScrollBarVisibility = acceptsReturn ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
			Background = ResourceBrush("ElevatedBrush"),
			Foreground = ResourceBrush("InkBrush"),
			BorderBrush = ResourceBrush("LineBrush"),
			Padding = new Thickness(10.0)
		};
	}

	private TextBlock CreateAudioStatusText()
	{
		return new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 10.0, 0.0, 0.0),
			Foreground = ResourceBrush("MutedBrush")
		};
	}

	private void InstallTtsSettingsPanel()
	{
		if (_ttsPanelInstalled)
		{
			return;
		}
		System.Windows.Controls.Panel? host = SettingsScrollViewer.Content as System.Windows.Controls.Panel;
		if (host == null)
		{
			AppLog.Warn("Could not install TTS settings panel because SettingsScrollViewer content is not a panel.");
			return;
		}
		_ttsPanelInstalled = true;

		Border card = new Border
		{
			CornerRadius = new CornerRadius(8.0),
			BorderBrush = ResourceBrush("LineBrush"),
			BorderThickness = new Thickness(1.0),
			Background = ResourceBrush("PanelBrush"),
			Padding = new Thickness(18.0),
			Margin = new Thickness(0.0, 18.0, 0.0, 0.0)
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical
		};
		card.Child = stackPanel;

		TextBlock heading = new TextBlock
		{
			Text = "Local TTS",
			FontSize = 17.0,
			FontWeight = FontWeights.SemiBold,
			Foreground = ResourceBrush("InkBrush")
		};
		stackPanel.Children.Add(heading);
		stackPanel.Children.Add(new TextBlock
		{
			Text = "Generate audio from the current Speak output using the D:\\Models Qwen3 and Tortoise models.",
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 5.0, 0.0, 14.0),
			Foreground = ResourceBrush("MutedBrush")
		});

		_ttsEngineComboBox = new System.Windows.Controls.ComboBox
		{
			ItemsSource = _ttsEngines,
			DisplayMemberPath = "Name",
			SelectedValuePath = "Id",
			SelectedValue = _settings.TtsEngineId,
			MinHeight = 34.0
		};
		_ttsEngineComboBox.SelectionChanged += TtsControl_Changed;
		stackPanel.Children.Add(CreateTtsSettingsRow("Engine", _ttsEngineComboBox));

		_ttsVoiceComboBox = new System.Windows.Controls.ComboBox
		{
			ItemsSource = _ttsVoices,
			DisplayMemberPath = "Name",
			SelectedValuePath = "Id",
			MinHeight = 34.0
		};
		_ttsVoiceComboBox.SelectionChanged += TtsControl_Changed;
		stackPanel.Children.Add(CreateTtsSettingsRow("Voice", _ttsVoiceComboBox));

		_ttsSampleTextBox = new System.Windows.Controls.TextBox
		{
			Text = "Hamza, Speak local TTS is ready.",
			MinHeight = 64.0,
			TextWrapping = TextWrapping.Wrap,
			AcceptsReturn = true,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Background = ResourceBrush("ElevatedBrush"),
			Foreground = ResourceBrush("InkBrush"),
			BorderBrush = ResourceBrush("LineBrush"),
			Padding = new Thickness(10.0)
		};
		stackPanel.Children.Add(CreateTtsSettingsRow("Sample", _ttsSampleTextBox));

		WrapPanel actions = new WrapPanel
		{
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left
		};
		_ttsSpeakCurrentButton = CreateTtsButton("Speak Current Output", TtsSpeakCurrentButton_Click, isPrimary: true);
		_ttsGenerateSampleButton = CreateTtsButton("Generate Sample", TtsGenerateSampleButton_Click, isPrimary: false);
		_ttsOpenLastButton = CreateTtsButton("Open Last", TtsOpenLastButton_Click, isPrimary: false);
		System.Windows.Controls.Button openFolderButton = CreateTtsButton("Open Folder", TtsOpenFolderButton_Click, isPrimary: false);
		actions.Children.Add(_ttsSpeakCurrentButton);
		actions.Children.Add(_ttsGenerateSampleButton);
		actions.Children.Add(_ttsOpenLastButton);
		actions.Children.Add(openFolderButton);
		stackPanel.Children.Add(actions);

		_ttsStatusTextBlock = new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0),
			Foreground = ResourceBrush("MutedBrush")
		};
		stackPanel.Children.Add(_ttsStatusTextBlock);
		_ttsOutputTextBlock = new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0),
			Foreground = ResourceBrush("MutedBrush")
		};
		stackPanel.Children.Add(_ttsOutputTextBlock);

		AddSettingsCardToHost(host, card);
		RefreshTtsVoiceChoices(preserveSelection: true);
		UpdateTtsStatus();
	}

	private static void AddSettingsCardToHost(System.Windows.Controls.Panel host, Border card)
	{
		if (host is Grid grid)
		{
			int row = grid.RowDefinitions.Count;
			grid.RowDefinitions.Add(new RowDefinition
			{
				Height = GridLength.Auto
			});
			Grid.SetRow(card, row);
			Grid.SetColumnSpan(card, Math.Max(1, grid.ColumnDefinitions.Count));
			grid.Children.Add(card);
			return;
		}
		host.Children.Add(card);
	}

	private UIElement CreateTtsSettingsRow(string label, UIElement control)
	{
		Grid grid = new Grid
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		};
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(130.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		TextBlock textBlock = new TextBlock
		{
			Text = label,
			VerticalAlignment = VerticalAlignment.Center,
			Foreground = ResourceBrush("MutedBrush"),
			FontWeight = FontWeights.SemiBold
		};
		Grid.SetColumn(textBlock, 0);
		Grid.SetColumn(control, 1);
		grid.Children.Add(textBlock);
		grid.Children.Add(control);
		return grid;
	}

	private System.Windows.Controls.Button CreateTtsButton(string label, RoutedEventHandler clickHandler, bool isPrimary)
	{
		System.Windows.Controls.Button button = new System.Windows.Controls.Button
		{
			Content = label,
			MinHeight = 34.0,
			Padding = new Thickness(12.0, 6.0, 12.0, 6.0),
			Margin = new Thickness(0.0, 0.0, 8.0, 8.0),
			Background = isPrimary ? ResourceBrush("AccentBrush") : ResourceBrush("ElevatedBrush"),
			Foreground = isPrimary ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 20, 28)) : ResourceBrush("InkBrush"),
			BorderBrush = isPrimary ? ResourceBrush("AccentBrush") : ResourceBrush("LineBrush")
		};
		button.Click += clickHandler;
		return button;
	}

	private void RefreshTtsVoiceChoices(bool preserveSelection)
	{
		if (_ttsVoiceComboBox == null)
		{
			return;
		}
		string currentVoice = preserveSelection ? FirstNonEmpty(_ttsVoiceComboBox.SelectedValue as string, _settings.TtsVoiceId) : _settings.TtsVoiceId;
		string engineId = SelectedTtsEngineId();
		_ttsVoices.Clear();
		_cloneVoiceRefs.Clear();
		foreach (TtsVoiceOption option in TtsVoiceOption.ForEngine(engineId))
		{
			_ttsVoices.Add(option);
		}
		try
		{
			string cloneRoot = FirstNonEmpty(_settings.VoiceCloneOutputRoot, MaxFlowSettings.Default.VoiceCloneOutputRoot);
			if (Directory.Exists(cloneRoot))
			{
				foreach (string profileDir in Directory.EnumerateDirectories(cloneRoot))
				{
					string manifestPath = System.IO.Path.Combine(profileDir, "voice-clone-profile.json");
					if (!File.Exists(manifestPath)) continue;
					try
					{
						using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
						JsonElement root = doc.RootElement;
						string? profileEngineId = root.TryGetProperty("engineId", out JsonElement e) ? e.GetString() : null;
						if (!string.IsNullOrWhiteSpace(profileEngineId) && (profileEngineId.Equals(engineId, StringComparison.OrdinalIgnoreCase) || (engineId.Equals("qwen3-customvoice-1.7b", StringComparison.OrdinalIgnoreCase) && profileEngineId.Equals("qwen3-base-1.7b", StringComparison.OrdinalIgnoreCase))))
						{
							string? profileName = root.TryGetProperty("profileName", out JsonElement p) ? p.GetString() : null;
							string? refAudio = root.TryGetProperty("referenceAudio", out JsonElement r) ? r.GetString() : null;
							if (!string.IsNullOrWhiteSpace(profileName) && !string.IsNullOrWhiteSpace(refAudio) && File.Exists(refAudio))
							{
								string voiceId = "clone:" + profileName;
								_ttsVoices.Add(new TtsVoiceOption
								{
									Id = voiceId,
									Name = profileName + " (clone)",
									EngineId = engineId
								});
								_cloneVoiceRefs[voiceId] = refAudio;
							}
						}
					}
					catch { }
				}
			}
		}
		catch { }
		TtsVoiceOption selected = _ttsVoices.FirstOrDefault((TtsVoiceOption option) => option.Id.Equals(currentVoice, StringComparison.OrdinalIgnoreCase)) ?? _ttsVoices.First();
		_ttsVoiceComboBox.SelectedValue = selected.Id;
	}

	private void UpdateTtsStatus()
	{
		if (_ttsStatusTextBlock == null || _ttsOutputTextBlock == null)
		{
			return;
		}
		TtsEngineOption engine = TtsEngineOption.Find(SelectedTtsEngineId());
		_ttsStatusTextBlock.Text = _ttsSynthesizer.DescribeAvailability(engine.Id) + " " + engine.Subtitle;
		_ttsOutputTextBlock.Text = string.IsNullOrWhiteSpace(_settings.TtsLastOutputPath)
			? "Output folder: " + _settings.TtsOutputRoot
			: "Last output: " + _settings.TtsLastOutputPath;
		bool canSpeak = _ttsSynthesizer.CanSynthesize(engine.Id);
		if (_ttsSpeakCurrentButton != null)
		{
			_ttsSpeakCurrentButton.IsEnabled = canSpeak;
		}
		if (_ttsGenerateSampleButton != null)
		{
			_ttsGenerateSampleButton.IsEnabled = canSpeak;
		}
		if (_ttsOpenLastButton != null)
		{
			_ttsOpenLastButton.IsEnabled = File.Exists(_settings.TtsLastOutputPath);
		}
		if (_ttsPlayLastButton != null)
		{
			_ttsPlayLastButton.IsEnabled = File.Exists(_settings.TtsLastOutputPath);
		}
		if (_audioPlaybackStatusTextBlock != null)
		{
			_audioPlaybackStatusTextBlock.Text = File.Exists(_settings.TtsLastOutputPath)
				? "Ready to preview: " + System.IO.Path.GetFileName(_settings.TtsLastOutputPath)
				: "No generated audio yet.";
		}
		if (_ttsWarmStatusTextBlock != null && string.IsNullOrWhiteSpace(_ttsWarmStatusTextBlock.Text))
		{
			_ttsWarmStatusTextBlock.Text = _ttsSynthesizer.IsEngineWarm(engine.Id)
				? $"Model ready. It stays loaded for {_settings.ModelKeepAliveMinutes} minutes after each generation."
				: (_ttsSynthesizer.CanWarmUp(engine.Id) ? "Model cold. It will load automatically on generation, or you can warm it now." : "Warm model is not available for this engine.");
		}
		if (_ttsWarmModelButton != null && _ttsWarmModelButton.IsEnabled != !_isWarming)
		{
			_ttsWarmModelButton.IsEnabled = !_isWarming;
		}
	}

	private void AudioTranscribeControl_Changed(object sender, RoutedEventArgs e)
	{
		if (_isLoading)
		{
			return;
		}
		SyncSettingsControlsFromAudio();
		if (ReferenceEquals(sender, _audioModelKeepAliveComboBox))
		{
			_ = StopWarmEngineIfNeededAsync();
		}
		if (ReferenceEquals(sender, _audioCloudSttProviderComboBox))
		{
			ApplySelectedCloudSttProviderDefaults();
			RefreshCloudSttModelsAsync(quiet: false);
		}
		SaveSettingsFromUi();
		UpdateAudioWorkspaceStatus();
	}

	private void AudioCloneControl_Changed(object sender, RoutedEventArgs e)
	{
		if (_isLoading)
		{
			return;
		}
		if (ReferenceEquals(sender, _audioCloneEngineComboBox))
		{
			_ = StopWarmEngineIfNeededAsync();
		}
		SaveSettingsFromUi();
		UpdateAudioWorkspaceStatus();
	}

	private void AudioDesignControl_Changed(object sender, RoutedEventArgs e)
	{
		if (_isLoading)
		{
			return;
		}
		SaveSettingsFromUi();
		UpdateAudioWorkspaceStatus();
	}

	private void AudioRecordNowButton_Click(object sender, RoutedEventArgs e)
	{
		SetActiveTab("dictate");
		if (!_isRecording && !_isTranscribing)
		{
			RecordButton_Click(sender, e);
		}
	}

	private void AudioOpenRecordingsButton_Click(object sender, RoutedEventArgs e)
	{
		string folder = System.IO.Path.Combine(_store.Root, "recordings");
		Directory.CreateDirectory(folder);
		OpenTtsPath(folder);
	}

	private void AudioChooseCloneReferenceButton_Click(object sender, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "Choose voice reference audio",
			Filter = "Audio files|*.wav;*.mp3;*.m4a;*.ogg;*.webm;*.mp4|All files|*.*"
		};
		if (openFileDialog.ShowDialog(this) == true && _audioCloneReferenceTextBox != null)
		{
			_audioCloneReferenceTextBox.Text = openFileDialog.FileName;
			SaveSettingsFromUi();
			UpdateAudioWorkspaceStatus();
		}
	}

	private void AudioPrepareCloneProfileButton_Click(object sender, RoutedEventArgs e)
	{
		SaveSettingsFromUi();
		if (string.IsNullOrWhiteSpace(_settings.VoiceCloneReferenceAudioPath) || !File.Exists(_settings.VoiceCloneReferenceAudioPath))
		{
			if (_audioCloneStatusTextBlock != null)
			{
				_audioCloneStatusTextBlock.Text = "Choose an existing WAV, MP3, M4A, OGG, WEBM, or MP4 file first.";
			}
			StatusTextBlock.Text = "Voice clone reference missing";
			return;
		}
		string profileName = FirstNonEmpty(_settings.VoiceCloneProfileName, "cloned voice");
		string engineId = (_audioCloneEngineComboBox?.SelectedValue as string) ?? "qwen3-base-1.7b";
		string folder = System.IO.Path.Combine(FirstNonEmpty(_settings.VoiceCloneOutputRoot, MaxFlowSettings.Default.VoiceCloneOutputRoot), StableDirectoryName(profileName) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
		Directory.CreateDirectory(folder);
		string copiedReference = System.IO.Path.Combine(folder, "reference" + System.IO.Path.GetExtension(_settings.VoiceCloneReferenceAudioPath));
		File.Copy(_settings.VoiceCloneReferenceAudioPath, copiedReference, overwrite: true);
		string manifestPath = System.IO.Path.Combine(folder, "voice-clone-profile.json");
		File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
		{
			profileName,
			createdAt = DateTimeOffset.Now,
			engineId,
			model = TtsEngineOption.Find("qwen3-base-1.7b").ModelPath,
			referenceAudio = copiedReference,
			status = "prepared"
		}, new JsonSerializerOptions
		{
			WriteIndented = true
		}));
		StatusTextBlock.Text = "Voice clone profile prepared";
		if (_audioCloneStatusTextBlock != null)
		{
			_audioCloneStatusTextBlock.Text = "Prepared profile: " + manifestPath;
		}
	}

	private async void AudioGenerateCloneButton_Click(object sender, RoutedEventArgs e)
	{
		SaveSettingsFromUi();
		string text = _ttsCloneTextTextBox?.Text.Trim() ?? "";
		if (string.IsNullOrWhiteSpace(text))
		{
			if (_audioCloneStatusTextBlock != null)
			{
				_audioCloneStatusTextBlock.Text = "Enter some text to speak with the cloned voice.";
			}
			return;
		}
		string refAudio = _settings.VoiceCloneReferenceAudioPath;
		if (string.IsNullOrWhiteSpace(refAudio) || !File.Exists(refAudio))
		{
			if (_audioCloneStatusTextBlock != null)
			{
				_audioCloneStatusTextBlock.Text = "Choose a reference audio file first.";
			}
			return;
		}
		string cloneEngineId = (_audioCloneEngineComboBox?.SelectedValue as string) ?? "qwen3-base-1.7b";
		if (_ttsCloneGenerateButton != null)
		{
			_ttsCloneGenerateButton.IsEnabled = false;
		}
		if (_audioCloneStatusTextBlock != null)
		{
			_audioCloneStatusTextBlock.Text = "Generating cloned voice with " + cloneEngineId + "... (this may take several minutes)";
		}
		try
		{
			await ReleaseWhisperForAudioStudioAsync();
			string output = await _ttsSynthesizer.SynthesizeVoiceCloneAsync(text, refAudio, cloneEngineId, "Auto", _settings.ModelKeepAliveMinutes, CancellationToken.None);
			if (_audioCloneStatusTextBlock != null)
			{
				_audioCloneStatusTextBlock.Text = $"Generated: {output}. The model stays loaded for {_settings.ModelKeepAliveMinutes} minutes after this generation.";
			}
			StatusTextBlock.Text = "Voice clone generated";
		}
		catch (Exception exception)
		{
			AppLog.Warn("Voice clone generation failed.", exception);
			StatusTextBlock.Text = "Voice clone failed";
			if (_audioCloneStatusTextBlock != null)
			{
				_audioCloneStatusTextBlock.Text = exception.Message;
			}
		}
		finally
		{
			if (_ttsCloneGenerateButton != null)
			{
				_ttsCloneGenerateButton.IsEnabled = true;
			}
		}
	}

	private void AudioOpenCloneFolderButton_Click(object sender, RoutedEventArgs e)
	{
		string folder = FirstNonEmpty(_settings.VoiceCloneOutputRoot, MaxFlowSettings.Default.VoiceCloneOutputRoot);
		Directory.CreateDirectory(folder);
		OpenTtsPath(folder);
	}

	private void AudioSaveDesignBriefButton_Click(object sender, RoutedEventArgs e)
	{
		SaveSettingsFromUi();
		string prompt = FirstNonEmpty(_settings.VoiceDesignPrompt, MaxFlowSettings.Default.VoiceDesignPrompt);
		string folder = FirstNonEmpty(_settings.VoiceDesignOutputRoot, MaxFlowSettings.Default.VoiceDesignOutputRoot);
		Directory.CreateDirectory(folder);
		string path = System.IO.Path.Combine(folder, "voice-design-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
		File.WriteAllText(path, JsonSerializer.Serialize(new
		{
			createdAt = DateTimeOffset.Now,
			model = "",
			prompt,
			status = "brief-saved"
		}, new JsonSerializerOptions
		{
			WriteIndented = true
		}));
		StatusTextBlock.Text = "Voice design brief saved";
		if (_audioDesignStatusTextBlock != null)
		{
			_audioDesignStatusTextBlock.Text = "Saved design brief: " + path;
		}
	}

	private void AudioOpenDesignFolderButton_Click(object sender, RoutedEventArgs e)
	{
		string folder = FirstNonEmpty(_settings.VoiceDesignOutputRoot, MaxFlowSettings.Default.VoiceDesignOutputRoot);
		Directory.CreateDirectory(folder);
		OpenTtsPath(folder);
	}

	private void SyncSettingsControlsFromAudio()
	{
		bool isLoading = _isLoading;
		_isLoading = true;
		try
		{
			if (_audioSttEngineComboBox != null)
			{
				EngineComboBox.SelectedValue = _audioSttEngineComboBox.SelectedValue ?? _settings.EngineId;
			}
			if (_audioTranscriptionModelComboBox != null)
			{
				TranscriptionModelComboBox.SelectedValue = _audioTranscriptionModelComboBox.SelectedValue ?? _settings.TranscriptionModelId;
			}
			if (_audioCloudSttProviderComboBox != null)
			{
				CloudSttProviderComboBox.SelectedValue = _audioCloudSttProviderComboBox.SelectedValue ?? _settings.SttCloudProviderId;
			}
			if (_audioCloudSttModelComboBox != null)
			{
				CloudSttModelComboBox.Text = FirstNonEmpty(_audioCloudSttModelComboBox.Text, _settings.SttCloudModel);
			}
			if (_audioWhisperDeviceComboBox != null)
			{
				WhisperDeviceComboBox.SelectedValue = _audioWhisperDeviceComboBox.SelectedValue ?? _settings.WhisperDeviceId;
			}
			if (_audioModelKeepAliveComboBox != null)
			{
				ModelKeepAliveComboBox.SelectedValue = _audioModelKeepAliveComboBox.SelectedValue ?? _settings.ModelKeepAliveMinutes;
			}
			if (_audioInputComboBox != null)
			{
				AudioInputComboBox.SelectedValue = _audioInputComboBox.SelectedValue ?? _settings.AudioInputDeviceNumber;
			}
		}
		finally
		{
			_isLoading = isLoading;
		}
	}

	private void SyncAudioControlsFromSettings()
	{
		bool isLoading = _isLoading;
		_isLoading = true;
		try
		{
			if (_audioSttEngineComboBox != null)
			{
				_audioSttEngineComboBox.SelectedValue = _settings.EngineId;
			}
			if (_audioTranscriptionModelComboBox != null)
			{
				_audioTranscriptionModelComboBox.SelectedValue = _settings.TranscriptionModelId;
			}
			if (_audioCloudSttProviderComboBox != null)
			{
				_audioCloudSttProviderComboBox.SelectedValue = _settings.SttCloudProviderId;
			}
			if (_audioCloudSttModelComboBox != null)
			{
				_audioCloudSttModelComboBox.Text = _settings.SttCloudModel;
			}
			if (_audioWhisperDeviceComboBox != null)
			{
				_audioWhisperDeviceComboBox.SelectedValue = _settings.WhisperDeviceId;
			}
			if (_audioModelKeepAliveComboBox != null)
			{
				_audioModelKeepAliveComboBox.SelectedValue = _settings.ModelKeepAliveMinutes;
			}
			if (_audioInputComboBox != null)
			{
				_audioInputComboBox.SelectedValue = _settings.AudioInputDeviceNumber;
			}
			if (_audioCloneReferenceTextBox != null)
			{
				_audioCloneReferenceTextBox.Text = _settings.VoiceCloneReferenceAudioPath;
			}
			if (_audioCloneNameTextBox != null)
			{
				_audioCloneNameTextBox.Text = _settings.VoiceCloneProfileName;
			}
			if (_audioDesignPromptTextBox != null)
			{
				_audioDesignPromptTextBox.Text = _settings.VoiceDesignPrompt;
			}
		}
		finally
		{
			_isLoading = isLoading;
		}
	}

	private void UpdateAudioWorkspaceStatus()
	{
		if (_audioTranscribeStatusTextBlock != null)
		{
			EngineProfile engine = EngineProfile.Presets.FirstOrDefault((EngineProfile option) => option.Id.Equals(_settings.EngineId, StringComparison.OrdinalIgnoreCase)) ?? EngineProfile.Presets.First();
			TranscriptionModelOption model = _transcriptionModels.FirstOrDefault((TranscriptionModelOption option) => option.Id.Equals(_settings.TranscriptionModelId, StringComparison.OrdinalIgnoreCase)) ?? _transcriptionModels.First();
			_audioTranscribeStatusTextBlock.Text = $"{engine.Name}. Model: {model.Name}. Device: {SelectedWhisperDeviceName()}. Audio files: {System.IO.Path.Combine(_store.Root, "recordings")}";
		}
		if (_audioCloneStatusTextBlock != null)
		{
			string referenceStatus = File.Exists(_settings.VoiceCloneReferenceAudioPath) ? "reference ready" : "reference missing";
			_audioCloneStatusTextBlock.Text = "Qwen3 1.7B Base: " + referenceStatus + ". Profiles: " + FirstNonEmpty(_settings.VoiceCloneOutputRoot, MaxFlowSettings.Default.VoiceCloneOutputRoot);
		}
		if (_audioDesignStatusTextBlock != null)
		{
			_audioDesignStatusTextBlock.Text = "VoiceDesign is not installed in D:\\Models.";
		}
		UpdateTtsStatus();
	}

	private string SelectedTtsEngineId()
	{
		return (_ttsEngineComboBox?.SelectedValue as string) ?? _settings.TtsEngineId ?? MaxFlowSettings.Default.TtsEngineId;
	}

	private string SelectedTtsVoiceId()
	{
		return (_ttsVoiceComboBox?.SelectedValue as string) ?? _settings.TtsVoiceId ?? "default";
	}

	private void TtsControl_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_isLoading)
		{
			return;
		}
		if (ReferenceEquals(sender, _ttsEngineComboBox))
		{
			RefreshTtsVoiceChoices(preserveSelection: false);
			_ = StopWarmEngineIfNeededAsync();
		}
		SaveSettingsFromUi();
		UpdateTtsStatus();
		UpdateAudioWorkspaceStatus();
	}

	private async Task StopWarmEngineIfNeededAsync()
	{
		try
		{
			_ttsWarmCts?.Cancel();
			_ttsWarmCts?.Dispose();
			_ttsWarmCts = null;
			await _ttsSynthesizer.StopWarmEngineAsync();
			if (_ttsWarmStatusTextBlock != null)
			{
				_ttsWarmStatusTextBlock.Text = "Previous model stopped. The selected model will load automatically on first use.";
			}
			if (_ttsWarmModelButton != null)
			{
				_ttsWarmModelButton.IsEnabled = true;
			}
		}
		catch
		{
		}
	}

	private async void TtsSpeakCurrentButton_Click(object sender, RoutedEventArgs e)
	{
		string text = FirstNonEmpty(CurrentFormattedText(), RawTranscriptTextBox.Text, _ttsSampleTextBox?.Text ?? "");
		await GenerateTtsAsync(text, playWhenReady: true);
	}

	private async void TtsGenerateSampleButton_Click(object sender, RoutedEventArgs e)
	{
		await GenerateTtsAsync(_ttsSampleTextBox?.Text ?? "", playWhenReady: true);
	}

	private void TtsOpenLastButton_Click(object sender, RoutedEventArgs e)
	{
		OpenTtsPath(_settings.TtsLastOutputPath);
	}

	private void TtsPlayLastButton_Click(object sender, RoutedEventArgs e)
	{
		PlayTtsPath(_settings.TtsLastOutputPath);
	}

	private void TtsStopPlaybackButton_Click(object sender, RoutedEventArgs e)
	{
		StopTtsPreview();
	}

	private async void TtsWarmModelButton_Click(object sender, RoutedEventArgs e)
	{
		if (_isWarming)
		{
			return;
		}
		string engineId = _settings.TtsEngineId;
		if (!_ttsSynthesizer.CanWarmUp(engineId))
		{
			if (_ttsWarmStatusTextBlock != null)
			{
				_ttsWarmStatusTextBlock.Text = "Warm model is not available for this engine.";
			}
			return;
		}
		_isWarming = true;
		_ttsWarmCts?.Cancel();
		_ttsWarmCts?.Dispose();
		_ttsWarmCts = new CancellationTokenSource();
		CancellationToken ct = _ttsWarmCts.Token;
		if (_ttsWarmStatusTextBlock != null)
		{
			_ttsWarmStatusTextBlock.Text = "Loading the selected local model...";
		}
		try
		{
			await ReleaseWhisperForAudioStudioAsync();
			bool success = await _ttsSynthesizer.WarmUpAsync(engineId, _settings.ModelKeepAliveMinutes, ct);
			if (ct.IsCancellationRequested)
			{
				return;
			}
			if (success)
			{
				StatusTextBlock.Text = "TTS model warmed up";
				if (_ttsWarmStatusTextBlock != null)
				{
					_ttsWarmStatusTextBlock.Text = $"Model ready. It stays loaded for {_settings.ModelKeepAliveMinutes} minutes after each generation.";
				}
			}
			else
			{
				StatusTextBlock.Text = "TTS warm-up failed";
				if (_ttsWarmStatusTextBlock != null)
				{
					_ttsWarmStatusTextBlock.Text = "Warm-up failed. Check the engine path and try again.";
				}
			}
		}
		catch (OperationCanceledException)
		{
			StatusTextBlock.Text = "TTS warm-up cancelled";
		}
		catch (Exception exception)
		{
			AppLog.Warn("TTS warm-up failed.", exception);
			StatusTextBlock.Text = "TTS warm-up error";
			if (_ttsWarmStatusTextBlock != null)
			{
				_ttsWarmStatusTextBlock.Text = exception.Message;
			}
		}
		finally
		{
			_isWarming = false;
			UpdateTtsStatus();
		}
	}

	private void TtsOpenFolderButton_Click(object sender, RoutedEventArgs e)
	{
		string folder = FirstNonEmpty(_settings.TtsOutputRoot, MaxFlowSettings.Default.TtsOutputRoot);
		Directory.CreateDirectory(folder);
		OpenTtsPath(folder);
	}

	private async Task GenerateTtsAsync(string text, bool playWhenReady)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			StatusTextBlock.Text = "No text to send to TTS";
			if (_ttsStatusTextBlock != null)
			{
				_ttsStatusTextBlock.Text = "Type a sample or generate from a transcript first.";
			}
			return;
		}
		_ttsGenerationCts?.Cancel();
		_ttsGenerationCts?.Dispose();
		_ttsGenerationCts = new CancellationTokenSource();
		CancellationToken cancellationToken = _ttsGenerationCts.Token;
		SetTtsBusy(isBusy: true);
		try
		{
			SaveSettingsFromUi();
			await ReleaseWhisperForAudioStudioAsync();
			string voiceId = _settings.TtsVoiceId;
			string voicePromptPath = "";
			if (!string.IsNullOrWhiteSpace(voiceId) && voiceId.StartsWith("clone:", StringComparison.OrdinalIgnoreCase) && _cloneVoiceRefs.TryGetValue(voiceId, out string refAudio))
			{
				voicePromptPath = refAudio;
			}
			TtsSynthesisResult result = await _ttsSynthesizer.SynthesizeAsync(new TtsSynthesisRequest
			{
				Text = text.Trim(),
				EngineId = _settings.TtsEngineId,
				VoiceId = voiceId,
				OutputRoot = _settings.TtsOutputRoot,
				Language = _settings.TtsLanguage,
				VoicePromptPath = voicePromptPath,
				ModelKeepAliveMinutes = _settings.ModelKeepAliveMinutes
			}, cancellationToken);
			_settings.TtsLastOutputPath = result.OutputPath;
			_store.SaveSettings(_settings);
			StatusTextBlock.Text = "TTS generated";
			if (_ttsStatusTextBlock != null)
			{
				_ttsStatusTextBlock.Text = result.Summary;
			}
			if (_audioPlaybackStatusTextBlock != null)
			{
				_audioPlaybackStatusTextBlock.Text = "Generated: " + System.IO.Path.GetFileName(result.OutputPath);
			}
			if (_ttsWarmStatusTextBlock != null && _ttsSynthesizer.IsEngineWarm(_settings.TtsEngineId))
			{
				_ttsWarmStatusTextBlock.Text = $"Model ready. It stays loaded for {_settings.ModelKeepAliveMinutes} minutes after each generation.";
			}
			UpdateTtsStatus();
			if (playWhenReady)
			{
				PlayTtsPath(result.OutputPath);
			}
		}
		catch (OperationCanceledException)
		{
			StatusTextBlock.Text = "TTS cancelled";
		}
		catch (Exception exception)
		{
			AppLog.Warn("Local TTS generation failed.", exception);
			StatusTextBlock.Text = "TTS failed";
			if (_ttsStatusTextBlock != null)
			{
				_ttsStatusTextBlock.Text = exception.Message;
			}
		}
		finally
		{
			SetTtsBusy(isBusy: false);
		}
	}

	private void SetTtsBusy(bool isBusy)
	{
		if (_ttsSpeakCurrentButton != null)
		{
			_ttsSpeakCurrentButton.IsEnabled = !isBusy;
		}
		if (_ttsGenerateSampleButton != null)
		{
			_ttsGenerateSampleButton.IsEnabled = !isBusy;
		}
		if (_ttsPlayLastButton != null)
		{
			_ttsPlayLastButton.IsEnabled = !isBusy && File.Exists(_settings.TtsLastOutputPath);
		}
		if (_ttsStopPlaybackButton != null)
		{
			_ttsStopPlaybackButton.IsEnabled = !isBusy;
		}
		if (_ttsEngineComboBox != null)
		{
			_ttsEngineComboBox.IsEnabled = !isBusy;
		}
		if (_ttsVoiceComboBox != null)
		{
			_ttsVoiceComboBox.IsEnabled = !isBusy;
		}
		if (_ttsStatusTextBlock != null && isBusy)
		{
			_ttsStatusTextBlock.Text = "Generating local audio...";
		}
		if (!isBusy)
		{
			UpdateTtsStatus();
		}
	}

	private static void OpenTtsPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
		{
			return;
		}
		Process.Start(new ProcessStartInfo
		{
			FileName = path,
			UseShellExecute = true
		});
	}

	private void PlayTtsPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			if (_audioPlaybackStatusTextBlock != null)
			{
				_audioPlaybackStatusTextBlock.Text = "Generate audio first.";
			}
			StatusTextBlock.Text = "No generated audio to play";
			return;
		}
		try
		{
			_ttsPreviewPlayer.Stop();
			_ttsPreviewPlayer.Open(new Uri(path, UriKind.Absolute));
			_ttsPreviewPlayer.Play();
			if (_audioPlaybackStatusTextBlock != null)
			{
				_audioPlaybackStatusTextBlock.Text = "Playing: " + System.IO.Path.GetFileName(path);
			}
			StatusTextBlock.Text = "Playing generated audio";
		}
		catch (Exception exception)
		{
			AppLog.Warn("Could not play generated TTS audio.", exception);
			if (_audioPlaybackStatusTextBlock != null)
			{
				_audioPlaybackStatusTextBlock.Text = exception.Message;
			}
			StatusTextBlock.Text = "Audio playback failed";
		}
	}

	private void StopTtsPreview()
	{
		try
		{
			_ttsPreviewPlayer.Stop();
			_ttsPreviewPlayer.Close();
			if (_audioPlaybackStatusTextBlock != null)
			{
				_audioPlaybackStatusTextBlock.Text = File.Exists(_settings.TtsLastOutputPath)
					? "Stopped. Ready: " + System.IO.Path.GetFileName(_settings.TtsLastOutputPath)
					: "Stopped.";
			}
			StatusTextBlock.Text = "Audio playback stopped";
		}
		catch (Exception exception)
		{
			AppLog.Warn("Could not stop generated audio playback.", exception);
		}
	}

	private static string StableDirectoryName(string value)
	{
		string text = string.Join("-", new string(value.ToLowerInvariant().Select((char ch) => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Split('-', StringSplitOptions.RemoveEmptyEntries));
		return string.IsNullOrWhiteSpace(text) ? "voice" : text;
	}


	private void ModeButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
	{
		UpdateModeButtons();
	}

	private void SettingsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (sender is ScrollViewer scrollViewer)
		{
			e.Handled = true;
			double targetOffset = Math.Clamp(scrollViewer.VerticalOffset - (double)e.Delta * 0.42, 0.0, scrollViewer.ScrollableHeight);
			_settingsScrollCts?.Cancel();
			_settingsScrollCts?.Dispose();
			_settingsScrollCts = new CancellationTokenSource();
			SmoothScrollToOffsetAsync(scrollViewer, targetOffset, _settingsScrollCts.Token);
		}
	}

	private static async Task SmoothScrollToOffsetAsync(ScrollViewer viewer, double targetOffset, CancellationToken cancellationToken)
	{
		double startOffset = viewer.VerticalOffset;
		double distance = targetOffset - startOffset;
		if (Math.Abs(distance) < 0.5)
		{
			return;
		}
		try
		{
			for (int frame = 1; frame <= 9; frame++)
			{
				await Task.Delay(10, cancellationToken);
				double num = (double)frame / 9.0;
				double num2 = 1.0 - Math.Pow(1.0 - num, 3.0);
				viewer.ScrollToVerticalOffset(startOffset + distance * num2);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private void SelectMode(DictationMode mode)
	{
		_selectedMode = mode;
		ModePillTextBlock.Text = mode.Name;
		ModeInstructionTextBlock.Text = mode.Instruction;
		AnimateTextRefresh(ModePillTextBlock);
		AnimateTextRefresh(ModeInstructionTextBlock);
		UpdateModeButtons();
		UpdateShortcutWidgetState();
		RefreshTrayMenu();
		if (!string.IsNullOrWhiteSpace(RawTranscriptTextBox.Text) && !_isRecording && !_isTranscribing)
		{
			FormatCurrent(addToHistory: false);
			StatusTextBlock.Text = mode.Name + " ready";
		}
	}

	private void UpdateModeButtons()
	{
		foreach (System.Windows.Controls.Button item in ModesPanel.Children.OfType<System.Windows.Controls.Button>())
		{
			DictationMode mode = (DictationMode)item.Tag;
			bool flag = string.Equals(mode.Id, _selectedMode.Id, StringComparison.OrdinalIgnoreCase);
			item.Background = (flag ? ThemeBrushWithOpacity("PremiumSoftBrush", IsDarkTheme() ? 0.78 : 1.0) : ThemeBrushWithOpacity("PanelBrush", IsDarkTheme() ? 0.48 : 1.0));
			item.Foreground = ResourceBrush("InkBrush");
			item.BorderBrush = (flag ? ThemeBrushWithOpacity("GoldBrush", IsDarkTheme() ? 0.72 : 1.0) : ThemeBrushWithOpacity(IsDarkTheme() ? "GoldBrush" : "LineBrush", IsDarkTheme() ? 0.16 : 1.0));
			AnimateScale(item, 1.0, 180);
			List<TextBlock> list = FindVisualChildren<TextBlock>(item).ToList();
			if (list.Count > 0)
			{
				list[0].Foreground = (flag ? ResourceBrush("AccentBrush") : ResourceBrush("MutedBrush"));
			}
			if (list.Count > 1)
			{
				list[1].Foreground = ResourceBrush("InkBrush");
			}
			if (list.Count > 2)
			{
				list[2].Foreground = (flag ? ResourceBrush("InkBrush") : ResourceBrush("MutedBrush"));
			}
			if (list.Count > 3)
			{
				list[3].Foreground = (flag ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 20, 28)) : ResourceBrush("MutedBrush"));
			}
			List<Border> source = FindVisualChildren<Border>(item).ToList();
			Border border = source.FirstOrDefault((Border border3) => Math.Abs(border3.Width - 24.0) < 0.1);
			if (border != null)
			{
				border.Background = (flag ? ThemeBrushWithOpacity("ElevatedBrush", IsDarkTheme() ? 0.82 : 1.0) : ThemeBrushWithOpacity("SoftBrush", IsDarkTheme() ? 0.54 : 1.0));
				border.BorderBrush = (flag ? ThemeBrushWithOpacity("AccentBrush", IsDarkTheme() ? 0.70 : 1.0) : ThemeBrushWithOpacity("LineBrush", IsDarkTheme() ? 0.62 : 1.0));
			}
			Border border2 = source.FirstOrDefault((Border border3) => border3.Child is TextBlock textBlock && textBlock.Text.Equals(mode.Badge, StringComparison.OrdinalIgnoreCase));
			if (border2 != null)
			{
				border2.Background = (flag ? ThemeBrushWithOpacity("ElevatedBrush", IsDarkTheme() ? 0.82 : 1.0) : ThemeBrushWithOpacity("SoftBrush", IsDarkTheme() ? 0.54 : 1.0));
				border2.BorderBrush = (flag ? ThemeBrushWithOpacity("AccentBrush", IsDarkTheme() ? 0.70 : 1.0) : ThemeBrushWithOpacity("LineBrush", IsDarkTheme() ? 0.62 : 1.0));
			}
		}
	}

	private void TabButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button { Tag: string tag })
		{
			SetActiveTab(tag);
		}
	}

	private void SetActiveTab(TabId tab)
	{
		SetActiveTab(tab.ToIdString());
	}

	private void SetActiveTab(string tab)
	{
		_activeTab = tab;
		DictatePage.Visibility = ((!(tab == "dictate")) ? Visibility.Collapsed : Visibility.Visible);
		HistoryPage.Visibility = ((!(tab == "history")) ? Visibility.Collapsed : Visibility.Visible);
		VoiceProfilePage.Visibility = ((!(tab == "profile")) ? Visibility.Collapsed : Visibility.Visible);
		DictionaryPage.Visibility = ((!(tab == "dictionary")) ? Visibility.Collapsed : Visibility.Visible);
		if (_audioPage != null)
		{
			_audioPage.Visibility = ((!(tab == "audio")) ? Visibility.Collapsed : Visibility.Visible);
		}
		SettingsPage.Visibility = ((!(tab == "settings")) ? Visibility.Collapsed : Visibility.Visible);
		TextBlock headerTitleTextBlock = HeaderTitleTextBlock;
		headerTitleTextBlock.Text = tab switch
		{
			"history" => "History", 
			"profile" => "Voice Profile", 
			"dictionary" => "Dictionary", 
			"audio" => "🎙️ Audio Studio",
			"settings" => "Settings", 
			_ => "Speak", 
		};
		headerTitleTextBlock = HeaderSubtitleTextBlock;
		headerTitleTextBlock.Text = tab switch
		{
			"history" => "Browse every saved local transcript and reopen the messages worth refining.", 
			"profile" => "Your words, corrections, and accuracy in one quiet view.", 
			"dictionary" => "Teach Speak the words, names, products, and symbols it should always write correctly.", 
			"audio" => "Generate local voices through the CUDA worker, preview outputs, and keep models offloaded when idle.",
			"settings" => "Tune the local recorder, model, device, and appearance.", 
			_ => "Your voice, perfectly written.", 
		};
		UpdateTabButton(DictateTabButton, "dictate");
		UpdateTabButton(HistoryTabButton, "history");
		UpdateTabButton(ProfileTabButton, "profile");
		UpdateTabButton(DictionaryTabButton, "dictionary");
		UpdateTabButton(_audioTabButton, "audio");
		UpdateTabButton(SettingsTabButton, "settings");
		AnimatePageIn(tab switch
		{
			"history" => HistoryPage, 
			"profile" => VoiceProfilePage, 
			"dictionary" => DictionaryPage, 
			"audio" => _audioPage ?? SettingsPage,
			"settings" => SettingsPage, 
			_ => DictatePage, 
		});
	}

	private void UpdateTabButton(System.Windows.Controls.Button? button, string tab)
	{
		if (button == null)
		{
			return;
		}
		bool flag = string.Equals(_activeTab, tab, StringComparison.OrdinalIgnoreCase);
		if (flag)
		{
			button.Background = ResourceBrush("PremiumSoftBrush");
			button.BorderBrush = ResourceBrush("AccentBrush");
			button.Foreground = ResourceBrush("GoldBrush");
		}
		else
		{
			button.Background = ResourceBrush("PanelBrush");
			button.BorderBrush = ResourceBrush("LineBrush");
			button.Foreground = ResourceBrush("InkBrush");
		}
		if (string.Equals(tab, "audio", StringComparison.OrdinalIgnoreCase))
		{
			button.Content = CreateAudioSidebarLogoContent(flag);
			button.ToolTip = "🎙️ Audio Studio";
			button.Padding = new Thickness(8.0);
		}
		AnimateScale(button, flag ? 1.015 : 1.0, 160);
	}

	private void UpdateLibraryStats()
	{
		int value = CountVisibleHistoryItems();
		int count = _history.Count;
		HistoryCountTextBlock.Text = ((!string.IsNullOrWhiteSpace(HistorySearchTextBox.Text)) ? $"{value}/{count} shown" : ((count == 1) ? "1 saved" : $"{count} saved"));
		int num = _vocabulary.Count((VocabularyEntry entry) => !string.IsNullOrWhiteSpace(entry.Spoken) || !string.IsNullOrWhiteSpace(entry.Written));
		DictionaryCountTextBlock.Text = ((num == 1) ? "1 term" : $"{num} terms");
		TranscriptStats transcriptStats = GetCachedTranscriptStats();
		WordsSpokenTextBlock.Text = transcriptStats.SpokenWordLabel + " / " + transcriptStats.TodaySpokenWordLabel;
		VoiceStatsTextBlock.Text = transcriptStats.VoiceStatsLabel;
		DictateWordsSpokenTextBlock.Text = transcriptStats.SpokenWordCount.ToString("N0");
		DictateTodayWordsTextBlock.Text = transcriptStats.TodaySpokenWordLabel;
		DictateVoiceStatsTextBlock.Text = transcriptStats.TodaySessionLabel + " / " + transcriptStats.ActiveStreakLabel;
		UpdateVoiceProfile();
		RefreshLearnedCorrectionsReview();
		UpdateEmptyStatePolish();
	}

	private void UpdateVoiceProfile()
	{
		VoiceProfileStats voiceProfileStats = GetCachedVoiceProfileStats();
		ProfileWordsSpokenTextBlock.Text = voiceProfileStats.SpokenWordCount.ToString("N0");
		ProfileTodayWordsTextBlock.Text = voiceProfileStats.TodayLabel;
		ProfileSavedCorrectionsTextBlock.Text = voiceProfileStats.SavedCorrections.ToString("N0");
		ProfileAutoLearnedTextBlock.Text = voiceProfileStats.AutoLearnedLabel;
		ProfileAccuracyTextBlock.Text = voiceProfileStats.AccuracyLabel;
		ProfileSessionsTextBlock.Text = voiceProfileStats.SessionLabel;
		ProfileAverageTextBlock.Text = ((voiceProfileStats.AverageWordsPerTranscript == 1) ? "1 avg word" : $"{voiceProfileStats.AverageWordsPerTranscript:N0} avg words");
		ProfileStreakTextBlock.Text = voiceProfileStats.StreakLabel;
		ProfileLearningTextBlock.Text = ((voiceProfileStats.SavedCorrections == 0) ? "No saved corrections yet" : (voiceProfileStats.SavedCorrectionsLabel + "; " + voiceProfileStats.AutoLearnedLabel + "."));
	}

	private int CountVisibleHistoryItems()
	{
		if (string.IsNullOrWhiteSpace(HistorySearchTextBox.Text))
		{
			return _history.Count;
		}
		return _historyView.Cast<object>().Count();
	}

	private TranscriptStats GetCachedTranscriptStats()
	{
		int statsVersion = Volatile.Read(ref _statsVersion);
		if (_cachedTranscriptStats == null || _cachedTranscriptStatsVersion != statsVersion)
		{
			_cachedTranscriptStats = TranscriptStats.FromHistory(_history);
			_cachedTranscriptStatsVersion = statsVersion;
		}
		return _cachedTranscriptStats;
	}

	private VoiceProfileStats GetCachedVoiceProfileStats()
	{
		int statsVersion = Volatile.Read(ref _statsVersion);
		if (_cachedVoiceProfileStats == null || _cachedVoiceProfileStatsVersion != statsVersion)
		{
			_cachedVoiceProfileStats = VoiceProfileStats.From(_history, _vocabulary);
			_cachedVoiceProfileStatsVersion = statsVersion;
		}
		return _cachedVoiceProfileStats;
	}

	private void InvalidateStatsCache()
	{
		Interlocked.Increment(ref _statsVersion);
		_cachedTranscriptStats = null;
		_cachedVoiceProfileStats = null;
	}

	private void RefreshLearnedCorrectionsReview()
	{
		List<VocabularyEntry> list = (from entry in _vocabulary
			where entry.Source.Equals("auto", StringComparison.OrdinalIgnoreCase) && entry.LearnedCount > 0 && !string.IsNullOrWhiteSpace(entry.Spoken) && !string.IsNullOrWhiteSpace(entry.Written)
			orderby entry.UpdatedAt descending
			select entry).Take(6).ToList();
		_learnedCorrections.Clear();
		foreach (VocabularyEntry item in list)
		{
			_learnedCorrections.Add(item);
		}
		LearnedCorrectionsSummaryTextBlock.Text = ((list.Count == 0) ? "No auto-learned corrections waiting." : ((list.Count == 1) ? "1 correction is waiting for review." : $"{list.Count} corrections are waiting for review."));
	}

	private void UpdateHistorySelectionDetails()
	{
		if (HistoryListBox.SelectedItem is TranscriptCard transcriptCard)
		{
			HistorySelectedTextBox.Text = transcriptCard.FormattedText;
			HistoryRawTextBox.Text = transcriptCard.RawText;
			HistoryTagsTextBox.Text = transcriptCard.Tags;
			HistoryComparisonTextBox.Text = (string.IsNullOrWhiteSpace(transcriptCard.AudioPath) ? "This saved item has no audio path, so Speak can re-polish the text but cannot re-transcribe it with another STT model." : ("Ready to retry saved audio with the current model.\n\nSaved source: " + transcriptCard.SourceLabel + "\nAudio: " + System.IO.Path.GetFileName(transcriptCard.AudioPath)));
			_lastRetryRawText = "";
			_lastRetryFormattedText = "";
			_lastRetrySourceLabel = "";
		}
		else
		{
			HistorySelectedTextBox.Text = ((_history.Count == 0) ? "No saved transcripts yet." : "Select a transcript to read the full saved output.");
			HistoryRawTextBox.Text = ((_history.Count == 0) ? "Record or polish text with history enabled to fill this archive." : "Raw speech text appears here.");
			HistoryComparisonTextBox.Text = ((_history.Count == 0) ? "Retry comparisons will appear here after saved audio exists." : "Retry results appear here.");
			HistoryTagsTextBox.Text = "";
			_lastRetryRawText = "";
			_lastRetryFormattedText = "";
			_lastRetrySourceLabel = "";
		}
		UpdateEmptyStatePolish();
		ApplyHistoryDetailFinishing();
	}

	private void ApplyPremiumRuntimePolish()
	{
		try
		{
			ApplyPremiumTypography();
			ApplyCompactWorkspaceSpacing();
			ApplyModeCardFinishing();
			ApplyHistoryDetailFinishing();
			ApplyDictionaryReviewPolish();
			ApplyVoiceProfilePolish();
			ApplyEmptyStatePolish();
			ApplyMutedDarkContrast();
			RemoveHeaderLogoMark();
			ApplyHeaderEditorialVisual();
			ApplyGlassSurfacePolish();
			ApplyRecordingMicrocopy();
			ApplyEditorialVisualSystem();
			ApplyEditorialMotionPass();
		}
		catch (Exception exception)
		{
			AppLog.Warn("Runtime UI polish failed.", exception);
		}
	}

	private void ApplyPremiumTypography()
	{
		System.Windows.Media.FontFamily fontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Display, Segoe UI");
		System.Windows.Media.FontFamily fontFamily2 = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI");
		base.FontFamily = fontFamily2;
		TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
		TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
		HeaderTitleTextBlock.FontFamily = fontFamily;
		HeaderTitleTextBlock.FontWeight = FontWeights.SemiBold;
		HeaderTitleTextBlock.FontStretch = FontStretches.Normal;
		HeaderSubtitleTextBlock.FontFamily = fontFamily2;
		HeaderSubtitleTextBlock.FontSize = 12.0;
		HeaderSubtitleTextBlock.FontWeight = FontWeights.Normal;
		ModePillTextBlock.FontFamily = fontFamily;
		ModePillTextBlock.FontWeight = FontWeights.SemiBold;
		StatusTextBlock.FontSize = 12.0;
		StatusTextBlock.FontWeight = FontWeights.Normal;
		RecordingStatusTextBlock.FontWeight = FontWeights.Normal;
		DictateWordsSpokenTextBlock.FontFamily = fontFamily;
		ProfileWordsSpokenTextBlock.FontFamily = fontFamily;
		ProfileAccuracyTextBlock.FontFamily = fontFamily;
	}

	private void ApplyEditorialMotionPass()
	{
		if (_editorialMotionApplied || !base.IsLoaded)
		{
			return;
		}
		_editorialMotionApplied = true;
		AnimateEditorialSlide(HeaderTitleTextBlock, 0);
		AnimateEditorialSlide(HeaderSubtitleTextBlock, 45);
		AnimateEditorialSlide(ModeInstructionTextBlock, 90);
		AnimateEditorialOpacity(DictateWordCounterPanel, 120);
		int delayMs = 150;
		foreach (System.Windows.Controls.Button item in ModesPanel.Children.OfType<System.Windows.Controls.Button>())
		{
			AnimateEditorialOpacity(item, delayMs);
			delayMs += 35;
		}
	}

	private void ApplyEditorialVisualSystem()
	{
		InstallEditorialVisualLayer(DictatePage, "dictate", 0.48);
		InstallEditorialVisualLayer(HistoryPage, "history", 0.46);
		InstallEditorialVisualLayer(VoiceProfilePage, "profile", 0.48);
		InstallEditorialVisualLayer(DictionaryPage, "dictionary", 0.46);
		if (_audioPage != null)
		{
			InstallEditorialVisualLayer(_audioPage, "audio", 0.34);
		}
		InstallEditorialVisualLayer(SettingsPage, "settings", 0.40);
		InstallEditorialVisualLayer(FindBorderContainingText(DictatePage, "Speak naturally"), "dictate-hero", 0.58);
		InstallEditorialVisualLayer(FindBorderContainingText(HistoryPage, "Saved messages"), "history-card", 0.54);
		InstallEditorialVisualLayer(FindBorderContainingText(VoiceProfilePage, "Spoken"), "profile-card", 0.56);
		InstallEditorialVisualLayer(DictionaryHeroPanel, "dictionary-hero", 0.74);
		InstallEditorialVisualLayer(FindBorderContainingText(SettingsPage, "Whisper runtime"), "settings-card", 0.50);
	}

	private void InstallEditorialVisualLayer(System.Windows.Controls.Panel panel, string kind, double opacity)
	{
		if (panel == null)
		{
			return;
		}
		Canvas canvas = panel.Children.OfType<Canvas>().FirstOrDefault((Canvas child) => string.Equals(child.Tag as string, "SpeakEditorialVisualLayer:" + kind, StringComparison.Ordinal));
		if (canvas == null)
		{
			canvas = CreateEditorialCanvas(kind, opacity);
			System.Windows.Controls.Panel.SetZIndex(canvas, -20);
			Grid.SetRowSpan(canvas, 99);
			Grid.SetColumnSpan(canvas, 99);
			panel.Children.Insert(0, canvas);
			canvas.SizeChanged += delegate(object sender, SizeChangedEventArgs e)
			{
				if (sender is Canvas target)
				{
					BuildEditorialVisualLayer(target, kind, e.NewSize.Width, e.NewSize.Height);
				}
			};
		}
		canvas.Opacity = opacity;
		BuildEditorialVisualLayer(canvas, kind, panel.ActualWidth, panel.ActualHeight);
	}

	private void InstallEditorialVisualLayer(Border border, string kind, double opacity)
	{
		if (border == null)
		{
			return;
		}
		Grid grid;
		if (border.Child is Grid existingGrid)
		{
			grid = existingGrid;
		}
		else if (border.Child is UIElement existingChild)
		{
			grid = new Grid();
			border.Child = null;
			grid.Children.Add(existingChild);
			border.Child = grid;
		}
		else
		{
			grid = new Grid();
			border.Child = grid;
		}
		border.ClipToBounds = true;
		InstallEditorialVisualLayer(grid, kind, opacity);
	}

	private Canvas CreateEditorialCanvas(string kind, double opacity)
	{
		return new Canvas
		{
			Tag = "SpeakEditorialVisualLayer:" + kind,
			IsHitTestVisible = false,
			Focusable = false,
			ClipToBounds = true,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Stretch,
			Opacity = opacity
		};
	}

	private void BuildEditorialVisualLayer(Canvas canvas, string kind, double width, double height)
	{
		if (width < 24.0 || height < 24.0)
		{
			return;
		}
		canvas.Children.Clear();
		bool isDictateLayer = kind.StartsWith("dictate", StringComparison.OrdinalIgnoreCase);
		System.Windows.Media.Brush gridBrush = ThemeBrushWithOpacity("MutedBrush", isDictateLayer ? 0.055 : 0.10);
		System.Windows.Media.Brush lineBrush = ThemeBrushWithOpacity("GoldBrush", isDictateLayer ? 0.20 : 0.30);
		System.Windows.Media.Brush strongBrush = ThemeBrushWithOpacity("GoldBrush", isDictateLayer ? 0.42 : 0.56);
		System.Windows.Media.Brush softBrush = ThemeBrushWithOpacity("GoldBrush", isDictateLayer ? 0.052 : 0.09);
		System.Windows.Media.Brush dustBrush = ThemeBrushWithOpacity("InkBrush", isDictateLayer ? 0.045 : 0.08);
		AddEditorialGrid(canvas, width, height, gridBrush);
		AddEditorialDust(canvas, width, height, dustBrush, kind.GetHashCode());
		switch (kind)
		{
		case "dictate":
			AddCleanSonicLensGlyph(canvas, width * 0.68, height * 0.52, Math.Min(width, height) * 0.18, lineBrush, strongBrush, softBrush);
			AddBezierWave(canvas, width * 0.43, height * 0.50, width * 0.31, Math.Min(42.0, height * 0.12), strongBrush, 1.05);
			AddTranscriptRibbonGlyph(canvas, width * 0.16, height * 0.78, width * 0.42, Math.Min(52.0, height * 0.13), lineBrush, strongBrush);
			AddEditorialLine(canvas, width * 0.22, height * 0.26, width * 0.62, height * 0.66, lineBrush, 0.55);
			break;
		case "dictate-hero":
			AddCleanSonicLensGlyph(canvas, width * 0.66, height * 0.50, Math.Min(width, height) * 0.22, lineBrush, strongBrush, softBrush);
			AddBezierWave(canvas, width * 0.38, height * 0.52, width * 0.28, Math.Min(34.0, height * 0.18), strongBrush, 1.35);
			AddCalibrationTicks(canvas, width * 0.42, height * 0.52, width * 0.20, Math.Min(28.0, height * 0.14), lineBrush);
			break;
		case "history":
		case "history-card":
			AddTranscriptMapGlyph(canvas, width, height, lineBrush, strongBrush, softBrush);
			AddSonicLensGlyph(canvas, width * 0.84, height * 0.24, Math.Min(width, height) * 0.17, lineBrush, strongBrush, softBrush);
			break;
		case "profile":
		case "profile-card":
			AddVoiceFingerprintGlyph(canvas, width * 0.31, height * 0.48, Math.Min(width, height) * 0.36, lineBrush, strongBrush, softBrush);
			AddBezierWave(canvas, width * 0.56, height * 0.36, width * 0.32, Math.Min(58.0, height * 0.20), strongBrush, 1.6);
			AddEditorialLine(canvas, width * 0.36, height * 0.62, width * 0.84, height * 0.26, lineBrush, 0.9);
			break;
		case "dictionary":
		case "dictionary-hero":
			AddLexiconAtlasGlyph(canvas, width, height, lineBrush, strongBrush, softBrush);
			AddBezierWave(canvas, width * 0.66, height * 0.72, width * 0.24, Math.Min(44.0, height * 0.18), lineBrush, 1.2);
			break;
		default:
			AddSonicLensGlyph(canvas, width * 0.82, height * 0.28, Math.Min(width, height) * 0.20, lineBrush, strongBrush, softBrush);
			AddTranscriptRibbonGlyph(canvas, width * 0.22, height * 0.72, width * 0.42, Math.Min(42.0, height * 0.18), lineBrush, strongBrush);
			AddEditorialLine(canvas, width * 0.18, height * 0.76, width * 0.84, height * 0.18, lineBrush, 0.8);
			break;
		}
	}

	private Border FindBorderContainingText(DependencyObject root, string text)
	{
		foreach (TextBlock item in FindVisualChildren<TextBlock>(root))
		{
			if (!string.IsNullOrWhiteSpace(item.Text) && item.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return FindVisualParent<Border>(item);
			}
		}
		return null;
	}

	private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
	{
		DependencyObject dependencyObject = child;
		while (dependencyObject != null)
		{
			dependencyObject = VisualTreeHelper.GetParent(dependencyObject);
			if (dependencyObject is T result)
			{
				return result;
			}
		}
		return null;
	}

	private SolidColorBrush ThemeBrushWithOpacity(string key, double opacity)
	{
		SolidColorBrush solidColorBrush = ResourceBrush(key).Clone();
		solidColorBrush.Opacity = opacity;
		if (solidColorBrush.CanFreeze)
		{
			solidColorBrush.Freeze();
		}
		return solidColorBrush;
	}

	private static void AddEditorialGrid(Canvas canvas, double width, double height, System.Windows.Media.Brush brush)
	{
		double spacing = Math.Max(56.0, Math.Min(width, height) / 5.0);
		for (double x = spacing; x < width; x += spacing)
		{
			AddEditorialLine(canvas, x, 0.0, x + height * 0.18, height, brush, 0.6);
		}
		for (double y = spacing * 0.8; y < height; y += spacing * 0.8)
		{
			AddEditorialLine(canvas, 0.0, y, width, y + width * 0.035, brush, 0.6);
		}
	}

	private static void AddEditorialDust(Canvas canvas, double width, double height, System.Windows.Media.Brush brush, int seed)
	{
		Random random = new Random(seed);
		int count = (int)Math.Clamp(width * height / 14000.0, 16.0, 60.0);
		for (int i = 0; i < count; i++)
		{
			System.Windows.Shapes.Rectangle rectangle = new System.Windows.Shapes.Rectangle
			{
				Width = 1.0,
				Height = 1.0,
				Fill = brush
			};
			Canvas.SetLeft(rectangle, random.NextDouble() * width);
			Canvas.SetTop(rectangle, random.NextDouble() * height);
			canvas.Children.Add(rectangle);
		}
	}

	private static void AddOrbitGlyph(Canvas canvas, double centerX, double centerY, double radius, System.Windows.Media.Brush lineBrush, System.Windows.Media.Brush dotBrush)
	{
		AddEllipse(canvas, centerX, centerY, radius * 1.55, radius * 0.62, lineBrush, 1.0, -18.0);
		AddEllipse(canvas, centerX, centerY, radius * 1.02, radius * 1.02, lineBrush, 0.9, 0.0);
		AddEllipse(canvas, centerX, centerY, radius * 0.45, radius * 0.45, lineBrush, 0.8, 0.0);
		AddEditorialLine(canvas, centerX - radius * 1.05, centerY, centerX + radius * 1.05, centerY, lineBrush, 0.7);
		AddEditorialLine(canvas, centerX, centerY - radius * 0.88, centerX, centerY + radius * 0.88, lineBrush, 0.7);
		for (int i = 0; i < 5; i++)
		{
			double angle = (-40.0 + i * 38.0) * Math.PI / 180.0;
			AddDot(canvas, centerX + Math.Cos(angle) * radius * 0.78, centerY + Math.Sin(angle) * radius * 0.36, 3.2 + i % 2, dotBrush);
		}
	}

	private static void AddWaveGlyph(Canvas canvas, double startX, double centerY, double width, double height, System.Windows.Media.Brush brush)
	{
		Polyline polyline = new Polyline
		{
			Stroke = brush,
			StrokeThickness = 1.4,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round,
			StrokeLineJoin = PenLineJoin.Round
		};
		int points = 42;
		for (int i = 0; i <= points; i++)
		{
			double progress = (double)i / points;
			double x = startX + progress * width;
			double envelope = Math.Sin(progress * Math.PI);
			double y = centerY + Math.Sin(progress * Math.PI * 8.0) * height * 0.5 * envelope;
			polyline.Points.Add(new System.Windows.Point(x, y));
		}
		canvas.Children.Add(polyline);
		for (int j = 0; j < 5; j++)
		{
			double x2 = startX + width * (0.17 + j * 0.16);
			AddEditorialLine(canvas, x2, centerY - height * 0.34, x2, centerY + height * 0.34, brush, 0.7);
		}
	}

	private static void AddTimelineGlyph(Canvas canvas, double width, double height, System.Windows.Media.Brush lineBrush, System.Windows.Media.Brush dotBrush)
	{
		double y = height * 0.63;
		AddEditorialLine(canvas, width * 0.14, y, width * 0.88, y, lineBrush, 1.1);
		for (int i = 0; i < 6; i++)
		{
			double x = width * (0.18 + i * 0.12);
			AddDot(canvas, x, y, (i == 4) ? 5.2 : 3.5, dotBrush);
			AddEditorialLine(canvas, x, y - height * 0.11, x, y + height * 0.08, lineBrush, 0.7);
		}
	}

	private static void AddConstellationGlyph(Canvas canvas, double width, double height, System.Windows.Media.Brush lineBrush, System.Windows.Media.Brush dotBrush)
	{
		System.Windows.Point[] points = new System.Windows.Point[6]
		{
			new System.Windows.Point(width * 0.18, height * 0.32),
			new System.Windows.Point(width * 0.32, height * 0.22),
			new System.Windows.Point(width * 0.48, height * 0.45),
			new System.Windows.Point(width * 0.62, height * 0.30),
			new System.Windows.Point(width * 0.78, height * 0.52),
			new System.Windows.Point(width * 0.88, height * 0.26)
		};
		for (int i = 0; i < points.Length - 1; i++)
		{
			AddEditorialLine(canvas, points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y, lineBrush, 0.9);
		}
		foreach (System.Windows.Point point in points)
		{
			AddDot(canvas, point.X, point.Y, 4.0, dotBrush);
		}
		AddOrbitGlyph(canvas, width * 0.70, height * 0.68, Math.Min(width, height) * 0.16, lineBrush, dotBrush);
	}

	private static void AddSonicLensGlyph(Canvas canvas, double centerX, double centerY, double radius, System.Windows.Media.Brush lineBrush, System.Windows.Media.Brush strongBrush, System.Windows.Media.Brush softBrush)
	{
		radius = Math.Max(12.0, radius);
		Ellipse wash = new Ellipse
		{
			Width = radius * 2.45,
			Height = radius * 1.42,
			Fill = softBrush,
			RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
			RenderTransform = new RotateTransform(-18.0)
		};
		Canvas.SetLeft(wash, centerX - wash.Width / 2.0);
		Canvas.SetTop(wash, centerY - wash.Height / 2.0);
		canvas.Children.Add(wash);
		AddEllipse(canvas, centerX, centerY, radius * 2.05, radius * 0.82, lineBrush, 1.0, -18.0);
		AddEllipse(canvas, centerX, centerY, radius * 1.48, radius * 1.48, lineBrush, 0.9, 0.0);
		AddEllipse(canvas, centerX, centerY, radius * 0.82, radius * 0.82, lineBrush, 0.8, 0.0);
		AddEditorialArc(canvas, centerX, centerY, radius * 1.22, radius * 1.22, -35.0, 112.0, strongBrush, 1.9);
		AddEditorialArc(canvas, centerX, centerY, radius * 1.67, radius * 0.62, 156.0, 88.0, lineBrush, 1.1);
		AddBezierWave(canvas, centerX - radius * 0.62, centerY, radius * 1.24, radius * 0.38, strongBrush, 1.4);
		for (int i = 0; i < 7; i++)
		{
			double offset = (i - 3.0) * radius * 0.16;
			double barHeight = radius * (0.34 + Math.Sin((i + 1.0) * 1.36) * 0.11);
			AddEditorialLine(canvas, centerX + offset, centerY - barHeight / 2.0, centerX + offset, centerY + barHeight / 2.0, (i == 3) ? strongBrush : lineBrush, (i == 3) ? 2.0 : 1.35);
		}
		for (int j = 0; j < 10; j++)
		{
			double degrees = -150.0 + j * 31.0;
			double angle = degrees * Math.PI / 180.0;
			double outer = (j % 3 == 0) ? radius * 1.10 : radius * 0.98;
			AddDot(canvas, centerX + Math.Cos(angle) * outer, centerY + Math.Sin(angle) * outer, (j % 3 == 0) ? 3.9 : 2.7, (j % 4 == 0) ? strongBrush : lineBrush);
		}
	}

	private static void AddCleanSonicLensGlyph(Canvas canvas, double centerX, double centerY, double radius, System.Windows.Media.Brush lineBrush, System.Windows.Media.Brush strongBrush, System.Windows.Media.Brush softBrush)
	{
		radius = Math.Max(18.0, radius);
		Ellipse wash = new Ellipse
		{
			Width = radius * 2.15,
			Height = radius * 1.20,
			Fill = softBrush,
			RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
			RenderTransform = new RotateTransform(-16.0)
		};
		Canvas.SetLeft(wash, centerX - wash.Width / 2.0);
		Canvas.SetTop(wash, centerY - wash.Height / 2.0);
		canvas.Children.Add(wash);
		AddEllipse(canvas, centerX, centerY, radius * 1.82, radius * 0.66, lineBrush, 0.86, -16.0);
		AddEllipse(canvas, centerX, centerY, radius * 1.04, radius * 1.04, lineBrush, 0.78, 0.0);
		AddEditorialArc(canvas, centerX, centerY, radius * 1.15, radius * 0.44, 188.0, 126.0, lineBrush, 0.74);
		AddBezierWave(canvas, centerX - radius * 0.58, centerY, radius * 1.16, radius * 0.30, strongBrush, 1.12);
		for (int i = 0; i < 5; i++)
		{
			double offset = (i - 2.0) * radius * 0.17;
			double barHeight = radius * (0.22 + Math.Abs(i - 2.0) * 0.025);
			AddEditorialLine(canvas, centerX + offset, centerY - barHeight / 2.0, centerX + offset, centerY + barHeight / 2.0, (i == 2) ? strongBrush : lineBrush, (i == 2) ? 1.24 : 0.88);
		}
	}

	private static void AddTranscriptRibbonGlyph(Canvas canvas, double startX, double centerY, double width, double height, System.Windows.Media.Brush lineBrush, System.Windows.Media.Brush strongBrush)
	{
		width = Math.Max(28.0, width);
		height = Math.Max(14.0, height);
		for (int i = 0; i < 4; i++)
		{
			double progress = i / 3.0;
			double y = centerY - height * 0.44 + progress * height * 0.31;
			double x1 = startX + width * (0.04 + i * 0.035);
			double x2 = startX + width * (0.92 - i * 0.05);
			AddEditorialLine(canvas, x1, y, x2, y + Math.Sin(i + 0.8) * height * 0.035, (i == 1) ? strongBrush : lineBrush, (i == 1) ? 1.8 : 1.0);
			AddDot(canvas, x1 - 9.0, y, (i == 1) ? 3.8 : 2.6, (i == 1) ? strongBrush : lineBrush);
		}
		AddBezierWave(canvas, startX + width * 0.08, centerY + height * 0.30, width * 0.78, height * 0.28, strongBrush, 1.25);
		for (int j = 0; j < 5; j++)
		{
			double x = startX + width * (0.18 + j * 0.15);
			AddEditorialLine(canvas, x, centerY + height * 0.08, x + height * 0.08, centerY + height * 0.40, lineBrush, 0.7);
		}
	}

	private static void AddBezierWave(Canvas canvas, double startX, double centerY, double width, double height, System.Windows.Media.Brush brush, double thickness)
	{
		if (width <= 8.0 || height <= 4.0)
		{
			return;
		}
		PathFigure figure = new PathFigure
		{
			StartPoint = new System.Windows.Point(startX, centerY)
		};
		double segment = width / 3.0;
		figure.Segments.Add(new BezierSegment(new System.Windows.Point(startX + segment * 0.32, centerY - height * 0.60), new System.Windows.Point(startX + segment * 0.68, centerY + height * 0.60), new System.Windows.Point(startX + segment, centerY), true));
		figure.Segments.Add(new BezierSegment(new System.Windows.Point(startX + segment * 1.33, centerY - height * 0.70), new System.Windows.Point(startX + segment * 1.68, centerY + height * 0.70), new System.Windows.Point(startX + segment * 2.0, centerY), true));
		figure.Segments.Add(new BezierSegment(new System.Windows.Point(startX + segment * 2.34, centerY - height * 0.58), new System.Windows.Point(startX + segment * 2.70, centerY + height * 0.48), new System.Windows.Point(startX + width, centerY), true));
		PathGeometry geometry = new PathGeometry();
		geometry.Figures.Add(figure);
		canvas.Children.Add(new System.Windows.Shapes.Path
		{
			Data = geometry,
			Stroke = brush,
			StrokeThickness = thickness,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round,
			StrokeLineJoin = PenLineJoin.Round
		});
	}

	private static void AddCalibrationTicks(Canvas canvas, double startX, double centerY, double width, double height, System.Windows.Media.Brush brush)
	{
		int count = 13;
		for (int i = 0; i < count; i++)
		{
			double progress = (double)i / (count - 1);
			double x = startX + width * progress;
			double envelope = 0.26 + Math.Sin(progress * Math.PI) * 0.62;
			double tickHeight = height * envelope * (0.52 + (i % 3) * 0.11);
			AddEditorialLine(canvas, x, centerY - tickHeight / 2.0, x, centerY + tickHeight / 2.0, brush, (i % 4 == 0) ? 1.05 : 0.65);
		}
	}

	private static void AddEditorialArc(Canvas canvas, double centerX, double centerY, double radiusX, double radiusY, double startDegrees, double sweepDegrees, System.Windows.Media.Brush stroke, double thickness)
	{
		if (radiusX <= 2.0 || radiusY <= 2.0 || Math.Abs(sweepDegrees) < 1.0)
		{
			return;
		}
		double startRadians = startDegrees * Math.PI / 180.0;
		double endRadians = (startDegrees + sweepDegrees) * Math.PI / 180.0;
		System.Windows.Point start = new System.Windows.Point(centerX + Math.Cos(startRadians) * radiusX, centerY + Math.Sin(startRadians) * radiusY);
		System.Windows.Point end = new System.Windows.Point(centerX + Math.Cos(endRadians) * radiusX, centerY + Math.Sin(endRadians) * radiusY);
		PathFigure figure = new PathFigure
		{
			StartPoint = start
		};
		figure.Segments.Add(new ArcSegment
		{
			Point = end,
			Size = new System.Windows.Size(radiusX, radiusY),
			IsLargeArc = Math.Abs(sweepDegrees) > 180.0,
			SweepDirection = sweepDegrees >= 0.0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise
		});
		PathGeometry geometry = new PathGeometry();
		geometry.Figures.Add(figure);
		canvas.Children.Add(new System.Windows.Shapes.Path
		{
			Data = geometry,
			Stroke = stroke,
			StrokeThickness = thickness,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round
		});
	}

	private static void AddTranscriptMapGlyph(Canvas canvas, double width, double height, System.Windows.Media.Brush lineBrush, System.Windows.Media.Brush strongBrush, System.Windows.Media.Brush softBrush)
	{
		AddTranscriptRibbonGlyph(canvas, width * 0.16, height * 0.34, width * 0.58, Math.Min(62.0, height * 0.22), lineBrush, strongBrush);
		AddEditorialArc(canvas, width * 0.42, height * 0.62, width * 0.30, height * 0.22, 190.0, 118.0, lineBrush, 1.0);
		AddEditorialArc(canvas, width * 0.62, height * 0.50, width * 0.22, height * 0.18, -42.0, 132.0, strongBrush, 1.3);
		System.Windows.Point[] nodes =
		{
			new System.Windows.Point(width * 0.24, height * 0.63),
			new System.Windows.Point(width * 0.38, height * 0.54),
			new System.Windows.Point(width * 0.52, height * 0.68),
			new System.Windows.Point(width * 0.68, height * 0.45),
			new System.Windows.Point(width * 0.80, height * 0.58)
		};
		for (int i = 0; i < nodes.Length - 1; i++)
		{
			AddEditorialLine(canvas, nodes[i].X, nodes[i].Y, nodes[i + 1].X, nodes[i + 1].Y, lineBrush, 0.8);
		}
		foreach (System.Windows.Point node in nodes)
		{
			AddDot(canvas, node.X, node.Y, 4.4, strongBrush);
		}
		Ellipse field = new Ellipse
		{
			Width = width * 0.20,
			Height = height * 0.28,
			Fill = softBrush
		};
		Canvas.SetLeft(field, width * 0.70);
		Canvas.SetTop(field, height * 0.18);
		canvas.Children.Add(field);
	}

	private static void AddVoiceFingerprintGlyph(Canvas canvas, double centerX, double centerY, double radius, System.Windows.Media.Brush lineBrush, System.Windows.Media.Brush strongBrush, System.Windows.Media.Brush softBrush)
	{
		radius = Math.Max(16.0, radius);
		Ellipse field = new Ellipse
		{
			Width = radius * 2.1,
			Height = radius * 1.55,
			Fill = softBrush,
			RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
			RenderTransform = new RotateTransform(12.0)
		};
		Canvas.SetLeft(field, centerX - field.Width / 2.0);
		Canvas.SetTop(field, centerY - field.Height / 2.0);
		canvas.Children.Add(field);
		for (int i = 0; i < 5; i++)
		{
			double factor = 0.42 + i * 0.20;
			AddEditorialArc(canvas, centerX, centerY, radius * factor * 1.34, radius * factor, -136.0 + i * 16.0, 248.0 - i * 18.0, (i == 2) ? strongBrush : lineBrush, (i == 2) ? 1.6 : 0.9);
		}
		for (int j = 0; j < 15; j++)
		{
			double angle = (-120.0 + j * 17.5) * Math.PI / 180.0;
			double inner = radius * 0.28;
			double outer = radius * (0.62 + (j % 4) * 0.06);
			AddEditorialLine(canvas, centerX + Math.Cos(angle) * inner, centerY + Math.Sin(angle) * inner, centerX + Math.Cos(angle) * outer, centerY + Math.Sin(angle) * outer, lineBrush, 0.65);
		}
		AddBezierWave(canvas, centerX - radius * 0.72, centerY, radius * 1.44, radius * 0.30, strongBrush, 1.35);
		AddDot(canvas, centerX, centerY, 5.0, strongBrush);
	}

	private static void AddLexiconAtlasGlyph(Canvas canvas, double width, double height, System.Windows.Media.Brush lineBrush, System.Windows.Media.Brush strongBrush, System.Windows.Media.Brush softBrush)
	{
		System.Windows.Point[] points =
		{
			new System.Windows.Point(width * 0.16, height * 0.30),
			new System.Windows.Point(width * 0.28, height * 0.20),
			new System.Windows.Point(width * 0.42, height * 0.36),
			new System.Windows.Point(width * 0.58, height * 0.25),
			new System.Windows.Point(width * 0.74, height * 0.42),
			new System.Windows.Point(width * 0.86, height * 0.26),
			new System.Windows.Point(width * 0.66, height * 0.64),
			new System.Windows.Point(width * 0.36, height * 0.70)
		};
		for (int i = 0; i < points.Length; i++)
		{
			System.Windows.Point current = points[i];
			System.Windows.Point next = points[(i + 1) % points.Length];
			if (i < points.Length - 1)
			{
				AddEditorialLine(canvas, current.X, current.Y, next.X, next.Y, lineBrush, 0.85);
			}
			AddDot(canvas, current.X, current.Y, (i == 2 || i == 5) ? 5.0 : 3.6, (i == 2 || i == 5) ? strongBrush : lineBrush);
		}
		AddEditorialArc(canvas, width * 0.52, height * 0.50, width * 0.36, height * 0.28, 204.0, 154.0, lineBrush, 0.9);
		AddEditorialArc(canvas, width * 0.52, height * 0.50, width * 0.23, height * 0.18, 22.0, 122.0, strongBrush, 1.3);
		for (int j = 0; j < 4; j++)
		{
			System.Windows.Shapes.Rectangle card = new System.Windows.Shapes.Rectangle
			{
				Width = Math.Max(18.0, width * 0.09),
				Height = Math.Max(5.0, height * 0.035),
				RadiusX = 2.0,
				RadiusY = 2.0,
				Fill = (j == 1) ? softBrush : null,
				Stroke = (j == 1) ? strongBrush : lineBrush,
				StrokeThickness = (j == 1) ? 1.1 : 0.7
			};
			Canvas.SetLeft(card, width * (0.22 + j * 0.14));
			Canvas.SetTop(card, height * (0.78 - j * 0.035));
			canvas.Children.Add(card);
		}
	}

	private static void AddEllipse(Canvas canvas, double centerX, double centerY, double width, double height, System.Windows.Media.Brush stroke, double thickness, double rotation)
	{
		Ellipse ellipse = new Ellipse
		{
			Width = width,
			Height = height,
			Stroke = stroke,
			StrokeThickness = thickness,
			RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
			RenderTransform = new RotateTransform(rotation)
		};
		Canvas.SetLeft(ellipse, centerX - width / 2.0);
		Canvas.SetTop(ellipse, centerY - height / 2.0);
		canvas.Children.Add(ellipse);
	}

	private static void AddDot(Canvas canvas, double centerX, double centerY, double size, System.Windows.Media.Brush fill)
	{
		Ellipse ellipse = new Ellipse
		{
			Width = size,
			Height = size,
			Fill = fill
		};
		Canvas.SetLeft(ellipse, centerX - size / 2.0);
		Canvas.SetTop(ellipse, centerY - size / 2.0);
		canvas.Children.Add(ellipse);
	}

	private static void AddEditorialLine(Canvas canvas, double x1, double y1, double x2, double y2, System.Windows.Media.Brush stroke, double thickness)
	{
		canvas.Children.Add(new Line
		{
			X1 = x1,
			Y1 = y1,
			X2 = x2,
			Y2 = y2,
			Stroke = stroke,
			StrokeThickness = thickness,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round
		});
	}

	private void ApplyCompactWorkspaceSpacing()
	{
		DictateWordCounterPanel.MinWidth = 132.0;
		DictateWordCounterPanel.Padding = new Thickness(14.0, 11.0, 14.0, 10.0);
		ActionBarPanel.Padding = new Thickness(10.0, 8.0, 10.0, 8.0);
		ModesPanel.Margin = new Thickness(0.0, 10.0, 0.0, 6.0);
		RawTranscriptTextBox.Padding = new Thickness(14.0);
		FormattedOutputTextBox.Padding = new Thickness(14.0);
		HistorySelectedTextBox.Padding = new Thickness(14.0);
		HistoryRawTextBox.Padding = new Thickness(14.0);
		HistoryComparisonTextBox.Padding = new Thickness(14.0);
		SettingsScrollViewer.PanningMode = PanningMode.VerticalOnly;
		SettingsScrollViewer.CanContentScroll = false;
	}

	private void ApplyModeCardFinishing()
	{
		ModesPanel.Rows = 1;
		ModesPanel.MinHeight = 50.0;
		ModesPanel.MaxHeight = 54.0;
		ModesPanel.ClipToBounds = false;
		if (ModesPanel.Parent is FrameworkElement frameworkElement)
		{
			frameworkElement.MinHeight = Math.Max(frameworkElement.MinHeight, 52.0);
			frameworkElement.ClipToBounds = false;
		}
		int modeIndex = 0;
		foreach (System.Windows.Controls.Button item in ModesPanel.Children.OfType<System.Windows.Controls.Button>())
		{
			item.Margin = new Thickness(modeIndex == 0 ? 2.0 : 0.0, 0.0, 8.0, 0.0);
			item.MinHeight = 46.0;
			item.MaxHeight = 48.0;
			item.Padding = new Thickness(12.0, 7.0, 12.0, 7.0);
			item.VerticalContentAlignment = VerticalAlignment.Center;
			ScaleTransform scaleTransform = EnsureScaleTransform(item);
			scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
			scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			scaleTransform.ScaleX = 1.0;
			scaleTransform.ScaleY = 1.0;
			if (item.Tag is DictationMode dictationMode)
			{
				item.ToolTip = dictationMode.Subtitle;
			}
			List<TextBlock> list = FindVisualChildren<TextBlock>(item).ToList();
			if (list.Count > 1)
			{
				list[1].FontSize = 12.5;
				list[1].LineHeight = 16.0;
				list[1].TextWrapping = TextWrapping.NoWrap;
				list[1].TextTrimming = TextTrimming.CharacterEllipsis;
			}
			if (list.Count > 2)
			{
				list[2].FontSize = 9.5;
				list[2].LineHeight = 11.0;
				list[2].MaxHeight = 12.0;
				list[2].TextWrapping = TextWrapping.NoWrap;
				list[2].TextTrimming = TextTrimming.CharacterEllipsis;
			}
			Border border = FindVisualChildren<Border>(item).FirstOrDefault((Border candidate) => Math.Abs(candidate.Width - 22.0) < 0.1 || Math.Abs(candidate.Width - 24.0) < 0.1);
			if (border != null)
			{
				border.Width = 22.0;
				border.Height = 22.0;
				border.Margin = new Thickness(0.0, 0.0, 9.0, 0.0);
				border.VerticalAlignment = VerticalAlignment.Center;
			}
			modeIndex++;
		}
	}

	private void ApplyHistoryDetailFinishing()
	{
		bool hasSelection = HistoryListBox != null && HistoryListBox.SelectedItem is TranscriptCard;
		ConfigureHistoryDetailTextBox(HistorySelectedTextBox, 58.0, hasSelection ? 138.0 : 78.0, Visibility.Visible);
		ConfigureHistoryDetailTextBox(HistoryRawTextBox, 52.0, 86.0, hasSelection ? Visibility.Visible : Visibility.Collapsed);
		ConfigureHistoryDetailTextBox(HistoryComparisonTextBox, 52.0, 86.0, hasSelection ? Visibility.Visible : Visibility.Collapsed);
		ConfigureHistoryDetailTextBox(HistoryTagsTextBox, 36.0, 48.0, hasSelection ? Visibility.Visible : Visibility.Collapsed);
		SetHistoryDetailLabelVisibility("Raw transcript", hasSelection);
		SetHistoryDetailLabelVisibility("Retry comparison", hasSelection);
		SetHistoryDetailLabelVisibility("Tags", hasSelection);
		foreach (System.Windows.Controls.Button button in FindVisualChildren<System.Windows.Controls.Button>(HistoryPage))
		{
			string text = ReadButtonText(button);
			if (IsSelectionHistoryAction(text))
			{
				button.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
			}
			if (IsHistoryAction(text))
			{
				button.MinHeight = 36.0;
				button.Padding = new Thickness(10.0, 7.0, 10.0, 7.0);
				button.FontSize = 12.0;
			}
		}
	}

	private static void ConfigureHistoryDetailTextBox(System.Windows.Controls.TextBox textBox, double minHeight, double maxHeight, Visibility visibility)
	{
		textBox.Visibility = visibility;
		textBox.Height = double.NaN;
		textBox.MinHeight = minHeight;
		textBox.MaxHeight = maxHeight;
		textBox.TextWrapping = TextWrapping.Wrap;
		textBox.AcceptsReturn = true;
		textBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
		textBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
	}

	private void SetHistoryDetailLabelVisibility(string label, bool isVisible)
	{
		foreach (TextBlock item in FindVisualChildren<TextBlock>(HistoryPage))
		{
			if (item.Text.Trim().Equals(label, StringComparison.OrdinalIgnoreCase))
			{
				item.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
			}
		}
	}

	private static bool IsSelectionHistoryAction(string text)
	{
		switch (text.Trim())
		{
		case "Open":
		case "Copy":
		case "Retry Model":
		case "Use Retry":
		case "Learn":
		case "Save Tags":
			return true;
		default:
			return false;
		}
	}

	private static bool IsHistoryAction(string text)
	{
		if (!IsSelectionHistoryAction(text))
		{
			if (!(text.Trim() == "Export"))
			{
				return text.Trim() == "Clear";
			}
			return true;
		}
		return true;
	}

	private static string ReadButtonText(System.Windows.Controls.Button button)
	{
		if (button.Content is string text)
		{
			return text;
		}
		if (button.Content is TextBlock textBlock)
		{
			return textBlock.Text;
		}
		if (button.Content is DependencyObject dependencyObject)
		{
			return string.Join(" ", from childTextBlock in FindVisualChildren<TextBlock>(dependencyObject)
				select childTextBlock.Text.Trim() into value
				where !string.IsNullOrWhiteSpace(value)
				select value);
		}
		return button.Content?.ToString() ?? "";
	}

	private void ApplyDictionaryReviewPolish()
	{
		InstallDictionaryDeletionActions();
		DictionaryHeroPanel.MinHeight = 116.0;
		DictionaryHeroPanel.Padding = new Thickness(28.0, 22.0, 28.0, 22.0);
		VocabularyGrid.EnableRowVirtualization = true;
		VocabularyGrid.EnableColumnVirtualization = true;
		VocabularyGrid.RowHeight = 42.0;
		VocabularyGrid.GridLinesVisibility = DataGridGridLinesVisibility.None;
		VocabularyGrid.RowHeaderWidth = 0.0;
		VocabularyGrid.CanUserResizeRows = false;
		LearnedCorrectionsListBox.MinHeight = 126.0;
		LearnedCorrectionsListBox.Padding = new Thickness(2.0);
		LearnedCorrectionsSummaryTextBlock.TextWrapping = TextWrapping.Wrap;
	}

	private void InstallDictionaryDeletionActions()
	{
		if (_dictionaryDeletionActionsInstalled)
		{
			return;
		}
		_dictionaryDeletionActionsInstalled = true;
		VocabularyGrid.PreviewKeyDown += DictionaryDeleteKeyDown;
		LearnedCorrectionsListBox.PreviewKeyDown += DictionaryDeleteKeyDown;
		VocabularyGrid.ContextMenu = CreateDictionaryDeleteContextMenu("Delete selected word");
		LearnedCorrectionsListBox.ContextMenu = CreateDictionaryDeleteContextMenu("Delete learned correction");
		if (DictionaryTabsPanel != null)
		{
			_dictionaryDeleteSelectedButton = new System.Windows.Controls.Button
			{
				Content = "Delete selected",
				Style = (Style)FindResource("RoundedButton"),
				MinHeight = 32.0,
				Padding = new Thickness(12.0, 6.0, 12.0, 6.0),
				Margin = new Thickness(12.0, 0.0, 0.0, 0.0),
				ToolTip = "Delete the selected dictionary word or learned correction."
			};
			_dictionaryDeleteSelectedButton.Click += DeleteSelectedDictionaryItem;
			DictionaryTabsPanel.Children.Add(_dictionaryDeleteSelectedButton);
		}
	}

	private System.Windows.Controls.ContextMenu CreateDictionaryDeleteContextMenu(string label)
	{
		System.Windows.Controls.ContextMenu contextMenu = new System.Windows.Controls.ContextMenu();
		System.Windows.Controls.MenuItem menuItem = new System.Windows.Controls.MenuItem
		{
			Header = label
		};
		menuItem.Click += DeleteSelectedDictionaryItem;
		contextMenu.Items.Add(menuItem);
		return contextMenu;
	}

	private void DictionaryDeleteKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (e.Key != Key.Delete)
		{
			return;
		}
		DeleteSelectedDictionaryItem(sender, e);
		e.Handled = true;
	}

	private void DeleteSelectedDictionaryItem(object sender, RoutedEventArgs e)
	{
		if (LearnedCorrectionsListBox.IsKeyboardFocusWithin && LearnedCorrectionsListBox.SelectedItem is VocabularyEntry learnedEntry)
		{
			DeleteSelectedLearnedCorrection(learnedEntry);
			return;
		}
		if (VocabularyGrid.SelectedItem is VocabularyEntry vocabularyEntry)
		{
			DeleteSelectedVocabularyEntry(vocabularyEntry);
			return;
		}
		if (LearnedCorrectionsListBox.SelectedItem is VocabularyEntry fallbackLearnedEntry)
		{
			DeleteSelectedLearnedCorrection(fallbackLearnedEntry);
			return;
		}
		StatusTextBlock.Text = "Select a dictionary word first";
	}

	private void DeleteSelectedVocabularyEntry(VocabularyEntry entry)
	{
		VocabularyGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
		VocabularyGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
		if (!_vocabulary.Remove(entry))
		{
			StatusTextBlock.Text = "Could not delete selected word";
			return;
		}
		AfterDictionaryEntryDeleted("Dictionary word deleted");
	}

	private void DeleteSelectedLearnedCorrection(VocabularyEntry entry)
	{
		if (!_vocabulary.Remove(entry))
		{
			StatusTextBlock.Text = "Could not delete learned correction";
			return;
		}
		AfterDictionaryEntryDeleted("Learned correction deleted");
	}

	private void AfterDictionaryEntryDeleted(string status)
	{
		NormalizeVocabularyIds();
		RefreshLearnedCorrectionsReview();
		InvalidateStatsCache();
		SaveVocabularyInBackground();
		VocabularyGrid.Items.Refresh();
		LearnedCorrectionsListBox.Items.Refresh();
		UpdateLibraryStats();
		StatusTextBlock.Text = status;
	}

	private void ApplyVoiceProfilePolish()
	{
		ProfileLearningTextBlock.TextWrapping = TextWrapping.Wrap;
		ProfileLearningTextBlock.MaxWidth = 680.0;
		ProfileWordsSpokenTextBlock.FontSize = Math.Max(ProfileWordsSpokenTextBlock.FontSize, 24.0);
		ProfileAccuracyTextBlock.FontSize = Math.Max(ProfileAccuracyTextBlock.FontSize, 24.0);
		ProfileSavedCorrectionsTextBlock.FontSize = Math.Max(ProfileSavedCorrectionsTextBlock.FontSize, 24.0);
	}

	private void ApplyEmptyStatePolish()
	{
		UpdateEmptyStatePolish();
	}

	private void UpdateEmptyStatePolish()
	{
		HistoryListBox.Opacity = ((_history.Count == 0) ? 0.74 : 1.0);
		VocabularyGrid.Opacity = ((_vocabulary.Count == 0) ? 0.78 : 1.0);
		LearnedCorrectionsListBox.Opacity = ((_learnedCorrections.Count == 0) ? 0.76 : 1.0);
		HistoryListBox.ToolTip = ((_history.Count == 0) ? "Saved transcripts will appear here." : null);
		VocabularyGrid.ToolTip = ((_vocabulary.Count == 0) ? "Dictionary terms will appear here." : null);
	}

	private void ApplyMutedDarkContrast()
	{
		if (!IsDarkTheme())
		{
			return;
		}
		StatusTextBlock.Foreground = ResourceBrush("MutedBrush");
		HeaderSubtitleTextBlock.Foreground = ResourceBrush("MutedBrush");
		DictateVoiceStatsTextBlock.Foreground = ResourceBrush("MutedBrush");
		VoiceStatsTextBlock.Foreground = ResourceBrush("MutedBrush");
		LearnedCorrectionsSummaryTextBlock.Foreground = ResourceBrush("MutedBrush");
	}

	private void RemoveHeaderLogoMark()
	{
		try
		{
			base.Icon = null;
			foreach (System.Windows.Controls.Image image in FindVisualChildren<System.Windows.Controls.Image>(this))
			{
				if (IsTopChromeLogoCandidate(image))
				{
					CollapseHeaderMark(image);
				}
			}
			foreach (Border border in FindVisualChildren<Border>(this))
			{
				if (IsTopChromeLogoCandidate(border) && FindVisualParent<System.Windows.Controls.Button>(border) == null)
				{
					CollapseHeaderMark(border);
				}
			}
		}
		catch (Exception exception)
		{
			AppLog.Warn("Could not remove header logo mark.", exception);
		}
	}

	private void ApplyHeaderEditorialVisual()
	{
		if (!IsDarkTheme())
		{
			return;
		}
		if (OpenAudioButton != null)
		{
			OpenAudioButton.Content = CreateIconTextButtonContent("\ue896", "Import Audio");
			OpenAudioButton.Padding = new Thickness(12.0, 8.0, 12.0, 8.0);
			OpenAudioButton.Background = ThemeBrushWithOpacity("ElevatedBrush", 0.52);
			OpenAudioButton.BorderBrush = ThemeBrushWithOpacity("GoldBrush", 0.14);
			OpenAudioButton.Foreground = ResourceBrush("InkBrush");
			OpenAudioButton.ToolTip = "Import audio";
		}
		System.Windows.Controls.Button headerVisualButton = FindHeaderVisualButton();
		if (headerVisualButton != null)
		{
			ApplyOrbitVisualToButton(headerVisualButton, 34.0, 34.0, "Audio tools");
		}
		Border headerVisualFrame = FindHeaderVisualFrame();
		if (headerVisualFrame != null)
		{
			ApplyOrbitVisualToBorder(headerVisualFrame, 34.0, 34.0);
		}
		System.Windows.Controls.Button railBrandButton = FindSidebarBrandButton();
		if (railBrandButton != null)
		{
			ApplyOrbitVisualToButton(railBrandButton, 28.0, 28.0, "Speak");
		}
		Border railBrandFrame = FindSidebarBrandFrame();
		if (railBrandFrame != null)
		{
			ApplyOrbitVisualToBorder(railBrandFrame, 28.0, 28.0);
		}
	}

	private void ApplyOrbitVisualToButton(System.Windows.Controls.Button button, double width, double height, string tooltip)
	{
		button.Content = CreateHeaderOrbitVisual(width, height);
		button.Padding = new Thickness(Math.Max(6.0, width * 0.22));
		button.Background = ThemeBrushWithOpacity("ElevatedBrush", 0.44);
		button.BorderBrush = ThemeBrushWithOpacity("GoldBrush", 0.14);
		button.Foreground = ResourceBrush("InkBrush");
		button.ToolTip = tooltip;
	}

	private void ApplyOrbitVisualToBorder(Border border, double width, double height)
	{
		border.Child = CreateHeaderOrbitVisual(width, height);
		border.Background = ThemeBrushWithOpacity("ElevatedBrush", 0.44);
		border.BorderBrush = ThemeBrushWithOpacity("GoldBrush", 0.14);
		border.Padding = new Thickness(Math.Max(6.0, width * 0.18));
	}

	private StackPanel CreateIconTextButtonContent(string glyph, string label)
	{
		StackPanel stackPanel = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = glyph,
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 12.0,
			Foreground = ResourceBrush("InkBrush"),
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		});
		stackPanel.Children.Add(new TextBlock
		{
			Text = label,
			FontSize = 13.0,
			FontWeight = FontWeights.SemiBold,
			Foreground = ResourceBrush("InkBrush"),
			VerticalAlignment = VerticalAlignment.Center
		});
		return stackPanel;
	}

	private Canvas CreateHeaderOrbitVisual(double width, double height)
	{
		Canvas canvas = new Canvas
		{
			Width = width,
			Height = height,
			Opacity = 0.92,
			IsHitTestVisible = false,
			ClipToBounds = false
		};
		System.Windows.Media.Brush lineBrush = ThemeBrushWithOpacity("GoldBrush", 0.44);
		System.Windows.Media.Brush strongBrush = ThemeBrushWithOpacity("InkBrush", 0.78);
		System.Windows.Media.Brush softBrush = ThemeBrushWithOpacity("GoldBrush", 0.065);
		double centerX = width / 2.0;
		double centerY = height / 2.0;
		Ellipse wash = new Ellipse
		{
			Width = width * 0.88,
			Height = height * 0.66,
			Fill = softBrush,
			RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
			RenderTransform = new RotateTransform(-16.0)
		};
		Canvas.SetLeft(wash, centerX - wash.Width / 2.0);
		Canvas.SetTop(wash, centerY - wash.Height / 2.0);
		canvas.Children.Add(wash);
		AddEllipse(canvas, centerX, centerY, width * 0.74, height * 0.34, lineBrush, 0.9, -16.0);
		AddEllipse(canvas, centerX, centerY, width * 0.42, height * 0.42, lineBrush, 0.8, 0.0);
		AddBezierWave(canvas, width * 0.18, centerY + height * 0.02, width * 0.64, height * 0.16, strongBrush, 1.15);
		AddDot(canvas, width * 0.74, height * 0.33, Math.Max(3.0, width * 0.095), strongBrush);
		AddDot(canvas, width * 0.28, height * 0.68, Math.Max(2.2, width * 0.065), lineBrush);
		return canvas;
	}

	private System.Windows.Controls.Button FindHeaderVisualButton()
	{
		foreach (System.Windows.Controls.Button button in FindVisualChildren<System.Windows.Controls.Button>(this))
		{
			if (button == OpenAudioButton || button == DictateTabButton || button == HistoryTabButton || button == ProfileTabButton || button == DictionaryTabButton || button == SettingsTabButton || button == _audioTabButton || button == RecordButton)
			{
				continue;
			}
			if (IsHeaderVisualButtonCandidate(button))
			{
				return button;
			}
		}
		return null;
	}

	private Border FindHeaderVisualFrame()
	{
		foreach (Border border in FindVisualChildren<Border>(this))
		{
			if (FindVisualParent<System.Windows.Controls.Button>(border) != null)
			{
				continue;
			}
			if (IsHeaderVisualButtonCandidate(border))
			{
				return border;
			}
		}
		return null;
	}

	private System.Windows.Controls.Button FindSidebarBrandButton()
	{
		foreach (System.Windows.Controls.Button button in FindVisualChildren<System.Windows.Controls.Button>(this))
		{
			if (button == OpenAudioButton || button == DictateTabButton || button == HistoryTabButton || button == ProfileTabButton || button == DictionaryTabButton || button == SettingsTabButton || button == _audioTabButton || button == RecordButton)
			{
				continue;
			}
			if (IsSidebarBrandCandidate(button))
			{
				return button;
			}
		}
		return null;
	}

	private Border FindSidebarBrandFrame()
	{
		foreach (Border border in FindVisualChildren<Border>(this))
		{
			if (FindVisualParent<System.Windows.Controls.Button>(border) != null)
			{
				continue;
			}
			if (IsSidebarBrandCandidate(border))
			{
				return border;
			}
		}
		return null;
	}

	private bool IsHeaderVisualButtonCandidate(FrameworkElement element)
	{
		try
		{
			System.Windows.Point point = element.TransformToAncestor(this).Transform(new System.Windows.Point(0.0, 0.0));
			double width = EffectiveLength(element.ActualWidth, element.Width, element.MinWidth);
			double height = EffectiveLength(element.ActualHeight, element.Height, element.MinHeight);
			double windowWidth = EffectiveLength(ActualWidth, Width, MinWidth);
			return windowWidth > 0.0 && point.X > windowWidth - 310.0 && point.X < windowWidth - 185.0 && point.Y >= 58.0 && point.Y < 150.0 && width >= 38.0 && width <= 72.0 && height >= 38.0 && height <= 72.0;
		}
		catch
		{
			return false;
		}
	}

	private bool IsSidebarBrandCandidate(FrameworkElement element)
	{
		try
		{
			System.Windows.Point point = element.TransformToAncestor(this).Transform(new System.Windows.Point(0.0, 0.0));
			double width = EffectiveLength(element.ActualWidth, element.Width, element.MinWidth);
			double height = EffectiveLength(element.ActualHeight, element.Height, element.MinHeight);
			return point.X >= 0.0 && point.X < 72.0 && point.Y >= 48.0 && point.Y < 118.0 && width >= 28.0 && width <= 58.0 && height >= 28.0 && height <= 58.0;
		}
		catch
		{
			return false;
		}
	}

	private void ApplyGlassSurfacePolish()
	{
		if (!IsDarkTheme())
		{
			return;
		}
		ApplyGlassSurfaceTo(DictatePage);
		ApplyGlassSurfaceTo(HistoryPage);
		ApplyGlassSurfaceTo(VoiceProfilePage);
		ApplyGlassSurfaceTo(DictionaryPage);
		ApplyGlassSurfaceTo(SettingsPage);
		ApplyGlassButtonPolish();
	}

	private void ApplyGlassSurfaceTo(DependencyObject root)
	{
		if (root == null)
		{
			return;
		}
		foreach (Border border in FindVisualChildren<Border>(root))
		{
			if (FindVisualParent<System.Windows.Controls.Button>(border) != null || IsSmallChromeElement(border))
			{
				continue;
			}
			double area = EstimateElementArea(border);
			if (area < 1800.0)
			{
				continue;
			}
			bool isLargeSurface = area > 42000.0;
			border.Background = ThemeBrushWithOpacity(isLargeSurface ? "PanelBrush" : "ElevatedBrush", isLargeSurface ? 0.58 : 0.50);
			border.BorderBrush = ThemeBrushWithOpacity("GoldBrush", isLargeSurface ? 0.16 : 0.12);
		}
		foreach (System.Windows.Controls.TextBox textBox in FindVisualChildren<System.Windows.Controls.TextBox>(root))
		{
			textBox.Background = ThemeBrushWithOpacity("InputBrush", 0.64);
			textBox.BorderBrush = ThemeBrushWithOpacity("GoldBrush", 0.14);
			textBox.Foreground = ResourceBrush("InkBrush");
		}
		foreach (System.Windows.Controls.ComboBox comboBox in FindVisualChildren<System.Windows.Controls.ComboBox>(root))
		{
			comboBox.Background = ThemeBrushWithOpacity("InputBrush", 0.70);
			comboBox.BorderBrush = ThemeBrushWithOpacity("GoldBrush", 0.18);
			comboBox.Foreground = ResourceBrush("InkBrush");
		}
		foreach (System.Windows.Controls.ListBox listBox in FindVisualChildren<System.Windows.Controls.ListBox>(root))
		{
			listBox.Background = ThemeBrushWithOpacity("PanelBrush", 0.42);
			listBox.BorderBrush = ThemeBrushWithOpacity("GoldBrush", 0.14);
			listBox.Foreground = ResourceBrush("InkBrush");
		}
		foreach (System.Windows.Controls.DataGrid dataGrid in FindVisualChildren<System.Windows.Controls.DataGrid>(root))
		{
			dataGrid.Background = ThemeBrushWithOpacity("PanelBrush", 0.42);
			dataGrid.BorderBrush = ThemeBrushWithOpacity("GoldBrush", 0.14);
			dataGrid.RowBackground = ThemeBrushWithOpacity("ElevatedBrush", 0.44);
			dataGrid.AlternatingRowBackground = ThemeBrushWithOpacity("PanelBrush", 0.28);
			dataGrid.Foreground = ResourceBrush("InkBrush");
		}
	}

	private void ApplyGlassButtonPolish()
	{
		foreach (System.Windows.Controls.Button button in ModesPanel.Children.OfType<System.Windows.Controls.Button>())
		{
			bool isSelected = button.Tag is DictationMode mode && string.Equals(mode.Id, _selectedMode.Id, StringComparison.OrdinalIgnoreCase);
			button.Background = isSelected ? ThemeBrushWithOpacity("PremiumSoftBrush", 0.78) : ThemeBrushWithOpacity("PanelBrush", 0.48);
			button.BorderBrush = isSelected ? ThemeBrushWithOpacity("GoldBrush", 0.72) : ThemeBrushWithOpacity("GoldBrush", 0.16);
		}
	}

	private bool IsTopChromeLogoCandidate(FrameworkElement element)
	{
		try
		{
			System.Windows.Point point = element.TransformToAncestor(this).Transform(new System.Windows.Point(0.0, 0.0));
			double width = EffectiveLength(element.ActualWidth, element.Width, element.MinWidth);
			double height = EffectiveLength(element.ActualHeight, element.Height, element.MinHeight);
			return point.X >= 0.0 && point.X < 78.0 && point.Y >= 0.0 && point.Y < 54.0 && width > 0.0 && width <= 42.0 && height > 0.0 && height <= 42.0;
		}
		catch
		{
			return false;
		}
	}

	private static void CollapseHeaderMark(FrameworkElement element)
	{
		element.Visibility = Visibility.Collapsed;
		element.Width = 0.0;
		element.MinWidth = 0.0;
		element.Margin = new Thickness(0.0);
	}

	private static bool IsSmallChromeElement(FrameworkElement element)
	{
		double width = EffectiveLength(element.ActualWidth, element.Width, element.MinWidth);
		double height = EffectiveLength(element.ActualHeight, element.Height, element.MinHeight);
		return width > 0.0 && height > 0.0 && width <= 64.0 && height <= 64.0;
	}

	private static double EstimateElementArea(FrameworkElement element)
	{
		double width = EffectiveLength(element.ActualWidth, element.Width, element.MinWidth);
		double height = EffectiveLength(element.ActualHeight, element.Height, element.MinHeight);
		return width * height;
	}

	private static double EffectiveLength(double actual, double declared, double fallback)
	{
		if (!double.IsNaN(actual) && actual > 0.0)
		{
			return actual;
		}
		if (!double.IsNaN(declared) && declared > 0.0)
		{
			return declared;
		}
		return (!double.IsNaN(fallback) && fallback > 0.0) ? fallback : 0.0;
	}

	private void ApplyRecordingMicrocopy()
	{
		if (RecordingStatusTextBlock.Text.Equals("Ready.", StringComparison.Ordinal))
		{
			RecordingStatusTextBlock.Text = "Ready";
		}
		if (StatusTextBlock.Text.Equals("Formatted", StringComparison.Ordinal))
		{
			StatusTextBlock.Text = "Polished";
		}
	}

	private bool HistoryFilter(object item)
	{
		if (!(item is TranscriptCard transcriptCard))
		{
			return false;
		}
		string text = HistorySearchTextBox?.Text.Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			return true;
		}
		if (!ContainsIgnoreCase(transcriptCard.FormattedText, text) && !ContainsIgnoreCase(transcriptCard.RawText, text) && !ContainsIgnoreCase(transcriptCard.ModeId, text))
		{
			return ContainsIgnoreCase(transcriptCard.Tags, text);
		}
		return true;
	}

	private static bool ContainsIgnoreCase(string value, string query)
	{
		if (value == null)
		{
			return false;
		}
		return value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static void AnimatePageIn(FrameworkElement page)
	{
		AnimatePageTransition(page);
	}

	private static void AnimatePageTransition(FrameworkElement page)
	{
		page.Opacity = 0.0;
		TranslateTransform translateTransform = (TranslateTransform)(page.RenderTransform = new TranslateTransform(0.0, 8.0));
		QuarticEase easingFunction = new QuarticEase
		{
			EasingMode = EasingMode.EaseOut
		};
		page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220.0))
		{
			EasingFunction = easingFunction
		});
		translateTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8.0, 0.0, TimeSpan.FromMilliseconds(240.0))
		{
			EasingFunction = easingFunction
		});
	}

	private static void AnimateScale(FrameworkElement element, double targetScale, int milliseconds)
	{
		ScaleTransform scaleTransform = EnsureScaleTransform(element);
		DoubleAnimation animation = new DoubleAnimation
		{
			To = targetScale,
			Duration = TimeSpan.FromMilliseconds(milliseconds),
			EasingFunction = new CubicEase
			{
				EasingMode = EasingMode.EaseOut
			}
		};
		scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
		scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
	}

	private static ScaleTransform EnsureScaleTransform(FrameworkElement element)
	{
		if (element.RenderTransform is ScaleTransform scaleTransform)
		{
			ScaleTransform scaleTransform2 = (scaleTransform.IsFrozen ? scaleTransform.CloneCurrentValue() : scaleTransform);
			if (scaleTransform2 != scaleTransform)
			{
				element.RenderTransform = scaleTransform2;
			}
			return scaleTransform2;
		}
		ScaleTransform result = (ScaleTransform)(element.RenderTransform = new ScaleTransform(1.0, 1.0));
		element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
		return result;
	}

	private static void AnimateTextRefresh(UIElement element)
	{
		element.Opacity = 0.35;
		element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(180.0))
		{
			EasingFunction = new CubicEase
			{
				EasingMode = EasingMode.EaseOut
			}
		});
	}

	private static void AnimateEditorialOpacity(UIElement element, int delayMs)
	{
		element.Opacity = 0.0;
		element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(420.0))
		{
			BeginTime = TimeSpan.FromMilliseconds(delayMs),
			EasingFunction = new QuarticEase
			{
				EasingMode = EasingMode.EaseOut
			}
		});
	}

	private static void AnimateEditorialSlide(FrameworkElement element, int delayMs)
	{
		bool canSlide = element.RenderTransform == null || element.RenderTransform.Value.IsIdentity;
		if (canSlide)
		{
			TranslateTransform translateTransform = new TranslateTransform(0.0, 5.0);
			element.RenderTransform = translateTransform;
			translateTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5.0, 0.0, TimeSpan.FromMilliseconds(460.0))
			{
				BeginTime = TimeSpan.FromMilliseconds(delayMs),
				EasingFunction = new QuarticEase
				{
					EasingMode = EasingMode.EaseOut
				}
			});
		}
		AnimateEditorialOpacity(element, delayMs);
	}

	private void RawTranscriptTextBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (!_isRecording && !_isTranscribing && !string.IsNullOrWhiteSpace(RawTranscriptTextBox.Text))
		{
			StatusTextBlock.Text = "Ready to format";
		}
	}

	private void LoadSampleButton_Click(object sender, RoutedEventArgs e)
	{
		RawTranscriptTextBox.Text = SampleForMode(_selectedMode.Id);
		FormatCurrent(addToHistory: false);
		StatusTextBlock.Text = "Sample loaded and formatted";
	}

	private async void RecordButton_Click(object sender, RoutedEventArgs e)
	{
		if (!_isTranscribing)
		{
			if (!_isRecording)
			{
				StartRecording();
			}
			else
			{
				await StopRecordingAndTranscribeAsync();
			}
		}
	}

	private async void OpenAudioButton_Click(object sender, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "Open audio to transcribe",
			Filter = "Audio files|*.wav;*.mp3;*.m4a;*.ogg;*.webm;*.mp4|All files|*.*"
		};
		if (openFileDialog.ShowDialog(this) == true)
		{
			await TranscribeAndFormatAsync(openFileDialog.FileName);
		}
	}

	private void StartRecording()
	{
		if (_settings.EngineId == "manual")
		{
			StatusTextBlock.Text = "Switch engine to Local Whisper to record";
			return;
		}
		CaptureDeliveryTargetWindow();
		int num = ((AudioInputComboBox.SelectedValue is int num2) ? num2 : _settings.AudioInputDeviceNumber);
		if (num < 0)
		{
			StatusTextBlock.Text = "No microphone available";
			return;
		}
		try
		{
			string text = System.IO.Path.Combine(_store.Root, "recordings");
			Directory.CreateDirectory(text);
			_recordingPath = System.IO.Path.Combine(text, $"speak-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
			_recordingStopped = new TaskCompletionSource();
			_recordingException = null;
			_waveIn = new WaveInEvent
			{
				DeviceNumber = num,
				WaveFormat = new WaveFormat(16000, 16, 1),
				BufferMilliseconds = 80
			};
			_waveWriter = new WaveFileWriter(_recordingPath, _waveIn.WaveFormat);
			_waveIn.DataAvailable += delegate(object? _, WaveInEventArgs args)
			{
				_waveWriter?.Write(args.Buffer, 0, args.BytesRecorded);
				_waveWriter?.Flush();
				double level = CalculateMicrophoneLevel(args.Buffer, args.BytesRecorded);
				base.Dispatcher.BeginInvoke((Action)delegate
				{
					_shortcutWidget?.SetMicActivity(level);
				});
			};
			_waveIn.RecordingStopped += delegate(object? _, StoppedEventArgs args)
			{
				_recordingException = args.Exception;
				_recordingStopped?.TrySetResult();
			};
			_waveIn.StartRecording();
			_isRecording = true;
			_recordingStartedAt = DateTimeOffset.Now;
			_recordingTimer.Start();
			SetRecordButtonState(isRecording: true);
			OpenAudioButton.IsEnabled = false;
			UpdateRecordingElapsedUi();
			StatusTextBlock.Text = "Recording";
			UpdateShortcutWidgetState();
		}
		catch (Exception ex)
		{
			DisposeRecording();
			_isRecording = false;
			_recordingTimer.Stop();
			_recordingStartedAt = null;
			StatusTextBlock.Text = "Recording failed";
			RecordingStatusTextBlock.Text = ex.Message;
			UpdateShortcutWidgetState();
		}
	}

	private async Task StopRecordingAndTranscribeAsync()
	{
		string path = _recordingPath;
		try
		{
			_waveIn?.StopRecording();
			if (_recordingStopped != null)
			{
				await Task.WhenAny(_recordingStopped.Task, Task.Delay(2500));
			}
			if (_recordingException != null)
			{
				throw _recordingException;
			}
		}
		finally
		{
			DisposeRecording();
			_isRecording = false;
			_recordingTimer.Stop();
			_recordingStartedAt = null;
			SetRecordButtonState(isRecording: false);
			OpenAudioButton.IsEnabled = true;
			UpdateShortcutWidgetState();
		}
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			StatusTextBlock.Text = "No recording saved";
			return;
		}
		await TranscribeAndFormatAsync(path);
		ArchiveOldRecordings();
	}

	private void ArchiveOldRecordings()
	{
		int recordingRetentionDays = _settings.RecordingRetentionDays;
		if (recordingRetentionDays <= 0)
		{
			return;
		}
		string path = System.IO.Path.Combine(_store.Root, "recordings");
		if (!Directory.Exists(path))
		{
			return;
		}
		try
		{
			DateTime dateTime = DateTime.Now.AddDays(-recordingRetentionDays);
			string path2 = System.IO.Path.Combine(_store.Root, "recordings-archive");
			int num = 0;
			foreach (string item in Directory.EnumerateFiles(path, "*.wav", SearchOption.TopDirectoryOnly))
			{
				DateTime lastWriteTime = File.GetLastWriteTime(item);
				if (!(lastWriteTime >= dateTime))
				{
					string text = System.IO.Path.Combine(path2, lastWriteTime.ToString("yyyy-MM"));
					Directory.CreateDirectory(text);
					File.Move(item, UniqueFilePath(System.IO.Path.Combine(text, System.IO.Path.GetFileName(item))));
					num++;
				}
			}
			if (num > 0)
			{
				AppLog.Info($"Archived {num} old recording file(s) from active recordings.");
			}
		}
		catch (Exception exception)
		{
			AppLog.Warn("Recording archive pass failed.", exception);
		}
	}

	private static string UniqueFilePath(string path)
	{
		if (!File.Exists(path))
		{
			return path;
		}
		string path2 = System.IO.Path.GetDirectoryName(path) ?? "";
		string fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(path);
		string extension = System.IO.Path.GetExtension(path);
		for (int i = 1; i < 1000; i++)
		{
			string text = System.IO.Path.Combine(path2, $"{fileNameWithoutExtension}-{i}{extension}");
			if (!File.Exists(text))
			{
				return text;
			}
		}
		return System.IO.Path.Combine(path2, $"{fileNameWithoutExtension}-{Guid.NewGuid():N}{extension}");
	}

	private void DisposeRecording()
	{
		_waveIn?.Dispose();
		_waveIn = null;
		_waveWriter?.Dispose();
		_waveWriter = null;
		_recordingStopped = null;
	}

	private async Task TranscribeAndFormatAsync(string audioPath)
	{
		if (!File.Exists(audioPath))
		{
			StatusTextBlock.Text = "Audio file missing";
			return;
		}
		try
		{
			_isTranscribing = true;
			SetTranscriptionControlsEnabled(isEnabled: false);
			StatusTextBlock.Text = "Transcribing";
			UpdateShortcutWidgetState();
			DeliveryCommand deliveryCommand = ExtractDeliveryCommand(await TranscribeAudioForCurrentSettingsAsync(audioPath));
			RawTranscriptTextBox.Text = deliveryCommand.Text;
			RecordingStatusTextBlock.Text = "Polishing.";
			string text = TranscriptionDeliveryPolicy.ResolveText(await FormatCurrentAsync(addToHistory: true, audioPath), FormattedOutputTextBox.Text);
			if (TranscriptionDeliveryPolicy.ShouldDeliver(text))
			{
				await DeliverTranscriptionOutputAsync(text, deliveryCommand.OutputDestinationId, deliveryCommand.PressEnterAfterPaste);
			}
		}
		catch (Exception ex)
		{
			StatusTextBlock.Text = "Transcription failed";
			RecordingStatusTextBlock.Text = ex.Message;
		}
		finally
		{
			_isTranscribing = false;
			SetTranscriptionControlsEnabled(isEnabled: true);
			UpdateShortcutWidgetState();
		}
	}

	private async Task<string> TranscribeAudioForCurrentSettingsAsync(string audioPath)
	{
		bool num = _settings.EngineId.Equals("cloud-stt", StringComparison.OrdinalIgnoreCase);
		TranscriptionModelOption transcriptionModelOption = SelectedTranscriptionModel();
		if (!num && transcriptionModelOption == null)
		{
			throw new InvalidOperationException("Select a transcription model first.");
		}
		string whisperPythonPath = _settings.WhisperPythonPath;
		if (!num && !File.Exists(whisperPythonPath))
		{
			throw new InvalidOperationException("Whisper runtime missing: " + whisperPythonPath);
		}
		RecordingStatusTextBlock.Text = "Transcribing.";
		return (!num) ? (await RunWhisperAsync(audioPath, transcriptionModelOption)) : (await RunCloudSttAsync(audioPath));
	}

	private async Task<string> RunWhisperAsync(string audioPath, TranscriptionModelOption model)
	{
		RecordingStatusTextBlock.Text = "Transcribing.";
		_ttsWarmCts?.Cancel();
		await _ttsSynthesizer.StopWarmEngineAsync();
		return await RunResidentWhisperAsync(audioPath, model);
	}

	private async Task ReleaseWhisperForAudioStudioAsync()
	{
		if (await IsWhisperServerReadyAsync())
		{
			await RequestWhisperServerStopAsync();
			WhisperRuntimeStatusTextBlock.Text = "Whisper was released so the selected Audio Studio model can use the GPU.";
		}
	}

	private async Task<string> RunCloudSttAsync(string audioPath)
	{
		string result = await _cloudSpeechTranscriber.TranscribeAsync(audioPath, _settings);
		CloudSttProviderOption cloudSttProviderOption = CloudSttProviderOption.Find(_settings.SttCloudProviderId);
		WhisperRuntimeStatusTextBlock.Text = $"{cloudSttProviderOption.Name}: {_settings.SttCloudModel} at {_settings.SttCloudEndpoint}. Local recording stayed on this PC.";
		return result;
	}

	private async Task<string> RunResidentWhisperAsync(string audioPath, TranscriptionModelOption model)
	{
		await EnsureWhisperServerAsync();
		string language = WhisperLanguageFromLocale(_settings.LocaleId);
		string content = JsonSerializer.Serialize(new WhisperTranscribeRequest
		{
			AudioPath = audioPath,
			Model = model.WhisperArgument,
			ModelDir = (System.IO.Path.GetDirectoryName(model.ModelPath) ?? TranscriptionModelOption.DefaultModelRoot),
			Language = language,
			Device = _settings.WhisperDeviceId,
			KeepAliveMinutes = _settings.ModelKeepAliveMinutes
		}, _jsonOptions);
		using StringContent content2 = new StringContent(content, Encoding.UTF8, "application/json");
		using HttpResponseMessage response = await _whisperClient.PostAsync("http://127.0.0.1:39731/transcribe", content2);
		string text = await response.Content.ReadAsStringAsync();
		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException(ExtractWhisperError(text));
		}
		WhisperTranscribeResponse whisperTranscribeResponse = JsonSerializer.Deserialize<WhisperTranscribeResponse>(text, _jsonOptions) ?? throw new InvalidOperationException("Whisper returned an invalid response.");
		if (string.IsNullOrWhiteSpace(whisperTranscribeResponse.Text))
		{
			throw new InvalidOperationException("Whisper returned an empty transcript.");
		}
		WhisperRuntimeStatusTextBlock.Text = $"Speak is keeping {whisperTranscribeResponse.Model} loaded on {whisperTranscribeResponse.Device}. It offloads after {_settings.ModelKeepAliveMinutes} minutes with no transcription activity.";
		return whisperTranscribeResponse.Text.Trim();
	}

	private async Task EnsureWhisperServerAsync()
	{
		WhisperHealthResponse whisperHealthResponse = await GetWhisperServerHealthAsync();
		if (whisperHealthResponse != null)
		{
			if (!ShouldRestartWhisperServer(whisperHealthResponse))
			{
				return;
			}
			await RequestWhisperServerStopAsync();
			for (int attempt = 0; attempt < 20; attempt++)
			{
				await Task.Delay(150);
				if (await GetWhisperServerHealthAsync() == null)
				{
					break;
				}
			}
		}
		string text = ResolveWhisperServerScriptPath();
		if (!File.Exists(_settings.WhisperPythonPath))
		{
			throw new InvalidOperationException("Whisper Python missing: " + _settings.WhisperPythonPath);
		}
		if (!File.Exists(text))
		{
			throw new InvalidOperationException("Whisper resident server script missing: " + text);
		}
		_whisperServerLastError = "";
		string value = TranscriptionModelOption.DefaultModelRoot;
		ProcessStartInfo processStartInfo = new ProcessStartInfo
		{
			FileName = _settings.WhisperPythonPath,
			Arguments = $"\"{text}\" --host 127.0.0.1 --port 39731 --idle-minutes {_settings.ModelKeepAliveMinutes} --model-dir \"{value}\"",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		processStartInfo.Environment["PYTHONUTF8"] = "1";
		processStartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
		processStartInfo.Environment["XDG_CACHE_HOME"] = AppConfig.Current.Paths.CacheRoot;
		string text2 = ResolveFfmpegDirectory();
		if (!string.IsNullOrWhiteSpace(text2))
		{
			string value2;
			string text3 = (processStartInfo.Environment.TryGetValue("PATH", out value2) ? value2 : (Environment.GetEnvironmentVariable("PATH") ?? ""));
			processStartInfo.Environment["PATH"] = text2 + System.IO.Path.PathSeparator + text3;
		}
		_whisperServerProcess = new Process
		{
			StartInfo = processStartInfo,
			EnableRaisingEvents = true
		};
		_whisperServerProcess.OutputDataReceived += delegate(object _, DataReceivedEventArgs args)
		{
			string.IsNullOrWhiteSpace(args.Data);
		};
		_whisperServerProcess.ErrorDataReceived += delegate(object _, DataReceivedEventArgs args)
		{
			if (!string.IsNullOrWhiteSpace(args.Data))
			{
				_whisperServerLastError = args.Data;
			}
		};
		if (!_whisperServerProcess.Start())
		{
			throw new InvalidOperationException("Could not start the resident Whisper server.");
		}
		_whisperServerProcess.BeginOutputReadLine();
		_whisperServerProcess.BeginErrorReadLine();
		for (int attempt = 0; attempt < 40; attempt++)
		{
			if (await IsWhisperServerReadyAsync())
			{
				WhisperRuntimeStatusTextBlock.Text = $"Speak-managed Whisper is ready. The model offloads after {_settings.ModelKeepAliveMinutes} minutes with no transcription activity.";
				return;
			}
			if (_whisperServerProcess.HasExited)
			{
				break;
			}
			await Task.Delay(250);
		}
		string text4 = (string.IsNullOrWhiteSpace(_whisperServerLastError) ? "No error detail was returned." : _whisperServerLastError);
		throw new InvalidOperationException("Resident Whisper server did not start. " + text4);
	}

	private async Task<bool> IsWhisperServerReadyAsync()
	{
		return await GetWhisperServerHealthAsync() != null;
	}

	private async Task<WhisperHealthResponse?> GetWhisperServerHealthAsync()
	{
		_ = 1;
		try
		{
			using HttpResponseMessage response = await _whisperClient.GetAsync("http://127.0.0.1:39731/health");
			if (!response.IsSuccessStatusCode)
			{
				return null;
			}
			return JsonSerializer.Deserialize<WhisperHealthResponse>(await response.Content.ReadAsStringAsync(), _jsonOptions);
		}
		catch (Exception)
		{
			return null;
		}
	}

	private bool ShouldRestartWhisperServer(WhisperHealthResponse health)
	{
		if (_settings.WhisperDeviceId.Equals("cuda", StringComparison.OrdinalIgnoreCase) || _settings.WhisperDeviceId.Equals("auto", StringComparison.OrdinalIgnoreCase))
		{
			return !health.CudaAvailable;
		}
		return false;
	}

	private async Task StopWhisperServerAsync()
	{
		if (!(await IsWhisperServerReadyAsync()))
		{
			WhisperRuntimeStatusTextBlock.Text = "No loaded Whisper model is running.";
			StatusTextBlock.Text = "No loaded model";
			return;
		}
		try
		{
			bool flag = await RequestWhisperServerStopAsync();
			WhisperRuntimeStatusTextBlock.Text = (flag ? "Resident Whisper stopped. GPU/RAM model memory is released." : "Stop request sent, but Whisper did not confirm cleanly.");
			StatusTextBlock.Text = "Model stopped";
		}
		catch (Exception ex)
		{
			WhisperRuntimeStatusTextBlock.Text = ex.Message;
			StatusTextBlock.Text = "Stop failed";
		}
	}

	private async Task<bool> RequestWhisperServerStopAsync()
	{
		bool stopConfirmed = false;
		try
		{
			using HttpClient stopClient = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(5.0)
			};
			using StringContent content = new StringContent("{}", Encoding.UTF8, "application/json");
			using HttpResponseMessage httpResponseMessage = await stopClient.PostAsync("http://127.0.0.1:39731/stop", content);
			stopConfirmed = httpResponseMessage.IsSuccessStatusCode;
		}
		catch (Exception exception)
		{
			AppLog.Warn("Whisper stop request failed; falling back to owned process cleanup.", exception);
		}
		for (int attempt = 0; attempt < 16; attempt++)
		{
			await Task.Delay(150);
			if (await GetWhisperServerHealthAsync() == null)
			{
				TerminateOwnedWhisperServerProcessTree();
				return stopConfirmed;
			}
		}
		TerminateOwnedWhisperServerProcessTree();
		return stopConfirmed;
	}

	private void StopWhisperServerForShutdown()
	{
		try
		{
			using HttpClient httpClient = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(2.0)
			};
			using StringContent content = new StringContent("{\"reason\":\"app-close\"}", Encoding.UTF8, "application/json");
			httpClient.PostAsync("http://127.0.0.1:39731/stop", content).GetAwaiter().GetResult();
		}
		catch (Exception exception)
		{
			AppLog.Warn("Whisper shutdown stop request failed; falling back to process cleanup.", exception);
		}
		Task.Delay(350).GetAwaiter().GetResult();
		TerminateOwnedWhisperServerProcessTree();
	}

	private void TerminateOwnedWhisperServerProcessTree()
	{
		Process whisperServerProcess = _whisperServerProcess;
		if (whisperServerProcess == null)
		{
			return;
		}
		try
		{
			if (!whisperServerProcess.HasExited)
			{
				using (Process process = Process.Start(new ProcessStartInfo
				{
					FileName = "taskkill.exe",
					Arguments = $"/PID {whisperServerProcess.Id} /T /F",
					CreateNoWindow = true,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true
				}))
				{
					process?.WaitForExit(2000);
					return;
				}
			}
		}
		catch (Exception exception)
		{
			AppLog.Warn("Owned Whisper process cleanup failed.", exception);
		}
		finally
		{
			_whisperServerProcess = null;
		}
	}

	private static string ResolveWhisperServerScriptPath()
	{
		string text = System.IO.Path.Combine(AppContext.BaseDirectory, "Tools", "whisper_resident_server.py");
		if (File.Exists(text))
		{
			return text;
		}
		return AppConfig.Current.Transcription.WhisperServerScriptPath;
	}

	private static string? ResolveFfmpegDirectory()
	{
		foreach (string candidate in ResolveBundledFfmpegDirectories())
		{
			if (File.Exists(System.IO.Path.Combine(candidate, "ffmpeg.exe")))
			{
				return candidate;
			}
		}
		string[] array = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
		foreach (string text2 in array)
		{
			try
			{
				if (File.Exists(System.IO.Path.Combine(text2.Trim(), "ffmpeg.exe")))
				{
					return text2.Trim();
				}
			}
			catch (Exception exception)
			{
				AppLog.Warn("Ignored malformed PATH entry while resolving ffmpeg.", exception);
			}
		}
		return null;
	}

	private static IEnumerable<string> ResolveBundledFfmpegDirectories()
	{
		yield return System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin");
		yield return System.IO.Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg", "bin");
		yield return System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "tools", "ffmpeg", "bin"));

		string toolsRoot = string.Empty;
		try
		{
			toolsRoot = AppConfig.Current.Paths.ToolsRoot;
		}
		catch (Exception exception)
		{
			AppLog.Warn("Could not resolve configured FFmpeg tools root.", exception);
		}

		if (!string.IsNullOrWhiteSpace(toolsRoot))
		{
			yield return System.IO.Path.Combine(toolsRoot, "tools", "ffmpeg", "bin");
			yield return System.IO.Path.Combine(toolsRoot, "ffmpeg", "bin");
		}

		yield return "C:\\ffmpeg\\bin";
	}

	private static string ExtractWhisperError(string body)
	{
		if (string.IsNullOrWhiteSpace(body))
		{
			return "Whisper returned an error.";
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(body);
			if (jsonDocument.RootElement.TryGetProperty("error", out var value))
			{
				return value.GetString() ?? body;
			}
		}
		catch (Exception exception)
		{
			AppLog.Warn("Could not parse Whisper error response.", exception);
		}
		return body;
	}

	private async Task<string> RunOneShotWhisperAsync(string audioPath, TranscriptionModelOption model)
	{
		string text = WhisperLanguageFromLocale(_settings.LocaleId);
		string outputRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maxflow-whisper-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(outputRoot);
		string text2 = (_settings.WhisperDeviceId.Equals("cuda", StringComparison.OrdinalIgnoreCase) ? "cuda" : "cpu");
		ProcessStartInfo processStartInfo = new ProcessStartInfo
		{
			FileName = _settings.WhisperPythonPath,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		processStartInfo.Environment["PYTHONUTF8"] = "1";
		processStartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
		processStartInfo.Environment["XDG_CACHE_HOME"] = AppConfig.Current.Paths.CacheRoot;
		string text3 = ResolveFfmpegDirectory();
		if (!string.IsNullOrWhiteSpace(text3))
		{
			string value;
			string text4 = (processStartInfo.Environment.TryGetValue("PATH", out value) ? value : (Environment.GetEnvironmentVariable("PATH") ?? ""));
			processStartInfo.Environment["PATH"] = text3 + System.IO.Path.PathSeparator + text4;
		}
		processStartInfo.ArgumentList.Add("-m");
		processStartInfo.ArgumentList.Add("whisper");
		processStartInfo.ArgumentList.Add(audioPath);
		processStartInfo.ArgumentList.Add("--model");
		processStartInfo.ArgumentList.Add(model.WhisperArgument);
		processStartInfo.ArgumentList.Add("--model_dir");
		processStartInfo.ArgumentList.Add(System.IO.Path.GetDirectoryName(model.ModelPath) ?? TranscriptionModelOption.DefaultModelRoot);
		processStartInfo.ArgumentList.Add("--output_dir");
		processStartInfo.ArgumentList.Add(outputRoot);
		processStartInfo.ArgumentList.Add("--output_format");
		processStartInfo.ArgumentList.Add("txt");
		processStartInfo.ArgumentList.Add("--device");
		processStartInfo.ArgumentList.Add(text2);
		processStartInfo.ArgumentList.Add("--fp16");
		processStartInfo.ArgumentList.Add((text2 == "cuda") ? "True" : "False");
		if (!string.IsNullOrWhiteSpace(text))
		{
			processStartInfo.ArgumentList.Add("--language");
			processStartInfo.ArgumentList.Add(text);
		}
		using Process process = new Process
		{
			StartInfo = processStartInfo
		};
		process.Start();
		Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
		Task<string> errorTask = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		string output = (await outputTask).Trim();
		string text5 = (await errorTask).Trim();
		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException(string.IsNullOrWhiteSpace(text5) ? $"Whisper exited with code {process.ExitCode}" : text5);
		}
		string text6 = Directory.EnumerateFiles(outputRoot, "*.txt").FirstOrDefault();
		if (text6 != null)
		{
			output = File.ReadAllText(text6, Encoding.UTF8).Trim();
		}
		if (string.IsNullOrWhiteSpace(output))
		{
			throw new InvalidOperationException("Whisper returned an empty transcript.");
		}
		return output;
	}

	private void SetTranscriptionControlsEnabled(bool isEnabled)
	{
		RecordButton.IsEnabled = isEnabled;
		OpenAudioButton.IsEnabled = isEnabled;
		StopLoadedModelButton.IsEnabled = isEnabled;
	}

	private void SetRecordButtonState(bool isRecording)
	{
		System.Windows.Media.Brush brush = (System.Windows.Media.Brush)TryFindResource("SpeakReadyBrush");
		System.Windows.Media.Brush brush2 = (System.Windows.Media.Brush)TryFindResource("SpeakRecordingBrush");
		RecordButton.Background = (isRecording ? brush2 : brush);
		RecordButton.BorderBrush = (isRecording ? ResourceBrush("GoldBrush") : ResourceBrush("DeepGoldBrush"));
		RecordButton.Foreground = System.Windows.Media.Brushes.White;
		RecordButtonGlyph.Visibility = (isRecording ? Visibility.Collapsed : Visibility.Visible);
		RecordActivityPanel.Visibility = ((!isRecording) ? Visibility.Collapsed : Visibility.Visible);
		RecordButton.ToolTip = (isRecording ? "Stop recording" : "Start recording");
		PlayRecordingFeedbackIfChanged(isRecording);
		RefreshTrayMenu();
		ScaleTransform scaleTransform2;
		if (RecordButton.RenderTransform is ScaleTransform scaleTransform)
		{
			scaleTransform2 = (scaleTransform.IsFrozen ? scaleTransform.CloneCurrentValue() : scaleTransform);
			if (scaleTransform2 != scaleTransform)
			{
				RecordButton.RenderTransform = scaleTransform2;
			}
		}
		else
		{
			scaleTransform2 = new ScaleTransform(1.0, 1.0);
			RecordButton.RenderTransform = scaleTransform2;
		}
		RecordButton.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
		if (isRecording)
		{
			DoubleAnimation animation = new DoubleAnimation
			{
				From = 1.0,
				To = 1.06,
				Duration = TimeSpan.FromMilliseconds(800.0),
				AutoReverse = true,
				RepeatBehavior = RepeatBehavior.Forever,
				EasingFunction = new SineEase
				{
					EasingMode = EasingMode.EaseInOut
				}
			};
			scaleTransform2.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
			scaleTransform2.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
			AnimateRecordingBars(RecordButtonBars());
			DropShadowEffect dropShadowEffect = EnsureDropShadowEffect(RecordButton);
			if (dropShadowEffect != null)
			{
				dropShadowEffect.Color = System.Windows.Media.Color.FromRgb(184, 177, 164);
				DoubleAnimation animation2 = new DoubleAnimation
				{
					From = 18.0,
					To = 20.0,
					Duration = TimeSpan.FromMilliseconds(800.0),
					AutoReverse = true,
					RepeatBehavior = RepeatBehavior.Forever,
					EasingFunction = new SineEase
					{
						EasingMode = EasingMode.EaseInOut
					}
				};
				dropShadowEffect.BeginAnimation(DropShadowEffect.BlurRadiusProperty, animation2);
			}
			RecordRipple1.Fill = brush2;
			DoubleAnimation animation3 = new DoubleAnimation
			{
				From = 1.0,
				To = 1.34,
				Duration = TimeSpan.FromMilliseconds(1200.0),
				RepeatBehavior = RepeatBehavior.Forever
			};
			DoubleAnimation animation4 = new DoubleAnimation
			{
				From = 0.24,
				To = 0.0,
				Duration = TimeSpan.FromMilliseconds(1200.0),
				RepeatBehavior = RepeatBehavior.Forever
			};
			RippleScale1.BeginAnimation(ScaleTransform.ScaleXProperty, animation3);
			RippleScale1.BeginAnimation(ScaleTransform.ScaleYProperty, animation3);
			RecordRipple1.BeginAnimation(UIElement.OpacityProperty, animation4);
			RecordRipple2.Fill = brush2;
			DoubleAnimation animation5 = new DoubleAnimation
			{
				From = 1.0,
				To = 1.34,
				Duration = TimeSpan.FromMilliseconds(1200.0),
				BeginTime = TimeSpan.FromMilliseconds(600.0),
				RepeatBehavior = RepeatBehavior.Forever
			};
			DoubleAnimation animation6 = new DoubleAnimation
			{
				From = 0.24,
				To = 0.0,
				Duration = TimeSpan.FromMilliseconds(1200.0),
				BeginTime = TimeSpan.FromMilliseconds(600.0),
				RepeatBehavior = RepeatBehavior.Forever
			};
			RippleScale2.BeginAnimation(ScaleTransform.ScaleXProperty, animation5);
			RippleScale2.BeginAnimation(ScaleTransform.ScaleYProperty, animation5);
			RecordRipple2.BeginAnimation(UIElement.OpacityProperty, animation6);
		}
		else
		{
			scaleTransform2.BeginAnimation(ScaleTransform.ScaleXProperty, null);
			scaleTransform2.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			scaleTransform2.ScaleX = 1.0;
			scaleTransform2.ScaleY = 1.0;
			StopRecordingBars(RecordButtonBars());
			DropShadowEffect dropShadowEffect2 = EnsureDropShadowEffect(RecordButton);
			if (dropShadowEffect2 != null)
			{
				dropShadowEffect2.Color = System.Windows.Media.Color.FromRgb(184, 177, 164);
				dropShadowEffect2.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
				dropShadowEffect2.BlurRadius = 10.0;
			}
			RippleScale1.BeginAnimation(ScaleTransform.ScaleXProperty, null);
			RippleScale1.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			RecordRipple1.BeginAnimation(UIElement.OpacityProperty, null);
			RecordRipple1.Opacity = 0.0;
			RippleScale2.BeginAnimation(ScaleTransform.ScaleXProperty, null);
			RippleScale2.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			RecordRipple2.BeginAnimation(UIElement.OpacityProperty, null);
			RecordRipple2.Opacity = 0.0;
		}
	}

	private static DropShadowEffect? EnsureDropShadowEffect(FrameworkElement element)
	{
		if (!(element.Effect is DropShadowEffect dropShadowEffect))
		{
			return null;
		}
		DropShadowEffect dropShadowEffect2 = (dropShadowEffect.IsFrozen ? dropShadowEffect.CloneCurrentValue() : dropShadowEffect);
		if (dropShadowEffect2 != dropShadowEffect)
		{
			element.Effect = dropShadowEffect2;
		}
		return dropShadowEffect2;
	}

	private Border[] RecordButtonBars()
	{
		return new Border[5] { RecordBar1, RecordBar2, RecordBar3, RecordBar4, RecordBar5 };
	}

	private static void StopRecordingBars(IReadOnlyList<Border> bars)
	{
		ScaleTransform scaleTransform2;
		double scaleY;
		for (int i = 0; i < bars.Count; scaleTransform2.ScaleY = scaleY, bars[i].Opacity = 1.0, i++)
		{
			ScaleTransform scaleTransform = EnsureScaleTransform(bars[i]);
			scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			bars[i].BeginAnimation(UIElement.OpacityProperty, null);
			scaleTransform2 = scaleTransform;
			bool flag;
			switch (i)
			{
			case 2:
				scaleY = 1.0;
				continue;
			case 1:
			case 3:
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			scaleY = (flag ? 0.7 : 0.42);
		}
	}

	private static void AnimateRecordingBars(IReadOnlyList<Border> bars)
	{
		for (int i = 0; i < bars.Count; i++)
		{
			EnsureScaleTransform(bars[i]).BeginAnimation(animation: new DoubleAnimation
			{
				From = ((i == 2) ? 0.52 : 0.36),
				To = ((i == 2) ? 1.0 : 0.92),
				Duration = TimeSpan.FromMilliseconds(460 + i % 3 * 80),
				BeginTime = TimeSpan.FromMilliseconds(i * 85),
				AutoReverse = true,
				RepeatBehavior = RepeatBehavior.Forever,
				EasingFunction = new SineEase
				{
					EasingMode = EasingMode.EaseInOut
				}
			}, dp: ScaleTransform.ScaleYProperty);
			DoubleAnimation animation = new DoubleAnimation
			{
				From = 0.62,
				To = 1.0,
				Duration = TimeSpan.FromMilliseconds(560.0),
				BeginTime = TimeSpan.FromMilliseconds(i * 70),
				AutoReverse = true,
				RepeatBehavior = RepeatBehavior.Forever,
				EasingFunction = new SineEase
				{
					EasingMode = EasingMode.EaseInOut
				}
			};
			bars[i].BeginAnimation(UIElement.OpacityProperty, animation);
		}
	}

	private void PlayRecordingFeedbackIfChanged(bool isRecording)
	{
		bool? lastFeedbackRecordingState = _lastFeedbackRecordingState;
		if (!lastFeedbackRecordingState.HasValue)
		{
			_lastFeedbackRecordingState = isRecording;
		}
		else if (_lastFeedbackRecordingState != isRecording)
		{
			_lastFeedbackRecordingState = isRecording;
			if (isRecording)
			{
				RecordingFeedbackSound.PlayStart();
			}
			else
			{
				RecordingFeedbackSound.PlayStop();
			}
		}
	}

	private async void FormatButton_Click(object sender, RoutedEventArgs e)
	{
		string text = TranscriptionDeliveryPolicy.ResolveText(await FormatCurrentAsync(addToHistory: true), FormattedOutputTextBox.Text);
		if (TranscriptionDeliveryPolicy.ShouldDeliver(text))
		{
			TryCopyToClipboard(text);
		}
	}

	private void ClearButton_Click(object sender, RoutedEventArgs e)
	{
		RawTranscriptTextBox.Clear();
		FormattedOutputTextBox.Clear();
		StatusTextBlock.Text = "Cleared";
	}

	private void CopyButton_Click(object sender, RoutedEventArgs e)
	{
		string text = CurrentFormattedText();
		if (string.IsNullOrWhiteSpace(text))
		{
			StatusTextBlock.Text = "Nothing to copy";
		}
		else
		{
			TryCopyToClipboard(text);
		}
	}

	private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (HistoryListBox.SelectedItem is TranscriptCard transcriptCard)
		{
			StatusTextBlock.Text = "Selected " + transcriptCard.ModeId + " history";
		}
		UpdateHistorySelectionDetails();
	}

	private void OpenSelectedHistoryButton_Click(object sender, RoutedEventArgs e)
	{
		if (!(HistoryListBox.SelectedItem is TranscriptCard card))
		{
			StatusTextBlock.Text = "Select a history item first";
			return;
		}
		LoadHistoryCard(card);
		SetActiveTab("dictate");
	}

	private void CopySelectedHistoryButton_Click(object sender, RoutedEventArgs e)
	{
		if (!(HistoryListBox.SelectedItem is TranscriptCard transcriptCard))
		{
			StatusTextBlock.Text = "Select a history item first";
		}
		else
		{
			TryCopyToClipboard(transcriptCard.FormattedText);
		}
	}

	private async void RetrySelectedHistoryButton_Click(object sender, RoutedEventArgs e)
	{
		object selectedItem = HistoryListBox.SelectedItem;
		TranscriptCard card = selectedItem as TranscriptCard;
		if (card == null)
		{
			StatusTextBlock.Text = "Select a history item first";
			return;
		}
		string text = ResolveHistoryAudioPath(card);
		if (string.IsNullOrWhiteSpace(text))
		{
			StatusTextBlock.Text = "Saved audio unavailable";
			HistoryComparisonTextBox.Text = "This history item does not have a saved audio file available. Open it and re-polish the raw text, or use a newer item recorded after this update.";
			return;
		}
		try
		{
			_isTranscribing = true;
			SetTranscriptionControlsEnabled(isEnabled: false);
			StatusTextBlock.Text = "Retrying selected audio";
			HistoryComparisonTextBox.Text = "Retrying with the current model...";
			UpdateShortcutWidgetState();
			DeliveryCommand deliveryCommand = ExtractDeliveryCommand(await TranscribeAudioForCurrentSettingsAsync(text));
			DictationMode mode = DictationMode.Presets.FirstOrDefault((DictationMode item) => item.Id.Equals(card.ModeId, StringComparison.OrdinalIgnoreCase)) ?? _selectedMode;
			string text2 = await FormatTranscriptTextAsync(deliveryCommand.Text, mode, updateStatus: false);
			_lastRetryRawText = deliveryCommand.Text;
			_lastRetryFormattedText = text2;
			_lastRetrySourceLabel = CurrentTranscriptionSourceLabel();
			HistoryComparisonTextBox.Text = BuildHistoryComparison(card, _lastRetryRawText, text2, _lastRetrySourceLabel);
			StatusTextBlock.Text = "Retry comparison ready";
			ShowCompletionToast("Speak comparison ready", _lastRetrySourceLabel);
		}
		catch (Exception ex)
		{
			StatusTextBlock.Text = "Retry failed";
			HistoryComparisonTextBox.Text = ex.Message;
			AppLog.Warn("History retry failed.", ex);
		}
		finally
		{
			_isTranscribing = false;
			SetTranscriptionControlsEnabled(isEnabled: true);
			UpdateShortcutWidgetState();
		}
	}

	private void UseRetryOutputButton_Click(object sender, RoutedEventArgs e)
	{
		if (string.IsNullOrWhiteSpace(_lastRetryFormattedText))
		{
			StatusTextBlock.Text = "Run a retry first";
			return;
		}
		RawTranscriptTextBox.Text = _lastRetryRawText;
		FormattedOutputTextBox.Text = _lastRetryFormattedText;
		StatusTextBlock.Text = (string.IsNullOrWhiteSpace(_lastRetrySourceLabel) ? "Retry output loaded" : ("Retry output loaded from " + _lastRetrySourceLabel));
		SetActiveTab("dictate");
	}

	private string? ResolveHistoryAudioPath(TranscriptCard card)
	{
		if (!string.IsNullOrWhiteSpace(card.AudioPath) && File.Exists(card.AudioPath))
		{
			return card.AudioPath;
		}
		string text = (string.IsNullOrWhiteSpace(card.AudioPath) ? "" : System.IO.Path.GetFileName(card.AudioPath));
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		string[] array = new string[2]
		{
			System.IO.Path.Combine(_store.Root, "recordings", text),
			System.IO.Path.Combine(_store.Root, "recordings-archive")
		};
		if (File.Exists(array[0]))
		{
			return array[0];
		}
		if (Directory.Exists(array[1]))
		{
			return Directory.EnumerateFiles(array[1], text, SearchOption.AllDirectories).FirstOrDefault();
		}
		return null;
	}

	private string CurrentTranscriptionSourceLabel()
	{
		if (_settings.EngineId.Equals("cloud-stt", StringComparison.OrdinalIgnoreCase))
		{
			return CloudSttProviderOption.Find(_settings.SttCloudProviderId).Name + " - " + _settings.SttCloudModel;
		}
		return SelectedTranscriptionModel()?.Name ?? _settings.TranscriptionModelId;
	}

	private static string BuildHistoryComparison(TranscriptCard original, string retryRaw, string retryFormatted, string retrySource)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("Original");
		stringBuilder.AppendLine(original.SourceLabel);
		stringBuilder.AppendLine();
		stringBuilder.AppendLine(original.FormattedText.Trim());
		stringBuilder.AppendLine();
		stringBuilder.AppendLine("Retry");
		stringBuilder.AppendLine(retrySource);
		stringBuilder.AppendLine();
		stringBuilder.AppendLine(retryFormatted.Trim());
		if (!string.Equals(original.RawText.Trim(), retryRaw.Trim(), StringComparison.Ordinal))
		{
			stringBuilder.AppendLine();
			stringBuilder.AppendLine("Retry raw transcript");
			stringBuilder.AppendLine(retryRaw.Trim());
		}
		return stringBuilder.ToString().Trim();
	}

	private void SaveHistoryTagsButton_Click(object sender, RoutedEventArgs e)
	{
		if (!(HistoryListBox.SelectedItem is TranscriptCard transcriptCard))
		{
			StatusTextBlock.Text = "Select a history item first";
			return;
		}
		transcriptCard.Tags = HistoryTagsTextBox.Text.Trim();
		InvalidateStatsCache();
		SaveHistoryInBackground();
		_historyView.Refresh();
		UpdateLibraryStats();
		StatusTextBlock.Text = "History tags saved";
	}

	private void ExportHistoryBackupButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			string detail = ExportHistoryBackup();
			StatusTextBlock.Text = "History backup exported";
			ShowCompletionToast("Speak backup exported", detail);
		}
		catch (Exception exception)
		{
			StatusTextBlock.Text = "Backup export failed";
			AppLog.Warn("History backup export failed.", exception);
		}
	}

	private string ExportHistoryBackup()
	{
		string text = (Directory.Exists("E:\\") ? System.IO.Path.Combine("E:\\", "Speak-App-Backups", "Exports") : System.IO.Path.Combine(_store.Root, "exports"));
		Directory.CreateDirectory(text);
		string text2 = System.IO.Path.Combine(_store.Root, "exports", "scratch-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(text2);
		try
		{
			SafeCopyIfExists(System.IO.Path.Combine(_store.Root, "history.json"), System.IO.Path.Combine(text2, "history.json"));
			SafeCopyIfExists(System.IO.Path.Combine(_store.Root, "vocabulary.json"), System.IO.Path.Combine(text2, "vocabulary.json"));
			SafeCopyIfExists(System.IO.Path.Combine(_store.Root, "settings.json"), System.IO.Path.Combine(text2, "settings.json"));
			var value = new
			{
				createdAt = DateTimeOffset.Now,
				dataRoot = _store.Root,
				historyItems = _history.Count,
				vocabularyItems = _vocabulary.Count,
				app = "Speak"
			};
			File.WriteAllText(System.IO.Path.Combine(text2, "export-manifest.json"), JsonSerializer.Serialize(value, _jsonOptions), Encoding.UTF8);
			string text3 = System.IO.Path.Combine(text, $"Speak-history-backup-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip");
			if (File.Exists(text3))
			{
				File.Delete(text3);
			}
			ZipFile.CreateFromDirectory(text2, text3, CompressionLevel.Optimal, includeBaseDirectory: false);
			AppLog.Info("Exported Speak history backup to " + text3 + ".");
			return text3;
		}
		finally
		{
			if (Directory.Exists(text2))
			{
				Directory.Delete(text2, recursive: true);
			}
		}
	}

	private static void SafeCopyIfExists(string source, string destination)
	{
		if (File.Exists(source))
		{
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination));
			File.Copy(source, destination, overwrite: true);
		}
	}

	private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
	{
		_history.Clear();
		InvalidateStatsCache();
		SaveHistoryInBackground();
		StatusTextBlock.Text = "History cleared";
		UpdateLibraryStats();
		UpdateHistorySelectionDetails();
	}

	private void HistorySearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		DebounceHistorySearchRefresh();
	}

	private void DebounceHistorySearchRefresh()
	{
		_historySearchRefreshTimer.Stop();
		_historySearchRefreshTimer.Start();
	}

	private void RefreshHistorySearchNow()
	{
		_historyView.Refresh();
		UpdateLibraryStats();
		UpdateHistorySelectionDetails();
	}

	private void SettingsControl_Changed(object sender, RoutedEventArgs e)
	{
		if (!_isLoading)
		{
			if (sender == ModelKeepAliveComboBox)
			{
				_ = StopWarmEngineIfNeededAsync();
			}
			if (sender == AutoLearnCorrectionsCheckBox)
			{
				SyncAutoLearnCorrectionControls(AutoLearnCorrectionsCheckBox.IsChecked == true);
			}
			if (sender == LlmPolishProviderComboBox)
			{
				ApplySelectedPolishProviderDefaults();
				SaveSettingsFromUi();
				RefreshLlmPolishModelsAsync(quiet: false);
			}
			else if (sender == CloudSttProviderComboBox)
			{
				ApplySelectedCloudSttProviderDefaults();
				SaveSettingsFromUi();
				RefreshCloudSttModelsAsync(quiet: false);
			}
			else
			{
				SaveSettingsFromUi();
			}
		}
	}

	private void DictionaryAutoLearnCheckBox_Changed(object sender, RoutedEventArgs e)
	{
		if (!_isLoading)
		{
			bool valueOrDefault = DictionaryAutoLearnCheckBox.IsChecked == true;
			SyncAutoLearnCorrectionControls(valueOrDefault);
			SaveSettingsFromUi();
			StatusTextBlock.Text = (valueOrDefault ? "Auto-add corrections is on" : "Auto-add corrections is off");
		}
	}

	private void SyncAutoLearnCorrectionControls(bool isEnabled)
	{
		bool isLoading = _isLoading;
		_isLoading = true;
		try
		{
			AutoLearnCorrectionsCheckBox.IsChecked = isEnabled;
			DictionaryAutoLearnCheckBox.IsChecked = isEnabled;
		}
		finally
		{
			_isLoading = isLoading;
		}
	}

	private async void RefreshPolishModelsButton_Click(object sender, RoutedEventArgs e)
	{
		await RefreshLlmPolishModelsAsync(quiet: false);
	}

	private async void TestPolishProviderButton_Click(object sender, RoutedEventArgs e)
	{
		await RefreshLlmPolishModelsAsync(quiet: false);
		if (_llmPolishModels.Count > 0)
		{
			StatusTextBlock.Text = "Provider test passed";
		}
	}

	private async void RefreshCloudSttModelsButton_Click(object sender, RoutedEventArgs e)
	{
		await RefreshCloudSttModelsAsync(quiet: false);
	}

	private async void TestCloudSttProviderButton_Click(object sender, RoutedEventArgs e)
	{
		await RefreshCloudSttModelsAsync(quiet: false);
		if (_cloudSttModels.Count > 0)
		{
			StatusTextBlock.Text = "Cloud STT provider test passed";
		}
	}

	private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
	{
		SaveSettingsFromUi();
		StatusTextBlock.Text = "Settings saved";
	}

	private void ApplySelectedPolishProviderDefaults()
	{
		LlmPolishProviderOption llmPolishProviderOption = LlmPolishProviderOption.Find((LlmPolishProviderComboBox.SelectedValue as string) ?? _settings.LlmPolishProviderId);
		_isLoading = true;
		LlmPolishEndpointTextBox.Text = llmPolishProviderOption.DefaultEndpoint;
		SeedLlmPolishModels(llmPolishProviderOption.DefaultModel);
		LlmPolishModelComboBox.Text = llmPolishProviderOption.DefaultModel;
		LlmPolishApiKeyEnvTextBox.Text = llmPolishProviderOption.DefaultApiKeyEnvironmentVariable;
		_isLoading = false;
	}

	private void ApplySelectedCloudSttProviderDefaults()
	{
		CloudSttProviderOption cloudSttProviderOption = CloudSttProviderOption.Find((CloudSttProviderComboBox.SelectedValue as string) ?? _settings.SttCloudProviderId);
		_isLoading = true;
		CloudSttEndpointTextBox.Text = cloudSttProviderOption.DefaultEndpoint;
		SeedCloudSttModels(cloudSttProviderOption.DefaultModel);
		CloudSttModelComboBox.Text = cloudSttProviderOption.DefaultModel;
		CloudSttApiKeyEnvTextBox.Text = cloudSttProviderOption.DefaultApiKeyEnvironmentVariable;
		_isLoading = false;
	}

	private void ScheduleProviderRefresh()
	{
		base.Dispatcher.BeginInvoke((Action)async delegate
		{
			await Task.Delay(350);
			await RefreshLlmPolishModelsAsync(quiet: true);
			await RefreshCloudSttModelsAsync(quiet: true);
		}, DispatcherPriority.Background);
	}

	private bool TryUseCachedModelDiscovery(string kind, MaxFlowSettings settings, out LlmModelDiscoveryResult result)
	{
		string key = ModelDiscoveryCacheKey(kind, settings);
		if (_modelDiscoveryCache.TryGetValue(key, out ModelDiscoveryCacheEntry modelDiscoveryCacheEntry) && DateTimeOffset.Now - modelDiscoveryCacheEntry.CapturedAt < ModelDiscoveryCacheDuration)
		{
			result = modelDiscoveryCacheEntry.Result;
			return true;
		}
		result = new LlmModelDiscoveryResult(Array.Empty<string>(), "", UsedFallback: true);
		return false;
	}

	private void StoreModelDiscoveryResult(string kind, MaxFlowSettings settings, LlmModelDiscoveryResult result)
	{
		_modelDiscoveryCache[ModelDiscoveryCacheKey(kind, settings)] = new ModelDiscoveryCacheEntry(DateTimeOffset.Now, result);
	}

	private static string ModelDiscoveryCacheKey(string kind, MaxFlowSettings settings)
	{
		if (kind.Equals("stt", StringComparison.OrdinalIgnoreCase))
		{
			return string.Join("|", kind, settings.SttCloudProviderId, settings.SttCloudEndpoint, settings.SttCloudApiKeyEnvironmentVariable);
		}
		return string.Join("|", kind, settings.LlmPolishProviderId, settings.LlmPolishEndpoint, settings.LlmPolishApiKeyEnvironmentVariable);
	}

	private async Task RefreshLlmPolishModelsAsync(bool quiet)
	{
		if (_isLoading)
		{
			return;
		}
		try
		{
			MaxFlowSettings settings = SettingsFromUiWithoutSaving();
			NormalizeLlmApiKeySetting(settings);
			if (!string.Equals(LlmPolishApiKeyEnvTextBox.Text, settings.LlmPolishApiKeyEnvironmentVariable, StringComparison.Ordinal))
			{
				_isLoading = true;
				LlmPolishApiKeyEnvTextBox.Text = settings.LlmPolishApiKeyEnvironmentVariable;
				_isLoading = false;
			}
			if (!quiet)
			{
				StatusTextBlock.Text = "Loading provider models";
				LlmPolishStatusTextBlock.Text = "Loading models from selected provider...";
			}
			LlmModelDiscoveryResult llmModelDiscoveryResult;
			bool flag = TryUseCachedModelDiscovery("llm", settings, out llmModelDiscoveryResult);
			if (!flag)
			{
				llmModelDiscoveryResult = await _llmModelDiscovery.LoadModelsAsync(settings);
				StoreModelDiscoveryResult("llm", settings, llmModelDiscoveryResult);
			}
			bool preserveMissingModel = ShouldPreserveMissingLlmModel(settings.LlmPolishProviderId, llmModelDiscoveryResult);
			ReplaceLlmPolishModels(llmModelDiscoveryResult.Models, settings.LlmPolishModel, preserveMissingModel);
			if (string.IsNullOrWhiteSpace(LlmPolishModelComboBox.Text) && _llmPolishModels.Count > 0)
			{
				LlmPolishModelComboBox.Text = _llmPolishModels[0];
			}
			LlmPolishStatusTextBlock.Text = llmModelDiscoveryResult.Detail + (flag ? " Cached." : "");
			if (!quiet)
			{
				StatusTextBlock.Text = (flag ? "Provider models cached" : (llmModelDiscoveryResult.UsedFallback ? "Loaded fallback models" : "Provider models loaded"));
			}
		}
		catch (Exception ex)
		{
			AppLog.Warn("Could not refresh LLM model list.", ex);
			if (!quiet)
			{
				StatusTextBlock.Text = "Model refresh failed";
				LlmPolishStatusTextBlock.Text = ex.Message;
			}
		}
	}

	private void SeedLlmPolishModels(params string[] modelIds)
	{
		foreach (string item in modelIds.Where((string model) => !string.IsNullOrWhiteSpace(model)))
		{
			if (!_llmPolishModels.Contains<string>(item, StringComparer.OrdinalIgnoreCase))
			{
				_llmPolishModels.Add(item);
			}
		}
	}

	private static bool ShouldPreserveMissingLlmModel(string providerId, LlmModelDiscoveryResult discoveryResult)
	{
		if (discoveryResult.UsedFallback)
		{
			return true;
		}
		return !LlmPolishProviderOption.Find(providerId).Id.Equals("lm-studio", StringComparison.OrdinalIgnoreCase);
	}

	private void ReplaceLlmPolishModels(IReadOnlyList<string> modelIds, string preferredModel, bool preserveMissingModel)
	{
		_isLoading = true;
		try
		{
			string text = FirstNonEmpty(preferredModel, LlmPolishModelComboBox.Text);
			_llmPolishModels.Clear();
			foreach (string item in modelIds.Where((string model) => !string.IsNullOrWhiteSpace(model)))
			{
				if (!_llmPolishModels.Contains<string>(item, StringComparer.OrdinalIgnoreCase))
				{
					_llmPolishModels.Add(item);
				}
			}
			if (!string.IsNullOrWhiteSpace(text) && !_llmPolishModels.Contains<string>(text, StringComparer.OrdinalIgnoreCase) && preserveMissingModel)
			{
				_llmPolishModels.Insert(0, text);
			}
			LlmPolishModelComboBox.Text = ((!string.IsNullOrWhiteSpace(text) && _llmPolishModels.Contains<string>(text, StringComparer.OrdinalIgnoreCase)) ? text : (_llmPolishModels.FirstOrDefault() ?? ""));
		}
		finally
		{
			_isLoading = false;
		}
	}

	private async Task RefreshCloudSttModelsAsync(bool quiet)
	{
		if (_isLoading)
		{
			return;
		}
		try
		{
			MaxFlowSettings settings = SettingsFromUiWithoutSaving();
			NormalizeCloudSttApiKeySetting(settings);
			if (!string.Equals(CloudSttApiKeyEnvTextBox.Text, settings.SttCloudApiKeyEnvironmentVariable, StringComparison.Ordinal))
			{
				_isLoading = true;
				CloudSttApiKeyEnvTextBox.Text = settings.SttCloudApiKeyEnvironmentVariable;
				_isLoading = false;
			}
			if (!quiet)
			{
				StatusTextBlock.Text = "Loading STT models";
				CloudSttStatusTextBlock.Text = "Loading speech-to-text models from selected provider...";
			}
			LlmModelDiscoveryResult llmModelDiscoveryResult;
			bool flag = TryUseCachedModelDiscovery("stt", settings, out llmModelDiscoveryResult);
			if (!flag)
			{
				llmModelDiscoveryResult = await _llmModelDiscovery.LoadSpeechModelsAsync(settings);
				StoreModelDiscoveryResult("stt", settings, llmModelDiscoveryResult);
			}
			ReplaceCloudSttModels(llmModelDiscoveryResult.Models, settings.SttCloudModel);
			if (string.IsNullOrWhiteSpace(CloudSttModelComboBox.Text) && _cloudSttModels.Count > 0)
			{
				CloudSttModelComboBox.Text = _cloudSttModels[0];
			}
			CloudSttStatusTextBlock.Text = llmModelDiscoveryResult.Detail + (flag ? " Cached." : "");
			if (!quiet)
			{
				StatusTextBlock.Text = (flag ? "Cloud STT models cached" : (llmModelDiscoveryResult.UsedFallback ? "Loaded fallback STT models" : "Cloud STT models loaded"));
			}
		}
		catch (Exception ex)
		{
			AppLog.Warn("Could not refresh cloud STT model list.", ex);
			if (!quiet)
			{
				StatusTextBlock.Text = "STT model refresh failed";
				CloudSttStatusTextBlock.Text = ex.Message;
			}
		}
	}

	private void SeedCloudSttModels(params string[] modelIds)
	{
		foreach (string item in modelIds.Where((string model) => !string.IsNullOrWhiteSpace(model)))
		{
			if (!_cloudSttModels.Contains<string>(item, StringComparer.OrdinalIgnoreCase))
			{
				_cloudSttModels.Add(item);
			}
		}
	}

	private void ReplaceCloudSttModels(IReadOnlyList<string> modelIds, string preferredModel)
	{
		_isLoading = true;
		try
		{
			string text = FirstNonEmpty(preferredModel, CloudSttModelComboBox.Text);
			_cloudSttModels.Clear();
			foreach (string item in modelIds.Where((string model) => !string.IsNullOrWhiteSpace(model)))
			{
				if (!_cloudSttModels.Contains<string>(item, StringComparer.OrdinalIgnoreCase))
				{
					_cloudSttModels.Add(item);
				}
			}
			if (!string.IsNullOrWhiteSpace(text) && !_cloudSttModels.Contains<string>(text, StringComparer.OrdinalIgnoreCase))
			{
				_cloudSttModels.Insert(0, text);
			}
			CloudSttModelComboBox.Text = ((!string.IsNullOrWhiteSpace(text)) ? text : (_cloudSttModels.FirstOrDefault() ?? ""));
		}
		finally
		{
			_isLoading = false;
		}
	}

	private MaxFlowSettings SettingsFromUiWithoutSaving()
	{
		TranscriptionModelOption transcriptionModelOption = SelectedTranscriptionModel() ?? _transcriptionModels.First();
		return new MaxFlowSettings
		{
			LocaleId = ((LocaleComboBox.SelectedValue as string) ?? _settings.LocaleId),
			EngineId = ((EngineComboBox.SelectedValue as string) ?? _settings.EngineId),
			TranscriptionModelId = transcriptionModelOption.Id,
			WhisperDeviceId = ((WhisperDeviceComboBox.SelectedValue as string) ?? _settings.WhisperDeviceId),
			ModelKeepAliveMinutes = ((ModelKeepAliveComboBox.SelectedValue is int num) ? num : _settings.ModelKeepAliveMinutes),
			WhisperPythonPath = _settings.WhisperPythonPath,
			WhisperWrapperPath = _settings.WhisperWrapperPath,
			WhisperModelPath = transcriptionModelOption.ModelPath,
			AudioInputDeviceNumber = ((AudioInputComboBox.SelectedValue is int num2) ? num2 : _settings.AudioInputDeviceNumber),
			OutputDestinationId = ((OutputDestinationComboBox.SelectedValue as string) ?? _settings.OutputDestinationId),
			SttCloudProviderId = ((CloudSttProviderComboBox.SelectedValue as string) ?? _settings.SttCloudProviderId),
			SttCloudEndpoint = CloudSttEndpointTextBox.Text.Trim(),
			SttCloudModel = CloudSttModelComboBox.Text.Trim(),
			SttCloudApiKeyEnvironmentVariable = CloudSttApiKeyEnvTextBox.Text.Trim(),
			LlmPolishProviderId = ((LlmPolishProviderComboBox.SelectedValue as string) ?? _settings.LlmPolishProviderId),
			LlmPolishEndpoint = LlmPolishEndpointTextBox.Text.Trim(),
			LlmPolishModel = LlmPolishModelComboBox.Text.Trim(),
			LlmPolishApiKeyEnvironmentVariable = LlmPolishApiKeyEnvTextBox.Text.Trim(),
			LlmPolishTimeoutSeconds = ((LlmPolishTimeoutComboBox.SelectedValue is int num3) ? num3 : _settings.LlmPolishTimeoutSeconds),
			ThemeId = ((ThemeComboBox.SelectedValue as string) ?? _settings.ThemeId),
			KeepHistory = (KeepHistoryCheckBox.IsChecked == true),
			DictationShortcut = _shortcutGesture.ToStorageString(),
			ShowShortcutWidget = (ShowWidgetCheckBox.IsChecked == true),
			MinimizeToTray = (MinimizeToTrayCheckBox.IsChecked == true),
			StartWithWindows = (StartWithWindowsCheckBox.IsChecked == true),
			RecordingRetentionDays = ((RecordingRetentionComboBox.SelectedValue is int num4) ? num4 : _settings.RecordingRetentionDays),
			ShowCompletionToast = (ShowCompletionToastCheckBox.IsChecked == true),
			AutoLearnCorrections = (AutoLearnCorrectionsCheckBox.IsChecked == true),
			TtsEngineId = SelectedTtsEngineId(),
			TtsVoiceId = SelectedTtsVoiceId(),
			TtsOutputRoot = FirstNonEmpty(_settings.TtsOutputRoot, MaxFlowSettings.Default.TtsOutputRoot),
			TtsLanguage = FirstNonEmpty(_settings.TtsLanguage, MaxFlowSettings.Default.TtsLanguage),
			TtsLastOutputPath = _settings.TtsLastOutputPath,
			QwenTtsCustomVoiceModelPath = _settings.QwenTtsCustomVoiceModelPath,
			QwenTtsBaseModelPath = _settings.QwenTtsBaseModelPath,
			QwenTtsVoiceDesignModelPath = _settings.QwenTtsVoiceDesignModelPath,
			VoiceCloneEngineId = (_audioCloneEngineComboBox?.SelectedValue as string) ?? _settings.VoiceCloneEngineId,
			VoiceCloneModelId = _settings.VoiceCloneModelId,
			VoiceCloneReferenceAudioPath = _audioCloneReferenceTextBox?.Text.Trim() ?? _settings.VoiceCloneReferenceAudioPath,
			VoiceCloneProfileName = _audioCloneNameTextBox?.Text.Trim() ?? _settings.VoiceCloneProfileName,
			VoiceCloneOutputRoot = FirstNonEmpty(_settings.VoiceCloneOutputRoot, MaxFlowSettings.Default.VoiceCloneOutputRoot),
			VoiceDesignModelId = _settings.VoiceDesignModelId,
			VoiceDesignPrompt = _audioDesignPromptTextBox?.Text.Trim() ?? _settings.VoiceDesignPrompt,
			VoiceDesignOutputRoot = FirstNonEmpty(_settings.VoiceDesignOutputRoot, MaxFlowSettings.Default.VoiceDesignOutputRoot)
		};
	}

	private async void StopLoadedModelButton_Click(object sender, RoutedEventArgs e)
	{
		await StopWhisperServerAsync();
	}

	private void ShortcutCaptureButton_Click(object sender, RoutedEventArgs e)
	{
		_isCapturingShortcut = true;
		ShortcutCaptureButton.Content = "Press shortcut...";
		ShortcutStatusTextBlock.Text = "Press the shortcut you want. Use at least two modifier keys, or one modifier plus a normal key.";
		ShortcutCaptureButton.Focus();
	}

	private void ResetShortcutButton_Click(object sender, RoutedEventArgs e)
	{
		_shortcutGesture = ShortcutGesture.Default;
		_settings.DictationShortcut = _shortcutGesture.ToStorageString();
		_store.SaveSettings(_settings);
		ConfigureShortcutHandling();
		UpdateShortcutUi();
		UpdateShortcutWidgetState();
		StatusTextBlock.Text = "Shortcut reset";
	}

	private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (_isCapturingShortcut)
		{
			ShortcutGesture shortcutGesture = ShortcutGesture.FromCapture((e.Key == Key.System) ? e.SystemKey : e.Key, Keyboard.Modifiers);
			e.Handled = true;
			if (!shortcutGesture.IsUsable())
			{
				ShortcutStatusTextBlock.Text = "Add another modifier, or press a normal key with a modifier.";
				return;
			}
			_shortcutGesture = shortcutGesture;
			_settings.DictationShortcut = shortcutGesture.ToStorageString();
			_store.SaveSettings(_settings);
			_isCapturingShortcut = false;
			ConfigureShortcutHandling();
			UpdateShortcutUi();
			UpdateShortcutWidgetState();
			StatusTextBlock.Text = "Shortcut saved";
		}
	}

	private void AddVocabularyButton_Click(object sender, RoutedEventArgs e)
	{
		VocabularyEntry vocabularyEntry = new VocabularyEntry();
		_vocabulary.Add(vocabularyEntry);
		InvalidateStatsCache();
		VocabularyGrid.SelectedItem = vocabularyEntry;
		VocabularyGrid.ScrollIntoView(vocabularyEntry);
		if (VocabularyGrid.Columns.Count > 0)
		{
			VocabularyGrid.CurrentCell = new DataGridCellInfo(vocabularyEntry, VocabularyGrid.Columns[0]);
			VocabularyGrid.BeginEdit();
		}
		StatusTextBlock.Text = "New dictionary row added";
		UpdateLibraryStats();
	}

	private void SaveVocabularyButton_Click(object sender, RoutedEventArgs e)
	{
		VocabularyGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
		VocabularyGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
		NormalizeVocabularyIds();
		InvalidateStatsCache();
		SaveVocabularyInBackground();
		StatusTextBlock.Text = "Vocabulary saved";
		UpdateLibraryStats();
	}

	private void ResetVocabularyButton_Click(object sender, RoutedEventArgs e)
	{
		_vocabulary.Clear();
		foreach (VocabularyEntry @default in VocabularyEntry.Defaults)
		{
			_vocabulary.Add(new VocabularyEntry
			{
				Spoken = @default.Spoken,
				Written = @default.Written
			});
		}
		InvalidateStatsCache();
		SaveVocabularyInBackground();
		StatusTextBlock.Text = "Vocabulary reset";
		UpdateLibraryStats();
	}

	private void ApproveLearnedCorrectionButton_Click(object sender, RoutedEventArgs e)
	{
		if (!(LearnedCorrectionsListBox.SelectedItem is VocabularyEntry entry))
		{
			StatusTextBlock.Text = "Select a learned correction first";
			return;
		}
		VocabularyMemory.ApproveLearnedCorrection(entry);
		InvalidateStatsCache();
		SaveVocabularyInBackground();
		VocabularyGrid.Items.Refresh();
		UpdateLibraryStats();
		StatusTextBlock.Text = "Learned correction approved";
	}

	private void UndoLearnedCorrectionButton_Click(object sender, RoutedEventArgs e)
	{
		if (!(LearnedCorrectionsListBox.SelectedItem is VocabularyEntry entry))
		{
			StatusTextBlock.Text = "Select a learned correction first";
			return;
		}
		if (!VocabularyMemory.UndoLearnedCorrection(_vocabulary, entry))
		{
			StatusTextBlock.Text = "Could not undo correction";
			return;
		}
		InvalidateStatsCache();
		SaveVocabularyInBackground();
		VocabularyGrid.Items.Refresh();
		UpdateLibraryStats();
		StatusTextBlock.Text = "Learned correction undone";
	}

	private void TeachCorrectionButton_Click(object sender, RoutedEventArgs e)
	{
		VocabularyGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
		VocabularyGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
		string text = CorrectionSpokenTextBox.Text.Trim();
		string text2 = CorrectionWrittenTextBox.Text.Trim();
		if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(text2))
		{
			StatusTextBlock.Text = "Add both correction fields first";
			return;
		}
		if (!VocabularyMemory.TrySaveCorrection(_vocabulary, text, text2, out VocabularyEntry savedEntry))
		{
			StatusTextBlock.Text = "Add both correction fields first";
			return;
		}
		NormalizeVocabularyIds();
		InvalidateStatsCache();
		SaveVocabularyInBackground();
		VocabularyGrid.Items.Refresh();
		VocabularyGrid.SelectedItem = savedEntry;
		VocabularyGrid.ScrollIntoView(savedEntry);
		UpdateLibraryStats();
		CorrectionSpokenTextBox.Clear();
		CorrectionWrittenTextBox.Clear();
		CorrectionSpokenTextBox.Focus();
		if (!string.IsNullOrWhiteSpace(RawTranscriptTextBox.Text) && !_isRecording && !_isTranscribing)
		{
			FormatCurrent(addToHistory: false);
		}
		StatusTextBlock.Text = "Correction saved to dictionary";
		AppLog.Info("Correction trainer saved a term.");
	}

	private void UseSelectionForCorrectionButton_Click(object sender, RoutedEventArgs e)
	{
		string text = RawTranscriptTextBox.SelectedText.Trim();
		string text2 = FormattedOutputTextBox.SelectedText.Trim();
		string text3 = HistoryRawTextBox.SelectedText.Trim();
		string text4 = HistorySelectedTextBox.SelectedText.Trim();
		string text5 = HistoryComparisonTextBox.SelectedText.Trim();
		string text6 = FirstNonEmpty(text, text3, text5, text2, text4);
		string text7 = FirstNonEmpty(text2, text4, text5, text, text3);
		if (string.IsNullOrWhiteSpace(text6) && string.IsNullOrWhiteSpace(text7))
		{
			StatusTextBlock.Text = "Select text first";
			return;
		}
		CorrectionSpokenTextBox.Text = text6;
		CorrectionWrittenTextBox.Text = text7;
		SetActiveTab("dictionary");
		CorrectionWrittenTextBox.Focus();
		CorrectionWrittenTextBox.SelectAll();
		StatusTextBlock.Text = "Selection loaded into trainer";
	}

	private TranscriptCard? FormatCurrent(bool addToHistory, string audioPath = "")
	{
		string text = RawTranscriptTextBox.Text.Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			StatusTextBlock.Text = "Add dictation text first";
			return null;
		}
		NormalizeVocabularyIds();
		string text2 = _formatter.Format(text, _selectedMode, _vocabulary);
		FormattedOutputTextBox.Text = text2;
		TranscriptCard transcriptCard = null;
		if (addToHistory && _settings.KeepHistory)
		{
			transcriptCard = CreateHistoryCard(text, text2, _selectedMode, audioPath);
			SaveNewHistoryCard(transcriptCard);
		}
		StatusTextBlock.Text = "Formatted";
		return transcriptCard;
	}

	private async Task<TranscriptCard?> FormatCurrentAsync(bool addToHistory, string audioPath = "")
	{
		string raw = RawTranscriptTextBox.Text.Trim();
		if (string.IsNullOrWhiteSpace(raw))
		{
			StatusTextBlock.Text = "Add dictation text first";
			return null;
		}
		string text = await FormatTranscriptTextAsync(raw, _selectedMode, updateStatus: true);
		FormattedOutputTextBox.Text = text;
		TranscriptCard transcriptCard = null;
		if (addToHistory && _settings.KeepHistory)
		{
			transcriptCard = CreateHistoryCard(raw, text, _selectedMode, audioPath);
			SaveNewHistoryCard(transcriptCard);
		}
		return transcriptCard;
	}

	private async Task<string> FormatTranscriptTextAsync(string raw, DictationMode mode, bool updateStatus)
	{
		NormalizeVocabularyIds();
		string text = _formatter.Format(raw, mode, _vocabulary);
		LlmPolishProviderOption provider = LlmPolishProviderOption.Find(_settings.LlmPolishProviderId);
		if (provider.Id.Equals("off", StringComparison.OrdinalIgnoreCase))
		{
			if (updateStatus)
			{
				StatusTextBlock.Text = "Formatted";
			}
			return text;
		}
		if (updateStatus)
		{
			StatusTextBlock.Text = "Polishing with " + provider.Name;
		}
		LlmPolishResult llmPolishResult = await _llmPolisher.PolishAsync(raw, text, mode, _settings, _vocabulary);
		if (updateStatus)
		{
			StatusTextBlock.Text = (llmPolishResult.Failed ? ("Formatted locally; LLM fallback: " + llmPolishResult.Detail) : llmPolishResult.Detail);
		}
		if (llmPolishResult.Failed)
		{
			AppLog.Warn("LLM polish fallback: " + llmPolishResult.Detail);
		}
		else
		{
			AppLog.Info("LLM polish applied with " + provider.Name + ".");
		}
		return llmPolishResult.Text;
	}

	private TranscriptCard CreateHistoryCard(string raw, string formatted, DictationMode mode, string audioPath)
	{
		TranscriptionModelOption transcriptionModelOption = SelectedTranscriptionModel();
		CloudSttProviderOption cloudSttProviderOption = CloudSttProviderOption.Find(_settings.SttCloudProviderId);
		return new TranscriptCard
		{
			ModeId = mode.Id,
			EngineId = _settings.EngineId,
			TranscriptionModelId = (transcriptionModelOption?.Id ?? ""),
			TranscriptionModelName = (transcriptionModelOption?.Name ?? ""),
			CloudSttProviderId = cloudSttProviderOption.Id,
			CloudSttModel = _settings.SttCloudModel,
			AudioPath = audioPath,
			RawText = raw,
			FormattedText = formatted,
			CreatedAt = DateTimeOffset.Now
		};
	}

	private void SaveNewHistoryCard(TranscriptCard card)
	{
		_history.Insert(0, card);
		InvalidateStatsCache();
		SaveHistoryInBackground();
		_historyView.Refresh();
		UpdateLibraryStats();
	}

	private void SaveHistoryInBackground()
	{
		List<TranscriptCard> snapshot = _history.ToList();
		int version = Interlocked.Increment(ref _historySaveVersion);
		_ = PersistHistorySnapshotAsync(snapshot, version);
	}

	private async Task PersistHistorySnapshotAsync(List<TranscriptCard> snapshot, int version)
	{
		await _historySaveGate.WaitAsync();
		try
		{
			if (version != Volatile.Read(ref _historySaveVersion))
			{
				return;
			}
			await _store.SaveHistoryAsync(snapshot);
		}
		catch (Exception exception)
		{
			AppLog.Warn("Background history save failed.", exception);
		}
		finally
		{
			_historySaveGate.Release();
		}
	}

	private void SaveVocabularyInBackground()
	{
		List<VocabularyEntry> snapshot = _vocabulary.ToList();
		int version = Interlocked.Increment(ref _vocabularySaveVersion);
		_ = PersistVocabularySnapshotAsync(snapshot, version);
	}

	private async Task PersistVocabularySnapshotAsync(List<VocabularyEntry> snapshot, int version)
	{
		await _vocabularySaveGate.WaitAsync();
		try
		{
			if (version != Volatile.Read(ref _vocabularySaveVersion))
			{
				return;
			}
			await _store.SaveVocabularyAsync(snapshot);
		}
		catch (Exception exception)
		{
			AppLog.Warn("Background dictionary save failed.", exception);
		}
		finally
		{
			_vocabularySaveGate.Release();
		}
	}

	private string CurrentFormattedText()
	{
		if (!string.IsNullOrWhiteSpace(FormattedOutputTextBox.Text))
		{
			return FormattedOutputTextBox.Text.Trim();
		}
		return FormatCurrent(addToHistory: false)?.FormattedText ?? FormattedOutputTextBox.Text.Trim();
	}

	private static string FirstNonEmpty(params string[] values)
	{
		return values.FirstOrDefault((string value) => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
	}

	private void TryCopyToClipboard(string text)
	{
		if (TrySetClipboardText(text))
		{
			StatusTextBlock.Text = "Copied to clipboard";
			ShowCompletionToast("Speak copied", PreviewToastText(text));
		}
		else
		{
			StatusTextBlock.Text = "Clipboard was busy";
		}
	}

	private async Task DeliverTranscriptionOutputAsync(string text, string? outputDestinationOverride = null, bool pressEnterAfterPaste = false)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}

		if (FirstNonEmpty(outputDestinationOverride ?? "", _settings.OutputDestinationId).Equals("paste", StringComparison.OrdinalIgnoreCase)
			&& !PasteGuard.IsSafeToPaste())
		{
			StatusTextBlock.Text = "Paste guarded: target appears to be a sensitive/terminal window";
			AppLog.Warn($"Paste blocked by PasteGuard for target '{_deliveryTargetProcessName}'");
			TryCopyToClipboard(text);
			return;
		}

		if (!FirstNonEmpty(outputDestinationOverride ?? "", _settings.OutputDestinationId).Equals("paste", StringComparison.OrdinalIgnoreCase))
		{
			TryCopyToClipboard(text);
			return;
		}
		if (!TrySetClipboardText(text))
		{
			StatusTextBlock.Text = "Paste skipped: clipboard was busy";
			return;
		}
		if (!(await TryRestoreDeliveryTargetAsync()))
		{
			StatusTextBlock.Text = "Copied to clipboard; target app was unavailable";
			return;
		}
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			await Task.Delay(PasteDelayForTarget(_deliveryTargetProcessName, attempt));
			if (SendCtrlV())
			{
				StatusTextBlock.Text = "Pasted into active app";
				_clipboardHistory.Push(text);
				ShowCompletionToast("Speak pasted", PreviewToastText(text));
				await PressEnterIfRequestedAsync(pressEnterAfterPaste);
				StartExternalEditLearningIfUseful(text, pressEnterAfterPaste);
				return;
			}
			AppLog.Warn($"Paste shortcut attempt {attempt} failed.");
			await TryRestoreDeliveryTargetAsync();
		}
		if (TrySendKeysPaste())
		{
			StatusTextBlock.Text = "Pasted into active app";
			ShowCompletionToast("Speak pasted", PreviewToastText(text));
			await PressEnterIfRequestedAsync(pressEnterAfterPaste);
			StartExternalEditLearningIfUseful(text, pressEnterAfterPaste);
		}
		else
		{
			StatusTextBlock.Text = "Copied to clipboard; paste shortcut failed";
			ShowCompletionToast("Speak copied", "Paste failed, but the text is on the clipboard.");
		}
	}

	private static DeliveryCommand ExtractDeliveryCommand(string transcript)
	{
		string text = transcript.Trim();
		if (TryRemoveTrailingDeliveryPhrase(text, "send message", out string cleaned))
		{
			return new DeliveryCommand(cleaned, "paste", PressEnterAfterPaste: true);
		}
		if (TryRemoveDeliveryPhrase(text, "copy only", out string cleaned2))
		{
			return new DeliveryCommand(cleaned2, "clipboard", PressEnterAfterPaste: false);
		}
		if (TryRemoveDeliveryPhrase(text, "paste only", out string cleaned3))
		{
			return new DeliveryCommand(cleaned3, "paste", PressEnterAfterPaste: false);
		}
		return new DeliveryCommand(text, null, PressEnterAfterPaste: false);
	}

	private static bool TryRemoveTrailingDeliveryPhrase(string text, string phrase, out string cleaned)
	{
		cleaned = text;
		string text2 = text.TrimEnd(' ', '\t', '\r', '\n', '.', ',', '!', '?', ':', ';');
		if (string.IsNullOrWhiteSpace(text2) || !text2.EndsWith(phrase, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		cleaned = TrimCommandSeparators(text2.Substring(0, text2.Length - phrase.Length));
		return !string.IsNullOrWhiteSpace(cleaned);
	}

	private static bool TryRemoveDeliveryPhrase(string text, string phrase, out string cleaned)
	{
		cleaned = text;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		if (text.StartsWith(phrase, StringComparison.OrdinalIgnoreCase))
		{
			string text2 = text;
			int length = phrase.Length;
			cleaned = TrimCommandSeparators(text2.Substring(length, text2.Length - length));
			return !string.IsNullOrWhiteSpace(cleaned);
		}
		if (text.EndsWith(phrase, StringComparison.OrdinalIgnoreCase))
		{
			string text2 = text;
			int length = phrase.Length;
			cleaned = TrimCommandSeparators(text2.Substring(0, text2.Length - length));
			return !string.IsNullOrWhiteSpace(cleaned);
		}
		return false;
	}

	private static string TrimCommandSeparators(string text)
	{
		return text.Trim(' ', '\t', '\r', '\n', ':', '-', '.', ',');
	}

	private static int PasteDelayForTarget(string processName, int attempt)
	{
		int num;
		switch (processName.ToLowerInvariant())
		{
		case "discord":
		case "chrome":
		case "msedge":
		case "obsidian":
			num = 190;
			break;
		case "notepad":
		case "winword":
		case "notepad++":
			num = 110;
			break;
		default:
			num = 140;
			break;
		}
		return num + attempt * 70;
	}

	private async Task PressEnterIfRequestedAsync(bool pressEnterAfterPaste)
	{
		if (pressEnterAfterPaste)
		{
			await Task.Delay(180);
			if (SendEnterKey() || TrySendKeysEnter())
			{
				StatusTextBlock.Text = "Pasted and sent message";
				ShowCompletionToast("Speak sent", "Pasted text and pressed Enter.");
			}
			else
			{
				StatusTextBlock.Text = "Pasted; Enter send failed";
			}
		}
	}

	private void ShowCompletionToast(string title, string detail)
	{
		if (!_settings.ShowCompletionToast)
		{
			return;
		}
		try
		{
			EnsureTrayIcon();
			if (_trayIcon != null)
			{
				_trayIcon.BalloonTipTitle = title;
				_trayIcon.BalloonTipText = PreviewToastText(detail);
				_trayIcon.ShowBalloonTip(2200);
			}
		}
		catch (Exception exception)
		{
			AppLog.Warn("Completion toast failed.", exception);
		}
	}

	private static string PreviewToastText(string text)
	{
		string text2 = (text ?? "").ReplaceLineEndings(" ").Trim();
		if (text2.Length > 120)
		{
			return text2.Substring(0, 120) + "...";
		}
		return text2;
	}

	private void StartExternalEditLearningIfUseful(string pastedText, bool pressEnterAfterPaste)
	{
		if (!_settings.AutoLearnCorrections || pressEnterAfterPaste || string.IsNullOrWhiteSpace(pastedText))
		{
			return;
		}
		nint deliveryTargetWindow = _deliveryTargetWindow;
		if (deliveryTargetWindow != IntPtr.Zero && !IsSpeakWindow(deliveryTargetWindow) && IsWindow(deliveryTargetWindow))
		{
			_externalEditLearningCts?.Cancel();
			_externalEditLearningCts?.Dispose();
			_externalEditLearningCts = new CancellationTokenSource();
			string text = TryReadFocusedEditableText(deliveryTargetWindow);
			if (string.IsNullOrWhiteSpace(text))
			{
				text = pastedText;
			}
			AppLog.Info("External edit learner watching " + _deliveryTargetProcessName + " for corrections.");
			MonitorExternalEditLearningAsync(deliveryTargetWindow, _deliveryTargetProcessName, text, pastedText, _externalEditLearningCts.Token);
		}
	}

	private async Task MonitorExternalEditLearningAsync(nint targetWindow, string processName, string baseline, string pastedText, CancellationToken cancellationToken)
	{
		string lastCandidate = "";
		int stableReads = 0;
		try
		{
			for (int poll = 0; poll < 30; poll++)
			{
				await Task.Delay((poll == 0) ? 1200 : 1200, cancellationToken);
				if (!IsWindow(targetWindow))
				{
					return;
				}
				string text = await base.Dispatcher.InvokeAsync(() => TryReadFocusedEditableText(targetWindow), DispatcherPriority.Background, cancellationToken);
				if (string.IsNullOrWhiteSpace(text) || text.Equals(baseline, StringComparison.Ordinal))
				{
					continue;
				}
				if (text.Equals(lastCandidate, StringComparison.Ordinal))
				{
					stableReads++;
				}
				else
				{
					lastCandidate = text;
					stableReads = 1;
				}
				if (stableReads < 2)
				{
					continue;
				}
				IReadOnlyList<LearnedCorrection> learnedCorrections = ExtractExternalEditCorrections(baseline, pastedText, text);
				if (learnedCorrections.Count != 0)
				{
					await base.Dispatcher.InvokeAsync(delegate
					{
						LearnExternalEditCorrections(learnedCorrections, processName);
					}, DispatcherPriority.Background, cancellationToken);
					return;
				}
			}
			AppLog.Info("External edit learner did not find a stable correction from " + processName + ".");
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			AppLog.Warn("External edit learning failed.", exception);
		}
	}

	private static IReadOnlyList<LearnedCorrection> ExtractExternalEditCorrections(string baseline, string pastedText, string edited)
	{
		IReadOnlyList<LearnedCorrection> readOnlyList = ExternalEditLearner.Extract(baseline, edited);
		if (readOnlyList.Count > 0 || string.IsNullOrWhiteSpace(pastedText) || pastedText.Equals(baseline, StringComparison.Ordinal))
		{
			return readOnlyList;
		}
		return ExternalEditLearner.Extract(pastedText, edited);
	}

	private string TryReadFocusedEditableText(nint expectedTargetWindow)
	{
		try
		{
			if (expectedTargetWindow != IntPtr.Zero && GetForegroundWindow() != expectedTargetWindow)
			{
				return "";
			}
			AutomationElement focusedElement = AutomationElement.FocusedElement;
			if ((object)focusedElement == null)
			{
				return "";
			}
			if (focusedElement.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObject) && patternObject is ValuePattern { Current: var current })
			{
				return (current.Value ?? "").TrimEnd('\r', '\n');
			}
			if (focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject2) && patternObject2 is TextPattern textPattern)
			{
				return (textPattern.DocumentRange.GetText(12000) ?? "").TrimEnd('\r', '\n');
			}
		}
		catch (ElementNotAvailableException)
		{
		}
		catch (InvalidOperationException exception)
		{
			AppLog.Warn("Focused edit text was unavailable.", exception);
		}
		catch (COMException exception2)
		{
			AppLog.Warn("Focused edit text UI Automation read failed.", exception2);
		}
		return "";
	}

	private void LearnExternalEditCorrections(IReadOnlyList<LearnedCorrection> corrections, string processName)
	{
		List<LearnedCorrection> list = new List<LearnedCorrection>();
		foreach (LearnedCorrection correction in corrections)
		{
			if (TrySaveLearnedCorrection(correction, out LearnedCorrection savedCorrection))
			{
				list.Add(savedCorrection);
			}
		}
		if (list.Count != 0)
		{
			NormalizeVocabularyIds();
			InvalidateStatsCache();
			SaveVocabularyInBackground();
			VocabularyGrid.Items.Refresh();
			UpdateLibraryStats();
			string detail = string.Join(", ", list.Select(PreviewLearnedCorrection));
			StatusTextBlock.Text = ((list.Count == 1) ? "Learned correction from edit" : "Learned corrections from edit");
			ShowLearningToast("Speak learned", detail);
			AppLog.Info($"External edit learner saved {list.Count} correction(s) from {processName}.");
		}
	}

	private bool TrySaveLearnedCorrection(LearnedCorrection correction, out LearnedCorrection savedCorrection)
	{
		return VocabularyMemory.TrySaveLearnedCorrection(_vocabulary, correction, out savedCorrection);
	}

	private static string CleanLearnedPhrase(string phrase)
	{
		return VocabularyMemory.CleanPhrase(phrase);
	}

	private static string PreviewLearnedCorrection(LearnedCorrection correction)
	{
		return PreviewLearningPhrase(correction.Spoken) + " -> " + PreviewLearningPhrase(correction.Written);
	}

	private static string PreviewLearningPhrase(string phrase)
	{
		string text = CleanLearnedPhrase(phrase);
		if (text.Length > 42)
		{
			return text.Substring(0, 42) + "...";
		}
		return text;
	}

	private void ShowLearningToast(string title, string detail)
	{
		if (!_settings.ShowCompletionToast)
		{
			return;
		}
		try
		{
			_learningToast?.Close();
			LearningToastWindow toast = new LearningToastWindow(title, PreviewToastText(detail));
			_learningToast = toast;
			toast.Closed += delegate
			{
				if (_learningToast == toast)
				{
					_learningToast = null;
				}
			};
			toast.Show();
			toast.PlaceNearTaskbar();
			CloseLearningToastAfterDelayAsync(toast);
		}
		catch (Exception exception)
		{
			AppLog.Warn("Learning toast failed.", exception);
		}
	}

	private async Task CloseLearningToastAfterDelayAsync(LearningToastWindow toast)
	{
		try
		{
			await Task.Delay(3600);
			if (_learningToast == toast)
			{
				toast.Close();
				_learningToast = null;
			}
		}
		catch (Exception exception)
		{
			AppLog.Warn("Learning toast close failed.", exception);
		}
	}

	private bool TrySetClipboardText(string text)
	{
		for (int i = 1; i <= 5; i++)
		{
			try
			{
				System.Windows.Clipboard.SetText(text, System.Windows.TextDataFormat.UnicodeText);
				return true;
			}
			catch (COMException exception)
			{
				if (i == 5)
				{
					AppLog.Warn("Clipboard was busy after retries.", exception);
					return false;
				}
				Thread.Sleep(60 * i);
			}
		}
		return false;
	}

	private void CaptureDeliveryTargetWindow()
	{
		nint foregroundWindow = GetForegroundWindow();
		if (foregroundWindow != IntPtr.Zero && !IsSpeakWindow(foregroundWindow) && IsWindow(foregroundWindow))
		{
			_deliveryTargetWindow = foregroundWindow;
			_deliveryTargetProcessName = TryGetWindowProcessName(foregroundWindow);
			AppLog.Info("Captured paste target window: " + _deliveryTargetProcessName + ".");
		}
		else if (_deliveryTargetWindow != IntPtr.Zero && IsWindow(_deliveryTargetWindow) && !IsSpeakWindow(_deliveryTargetWindow))
		{
			AppLog.Info("Kept previous paste target because current foreground was Speak.");
		}
		else
		{
			_deliveryTargetWindow = IntPtr.Zero;
			_deliveryTargetProcessName = "";
			AppLog.Warn("Could not capture a non-Speak paste target.");
		}
	}

	private async Task<bool> TryRestoreDeliveryTargetAsync()
	{
		nint hwnd = _deliveryTargetWindow;
		if (hwnd == IntPtr.Zero || IsSpeakWindow(hwnd) || !IsWindow(hwnd))
		{
			AppLog.Warn("Paste target was missing, invalid, or Speak itself.");
			return false;
		}
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			if (TryRestoreDeliveryTargetWindow(hwnd))
			{
				return true;
			}
			AppLog.Warn($"Could not restore paste target on attempt {attempt}.");
			await Task.Delay(100 + attempt * 80);
		}
		return false;
	}

	private bool TryRestoreDeliveryTargetWindow(nint hwnd)
	{
		if (IsIconic(hwnd))
		{
			ShowWindowAsync(hwnd, 9);
		}
		uint currentThreadId = GetCurrentThreadId();
		nint foregroundWindow = GetForegroundWindow();
		uint processId;
		uint num = ((foregroundWindow != IntPtr.Zero) ? GetWindowThreadProcessId(foregroundWindow, out processId) : 0u);
		uint windowThreadProcessId = GetWindowThreadProcessId(hwnd, out processId);
		bool flag = false;
		bool flag2 = false;
		try
		{
			if (num != 0 && num != currentThreadId)
			{
				flag = AttachThreadInput(currentThreadId, num, attach: true);
			}
			if (windowThreadProcessId != 0 && windowThreadProcessId != currentThreadId)
			{
				flag2 = AttachThreadInput(currentThreadId, windowThreadProcessId, attach: true);
			}
			bool value = BringWindowToTop(hwnd);
			bool value2 = SetForegroundWindow(hwnd);
			Thread.Sleep(90);
			bool num2 = GetForegroundWindow() == hwnd;
			if (!num2)
			{
				AppLog.Warn($"Foreground restore failed for {_deliveryTargetProcessName}. BringToTop={value}, SetForeground={value2}, lastError={Marshal.GetLastWin32Error()}.");
			}
			return num2;
		}
		finally
		{
			if (flag2)
			{
				AttachThreadInput(currentThreadId, windowThreadProcessId, attach: false);
			}
			if (flag)
			{
				AttachThreadInput(currentThreadId, num, attach: false);
			}
		}
	}

	private static string TryGetWindowProcessName(nint hwnd)
	{
		try
		{
			GetWindowThreadProcessId(hwnd, out var processId);
			return (processId == 0) ? "unknown" : Process.GetProcessById((int)processId).ProcessName;
		}
		catch
		{
			return "unknown";
		}
	}

	private bool IsSpeakWindow(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return false;
		}
		nint handle = new WindowInteropHelper(this).Handle;
		if (hwnd == handle)
		{
			return true;
		}
		if (_shortcutWidget != null)
		{
			nint handle2 = new WindowInteropHelper(_shortcutWidget).Handle;
			return hwnd == handle2;
		}
		return false;
	}

	private static bool SendCtrlV()
	{
		Input[] array = new Input[4]
		{
			KeyboardInputDown(17),
			KeyboardInputDown(86),
			KeyboardInputUp(86),
			KeyboardInputUp(17)
		};
		uint num = SendInput((uint)array.Length, array, Marshal.SizeOf<Input>());
		if (num == array.Length)
		{
			return true;
		}
		AppLog.Warn($"SendInput Ctrl+V failed. Sent={num}/{array.Length}, lastError={Marshal.GetLastWin32Error()}.");
		return false;
	}

	private static bool TrySendKeysPaste()
	{
		try
		{
			SendKeys.SendWait("^v");
			return true;
		}
		catch (Exception exception)
		{
			AppLog.Warn("SendKeys paste fallback failed.", exception);
			return false;
		}
	}

	private static bool SendEnterKey()
	{
		Input[] array = new Input[2]
		{
			KeyboardInputDown(13),
			KeyboardInputUp(13)
		};
		uint num = SendInput((uint)array.Length, array, Marshal.SizeOf<Input>());
		if (num == array.Length)
		{
			return true;
		}
		AppLog.Warn($"SendInput Enter failed. Sent={num}/{array.Length}, lastError={Marshal.GetLastWin32Error()}.");
		return false;
	}

	private static bool TrySendKeysEnter()
	{
		try
		{
			SendKeys.SendWait("{ENTER}");
			return true;
		}
		catch (Exception exception)
		{
			AppLog.Warn("SendKeys enter fallback failed.", exception);
			return false;
		}
	}

	private static Input KeyboardInputDown(ushort key)
	{
		return new Input
		{
			Type = 1,
			U = new InputUnion
			{
				Ki = new KeyboardInput
				{
					Vk = key
				}
			}
		};
	}

	private static Input KeyboardInputUp(ushort key)
	{
		return new Input
		{
			Type = 1,
			U = new InputUnion
			{
				Ki = new KeyboardInput
				{
					Vk = key,
					Flags = 2u
				}
			}
		};
	}

	private void LoadHistoryCard(TranscriptCard card)
	{
		DictationMode dictationMode = DictationMode.Presets.FirstOrDefault((DictationMode item) => item.Id == card.ModeId);
		if (dictationMode != null)
		{
			SelectMode(dictationMode);
		}
		RawTranscriptTextBox.Text = card.RawText;
		FormattedOutputTextBox.Text = card.FormattedText;
		StatusTextBlock.Text = "History loaded";
	}

	private void SaveSettingsFromUi()
	{
		TranscriptionModelOption transcriptionModelOption = SelectedTranscriptionModel() ?? _transcriptionModels.First();
		_settings = new MaxFlowSettings
		{
			LocaleId = ((LocaleComboBox.SelectedValue as string) ?? MaxFlowSettings.Default.LocaleId),
			EngineId = ((EngineComboBox.SelectedValue as string) ?? MaxFlowSettings.Default.EngineId),
			TranscriptionModelId = transcriptionModelOption.Id,
			WhisperDeviceId = ((WhisperDeviceComboBox.SelectedValue as string) ?? MaxFlowSettings.Default.WhisperDeviceId),
			ModelKeepAliveMinutes = ((ModelKeepAliveComboBox.SelectedValue is int num) ? num : MaxFlowSettings.Default.ModelKeepAliveMinutes),
			WhisperPythonPath = MaxFlowSettings.Default.WhisperPythonPath,
			WhisperWrapperPath = MaxFlowSettings.Default.WhisperWrapperPath,
			WhisperModelPath = transcriptionModelOption.ModelPath,
			AudioInputDeviceNumber = ((AudioInputComboBox.SelectedValue is int num2) ? num2 : 0),
			OutputDestinationId = ((OutputDestinationComboBox.SelectedValue as string) ?? MaxFlowSettings.Default.OutputDestinationId),
			SttCloudProviderId = ((CloudSttProviderComboBox.SelectedValue as string) ?? MaxFlowSettings.Default.SttCloudProviderId),
			SttCloudEndpoint = CloudSttEndpointTextBox.Text.Trim(),
			SttCloudModel = CloudSttModelComboBox.Text.Trim(),
			SttCloudApiKeyEnvironmentVariable = CloudSttApiKeyEnvTextBox.Text.Trim(),
			LlmPolishProviderId = ((LlmPolishProviderComboBox.SelectedValue as string) ?? MaxFlowSettings.Default.LlmPolishProviderId),
			LlmPolishEndpoint = LlmPolishEndpointTextBox.Text.Trim(),
			LlmPolishModel = LlmPolishModelComboBox.Text.Trim(),
			LlmPolishApiKeyEnvironmentVariable = LlmPolishApiKeyEnvTextBox.Text.Trim(),
			LlmPolishTimeoutSeconds = ((LlmPolishTimeoutComboBox.SelectedValue is int num3) ? num3 : MaxFlowSettings.Default.LlmPolishTimeoutSeconds),
			ThemeId = ((ThemeComboBox.SelectedValue as string) ?? MaxFlowSettings.Default.ThemeId),
			KeepHistory = (KeepHistoryCheckBox.IsChecked == true),
			DictationShortcut = _shortcutGesture.ToStorageString(),
			ShowShortcutWidget = (ShowWidgetCheckBox.IsChecked == true),
			MinimizeToTray = (MinimizeToTrayCheckBox.IsChecked == true),
			StartWithWindows = (StartWithWindowsCheckBox.IsChecked == true),
			RecordingRetentionDays = ((RecordingRetentionComboBox.SelectedValue is int num4) ? num4 : MaxFlowSettings.Default.RecordingRetentionDays),
			ShowCompletionToast = (ShowCompletionToastCheckBox.IsChecked == true),
			AutoLearnCorrections = (AutoLearnCorrectionsCheckBox.IsChecked == true),
			TtsEngineId = SelectedTtsEngineId(),
			TtsVoiceId = SelectedTtsVoiceId(),
			TtsOutputRoot = FirstNonEmpty(_settings.TtsOutputRoot, MaxFlowSettings.Default.TtsOutputRoot),
			TtsLanguage = FirstNonEmpty(_settings.TtsLanguage, MaxFlowSettings.Default.TtsLanguage),
			TtsLastOutputPath = _settings.TtsLastOutputPath,
			QwenTtsCustomVoiceModelPath = _settings.QwenTtsCustomVoiceModelPath,
			QwenTtsBaseModelPath = _settings.QwenTtsBaseModelPath,
			QwenTtsVoiceDesignModelPath = _settings.QwenTtsVoiceDesignModelPath,
			VoiceCloneEngineId = (_audioCloneEngineComboBox?.SelectedValue as string) ?? _settings.VoiceCloneEngineId,
			VoiceCloneModelId = _settings.VoiceCloneModelId,
			VoiceCloneReferenceAudioPath = _audioCloneReferenceTextBox?.Text.Trim() ?? _settings.VoiceCloneReferenceAudioPath,
			VoiceCloneProfileName = _audioCloneNameTextBox?.Text.Trim() ?? _settings.VoiceCloneProfileName,
			VoiceCloneOutputRoot = FirstNonEmpty(_settings.VoiceCloneOutputRoot, MaxFlowSettings.Default.VoiceCloneOutputRoot),
			VoiceDesignModelId = _settings.VoiceDesignModelId,
			VoiceDesignPrompt = _audioDesignPromptTextBox?.Text.Trim() ?? _settings.VoiceDesignPrompt,
			VoiceDesignOutputRoot = FirstNonEmpty(_settings.VoiceDesignOutputRoot, MaxFlowSettings.Default.VoiceDesignOutputRoot)
		};
		_settings = NormalizeSettings(_settings, _transcriptionModels);
		_store.SaveSettings(_settings);
		SyncAudioControlsFromSettings();
		UpdateTtsStatus();
		UpdateAudioWorkspaceStatus();
		if (!string.Equals(LlmPolishApiKeyEnvTextBox.Text, _settings.LlmPolishApiKeyEnvironmentVariable, StringComparison.Ordinal))
		{
			_isLoading = true;
			LlmPolishApiKeyEnvTextBox.Text = _settings.LlmPolishApiKeyEnvironmentVariable;
			_isLoading = false;
		}
		if (!string.Equals(CloudSttApiKeyEnvTextBox.Text, _settings.SttCloudApiKeyEnvironmentVariable, StringComparison.Ordinal))
		{
			_isLoading = true;
			CloudSttApiKeyEnvTextBox.Text = _settings.SttCloudApiKeyEnvironmentVariable;
			_isLoading = false;
		}
		ApplyStartupRegistration();
		ApplyTheme();
		UpdateTranscriptionStatus();
		ConfigureShortcutHandling();
		EnsureShortcutWidget();
		UpdateShortcutUi();
		UpdateShortcutWidgetState();
		RefreshTrayMenu();
	}

	private static void NormalizeLlmApiKeySetting(MaxFlowSettings settings)
	{
		LlmPolishProviderOption llmPolishProviderOption = LlmPolishProviderOption.Find(settings.LlmPolishProviderId);
		if (llmPolishProviderOption.RequiresApiKey)
		{
			string value = settings.LlmPolishApiKeyEnvironmentVariable.Trim();
			if (string.IsNullOrWhiteSpace(value))
			{
				settings.LlmPolishApiKeyEnvironmentVariable = llmPolishProviderOption.DefaultApiKeyEnvironmentVariable;
			}
			else if (LooksLikeSecretApiKey(value))
			{
				string text = (string.IsNullOrWhiteSpace(llmPolishProviderOption.DefaultApiKeyEnvironmentVariable) ? "SPEAK_LLM_API_KEY" : llmPolishProviderOption.DefaultApiKeyEnvironmentVariable);
				Environment.SetEnvironmentVariable(text, value, EnvironmentVariableTarget.User);
				Environment.SetEnvironmentVariable(text, value, EnvironmentVariableTarget.Process);
				settings.LlmPolishApiKeyEnvironmentVariable = text;
				AppLog.Info("Moved pasted LLM API key into user environment variable " + text + ".");
			}
		}
	}

	private static void NormalizeCloudSttApiKeySetting(MaxFlowSettings settings)
	{
		CloudSttProviderOption cloudSttProviderOption = CloudSttProviderOption.Find(settings.SttCloudProviderId);
		string value = settings.SttCloudApiKeyEnvironmentVariable.Trim();
		if (string.IsNullOrWhiteSpace(value))
		{
			settings.SttCloudApiKeyEnvironmentVariable = cloudSttProviderOption.DefaultApiKeyEnvironmentVariable;
		}
		else if (LooksLikeSecretApiKey(value))
		{
			string text = (string.IsNullOrWhiteSpace(cloudSttProviderOption.DefaultApiKeyEnvironmentVariable) ? "SPEAK_STT_API_KEY" : cloudSttProviderOption.DefaultApiKeyEnvironmentVariable);
			Environment.SetEnvironmentVariable(text, value, EnvironmentVariableTarget.User);
			Environment.SetEnvironmentVariable(text, value, EnvironmentVariableTarget.Process);
			settings.SttCloudApiKeyEnvironmentVariable = text;
			AppLog.Info("Moved pasted cloud STT API key into user environment variable " + text + ".");
		}
	}

	private static bool LooksLikeSecretApiKey(string value)
	{
		string text = value.Trim();
		if (text.StartsWith("gsk_", StringComparison.OrdinalIgnoreCase) || text.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
		{
			return text.Length >= 24;
		}
		if (text.Length >= 32)
		{
			return !IsEnvironmentVariableName(text);
		}
		return false;
	}

	private static bool IsEnvironmentVariableName(string value)
	{
		if (string.IsNullOrWhiteSpace(value) || char.IsDigit(value[0]))
		{
			return false;
		}
		return value.All((char character) => char.IsLetterOrDigit(character) || character == '_');
	}

	private static MaxFlowSettings NormalizeSettings(MaxFlowSettings settings, IReadOnlyList<TranscriptionModelOption> transcriptionModels)
	{
		string legacyWhisperPythonPath = System.IO.Path.Combine(AppConfig.Current.Paths.ToolsRoot, "whisper-local", "Scripts", "python.exe");
		bool flag = settings.WhisperPythonPath.Equals(legacyWhisperPythonPath, StringComparison.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(settings.EngineId) || settings.EngineId.Equals("windows-local-lab", StringComparison.OrdinalIgnoreCase))
		{
			settings.EngineId = MaxFlowSettings.Default.EngineId;
		}
		if (string.IsNullOrWhiteSpace(settings.TranscriptionModelId))
		{
			settings.TranscriptionModelId = MaxFlowSettings.Default.TranscriptionModelId;
		}
		if (string.IsNullOrWhiteSpace(settings.WhisperWrapperPath))
		{
			settings.WhisperWrapperPath = MaxFlowSettings.Default.WhisperWrapperPath;
		}
		if (string.IsNullOrWhiteSpace(settings.WhisperPythonPath) || flag)
		{
			settings.WhisperPythonPath = MaxFlowSettings.Default.WhisperPythonPath;
		}
		if (flag && settings.WhisperDeviceId.Equals("auto", StringComparison.OrdinalIgnoreCase))
		{
			settings.WhisperDeviceId = MaxFlowSettings.Default.WhisperDeviceId;
		}
		if (WhisperDeviceOption.Presets.All((WhisperDeviceOption option) => !option.Id.Equals(settings.WhisperDeviceId, StringComparison.OrdinalIgnoreCase)))
		{
			settings.WhisperDeviceId = MaxFlowSettings.Default.WhisperDeviceId;
		}
		if (ModelKeepAliveOption.Presets.All((ModelKeepAliveOption option) => option.Minutes != settings.ModelKeepAliveMinutes))
		{
			settings.ModelKeepAliveMinutes = MaxFlowSettings.Default.ModelKeepAliveMinutes;
		}
		if (string.IsNullOrWhiteSpace(settings.OutputDestinationId) || OutputDestinationOption.Presets.All((OutputDestinationOption option) => !option.Id.Equals(settings.OutputDestinationId, StringComparison.OrdinalIgnoreCase)))
		{
			settings.OutputDestinationId = MaxFlowSettings.Default.OutputDestinationId;
		}
		if (EngineProfile.Presets.All((EngineProfile option) => !option.Id.Equals(settings.EngineId, StringComparison.OrdinalIgnoreCase)))
		{
			settings.EngineId = MaxFlowSettings.Default.EngineId;
		}
		CloudSttProviderOption cloudSttProviderOption = CloudSttProviderOption.Find(settings.SttCloudProviderId);
		settings.SttCloudProviderId = cloudSttProviderOption.Id;
		if (string.IsNullOrWhiteSpace(settings.SttCloudEndpoint))
		{
			settings.SttCloudEndpoint = cloudSttProviderOption.DefaultEndpoint;
		}
		if (string.IsNullOrWhiteSpace(settings.SttCloudModel))
		{
			settings.SttCloudModel = cloudSttProviderOption.DefaultModel;
		}
		if (string.IsNullOrWhiteSpace(settings.SttCloudApiKeyEnvironmentVariable))
		{
			settings.SttCloudApiKeyEnvironmentVariable = cloudSttProviderOption.DefaultApiKeyEnvironmentVariable;
		}
		NormalizeCloudSttApiKeySetting(settings);
		LlmPolishProviderOption llmPolishProviderOption = LlmPolishProviderOption.Find(settings.LlmPolishProviderId);
		settings.LlmPolishProviderId = llmPolishProviderOption.Id;
		if (!llmPolishProviderOption.Id.Equals("off", StringComparison.OrdinalIgnoreCase))
		{
			if (string.IsNullOrWhiteSpace(settings.LlmPolishEndpoint))
			{
				settings.LlmPolishEndpoint = llmPolishProviderOption.DefaultEndpoint;
			}
			if (string.IsNullOrWhiteSpace(settings.LlmPolishModel))
			{
				settings.LlmPolishModel = llmPolishProviderOption.DefaultModel;
			}
			NormalizeLegacyGroqPolishModel(settings, llmPolishProviderOption);
			if (string.IsNullOrWhiteSpace(settings.LlmPolishApiKeyEnvironmentVariable))
			{
				settings.LlmPolishApiKeyEnvironmentVariable = llmPolishProviderOption.DefaultApiKeyEnvironmentVariable;
			}
			NormalizeLlmApiKeySetting(settings);
		}
		if (LlmPolishTimeoutOption.Presets.All((LlmPolishTimeoutOption option) => option.Seconds != settings.LlmPolishTimeoutSeconds))
		{
			settings.LlmPolishTimeoutSeconds = MaxFlowSettings.Default.LlmPolishTimeoutSeconds;
		}
		if (RecordingRetentionOption.Presets.All((RecordingRetentionOption option) => option.Days != settings.RecordingRetentionDays))
		{
			settings.RecordingRetentionDays = MaxFlowSettings.Default.RecordingRetentionDays;
		}
		ShortcutGesture shortcutGesture = ShortcutGesture.Parse(settings.DictationShortcut);
		if (!shortcutGesture.IsUsable())
		{
			shortcutGesture = ShortcutGesture.Default;
		}
		settings.DictationShortcut = shortcutGesture.ToStorageString();
		TranscriptionModelOption transcriptionModelOption = transcriptionModels.FirstOrDefault((TranscriptionModelOption option) => option.Id == settings.TranscriptionModelId) ?? transcriptionModels.FirstOrDefault((TranscriptionModelOption option) => option.Id == MaxFlowSettings.Default.TranscriptionModelId) ?? transcriptionModels.First();
		settings.TranscriptionModelId = transcriptionModelOption.Id;
		settings.WhisperModelPath = transcriptionModelOption.ModelPath;
		TtsEngineOption ttsEngineOption = TtsEngineOption.Find(settings.TtsEngineId);
		settings.TtsEngineId = ttsEngineOption.Id;
		if (string.IsNullOrWhiteSpace(settings.TtsOutputRoot))
		{
			settings.TtsOutputRoot = MaxFlowSettings.Default.TtsOutputRoot;
		}
		if (string.IsNullOrWhiteSpace(settings.TtsLanguage))
		{
			settings.TtsLanguage = MaxFlowSettings.Default.TtsLanguage;
		}
		IReadOnlyList<TtsVoiceOption> ttsVoiceOptions = TtsVoiceOption.ForEngine(ttsEngineOption.Id);
		if (!settings.TtsVoiceId.StartsWith("clone:", StringComparison.OrdinalIgnoreCase)
			&& ttsVoiceOptions.All((TtsVoiceOption option) => !option.Id.Equals(settings.TtsVoiceId, StringComparison.OrdinalIgnoreCase)))
		{
			settings.TtsVoiceId = ttsVoiceOptions.First().Id;
		}
		if (string.IsNullOrWhiteSpace(settings.QwenTtsCustomVoiceModelPath) || !Directory.Exists(settings.QwenTtsCustomVoiceModelPath))
		{
			settings.QwenTtsCustomVoiceModelPath = MaxFlowSettings.Default.QwenTtsCustomVoiceModelPath;
		}
		if (string.IsNullOrWhiteSpace(settings.QwenTtsBaseModelPath) || !Directory.Exists(settings.QwenTtsBaseModelPath))
		{
			settings.QwenTtsBaseModelPath = MaxFlowSettings.Default.QwenTtsBaseModelPath;
		}
		if (string.IsNullOrWhiteSpace(settings.QwenTtsVoiceDesignModelPath) || !Directory.Exists(settings.QwenTtsVoiceDesignModelPath))
		{
			settings.QwenTtsVoiceDesignModelPath = MaxFlowSettings.Default.QwenTtsVoiceDesignModelPath;
		}
		if (string.IsNullOrWhiteSpace(settings.VoiceCloneEngineId) || TtsEngineOption.Presets.All((TtsEngineOption option) => !option.Id.Equals(settings.VoiceCloneEngineId, StringComparison.OrdinalIgnoreCase)))
		{
			settings.VoiceCloneEngineId = MaxFlowSettings.Default.VoiceCloneEngineId;
		}
		if (string.IsNullOrWhiteSpace(settings.VoiceCloneModelId))
		{
			settings.VoiceCloneModelId = MaxFlowSettings.Default.VoiceCloneModelId;
		}
		if (string.IsNullOrWhiteSpace(settings.VoiceCloneProfileName))
		{
			settings.VoiceCloneProfileName = MaxFlowSettings.Default.VoiceCloneProfileName;
		}
		if (string.IsNullOrWhiteSpace(settings.VoiceCloneOutputRoot))
		{
			settings.VoiceCloneOutputRoot = MaxFlowSettings.Default.VoiceCloneOutputRoot;
		}
		if (string.IsNullOrWhiteSpace(settings.VoiceDesignModelId) || TtsEngineOption.Presets.All((TtsEngineOption option) => !option.Id.Equals(settings.VoiceDesignModelId, StringComparison.OrdinalIgnoreCase)))
		{
			settings.VoiceDesignModelId = MaxFlowSettings.Default.VoiceDesignModelId;
		}
		if (string.IsNullOrWhiteSpace(settings.VoiceDesignPrompt))
		{
			settings.VoiceDesignPrompt = MaxFlowSettings.Default.VoiceDesignPrompt;
		}
		if (string.IsNullOrWhiteSpace(settings.VoiceDesignOutputRoot))
		{
			settings.VoiceDesignOutputRoot = MaxFlowSettings.Default.VoiceDesignOutputRoot;
		}
		return settings;
	}

	private static void NormalizeLegacyGroqPolishModel(MaxFlowSettings settings, LlmPolishProviderOption polishProvider)
	{
	}

	private void UpdateTranscriptionStatus()
	{
		if (_settings.EngineId.Equals("cloud-stt", StringComparison.OrdinalIgnoreCase))
		{
			CloudSttProviderOption cloudSttProviderOption = CloudSttProviderOption.Find(_settings.SttCloudProviderId);
			TranscriptionModelStatusTextBlock.Text = "Cloud STT: " + cloudSttProviderOption.Name + " - " + _settings.SttCloudModel;
			WhisperModelPathTextBlock.Text = "Cloud STT endpoint: " + _settings.SttCloudEndpoint;
			WhisperWrapperPathTextBlock.Text = "Cloud STT key env var: " + FirstNonEmpty(_settings.SttCloudApiKeyEnvironmentVariable, cloudSttProviderOption.DefaultApiKeyEnvironmentVariable);
			WhisperRuntimeStatusTextBlock.Text = "Cloud STT records audio locally on this PC, sends it to " + cloudSttProviderOption.Name + ", then formats/pastes with Speak.";
			RuntimeModelPillTextBlock.Text = _settings.SttCloudModel;
			RuntimeDevicePillTextBlock.Text = "Cloud";
			if (!_isRecording && !_isTranscribing)
			{
				RecordingStatusTextBlock.Text = "Ready.";
			}
			UpdateLlmPolishStatus();
			UpdateCloudSttStatus();
			return;
		}
		TranscriptionModelOption transcriptionModelOption = SelectedTranscriptionModel() ?? _transcriptionModels.First();
		bool flag = File.Exists(transcriptionModelOption.ModelPath);
		bool flag2 = File.Exists(_settings.WhisperPythonPath);
		TranscriptionModelStatusTextBlock.Text = $"{transcriptionModelOption.Name} - model {(flag ? "found" : "missing")} - CUDA runtime {(flag2 ? "ready" : "missing")}";
		WhisperModelPathTextBlock.Text = transcriptionModelOption.ModelPath + " " + (flag ? "(found)" : "(missing)");
		WhisperWrapperPathTextBlock.Text = "Python runtime: " + _settings.WhisperPythonPath + " " + (flag2 ? "(ready)" : "(missing)");
		WhisperRuntimeStatusTextBlock.Text = $"Device: {SelectedWhisperDeviceName()}. Offload timer: {_settings.ModelKeepAliveMinutes} minutes idle.";
		RuntimeModelPillTextBlock.Text = transcriptionModelOption.WhisperArgument;
		string text = SelectedWhisperDeviceName();
		string text2 = (text.Contains("CUDA", StringComparison.OrdinalIgnoreCase) ? "CUDA" : text);
		RuntimeDevicePillTextBlock.Text = text2;
		if (!_isRecording && !_isTranscribing)
		{
			RecordingStatusTextBlock.Text = "Ready.";
		}
		UpdateLlmPolishStatus();
		UpdateCloudSttStatus();
	}

	private void UpdateCloudSttStatus()
	{
		CloudSttProviderOption cloudSttProviderOption = CloudSttProviderOption.Find(_settings.SttCloudProviderId);
		string value = (string.IsNullOrWhiteSpace(_settings.SttCloudEndpoint) ? cloudSttProviderOption.DefaultEndpoint : _settings.SttCloudEndpoint);
		string value2 = (string.IsNullOrWhiteSpace(_settings.SttCloudModel) ? cloudSttProviderOption.DefaultModel : _settings.SttCloudModel);
		string value3 = FirstNonEmpty(_settings.SttCloudApiKeyEnvironmentVariable, cloudSttProviderOption.DefaultApiKeyEnvironmentVariable);
		CloudSttStatusTextBlock.Text = $"{cloudSttProviderOption.Name}: {value2}. Uses env var {value3}. Endpoint {value}.";
	}

	private void UpdateLlmPolishStatus()
	{
		LlmPolishProviderOption llmPolishProviderOption = LlmPolishProviderOption.Find(_settings.LlmPolishProviderId);
		if (llmPolishProviderOption.Id.Equals("off", StringComparison.OrdinalIgnoreCase))
		{
			LlmPolishStatusTextBlock.Text = "Off. Speak uses its fast local formatter only.";
			LlmPolishRuntimeTextBlock.Text = "LLM polish is off. No local or cloud model is called.";
			return;
		}
		string value = (string.IsNullOrWhiteSpace(_settings.LlmPolishEndpoint) ? llmPolishProviderOption.DefaultEndpoint : _settings.LlmPolishEndpoint);
		string value2 = (string.IsNullOrWhiteSpace(_settings.LlmPolishModel) ? llmPolishProviderOption.DefaultModel : _settings.LlmPolishModel);
		string value3 = (llmPolishProviderOption.RequiresApiKey ? (" Uses env var " + FirstNonEmpty(_settings.LlmPolishApiKeyEnvironmentVariable, llmPolishProviderOption.DefaultApiKeyEnvironmentVariable) + ".") : "");
		LlmPolishStatusTextBlock.Text = $"{llmPolishProviderOption.Name}. Timeout {_settings.LlmPolishTimeoutSeconds}s.{value3}";
		LlmPolishRuntimeTextBlock.Text = $"{llmPolishProviderOption.Name}: {value2} at {value}. Falls back to local formatting if unavailable.";
	}

	private void UpdateShortcutUi()
	{
		ShortcutCaptureButton.Content = _shortcutGesture.ToDisplayString();
		ShortcutStatusTextBlock.Text = (string.IsNullOrWhiteSpace(_shortcutStatusDetail) ? ("Press " + _shortcutGesture.ToDisplayString() + " anywhere to start or stop recording.") : _shortcutStatusDetail);
		ShowWidgetCheckBox.IsChecked = _settings.ShowShortcutWidget;
		MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTray;
		StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
	}

	private TranscriptionModelOption? SelectedTranscriptionModel()
	{
		string id = (TranscriptionModelComboBox.SelectedValue as string) ?? _settings.TranscriptionModelId;
		return _transcriptionModels.FirstOrDefault((TranscriptionModelOption option) => option.Id == id);
	}

	private string SelectedWhisperDeviceName()
	{
		string id = (WhisperDeviceComboBox.SelectedValue as string) ?? _settings.WhisperDeviceId;
		return WhisperDeviceOption.Presets.FirstOrDefault((WhisperDeviceOption option) => option.Id == id)?.Name ?? "Auto";
	}

	private void NormalizeVocabularyIds()
	{
		foreach (VocabularyEntry item in _vocabulary)
		{
			if (item.Id == Guid.Empty)
			{
				item.Id = Guid.NewGuid();
			}
			if (string.IsNullOrWhiteSpace(item.Source))
			{
				item.Source = "manual";
			}
			if (item.CreatedAt == default(DateTimeOffset))
			{
				item.CreatedAt = DateTimeOffset.Now;
			}
			if (item.UpdatedAt == default(DateTimeOffset))
			{
				item.UpdatedAt = item.CreatedAt;
			}
		}
	}

	private void ApplyTheme()
	{
		bool flag = IsDarkTheme();
		SetBrush("CanvasBrush", flag ? "#111214" : "#F4F8FB");
		SetBrush("SidebarBrush", flag ? "#151618" : "#EDEDED");
		SetBrush("PanelBrush", flag ? "#18191C" : "#FFFFFF");
		SetBrush("InputBrush", flag ? "#121316" : "#FAFAFA");
		SetBrush("InkBrush", flag ? "#D8D3C7" : "#101010");
		SetBrush("MutedBrush", flag ? "#918D84" : "#666666");
		SetBrush("LineBrush", flag ? "#2B2C30" : "#D7D7D7");
		SetBrush("RailBrush", flag ? "#B8B1A4" : "#101010");
		SetBrush("AccentBrush", flag ? "#B8B1A4" : "#101010");
		SetBrush("SoftBrush", flag ? "#202124" : "#EFEFEF");
		SetBrush("SuccessBrush", flag ? "#B8B1A4" : "#333333");
		SetBrush("DangerBrush", flag ? "#B8B1A4" : "#333333");
		SetBrush("AmberBrush", flag ? "#AAA397" : "#4A4A4A");
		SetBrush("SignalBrush", flag ? "#77736C" : "#6F6F6F");
		SetBrush("PremiumBrush", flag ? "#34363C" : "#111111");
		SetBrush("PremiumSoftBrush", flag ? "#242529" : "#E9E9E9");
		SetBrush("ElevatedBrush", flag ? "#26272B" : "#F2F2F2");
		SetBrush("ChromeBrush", flag ? "#17181B" : "#EDEDED");
		SetBrush("GoldBrush", flag ? "#CBC4B5" : "#101010");
		SetBrush("DeepGoldBrush", flag ? "#4A4843" : "#A0A0A0");
		SetBrush("SpeakReadyBrush", flag ? "#202124" : "#101010");
		SetBrush("SpeakRecordingBrush", flag ? "#2B2C31" : "#E2E2E2");
		SetSystemBrush(System.Windows.SystemColors.WindowBrushKey, flag ? "#111214" : "#FFFFFF");
		SetSystemBrush(System.Windows.SystemColors.WindowTextBrushKey, flag ? "#D8D3C7" : "#101010");
		SetSystemBrush(System.Windows.SystemColors.ControlBrushKey, flag ? "#18191C" : "#FFFFFF");
		SetSystemBrush(System.Windows.SystemColors.ControlTextBrushKey, flag ? "#D8D3C7" : "#101010");
		SetSystemBrush(System.Windows.SystemColors.HighlightBrushKey, flag ? "#34363C" : "#101010");
		SetSystemBrush(System.Windows.SystemColors.HighlightTextBrushKey, flag ? "#FFFFFF" : "#FFFFFF");
		SetSystemBrush(System.Windows.SystemColors.GrayTextBrushKey, flag ? "#918D84" : "#666666");
		base.Background = ResourceBrush("CanvasBrush");
		ApplyWindowChromeTheme();
		UpdateModeButtons();
		UpdateTabButton(DictateTabButton, "dictate");
		UpdateTabButton(HistoryTabButton, "history");
		UpdateTabButton(ProfileTabButton, "profile");
		UpdateTabButton(DictionaryTabButton, "dictionary");
		UpdateTabButton(_audioTabButton, "audio");
		UpdateTabButton(SettingsTabButton, "settings");
		RemoveHeaderLogoMark();
		ApplyHeaderEditorialVisual();
		ApplyGlassSurfacePolish();
	}

	private void ApplyWindowChromeTheme()
	{
		try
		{
			nint handle = new WindowInteropHelper(this).Handle;
			if (handle != IntPtr.Zero)
			{
				int value = (IsDarkTheme() ? 1 : 0);
				int size = Marshal.SizeOf<int>();
				DwmSetWindowAttribute(handle, 20, ref value, size);
				DwmSetWindowAttribute(handle, 19, ref value, size);
			}
		}
		catch (Exception exception)
		{
			AppLog.Warn("Could not apply dark window chrome.", exception);
		}
	}

	private bool IsDarkTheme()
	{
		if (_settings.ThemeId.Equals("dark", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (_settings.ThemeId.Equals("light", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
			return registryKey?.GetValue("AppsUseLightTheme") is int num && num == 0;
		}
		catch (Exception exception)
		{
			AppLog.Warn("Could not read Windows app theme.", exception);
			return false;
		}
	}

	private void SetBrush(string key, string hex)
	{
		System.Windows.Media.Color color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
		SolidColorBrush solidColorBrush = new SolidColorBrush(color);
		base.Resources[key] = solidColorBrush;
		_resourceBrushCache[key] = solidColorBrush;
	}

	private void SetSystemBrush(ResourceKey key, string hex)
	{
		System.Windows.Media.Color color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
		base.Resources[key] = new SolidColorBrush(color);
	}

	private SolidColorBrush ResourceBrush(string key)
	{
		if (_resourceBrushCache.TryGetValue(key, out SolidColorBrush value))
		{
			return value;
		}
		value = (SolidColorBrush)FindResource(key);
		_resourceBrushCache[key] = value;
		return value;
	}

	private static double CalculateMicrophoneLevel(byte[] buffer, int bytesRecorded)
	{
		return MicrophoneActivityMeter.FromPcm16(buffer, bytesRecorded);
	}

	private static string WhisperLanguageFromLocale(string localeId)
	{
		switch (localeId)
		{
		case "en-US":
		case "en-GB":
			return "en";
		case "ur-PK":
			return "ur";
		case "hi-IN":
			return "hi";
		default:
			return "";
		}
	}

	private static string SampleForMode(string modeId)
	{
		return modeId switch
		{
			"message" => "um hey can you check the speak windows app question mark tell me if the recording feels natural now", 
			"email" => "i wanted to follow up on the speak windows app. please review the recording and formatting modes. confirm whether the local transcription workflow feels ready.", 
			"prompt" => "create a simple local first speak windows app. record audio. transcribe with whisper large. format the result into the selected writing mode.", 
			"notes" => "test speak on windows. record locally. transcribe with whisper large. save vocabulary. keep history. make the app simple.", 
			"raw" => "um this is raw speak dictation with open claw and chat gpt words left mostly untouched", 
			_ => "um please review the speak recording setup and test the local whisper transcription before we decide the next build step", 
		};
	}

	private static System.Windows.Media.Brush BrushFromHex(string hex)
	{
		return (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(hex);
	}

	private void ConfigureShortcutHandling()
	{
		UnregisterNativeHotkey();
		UninstallKeyboardShortcutHook();
		_shortcutIsLatched = false;
		if (!_shortcutGesture.IsUsable())
		{
			_shortcutStatusDetail = "Choose a shortcut with at least two modifiers, or one modifier plus a normal key.";
		}
		else if (_shortcutGesture.HasMainKey)
		{
			if (RegisterNativeHotkey())
			{
				_shortcutStatusDetail = "Press " + _shortcutGesture.ToDisplayString() + " anywhere to start or stop recording.";
			}
			else
			{
				InstallKeyboardShortcutHook();
				if (_keyboardHookId != IntPtr.Zero)
				{
					_shortcutStatusDetail = "Press " + _shortcutGesture.ToDisplayString() + " anywhere to start or stop recording.";
				}
			}
		}
		else
		{
			InstallKeyboardShortcutHook();
			_shortcutStatusDetail = "Press " + _shortcutGesture.ToDisplayString() + " anywhere to start or stop recording.";
		}
	}

	private bool RegisterNativeHotkey()
	{
		nint handle = new WindowInteropHelper(this).Handle;
		if (handle == IntPtr.Zero)
		{
			_shortcutStatusDetail = "Shortcut will register after the Speak window finishes loading.";
			return false;
		}
		if (_hotkeySource == null)
		{
			_hotkeySource = HwndSource.FromHwnd(handle);
		}
		_hotkeySource?.RemoveHook(WindowMessageHook);
		_hotkeySource?.AddHook(WindowMessageHook);
		int num = _shortcutGesture.MainVirtualKey();
		if (num == 0)
		{
			_shortcutStatusDetail = "Shortcut key is not supported by Windows.";
			return false;
		}
		if (RegisterHotKey(handle, 19782, _shortcutGesture.NativeModifierFlags(), num))
		{
			_nativeHotkeyRegistered = true;
			return true;
		}
		int lastWin32Error = Marshal.GetLastWin32Error();
		_shortcutStatusDetail = ((lastWin32Error == 1409) ? (_shortcutGesture.ToDisplayString() + " is already owned by another app. Close the other app or choose a different shortcut.") : $"Windows could not register {_shortcutGesture.ToDisplayString()} (error {lastWin32Error}). Falling back to keyboard hook.");
		return false;
	}

	private void UnregisterNativeHotkey()
	{
		if (_nativeHotkeyRegistered)
		{
			nint handle = new WindowInteropHelper(this).Handle;
			if (handle != IntPtr.Zero)
			{
				UnregisterHotKey(handle, 19782);
			}
			_nativeHotkeyRegistered = false;
		}
	}

	private nint WindowMessageHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		if (msg == 786 && ((IntPtr)wParam).ToInt32() == 19782)
		{
			handled = true;
			base.Dispatcher.BeginInvoke((Action)async delegate
			{
				await ToggleRecordingFromShortcutAsync();
			});
		}
		return IntPtr.Zero;
	}

	private void InstallKeyboardShortcutHook()
	{
		if (_keyboardHookId != IntPtr.Zero)
		{
			return;
		}
		_keyboardHookProc = KeyboardHookCallback;
		using Process process = Process.GetCurrentProcess();
		string lpModuleName = process.MainModule?.ModuleName;
		_keyboardHookId = SetWindowsHookEx(13, _keyboardHookProc, IntPtr.Zero, 0u);
		if (_keyboardHookId == IntPtr.Zero)
		{
			int lastWin32Error = Marshal.GetLastWin32Error();
			_shortcutStatusDetail = $"Windows could not install the shortcut hook (error {lastWin32Error}).";
		}
	}

	private void UninstallKeyboardShortcutHook()
	{
		if (_keyboardHookId != IntPtr.Zero)
		{
			UnhookWindowsHookEx(_keyboardHookId);
			_keyboardHookId = IntPtr.Zero;
			_keyboardHookProc = null;
		}
	}

	private nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
	{
		if (nCode < 0 || _isCapturingShortcut)
		{
			return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
		}
		int num = ((IntPtr)wParam).ToInt32();
		if (((uint)(num - 256) <= 1u || (uint)(num - 260) <= 1u) ? true : false)
		{
			if (!_shortcutGesture.IsCurrentlyDown(IsVirtualKeyDown))
			{
				_shortcutIsLatched = false;
			}
			else
			{
				bool flag = !_shortcutIsLatched;
				if (flag)
				{
					bool flag2 = ((num == 256 || num == 260) ? true : false);
					flag = flag2;
				}
				if (flag)
				{
					_shortcutIsLatched = true;
					base.Dispatcher.BeginInvoke((Action)async delegate
					{
						await ToggleRecordingFromShortcutAsync();
					});
				}
			}
		}
		return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
	}

	private static bool IsVirtualKeyDown(int virtualKey)
	{
		return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
	}

	private async Task ToggleRecordingFromShortcutAsync()
	{
		if (_shortcutToggleInFlight)
		{
			return;
		}
		_shortcutToggleInFlight = true;
		if (_isTranscribing)
		{
			StatusTextBlock.Text = "Transcribing";
			UpdateShortcutWidgetState();
			_shortcutToggleInFlight = false;
			return;
		}
		try
		{
			if (_isRecording)
			{
				await StopRecordingAndTranscribeAsync();
			}
			else
			{
				StartRecording();
			}
		}
		finally
		{
			_shortcutToggleInFlight = false;
		}
	}

	private static IEnumerable<T> FindVisualChildren<T>(DependencyObject dependencyObject) where T : DependencyObject
	{
		if (dependencyObject == null)
		{
			yield break;
		}
		for (int index = 0; index < VisualTreeHelper.GetChildrenCount(dependencyObject); index++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(dependencyObject, index);
			if (child is T val)
			{
				yield return val;
			}
			foreach (T item in FindVisualChildren<T>(child))
			{
				yield return item;
			}
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.27.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/Speak;V0.5.0.0;component/mainwindow.xaml", UriKind.Relative);
			System.Windows.Application.LoadComponent(this, resourceLocator);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.27.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			((MainWindow)target).Loaded += Window_Loaded;
			((MainWindow)target).StateChanged += Window_StateChanged;
			((MainWindow)target).PreviewKeyDown += Window_PreviewKeyDown;
			break;
		case 2:
			((System.Windows.Controls.Button)target).Click += MinimizeWindowButton_Click;
			break;
		case 3:
			((System.Windows.Controls.Button)target).Click += MaximizeWindowButton_Click;
			break;
		case 4:
			((System.Windows.Controls.Button)target).Click += CloseWindowButton_Click;
			break;
		case 5:
			DictateTabButton = (System.Windows.Controls.Button)target;
			DictateTabButton.Click += TabButton_Click;
			break;
		case 6:
			HistoryTabButton = (System.Windows.Controls.Button)target;
			HistoryTabButton.Click += TabButton_Click;
			break;
		case 7:
			ProfileTabButton = (System.Windows.Controls.Button)target;
			ProfileTabButton.Click += TabButton_Click;
			break;
		case 8:
			DictionaryTabButton = (System.Windows.Controls.Button)target;
			DictionaryTabButton.Click += TabButton_Click;
			break;
		case 9:
			SettingsTabButton = (System.Windows.Controls.Button)target;
			SettingsTabButton.Click += TabButton_Click;
			break;
		case 10:
			StorePathTextBlock = (TextBlock)target;
			break;
		case 11:
			HeaderTitleTextBlock = (TextBlock)target;
			break;
		case 12:
			HeaderSubtitleTextBlock = (TextBlock)target;
			break;
		case 13:
			ModePillTextBlock = (TextBlock)target;
			break;
		case 14:
			StatusTextBlock = (TextBlock)target;
			break;
		case 15:
			DictatePage = (Grid)target;
			break;
		case 16:
			RecordingStatusTextBlock = (TextBlock)target;
			break;
		case 17:
			OpenAudioButton = (System.Windows.Controls.Button)target;
			OpenAudioButton.Click += OpenAudioButton_Click;
			break;
		case 18:
			RuntimeDevicePillTextBlock = (TextBlock)target;
			break;
		case 19:
			RuntimeModelPillTextBlock = (TextBlock)target;
			break;
		case 20:
			TranscriptionModelStatusTextBlock = (TextBlock)target;
			break;
		case 21:
			DictateWordCounterPanel = (Border)target;
			break;
		case 22:
			DictateWordsSpokenTextBlock = (TextBlock)target;
			break;
		case 23:
			DictateTodayWordsTextBlock = (TextBlock)target;
			break;
		case 24:
			DictateVoiceStatsTextBlock = (TextBlock)target;
			break;
		case 25:
			AmbientMicHalo = (Ellipse)target;
			break;
		case 26:
			RecordRipple1 = (Ellipse)target;
			break;
		case 27:
			RippleScale1 = (ScaleTransform)target;
			break;
		case 28:
			RecordRipple2 = (Ellipse)target;
			break;
		case 29:
			RippleScale2 = (ScaleTransform)target;
			break;
		case 30:
			RecordButton = (System.Windows.Controls.Button)target;
			RecordButton.Click += RecordButton_Click;
			break;
		case 31:
			RecordButtonGlyph = (TextBlock)target;
			break;
		case 32:
			RecordActivityPanel = (StackPanel)target;
			break;
		case 33:
			RecordBar1 = (Border)target;
			break;
		case 34:
			RecordBar2 = (Border)target;
			break;
		case 35:
			RecordBar3 = (Border)target;
			break;
		case 36:
			RecordBar4 = (Border)target;
			break;
		case 37:
			RecordBar5 = (Border)target;
			break;
		case 38:
			ModesPanel = (UniformGrid)target;
			break;
		case 39:
			ModeInstructionTextBlock = (TextBlock)target;
			break;
		case 40:
			RawTranscriptTextBox = (System.Windows.Controls.TextBox)target;
			RawTranscriptTextBox.TextChanged += RawTranscriptTextBox_TextChanged;
			break;
		case 41:
			FormattedOutputTextBox = (System.Windows.Controls.TextBox)target;
			break;
		case 42:
			ActionBarPanel = (Border)target;
			break;
		case 43:
			((System.Windows.Controls.Button)target).Click += LoadSampleButton_Click;
			break;
		case 44:
			((System.Windows.Controls.Button)target).Click += FormatButton_Click;
			break;
		case 45:
			((System.Windows.Controls.Button)target).Click += ClearButton_Click;
			break;
		case 46:
			((System.Windows.Controls.Button)target).Click += CopyButton_Click;
			break;
		case 47:
			HistoryPage = (Grid)target;
			break;
		case 48:
			HistoryCountTextBlock = (TextBlock)target;
			break;
		case 49:
			WordsSpokenTextBlock = (TextBlock)target;
			break;
		case 50:
			VoiceStatsTextBlock = (TextBlock)target;
			break;
		case 51:
			HistorySearchTextBox = (System.Windows.Controls.TextBox)target;
			HistorySearchTextBox.TextChanged += HistorySearchTextBox_TextChanged;
			break;
		case 52:
			HistoryListBox = (System.Windows.Controls.ListBox)target;
			HistoryListBox.SelectionChanged += HistoryListBox_SelectionChanged;
			break;
		case 53:
			HistorySelectedTextBox = (System.Windows.Controls.TextBox)target;
			break;
		case 54:
			HistoryRawTextBox = (System.Windows.Controls.TextBox)target;
			break;
		case 55:
			HistoryComparisonTextBox = (System.Windows.Controls.TextBox)target;
			break;
		case 56:
			HistoryTagsTextBox = (System.Windows.Controls.TextBox)target;
			break;
		case 57:
			((System.Windows.Controls.Button)target).Click += OpenSelectedHistoryButton_Click;
			break;
		case 58:
			((System.Windows.Controls.Button)target).Click += CopySelectedHistoryButton_Click;
			break;
		case 59:
			((System.Windows.Controls.Button)target).Click += RetrySelectedHistoryButton_Click;
			break;
		case 60:
			((System.Windows.Controls.Button)target).Click += UseRetryOutputButton_Click;
			break;
		case 61:
			((System.Windows.Controls.Button)target).Click += UseSelectionForCorrectionButton_Click;
			break;
		case 62:
			((System.Windows.Controls.Button)target).Click += SaveHistoryTagsButton_Click;
			break;
		case 63:
			((System.Windows.Controls.Button)target).Click += ExportHistoryBackupButton_Click;
			break;
		case 64:
			((System.Windows.Controls.Button)target).Click += ClearHistoryButton_Click;
			break;
		case 65:
			VoiceProfilePage = (Grid)target;
			break;
		case 66:
			ProfileWordsSpokenTextBlock = (TextBlock)target;
			break;
		case 67:
			ProfileTodayWordsTextBlock = (TextBlock)target;
			break;
		case 68:
			ProfileSavedCorrectionsTextBlock = (TextBlock)target;
			break;
		case 69:
			ProfileAutoLearnedTextBlock = (TextBlock)target;
			break;
		case 70:
			ProfileAccuracyTextBlock = (TextBlock)target;
			break;
		case 71:
			ProfileSessionsTextBlock = (TextBlock)target;
			break;
		case 72:
			ProfileAverageTextBlock = (TextBlock)target;
			break;
		case 73:
			ProfileStreakTextBlock = (TextBlock)target;
			break;
		case 74:
			ProfileLearningTextBlock = (TextBlock)target;
			break;
		case 75:
			DictionaryPage = (Grid)target;
			break;
		case 76:
			DictionaryCountTextBlock = (TextBlock)target;
			break;
		case 77:
			((System.Windows.Controls.Button)target).Click += AddVocabularyButton_Click;
			break;
		case 78:
			DictionaryTabsPanel = (StackPanel)target;
			break;
		case 79:
			DictionaryHeroPanel = (Border)target;
			break;
		case 80:
			VocabularyGrid = (DataGrid)target;
			break;
		case 81:
			DictionaryAutoLearnCheckBox = (System.Windows.Controls.CheckBox)target;
			DictionaryAutoLearnCheckBox.Checked += DictionaryAutoLearnCheckBox_Changed;
			DictionaryAutoLearnCheckBox.Unchecked += DictionaryAutoLearnCheckBox_Changed;
			break;
		case 82:
			CorrectionSpokenTextBox = (System.Windows.Controls.TextBox)target;
			break;
		case 83:
			CorrectionWrittenTextBox = (System.Windows.Controls.TextBox)target;
			break;
		case 84:
			((System.Windows.Controls.Button)target).Click += TeachCorrectionButton_Click;
			break;
		case 85:
			((System.Windows.Controls.Button)target).Click += UseSelectionForCorrectionButton_Click;
			break;
		case 86:
			((System.Windows.Controls.Button)target).Click += SaveVocabularyButton_Click;
			break;
		case 87:
			((System.Windows.Controls.Button)target).Click += ResetVocabularyButton_Click;
			break;
		case 88:
			LearnedCorrectionsSummaryTextBlock = (TextBlock)target;
			break;
		case 89:
			LearnedCorrectionsListBox = (System.Windows.Controls.ListBox)target;
			break;
		case 90:
			((System.Windows.Controls.Button)target).Click += ApproveLearnedCorrectionButton_Click;
			break;
		case 91:
			((System.Windows.Controls.Button)target).Click += UndoLearnedCorrectionButton_Click;
			break;
		case 92:
			SettingsPage = (Grid)target;
			break;
		case 93:
			SettingsScrollViewer = (ScrollViewer)target;
			SettingsScrollViewer.PreviewMouseWheel += SettingsScrollViewer_PreviewMouseWheel;
			break;
		case 94:
			LocaleComboBox = (System.Windows.Controls.ComboBox)target;
			LocaleComboBox.SelectionChanged += SettingsControl_Changed;
			break;
		case 95:
			EngineComboBox = (System.Windows.Controls.ComboBox)target;
			EngineComboBox.SelectionChanged += SettingsControl_Changed;
			break;
		case 96:
			TranscriptionModelComboBox = (System.Windows.Controls.ComboBox)target;
			TranscriptionModelComboBox.SelectionChanged += SettingsControl_Changed;
			break;
		case 97:
			CloudSttProviderComboBox = (System.Windows.Controls.ComboBox)target;
			CloudSttProviderComboBox.SelectionChanged += SettingsControl_Changed;
			break;
		case 98:
			CloudSttStatusTextBlock = (TextBlock)target;
			break;
		case 99:
			CloudSttModelComboBox = (System.Windows.Controls.ComboBox)target;
			CloudSttModelComboBox.SelectionChanged += SettingsControl_Changed;
			CloudSttModelComboBox.LostFocus += SettingsControl_Changed;
			break;
		case 100:
			((System.Windows.Controls.Button)target).Click += RefreshCloudSttModelsButton_Click;
			break;
		case 101:
			((System.Windows.Controls.Button)target).Click += TestCloudSttProviderButton_Click;
			break;
		case 102:
			CloudSttEndpointTextBox = (System.Windows.Controls.TextBox)target;
			CloudSttEndpointTextBox.TextChanged += SettingsControl_Changed;
			break;
		case 103:
			CloudSttApiKeyEnvTextBox = (System.Windows.Controls.TextBox)target;
			CloudSttApiKeyEnvTextBox.TextChanged += SettingsControl_Changed;
			break;
		case 104:
			WhisperDeviceComboBox = (System.Windows.Controls.ComboBox)target;
			WhisperDeviceComboBox.SelectionChanged += SettingsControl_Changed;
			break;
		case 105:
			ModelKeepAliveComboBox = (System.Windows.Controls.ComboBox)target;
			ModelKeepAliveComboBox.SelectionChanged += SettingsControl_Changed;
			break;
		case 106:
			AudioInputComboBox = (System.Windows.Controls.ComboBox)target;
			AudioInputComboBox.SelectionChanged += SettingsControl_Changed;
			break;
		case 107:
			OutputDestinationComboBox = (System.Windows.Controls.ComboBox)target;
			OutputDestinationComboBox.SelectionChanged += SettingsControl_Changed;
			break;
		case 108:
			LlmPolishProviderComboBox = (System.Windows.Controls.ComboBox)target;
			LlmPolishProviderComboBox.SelectionChanged += SettingsControl_Changed;
			break;
		case 109:
			LlmPolishStatusTextBlock = (TextBlock)target;
			break;
		case 110:
			LlmPolishModelComboBox = (System.Windows.Controls.ComboBox)target;
			LlmPolishModelComboBox.SelectionChanged += SettingsControl_Changed;
			LlmPolishModelComboBox.LostFocus += SettingsControl_Changed;
			break;
		case 111:
			((System.Windows.Controls.Button)target).Click += RefreshPolishModelsButton_Click;
			break;
		case 112:
			((System.Windows.Controls.Button)target).Click += TestPolishProviderButton_Click;
			break;
		case 113:
			LlmPolishEndpointTextBox = (System.Windows.Controls.TextBox)target;
			LlmPolishEndpointTextBox.TextChanged += SettingsControl_Changed;
			break;
		case 114:
			LlmPolishApiKeyEnvTextBox = (System.Windows.Controls.TextBox)target;
			LlmPolishApiKeyEnvTextBox.TextChanged += SettingsControl_Changed;
			break;
		case 115:
			LlmPolishTimeoutComboBox = (System.Windows.Controls.ComboBox)target;
			LlmPolishTimeoutComboBox.SelectionChanged += SettingsControl_Changed;
			break;
		case 116:
			RecordingRetentionComboBox = (System.Windows.Controls.ComboBox)target;
			RecordingRetentionComboBox.SelectionChanged += SettingsControl_Changed;
			break;
		case 117:
			ThemeComboBox = (System.Windows.Controls.ComboBox)target;
			ThemeComboBox.SelectionChanged += SettingsControl_Changed;
			break;
		case 118:
			KeepHistoryCheckBox = (System.Windows.Controls.CheckBox)target;
			KeepHistoryCheckBox.Checked += SettingsControl_Changed;
			KeepHistoryCheckBox.Unchecked += SettingsControl_Changed;
			break;
		case 119:
			ShowCompletionToastCheckBox = (System.Windows.Controls.CheckBox)target;
			ShowCompletionToastCheckBox.Checked += SettingsControl_Changed;
			ShowCompletionToastCheckBox.Unchecked += SettingsControl_Changed;
			break;
		case 120:
			AutoLearnCorrectionsCheckBox = (System.Windows.Controls.CheckBox)target;
			AutoLearnCorrectionsCheckBox.Checked += SettingsControl_Changed;
			AutoLearnCorrectionsCheckBox.Unchecked += SettingsControl_Changed;
			break;
		case 121:
			ShortcutCaptureButton = (System.Windows.Controls.Button)target;
			ShortcutCaptureButton.Click += ShortcutCaptureButton_Click;
			break;
		case 122:
			((System.Windows.Controls.Button)target).Click += ResetShortcutButton_Click;
			break;
		case 123:
			ShortcutStatusTextBlock = (TextBlock)target;
			break;
		case 124:
			ShowWidgetCheckBox = (System.Windows.Controls.CheckBox)target;
			ShowWidgetCheckBox.Checked += SettingsControl_Changed;
			ShowWidgetCheckBox.Unchecked += SettingsControl_Changed;
			break;
		case 125:
			MinimizeToTrayCheckBox = (System.Windows.Controls.CheckBox)target;
			MinimizeToTrayCheckBox.Checked += SettingsControl_Changed;
			MinimizeToTrayCheckBox.Unchecked += SettingsControl_Changed;
			break;
		case 126:
			StartWithWindowsCheckBox = (System.Windows.Controls.CheckBox)target;
			StartWithWindowsCheckBox.Checked += SettingsControl_Changed;
			StartWithWindowsCheckBox.Unchecked += SettingsControl_Changed;
			break;
		case 127:
			((System.Windows.Controls.Button)target).Click += SaveSettingsButton_Click;
			break;
		case 128:
			WhisperModelPathTextBlock = (TextBlock)target;
			break;
		case 129:
			WhisperWrapperPathTextBlock = (TextBlock)target;
			break;
		case 130:
			WhisperRuntimeStatusTextBlock = (TextBlock)target;
			break;
		case 131:
			LlmPolishRuntimeTextBlock = (TextBlock)target;
			break;
		case 132:
			StopLoadedModelButton = (System.Windows.Controls.Button)target;
			StopLoadedModelButton.Click += StopLoadedModelButton_Click;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
