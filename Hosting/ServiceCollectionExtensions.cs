using ActionManager.Services;
using ActionManager.ViewModels;
using ActionManager.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ActionManager.Hosting;

/// <summary>
/// Регистрация всех сервисов и ViewModel приложения (никаких ViewLocator).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddServices(this IServiceCollection services)
    {
        // инфраструктура
        services.AddSingleton<IOptionsStore, JsonOptionsStore>();
        services.AddSingleton<ISystemCommandsService, SystemCommandsService>();
        services.AddSingleton<ISchedulerService, TaskSchedulerService>();

        // view-модель и окно
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<Views.MainWindow>();

        return services;
    }
}
