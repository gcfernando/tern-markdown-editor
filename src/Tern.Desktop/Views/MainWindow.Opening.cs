namespace Tern.Desktop.Views;

public partial class MainWindow
{
    internal async Task OpenDocumentPathAsync(string path, int line = 0, bool askDuplicate = false)
    {
        if (line > 0) await _shell.GoToAsync(path, line, 1);
        else await _shell.OpenFileAsync(path, duplicateBehavior: askDuplicate ? Tern.App.Shell.DuplicateOpenBehavior.Ask : Tern.App.Shell.DuplicateOpenBehavior.Activate);
    }
}
