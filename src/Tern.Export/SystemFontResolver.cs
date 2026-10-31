using PdfSharp.Fonts;

namespace Tern.Export;

/// <summary>Resolves the two font families used by <see cref="PdfExporter"/> from fonts already installed on the machine. Nothing is bundled.</summary>
internal sealed class SystemFontResolver : IFontResolver
{
    public const string SansFamily = "Tern Sans";
    public const string MonoFamily = "Tern Mono";

    private static readonly object InstallLock = new();
    private static SystemFontResolver? _installed;

    private static readonly FontSet[] SansCandidates =
    [
        new("segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf"),
        new("arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf"),
        new("calibri.ttf", "calibrib.ttf", "calibrii.ttf", "calibriz.ttf"),
        new("DejaVuSans.ttf", "DejaVuSans-Bold.ttf", "DejaVuSans-Oblique.ttf", "DejaVuSans-BoldOblique.ttf"),
        new("LiberationSans-Regular.ttf", "LiberationSans-Bold.ttf", "LiberationSans-Italic.ttf", "LiberationSans-BoldItalic.ttf"),
        new("NotoSans-Regular.ttf", "NotoSans-Bold.ttf", "NotoSans-Italic.ttf", "NotoSans-BoldItalic.ttf"),
        new("Helvetica.ttf", "Helvetica-Bold.ttf", "Helvetica-Oblique.ttf", "Helvetica-BoldOblique.ttf"),
    ];

    private static readonly FontSet[] MonoCandidates =
    [
        new("consola.ttf", "consolab.ttf", "consolai.ttf", "consolaz.ttf"),
        new("cour.ttf", "courbd.ttf", "couri.ttf", "courbi.ttf"),
        new("DejaVuSansMono.ttf", "DejaVuSansMono-Bold.ttf", "DejaVuSansMono-Oblique.ttf", "DejaVuSansMono-BoldOblique.ttf"),
        new("LiberationMono-Regular.ttf", "LiberationMono-Bold.ttf", "LiberationMono-Italic.ttf", "LiberationMono-BoldItalic.ttf"),
        new("NotoSansMono-Regular.ttf", "NotoSansMono-Bold.ttf", "NotoSansMono-Regular.ttf", "NotoSansMono-Bold.ttf"),
    ];

    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _data = new(StringComparer.Ordinal);
    private readonly object _dataLock = new();

    private SystemFontResolver(IReadOnlyList<string> directories)
    {
        Register(SansFamily, SansCandidates, directories);
        Register(MonoFamily, MonoCandidates, directories);
    }

    /// <summary>The display name of the sans font that was found, for diagnostics.</summary>
    public string SansFileName => Path.GetFileName(_files[Key(SansFamily, false, false)]);

    /// <summary>Installs the resolver (once per process) and returns it.</summary>
    public static SystemFontResolver Install()
    {
        lock (InstallLock)
        {
            if (_installed is not null)
            {
                return _installed;
            }

            var resolver = new SystemFontResolver(SearchDirectories());
            try
            {
                GlobalFontSettings.FontResolver = resolver;
            }
            catch (InvalidOperationException ex)
            {
                throw new ExportException("PDF export needs to install its font resolver, but another one is already active in this process.", ex);
            }

            _installed = resolver;
            return resolver;
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var family = string.Equals(familyName, MonoFamily, StringComparison.OrdinalIgnoreCase) ? MonoFamily : SansFamily;
        if (_files.ContainsKey(Key(family, bold, italic)))
        {
            return new FontResolverInfo(Key(family, bold, italic));
        }

        // The family lacks this style on disk: use the closest real face and let PDFsharp synthesise the rest.
        if (bold && italic && _files.ContainsKey(Key(family, true, false)))
        {
            return new FontResolverInfo(Key(family, true, false), mustSimulateBold: false, mustSimulateItalic: true);
        }

        if (bold && italic && _files.ContainsKey(Key(family, false, true)))
        {
            return new FontResolverInfo(Key(family, false, true), mustSimulateBold: true, mustSimulateItalic: false);
        }

        return new FontResolverInfo(Key(family, false, false), mustSimulateBold: bold, mustSimulateItalic: italic);
    }

    public byte[]? GetFont(string faceName)
    {
        if (!_files.TryGetValue(faceName, out var path))
        {
            return null;
        }

        lock (_dataLock)
        {
            if (!_data.TryGetValue(faceName, out var data))
            {
                data = File.ReadAllBytes(path);
                _data[faceName] = data;
            }

            return data;
        }
    }

    private static string Key(string family, bool bold, bool italic) => $"{family}|{(bold ? 'b' : 'r')}{(italic ? 'i' : 'r')}";

    private void Register(string family, FontSet[] candidates, IReadOnlyList<string> directories)
    {
        foreach (var candidate in candidates)
        {
            var regular = Find(directories, candidate.Regular);
            if (regular is null)
            {
                continue;
            }

            _files[Key(family, false, false)] = regular;
            AddIfFound(family, true, false, directories, candidate.Bold);
            AddIfFound(family, false, true, directories, candidate.Italic);
            AddIfFound(family, true, true, directories, candidate.BoldItalic);
            return;
        }

        throw new ExportException(
            $"PDF export needs an installed TrueType font for '{family}' (for example Segoe UI/Consolas, Arial/Courier New or DejaVu), but none was found in the system font folders.");
    }

    private void AddIfFound(string family, bool bold, bool italic, IReadOnlyList<string> directories, string fileName)
    {
        if (Find(directories, fileName) is { } path)
        {
            _files[Key(family, bold, italic)] = path;
        }
    }

    private static string? Find(IReadOnlyList<string> directories, string fileName)
    {
        foreach (var directory in directories)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static List<string> SearchDirectories()
    {
        var directories = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                directories.Add(path);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            Add(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"));
        }
        else
        {
            foreach (var root in new[] { "/usr/share/fonts", "/usr/local/share/fonts", "/Library/Fonts", "/System/Library/Fonts", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fonts") })
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                Add(root);
                foreach (var sub in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 }))
                {
                    Add(sub);
                }
            }
        }

        return directories;
    }

    private readonly record struct FontSet(string Regular, string Bold, string Italic, string BoldItalic);
}
