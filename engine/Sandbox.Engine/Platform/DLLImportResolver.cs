using System.Reflection;
using System.Runtime.InteropServices;
using System.IO;

namespace Sandbox;

internal static class DLLImportResolver
{
    private static readonly HashSet<string> registeredAssemblies = new();

    internal static void SetupResolvers()
    {
        // Register for all currently loaded assemblies
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            TryRegister(assembly);

        // Register for any assemblies loaded in the future
        AppDomain.CurrentDomain.AssemblyLoad += (_, args) => TryRegister(args.LoadedAssembly);
    }

    private static void TryRegister(Assembly assembly)
    {
        if (assembly.IsDynamic)
            return;

        // SetDllImportResolver throws if called twice for the same assembly
        if (!registeredAssemblies.Add(assembly.FullName))
            return;

        try
        {
            NativeLibrary.SetDllImportResolver(assembly, ResolveFromNativePath);
        }
        catch (InvalidOperationException)
        {
            // A resolver was already registered for this assembly (e.g. after hotload).
            // Safe to ignore — the existing resolver is still active.
        }
    }

    private static IntPtr ResolveFromNativePath(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Remap steam_api64 → libsteam_api on non-Windows
        if (libraryName == "steam_api64" && !OperatingSystem.IsWindows())
        {
            libraryName = "libsteam_api64";
        }

        var nativeName = true switch
        {
            _ when OperatingSystem.IsWindows() => $"{libraryName}.dll",
            _ when OperatingSystem.IsMacOS() => $"{libraryName}.dylib",
            _ when OperatingSystem.IsLinux() => $"{libraryName}.so",
            _ => libraryName
        };

        // Resolve NativeDllPath to absolute at call time — it may be relative ("bin/linuxsteamrt64/")
        // and Environment.CurrentDirectory can change during bootstrap (InitMinimal resets it).
        var nativeDllDir = NetCore.NativeDllPath;
        if (!Path.IsPathRooted(nativeDllDir))
        {
            nativeDllDir = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, nativeDllDir));
        }

        if (NativeLibrary.TryLoad(Path.Combine(nativeDllDir, nativeName), out var handle))
        {
            return handle;
        }

        // Second try: bare name — lets OS loader use LD_LIBRARY_PATH / PATH
        if (NativeLibrary.TryLoad(nativeName, out handle))
        {
            return handle;
        }

        return IntPtr.Zero;
    }
}
