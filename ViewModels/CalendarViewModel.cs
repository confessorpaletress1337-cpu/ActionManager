using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ActionManager.ViewModels;

/// <summary>
/// Месячная сетка календаря (недели с понедельника, как в русских календарях).
/// Само расписание сюда приходит через <see cref="RefreshCalendar"/> из MainWindowViewModel.
/// </summary>
public partial class CalendarViewModel : ViewModelBase
{
    /// <summary>Контекст, по которому красим дни: включён ли планировщик и какие дни отменены.</summary>
    public sealed record CalendarContext(bool ScheduleEnabled, System.Collections.Generic.IReadOnlySet<DateOnly> SkippedDates);

    private readonly ObservableCollection<CalendarDayViewModel> _cells = new();
    private DateOnly _currentMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private CalendarDayViewModel? _selectedDay;
    private CalendarContext _context = new(false, new HashSet<DateOnly>());

    public event Action<CalendarDayViewModel?>? SelectionChanged;

    public CalendarViewModel()
    {
        RebuildCells();
        SelectedDay = FindToday();
    }

    public ReadOnlyObservableCollection<CalendarDayViewModel> Cells { get; private set; } = null!;

    public CalendarDayViewModel? SelectedDay
    {
        get => _selectedDay;
        set
        {
            if (SetProperty(ref _selectedDay, value))
            {
                RefreshSelection();
                OnPropertyChanged(nameof(SelectedStateText));
                SelectionChanged?.Invoke(value);
            }
        }
    }

    public string MonthTitle
    {
        get
        {
            var raw = _currentMonth.ToString("MMMM yyyy", Format.Ru);
            return char.ToUpperInvariant(raw[0]) + raw[1..];
        }
    }

    public string SelectedStateText
    {
        get
        {
            if (SelectedDay is not { } day || day.IsPlaceholder)
                return "Выберите день в календаре, чтобы управлять выключением.";

            var today = DateOnly.FromDateTime(DateTime.Today);
            if (day.Date < today)
                return "День уже прошёл — отменять нечего.";

            if (!_context.ScheduleEnabled)
                return "Планировщик сейчас отключён, но отмену дня можно пометить заранее.";

            return day.IsCanceled
                ? "Выключение в этот день отменено."
                : "Выключение в этот день запланировано.";
        }
    }

    // ---------------------------------------------------------------------------

    public void RefreshCalendar(CalendarContext context)
    {
        _context = context;
        Recolor();
    }

    /// <summary>Обновить окраску дней без пересоздания коллекции (не дёргаем UI зря).</summary>
    public void Recolor()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        foreach (var cell in Cells)
        {
            if (cell.IsPlaceholder)
                continue;
            cell.ApplyState(ResolveState(cell.Date, today), cell.Date == today);
        }
        OnPropertyChanged(nameof(SelectedStateText));
    }

    private DayCellState ResolveState(DateOnly date, DateOnly today)
    {
        if (date < today)
            return DayCellState.Past;
        if (!_context.ScheduleEnabled)
            return DayCellState.Off;
        return _context.SkippedDates.Contains(date) ? DayCellState.Canceled : DayCellState.Planned;
    }

    // ---------------------------------------------------------------------------

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void PrevMonth()
    {
        _currentMonth = _currentMonth.AddMonths(-1);
        RebuildCells();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void NextMonth()
    {
        _currentMonth = _currentMonth.AddMonths(1);
        RebuildCells();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void GoToToday()
    {
        _currentMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        RebuildCells();
        SelectedDay = FindToday();
    }

    internal void SelectDay(CalendarDayViewModel day)
    {
        if (day.IsPlaceholder)
            return;
        SelectedDay = day;
    }

    // ---------------------------------------------------------------------------

    private CalendarDayViewModel? FindToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (today.Year == _currentMonth.Year && today.Month == _currentMonth.Month)
        {
            foreach (var cell in Cells)
                if (!cell.IsPlaceholder && cell.Date == today)
                    return cell;
        }
        return null;
    }

    /// <summary>Перестроить сетку текущего месяца: отступ на понедельник + пустышки до кратности 7.</summary>
    private void RebuildCells()
    {
        _cells.Clear();
        var firstDay = new DateOnly(_currentMonth.Year, _currentMonth.Month, 1);
        var daysInMonth = System.DateTime.DaysInMonth(firstDay.Year, firstDay.Month);

        // понедельник = первый день недели
        var offset = ((int)firstDay.DayOfWeek + 6) % 7;
        for (var i = 0; i < offset; i++)
            _cells.Add(new CalendarDayViewModel(this, default, isPlaceholder: true));

        var today = DateOnly.FromDateTime(DateTime.Today);
        for (var day = 1; day <= daysInMonth; day++)
        {
            var date = new DateOnly(firstDay.Year, firstDay.Month, day);
            var cell = new CalendarDayViewModel(this, date, isPlaceholder: false);
            cell.ApplyState(ResolveState(date, today), date == today);
            _cells.Add(cell);
        }

        // добиваем до полного количества недель
        while (_cells.Count % 7 != 0)
            _cells.Add(new CalendarDayViewModel(this, default, isPlaceholder: true));

        Cells = new ReadOnlyObservableCollection<CalendarDayViewModel>(_cells);
        OnPropertyChanged(nameof(Cells));
        OnPropertyChanged(nameof(MonthTitle));

        // восстановить выделение, если день по-прежнему виден
        if (_selectedDay is { } selected)
        {
            _selectedDay = null;
            foreach (var cell in _cells)
                if (!cell.IsPlaceholder && cell.Date == selected.Date)
                {
                    SelectedDay = cell;
                    return;
                }
        }
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        foreach (var cell in _cells)
            cell.SetSelected(ReferenceEquals(cell, _selectedDay));
    }
}
