using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tern.App.Documents;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private void OnReadingNavigationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Control { Tag: string action }) return;
        if (action == "top") Preview.ScrollToTop();
        else Preview.ScrollByPage(action == "up" ? -1 : 1);
        Preview.Focus();
    }
    private enum ScrollDragOwner { None, Source, Preview }
    private ScrollDragOwner _scrollDragOwner;
    private IPointer? _scrollDragPointer;
    private int _scrollDragGeneration;
    private bool _sourceScrollSyncQueued;
    private DocumentTab? _sourceScrollTab;
    private long _sourceScrollRevision;

    private void QueueSourceToPreviewScroll()
    {
        if (_shown is not { } sourceTab) return;
        _sourceScrollTab = sourceTab;
        _sourceScrollRevision = sourceTab.Session.Revision;
        if (_sourceScrollSyncQueued) return;
        _sourceScrollSyncQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _sourceScrollSyncQueued = false;
            var tab = _sourceScrollTab;
            var revision = _sourceScrollRevision;
            _sourceScrollTab = null;
            if (tab is null || _windowClosed || _switchingDocument || _syncingScroll || !ReferenceEquals(tab, _shown)
                || !tab.Session.IsCurrent(revision) || !ReferenceEquals(_renderedPreviewTab, tab)
                || _renderedPreviewRevision != revision || !_shell.Settings.SyncScroll
                || _shell.ViewMode != Tern.App.Settings.ViewMode.Split || _scrollDragOwner == ScrollDragOwner.Preview) return;
            // Preview anchor realization can run layout. Do it after the native
            // Thumb/Track drag handler has finished recording its drag delta.
            ScrollPreviewTo(EditorTopLine());
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void WireScrollInteraction()
    {
        AddHandler(PointerPressedEvent, OnScrollbarPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnScrollbarPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnScrollbarCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnScrollbarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual visual || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || visual.GetSelfAndVisualAncestors().OfType<ScrollBar>().FirstOrDefault() is not { } bar) return;
        var ancestors = bar.GetVisualAncestors().ToArray();
        var owner = ancestors.Contains(Editor) ? ScrollDragOwner.Source
            : ancestors.Contains(Preview) ? ScrollDragOwner.Preview : ScrollDragOwner.None;
        if (owner == ScrollDragOwner.None) return;
        ++_scrollDragGeneration;
        _scrollDragPointer = e.Pointer;
        _scrollDragOwner = owner;
    }

    private void OnScrollbarPointerReleased(object? sender, PointerReleasedEventArgs e) => EndScrollbarDrag(e.Pointer);
    private void OnScrollbarCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndScrollbarDrag(e.Pointer);

    private void EndScrollbarDrag(IPointer pointer)
    {
        if (!ReferenceEquals(pointer, _scrollDragPointer)) return;
        var generation = _scrollDragGeneration;
        // Let the release's layout/scroll notifications finish before accepting
        // reverse synchronization. A later press owns its own generation.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (generation != _scrollDragGeneration) return;
            _scrollDragOwner = ScrollDragOwner.None;
            _scrollDragPointer = null;
        }, Avalonia.Threading.DispatcherPriority.Background);
    }
}
