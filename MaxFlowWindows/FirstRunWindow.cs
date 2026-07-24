using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MaxFlowWindows;

public sealed class FirstRunWindow : Window
{
    private int _currentStep;
    private readonly Grid _contentGrid;
    private readonly TextBlock _titleText;
    private readonly TextBlock _descText;
    private readonly Button _nextButton;
    private readonly Button _skipButton;
    private readonly StackPanel _dotsPanel;

    private static readonly (string Title, string Desc)[] Steps = new[]
    {
        ("Welcome to Speak",
         "Your local-first dictation companion.\n\nSpeak runs entirely on your machine.\nNo data ever leaves your computer."),
        ("Microphone Check",
         "Make sure your microphone is connected and selected.\nYou can change this anytime in Settings → Audio Input."),
        ("Your Shortcut Key",
         "The default shortcut is Ctrl+Win.\nPress it anywhere to start/stop dictation.\nConfigure it in Settings."),
        ("Ready to Go",
         "You're all set. Click Finish to start dictating.\n\nTip: Speak works in any app — email, code editor, browser, chat.")
    };

    public FirstRunWindow()
    {
        Title = "Welcome to Speak";
        Width = 520;
        Height = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(28, 28, 30));

        var root = new Grid
        {
            Margin = new Thickness(32, 28, 32, 20),
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };

        _titleText = new TextBlock
        {
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            Text = Steps[0].Title,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(_titleText);

        _contentGrid = new Grid();
        Grid.SetRow(_contentGrid, 1);

        _descText = new TextBlock
        {
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 185)),
            TextWrapping = TextWrapping.Wrap,
            Text = Steps[0].Desc,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _contentGrid.Children.Add(_descText);
        root.Children.Add(_contentGrid);

        var bottomPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(bottomPanel, 2);

        _dotsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        for (int i = 0; i < Steps.Length; i++)
        {
            _dotsPanel.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(i == 0 ? Color.FromRgb(0, 122, 255) : Color.FromRgb(80, 80, 85)),
                Margin = new Thickness(4, 0, 4, 0)
            });
        }
        bottomPanel.Children.Add(_dotsPanel);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _skipButton = new Button
        {
            Content = "Skip",
            MinWidth = 100,
            MinHeight = 36,
            Margin = new Thickness(0, 0, 12, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 185)),
            BorderThickness = new Thickness(0)
        };
        _skipButton.Click += (_, _) => Close();

        _nextButton = new Button
        {
            Content = "Next →",
            MinWidth = 120,
            MinHeight = 36,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(0, 122, 255)),
            Foreground = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(0)
        };
        _nextButton.Click += OnNextClick;

        buttonPanel.Children.Add(_skipButton);
        buttonPanel.Children.Add(_nextButton);
        bottomPanel.Children.Add(buttonPanel);
        root.Children.Add(bottomPanel);

        Content = root;
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        _currentStep++;
        if (_currentStep >= Steps.Length)
        {
            Close();
            return;
        }

        _titleText.Text = Steps[_currentStep].Title;
        _descText.Text = Steps[_currentStep].Desc;
        _nextButton.Content = _currentStep == Steps.Length - 1 ? "Finish ✓" : "Next →";

        for (int i = 0; i < _dotsPanel.Children.Count; i++)
        {
            if (_dotsPanel.Children[i] is Ellipse dot)
            {
                dot.Fill = new SolidColorBrush(i == _currentStep
                    ? Color.FromRgb(0, 122, 255)
                    : Color.FromRgb(80, 80, 85));
            }
        }
    }
}