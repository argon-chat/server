namespace Argon.Features.Expressions.Native;

using System.Runtime.InteropServices;

/// <summary>
/// The one <see cref="DllImportResolver"/> of this assembly: maps the logical names the bindings import
/// by to the file names distributions ship. An assembly takes a single resolver, so every binding goes
/// through this one.
/// </summary>
internal static class NativeLibraries
{
    public const string Rlottie = "argon-rlottie";
    public const string Vpx     = "argon-vpx";

    private static readonly Dictionary<string, string[]> Candidates = new(StringComparer.Ordinal)
    {
        // Debian and Ubuntu ship the soname librlottie.so.0-1; upstream and Alpine librlottie.so.0.
        [Rlottie] = ["librlottie.so.0-1", "librlottie.so.0", "librlottie.so", "rlottie.dll", "librlottie.dll", "librlottie.0.dylib", "librlottie.dylib"],
        [Vpx] =
        [
            "libvpx.so.12", "libvpx.so.11", "libvpx.so.10", "libvpx.so.9", "libvpx.so.8", "libvpx.so.7", "libvpx.so",
            "vpx.dll", "libvpx-1.dll", "libvpx.dll", "libvpx.dylib"
        ]
    };

    private static readonly ConcurrentDictionary<string, nint> Handles = new(StringComparer.Ordinal);

    private static int registered;

    public static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref registered, 1) == 0)
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);
    }

    /// <summary>The library's handle, or zero when no candidate loads.</summary>
    public static nint Load(string name)
        => Handles.GetOrAdd(name, static n =>
        {
            foreach (var candidate in Candidates[n])
                if (NativeLibrary.TryLoad(candidate, typeof(NativeLibraries).Assembly, null, out var handle))
                    return handle;
            return 0;
        });

    /// <summary>True when the library loads and exports every one of <paramref name="exports"/>.</summary>
    public static bool Exports(string name, params ReadOnlySpan<string> exports)
    {
        var handle = Load(name);
        if (handle == 0)
            return false;

        foreach (var export in exports)
            if (!NativeLibrary.TryGetExport(handle, export, out _))
                return false;

        return true;
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        => Candidates.ContainsKey(name) ? Load(name) : 0;
}
