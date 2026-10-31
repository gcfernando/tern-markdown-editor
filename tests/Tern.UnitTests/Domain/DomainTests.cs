using Tern.Domain.Documents;
using Tern.Domain.Merging;
using Tern.Domain.Text;

namespace Tern.UnitTests.Domain;

public class DocumentSessionTests
{
    private static DocumentSession Create(string text = "hello") =>
        new("a.md", text, TextFileFormat.NewFileDefault, null);

    [Fact]
    public void A_new_session_is_clean_at_revision_zero()
    {
        var session = Create();

        Assert.False(session.IsDirty);
        Assert.Equal(0, session.Revision);
        Assert.Equal("hello", session.GetSnapshot().Text);
    }

    [Fact]
    public void Each_real_edit_advances_the_revision_and_marks_dirty()
    {
        var session = Create();

        session.ApplyEdits([TextEdit.Insert(5, "!")]);

        Assert.Equal(1, session.Revision);
        Assert.True(session.IsDirty);
        Assert.Equal("hello!", session.GetSnapshot().Text);
    }

    [Fact]
    public void Reading_snapshots_never_changes_the_revision()
    {
        var session = Create();

        _ = session.GetSnapshot();
        _ = session.GetSnapshot();

        Assert.Equal(0, session.Revision);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void Snapshots_are_immutable_after_later_edits()
    {
        var session = Create();
        var before = session.GetSnapshot();

        session.ApplyEdits([TextEdit.Insert(0, ">> ")]);

        Assert.Equal("hello", before.Text);
        Assert.False(session.IsCurrent(before.Revision));
    }

    [Fact]
    public void Saving_an_older_snapshot_keeps_newer_edits_dirty()
    {
        var session = Create();
        session.ApplyEdits([TextEdit.Insert(5, "1")]);
        var saved = session.GetSnapshot();
        session.ApplyEdits([TextEdit.Insert(6, "2")]);

        session.MarkSaved(saved, session.Format, new DiskBaseline(1, DateTime.UtcNow, "x"));

        Assert.True(session.IsDirty);
        Assert.Equal(saved.Revision, session.SavedRevision);
    }

    [Fact]
    public void Undo_back_to_the_saved_text_clears_the_dirty_flag()
    {
        var session = Create();
        var buffer = new UndoableBuffer("hello");
        session.AttachBuffer(buffer);

        buffer.Edit("hello world");
        Assert.True(session.IsDirty);
        buffer.Undo();

        Assert.False(session.IsDirty);
    }

    [Fact]
    public void Manual_revert_compares_exact_text_including_whitespace_and_format()
    {
        var session = Create("hello \n");
        session.ApplyEdits([TextEdit.Insert(0, "changed ")]);
        Assert.True(session.IsDirty);
        session.ApplyEdits([TextEdit.Delete(0, 8)]);
        Assert.False(session.IsDirty);
        session.ApplyEdits([TextEdit.Delete(5, 1)]);
        Assert.True(session.IsDirty);
        session.ApplyEdits([TextEdit.Insert(5, " ")]);
        Assert.False(session.IsDirty);
        var originalFormat = session.Format;
        session.ChangeFormat(originalFormat with { HasBom = !originalFormat.HasBom });
        Assert.True(session.IsDirty);
        session.ChangeFormat(originalFormat);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void Undo_to_saved_text_keeps_a_pending_encoding_conversion_dirty()
    {
        var session = Create();
        var buffer = new UndoableBuffer("hello");
        session.AttachBuffer(buffer);
        buffer.Edit("hello world");
        session.ChangeFormat(session.Format with { HasBom = true });
        buffer.Undo();
        Assert.Equal("hello", session.GetSnapshot().Text);
        Assert.True(session.IsDirty);
        Assert.True(session.Format.HasBom);
    }

    [Fact]
    public void Completing_an_older_save_does_not_revert_a_newer_format_conversion()
    {
        var session = Create();
        var snapshot = session.GetSnapshot();
        var savedFormat = session.Format;
        session.ChangeFormat(savedFormat with { Encoding = TextEncodingKind.Utf16LittleEndian, HasBom = true });
        session.MarkSaved(snapshot, savedFormat, new DiskBaseline(5, DateTime.UtcNow, "hash"));
        Assert.True(session.IsDirty);
        Assert.Equal(TextEncodingKind.Utf16LittleEndian, session.Format.Encoding);
        Assert.True(session.Format.HasBom);
    }

    [Fact]
    public void Read_only_sessions_reject_edits()
    {
        var session = new DocumentSession("a.md", "x", TextFileFormat.NewFileDefault, null, isReadOnly: true);

        Assert.Throws<InvalidOperationException>(() => session.ApplyEdits([TextEdit.Insert(0, "y")]));
    }

    [Fact]
    public void Reloading_disk_content_replaces_text_and_clears_dirty()
    {
        var session = Create();
        session.ApplyEdits([TextEdit.Insert(0, "local ")]);

        session.ReplaceWithDiskContent("from disk", session.Format, new DiskBaseline(9, DateTime.UtcNow, "h"));

        Assert.False(session.IsDirty);
        Assert.Equal("from disk", session.GetSnapshot().Text);
    }

    [Fact]
    public void Overlapping_edits_are_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            TextEdit.ApplyAll("abcdef", [new TextEdit(0, 4, "x"), new TextEdit(2, 2, "y")]));
    }

