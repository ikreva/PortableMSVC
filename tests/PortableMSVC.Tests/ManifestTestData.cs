namespace PortableMSVC.Tests;

internal static class ManifestTestData
{
    private static readonly object SyncRoot = new object();

    private static string? s_manifestDirectory;

    public static string ManifestDirectory
    {
        get
        {
            lock (SyncRoot)
            {
                if (s_manifestDirectory != null)
                {
                    return s_manifestDirectory;
                }

                s_manifestDirectory = ResolveManifestDirectory();
                EnsureManifestsCached(s_manifestDirectory);
                return s_manifestDirectory;
            }
        }
    }

    public static PackageIndex Load(string vs)
    {
        return new ManifestLoader(ManifestDirectory).LoadVsManifest(vs);
    }

    private static string ResolveManifestDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var existing = Path.Combine(directory.FullName, "Cache", "manifests");
            if (Directory.Exists(existing))
            {
                return existing;
            }

            if (File.Exists(Path.Combine(directory.FullName, "PortableMSVC.csproj")))
            {
                var created = Path.Combine(directory.FullName, "Cache", "manifests");
                Directory.CreateDirectory(created);
                return created;
            }

            directory = directory.Parent;
        }

        var fallback = Path.Combine(AppContext.BaseDirectory, "Cache", "manifests");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static void EnsureManifestsCached(string manifestDirectory)
    {
        ManifestCache cache = new ManifestCache(manifestDirectory);
        foreach (string alias in ManifestLoader.KnownVsAliases)
        {
            string path = Path.Combine(manifestDirectory, alias + ".vsman.json");
            if (File.Exists(path))
            {
                continue;
            }

            cache.EnsureAsync(alias, forceRefresh: false, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
