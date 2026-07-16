using System.IO;
using System.Windows;
using Microsoft.Win32;
using TcmInzenjering.Plugin.Roads;

namespace TcmInzenjering.Plugin.Dialogs;

public partial class ProjectFolderDialog : Window
{
    public string SelectedFolder { get; private set; } = string.Empty;

    public ProjectFolderDialog(string? initialFolder = null, Window? owner = null)
    {
        InitializeComponent();
        if (owner is not null)
        {
            Owner = owner;
        }

        var start = string.IsNullOrWhiteSpace(initialFolder)
            ? ProjectFolderPreferences.FolderPath
            : initialFolder.Trim();
        PathBox.Text = start;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
#if NETFRAMEWORK
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Izaberite folder projekta",
            ShowNewFolderButton = true
        };

        var current = PathBox.Text.Trim();
        if (Directory.Exists(current))
        {
            dlg.SelectedPath = current;
        }

        var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (dlg.ShowDialog(owner == IntPtr.Zero
                ? null
                : new Win32Window(owner)) != System.Windows.Forms.DialogResult.OK ||
            string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            return;
        }

        PathBox.Text = dlg.SelectedPath;
#else
        var dlg = new OpenFolderDialog
        {
            Title = "Izaberite folder projekta",
            Multiselect = false
        };

        var current = PathBox.Text.Trim();
        if (Directory.Exists(current))
        {
            dlg.InitialDirectory = current;
        }

        if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            PathBox.Text = dlg.FolderName;
        }
#endif
    }

#if NETFRAMEWORK
    private sealed class Win32Window : System.Windows.Forms.IWin32Window
    {
        public Win32Window(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }
#endif


    private void OnCreateFolder(object sender, RoutedEventArgs e)
    {
        var parent = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(parent))
        {
            parent = ProjectFolderPreferences.FolderPath;
        }

        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Ne mogu da pristupim folderu:\n{ex.Message}", "TCM-INŽINJERING",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var name = PromptNewFolderName();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            if (name.Contains(c))
            {
                MessageBox.Show(this, "Ime foldera sadrzi nedozvoljene karaktere.", "TCM-INŽINJERING",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var created = Path.Combine(parent, name.Trim());
        try
        {
            Directory.CreateDirectory(created);
            PathBox.Text = created;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Ne mogu da napravim folder:\n{ex.Message}", "TCM-INŽINJERING",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string? PromptNewFolderName()
    {
        var input = new Window
        {
            Title = "Napravi nov folder",
            Width = 360,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };

        var box = new System.Windows.Controls.TextBox
        {
            Margin = new Thickness(12, 12, 12, 0),
            Height = 28,
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = "Novi projekat"
        };
        box.SelectAll();

        string? accepted = null;
        var ok = new System.Windows.Controls.Button
        {
            Content = "OK",
            Width = 88,
            Height = 26,
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        ok.Click += (_, _) =>
        {
            accepted = box.Text;
            input.DialogResult = true;
        };
        var cancel = new System.Windows.Controls.Button
        {
            Content = "Odustani",
            Width = 88,
            Height = 26,
            IsCancel = true
        };

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 12, 12, 12)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new System.Windows.Controls.DockPanel { LastChildFill = true };
        System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(box);
        input.Content = root;

        return input.ShowDialog() == true ? accepted?.Trim() : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "Unesite putanju foldera.", "TCM-INŽINJERING",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Ne mogu da koristim folder:\n{ex.Message}", "TCM-INŽINJERING",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedFolder = path;
        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        try
        {
            DialogResult = false;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }
}
