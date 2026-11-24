using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed partial class AIWritingWorkflowTests
{
    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper, false, 1, 1120, 1040)]
    [InlineData(ThemeChoice.Midnight, true, 1, 1120, 1040)]
    [InlineData(ThemeChoice.Paper, true, 1, 760, 640)]
    [InlineData(ThemeChoice.Midnight, false, 1, 760, 640)]
    [InlineData(ThemeChoice.Paper, false, 2, 720, 520)]
    [InlineData(ThemeChoice.Midnight, true, 2, 720, 520)]
    public async Task Knowledge_height_and_scrollbar_gutter_keep_the_question_and_actions_usable(
        ThemeChoice theme, bool reduced, double scale, int width, int height)
    {
        using var f = Fixture((_, _) => Task.FromResult(Models()));
        var previousAnimationsReader = MotionPolicy.SystemAnimationsReader;
        try
        {
            MotionPolicy.SystemAnimationsReader = () => true;
            f.Shell.Settings.Theme = theme;
            f.Shell.Settings.ReduceMotion = reduced;
            f.Shell.Settings.UiScale = scale;
            f.Window.ApplySettings(); f.Window.ApplyTheme();
            f.Window.ShowKnowledgeAssistant();
            var window = Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>());
            Pump(window);
            var preferredHeight = Math.Min(1040, window.MaxHeight);
            Assert.InRange(window.Height, preferredHeight - 1, preferredHeight + 1);
            Assert.True(window.MinHeight <= 240, "Small screens must retain a genuinely resizable window.");
            if (window.Screens.ScreenFromWindow(window) is { } screen)
                Assert.True(window.Height <= screen.WorkingArea.Height / screen.Scaling - 48 + 1);

            window.Width = Math.Min(width, window.MaxWidth);
            window.Height = Math.Min(height, window.MaxHeight);
            await Task.Delay(160); Pump(window);
            var panel = window.Assistant;
            var question = Find<TextBox>(panel, "Your question");
            var ask = Find<StackPanel>(panel, "Ask your notes");
            var page = ask.GetVisualAncestors().OfType<ScrollViewer>().First();
            var pages = (Grid)page.Parent!;
            foreach (var scroller in pages.Children.OfType<ScrollViewer>())
                Assert.True(((Control)scroller.Content!).Margin.Right >= 12,
                    "Each step must reserve a content gutter before its scrollbar.");
            var questionRight = question.TranslatePoint(new Point(question.Bounds.Width, 0), page)!.Value.X;
            var bar = page.GetVisualDescendants().OfType<ScrollBar>()
                .Single(control => control.Orientation == Avalonia.Layout.Orientation.Vertical
                    && ReferenceEquals(control.GetVisualAncestors().OfType<ScrollViewer>().First(), page));
            var rightLimit = bar.IsVisible ? bar.TranslatePoint(default, page)!.Value.X : page.Bounds.Width;
            Assert.True(rightLimit - questionRight >= 11,
                $"Question touches page scrollbar: gap {rightLimit - questionRight}, page={page.Bounds}.");
            foreach (var scroller in pages.Children)
                Assert.Equal(reduced, scroller.Transitions is null);
            if (height == 1040)
            {
                Assert.True(page.Extent.Height <= page.Viewport.Height + 1,
                    $"The reference-height ask page should fit: extent={page.Extent}, viewport={page.Viewport}.");
                AssertInsideKnowledgeWindow(ask.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Text == "Not sure what to ask? Choose one"), window);
            }
            var folder = Path.Combine(Screenshots.Folder, "reported-knowledge-height");
            Directory.CreateDirectory(folder);
            var name = $"{theme}-{width}x{height}-scale{scale}-reduced{reduced}";
            using (var initial = window.CaptureRenderedFrame())
            {
                Assert.NotNull(initial);
                using var initialOutput = File.Create(Path.Combine(folder, name + "-initial.png"));
                initial.Save(initialOutput, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            question.BringIntoView(); Pump(window);
            AssertInsideKnowledgeWindow(question, window);
            Assert.True(question.Focus());
            window.KeyTextInput("A private question");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyTextInput("with a second line");
            Assert.Contains('\n', question.Text!);
            Assert.Contains("with a second line", question.Text);
            Assert.Empty(f.Shell.Tabs.Where(document => document.CurrentText.Contains("private question")));
            question.Text = string.Join('\n', Enumerable.Repeat("A synthetic question line to check the text box scrollbar.", 22));
            question.CaretIndex = question.Text.Length;
            var find = Find<Button>(panel, "Find my notes");
            find.BringIntoView(); Pump(window);
            AssertInsideKnowledgeWindow(find, window);
            var status = Find<TextBlock>(panel, "Explore status");
            status.BringIntoView(); Pump(window);
            AssertInsideKnowledgeWindow(status, window);
            question.BringIntoView(); Pump(window);
            using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
            using (var output = File.Create(Path.Combine(folder, name + ".png")))
                bitmap.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            await File.WriteAllTextAsync(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new
            {
                theme = theme.ToString(), reduced, scale, window.Width, window.Height, preferredHeight,
                window.MaxHeight, questionHeight = question.Bounds.Height, gutter = rightLimit - questionRight,
                extent = page.Extent.Height, viewport = page.Viewport.Height,
                footerHeight = find.Bounds.Height, typedMultiline = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            var questionText = question.Text;
            Assert.True(question.Focus());
            f.Shell.Settings.UiScale = scale == 1 ? 1.5 : 1;
            f.Window.ApplySettings(); Pump(window);
            Assert.Equal(questionText, question.Text);
            Assert.True(question.IsKeyboardFocusWithin);
            Assert.Equal(14 * f.Shell.Settings.UiScale, question.FontSize);
            question.BringIntoView(); Pump(window);
            questionRight = question.TranslatePoint(new Point(question.Bounds.Width, 0), page)!.Value.X;
            rightLimit = bar.IsVisible ? bar.TranslatePoint(default, page)!.Value.X : page.Bounds.Width;
            Assert.True(rightLimit - questionRight >= 11, "Live text scaling must retain the page gutter.");
        }
        finally
        {
            MotionPolicy.SystemAnimationsReader = previousAnimationsReader;
            f.Shell.Settings.UiScale = 1; f.Window.ApplySettings();
        }
    }

    private static void AssertInsideKnowledgeWindow(Control control, Window window)
    {
        var point = control.TranslatePoint(default, window)!.Value;
        Assert.True(point.X >= -1 && point.Y >= -1 && point.X + control.Bounds.Width <= window.ClientSize.Width + 1
            && point.Y + control.Bounds.Height <= window.ClientSize.Height + 1,
            $"{AutomationProperties.GetName(control) ?? control.GetType().Name}: {point}, {control.Bounds}, client={window.ClientSize}");
    }
}
