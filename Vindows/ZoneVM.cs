using System.ComponentModel;
using Vindows.Core;

namespace Vindows;

/// <summary>Редактор назначения зоны. Список вариантов — снимок, обновление не стирает выбор.</summary>
public sealed class ZoneVM : INotifyPropertyChanged
{
    private readonly Zone _zone;
    private readonly IReadOnlyList<WindowInfo> _windows;
    private readonly Action _assignmentChanged;
    private SelectionOption? _selectedOption;

    public ZoneVM(Zone zone, string position, IReadOnlyList<WindowInfo> windows, Action assignmentChanged)
    {
        _zone = zone;
        _windows = windows;
        _assignmentChanged = assignmentChanged;
        Name = $"{zone.Name} — {position}";
        RebuildOptions();
    }

    public string Name { get; }
    public string SizeText => $"{_zone.Width:0.#}% × {_zone.Height:0.#}%";
    public string AssignedWindowText => string.IsNullOrWhiteSpace(_zone.ProcessName)
        ? "Перетащите окно на + в предпросмотре"
        : $"{_zone.WindowTitle ?? _zone.ProcessName} [{_zone.ProcessName}]";
    public string Status => _zone.Status;
    public IReadOnlyList<SelectionOption> Options { get; private set; } = [];
    public string SelectionHint => ModeIndex == 1
        ? "Закрепляется выбранное окно. После загрузки раскладки ищется единственное окно с тем же заголовком."
        : "Подбирается любое свободное окно выбранного приложения.";

    public int ModeIndex
    {
        get => (int)_zone.SelectionMode;
        set
        {
            if (value < 0 || value > 1 || value == ModeIndex) return;
            _zone.SelectionMode = (WindowSelectionMode)value;
            _zone.WindowHandle = IntPtr.Zero;
            _zone.WindowProcessId = 0;
            _zone.WindowTitle = null;
            // Конкретное окно требует явного выбора.
            if (value == 1) _zone.ProcessName = null;
            RebuildOptions();
            Changed();
            Notify(nameof(ModeIndex));
            Notify(nameof(SelectionHint));
        }
    }

    public SelectionOption? SelectedOption
    {
        get => _selectedOption;
        set
        {
            // WPF сбрасывает SelectedItem при обновлении ItemsSource.
            // Явное снятие назначения — вариант «Не назначено».
            if (value == null || ReferenceEquals(value, _selectedOption)) return;
            _selectedOption = value;
            _zone.ProcessName = value.ProcessName;
            _zone.WindowHandle = value.Window?.Handle ?? IntPtr.Zero;
            _zone.WindowProcessId = value.Window?.ProcessId ?? 0;
            _zone.WindowTitle = value.Window?.Title;
            Changed();
            Notify(nameof(SelectedOption));
        }
    }

    public bool AssignWindow(WindowInfo window)
    {
        var option = Options.FirstOrDefault(o => o.Window?.Handle == window.Handle)
            ?? new SelectionOption(window.DisplayName, window.ProcessName, window);
        if (!Options.Contains(option))
            Options = Options.Append(option).ToList();

        _zone.SelectionMode = WindowSelectionMode.SpecificWindow;
        _selectedOption = option;
        _zone.ProcessName = window.ProcessName;
        _zone.WindowHandle = window.Handle;
        _zone.WindowProcessId = window.ProcessId;
        _zone.WindowTitle = window.Title;
        Notify(nameof(ModeIndex));
        Notify(nameof(Options));
        Notify(nameof(SelectedOption));
        Notify(nameof(AssignedWindowText));
        Changed();
        return true;
    }

    private void Changed()
    {
        _zone.Status = string.IsNullOrWhiteSpace(_zone.ProcessName)
            ? "Не назначено" : "Назначение изменено — нажмите «Расставить окна»";
        _assignmentChanged();
        RefreshStatus();
    }

    private void RebuildOptions()
    {
        var options = new List<SelectionOption> { new("— Не назначено —", null) };
        if (_zone.SelectionMode == WindowSelectionMode.AnyWindow)
        {
            options.AddRange(_windows.GroupBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key).Select(g => new SelectionOption($"{g.Key} — открытых окон: {g.Count()}", g.Key)));
            _selectedOption = options.FirstOrDefault(o => string.Equals(o.ProcessName, _zone.ProcessName, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            options.AddRange(_windows.Select(w => new SelectionOption(w.DisplayName, w.ProcessName, w)));
            _selectedOption = options.FirstOrDefault(o => o.Window != null &&
                o.Window.Handle == _zone.WindowHandle && o.Window.ProcessId == _zone.WindowProcessId &&
                string.Equals(o.ProcessName, _zone.ProcessName, StringComparison.OrdinalIgnoreCase));
        }

        if (_selectedOption == null && !string.IsNullOrWhiteSpace(_zone.ProcessName))
        {
            _selectedOption = new SelectionOption(
                _zone.SelectionMode == WindowSelectionMode.SpecificWindow
                    ? $"{_zone.WindowTitle} [{_zone.ProcessName}] — недоступно / сохранено"
                    : $"{_zone.ProcessName} — нет открытых окон", _zone.ProcessName);
            options.Add(_selectedOption);
        }
        _selectedOption ??= options[0];
        Options = options;
        Notify(nameof(Options));
        Notify(nameof(SelectedOption));
    }

    public void RefreshStatus() => Notify(nameof(Status));
    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record SelectionOption(string Label, string? ProcessName, WindowInfo? Window = null);
