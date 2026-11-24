using Avalonia.Automation;
using Avalonia.Controls;
using Tern.Rendering.Resources;

namespace Tern.PreviewView;

public sealed class SourceImageThumbnail : Border, IDisposable
{
    private readonly ImageView _image;

    public SourceImageThumbnail(SafeResourceResolver resolver, string destination, string alt)
    {
        _image = new ImageView(resolver.ResolveImage(destination), alt, null) { MaxWidth = 160, MaxHeight = 96 };
        Child = _image;
        Width = 164;
        Height = 100;
        MaxWidth = 164;
        MaxHeight = 100;
        ClipToBounds = true;
        AutomationProperties.SetName(this, "Source image: " + alt);
    }

    public bool IsLoaded => _image.State == ImageState.Loaded;
    public void Dispose() => _image.Dispose();
}
