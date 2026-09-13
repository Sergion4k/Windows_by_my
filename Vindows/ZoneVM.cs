using System.ComponentModel;
using Vindows.Core;

namespace Vindows;

/// <summary>Зона для отображения в списке: имя, размер и выбранное приложение.</summary>
public sealed class ZoneVM : INotifyPropertyChanged
{
    private readonly Zone _zone;

    public ZoneVM(Zone zone) => _zone = zone;

    public string Name => _zone.Name;

    public string SizeText => $"{_zone.Width:0.#}% × {_zone.Height:0.#}%";

    /// <summary>Имя процесса, выбранного для зоны (null/пусто — зона не используется).</summary>
    public string? SelectedProcess
    {
        get => _zone.ProcessName;
        set
        {
            if (_zone.ProcessName == value) return;
            _zone.ProcessName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProcess)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
