using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace MaxFlowWindows;

public partial class LearningToastWindow : Window, IComponentConnector
{
	private const double RightOffset = 16.0;

	private const double BottomOffset = 76.0;

#if LEGACY_BAML_CONNECTOR
	internal TextBlock TitleTextBlock;

	internal TextBlock DetailTextBlock;

	private bool _contentLoaded;
#endif

	public LearningToastWindow(string title, string detail)
	{
		InitializeComponent();
		TitleTextBlock.Text = title;
		DetailTextBlock.Text = detail;
	}

	public void PlaceNearTaskbar()
	{
		Rect workArea = SystemParameters.WorkArea;
		double num = ((base.ActualWidth > 0.0) ? base.ActualWidth : base.Width);
		double num2 = ((base.ActualHeight > 0.0) ? base.ActualHeight : 82.0);
		base.Left = workArea.Right - num - 16.0;
		base.Top = workArea.Bottom - num2 - 76.0;
	}

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
		PlaceNearTaskbar();
	}

#if LEGACY_BAML_CONNECTOR
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.27.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/Speak;V0.5.0.0;component/learningtoastwindow.xaml", UriKind.Relative);
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
			((LearningToastWindow)target).Loaded += Window_Loaded;
			break;
		case 2:
			TitleTextBlock = (TextBlock)target;
			break;
		case 3:
			DetailTextBlock = (TextBlock)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
#endif
}
