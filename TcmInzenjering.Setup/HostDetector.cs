using Microsoft.Win32;

namespace TcmInzenjering.Setup;

internal enum CadHostKind
{
    AutoCAD,
    BricsCAD
}

internal sealed class CadHostTarget
{
    public CadHostKind Kind { get; init; }
    public string Series { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool UseModernRuntime { get; init; }
}

internal static class HostDetector
{
    private static readonly (string Series, int Year, bool Modern)[] AutoCadSeries =
    {
        ("R23.1", 2020, false),
        ("R24.0", 2021, false),
        ("R24.1", 2022, false),
        ("R24.2", 2023, false),
        ("R24.3", 2024, false),
        ("R25.0", 2025, true),
        ("R25.1", 2026, true)
    };

    public static List<CadHostTarget> DetectTargets()
    {
        var targets = new List<CadHostTarget>();
        targets.AddRange(DetectAutoCad());
        targets.AddRange(DetectBricsCad());
        return targets;
    }

    private static IEnumerable<CadHostTarget> DetectAutoCad()
    {
        var found = new List<CadHostTarget>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Autodesk\AutoCAD");
        if (root is null)
        {
            return found;
        }

        foreach (var seriesInfo in AutoCadSeries)
        {
            using var seriesKey = root.OpenSubKey(seriesInfo.Series);
            if (seriesKey is null)
            {
                continue;
            }

            foreach (var productCode in seriesKey.GetSubKeyNames())
            {
                if (!productCode.StartsWith("ACAD", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found.Add(new CadHostTarget
                {
                    Kind = CadHostKind.AutoCAD,
                    Series = seriesInfo.Series,
                    ProductCode = productCode,
                    DisplayName = $"AutoCAD {seriesInfo.Year}",
                    UseModernRuntime = seriesInfo.Modern
                });
            }
        }

        return found;
    }

    private static IEnumerable<CadHostTarget> DetectBricsCad()
    {
        var found = new List<CadHostTarget>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Bricsys\BricsCAD");
        if (root is null)
        {
            return found;
        }

        foreach (var versionName in root.GetSubKeyNames())
        {
            if (!versionName.StartsWith("V", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryParseBricsMajor(versionName, out var major) || major < 22)
            {
                continue;
            }

            foreach (var locale in root.OpenSubKey(versionName)?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                found.Add(new CadHostTarget
                {
                    Kind = CadHostKind.BricsCAD,
                    Series = versionName,
                    ProductCode = locale,
                    DisplayName = $"BricsCAD {versionName} ({locale})",
                    UseModernRuntime = false
                });
            }
        }

        return found;
    }

    private static bool TryParseBricsMajor(string versionName, out int major)
    {
        major = 0;
        var digits = new string(versionName.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out major);
    }
}

internal static class RegistryInstaller
{
  private const string AppName = "TcmInzenjering";
  private const string Description = "TCM-INZINJERING";

  public static void RegisterAutocadApplication(string series, string productCode, string dllPath)
  {
    var keyPath = $@"Software\Autodesk\AutoCAD\{series}\{productCode}\Applications\{AppName}";
    using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
    if (key is null)
    {
      throw new InvalidOperationException($"Ne mogu da otvorim registry kljuc: {keyPath}");
    }

    key.SetValue("DESCRIPTION", Description, RegistryValueKind.String);
    key.SetValue("LOADCTRLS", 2, RegistryValueKind.DWord);
    key.SetValue("LOADER", dllPath, RegistryValueKind.String);
    key.SetValue("MANAGED", 1, RegistryValueKind.DWord);
  }

  public static void RegisterBricsCadApplication(string series, string locale, string dllPath)
  {
    var keyPath = $@"Software\Bricsys\BricsCAD\{series}\{locale}\Applications\{AppName}";
    using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
    if (key is null)
    {
      throw new InvalidOperationException($"Ne mogu da otvorim registry kljuc: {keyPath}");
    }

    key.SetValue("DESCRIPTION", Description, RegistryValueKind.String);
    key.SetValue("LOADCTRLS", 2, RegistryValueKind.DWord);
    key.SetValue("LOADER", dllPath, RegistryValueKind.String);
    key.SetValue("MANAGED", 1, RegistryValueKind.DWord);
  }
}
