using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MaxFlowWindows.Core;

namespace MaxFlowWindows;

public sealed class FirstRunWindow : Window, IDisposable
{
    private const int ReadinessStep = 1;
    private const int FinalStep = 4;

    private int _currentStep;
    private bool _disposed;
    private bool _isCheckingReadiness;
    private bool _isRunningSelfTest;
    private readonly Grid _contentGrid;
    private readonly TextBlock _titleText;
    private readonly TextBlock _descText;
    private readonly TextBlock _readinessText;
    private readonly Button _selfTestButton;
    private readonly Button _nextButton;
    private readonly Button _skipButton;
    private readonly StackPanel _dotsPanel;
    private readonly CancellationTokenSource _readinessCts = new();
    private WhisperReadinessReport? _readinessReport;

    public bool LocalRuntimeReady => _readinessReport?.Find("self-test")?.IsReady == true;

    private static readonly (string Title, string Desc)[] Steps = new[]
    {
        ("Welcome to Speak",
         "Your local-first dictation companion.\n\nLocal Whisper keeps transcription on this computer. Optional Cloud STT sends recorded audio to the provider you select, and optional cloud polish sends transcript text and the local draft. Both cloud features require your configuration."),
        ("Local Whisper readiness", ""),
        ("Microphone Check",
         "Make sure your microphone is connected and selected.\nYou can change this anytime in Settings → Audio Input."),
        ("Your Shortcut Key",
         "The default shortcut is Ctrl+Win.\nPress it anywhere to start/stop dictation.\nConfigure it in Settings."),
        ("Local Whisper status", "")
    };

