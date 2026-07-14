using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
#if NET8_0_OR_GREATER
using System.Windows;
#elif !BRICSCAD
using System.Windows;
#endif
using TcmInzenjering.Plugin.Update;

namespace TcmInzenjering.Plugin;

/// <summary>
/// Preuzima setup EXE i pokrece spoljasnji skript koji ceka zatvaranje CAD-a pa instalira.
/// DLL ostaje zakljucan dok je AutoCAD otvoren, zato instalacija mora da krene van procesa.
/// </summary>
internal static class PluginUpdater
{
    private static readonly HttpClient DownloadClient = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TcmInzenjering-Plugin-Updater");
        return client;
    }

    public static bool TryStartUpdate(UpdateCheckResult result, out string message)
    {
        message = string.Empty;
        if (!result.CheckSucceeded || !result.UpdateAvailable)
        {
            message = "Nema dostupne nadogradnje.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(result.DownloadUrl) ||
            !result.DownloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            message = "Nije pronadjen EXE installer u manifestu. Otvorite GitHub Releases rucno.";
            return false;
        }

#if !BRICSCAD
        var answer = MessageBox.Show(
            $"Dostupna je nova verzija {result.LatestVersion} (trenutna {result.CurrentVersion})." +
            Environment.NewLine + Environment.NewLine +
            "Plugin ce preuzeti installer, a zatim ce sacekati da zatvorite AutoCAD/BricsCAD " +
            "pre instalacije (DLL je zakljucan dok je program otvoren)." +
            Environment.NewLine + Environment.NewLine +
            "Nastaviti?",
            "TCM-INZINJERING - Nadogradnja",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
        {
            message = "Nadogradnja otkazana.";
            return false;
        }
#else
        // BricsCAD / legacy CLI: potvrda preko UpdateCommands.
#endif

        try
        {
            var version = result.LatestVersion ?? "latest";
            var setupPath = Path.Combine(Path.GetTempPath(), $"TCM-INZINJERING-Setup-{version}.exe");
            message = "Preuzimanje instalera...";
            DownloadFile(result.DownloadUrl!, setupPath);

            var scriptPath = Path.Combine(Path.GetTempPath(), "TcmInzenjering-update.ps1");
            File.WriteAllText(scriptPath, BuildUpdateScript(setupPath, version), Encoding.UTF8);

            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            Process.Start(start);

            message =
                "Installer je preuzet. Zatvorite AutoCAD/BricsCAD — instalacija ce se pokrenuti " +
                "automatski cim se program zatvori.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Nije moguce pokrenuti nadogradnju: {ex.Message}";
            return false;
        }
    }

    private static void DownloadFile(string url, string destinationPath)
    {
        using var response = DownloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            .GetAwaiter()
            .GetResult();
        response.EnsureSuccessStatusCode();

        using var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var output = File.Create(destinationPath);
        input.CopyTo(output);
    }

    private static string BuildUpdateScript(string setupPath, string version)
    {
        var escapedSetup = setupPath.Replace("'", "''");
        return $$"""
Add-Type -AssemblyName System.Windows.Forms | Out-Null
$ErrorActionPreference = "Continue"
$title = "TCM-INZINJERING - Nadogradnja"
$setup = '{{escapedSetup}}'
$version = '{{version.Replace("'", "''")}}'
$nl = [Environment]::NewLine

function Show-Msg([string]$msg, [string]$icon = "Info") {
  $boxIcon = switch ($icon) {
    "Warning" { [System.Windows.Forms.MessageBoxIcon]::Warning }
    "Error"   { [System.Windows.Forms.MessageBoxIcon]::Error }
    default   { [System.Windows.Forms.MessageBoxIcon]::Information }
  }
  [System.Windows.Forms.MessageBox]::Show($msg, $title, [System.Windows.Forms.MessageBoxButtons]::OK, $boxIcon) | Out-Null
}

function Get-CadRunning {
  @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match '^(acad|bricscad)$' })
}

if (-not (Test-Path -LiteralPath $setup)) {
  Show-Msg ("Nije pronadjen preuzeti installer:" + $nl + $setup) "Error"
  exit 1
}

[System.Windows.Forms.MessageBox]::Show(
  ("Installer v$version je preuzet." + $nl + $nl +
   "Sacuvajte crteze i zatvorite AutoCAD/BricsCAD, zatim kliknite OK." + $nl + $nl +
   "Instalacija ne moze da zameni DLL dok je program otvoren."),
  $title,
  [System.Windows.Forms.MessageBoxButtons]::OK,
  [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null

while ($true) {
  $running = Get-CadRunning
  if ($running.Count -eq 0) { break }
  $summary = ($running | Group-Object ProcessName | ForEach-Object { "$($_.Name).exe ($($_.Count))" }) -join ", "
  $retry = [System.Windows.Forms.MessageBox]::Show(
    ("Jos uvek je pokrenuto: $summary" + $nl + $nl + "Zatvorite CAD pa izaberite Retry."),
    $title,
    [System.Windows.Forms.MessageBoxButtons]::RetryCancel,
    [System.Windows.Forms.MessageBoxIcon]::Warning)
  if ($retry -eq [System.Windows.Forms.DialogResult]::Cancel) {
    Show-Msg "Nadogradnja otkazana." "Warning"
    exit 1
  }
  Start-Sleep -Milliseconds 800
}

Start-Sleep -Seconds 2

try {
  $p = Start-Process -FilePath $setup -ArgumentList "--silent" -PassThru -Wait
  if ($p.ExitCode -ne 0) {
    Show-Msg ("Instalacija nije uspela (exit $($p.ExitCode))." + $nl + "Pokrenite installer rucno:" + $nl + $setup) "Error"
    exit $p.ExitCode
  }
  Show-Msg ("Nadogradnja na v$version je zavrsena." + $nl + $nl + "Pokrenite AutoCAD/BricsCAD ponovo.") "Info"
} catch {
  Show-Msg ("Greska pri pokretanju instalera:" + $nl + $_.Exception.Message + $nl + $nl + $setup) "Error"
  exit 1
}

try { Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue } catch { }
""";
    }
}