    [Fact]
    public void Several_edits_apply_against_the_original_offsets()
    {
        var result = TextEdit.ApplyAll("abcdef", [new TextEdit(4, 1, "E"), TextEdit.Insert(1, "-"), TextEdit.Delete(0, 1)]);

        Assert.Equal("-bcdEf", result);
    }

    private sealed class UndoableBuffer(string text) : IDocumentBuffer
    {
        private readonly Stack<string> _history = new();
        private string _text = text;

        public event EventHandler<BufferChangedEventArgs>? Changed;

        public string GetText() => _text;

        public void Apply(IReadOnlyList<TextEdit> edits) => Edit(TextEdit.ApplyAll(_text, edits));

        public void Edit(string newText)
        {
            _history.Push(_text);
            _text = newText;
            Changed?.Invoke(this, new BufferChangedEventArgs(false));
        }

        public void Undo()
        {
            _text = _history.Pop();
            Changed?.Invoke(this, new BufferChangedEventArgs(true));
        }
    }
}

public class ThreeWayMergerTests
{
    [Fact]
    public void Changes_to_different_parts_merge_cleanly()
    {
        var baseText = "one\ntwo\nthree\nfour\nfive\nsix\n";
        var mine = "ONE\ntwo\nthree\nfour\nfive\nsix\n";
        var disk = "one\ntwo\nthree\nfour\nfive\nSIX\n";

        var result = ThreeWayMerger.Merge(baseText, mine, disk);

        Assert.False(result.HasConflicts);
        Assert.Equal("ONE\ntwo\nthree\nfour\nfive\nSIX\n", result.MergedText);
    }

    [Fact]
    public void Competing_edits_to_the_same_line_conflict()
    {
        var result = ThreeWayMerger.Merge("a\nb\nc\n", "a\nmine\nc\n", "a\ndisk\nc\n");

        Assert.True(result.HasConflicts);
        var conflict = Assert.Single(result.Regions.OfType<ConflictRegion>());
        Assert.Equal("b\n", conflict.Base);
        Assert.Equal("mine\n", conflict.Mine);
        Assert.Equal("disk\n", conflict.Disk);
    }

    [Fact]
    public void Identical_changes_on_both_sides_are_accepted_once()
    {
        var result = ThreeWayMerger.Merge("a\nb\nc\n", "a\nX\nc\n", "a\nX\nc\n");

        Assert.False(result.HasConflicts);
        Assert.Equal("a\nX\nc\n", result.MergedText);
    }

    [Fact]
    public void Line_endings_are_preserved_through_a_merge()
    {
        var baseText = "a\r\nb\r\nc\r\nd\r\ne\r\n";

        var result = ThreeWayMerger.Merge(baseText, "A\r\nb\r\nc\r\nd\r\ne\r\n", "a\r\nb\r\nc\r\nd\r\nE\r\n");

        Assert.Equal("A\r\nb\r\nc\r\nd\r\nE\r\n", result.MergedText);
    }

