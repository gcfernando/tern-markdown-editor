namespace Tern.App.Shell;

/// <summary>An optional button on a notification banner, such as Retry, Rescan or Details.</summary>
public sealed record BannerAction(string Label, Action Invoke);

/// <summary>A persistent, non-modal notice for a background problem (SB-01).</summary>
public sealed record BannerState(string Text, StatusSeverity Severity, IReadOnlyList<BannerAction> Actions);

public sealed partial class ShellViewModel
{
    public BannerState? Banner { get; private set; }
    public event EventHandler? BannerChanged;

    /// <summary>
    /// Shows a non-modal banner that stays until <see cref="DismissBanner"/> or a replacement. The text is also
    /// mirrored into Activity. Safe to call from any thread.
    /// </summary>
    public void ShowBanner(string text, StatusSeverity severity, params BannerAction[] actions)
        => ShowBanner(text, severity, null, actions);

    public void ShowBanner(string text, StatusSeverity severity, string? details, params BannerAction[] actions)
    {
        if (UiDispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Post(() => { if (!_disposed) ApplyBanner(text, severity, actions, details); });
            return;
        }
        ApplyBanner(text, severity, actions, details);
    }

    public void DismissBanner()
    {
        if (UiDispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Post(() => { if (!_disposed) ApplyBanner(null, StatusSeverity.Info, []); });
            return;
        }
        ApplyBanner(null, StatusSeverity.Info, []);
    }

    private void ApplyBanner(string? text, StatusSeverity severity, BannerAction[] actions, string? details = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (Banner is null) return;
            Banner = null;
        }
        else
        {
            Banner = new BannerState(text, severity, actions);
            Activities.Post(text, severity switch
            {
                StatusSeverity.Error => ActivitySeverity.Error,
                StatusSeverity.Warning => ActivitySeverity.Warning,
                StatusSeverity.Success => ActivitySeverity.Success,
                _ => ActivitySeverity.Neutral,
            }, actions, details);
        }
        OnPropertyChanged(nameof(Banner));
        BannerChanged?.Invoke(this, EventArgs.Empty);
    }
}
