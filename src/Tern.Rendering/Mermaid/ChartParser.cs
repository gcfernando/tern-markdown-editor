using System.Globalization;
using System.Text.RegularExpressions;

namespace Tern.Rendering.Mermaid;

internal static partial class ChartParser
{
    public static PieDiagram Pie(IReadOnlyList<MermaidDiagram.SourceLine> lines)
    {
        var content = lines.SkipWhile(line => line.Text.Length == 0).ToArray();
        var header = content[0].Text;
        var keywordEnd = header.IndexOfAny([' ', '\t']);
        var options = keywordEnd < 0 ? string.Empty : header[keywordEnd..].Trim();
        var showData = options == "showData" || options.StartsWith("showData ", StringComparison.Ordinal) || options.StartsWith("showData\t", StringComparison.Ordinal);
        if (showData) options = options[8..].Trim();
        var categories = new List<PieCategory>();
        string? title = null;
        if (options.StartsWith("title ", StringComparison.Ordinal) || options.StartsWith("title\t", StringComparison.Ordinal))
            title = MermaidDiagram.DecodeEntities(options[5..].Trim());
        else if (options.Length > 0) throw new MermaidSyntaxException("Unsupported pie header option; expected showData or title.", content[0].Number);
        foreach (var (number, text) in content.Skip(1))
        {
            if (text.Length == 0) continue;
            if (text.StartsWith("title ", StringComparison.Ordinal)) { title = MermaidDiagram.DecodeEntities(text[6..].Trim()); continue; }
            var match = PieValue().Match(text);
            if (!match.Success || !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < 0)
                throw new MermaidSyntaxException("Pie categories require a quoted label and a finite nonnegative value.", number);
            if (categories.Count >= MermaidLimits.MaxNodes) throw new MermaidSyntaxException("A pie chart has more than 500 categories.", number);
            categories.Add(new(MermaidDiagram.DecodeEntities(match.Groups["label"].Value), value));
        }
        if (categories.Count == 0 || categories.All(category => category.Value == 0)) throw new MermaidSyntaxException("A pie chart needs a positive total.", 1);
        return new(categories, title, showData);
    }

    private sealed record PendingTask(string Id, string Label, string Section, string Start, string Length, bool Milestone, bool Done, bool Active, bool Critical, int Line);

    public static GanttDiagram Gantt(IReadOnlyList<MermaidDiagram.SourceLine> lines)
    {
        var pending = new List<PendingTask>();
        string? title = null;
        var section = string.Empty;
        foreach (var (number, text) in lines.SkipWhile(line => line.Text.Length == 0).Skip(1))
        {
            if (text.Length == 0) continue;
            if (text.StartsWith("title ", StringComparison.Ordinal)) { title = MermaidDiagram.DecodeEntities(text[6..].Trim()); continue; }
            if (text.StartsWith("section ", StringComparison.Ordinal)) { section = MermaidDiagram.DecodeEntities(text[8..].Trim()); continue; }
            if (text.StartsWith("dateFormat ", StringComparison.Ordinal))
            {
                if (text[11..].Trim() != "YYYY-MM-DD") throw new MermaidSyntaxException("Native Gantt charts currently require dateFormat YYYY-MM-DD.", number);
                continue;
            }
            var colon = text.IndexOf(':');
            if (colon <= 0) throw new MermaidSyntaxException("Expected a Gantt task label followed by dates or dependencies.", number);
            var label = MermaidDiagram.DecodeEntities(text[..colon].Trim());
            var tokens = text[(colon + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            var flags = new HashSet<string>(StringComparer.Ordinal);
            while (tokens.Count > 0 && tokens[0] is "done" or "active" or "crit" or "milestone") { flags.Add(tokens[0]); tokens.RemoveAt(0); }
            if (tokens.Count is not (2 or 3)) throw new MermaidSyntaxException("A Gantt task needs optional id, start/dependency, and duration/end date.", number);
            if (pending.Count >= MermaidLimits.MaxNodes) throw new MermaidSyntaxException("A Gantt chart has more than 500 tasks.", number);
            var id = tokens.Count == 3 ? tokens[0] : "\u0001task-" + pending.Count.ToString(CultureInfo.InvariantCulture);
            if (id.Length == 0 || pending.Any(task => task.Id == id)) throw new MermaidSyntaxException("Gantt task identifiers must be unique.", number);
            pending.Add(new(id, label, section, tokens[^2], tokens[^1], flags.Contains("milestone"), flags.Contains("done"), flags.Contains("active"), flags.Contains("crit"), number));
        }
        if (pending.Count == 0) throw new MermaidSyntaxException("The Gantt chart has no tasks.", 1);
        var indexes = pending.Select((task, index) => (task.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
        var resolved = new GanttTask?[pending.Count];
        var visiting = new bool[pending.Count];
        for (var index = 0; index < pending.Count; index++) Resolve(index);
        var result = resolved.Select(task => task!).ToArray();
        if (result.Max(task => task.End.DayNumber) - result.Min(task => task.Start.DayNumber) > 36600) throw new MermaidSyntaxException("A Gantt date range exceeds 100 years.", 1);
        return new(result, title);

        GanttTask Resolve(int index)
        {
            if (resolved[index] is { } existing) return existing;
            var task = pending[index];
            if (visiting[index]) throw new MermaidSyntaxException("Gantt dependencies contain a cycle.", task.Line);
            visiting[index] = true;
            DateOnly start;
            if (task.Start.StartsWith("after ", StringComparison.Ordinal))
            {
                var dependencies = task.Start[6..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (dependencies.Length == 0) throw new MermaidSyntaxException("An after clause needs a task id.", task.Line);
                start = dependencies.Select(id => indexes.TryGetValue(id, out var dependency) ? Resolve(dependency).End
                    : throw new MermaidSyntaxException("Unknown Gantt dependency '" + id + "'.", task.Line)).Max();
            }
            else start = Date(task.Start, task.Line);
            DateOnly end;
            var duration = Duration().Match(task.Length);
            if (duration.Success)
            {
                if (!int.TryParse(duration.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > 36600)
                    throw new MermaidSyntaxException("Gantt duration is too large.", task.Line);
                var days = (long)value * (duration.Groups["unit"].Value == "w" ? 7 : 1);
                if (days > 36600 || days > DateOnly.MaxValue.DayNumber - start.DayNumber) throw new MermaidSyntaxException("Gantt end date is out of range.", task.Line);
                end = start.AddDays((int)days);
            }
            else end = Date(task.Length, task.Line);
            if (end < start || (!task.Milestone && end == start)) throw new MermaidSyntaxException("Task end must follow its start; zero duration is reserved for milestones.", task.Line);
            if (task.Milestone && end != start) throw new MermaidSyntaxException("A milestone must have zero duration.", task.Line);
            visiting[index] = false;
            var completed = new GanttTask(task.Id, task.Label, task.Section, start, end, task.Milestone, task.Done, task.Active, task.Critical);
            resolved[index] = completed;
            return completed;
        }
    }

    private static DateOnly Date(string text, int line) => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
        ? value : throw new MermaidSyntaxException("Invalid Gantt date; expected YYYY-MM-DD.", line);
    [GeneratedRegex("^\"(?<label>[^\"]+)\"\\s*:\\s*(?<value>[+\\-]?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:[eE][+\\-]?[0-9]+)?)\\s*$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex PieValue();
    [GeneratedRegex("^(?<number>[0-9]+)(?<unit>d|w)$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex Duration();
}
