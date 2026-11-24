using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
namespace Tern.Desktop.Services;

/// <summary>Single-file downloads keep the guides and verbatim notices inside the EXE, expanded only on request.</summary>
internal static class DistributionContent
{
    private const string Resource = "Tern.DistributionContent.zip";
    internal static async Task<string> LocationAsync()
    {
        using var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource);
        if (embedded is null) return AppContext.BaseDirectory;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(embedded)); embedded.Position = 0;
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tern", "Distribution");
        var destination = Path.Combine(parent, hash);
        if (Directory.Exists(destination)) return destination;
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            using var archive = new ZipArchive(embedded, ZipArchiveMode.Read);
            if (archive.Entries.Count > 20000 || archive.Entries.Sum(e => e.Length) > 512L * 1024 * 1024) throw new IOException("Bundled documentation exceeds its size limit.");
            foreach (var entry in archive.Entries)
            {
                var path = Path.GetFullPath(Path.Combine(stage, entry.FullName));
                if (!path.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':')) throw new IOException("Invalid bundled documentation path.");
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var source = entry.Open(); using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                await source.CopyToAsync(target);
            }
            try { Directory.Move(stage, destination); }
            catch (IOException) when (Directory.Exists(destination)) { /* Another request completed the same immutable payload. */ }
            return destination;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }
}
