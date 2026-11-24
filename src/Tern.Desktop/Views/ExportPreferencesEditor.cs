using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Styling;
using Tern.App.Settings;
using Tern.Export;

namespace Tern.Desktop.Views;

/// <summary>Edits the Settings window's pending copy, never exports or changes files.</summary>
internal static class ExportPreferencesEditor
{
    internal static Control Build(ExportPreferences preferences)
    {
        var format = new ComboBox
        {
            ItemsSource = new[] { ExportFormat.Pdf, ExportFormat.Html, ExportFormat.Text },
            ItemTemplate = new FuncDataTemplate<ExportFormat>((value, _) => new TextBlock
            {
                Text = value.DisplayName(), TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            }), SelectedItem = ExportFormat.Pdf,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(format, "Export defaults format");
        var fields = GuideParts.Vertical();
        var reset = new Button { Content = "Reset selected format" };
        AutomationProperties.SetName(reset, "Reset selected export format defaults");
        var forget = new Button { Content = "Forget remembered export folders" };
        AutomationProperties.SetName(forget, "Forget remembered export folders");
        var folderStatus = MessageTones.Follow(GuideParts.Caption(""));

        ExportFormat Selected() => format.SelectedItem is ExportFormat value ? value : ExportFormat.Pdf;
        ExportPreset Read(ExportFormat kind) => preferences.Formats.GetValueOrDefault(kind.ToString())
            ?? new ExportPreset(Theme: Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? "Dark" : "Light");
        void UpdateFolders()
        {
            var count = preferences.WorkspaceFolders.Count;
            forget.IsEnabled = count > 0;
            folderStatus.Text = count == 0 ? "No destination folders are remembered."
                : $"Destination folders remembered for {count} {(count == 1 ? "workspace" : "workspaces")}. Forgetting them never removes files.";
        }
        void Rebuild()
        {
            fields.Children.Clear();
            var kind = Selected();
            var preset = Read(kind);
            void Set(Func<ExportPreset, ExportPreset> change)
            {
                preferences.Formats[kind.ToString()] = change(Read(kind));
                reset.IsEnabled = true;
            }
            if (kind == ExportFormat.Pdf)
            {
                var paper = new ComboBox
                {
                    ItemsSource = Enum.GetValues<PdfPageSize>(), ItemTemplate = EnumChoices.Template<PdfPageSize>(),
                    SelectedItem = Enum.TryParse<PdfPageSize>(preset.Paper, out var page) && Enum.IsDefined(page) ? page : PdfPageSize.A4,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                AutomationProperties.SetName(paper, "Default PDF paper size");
                paper.SelectionChanged += (_, _) => { if (paper.SelectedItem is PdfPageSize value) Set(p => p with { Paper = value.ToString() }); };
                var margin = DialogParts.Number((double)Math.Clamp(preset.Margin, 10, 40), 10, 40, 1,
                    value => Set(p => p with { Margin = (decimal)value }));
                AutomationProperties.SetName(margin, "Default PDF margin in millimetres");
                var numbers = Check("Show page numbers by default", "Default PDF page numbers", preset.PageNumbers,
                    value => Set(p => p with { PageNumbers = value }));
                fields.Children.Add(Field("Paper size", paper));
                fields.Children.Add(Field("Margins on each side (mm)", margin));
                fields.Children.Add(numbers);
            }
            else if (kind == ExportFormat.Html)
            {
                var appearance = new ComboBox
                {
                    ItemsSource = Enum.GetValues<ExportTheme>(), ItemTemplate = EnumChoices.Template<ExportTheme>(),
                    SelectedItem = Enum.TryParse<ExportTheme>(preset.Theme, out var theme) && Enum.IsDefined(theme)
                        ? theme : Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? ExportTheme.Dark : ExportTheme.Light,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                AutomationProperties.SetName(appearance, "Default HTML appearance");
                appearance.SelectionChanged += (_, _) => { if (appearance.SelectedItem is ExportTheme value) Set(p => p with { Theme = value.ToString() }); };
                var language = new TextBox { Text = preset.Language ?? "en", PlaceholderText = "For example en, sv or en-GB" };
                AutomationProperties.SetName(language, "Default HTML document language");
                language.PropertyChanged += (_, change) =>
                {
                    if (change.Property == TextBox.TextProperty)
                        Set(p => p with { Language = string.IsNullOrWhiteSpace(language.Text) ? "en" : language.Text.Trim() });
                };
                fields.Children.Add(Field("Appearance", appearance));
                fields.Children.Add(Field("Document language", language));
            }
            if (kind != ExportFormat.Text)
                fields.Children.Add(Check("Include front matter properties by default", $"Default {(kind == ExportFormat.Pdf ? "PDF" : "HTML")} front matter properties",
                    preset.IncludeFrontMatter, value => Set(p => p with { IncludeFrontMatter = value })));
            else
                fields.Children.Add(GuideParts.Body("Plain text has no paper, colour or page-number options. Text exports omit the front matter properties table. Layout preferences here never change your Markdown."));
            reset.IsEnabled = preferences.Formats.ContainsKey(kind.ToString());
        }
        format.SelectionChanged += (_, _) => Rebuild();
        reset.Click += (_, _) => { preferences.Formats.Remove(Selected().ToString()); Rebuild(); };
        forget.Click += (_, _) => { preferences.WorkspaceFolders.Clear(); UpdateFolders(); };
        Rebuild();
        UpdateFolders();
        return GuideParts.Vertical(
            GuideParts.Body("Choose the starting options for each export format. Successful exports also remember their latest options. These pending changes take effect with Apply or Done; Cancel keeps the saved defaults."),
            Field("Export format", format), fields, GuideParts.ButtonRow(reset),
            GuideParts.Caption("Resetting a format restores its normal options; HTML without saved options starts with the current application appearance."),
            folderStatus, GuideParts.ButtonRow(forget));
    }

    private static Control Field(string label, Control editor) =>
        GuideParts.Vertical(new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, editor);

    private static CheckBox Check(string title, string name, bool value, Action<bool> changed)
    {
        var control = new CheckBox { Content = title, IsChecked = value };
        AutomationProperties.SetName(control, name);
        control.IsCheckedChanged += (_, _) => changed(control.IsChecked == true);
        return control;
    }
}
