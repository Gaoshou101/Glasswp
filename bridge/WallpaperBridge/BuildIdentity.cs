using System.Reflection;
namespace WallpaperBridge;

public static class BuildIdentity
{
    // Compiled into the assembly: replacing files cannot change an old process's identity.
    public static string Id { get; } = typeof(BuildIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "WallpaperBuildId")?.Value ?? "development";
}
