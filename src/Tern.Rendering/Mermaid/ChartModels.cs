using System.Globalization;

namespace Tern.Rendering.Mermaid;

/// <summary>Plain vector geometry shared by native charts and script-free HTML export.</summary>
public abstract record ChartPrimitive;
/// <summary>Text within a measured box.</summary>
public sealed record ChartText(string Text, DiagramRect Bounds, bool Bold = false) : ChartPrimitive;
/// <summary>A colored rectangle; a negative palette index denotes a neutral section band.</summary>
public sealed record ChartBox(DiagramRect Bounds, int PaletteIndex) : ChartPrimitive;
/// <summary>A colored closed polygon.</summary>
public sealed record ChartPolygon(IReadOnlyList<DiagramPoint> Points, int PaletteIndex) : ChartPrimitive;
/// <summary>A neutral grid or axis line.</summary>
public sealed record ChartLine(DiagramPoint From, DiagramPoint To) : ChartPrimitive;
/// <summary>Positioned chart geometry.</summary>
public sealed record ChartLayout(string Description, IReadOnlyList<ChartPrimitive> Primitives, double Width, double Height, int ElementCount);

/// <summary>Fixed categorical colors; source text cannot supply CSS or paint expressions.</summary>
public static class ChartPalette
{
    private static readonly string[] Colors = ["#4477AA", "#EE6677", "#228833", "#CCBB44", "#66CCEE", "#AA3377", "#BBBBBB", "#EE9944"];
    /// <summary>Gets a bounded, inert hexadecimal paint.</summary>
    public static string Get(int index) => Colors[(index % Colors.Length + Colors.Length) % Colors.Length];
}

/// <summary>Base of supported pie and date-based Gantt models.</summary>
public abstract class ChartDiagram
{
    /// <summary>A plain title, if supplied.</summary>
    public abstract string? Title { get; }
    /// <summary>Accessible chart type description.</summary>
    public abstract string Description { get; }
    /// <summary>Computes bounded deterministic geometry using the caller's actual font metrics.</summary>
    public abstract ChartLayout Layout(Func<string, (double Width, double Height)>? measurer = null);
}

/// <summary>One nonnegative finite category value.</summary>
public sealed record PieCategory(string Label, double Value);

/// <summary>A pie chart; category labels and values remain readable in an external legend.</summary>
public sealed class PieDiagram(IReadOnlyList<PieCategory> categories, string? title, bool showData) : ChartDiagram
{
    /// <summary>Categories in source order.</summary>
    public IReadOnlyList<PieCategory> Categories { get; } = categories;
    /// <inheritdoc/>
    public override string? Title { get; } = title;
    /// <summary>Whether the source requested numeric category values.</summary>
    public bool ShowData { get; } = showData;
    /// <inheritdoc/>
    public override string Description => "Pie chart";
    /// <inheritdoc/>
    public override ChartLayout Layout(Func<string, (double Width, double Height)>? measurer = null)
    {
        var measure = MermaidDiagram.Guard(measurer);
        var primitives = new List<ChartPrimitive>();
        const double radius = 132;
        var titleHeight = Title is null ? 0 : measure(MermaidDiagram.WrapLabel(Title, measure, 620)).Height + 16;
        var center = new DiagramPoint(16 + radius, 16 + titleHeight + radius);
        if (Title is not null) primitives.Add(new ChartText(MermaidDiagram.WrapLabel(Title, measure, 620), new(16, 8, 620, titleHeight), true));
        var maximum = Categories.Max(category => category.Value);
        var total = Categories.Sum(category => category.Value / maximum);
        var angle = -Math.PI / 2;
        var legendY = 16 + titleHeight;
        var legendWidth = 0d;
        for (var index = 0; index < Categories.Count; index++)
        {
            var category = Categories[index];
            var fraction = category.Value / maximum / total;
            var sweep = fraction * Math.PI * 2;
            if (sweep > 0)
            {
                var points = new List<DiagramPoint> { center };
                var segments = Math.Max(2, (int)Math.Ceiling(fraction * 128));
                for (var step = 0; step <= segments; step++)
                {
                    var position = angle + sweep * step / segments;
                    points.Add(new(center.X + radius * Math.Cos(position), center.Y + radius * Math.Sin(position)));
                }
                primitives.Add(new ChartPolygon(points, index));
            }
            angle += sweep;
            var label = category.Label + (ShowData ? " — " + category.Value.ToString("G", CultureInfo.InvariantCulture) : string.Empty)
                + " (" + (fraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%)";
            label = MermaidDiagram.WrapLabel(label, measure, 280);
            var size = measure(label);
            var rowHeight = Math.Max(24, size.Height + 8);
            primitives.Add(new ChartBox(new(304, legendY + 4, 16, 16), index));
            primitives.Add(new ChartText(label, new(332, legendY, Math.Max(1, size.Width), rowHeight)));
            legendWidth = Math.Max(legendWidth, size.Width);
            legendY += rowHeight;
        }
        return new(Description, primitives, Math.Max(360 + legendWidth, Title is null ? 0 : 652), Math.Max(center.Y + radius + 16, legendY + 16), Categories.Count);
    }
}

