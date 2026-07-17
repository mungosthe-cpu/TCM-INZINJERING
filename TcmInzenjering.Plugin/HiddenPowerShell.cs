using System.Diagnostics;

namespace TcmInzenjering.Plugin;

/// <summary>
/// Pokreće PowerShell GUI skriptu bez crnog konzolnog prozora
/// (kao Chrome/VS Code updater: CREATE_NO_WINDOW + WinForms UI).
/// </summary>
internal static class HiddenPowerShell
{
    public static Process? StartFile(string scriptPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments =
                "-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" +
                scriptPath + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        return Process.Start(start);
    }

    /// <summary>Ubacuje se na početak PS skripti — defense-in-depth ako konzola ipak postoji.</summary>
    public const string HideConsoleSnippet = """
function Hide-HostConsole {
  try {
    if (-not ("Native.Win32Console" -as [type])) {
      Add-Type -Namespace Native -Name Win32Console -MemberDefinition @"
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern System.IntPtr GetConsoleWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
"@
    }
    $hwnd = [Native.Win32Console]::GetConsoleWindow()
    if ($hwnd -ne [System.IntPtr]::Zero) {
      [void][Native.Win32Console]::ShowWindow($hwnd, 0)
    }
  } catch { }
}
Hide-HostConsole
""";
}
