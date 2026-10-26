using ActionManager.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ActionManager.ViewModels;

public partial class LaunchPoolItemViewModel : ViewModelBase
{
    public LaunchPoolItemViewModel(LaunchPoolEntry model)
    {
        Model = model;
        _name = model.Name;
        _target = model.Target;
        _arguments = model.Arguments;
        _workingDirectory = model.WorkingDirectory;
        _skipIfRunning = model.SkipIfRunning;
        _isEnabled = model.Enabled;
    }

    public LaunchPoolEntry Model { get; }
    public event System.Action? Changed;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _target;

    [ObservableProperty]
    private string _arguments;

    [ObservableProperty]
    private string _workingDirectory;

    [ObservableProperty]
    private bool _skipIfRunning;

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnNameChanged(string value)
    {
        Model.Name = value;
        Changed?.Invoke();
    }

    partial void OnTargetChanged(string value)
    {
        Model.Target = value;
        Changed?.Invoke();
    }

    partial void OnArgumentsChanged(string value)
    {
        Model.Arguments = value;
        Changed?.Invoke();
    }

    partial void OnWorkingDirectoryChanged(string value)
    {
        Model.WorkingDirectory = value;
        Changed?.Invoke();
    }

    partial void OnSkipIfRunningChanged(bool value)
    {
        Model.SkipIfRunning = value;
        Changed?.Invoke();
    }

    partial void OnIsEnabledChanged(bool value)
    {
        Model.Enabled = value;
        Changed?.Invoke();
    }
}
