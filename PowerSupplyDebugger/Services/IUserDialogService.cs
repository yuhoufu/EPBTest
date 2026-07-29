namespace PowerSupplyDebugger.Services;

public interface IUserDialogService
{
    bool Confirm(string title, string message, bool dangerous = false);
    void ShowInformation(string title, string message);
    void ShowWarning(string title, string message);
    void ShowError(string title, string message);
}
