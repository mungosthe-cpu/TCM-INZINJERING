using System.Diagnostics;
using System.IO;
using System.Text;

namespace TcmInzenjering.Plugin;

/// <summary>
/// Pokreće PowerShell GUI skriptu bez crnog konzolnog prozora.
/// Koristi wscript.exe (WindowStyle 0) — pouzdanije od CreateNoWindow na Win10/11.
/// </summary>
internal static class HiddenPowerShell
{
    public static Process? StartFile(string scriptPath)
    {
        scriptPath = Path.GetFullPath(scriptPath);
        var vbsPath = Path.Combine(
            Path.GetTempPath(),
            "TcmInzenjering-launch-" + Guid.NewGuid().ToString("N")[..10] + ".vbs");

        // VBS: unutrašnji navodnici se udvostručavaju "".
        // Primer: sh.Run "powershell.exe ... -File ""C:\temp\a.ps1""", 0, False
        var psInner =
            "powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" +
            scriptPath + "\"";
        var psForVbs = psInner.Replace("\"", "\"\"");

        var vbs = new StringBuilder();
        vbs.AppendLine("Set sh = CreateObject(\"WScript.Shell\")");
        vbs.AppendLine("sh.Run \"" + psForVbs + "\", 0, False");
        vbs.AppendLine("WScript.Sleep 2500");
        vbs.AppendLine("On Error Resume Next");
        vbs.AppendLine("CreateObject(\"Scripting.FileSystemObject\").DeleteFile WScript.ScriptFullName, True");
        File.WriteAllText(vbsPath, vbs.ToString(), Encoding.ASCII);

        var start = new ProcessStartInfo
        {
            FileName = "wscript.exe",
            Arguments = "//B //Nologo \"" + vbsPath + "\"",
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
