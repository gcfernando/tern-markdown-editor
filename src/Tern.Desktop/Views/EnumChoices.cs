using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Tern.App.Presentation;

namespace Tern.Desktop.Views;

internal static class EnumChoices
{
    internal static FuncDataTemplate<T> Template<T>() where T : struct, Enum =>
        new((value, _) => new TextBlock { Text = EnumDisplayNames.For(value), TextWrapping = TextWrapping.Wrap });
}
