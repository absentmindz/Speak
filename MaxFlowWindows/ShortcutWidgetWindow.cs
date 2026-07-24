using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MaxFlowWindows.Core;

namespace MaxFlowWindows;

public class ShortcutWidgetWindow : Window, IComponentConnector
{
	private const double CompactWidth = 36.0;

	private const double CompactHeight = 36.0;

	private const double ExpandedWidth = 220.0;

	private const double ExpandedHeight = 36.0;

	private const double DefaultRightOffset = 14.0;

	private const double DefaultBottomOffset = 14.0;

	private static Duration LayoutTransitionDuration = new Duration(TimeSpan.FromMilliseconds(285.0));

	private static Duration PanelFadeDuration = new Duration(TimeSpan.FromMilliseconds(185.0));

	private static readonly double[] MicBarWeights = new double[7] { 0.34, 0.58, 0.84, 1.0, 0.76, 0.52, 0.3 };

	private bool _isExpanded;

	private bool _wasRecording;

	private bool _userMoved;

	private bool _hasCompactHome;

	private double _compactHomeLeft;

	private double _compactHomeTop;

	private double _smoothedMicLevel = 0.08;

	private int _transitionVersion;

	private DispatcherTimer? _collapseTimer;

	internal Grid WidgetRoot;

	internal Border WidgetShell;

	internal Grid CompactPanel;

	internal Button CompactMicButton;

	internal TextBlock CompactMicGlyph;

	internal StackPanel CompactActivityPanel;

	internal Border CompactBar1;

	internal Border CompactBar2;

	internal Border CompactBar3;

	internal Border CompactBar4;

	internal Border CompactBar5;

	internal Border CompactBar6;

	internal Border CompactBar7;

	internal Grid ExpandedPanel;

	internal Border StatusPill;

	internal StackPanel ExpandedActivityPanel;

	internal Border ExpandedBar1;

	internal Border ExpandedBar2;

	internal Border ExpandedBar3;

	internal Border ExpandedBar4;

	internal Border ExpandedBar5;

	internal Border ExpandedBar6;

	internal Border ExpandedBar7;

	internal TextBlock ModeTextBlock;

	internal TextBlock RecordingTimerTextBlock;

	internal TextBlock ShortcutTextBlock;

	internal Button MicButton;

	private bool _contentLoaded;

	public event EventHandler? ToggleRecordingRequested;

	public event EventHandler? OpenMainWindowRequested;

	public event EventHandler? OpenSettingsRequested;

	public event EventHandler? CycleModeRequested;

	public ShortcutWidgetWindow()
	{
		InitializeComponent();
		base.Width = 36.0;
		base.Height = 36.0;
	}

	public void SetState(string modeName, string shortcutText, bool isRecording, bool isTranscribing, TimeSpan recordingElapsed)
	{
		if (isRecording != _wasRecording)
		{
			_wasRecording = isRecording;
			SetRecordingVisualization(isRecording);
		}
		ModeTextBlock.Text = (isRecording ? "Listening" : modeName);
		ExpandedActivityPanel.Visibility = ((!isRecording) ? Visibility.Collapsed : Visibility.Visible);
		CompactActivityPanel.Visibility = ((!isRecording) ? Visibility.Collapsed : Visibility.Visible);
		CompactMicGlyph.Visibility = (isRecording ? Visibility.Collapsed : Visibility.Visible);
		RecordingTimerTextBlock.Visibility = ((!isRecording) ? Visibility.Collapsed : Visibility.Visible);
		RecordingTimerTextBlock.Text = " " + FormatElapsed(recordingElapsed);
		ShortcutTextBlock.Text = " " + shortcutText;
		MicButton.Content = (isRecording ? "\ue71a" : "\ue720");
		UpdateWidgetChrome(isRecording);
		base.ToolTip = (isTranscribing ? "Speak is transcribing" : "Speak dictation shortcut");
	}

