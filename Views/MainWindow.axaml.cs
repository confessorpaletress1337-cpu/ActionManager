using ActionManager.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace ActionManager.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void BrowsePoolFiles_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Добавить программы или проекты в рабочий пул",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Программы, проекты и ярлыки")
                {
                    Patterns = new[] { "*.exe", "*.lnk", "*.sln", "*.slnx", "*.cmd", "*.bat" },
                },
                FilePickerFileTypes.All,
            },
        });

        if (DataContext is not MainWindowViewModel viewModel)
            return;
        foreach (var file in files)
            viewModel.AddLaunchPoolTarget(file.Path.LocalPath);
    }

    private async void BrowsePoolFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Добавить папку в рабочий пул",
            AllowMultiple = true,
        });

        if (DataContext is not MainWindowViewModel viewModel)
            return;
        foreach (var folder in folders)
            viewModel.AddLaunchPoolTarget(folder.Path.LocalPath);
    }

    private void RemovePoolItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LaunchPoolItemViewModel item }
            && DataContext is MainWindowViewModel viewModel)
            viewModel.RemoveLaunchPoolItem(item);
    }
}
