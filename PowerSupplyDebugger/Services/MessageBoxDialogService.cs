using System.Windows;

namespace PowerSupplyDebugger.Services;

public sealed class MessageBoxDialogService : IUserDialogService
{
    public bool Confirm(string title, string message, bool dangerous = false) =>
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            dangerous ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowInformation(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowWarning(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
