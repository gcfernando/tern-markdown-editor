using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Tern.Desktop.Services;

namespace Tern.Desktop.Views;

/// <summary>The "Characters to send at one time" field shared by the AI connection screens.</summary>
internal sealed class SendLimitField
{
    internal const string LabelText = "Characters to send at one time";
    internal const string TipText = "Tip: If this AI says your text is too large, use a smaller number.";

    public TextBox Input { get; } = new() { MaxLength = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    public MessageBar Message { get; } = new();
    public Control View { get; }
    public event Action? Changed;

    public SendLimitField(bool strongLabel = false)
    {
        var label = GuideParts.Body(LabelText);
        if (strongLabel) label.FontWeight = FontWeight.SemiBold;
        AutomationProperties.SetName(Input, LabelText);
        AutomationProperties.SetLabeledBy(Input, label);
        AutomationProperties.SetName(Message.Label, "Characters to send message");
        View = GuideParts.Vertical(label, Input, Message, GuideParts.Caption(TipText));
        Input.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            Refresh();
            Changed?.Invoke();
        };
        Set(AIConnectionRules.DefaultSendCharacters);
    }

    /// <summary>The saved number, or null while the field holds something that cannot be saved.</summary>
    public int? Value => AIConnectionRules.TryParseSendCharacters(Input.Text, out var value) ? value : null;
    public string? Problem => Value is null ? AIConnectionRules.SendCharactersProblem : null;

    public void Set(int value) => Input.Text = AIConnectionRules.FormatSendCharacters(value);

    private void Refresh()
    {
        var problem = Problem;
        var wasShown = Message.IsVisible;
        Input.Classes.Set("rename-error", problem is not null);
        if (problem is null) { Message.Clear(); return; }
        Message.Show(MessageTone.Error, problem);
        // The field can sit at the bottom of a scrolling form, so make sure the reason is on screen.
        if (!wasShown) Dispatcher.UIThread.Post(() => { if (Message.IsVisible) View.BringIntoView(); }, DispatcherPriority.Loaded);
    }
}
