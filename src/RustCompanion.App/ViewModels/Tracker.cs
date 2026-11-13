using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RustCompanion.Core;
using RustCompanion.Core.Models;
using RustCompanion.Core.Services;

namespace RustCompanion.App.ViewModels;

public sealed partial class TimerItemViewModel : ObservableObject
{
    private readonly TrackerViewModel _parent;
    public TimerEntry Entry { get; }
    public ObservableCollection<string> History { get; } = new();

    [ObservableProperty] private string _remainingText = "";
    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private string _dueText = "";
    [ObservableProperty] private string _note;

    public TimerItemViewModel(TimerEntry e, TrackerViewModel parent) { Entry = e; _parent = parent; _note = e.Note; }

    public string Name => Entry.Name;
    public string IntervalText => $"Интервал: {Entry.IntervalSeconds / 60.0:0.##} мин, предупреждение за {Entry.LeadSeconds / 60.0:0.##} мин";
    public string StatusLabel => DataStatus.Estimate.Ru() + " — по введённым вами данным, не серверный таймер";
    public string StartLabel => _parent.StartLabel;
    public string MarkLabel => _parent.MarkLabel;

    public static string Fmt(TimeSpan t) => $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";

    public void Refresh(DateTimeOffset now)
    {
        var rem = TimerCalculator.Remaining(Entry, now);
        var phase = TimerCalculator.Phase(Entry, now);
        PhaseText = phase switch { TimerPhase.Idle => "Не запущен", TimerPhase.Running => "Идёт отсчёт", TimerPhase.LeadWindow => "Скоро", _ => "Время наступило" };
        RemainingText = rem is null ? "—" : rem.Value <= TimeSpan.Zero ? "Прошло " + Fmt(-rem.Value) : Fmt(rem.Value);
        var due = TimerCalculator.DueAt(Entry);
        DueText = due is null ? "" : "Расчётное время: " + due.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }

    public async Task LoadHistoryAsync(ITimerRepository repo)
    {
        var items = await repo.HistoryAsync(Entry.Id, 5);
        History.Clear();
        foreach (var h in items)
            History.Add($"{h.AtUtc.ToLocalTime():g} — {h.Action}{(string.IsNullOrEmpty(h.Note) ? "" : ": " + h.Note)}");
    }

    [RelayCommand] private Task StartAsync() => _parent.StartItemAsync(this);
    [RelayCommand] private Task ResetAsync() => _parent.ResetItemAsync(this);
    [RelayCommand] private Task MarkAsync() => _parent.MarkItemAsync(this);
    [RelayCommand] private Task SaveNoteAsync() => _parent.SaveNoteAsync(this);
    [RelayCommand] private Task DeleteAsync() => _parent.DeleteItemAsync(this);
}

public sealed partial class TrackerViewModel : ObservableObject
{
    private readonly TimerService _svc;
    private readonly ITimerRepository _repo;
    private readonly IClock _clock;
    private readonly bool _restartOnMark;

    public TrackerKind Kind { get; }
    public string Title { get; }
    public string Hint { get; }
    public string StartLabel { get; }
    public string MarkLabel { get; }
    public ObservableCollection<TimerItemViewModel> Items { get; } = new();

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newIntervalMinutes = "";
    [ObservableProperty] private string _newLeadMinutes = "0";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _isEmpty = true;

    public TrackerViewModel(TrackerKind kind, TimerService svc, ITimerRepository repo, IClock clock)
    {
        Kind = kind; _svc = svc; _repo = repo; _clock = clock;
        (Title, Hint, StartLabel, MarkLabel, _restartOnMark, _newName) = kind switch
        {
            TrackerKind.Monument => ("Мониторинг РТ", "Ручной таймер респавна лута. Интервал вы задаёте сами — он зависит от сервера и патча. Это оценка, а не данные сервера.", "Начать отсчёт", "Лут собран (новый отсчёт)", true, ""),
            TrackerKind.DeepSea => ("Deep Sea", "Ручные отметки: когда началось событие и когда оно закончилось. Приложение не читает данные игры.", "Событие началось", "Событие завершено", false, "Deep Sea"),
            TrackerKind.PatrolHelicopter => ("Патрульный вертолёт", "Ручная отметка появления или уничтожения. Интервал — ваша оценка.", "Отметить появление", "Уничтожен (новый отсчёт)", true, "Патрульный вертолёт"),
            _ => ("Bradley APC", "Ручная отметка обнаружения или уничтожения. Интервал — ваша оценка.", "Отметить обнаружение", "Уничтожен (новый отсчёт)", true, "Bradley APC"),
        };
    }

    public async Task LoadAsync()
    {
        Items.Clear();
        foreach (var t in await _repo.ListAsync(Kind))
        {
            var vm = new TimerItemViewModel(t, this);
            vm.Refresh(_clock.UtcNow);
            await vm.LoadHistoryAsync(_repo);
            Items.Add(vm);
        }
        IsEmpty = Items.Count == 0;
    }

    private static bool TryMinutes(string text, out int seconds)
    {
        seconds = 0;
        var s = text.Trim().Replace(',', '.');
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) || m < 0 || m > 60 * 24 * 30) return false;
        seconds = (int)Math.Round(m * 60);
        return true;
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        Error = "";
        if (!TryMinutes(NewIntervalMinutes, out var interval)) { Error = "Укажите интервал в минутах (число)."; return; }
        if (!TryMinutes(NewLeadMinutes, out var lead)) { Error = "Предупреждение — число минут (0 = без предупреждения)."; return; }
        try
        {
            var t = await _svc.CreateAsync(Kind, NewName, interval, lead);
            var vm = new TimerItemViewModel(t, this);
            vm.Refresh(_clock.UtcNow);
            Items.Add(vm);
            IsEmpty = false;
            NewName = Kind == TrackerKind.Monument ? "" : NewName;
        }
        catch (ArgumentException ex) { Error = ex.Message; }
    }

    internal async Task StartItemAsync(TimerItemViewModel i) { await _svc.StartAsync(i.Entry); i.Refresh(_clock.UtcNow); await i.LoadHistoryAsync(_repo); }
    internal async Task ResetItemAsync(TimerItemViewModel i) { await _svc.ResetAsync(i.Entry); i.Refresh(_clock.UtcNow); await i.LoadHistoryAsync(_repo); }
    internal async Task MarkItemAsync(TimerItemViewModel i) { await _svc.MarkAsync(i.Entry, "mark", _restartOnMark); i.Refresh(_clock.UtcNow); await i.LoadHistoryAsync(_repo); }
    internal async Task SaveNoteAsync(TimerItemViewModel i) { await _svc.SaveNoteAsync(i.Entry, i.Note); await i.LoadHistoryAsync(_repo); }
    internal async Task DeleteItemAsync(TimerItemViewModel i) { await _svc.DeleteAsync(i.Entry); Items.Remove(i); IsEmpty = Items.Count == 0; }

    public async Task TickAsync()
    {
        var now = _clock.UtcNow;
        foreach (var i in Items) i.Refresh(now);
        await _svc.TickAsync(Items.Select(i => i.Entry).ToList());
    }
}
