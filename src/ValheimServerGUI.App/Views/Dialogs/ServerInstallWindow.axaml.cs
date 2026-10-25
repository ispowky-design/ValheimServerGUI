using System;
using Avalonia.Interactivity;
using ValheimServerGUI.App.ViewModels.Dialogs;

namespace ValheimServerGUI.App.Views.Dialogs;

public partial class ServerInstallWindow : DialogWindow
{
    public ServerInstallWindow()
    {
        InitializeComponent();
    }

    public ServerInstallWindow(ServerInstallViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.ErrorReported = msg => _ = MessageBox.ShowAsync(this, Localization.Strings.Prompt_SteamCmdFailed_Title, msg);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Closing mid-run cancels SteamCMD rather than leaving it running unseen.
    protected override void OnClosed(EventArgs e)
    {
        (DataContext as ServerInstallViewModel)?.Dispose();
        base.OnClosed(e);
    }
}