	public void SetMicActivity(double level)
	{
		if (!base.Dispatcher.CheckAccess())
		{
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				SetMicActivity(level);
			});
		}
		else if (_wasRecording)
		{
			double num = Math.Clamp(level, 0.0, 1.0);
			_smoothedMicLevel = ((num > _smoothedMicLevel) ? (_smoothedMicLevel * 0.28 + num * 0.72) : (_smoothedMicLevel * 0.7 + num * 0.3));
			ApplyMicLevel(CompactRecordingBars(), _smoothedMicLevel);
			ApplyMicLevel(ExpandedRecordingBars(), _smoothedMicLevel);
		}
	}

	public void SetTransitionDurations(int layoutMilliseconds, int fadeMilliseconds)
	{
		LayoutTransitionDuration = new Duration(TimeSpan.FromMilliseconds(Math.Clamp(layoutMilliseconds, 180, 480)));
		PanelFadeDuration = new Duration(TimeSpan.FromMilliseconds(Math.Clamp(fadeMilliseconds, 120, 320)));
	}

	public void PlaceNearTaskbar()
	{
		if (!_userMoved)
		{
			double num = ((base.Width > 0.0) ? base.Width : 36.0);
			double num2 = ((base.Height > 0.0) ? base.Height : 36.0);
			base.Left = SystemParameters.WorkArea.Right - num - 14.0;
			base.Top = SystemParameters.WorkArea.Bottom - num2 - 14.0;
			ClampToWorkArea(num, num2);
			SetCompactHome(base.Left, base.Top);
		}
	}

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
		PlaceNearTaskbar();
	}

	private void Window_MouseEnter(object sender, MouseEventArgs e)
	{
		CancelScheduledCollapse();
		SetExpanded(isExpanded: true);
	}

	private void Window_MouseLeave(object sender, MouseEventArgs e)
	{
		ScheduleCollapseAfterHoverGrace();
	}

	private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount > 1)
		{
			this.OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
			return;
		}
		try
		{
			PreserveAnimatedLayout();
			DragMove();
			_userMoved = true;
			ClampToWorkArea((base.ActualWidth > 0.0) ? base.ActualWidth : base.Width, (base.ActualHeight > 0.0) ? base.ActualHeight : base.Height);
			CaptureCompactHomeFromCurrent(_isExpanded);
		}
		catch (Exception exception)
		{
			AppLog.Warn("Shortcut widget drag failed.", exception);
		}
	}

	private void ToggleButton_Click(object sender, RoutedEventArgs e)
	{
		this.ToggleRecordingRequested?.Invoke(this, EventArgs.Empty);
	}

	private void OpenButton_Click(object sender, RoutedEventArgs e)
	{
		this.OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
	}

	private void SettingsButton_Click(object sender, RoutedEventArgs e)
	{
		this.OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
	}

	private void CycleModeButton_Click(object sender, RoutedEventArgs e)
	{
		this.CycleModeRequested?.Invoke(this, EventArgs.Empty);
	}

	private void SetExpanded(bool isExpanded)
	{
		if (_isExpanded == isExpanded)
		{
			return;
		}
		PreserveAnimatedLayout();
		if (!_hasCompactHome)
		{
			SetCompactHome(base.Left, base.Top);
		}
		_isExpanded = isExpanded;
		UpdateWidgetChrome(_wasRecording);
		double num = (isExpanded ? 220.0 : 36.0);
		double num2 = (isExpanded ? 36.0 : 36.0);
		double value = (isExpanded ? (_compactHomeLeft + 36.0 - 220.0) : _compactHomeLeft);
		double value2 = (isExpanded ? (_compactHomeTop + 36.0 - 36.0) : _compactHomeTop);
		Rect workArea = SystemParameters.WorkArea;
		value = Math.Clamp(value, workArea.Left + 6.0, workArea.Right - num - 6.0);
		value2 = Math.Clamp(value2, workArea.Top + 6.0, workArea.Bottom - num2 - 6.0);
		if (isExpanded)
		{
			ExpandedPanel.Visibility = Visibility.Visible;
			CompactPanel.Visibility = Visibility.Visible;
			AnimateOpacity(ExpandedPanel, 1.0);
			AnimateOpacity(CompactPanel, 0.0);
		}
		else
		{
			CompactPanel.Visibility = Visibility.Visible;
			AnimateOpacity(CompactPanel, 1.0);
			DoubleAnimation doubleAnimation = CreateFadeAnimation(0.0);
			doubleAnimation.Completed += delegate
			{
				if (!_isExpanded)
				{
					ExpandedPanel.Visibility = Visibility.Collapsed;
				}
			};
			ExpandedPanel.BeginAnimation(UIElement.OpacityProperty, doubleAnimation);
		}
		BeginWidgetTransition(num, num2, value, value2);
	}

	private void BeginWidgetTransition(double targetWidth, double targetHeight, double targetLeft, double targetTop)
	{
		double width = base.Width;
		double height = base.Height;
		double left = base.Left;
		double top = base.Top;
		int version = ++_transitionVersion;
		DoubleAnimation doubleAnimation = CreateLayoutAnimation(left, targetLeft);
		doubleAnimation.Completed += delegate
		{
			if (version == _transitionVersion)
			{
				CompleteWidgetTransition(targetWidth, targetHeight, targetLeft, targetTop);
			}
		};
		BeginAnimation(FrameworkElement.WidthProperty, CreateLayoutAnimation(width, targetWidth));
		BeginAnimation(FrameworkElement.HeightProperty, CreateLayoutAnimation(height, targetHeight));
		BeginAnimation(Window.LeftProperty, doubleAnimation);
		BeginAnimation(Window.TopProperty, CreateLayoutAnimation(top, targetTop));
	}

	private void CompleteWidgetTransition(double targetWidth, double targetHeight, double targetLeft, double targetTop)
	{
		BeginAnimation(FrameworkElement.WidthProperty, null);
		BeginAnimation(FrameworkElement.HeightProperty, null);
		BeginAnimation(Window.LeftProperty, null);
		BeginAnimation(Window.TopProperty, null);
		base.Width = targetWidth;
		base.Height = targetHeight;
		base.Left = targetLeft;
		base.Top = targetTop;
		if (!_isExpanded)
		{
			SetCompactHome(base.Left, base.Top);
		}
		if (_isExpanded && !base.IsMouseOver)
		{
			ScheduleCollapseAfterHoverGrace();
		}
		else if (!_isExpanded && base.IsMouseOver)
		{
			SetExpanded(isExpanded: true);
		}
	}

	private void CaptureCompactHomeFromCurrent(bool currentLayoutIsExpanded)
	{
		double num = ((base.ActualWidth > 0.0) ? base.ActualWidth : base.Width);
		double num2 = ((base.ActualHeight > 0.0) ? base.ActualHeight : base.Height);
		double left = (currentLayoutIsExpanded ? (base.Left + num - 36.0) : base.Left);
		double top = (currentLayoutIsExpanded ? (base.Top + num2 - 36.0) : base.Top);
		SetCompactHome(left, top);
	}

	private void SetCompactHome(double left, double top)
	{
		Rect workArea = SystemParameters.WorkArea;
		_compactHomeLeft = Math.Clamp(left, workArea.Left + 6.0, workArea.Right - 36.0 - 6.0);
		_compactHomeTop = Math.Clamp(top, workArea.Top + 6.0, workArea.Bottom - 36.0 - 6.0);
		_hasCompactHome = true;
	}

	private void ScheduleCollapseAfterHoverGrace()
	{
		CancelScheduledCollapse();
		_collapseTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(120.0)
		};
		_collapseTimer.Tick += delegate
		{
			CancelScheduledCollapse();
			if (!base.IsMouseOver)
			{
				SetExpanded(isExpanded: false);
			}
		};
		_collapseTimer.Start();
	}

	private void CancelScheduledCollapse()
	{
		if (_collapseTimer != null)
		{
			_collapseTimer.Stop();
			_collapseTimer = null;
		}
	}

	private void ClampToWorkArea(double width, double height)
	{
		Rect workArea = SystemParameters.WorkArea;
		base.Left = Math.Clamp(base.Left, workArea.Left + 6.0, workArea.Right - width - 6.0);
		base.Top = Math.Clamp(base.Top, workArea.Top + 6.0, workArea.Bottom - height - 6.0);
	}

	private void PreserveAnimatedLayout()
	{
		double left = base.Left;
		double top = base.Top;
		double width = ((base.ActualWidth > 0.0) ? base.ActualWidth : base.Width);
		double height = ((base.ActualHeight > 0.0) ? base.ActualHeight : base.Height);
		BeginAnimation(Window.LeftProperty, null);
		BeginAnimation(Window.TopProperty, null);
		BeginAnimation(FrameworkElement.WidthProperty, null);
		BeginAnimation(FrameworkElement.HeightProperty, null);
		base.Left = left;
		base.Top = top;
		base.Width = width;
		base.Height = height;
	}

	private void SetRecordingVisualization(bool isRecording)
	{
		if (!isRecording)
		{
			_smoothedMicLevel = 0.08;
			StopRecordingBars(CompactRecordingBars());
			StopRecordingBars(ExpandedRecordingBars());
		}
		else
		{
			SetMicActivity(0.12);
		}
	}

	private void UpdateWidgetChrome(bool isRecording)
	{
		bool flag = isRecording && !_isExpanded;
		SolidColorBrush solidColorBrush = new SolidColorBrush(Color.FromRgb(26, 27, 31));
		SolidColorBrush solidColorBrush2 = new SolidColorBrush(Color.FromRgb(28, 29, 32));
		SolidColorBrush solidColorBrush3 = new SolidColorBrush(Color.FromRgb(48, 49, 54));
		SolidColorBrush solidColorBrush4 = new SolidColorBrush(Color.FromRgb(184, 177, 164));
		SolidColorBrush solidColorBrush5 = new SolidColorBrush(Colors.Transparent);
		WidgetShell.Background = (flag ? solidColorBrush5.Clone() : new SolidColorBrush(Color.FromRgb(23, 24, 27)));
		WidgetShell.BorderBrush = (flag ? solidColorBrush5.Clone() : (isRecording ? solidColorBrush4.Clone() : solidColorBrush3.Clone()));
		WidgetShell.Effect = (flag ? null : (TryFindResource("WidgetShadow") as Effect));
		CompactMicButton.Background = (flag ? solidColorBrush5.Clone() : (isRecording ? solidColorBrush2.Clone() : solidColorBrush.Clone()));
		CompactMicButton.BorderBrush = (flag ? solidColorBrush5.Clone() : (isRecording ? solidColorBrush4.Clone() : solidColorBrush3.Clone()));
		MicButton.Background = (isRecording ? solidColorBrush2.Clone() : solidColorBrush.Clone());
		MicButton.BorderBrush = (isRecording ? solidColorBrush4.Clone() : solidColorBrush3.Clone());
		StatusPill.BorderBrush = (isRecording ? solidColorBrush4.Clone() : solidColorBrush3.Clone());
	}

	private Border[] CompactRecordingBars()
	{
		return new Border[7] { CompactBar1, CompactBar2, CompactBar3, CompactBar4, CompactBar5, CompactBar6, CompactBar7 };
	}

	private Border[] ExpandedRecordingBars()
	{
		return new Border[7] { ExpandedBar1, ExpandedBar2, ExpandedBar3, ExpandedBar4, ExpandedBar5, ExpandedBar6, ExpandedBar7 };
	}

	private static void StopRecordingBars(IReadOnlyList<Border> bars)
	{
		ScaleTransform scaleTransform2;
		double scaleY;
		for (int i = 0; i < bars.Count; scaleTransform2.ScaleY = scaleY, bars[i].Opacity = 0.58, i++)
		{
			ScaleTransform scaleTransform = EnsureScaleTransform(bars[i]);
			scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			bars[i].BeginAnimation(UIElement.OpacityProperty, null);
			scaleTransform2 = scaleTransform;
			bool flag;
			switch (i)
			{
			case 3:
				scaleY = 0.44;
				continue;
			case 2:
			case 4:
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			scaleY = (flag ? 0.32 : 0.2);
		}
	}

	private static void ApplyMicLevel(IReadOnlyList<Border> bars, double level)
	{
		for (int i = 0; i < bars.Count; i++)
		{
			ScaleTransform scaleTransform = EnsureScaleTransform(bars[i]);
			double value = Math.Clamp(0.18 + Math.Pow(level, 0.72) * MicBarWeights[i] * 0.86, 0.18, 1.0);
			scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation
			{
				To = value,
				Duration = TimeSpan.FromMilliseconds(55.0),
				EasingFunction = new QuadraticEase
				{
					EasingMode = EasingMode.EaseOut
				},
				FillBehavior = FillBehavior.HoldEnd
			});
			bars[i].BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
			{
				To = Math.Clamp(0.5 + level * 0.5, 0.5, 1.0),
				Duration = TimeSpan.FromMilliseconds(55.0),
				EasingFunction = new QuadraticEase
				{
					EasingMode = EasingMode.EaseOut
				},
				FillBehavior = FillBehavior.HoldEnd
			});
		}
	}

	private static ScaleTransform EnsureScaleTransform(Border bar)
	{
		if (bar.RenderTransform is ScaleTransform scaleTransform)
		{
			ScaleTransform scaleTransform2 = (scaleTransform.IsFrozen ? scaleTransform.CloneCurrentValue() : scaleTransform);
			if (scaleTransform2 != scaleTransform)
			{
				bar.RenderTransform = scaleTransform2;
			}
			return scaleTransform2;
		}
		return (ScaleTransform)(bar.RenderTransform = new ScaleTransform(1.0, 1.0));
	}

	private static void AnimateOpacity(UIElement element, double opacity)
	{
		element.BeginAnimation(UIElement.OpacityProperty, CreateFadeAnimation(opacity));
	}

	private static DoubleAnimation CreateLayoutAnimation(double from, double to)
	{
		return new DoubleAnimation
		{
			From = from,
			To = to,
			Duration = LayoutTransitionDuration,
			EasingFunction = new QuarticEase
			{
				EasingMode = EasingMode.EaseOut
			},
			FillBehavior = FillBehavior.Stop
		};
	}

	private static DoubleAnimation CreateFadeAnimation(double to)
	{
		return new DoubleAnimation
		{
			To = to,
			Duration = PanelFadeDuration,
			EasingFunction = new CubicEase
			{
				EasingMode = EasingMode.EaseOut
			},
			FillBehavior = FillBehavior.HoldEnd
		};
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

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.27.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/Speak;V0.5.0.0;component/shortcutwidgetwindow.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocator);
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
			((ShortcutWidgetWindow)target).Loaded += Window_Loaded;
			((ShortcutWidgetWindow)target).MouseEnter += Window_MouseEnter;
			((ShortcutWidgetWindow)target).MouseLeave += Window_MouseLeave;
			((ShortcutWidgetWindow)target).MouseLeftButtonDown += Window_MouseLeftButtonDown;
			break;
		case 2:
			WidgetRoot = (Grid)target;
			break;
		case 3:
			WidgetShell = (Border)target;
			break;
		case 4:
			CompactPanel = (Grid)target;
			break;
		case 5:
			CompactMicButton = (Button)target;
			CompactMicButton.Click += ToggleButton_Click;
			break;
		case 6:
			CompactMicGlyph = (TextBlock)target;
			break;
		case 7:
			CompactActivityPanel = (StackPanel)target;
			break;
		case 8:
			CompactBar1 = (Border)target;
			break;
		case 9:
			CompactBar2 = (Border)target;
			break;
		case 10:
			CompactBar3 = (Border)target;
			break;
		case 11:
			CompactBar4 = (Border)target;
			break;
		case 12:
			CompactBar5 = (Border)target;
			break;
		case 13:
			CompactBar6 = (Border)target;
			break;
		case 14:
			CompactBar7 = (Border)target;
			break;
		case 15:
			ExpandedPanel = (Grid)target;
			break;
		case 16:
			StatusPill = (Border)target;
			break;
		case 17:
			ExpandedActivityPanel = (StackPanel)target;
			break;
		case 18:
			ExpandedBar1 = (Border)target;
			break;
		case 19:
			ExpandedBar2 = (Border)target;
			break;
		case 20:
			ExpandedBar3 = (Border)target;
			break;
		case 21:
			ExpandedBar4 = (Border)target;
			break;
		case 22:
			ExpandedBar5 = (Border)target;
			break;
		case 23:
			ExpandedBar6 = (Border)target;
			break;
		case 24:
			ExpandedBar7 = (Border)target;
			break;
		case 25:
			ModeTextBlock = (TextBlock)target;
			break;
		case 26:
			RecordingTimerTextBlock = (TextBlock)target;
			break;
		case 27:
			ShortcutTextBlock = (TextBlock)target;
			break;
		case 28:
			((Button)target).Click += OpenButton_Click;
			break;
		case 29:
			MicButton = (Button)target;
			MicButton.Click += ToggleButton_Click;
			break;
		case 30:
			((Button)target).Click += CycleModeButton_Click;
			break;
		case 31:
			((Button)target).Click += SettingsButton_Click;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
