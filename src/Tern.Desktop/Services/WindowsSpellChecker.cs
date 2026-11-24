using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Tern.App.Editing;

namespace Tern.Desktop.Services;

internal sealed class WindowsSpellChecker
{
    internal static IReadOnlyList<SpellingIssue> Check(string text, CancellationToken token, string? language = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Spell checking requires Windows.");
        if (string.IsNullOrWhiteSpace(text)) return [];
        var factory = (ISpellCheckerFactory)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"), throwOnError: true)!)!;
        ISpellChecker? checker = null;
        try
        {
            var languages = language is not null ? [language] : new[] { CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name };
            var selected = languages.Distinct().FirstOrDefault(candidate => candidate.Length > 0 && factory.IsSupported(candidate));
            if (selected is null)
                throw new NotSupportedException("Install a Windows spelling dictionary for your current language in Windows Settings, or turn off Windows spell checking in Tern Settings.");
            checker = factory.CreateSpellChecker(selected);
            var result = new List<SpellingIssue>();
            for (var offset = 0; offset < text.Length && result.Count < 512;)
            {
                token.ThrowIfCancellationRequested();
                var end = Math.Min(text.Length, offset + 16000);
                while (end < text.Length && end > offset && !char.IsWhiteSpace(text[end])) end--;
                if (end == offset) end = Math.Min(text.Length, offset + 16000);
                var chunk = text[offset..end];
                var errors = checker.Check(chunk);
                try
                {
                    while (result.Count < 512)
                    {
                        token.ThrowIfCancellationRequested();
                        var status = errors.Next(out var error);
                        if (status < 0) Marshal.ThrowExceptionForHR(status);
                        if (error is null) break;
                        try
                        {
                            var start = checked((int)error.StartIndex());
                            var length = checked((int)error.Length());
                            var action = error.CorrectiveAction();
                            if (action == 0 || length == 0 || start < 0 || start > chunk.Length - length) continue;
                            var word = chunk.Substring(start, length);
                            var suggestions = new List<string>();
                            if (action == 2) suggestions.Add(error.Replacement());
                            else if (action == 3) suggestions.Add("");
                            else
                            {
                                var enumerator = checker.Suggest(word);
                                try
                                {
                                    var values = new string[1];
                                    for (var i = 0; i < 5; i++)
                                    {
                                        var next = enumerator.Next(1, values, IntPtr.Zero);
                                        if (next < 0) Marshal.ThrowExceptionForHR(next);
                                        if (next != 0) break;
                                        if (!suggestions.Contains(values[0], StringComparer.Ordinal)) suggestions.Add(values[0]);
                                    }
                                }
                                finally { Marshal.ReleaseComObject(enumerator); }
                            }
                            result.Add(new SpellingIssue(offset + start, length, word, suggestions));
                        }
                        finally { Marshal.ReleaseComObject(error); }
                        if (status != 0) break;
                    }
                }
                finally { Marshal.ReleaseComObject(errors); }
                offset = end;
            }
            return result;
        }
        finally
        {
            if (checker is not null) Marshal.ReleaseComObject(checker);
            Marshal.ReleaseComObject(factory);
        }
    }

    // Only the read-only leading COM slots are exposed; dictionary-mutating methods are intentionally absent.
    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        IEnumString SupportedLanguages();
        [return: MarshalAs(UnmanagedType.Bool)] bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string language);
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string language);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        [return: MarshalAs(UnmanagedType.LPWStr)] string LanguageTag();
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig] int Next([MarshalAs(UnmanagedType.Interface)] out ISpellingError? error);
    }

    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellingError
    {
        uint StartIndex();
        uint Length();
        int CorrectiveAction();
        [return: MarshalAs(UnmanagedType.LPWStr)] string Replacement();
    }
}