    public FirstRunWindow()
    {
        Title = "Welcome to Speak";
        Width = 560;
        Height = 500;
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

        var contentPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

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
        contentPanel.Children.Add(_descText);

        _readinessText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(220, 170, 90)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Left,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 18, 0, 0)
        };
        contentPanel.Children.Add(_readinessText);

        _selfTestButton = new Button
        {
            Content = "Run local self-test",
            MinWidth = 184,
            MinHeight = 34,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(0, 122, 255)),
            Foreground = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 16, 0, 0)
        };
        _selfTestButton.Click += async (_, _) => await RunSelfTestAsync();
        contentPanel.Children.Add(_selfTestButton);

        _contentGrid.Children.Add(contentPanel);
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
        Loaded += OnLoaded;
        Closed += OnClosed;
        RenderCurrentStep();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _readinessCts.Cancel();
        _readinessCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshReadinessAsync();
        if (_readinessReport?.IsReady == true && !_readinessCts.IsCancellationRequested)
        {
            await RunSelfTestAsync();
        }
    }

    private async Task RefreshReadinessAsync()
    {
        if (_isCheckingReadiness || _isRunningSelfTest)
        {
            return;
        }

        _isCheckingReadiness = true;
        RenderCurrentStep();
        try
        {
            _readinessReport = await WhisperRuntimeReadiness.CheckAsync(
                AppConfig.Current,
                _readinessCts.Token);
        }
        catch (OperationCanceledException) when (_readinessCts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _readinessReport = FailureReport("Readiness check", exception.Message);
        }
        finally
        {
            _isCheckingReadiness = false;
            if (!_readinessCts.IsCancellationRequested)
            {
                RenderCurrentStep();
            }
        }
    }

    private async Task RunSelfTestAsync()
    {
        if (_isCheckingReadiness || _isRunningSelfTest)
        {
            return;
        }

        _isRunningSelfTest = true;
        RenderCurrentStep();
        try
        {
            _readinessReport = await WhisperRuntimeReadiness.RunSelfTestAsync(
                AppConfig.Current,
                _readinessCts.Token);
        }
        catch (OperationCanceledException) when (_readinessCts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _readinessReport = FailureReport("Local worker self-test", exception.Message);
        }
        finally
        {
            _isRunningSelfTest = false;
            if (!_readinessCts.IsCancellationRequested)
            {
                RenderCurrentStep();
            }
        }
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        _currentStep++;
        if (_currentStep >= Steps.Length)
        {
            Close();
            return;
        }

        RenderCurrentStep();
    }

    private void RenderCurrentStep()
    {
        _titleText.Text = Steps[_currentStep].Title;
        _descText.Text = Steps[_currentStep].Desc;
        _readinessText.Visibility = Visibility.Collapsed;
        _selfTestButton.Visibility = Visibility.Collapsed;

        if (_currentStep == ReadinessStep)
        {
            RenderReadinessStep();
        }
        else if (_currentStep == FinalStep)
        {
            RenderFinalStep();
        }

        _skipButton.Content = _currentStep == FinalStep ? "Close" : "Skip";
        _nextButton.Content = _currentStep == FinalStep
            ? FinalButtonText()
            : "Next →";
        _nextButton.IsEnabled = _currentStep != FinalStep || LocalRuntimeReady;

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

    private void RenderReadinessStep()
    {
        _titleText.Text = "Local Whisper readiness";
        _descText.Text = "Before Speak enables local dictation, it checks the installed Python runtime, Whisper worker script, base model, and FFmpeg. Nothing is uploaded.";
        _readinessText.Visibility = Visibility.Visible;
        _selfTestButton.Visibility = Visibility.Visible;

        if (_isCheckingReadiness)
        {
            _readinessText.Foreground = new SolidColorBrush(Color.FromRgb(220, 170, 90));
            _readinessText.Text = "Checking installed local components…";
            _selfTestButton.Content = "Checking…";
            _selfTestButton.IsEnabled = false;
            return;
        }

        if (_isRunningSelfTest)
        {
            _readinessText.Foreground = new SolidColorBrush(Color.FromRgb(220, 170, 90));
            _readinessText.Text = "Starting the installed worker on a private loopback port, checking its authenticated health endpoint, and loading the configured base model…";
            _selfTestButton.Content = "Running self-test…";
            _selfTestButton.IsEnabled = false;
            return;
        }

        RenderReadinessReport();
        _selfTestButton.Content = HasCompletedSelfTest()
            ? "Run local self-test again"
            : "Run local self-test";
        _selfTestButton.IsEnabled = true;
    }

    private void RenderFinalStep()
    {
        _readinessText.Visibility = Visibility.Visible;
        _selfTestButton.Visibility = Visibility.Visible;

        if (_isCheckingReadiness || _isRunningSelfTest || _readinessReport == null)
        {
            _titleText.Text = "Checking local Whisper";
            _descText.Text = "Speak is still verifying the local runtime. Do not assume local dictation is ready until this screen reports that every required component passed.";
            _readinessText.Foreground = new SolidColorBrush(Color.FromRgb(220, 170, 90));
            _readinessText.Text = "Checking local components…";
            _selfTestButton.Content = "Checking…";
            _selfTestButton.IsEnabled = false;
            return;
        }

        if (LocalRuntimeReady)
        {
            _titleText.Text = "Local Whisper is ready";
            _descText.Text = "Python, the worker, the base model, and FFmpeg passed their checks, and the installed worker loaded the base model successfully. Speak can keep transcription on this computer.";
        }
        else
        {
            _titleText.Text = "Local Whisper needs setup";
            _descText.Text = "Local dictation is unavailable until every required component below passes. Speak will not claim it is ready. Repair or reinstall the missing component, then run the local self-test again.";
        }

        RenderReadinessReport();
        _selfTestButton.Content = HasCompletedSelfTest()
            ? "Run local self-test again"
            : "Run local self-test";
        _selfTestButton.IsEnabled = true;
    }

    private void RenderReadinessReport()
    {
        if (_readinessReport == null)
        {
            _readinessText.Foreground = new SolidColorBrush(Color.FromRgb(220, 170, 90));
            _readinessText.Text = "The readiness check has not completed yet.";
            return;
        }

        _readinessText.Foreground = new SolidColorBrush(_readinessReport.IsReady
            ? Color.FromRgb(115, 210, 145)
            : Color.FromRgb(235, 125, 125));
        _readinessText.Text = string.Join(
            Environment.NewLine,
            _readinessReport.Checks.Select(check =>
                $"{(check.IsReady ? "✓" : "×")} {check.Label}: {check.Detail}"
                + (string.IsNullOrWhiteSpace(check.Location) ? "" : $"\n   {check.Location}")));
    }

    private string FinalButtonText()
    {
        if (LocalRuntimeReady)
        {
            return "Finish ✓";
        }

        return _readinessReport == null || _isCheckingReadiness || _isRunningSelfTest
            ? "Finish (checking…)"
            : "Finish (run self-test first)";
    }

    private bool HasCompletedSelfTest()
    {
        return _readinessReport?.Find("self-test") is not null;
    }

    private static WhisperReadinessReport FailureReport(string label, string detail)
    {
        return new WhisperReadinessReport(new[]
        {
            new WhisperReadinessCheck(
                "readiness",
                label,
                false,
                string.IsNullOrWhiteSpace(detail) ? "unexpected readiness check failure" : detail,
                "")
        });
    }
}
