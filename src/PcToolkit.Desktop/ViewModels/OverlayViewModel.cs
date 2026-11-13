using PcToolkit.Core.Overlay;
using PcToolkit.Desktop.Mvvm;

namespace PcToolkit.Desktop.ViewModels;

/// <summary>Окно оверлея поверх игры: что показывать (<see cref="Content"/>), виден ли он и двигают ли его мышью.</summary>
public sealed class OverlayViewModel : ObservableObject
{
    private bool _isVisible;
    private bool _isMoving;
    private int? _targetProcessId;
    private bool _showOverBackgroundGame;

    public OverlayViewModel(OverlayContent content)
    {
        Content = content;
    }

    public OverlayContent Content { get; }

    /// <summary>Оверлей показан поверх игры.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>
    /// Оверлей двигают мышью: он виден даже без игры, ловит мышь и показывает рамку.
    /// Куда отпустили — сообщает <see cref="PositionDropped"/>.
    /// </summary>
    public bool IsMoving
    {
        get => _isMoving;
        set => SetProperty(ref _isMoving, value);
    }

    /// <summary>
    /// Настройки оверлея открыты, а игра идёт не на переднем плане: оверлей остаётся над игрой,
    /// если она на другом мониторе, чем окно PC Toolkit (решает окно оверлея).
    /// </summary>
    public bool ShowOverBackgroundGame
    {
        get => _showOverBackgroundGame;
        set => SetProperty(ref _showOverBackgroundGame, value);
    }

    /// <summary>Процесс игры, над которой оверлей: окно ставится на её монитор, даже когда игра не на переднем плане.</summary>
    public int? TargetProcessId
    {
        get => _targetProcessId;
        set => SetProperty(ref _targetProcessId, value);
    }

    /// <summary>Оверлей перетащили мышью и отпустили: новое место.</summary>
    public event EventHandler<OverlayPosition>? PositionDropped;

    /// <summary>Двойной щелчок по оверлею, пока его двигают: «готово».</summary>
    public event EventHandler? MoveFinished;

    public void DropPosition(OverlayPosition position) => PositionDropped?.Invoke(this, position);

    public void FinishMoving() => MoveFinished?.Invoke(this, EventArgs.Empty);
}
