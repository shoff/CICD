using System.Reflection;
using System.Runtime.Loader;

namespace Cicd.Core.Plugins;

/// <summary>
/// Loads a plugin and its private dependencies in isolation. Assemblies that the host already has loaded
/// (the SDK, Contracts, Microsoft.Extensions.*) are shared so that interface types unify across the boundary.
/// </summary>
internal sealed class PluginLoadContext(string mainAssemblyPath) : AssemblyLoadContext(name: Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: false)
{
    private readonly AssemblyDependencyResolver resolver = new(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var shared = Default.Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        if (shared is not null)
        {
            return shared;
        }

        var path = resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }
}
