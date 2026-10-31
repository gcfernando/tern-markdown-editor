using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using System.Text;
using Tern.Rendering.Resources;

namespace Tern.PreviewView.Tests;

public sealed class ImageAccessibilityTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Image_peer_remains_stable_across_loading_and_completion(bool success)
    {
        var response = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var image = new ImageView(new Blocked(BlockReason.RemoteResourceBlocked, "https://example.com/badge.svg", "Host retrieval required"),
            "Readable badge", null, remoteImageLoader: (_, _) => response.Task);
        var window = new Window { Content = image, Width = 300, Height = 200 };
        try
        {
            window.Show();
            Harness.Pump(window);
            var peer = ControlAutomationPeer.CreatePeerForElement(image);
            Assert.Equal(ImageState.Loading, image.State);
            Assert.Equal("Loading", peer.GetItemStatus());
            Assert.Equal("Image: Readable badge", peer.GetName());
            Assert.Empty(peer.GetChildren());
            response.SetResult(success ? Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='40' height='20'><rect width='40' height='20' fill='red'/></svg>") : null);
            for (var i = 0; i < 200 && image.State == ImageState.Loading; i++) await Task.Delay(10);
            Assert.Equal(success ? ImageState.Loaded : ImageState.Unavailable, image.State);
            Assert.Equal(success ? "Loaded" : "Unavailable", peer.GetItemStatus());
            Assert.Same(peer, ControlAutomationPeer.CreatePeerForElement(image));
            Assert.Equal(AutomationControlType.Image, peer.GetAutomationControlType());
            Assert.Equal("Image: Readable badge", peer.GetName());
            Assert.Empty(peer.GetChildren());
        }
        finally { window.Close(); await image.LoadingCompletion.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [AvaloniaFact]
    public void Mixed_image_paragraph_exposes_alt_text_without_changing_link_offsets()
    {
        const string markdown = "Before [![First badge](https://example.com/one.svg)](https://example.com/target) between ![Second badge](https://example.com/two.png) after.";
        var (window, view) = Harness.Show(Harness.Build(markdown));
        try
        {
            var block = Assert.Single(Harness.All<RichTextBlock>(view), b => b.Links.Count == 1);
            var originalText = block.Inlines!.Text;
            var link = Assert.Single(block.Links);
            Assert.Equal(7, link.Start);
            Assert.Equal(1, link.Length);
            Assert.Equal("Before First badge between Second badge after.", ControlAutomationPeer.CreatePeerForElement(block).GetName());
            Assert.Equal(originalText, block.Inlines.Text);
            Assert.Contains('\uFFFC', originalText!);
            Assert.Equal(link, Assert.Single(block.Links));
            foreach (var image in Harness.All<ImageView>(view))
            {
                var peer = ControlAutomationPeer.CreatePeerForElement(image);
                Assert.Equal(AutomationControlType.Image, peer.GetAutomationControlType());
                Assert.Equal("Image: " + image.Alt, peer.GetName());
                Assert.Empty(peer.GetChildren());
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Explicit_heading_name_takes_precedence_over_inline_content()
    {
        var (window, view) = Harness.Show(Harness.Build("## Heading **text**"));
        try
        {
            var block = Assert.Single(Harness.All<RichTextBlock>(view));
            Assert.Equal("Heading level 2: Heading text", ControlAutomationPeer.CreatePeerForElement(block).GetName());
            AutomationProperties.SetName(block, "Custom accessible heading");
            Assert.Equal("Custom accessible heading", ControlAutomationPeer.CreatePeerForElement(block).GetName());
        }
        finally { window.Close(); }
    }
}
