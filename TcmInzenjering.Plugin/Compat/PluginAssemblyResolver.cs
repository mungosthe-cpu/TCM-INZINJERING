using System.IO;
using System.Reflection;

namespace TcmInzenjering.Plugin.Compat;

/// <summary>
/// AutoCAD often does not probe the plugin folder for NuGet dependencies (e.g. System.Text.Json).
/// Resolve them from the same directory as the plugin DLL.
/// </summary>
internal static class PluginAssemblyResolver
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        _registered = true;
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        try
        {
            var requested = new AssemblyName(args.Name);
            if (string.IsNullOrWhiteSpace(requested.Name))
            {
                return null;
            }

            // Ignore satellite / resource assemblies.
            if (requested.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var pluginDir = Path.GetDirectoryName(typeof(PluginAssemblyResolver).Assembly.Location);
            if (string.IsNullOrWhiteSpace(pluginDir))
            {
                return null;
            }

            var candidate = Path.Combine(pluginDir, requested.Name + ".dll");
            if (!File.Exists(candidate))
            {
                return null;
            }

            return Assembly.LoadFrom(candidate);
        }
        catch
        {
            return null;
        }
    }
}
