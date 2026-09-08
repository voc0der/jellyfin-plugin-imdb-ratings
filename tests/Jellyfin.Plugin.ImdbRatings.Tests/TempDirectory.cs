namespace Jellyfin.Plugin.ImdbRatings.Tests;

/// <summary>
/// A throwaway directory for tests that touch the filesystem, removed on dispose.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    private readonly string _path;

    public TempDirectory()
    {
        _path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "imdb-ratings-cache-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
    }

    /// <summary>
    /// Gets the directory itself, for callers that need a data path rather than a file inside it.
    /// </summary>
    public string Path => _path;

    public string PathFor(string fileName) => System.IO.Path.Combine(_path, fileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for test temp directories.
        }
    }
}
