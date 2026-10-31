using Tern.App.Commands;
using Tern.App.Help;

namespace Tern.App.Palette;

public sealed record PaletteEntry(string CommandId, string Title, string Category, string Shortcut);

/// <summary>Filters the command list for the palette: every typed word must appear in the title or category, and title matches rank first.</summary>
public static class CommandPaletteModel
{
    public static IReadOnlyList<PaletteEntry> Filter(CommandRegistry registry, string? query)
    {
        var words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ranked = new List<(int Rank, PaletteEntry Entry)>();
        var helpMatches = FeatureCatalog.Search(registry, query).Select(f => f.LaunchCommandId ?? f.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var command in registry.Commands)
        {
            var rank = Rank(command, words);
            if (rank < 0)
            {
                if (!helpMatches.Contains(command.Id)) continue;
                rank = 10;
            }

            var gesture = registry.GetGesture(command.Id);
            ranked.Add((rank, new PaletteEntry(command.Id, command.Title, command.Category, gesture is null ? string.Empty : Gesture.Describe(gesture))));
        }

        return [.. ranked.OrderBy(r => r.Rank).ThenBy(r => r.Entry.Category, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Entry.Title, StringComparer.OrdinalIgnoreCase).Select(r => r.Entry)];
    }

    private static int Rank(CommandDefinition command, string[] words)
    {
        if (words.Length == 0)
        {
            return 1;
        }

        var rank = 0;
        foreach (var word in words)
        {
            if (command.Title.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (command.Title.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                rank++;
            }
            else if (command.Category.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                rank += 2;
            }
            else
            {
                return -1;
            }
        }

        return rank;
    }
}
