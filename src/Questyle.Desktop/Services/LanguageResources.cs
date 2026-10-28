using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Questyle.Desktop.Services;

internal static class LanguageResources
{
    public static void Register() => AssemblyLoadContext.Default.Resolving += Resolve;

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (string.IsNullOrEmpty(name.CultureName) || name.Name == null || !name.Name.EndsWith(".resources", StringComparison.Ordinal)) return null;
        var path = Path.Combine(AppPaths.LanguageDirectory, name.CultureName.ToLowerInvariant(), name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }
}
