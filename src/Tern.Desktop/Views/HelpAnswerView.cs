using Avalonia.Controls;
using Tern.Rendering.Preview;

namespace Tern.Desktop.Views;

/// <summary>A bounded native Markdown answer. It has no file roots or network image loader.</summary>
internal sealed class HelpAnswerView : Decorator
{
    internal Tern.PreviewView.PreviewView Preview { get; } = new() { UseFullWidth = true };
    private string? _text;
    private int _version;
    internal string? Text
    {
        get => _text;
        set { _version++; _text = value; Preview.Document = null; IsVisible = false; }
    }
    public HelpAnswerView() { Child = Preview; IsVisible = false; }

    internal async Task<bool> ShowAsync(string markdown, CancellationToken token, Func<bool> current)
    {
        var version = ++_version;
        var document = await Task.Run(() => PreviewBuilder.Build(markdown, new PreviewOptions
        {
            MaxSourceLength = 150_000, MaxNodes = 25_000, MaxBuildTime = TimeSpan.FromSeconds(2),
            Resolver = new() { AllowDataImages = false, ImageExtensions = new HashSet<string>() }
        }), token);
        token.ThrowIfCancellationRequested();
        if (version != _version || !current()) return false;
        _text = markdown; Preview.Document = document; IsVisible = true; Preview.ScrollToTop();
        return true;
    }
}
