using System.Reflection;
using System.Windows.Forms;

namespace TcmInzenjering.Setup;

internal static class Program
{
    private const string BundleFolderName = "TcmInzenjering.bundle";
    private const string BricsBundleFolderName = "TcmInzenjering.BricsCAD.bundle";
    private const string AppName = "TcmInzenjering";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var silent = args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));

        using var form = new InstallerForm();
        if (!silent)
        {
            form.Show();
            Application.DoEvents();
        }

        try
        {
            form.SetStatus($"TCM-INŽINJERING v{GetInstallerVersion()} — priprema…");
            form.SetProgress(5);

            var sourceRoot = ResolvePayloadRoot();
            var autocadBundle = Path.Combine(sourceRoot, BundleFolderName);
            var bricsBundle = Path.Combine(sourceRoot, BricsBundleFolderName);

            if (!Directory.Exists(autocadBundle))
            {
                throw new DirectoryNotFoundException($"Nije pronađen paket: {autocadBundle}");
            }

            var targets = HostDetector.DetectTargets();
            form.SetStatus(
                targets.Count == 0
                    ? "CAD host nije detektovan — biće korišćeni standardni ApplicationPlugins folderi."
                    : $"Pronađeno {targets.Count} CAD hostova. Kopiranje…");
            form.SetProgress(20);

            InstallBundle(autocadBundle, GetAutocadInstallTargets(), form, 20, 55);
            form.SetProgress(55);

            if (Directory.Exists(bricsBundle))
            {
                InstallBundle(bricsBundle, GetBricsInstallTargets(), form, 55, 75);
            }
            else
            {
                InstallBundle(autocadBundle, GetBricsInstallTargets(), form, 55, 75);
            }

            form.SetStatus("Registrovanje u AutoCAD / BricsCAD profilima…");
            form.SetProgress(80);
            RegisterAutocadProfiles(targets.Where(t => t.Kind == CadHostKind.AutoCAD));
            RegisterBricsCadProfiles(targets.Where(t => t.Kind == CadHostKind.BricsCAD));
            form.SetProgress(100);

            var okMsg =
                "Instalacija završena. Restartujte AutoCAD/BricsCAD.\n" +
                "Komanda za proveru: TCMUPDATE";

            if (silent)
            {
                return 0;
            }

            form.Complete(true, okMsg);
            Application.Run(form);
            return 0;
        }
        catch (Exception ex)
        {
            if (silent)
            {
                return 1;
            }

            form.Complete(false, $"Greška: {ex.Message}");
            Application.Run(form);
            return 1;
        }
    }

    private static void InstallBundle(
        string bundleSource,
        IEnumerable<string> targets,
        InstallerForm form,
        int progressFrom,
        int progressTo)
    {
        var list = targets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var targetRoot = list[i];
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
                form.SetStatus($"Kopirano: {targetRoot}");
            }
            catch (Exception ex)
            {
                form.SetStatus($"Preskočeno: {targetRoot} ({ex.Message})");
            }

            if (list.Count > 0)
            {
                var t = (i + 1) / (double)list.Count;
                form.SetProgress(progressFrom + (int)((progressTo - progressFrom) * t));
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
            }
            catch
            {
                // Preskoči individualni profil.
            }
        }
    }

    private static void RegisterBricsCadProfiles(IEnumerable<CadHostTarget> bricsTargets)
    {
        var bundleRoot = GetBricsInstallTargets().First();
        var dllPath = Path.Combine(bundleRoot, "Contents", "net48", "TcmInzenjering.Plugin.Legacy.dll");
        if (!File.Exists(dllPath))
        {
            return;
        }

        foreach (var target in bricsTargets)
        {
            try
            {
                RegistryInstaller.RegisterBricsCadApplication(target.Series, target.ProductCode, dllPath);
            }
            catch
            {
                // Preskoči.
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
            exeDir,
            Path.Combine(exeDir, "..", "payload"),
            Path.Combine(exeDir, ".."),
            Directory.GetCurrentDirectory(),
            Path.Combine(Directory.GetCurrentDirectory(), "payload")
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (Directory.Exists(Path.Combine(full, BundleFolderName)))
            {
                return full;
            }
        }

        throw new DirectoryNotFoundException("Nije pronađen folder sa bundle paketom (payload/TcmInzenjering.bundle).");
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

    private static string GetInstallerVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.0";
}