    [Fact]
    public void Insertions_at_different_places_merge()
    {
        var baseText = "a\nb\nc\nd\ne\n";

        var result = ThreeWayMerger.Merge(baseText, "x\na\nb\nc\nd\ne\n", "a\nb\nc\nd\ne\ny\n");

        Assert.False(result.HasConflicts);
        Assert.Equal("x\na\nb\nc\nd\ne\ny\n", result.MergedText);
    }

    [Fact]
    public void Conflicts_render_with_markers_and_can_be_resolved_per_region()
    {
        var result = ThreeWayMerger.Merge("a\nb\nc\n", "a\nmine\nc\n", "a\ndisk\nc\n");

        Assert.Contains("<<<<<<< Mine", result.MergedText, StringComparison.Ordinal);
        Assert.Equal("a\nmine\nc\n", result.Render(c => c.Mine));
        Assert.Equal("a\ndisk\nc\n", result.Render(c => c.Disk));
    }

    [Fact]
    public void Randomised_merges_never_lose_changes_when_only_one_side_edits()
    {
        var random = new Random(7);
        for (var round = 0; round < 200; round++)
        {
            var baseLines = Enumerable.Range(0, random.Next(0, 30)).Select(i => $"line {i}\n").ToList();
            var changed = baseLines.ToList();
            for (var edits = random.Next(0, 5); edits > 0; edits--)
            {
                switch (random.Next(3))
                {
                    case 0 when changed.Count > 0:
                        changed[random.Next(changed.Count)] = $"edit {random.Next(1000)}\n";
                        break;
                    case 1:
                        changed.Insert(random.Next(changed.Count + 1), $"new {random.Next(1000)}\n");
                        break;
                    case 2 when changed.Count > 0:
                        changed.RemoveAt(random.Next(changed.Count));
                        break;
                }
            }

            var baseText = string.Concat(baseLines);
            var changedText = string.Concat(changed);

            Assert.Equal(changedText, ThreeWayMerger.Merge(baseText, changedText, baseText).MergedText);
            Assert.Equal(changedText, ThreeWayMerger.Merge(baseText, baseText, changedText).MergedText);
            Assert.Equal(changedText, ThreeWayMerger.Merge(baseText, changedText, changedText).MergedText);
        }
    }

    [Fact]
    public void Randomised_two_sided_merges_reproduce_each_side_when_resolved_to_it()
    {
        var random = new Random(11);
        for (var round = 0; round < 200; round++)
        {
            var baseLines = Enumerable.Range(0, 20).Select(i => $"l{i}\n").ToList();
            var mine = Mutate(baseLines, random);
            var disk = Mutate(baseLines, random);
            var result = ThreeWayMerger.Merge(string.Concat(baseLines), string.Concat(mine), string.Concat(disk));

            // Whatever the merge decides, no line either side introduced may vanish once conflicts
            // are resolved in favour of that side.
            var resolvedMine = result.Render(c => c.Mine);
            var resolvedDisk = result.Render(c => c.Disk);
            foreach (var line in mine.Where(l => !baseLines.Contains(l)))
            {
                Assert.Contains(line, resolvedMine, StringComparison.Ordinal);
            }

            foreach (var line in disk.Where(l => !baseLines.Contains(l)))
            {
                Assert.Contains(line, resolvedDisk, StringComparison.Ordinal);
            }
        }

        static List<string> Mutate(List<string> lines, Random r)
        {
            var copy = lines.ToList();
            var tag = r.Next(1_000_000);
            for (var i = r.Next(0, 4); i > 0; i--)
            {
                var idx = r.Next(copy.Count);
                if (r.Next(2) == 0)
                {
                    copy[idx] = $"edit-{tag}-{i}\n";
                }
                else
                {
                    copy.Insert(idx, $"ins-{tag}-{i}\n");
                }
            }

            return copy;
        }
    }

    [Fact]
    public void Very_different_inputs_do_not_hang_and_report_a_conflict()
    {
        var a = string.Concat(Enumerable.Range(0, 6000).Select(i => $"a{i}\n"));
        var b = string.Concat(Enumerable.Range(0, 6000).Select(i => $"b{i}\n"));

        var result = ThreeWayMerger.Merge("x\n", a, b);

        Assert.True(result.HasConflicts);
    }
}
