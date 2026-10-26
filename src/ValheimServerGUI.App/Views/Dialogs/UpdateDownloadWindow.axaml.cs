using System;
using Avalonia.Interactivity;
using ValheimServerGUI.App.ViewModels.Dialogs;

namespace ValheimServerGUI.App.Views.Dialogs;

/// <summary>Downloads an update with a progress bar. Closes with the downloaded file, or with null after a failure
/// the user has read.</summary>
public partial class UpdateDownloadWindow : DialogWindow
{
    public UpdateDownloadWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    public UpdateDownloadWindow(UpdateDownloadViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is UpdateDownloadViewModel vm && await vm.RunAsync())
            Close(vm.DownloadedFile);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(null);
}
