using System;
using Avalonia.Interactivity;
using ValheimServerGUI.App.Infrastructure;
using ValheimServerGUI.App.ViewModels.Dialogs;

namespace ValheimServerGUI.App.Views.Dialogs;

public partial class ConnectionCheckWindow : DialogWindow
{
    public ConnectionCheckWindow()
    {
        InitializeComponent();
    }

    public ConnectionCheckWindow(ConnectionCheckViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CopyToClipboard = text => ClipboardHelper.CopyTextAsync(this, text);
        Opened += async (_, _) => await viewModel.RunAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
