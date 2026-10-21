using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ActionManager.ViewModels;

/// <summary>Состояние одного дня календаря (для окраски ячейки).</summary>
public enum DayCellState
{
    /// <summary>Прошедший день — приглушён.</summary>
    Past,

    /// <summary>Будущий день, планировщик отключён — нейтральный.</summary>
    Off,

    /// <summary>Будущий день, выключение запланировано.</summary>
    Planned,

    /// <summary>Будущий день, выключение отменено пользователем.</summary>
    Canceled,
}

/// <summary>Ячейка календаря: реальный день или пустышка для выравнивания сетки.</summary>
public partial class CalendarDayViewModel : ViewModelBase
{
    private readonly CalendarViewModel _owner;
    private DayCellState _state;
    private bool _isToday;
    private bool _isSelected;

    public CalendarDayViewModel(CalendarViewModel owner, DateOnly date, bool isPlaceholder)
    {
        _owner = owner;
        Date = date;
        IsPlaceholder = isPlaceholder;
    }

    public DateOnly Date { get; }

    public bool IsPlaceholder { get; }

    public int DayNumber => Date.Day;

    public DayCellState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPast));
                OnPropertyChanged(nameof(IsPlanned));
                OnPropertyChanged(nameof(IsCanceled));
                OnPropertyChanged(nameof(IsOff));
                OnPropertyChanged(nameof(ToolTipText));
            }
        }
    }

    public bool IsToday
    {
        get => _isToday;
        private set => SetProperty(ref _isToday, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        private set => SetProperty(ref _isSelected, value);
    }

    public bool IsPast => State == DayCellState.Past;
    public bool IsPlanned => State == DayCellState.Planned;
    public bool IsCanceled => State == DayCellState.Canceled;
    public bool IsOff => State == DayCellState.Off;

    public bool IsSelectable => !IsPlaceholder;

    public string ToolTipText
    {
        get
        {
            var date = Date.ToString("d MMMM yyyy", Format.Ru);
            return IsCanceled
                ? $"{date} — выключение отменено"
                : IsPlanned
                    ? $"{date} — выключение запланировано"
                    : date;
        }
    }

    internal void ApplyState(DayCellState state, bool isToday)
    {
        State = state;
        IsToday = isToday;
    }

    internal void SetSelected(bool selected) => IsSelected = selected;

    [RelayCommand]
    private void Select() => _owner.SelectDay(this);
}

/// <summary>Форматирование по-русски независимо от локали Windows.</summary>
public static class Format
{
    public static readonly System.Globalization.CultureInfo Ru = new("ru-RU");
}
