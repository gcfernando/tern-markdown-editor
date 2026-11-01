using System.Text;
using Tern.App.Commands;
using Tern.App.Help;

namespace Tern.Desktop.Services;

/// <summary>Keeps the exact question and fits complete guide excerpts to the selected provider's request limit.</summary>
internal static class GuideQuestionPrompt
{
    private const string Instructions = "Answer the user's question clearly. General questions are welcome. If the question concerns Tern, " +
        "use the supplied current command list and guide; do not invent features or claim to have performed actions. " +
        "If these facts do not establish an answer, explain the uncertainty.";

    public static string Build(CommandRegistry registry, string question, bool forCopilot)
    {
        var commands = registry.Commands.Select(c => c.Title + " | " + c.Category + " | " + Gesture.Describe(registry.GetGesture(c.Id))).ToArray();
        var features = FeatureCatalog.Search(registry, question).Take(8).ToArray();
        var ending = "\n\nUSER QUESTION\n" + question;
        if (!forCopilot)
        {
            var relevant = string.Join('\n', features.Select(f => f.Name + ": " + f.Description + " " + string.Join(" ", f.Steps)));
            return Instructions + "\n\nTERN COMMANDS\n" + string.Join('\n', commands) + "\n\nRELEVANT GUIDE\n" + relevant + ending;
        }

        const string guideHeading = "\n\nRELEVANT GUIDE (selected excerpts)\n";
        var prefix = Instructions + guideHeading;
        var budget = CopilotTextClient.MaximumPromptCharacters - ending.Length;
        if (prefix.Length > budget)
            throw new InvalidOperationException("This question is too long for Copilot's 8,192-character request limit. Shorten the question and try again.");
        var text = new StringBuilder(prefix);
        void Add(string chunk)
        {
            // Reference prose can discuss mentions; keep those examples literal
            // without introducing CLI mention syntax into a text-only request.
            chunk = chunk.Replace("@", "at-sign ", StringComparison.Ordinal);
            if (text.Length + chunk.Length + 1 <= budget) text.Append(chunk).Append('\n');
        }
        // Descriptions establish the available behavior before the more detailed
        // steps consume the remaining space. Never truncate the user's question
        // or cut an individual guide instruction in half.
        foreach (var feature in features)
            Add(feature.Name + ": " + feature.Description + (feature.MenuPath is { Length: > 0 } menu ? " Menu: " + menu + "." : "") +
                (feature.Shortcut.Length > 0 ? " Shortcut: " + feature.Shortcut + "." : ""));
        foreach (var feature in features)
            foreach (var step in feature.Steps) Add(feature.Name + " — " + step);
        if (text.Length + "\nTERN COMMANDS\n".Length <= budget)
        {
            text.Append("\nTERN COMMANDS\n");
            foreach (var command in commands) Add(command);
        }
        return text.Append(ending).ToString();
    }
}
