using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TcmInzenjering.Plugin.Dialogs;

/// <summary>Jednostavan progress za duge komande (isti UI thread — Pump() osvežava bar).</summary>
public sealed class CommandProgressWindow : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _status;
    private readonly TextBlock _percent;

    public CommandProgressWindow(string title = "TCM-ROADS")
    {
        Title = title;
        Width = 420;
        Height = 130;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStyle = WindowStyle.ToolWindow;
        Background = SystemColors.ControlBrush;

        var root = new StackPanel { Margin = new Thickness(16) };
        _status = new TextBlock
        {
            Text = "Pripremam…",
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap
        };
        _bar = new ProgressBar
        {
            Height = 22,
            Minimum = 0,
            Maximum = 100,
            Value = 0
        };
        _percent = new TextBlock
        {
            Text = "0 %",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37))
        };

        root.Children.Add(_status);
        root.Children.Add(_bar);
        root.Children.Add(_percent);
        Content = root;
    }

    public void Report(int percent, string status)
    {
        percent = Math.Max(0, Math.Min(100, percent));
        void Apply()
        {
            _bar.IsIndeterminate = false;
            _bar.Value = percent;
            _status.Text = status;
            _percent.Text = $"{percent} %";
        }

        if (Dispatcher.CheckAccess())
        {
            Apply();
            Pump();
        }
        else
        {
            Dispatcher.Invoke(Apply);
            Dispatcher.Invoke(Pump, DispatcherPriority.Background);
        }
    }

    public void SetIndeterminate(string status)
    {
        void Apply()
        {
            _bar.IsIndeterminate = true;
            _status.Text = status;
            _percent.Text = "…";
        }

        if (Dispatcher.CheckAccess())
        {
            Apply();
            Pump();
        }
        else
        {
            Dispatcher.Invoke(Apply);
        }
    }

    public static void Pump()
    {
        try
        {
            Dispatcher.CurrentDispatcher.Invoke(
                DispatcherPriority.Background,
                new Action(static () => { }));
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Sinhroni progress — Report se izvršava odmah (ne preko dispatcher reda).
    /// Standardni Progress&lt;T&gt; poštuje SynchronizationContext pa se na zauzetom
    /// UI thread-u izveštaji nikad ne obrade i prozor ostane prazan.
    /// </summary>
    public IProgress<(int Percent, string Status)> AsProgress() =>
        new SyncProgress(this);

    private sealed class SyncProgress : IProgress<(int Percent, string Status)>
    {
        private readonly CommandProgressWindow _window;

        public SyncProgress(CommandProgressWindow window) => _window = window;

        public void Report((int Percent, string Status) value) =>
            _window.Report(value.Percent, value.Status);
    }
}
