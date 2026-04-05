using System.Reflection;
using System.Runtime.Loader;
using KPACS.SDK;

namespace KPACS.Viewer.Plugins;

/// <summary>
/// Loads in-process .NET plugins from assemblies using isolated
/// <see cref="AssemblyLoadContext"/> instances. Each plugin gets its own
/// load context to prevent dependency conflicts between plugins and
/// between plugins and the host application.
/// </summary>
internal sealed class InProcessPluginLoader
{
    /// <summary>
    /// Load a .NET plugin assembly and create the <see cref="IPlugin"/> instance.
    /// </summary>
    /// <param name="manifest">Plugin manifest (Runtime.Type must be "dotnet").</param>
    /// <param name="pluginDirectory">Directory containing the plugin DLL.</param>
    /// <returns>An initialized <see cref="IPlugin"/> implementation.</returns>
    public static IPlugin Load(PluginManifest manifest, string pluginDirectory)
    {
        if (manifest.Runtime is null)
        {
            throw new InvalidOperationException(
                $"Plugin '{manifest.Id}' does not declare a Runtime section.");
        }

        // Resolve the assembly path.
        // For "dotnet" runtime, the Command is the assembly name (without .dll),
        // or a relative path to the DLL.
        string assemblyPath = ResolveDllPath(manifest.Runtime.Command, pluginDirectory);
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException(
                $"Plugin assembly not found: {assemblyPath}", assemblyPath);
        }

        // Create an isolated load context.
        var loadContext = new PluginAssemblyLoadContext(manifest.Id, assemblyPath);
        Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

        // Find the IPlugin implementation in the loaded assembly.
        Type? pluginType = FindPluginType(assembly);
        if (pluginType is null)
        {
            throw new InvalidOperationException(
                $"Assembly '{assemblyPath}' does not contain a public class implementing IPlugin.");
        }

        // Create instance.
        object? instance = Activator.CreateInstance(pluginType);
        if (instance is not IPlugin plugin)
        {
            throw new InvalidOperationException(
                $"Failed to create IPlugin instance from type '{pluginType.FullName}'.");
        }

        return plugin;
    }

    /// <summary>
    /// Unload a previously loaded plugin's <see cref="AssemblyLoadContext"/>.
    /// The caller must have already called <see cref="IPlugin.ShutdownAsync"/>
    /// and released all references to plugin types before calling this.
    /// </summary>
    public static void TryUnload(IPlugin plugin)
    {
        Type pluginType = plugin.GetType();
        if (AssemblyLoadContext.GetLoadContext(pluginType.Assembly) is PluginAssemblyLoadContext ctx)
        {
            ctx.Unload();
        }
    }

    private static string ResolveDllPath(string command, string pluginDirectory)
    {
        // If the command is already an absolute path, use it directly.
        if (Path.IsPathRooted(command))
        {
            return command.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? command : command + ".dll";
        }

        // Relative path: resolve against the plugin directory.
        string candidate = Path.Combine(pluginDirectory, command);
        if (!candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            candidate += ".dll";
        }

        return Path.GetFullPath(candidate);
    }

    private static Type? FindPluginType(Assembly assembly)
    {
        Type iPluginType = typeof(IPlugin);
        return assembly.GetExportedTypes()
            .FirstOrDefault(t =>
                t.IsClass &&
                !t.IsAbstract &&
                iPluginType.IsAssignableFrom(t));
    }

    /// <summary>
    /// Isolated load context for a single plugin.
    /// Resolves dependencies from the plugin's own directory first,
    /// then falls back to the default context.
    /// </summary>
    private sealed class PluginAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginAssemblyLoadContext(string name, string pluginAssemblyPath)
            : base(name, isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Try to resolve from the plugin's dependency tree first.
            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is not null)
            {
                return LoadFromAssemblyPath(path);
            }

            // Fall back to default context (host assemblies, including KPACS.SDK).
            return null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path is not null)
            {
                return LoadUnmanagedDllFromPath(path);
            }

            return nint.Zero;
        }
    }
}
