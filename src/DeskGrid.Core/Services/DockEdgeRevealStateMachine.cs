namespace DeskGrid.Core.Services;

public enum DockEdgeRevealVisualState
{
    /// <summary>Not hidden by fullscreen; Dock follows normal visibility.</summary>
    Normal,

    /// <summary>Hidden due to an active fullscreen application on this monitor.</summary>
    FullscreenHidden,

    /// <summary>Temporarily revealed above the fullscreen app after bottom-edge dwell.</summary>
    TemporarilyRevealed
}

public readonly record struct DockEdgeRevealStep(
    DockEdgeRevealVisualState State,
    bool StateChanged);

/// <summary>
/// Deterministic per-monitor state machine governing bottom-edge hotzone reveal
/// and delayed auto-hide when the Dock is hidden for fullscreen applications.
/// </summary>
public sealed class DockEdgeRevealStateMachine
{
    public const int DefaultRevealDwellMs = 135;
    public const int DefaultHideDelayMs = 350;

    private readonly int _revealDwellMs;
    private readonly int _hideDelayMs;

    private long? _edgeEnterTimestampMs;
    private long? _dockLeaveTimestampMs;

    public DockEdgeRevealVisualState State { get; private set; } = DockEdgeRevealVisualState.Normal;

    /// <summary>
    /// True while a dwell-to-reveal or delay-to-hide countdown is actively ticking.
    /// Allows high-level cursor polling to skip completely when cursor coordinates are stationary and no timer is pending.
    /// </summary>
    public bool HasPendingTransition => _edgeEnterTimestampMs.HasValue || _dockLeaveTimestampMs.HasValue;

    public DockEdgeRevealStateMachine(
        int revealDwellMs = DefaultRevealDwellMs,
        int hideDelayMs = DefaultHideDelayMs)
    {
        _revealDwellMs = Math.Max(0, revealDwellMs);
        _hideDelayMs = Math.Max(0, hideDelayMs);
    }

    public void Reset(bool isFullscreenHidden = false)
    {
        _edgeEnterTimestampMs = null;
        _dockLeaveTimestampMs = null;
        State = isFullscreenHidden
            ? DockEdgeRevealVisualState.FullscreenHidden
            : DockEdgeRevealVisualState.Normal;
    }

    /// <summary>
    /// Advances the state machine based on current fullscreen status, cursor location, and open overlays.
    /// </summary>
    public DockEdgeRevealStep Update(
        long nowMs,
        bool isFullscreenHiddenActive,
        bool revealOnBottomEdgeEnabled,
        bool isCursorAtBottomEdge,
        bool isCursorInsideDock,
        bool hasOpenInteractiveOverlay)
    {
        var previous = State;

        if (!isFullscreenHiddenActive)
        {
            _edgeEnterTimestampMs = null;
            _dockLeaveTimestampMs = null;
            State = DockEdgeRevealVisualState.Normal;
            return new DockEdgeRevealStep(State, State != previous);
        }

        if (!revealOnBottomEdgeEnabled)
        {
            _edgeEnterTimestampMs = null;
            _dockLeaveTimestampMs = null;
            State = DockEdgeRevealVisualState.FullscreenHidden;
            return new DockEdgeRevealStep(State, State != previous);
        }

        if (State == DockEdgeRevealVisualState.Normal)
        {
            _edgeEnterTimestampMs = null;
            _dockLeaveTimestampMs = null;
            State = DockEdgeRevealVisualState.FullscreenHidden;
            return new DockEdgeRevealStep(State, State != previous);
        }

        if (State == DockEdgeRevealVisualState.FullscreenHidden)
        {
            _dockLeaveTimestampMs = null;
            if (isCursorAtBottomEdge)
            {
                _edgeEnterTimestampMs ??= nowMs;
                if (nowMs - _edgeEnterTimestampMs.Value >= _revealDwellMs)
                {
                    _edgeEnterTimestampMs = null;
                    State = DockEdgeRevealVisualState.TemporarilyRevealed;
                }
            }
            else
            {
                _edgeEnterTimestampMs = null;
            }

            return new DockEdgeRevealStep(State, State != previous);
        }

        // State == DockEdgeRevealVisualState.TemporarilyRevealed
        _edgeEnterTimestampMs = null;
        if (hasOpenInteractiveOverlay || isCursorInsideDock || isCursorAtBottomEdge)
        {
            _dockLeaveTimestampMs = null;
        }
        else
        {
            _dockLeaveTimestampMs ??= nowMs;
            if (nowMs - _dockLeaveTimestampMs.Value >= _hideDelayMs)
            {
                _dockLeaveTimestampMs = null;
                State = DockEdgeRevealVisualState.FullscreenHidden;
            }
        }

        return new DockEdgeRevealStep(State, State != previous);
    }
}
