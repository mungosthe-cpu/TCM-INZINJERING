using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TcmInzenjering.Plugin.Roads;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class ChainageFormatDialog : Window
{
    private bool _updating;

    public int SelectedFormat { get; private set; } = ChainageFormatter.DefaultFormat;

    public ChainageFormatDialog(int currentFormat, Window? owner = null)
    {
        SelectedFormat = ChainageFormatter.ClampFormat(currentFormat);
        if (owner is not null)
        {
            Owner = owner;
        }

        InitializeComponent();
        FormatList.ItemsSource = ChainageFormatter.GetAllSamples()
            .Select(sample => new FormatSampleViewModel(sample.Index, sample.Sample))
            .ToList();
        FormatNumberBox.Text = SelectedFormat.ToString(CultureInfo.InvariantCulture);
    }

    private void OnFormatNumberChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        if (!int.TryParse(FormatNumberBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var format))
        {
            return;
        }

        if (format < ChainageFormatter.MinFormat || format > ChainageFormatter.MaxFormat)
        {
            return;
        }

        SelectedFormat = format;
    }

    private void OnFormatItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not int format)
        {
            return;
        }

        SelectedFormat = format;
        _updating = true;
        try
        {
            FormatNumberBox.Text = format.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnDefaults(object sender, RoutedEventArgs e)
    {
        SelectedFormat = ChainageFormatter.DefaultFormat;
        _updating = true;
        try
        {
            FormatNumberBox.Text = SelectedFormat.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!TryReadFormat(out var message))
        {
            MessageBox.Show(this, message, "TCM-INZINJERING", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private bool TryReadFormat(out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!int.TryParse(FormatNumberBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var format) ||
            format < ChainageFormatter.MinFormat ||
            format > ChainageFormatter.MaxFormat)
        {
            errorMessage = $"Format mora biti ceo broj od {ChainageFormatter.MinFormat} do {ChainageFormatter.MaxFormat}.";
            return false;
        }

        SelectedFormat = format;
        return true;
    }

    private sealed class FormatSampleViewModel
    {
        public FormatSampleViewModel(int index, string sample)
        {
            Index = index;
            Sample = sample;
        }

        public int Index { get; }
        public string Sample { get; }
        public string DisplayText => $"{Index}. {Sample}";
    }
}
