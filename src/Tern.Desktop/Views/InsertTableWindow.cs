using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Tern.Desktop.Views;

public sealed record TableDimensions(int Rows, int Columns);

public sealed class InsertTableWindow : Window
{
    internal TextBox RowsInput { get; } = new() { Text = "3", Width = 120 };
    internal TextBox ColumnsInput { get; } = new() { Text = "3", Width = 120 };
    internal Button InsertButton { get; } = new() { Content = "Insert table", IsDefault = true };
    private readonly TextBlock _validation = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Grid _preview = new() { ColumnSpacing = 3, RowSpacing = 3 };

    public InsertTableWindow(bool replacesSelection = false)
    {
        DialogParts.Setup(this, "Insert Table", 540, 460);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        InsertButton.Click += (_, _) => { if (Dimensions is { } dimensions) Close(dimensions); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(InsertButton);
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Rows include the header. Choose 1–100 rows and 1–20 columns.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = replacesSelection ? "This table will replace the selected text." : "The table will be inserted at the cursor.", TextWrapping = TextWrapping.Wrap },
                DialogParts.Labeled("Rows (including header)", RowsInput),
                DialogParts.Labeled("Columns", ColumnsInput), _validation, _summary, _preview,
                new TextBlock { Text = "Edit Column 1, Column 2… headings in Source after insertion. Preview shows at most 6 rows and 6 columns.", TextWrapping = TextWrapping.Wrap }, buttons,
            },
        };
        AutomationProperties.SetName(InsertButton, "Insert table");
        // TextChanged is deferred by TextBox. Availability must follow the text
        // property immediately, including keyboard/default-button activation.
        RowsInput.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) Validate(); };
        ColumnsInput.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) Validate(); };
        Opened += (_, _) => { RowsInput.Focus(); RowsInput.SelectAll(); };
        Validate();
    }

    internal TableDimensions? Dimensions => int.TryParse(RowsInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var rows)
        && int.TryParse(ColumnsInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var columns)
        && rows is >= 1 and <= 100 && columns is >= 1 and <= 20 ? new(rows, columns) : null;

    private void Validate()
    {
        InsertButton.IsEnabled = Dimensions is not null;
        _validation.Text = InsertButton.IsEnabled ? string.Empty : "Enter whole numbers: 1–100 rows and 1–20 columns.";
        _validation.IsVisible = !InsertButton.IsEnabled;
        if (Dimensions is { } dimensions)
        {
            _preview.Children.Clear(); _preview.ColumnDefinitions.Clear(); _preview.RowDefinitions.Clear();
            _summary.Text = $"{dimensions.Columns} columns, {dimensions.Rows - 1} body rows + header";
            for (var c = 0; c < Math.Min(6, dimensions.Columns); c++) _preview.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            for (var r = 0; r < Math.Min(6, dimensions.Rows); r++)
            {
                _preview.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (var c = 0; c < Math.Min(6, dimensions.Columns); c++)
                {
                    var cell = new Border { BorderThickness = new Avalonia.Thickness(1), Padding = new Avalonia.Thickness(4), MinHeight = 22,
                        Child = new TextBlock { Text = r == 0 ? "Column " + (c + 1) : "", TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = r == 0 ? FontWeight.SemiBold : FontWeight.Normal } };
                    cell.Bind(Border.BorderBrushProperty, this.GetResourceObservable("Tern.Border"));
                    Grid.SetRow(cell, r); Grid.SetColumn(cell, c); _preview.Children.Add(cell);
                }
            }
        }
        if (IsVisible) DialogParts.FitContentHeight(this, 460);
    }
}