/// <summary>Resolved Gantt task dates, section and marker state.</summary>
public sealed record GanttTask(string Id, string Label, string Section, DateOnly Start, DateOnly End, bool Milestone, bool Done, bool Active, bool Critical);

/// <summary>A date-based schedule retaining resolved dependency semantics.</summary>
public sealed class GanttDiagram(IReadOnlyList<GanttTask> tasks, string? title) : ChartDiagram
{
    /// <summary>Tasks in declaration order.</summary>
    public IReadOnlyList<GanttTask> Tasks { get; } = tasks;
    /// <inheritdoc/>
    public override string? Title { get; } = title;
    /// <inheritdoc/>
    public override string Description => "Gantt chart";
    /// <inheritdoc/>
    public override ChartLayout Layout(Func<string, (double Width, double Height)>? measurer = null)
    {
        var measure = MermaidDiagram.Guard(measurer);
        var primitives = new List<ChartPrimitive>();
        var start = Tasks.Min(task => task.Start);
        var end = Tasks.Max(task => task.End);
        var days = Math.Max(1, end.DayNumber - start.DayNumber);
        const double plotLeft = 244;
        const double plotWidth = 650;
        var titleText = Title is null ? null : MermaidDiagram.WrapLabel(Title, measure, 850);
        var titleHeight = titleText is null ? 0 : measure(titleText).Height + 16;
        if (titleText is not null) primitives.Add(new ChartText(titleText, new(16, 8, 850, titleHeight), true));
        var axisY = 20 + titleHeight;
        var tickCount = Math.Max(1, Math.Min(days, 6));
        var ticks = Enumerable.Range(0, tickCount + 1).Select(index => (int)Math.Round(days * (double)index / tickCount)).Distinct().ToArray();
        var y = axisY + 38;
        string? section = null;
        var sectionIndex = -1;
        foreach (var task in Tasks)
        {
            if (section != task.Section)
            {
                section = task.Section; sectionIndex++;
                if (section.Length > 0)
                {
                    var sectionText = MermaidDiagram.WrapLabel(section, measure, 850);
                    var height = measure(sectionText).Height + 12;
                    primitives.Add(new ChartBox(new(16, y, plotLeft + plotWidth - 16, height), -1));
                    primitives.Add(new ChartText(sectionText, new(20, y + 4, 850, height - 8), true));
                    y += height + 4;
                }
            }
            var label = MermaidDiagram.WrapLabel(task.Label, measure, 210);
            var rowHeight = Math.Max(32, measure(label).Height + 12);
            primitives.Add(new ChartText(label, new(16, y, 210, rowHeight)));
            var left = plotLeft + (task.Start.DayNumber - start.DayNumber) * plotWidth / days;
            var right = plotLeft + (task.End.DayNumber - start.DayNumber) * plotWidth / days;
            var color = task.Critical ? 1 : task.Done ? 2 : task.Active ? 0 : sectionIndex;
            if (task.Milestone)
                primitives.Add(new ChartPolygon([new(left, y + rowHeight / 2 - 9), new(left + 9, y + rowHeight / 2), new(left, y + rowHeight / 2 + 9), new(left - 9, y + rowHeight / 2)], color));
            else primitives.Add(new ChartBox(new(left, y + (rowHeight - 20) / 2, Math.Max(1, right - left), 20), color));
            y += rowHeight + 4;
        }
        foreach (var tick in ticks)
        {
            var x = plotLeft + tick * plotWidth / days;
            var text = start.AddDays(tick).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var size = measure(text);
            primitives.Insert(0, new ChartLine(new(x, axisY + 28), new(x, y)));
            primitives.Add(new ChartText(text, new(x - size.Width / 2, axisY, size.Width, 24)));
        }
        return new(Description, primitives, plotLeft + plotWidth + 64, y + 16, Tasks.Count);
    }
}
