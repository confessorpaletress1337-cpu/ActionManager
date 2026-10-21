using ActionManager.Hosting;
using ActionManager.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace ActionManager;

public partial class App : Application
{
    public static void ActivateMainWindow()
    {
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } window)
            return;

        if (!window.IsVisible)
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection()
                .AddServices()
                .BuildServiceProvider();

            var mainWindow = services.GetRequiredService<Views.MainWindow>();
            var viewModel = services.GetRequiredService<MainWindowViewModel>();

            mainWindow.DataContext = viewModel;
            desktop.MainWindow = mainWindow;

            viewModel.StartUiTimer();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
