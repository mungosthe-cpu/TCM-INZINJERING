using System.Reflection;

namespace TcmInzenjering.Setup;

internal static class Program
{
    private const string BundleFolderName = "TcmInzenjering.bundle";
    private const string BricsBundleFolderName = "TcmInzenjering.BricsCAD.bundle";
    private const string AppName = "TcmInzenjering";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("========================================");
        Console.WriteLine(" TCM-INZINJERING - instalacija");
        Console.WriteLine($" Verzija: {GetInstallerVersion()}");
        Console.WriteLine("========================================");
        Console.WriteLine();

        try
        {
            var sourceRoot = ResolvePayloadRoot();
            var autocadBundle = Path.Combine(sourceRoot, BundleFolderName);
            var bricsBundle = Path.Combine(sourceRoot, BricsBundleFolderName);

            if (!Directory.Exists(autocadBundle))
            {
                throw new DirectoryNotFoundException($"Nije pronadjen paket: {autocadBundle}");
            }

            var targets = HostDetector.DetectTargets();
            if (targets.Count == 0)
            {
                Console.WriteLine("Upozorenje: nije pronadjen AutoCAD (2020+) ni BricsCAD (2022+).");
                Console.WriteLine("Instalacija ce kopirati bundle u standardne ApplicationPlugins foldere.");
            }
            else
            {
                Console.WriteLine("Pronadjeni CAD hostovi:");
                foreach (var target in targets)
                {
                    Console.WriteLine($"  - {target.DisplayName} ({target.Kind}, {target.Series})");
                }
            }

            Console.WriteLine();
            InstallBundle(autocadBundle, GetAutocadInstallTargets());
            if (Directory.Exists(bricsBundle))
            {
                InstallBundle(bricsBundle, GetBricsInstallTargets());
            }
            else
            {
                InstallBundle(autocadBundle, GetBricsInstallTargets());
            }

            RegisterAutocadProfiles(targets.Where(t => t.Kind == CadHostKind.AutoCAD));
            Console.WriteLine();
            Console.WriteLine("Instalacija zavrsena.");
            Console.WriteLine("Restartujte AutoCAD/BricsCAD. Komanda za proveru nadogradnje: TCMUPDATE");
            PauseIfInteractive(args);
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Greska: {ex.Message}");
            Console.ResetColor();
            PauseIfInteractive(args);
            return 1;
        }
    }

    private static void InstallBundle(string bundleSource, IEnumerable<string> targets)
    {
        foreach (var targetRoot in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var parent = Path.GetDirectoryName(targetRoot);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                if (Directory.Exists(targetRoot))
                {
                    Directory.Delete(targetRoot, recursive: true);
                }

                CopyDirectory(bundleSource, targetRoot);
                Console.WriteLine($"Kopirano u: {targetRoot}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Preskoceno: {targetRoot} ({ex.Message})");
            }
        }
    }

    private static void RegisterAutocadProfiles(IEnumerable<CadHostTarget> autocadTargets)
    {
        foreach (var target in autocadTargets)
        {
            var dllPath = Path.Combine(
                GetAutocadInstallTargets().First(),
                "Contents",
                target.UseModernRuntime ? "net8" : "net48",
                target.UseModernRuntime ? "TcmInzenjering.Plugin.dll" : "TcmInzenjering.Plugin.Legacy.dll");

            if (!File.Exists(dllPath))
            {
                continue;
            }

            try
            {
                RegistryInstaller.RegisterAutocadApplication(target.Series, target.ProductCode, dllPath);
                Console.WriteLine($"Registry: {target.Series}\\{target.ProductCode}\\Applications\\{AppName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Registry preskocen ({target.ProductCode}): {ex.Message}");
            }
        }
    }

    private static IEnumerable<string> GetAutocadInstallTargets()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "ApplicationPlugins", BundleFolderName);

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Autodesk", "ApplicationPlugins", BundleFolderName);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "Autodesk", "ApplicationPlugins", BundleFolderName);
    }

    private static IEnumerable<string> GetBricsInstallTargets()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Bricsys", "ApplicationPlugins", BricsBundleFolderName);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "Bricsys", "ApplicationPlugins", BricsBundleFolderName);
    }

    private static string ResolvePayloadRoot()
    {
        var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidates = new[]
        {
            Path.Combine(exeDir, "payload"),
            Path.Combine(exeDir, "..", "payload"),
            Path.Combine(exeDir, ".."),
            Directory.GetCurrentDirectory()
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (Directory.Exists(Path.Combine(full, BundleFolderName)))
            {
                return full;
            }
        }

        throw new DirectoryNotFoundException("Nije pronadjen folder sa bundle paketom (payload/TcmInzenjering.bundle).");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(source, destination, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string GetInstallerVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.0";
    }

    private static void PauseIfInteractive(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Pritisnite Enter za izlaz...");
        Console.ReadLine();
    }
}
