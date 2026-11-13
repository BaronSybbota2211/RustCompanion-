using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RustCompanion.App.Services;
using RustCompanion.Core;
using RustCompanion.Core.Models;
using RustCompanion.Core.Overlay;

namespace RustCompanion.App.ViewModels;

public sealed partial class HudItemRowViewModel : ObservableObject
{
    public string Id { get; }
    public string Title { get; }
    [ObservableProperty] private bool _selected;
    public HudItemRowViewModel(string id, string title, bool selected) { Id = id; Title = title; _selected = selected; }
}

public sealed partial class OverlayViewModel : ObservableObject
{
    private readonly ISettingsProvider _settings; private readonly RustCompanion.Core.Data.SettingsRepository _repo; private readonly OverlayService _overlay;
    private bool _loading;
    public ObservableCollection<HudItemRowViewModel> Items { get; } = new();
    public ObservableCollection<string> Report { get; } = new();
    public IReadOnlyList<string> Corners { get; } = new[] { "Верхний левый", "Верхний правый", "Нижний левый", "Нижний правый" };

    [ObservableProperty] private bool _hudVisible;
    [ObservableProperty] private bool _moveMode;
    [ObservableProperty] private bool _followRust = true;
    [ObservableProperty] private int _width;
    [ObservableProperty] private bool _toastsEnabled;
    [ObservableProperty] private int _corner;
    [ObservableProperty] private int _duration;
    [ObservableProperty] private int _margin;
    [ObservableProperty] private int _maxVisible;
    [ObservableProperty] private string _message = "";

    public OverlayViewModel(ISettingsProvider settings, RustCompanion.Core.Data.SettingsRepository repo, OverlayService overlay)
    { _settings = settings; _repo = repo; _overlay = overlay; }

    public void Load()
    {
        _loading = true;
        var s = _settings.Current; var sel = OverlayHud.ParseSelection(s.OverlayHudItems);
        HudVisible = s.OverlayHudVisible; MoveMode = !s.OverlayLocked; FollowRust = s.OverlayFollowRust; Width = (int)s.OverlayWidth;
        ToastsEnabled = s.OverlayEnabled; Corner = s.OverlayCorner; Duration = s.OverlayDurationSeconds; Margin = s.OverlayMargin; MaxVisible = s.OverlayMaxVisible;
        Items.Clear();
        foreach (var (id, title) in OverlayHud.Catalog) { var row = new HudItemRowViewModel(id, title, sel.Contains(id)); row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HudItemRowViewModel.Selected)) _ = SaveAsync(); }; Items.Add(row); }
        _loading = false;
    }

    partial void OnHudVisibleChanged(bool value) => _ = SaveAsync();
    partial void OnMoveModeChanged(bool value) => _ = SaveAsync();
    partial void OnFollowRustChanged(bool value) => _ = SaveAsync();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_loading) return;
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(_settings.Current))!;
        s.OverlayHudVisible = HudVisible; s.OverlayLocked = !MoveMode; s.OverlayFollowRust = FollowRust; s.OverlayWidth = Width;
        s.OverlayEnabled = ToastsEnabled; s.OverlayCorner = Corner; s.OverlayDurationSeconds = Duration; s.OverlayMargin = Margin; s.OverlayMaxVisible = MaxVisible;
        s.OverlayHudItems = OverlayHud.Serialize(Items.Where(i => i.Selected).Select(i => i.Id));
        var errors = s.Validate();
        if (errors.Count > 0) { Message = string.Join(" ", errors); return; }
        await _repo.SaveAsync(s); _settings.Update(s); Message = "Сохранено."; _overlay.Apply();
    }

    [RelayCommand] private void ShowTest() { Message = "Тестовая плашка отправлена. Если её не видно поверх игры — нажмите «Диагностика»."; _overlay.ShowTest(); }
    [RelayCommand] private void HideToasts() => _overlay.HideAll();
    [RelayCommand] private void ResetPosition() { _overlay.ResetPosition(); Message = "Позиция сброшена к выбранному углу."; }

    [RelayCommand]
    private void Diagnose()
    {
        Report.Clear();
        foreach (var l in _overlay.Diagnose()) Report.Add(l);
    }
}
