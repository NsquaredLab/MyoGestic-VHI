#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Vhi;

// Workaround for godotengine/godot#112701 and #75095: Godot's managed host does
// not follow standard .NET shared-framework probing rules, so assemblies from
// the Microsoft.AspNetCore.App shared framework (Kestrel, Microsoft.Extensions.*,
// etc. — pulled in by Grpc.AspNetCore) fail to load with FileNotFoundException
// even when the framework is installed.
//
// This registers an AssemblyLoadContext.Resolving handler that probes the
// on-disk .NET shared-framework directories. It runs from a [ModuleInitializer]
// so the resolver is in place before Godot JITs any code that references those
// assemblies. This file must keep NO static references to ASP.NET Core / gRPC
// types, or the JIT would try to load them before the resolver is registered.
//
// Cross-platform adaptation of jasonswearingen's SharedFrameworkAssemblyLoader.cs
// attached to godot#112701.
internal static class SharedFrameworkAssemblyLoader
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] ProbeDirectories = BuildProbeDirectories();

    [ModuleInitializer]
    public static void InjectGodotAssemblyResolverFix()
    {
        // Console.WriteLine (not GD.Print) — Godot's managed API may not be
        // initialised yet when the module initializer runs.
        Console.WriteLine(
            $"[SharedFrameworkAssemblyLoader] registered; {ProbeDirectories.Length} shared-framework probe dir(s)");

        ResolveEventHandler();

        // Also hook the load context the project assembly actually lives in, in
        // case Godot loaded it into a context other than Default.
        var projectContext = AssemblyLoadContext.GetLoadContext(typeof(SharedFrameworkAssemblyLoader).Assembly);
        if (projectContext is not null && !ReferenceEquals(projectContext, AssemblyLoadContext.Default))
            projectContext.Resolving += Resolve;

        static void ResolveEventHandler() => AssemblyLoadContext.Default.Resolving += Resolve;
    }

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        var simpleName = assemblyName.Name;
        if (string.IsNullOrEmpty(simpleName))
            return null;

        var fileName = simpleName + ".dll";

        // Prefer an assembly sitting next to the project output.
        if (TryLoad(Path.Combine(AppContext.BaseDirectory, fileName), out var local))
            return local;

        return TryLoadFromSharedFrameworks(simpleName, fileName);
    }

    private static bool TryLoad(string fullPath, out Assembly? assembly)
    {
        assembly = null;
        if (!File.Exists(fullPath))
            return false;
        try
        {
            assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
            return true;
        }
        catch (FileLoadException)
        {
            return false;
        }
    }

    private static Assembly? TryLoadFromSharedFrameworks(string simpleName, string fileName)
    {
        if (Cache.TryGetValue(simpleName, out var cached))
            return cached is not null && TryLoad(cached, out var a) ? a : null;

        string? located = null;
        foreach (var dir in ProbeDirectories)
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
            {
                located = candidate;
                break;
            }
        }

        Cache[simpleName] = located;
        return located is not null && TryLoad(located, out var asm) ? asm : null;
    }

    // Every version directory of every shared framework under every shared root,
    // newest version first.
    private static string[] BuildProbeDirectories()
    {
        string[] frameworks =
        {
            "Microsoft.AspNetCore.App",
            "Microsoft.NETCore.App",
            "Microsoft.WindowsDesktop.App",
        };

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sharedRoot in EnumerateSharedRoots())
        {
            foreach (var fw in frameworks)
            {
                var fwRoot = Path.Combine(sharedRoot, fw);
                if (!Directory.Exists(fwRoot))
                    continue;
                try
                {
                    var versions = Directory.GetDirectories(fwRoot);
                    Array.Sort(versions, StringComparer.OrdinalIgnoreCase);
                    Array.Reverse(versions);
                    foreach (var v in versions)
                        if (seen.Add(v))
                            result.Add(v);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        return result.ToArray();
    }

    // Cross-platform discovery of the dotnet "shared" root(s).
    private static IEnumerable<string> EnumerateSharedRoots()
    {
        var roots = new List<string>();

        void Add(string? candidate)
        {
            if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate) &&
                !roots.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                roots.Add(candidate);
        }

        // Most reliable: derive from the currently-running base runtime, e.g.
        // <dotnet>/shared/Microsoft.NETCore.App/<ver>/ -> <dotnet>/shared
        try
        {
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            var sharedRoot = Directory.GetParent(runtimeDir.TrimEnd(Path.DirectorySeparatorChar))?.Parent?.FullName;
            Add(sharedRoot);
        }
        catch
        {
            // ignore — fall back to the well-known locations below
        }

        Add(Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT"), "shared"));
        Add(Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)"), "shared"));

        if (OperatingSystem.IsWindows())
        {
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared"));
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "shared"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            Add("/usr/local/share/dotnet/shared");
            Add("/opt/homebrew/share/dotnet/shared");
        }
        else
        {
            Add("/usr/share/dotnet/shared");
            Add("/usr/lib/dotnet/shared");
        }

        return roots;
    }

    private static string? Combine(string? root, string sub) =>
        string.IsNullOrEmpty(root) ? null : Path.Combine(root, sub);
}
