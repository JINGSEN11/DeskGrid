using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskGrid.Core.Services;
using DeskGrid.Win32.Services;

namespace DeskGrid.App.Controls;

/// <summary>
/// Horizontal Dock icon strip with smooth 60fps raised-cosine wave magnification.
/// Uses ONLY GPU-composited RenderTransform (ScaleTransform + TranslateTransform) —
/// NEVER mutates Width, Height, or Margin during animation frames.
/// Subscribes to CompositionTarget.Rendering strictly while actively animating and
/// unsubscribes immediately once settled (keeping idle CPU at 0% across all monitors).
/// </summary>
public sealed class DockMagnifyingPanel : StackPanel
{
    private const double DragPartShiftPixels = 6.0;
    private const double ScaleSettleEpsilon = 0.002;
    private const double OffsetSettleEpsilon = 0.08;
    private const double TransformWriteEpsilon = 0.0002;

    private readonly DispatcherTimer _hoverWatchdogTimer;

    private double[] _currentScales = [];
    private double[] _targetScales = [];
    private double[] _currentOffsetX = [];
    private double[] _targetOffsetX = [];
    private double[] _centersBuffer = [];
    private double? _mouseX;
    private bool _isHoverActive;
    private bool _isAnimating;
    private bool _isDragReordering;
    private int? _dragInsertionSlot;

    public bool EnableMagnification { get; set; } = true;
    public double DockScale { get; set; } = DockLayoutMetrics.DefaultScale;
    public double MaxScale { get; set; } = 1.42;
    public double EffectRadius { get; set; } = 130.0;

    public DockMagnifyingPanel()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Bottom;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        // Lightweight watchdog ONLY runs while mouse is stationary over this panel with settled magnification,
        // catching the rare case where another window covers the Dock without firing WPF MouseLeave.
        _hoverWatchdogTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(160)
        };
        _hoverWatchdogTimer.Tick += OnHoverWatchdogTick;

        MouseMove += OnPanelMouseMove;
        MouseLeave += OnPanelMouseLeave;
        LostMouseCapture += (_, _) => SmoothResetScales();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                StopAndCleanup();
        };
        Unloaded += (_, _) => StopAndCleanup();
    }

    /// <summary>
    /// Immediately detaches all rendering hooks and timers and resets transforms to rest.
    /// Must be called when a DockWindow hides or closes to guarantee 0% idle CPU and zero leaks.
    /// </summary>
    public void StopAndCleanup()
    {
        StopHoverWatchdog();
        StopAnimation();
        ResetScales();
    }

    /// <summary>
    /// Immediately resets all scales and horizontal drag offsets to rest without animation.
    /// </summary>
    public void ResetScales()
    {
        _isHoverActive = false;
        _isDragReordering = false;
        _dragInsertionSlot = null;
        _mouseX = null;
        StopHoverWatchdog();

        var count = Children.Count;
        _currentScales = new double[count];
        _targetScales = new double[count];
        _currentOffsetX = new double[count];
        _targetOffsetX = new double[count];
        Array.Fill(_currentScales, 1.0);
        Array.Fill(_targetScales, 1.0);
        ApplyTransforms();
        StopAnimation();
    }

    /// <summary>
    /// Smoothly animates all icons back to 1.0 scale, clearing any hover/sticky magnification state.
    /// </summary>
    public void SmoothResetScales()
    {
        _isHoverActive = false;
        _mouseX = null;
        StopHoverWatchdog();
        SyncCurrentScalesFromVisuals();
        Array.Fill(_targetScales, 1.0);
        if (!_isDragReordering)
            Array.Fill(_targetOffsetX, 0.0);

        if (AnyTransformNotSettled())
            StartAnimation();
        else
        {
            ApplyTransforms();
            StopAnimation();
        }
    }

    /// <summary>
    /// Enters drag-reordering mode: smoothly settles magnification to 1.0 and prepares icons for parting shifts.
    /// </summary>
    public void BeginDragReorder()
    {
        _isDragReordering = true;
        _dragInsertionSlot = null;
        _isHoverActive = false;
        _mouseX = null;
        StopHoverWatchdog();
        SyncCurrentScalesFromVisuals();
        Array.Fill(_targetScales, 1.0);
        Array.Fill(_targetOffsetX, 0.0);

        if (AnyTransformNotSettled())
            StartAnimation();
        else
            ApplyTransforms();
    }

    /// <summary>
    /// Updates the target insertion gap during drag-reordering so adjacent icons smoothly glide apart.
    /// Pass null when no valid reorder gap is targeted.
    /// </summary>
    public void SetDragInsertionSlot(int? insertionSlot)
    {
        if (!_isDragReordering)
            return;

        EnsureArrays();
        if (_dragInsertionSlot == insertionSlot && _targetOffsetX.Length == Children.Count)
            return;

        _dragInsertionSlot = insertionSlot;
        RecomputeDragOffsets();

        if (AnyTransformNotSettled())
            StartAnimation();
        else
            ApplyTransforms();
    }

    /// <summary>
    /// Exits drag-reordering mode and returns all icon horizontal offsets back to zero.
    /// </summary>
    public void EndDragReorder(bool snapOffsets = false)
    {
        _isDragReordering = false;
        _dragInsertionSlot = null;
        StopHoverWatchdog();
        SyncCurrentScalesFromVisuals();
        Array.Fill(_targetScales, 1.0);
        Array.Fill(_targetOffsetX, 0.0);

        if (snapOffsets)
            Array.Fill(_currentOffsetX, 0.0);

        if (AnyTransformNotSettled())
            StartAnimation();
        else
        {
            ApplyTransforms();
            StopAnimation();
        }
    }

    /// <summary>
    /// Returns pure layout bounds (unaffected by RenderTransform) for every child slot.
    /// </summary>
    public IReadOnlyList<HorizontalSlotBound> GetLayoutSlotBounds()
    {
        var count = Children.Count;
        var bounds = new List<HorizontalSlotBound>(count);
        for (var i = 0; i < count; i++)
        {
            if (Children[i] is FrameworkElement fe)
            {
                var offset = VisualTreeHelper.GetOffset(fe);
                bounds.Add(new HorizontalSlotBound(offset.X, fe.ActualWidth));
            }
        }
        return bounds;
    }

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);

        // Whenever the icon list changes (app opened/closed/pinned/unpinned), clear stale hover state
        // and smoothly settle every icon back to 1.0 scale so no magnified ghost sticks around.
        _isHoverActive = false;
        _mouseX = null;
        StopHoverWatchdog();
        SyncCurrentScalesFromVisuals();
        Array.Fill(_targetScales, 1.0);
        if (_isDragReordering)
            RecomputeDragOffsets();
        else
            Array.Fill(_targetOffsetX, 0.0);

        if (AnyTransformNotSettled())
            StartAnimation();
        else
            StopAnimation();
    }

    private void OnPanelMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragReordering)
            return;

        if (!EnableMagnification)
        {
            SmoothResetScales();
            return;
        }

        var nextX = e.GetPosition(this).X;
        if (_isHoverActive && _mouseX.HasValue && Math.Abs(_mouseX.Value - nextX) < 0.25 && !_isAnimating)
            return;

        _isHoverActive = true;
        _mouseX = nextX;
        RecomputeTargets();
        if (AnyTransformNotSettled())
            StartAnimation();
        else
            StartHoverWatchdogIfNeeded();
    }

    private void OnPanelMouseLeave(object sender, MouseEventArgs e)
    {
        if (_isDragReordering)
            return;

        SmoothResetScales();
    }

    private void OnHoverWatchdogTick(object? sender, EventArgs e)
    {
        if (!_isHoverActive || _isAnimating || _isDragReordering || !IsVisible)
        {
            StopHoverWatchdog();
            return;
        }

        var liveX = SampleRealTimeMouseX();
        if (liveX is null)
        {
            SmoothResetScales();
            return;
        }

        if (!_mouseX.HasValue || Math.Abs(liveX.Value - _mouseX.Value) >= 0.5)
        {
            _mouseX = liveX;
            RecomputeTargets();
            if (AnyTransformNotSettled())
                StartAnimation();
        }
    }

    private void StartHoverWatchdogIfNeeded()
    {
        if (_isHoverActive && !_isAnimating && !_isDragReordering && IsVisible && EnableMagnification)
        {
            if (!_hoverWatchdogTimer.IsEnabled)
                _hoverWatchdogTimer.Start();
        }
        else
        {
            StopHoverWatchdog();
        }
    }

    private void StopHoverWatchdog()
    {
        if (_hoverWatchdogTimer.IsEnabled)
            _hoverWatchdogTimer.Stop();
    }

    private void SyncCurrentScalesFromVisuals()
    {
        var count = Children.Count;
        _currentScales = new double[count];
        _targetScales = new double[count];
        _currentOffsetX = new double[count];
        _targetOffsetX = new double[count];

        for (var i = 0; i < count; i++)
        {
            var scale = 1.0;
            var offsetX = 0.0;
            if (Children[i] is FrameworkElement fe &&
                fe.RenderTransform is TransformGroup group &&
                group.Children.Count >= 1)
            {
                if (group.Children[0] is ScaleTransform st &&
                    double.IsFinite(st.ScaleX))
                {
                    scale = Math.Clamp(st.ScaleX, 1.0, 2.0);
                }

                if (group.Children.Count >= 2 &&
                    group.Children[1] is TranslateTransform tt &&
                    double.IsFinite(tt.X))
                {
                    offsetX = Math.Clamp(tt.X, -40.0, 40.0);
                }
            }

            _currentScales[i] = scale;
            _targetScales[i] = 1.0;
            _currentOffsetX[i] = offsetX;
            _targetOffsetX[i] = 0.0;
        }
    }

    private void EnsureArrays()
    {
        if (_currentScales.Length != Children.Count || _currentOffsetX.Length != Children.Count)
            SyncCurrentScalesFromVisuals();
    }

    private void RecomputeDragOffsets()
    {
        EnsureArrays();
        var count = Children.Count;
        Array.Fill(_targetOffsetX, 0.0);

        if (!_isDragReordering || _dragInsertionSlot is null || count == 0)
            return;

        var slot = Math.Clamp(_dragInsertionSlot.Value, 0, count);
        var partShift = DragPartShiftPixels * DockLayoutMetrics.ClampScale(DockScale);
        for (var i = 0; i < count; i++)
        {
            if (slot == 0)
                _targetOffsetX[i] = partShift;
            else if (slot == count)
                _targetOffsetX[i] = -partShift;
            else
                _targetOffsetX[i] = i < slot ? -partShift : partShift;
        }
    }

    private double? SampleRealTimeMouseX()
    {
        if (!EnableMagnification || !IsVisible || Children.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0)
            return null;

        if (PresentationSource.FromVisual(this) is not HwndSource hwndSource || hwndSource.Handle == 0)
            return null;

        if (!DesktopHostService.TryGetCursorScreenPosition(out var screenX, out var screenY))
            return null;

        // If cursor moved over another window (activated app, popup, context menu), release magnification immediately
        if (!DesktopHostService.IsCursorOverWindow(hwndSource.Handle, screenX, screenY))
            return null;

        Point localPt;
        try
        {
            localPt = PointFromScreen(new Point(screenX, screenY));
        }
        catch
        {
            return null;
        }

        // Allow vertical headroom for upward-magnified icons while strictly checking horizontal panel bounds
        var safeDockScale = DockLayoutMetrics.ClampScale(DockScale);
        var maxUpwardHeadroom = Math.Max(
            DockLayoutMetrics.ComputeTopBufferHeight(safeDockScale, MaxScale, EnableMagnification),
            (Math.Clamp(MaxScale, 1.0, 2.0) - 1.0) * (ActualHeight + 24.0 * safeDockScale) + 12.0 * safeDockScale);
        if (localPt.X < -8.0 * safeDockScale || localPt.X > ActualWidth + 8.0 * safeDockScale ||
            localPt.Y < -maxUpwardHeadroom || localPt.Y > ActualHeight + 10.0 * safeDockScale)
        {
            return null;
        }

        return localPt.X;
    }

    private void RecomputeTargets()
    {
        EnsureArrays();
        var count = Children.Count;
        if (count == 0) return;

        if (!_isHoverActive || _mouseX is null || !EnableMagnification)
        {
            Array.Fill(_targetScales, 1.0);
            return;
        }

        if (_centersBuffer.Length != count)
            _centersBuffer = new double[count];

        for (var i = 0; i < count; i++)
        {
            if (Children[i] is FrameworkElement fe && fe.ActualWidth > 0)
            {
                // Use pure layout offset (unaffected by RenderTransform) so centers never drift during animation
                var offset = VisualTreeHelper.GetOffset(fe);
                _centersBuffer[i] = offset.X + fe.ActualWidth * 0.5;
            }
            else
            {
                _centersBuffer[i] = 0.0;
            }
        }

        var effectiveRadius = EffectRadius * DockLayoutMetrics.ClampScale(DockScale);
        _targetScales = DockMagnificationCalculator.ComputeScales(
            _mouseX,
            _centersBuffer,
            EnableMagnification,
            MaxScale,
            effectiveRadius);
    }

    private bool AnyTransformNotSettled()
    {
        for (var i = 0; i < _currentScales.Length && i < _targetScales.Length; i++)
        {
            if (Math.Abs(_targetScales[i] - _currentScales[i]) > ScaleSettleEpsilon)
                return true;
        }
        for (var i = 0; i < _currentOffsetX.Length && i < _targetOffsetX.Length; i++)
        {
            if (Math.Abs(_targetOffsetX[i] - _currentOffsetX[i]) > OffsetSettleEpsilon)
                return true;
        }
        return false;
    }

    private void StartAnimation()
    {
        StopHoverWatchdog();
        if (_isAnimating) return;
        _isAnimating = true;
        CompositionTarget.Rendering += OnRenderFrame;
    }

    private void StopAnimation()
    {
        if (!_isAnimating) return;
        _isAnimating = false;
        CompositionTarget.Rendering -= OnRenderFrame;
    }

    private void OnRenderFrame(object? sender, EventArgs e)
    {
        if (!IsVisible)
        {
            StopAndCleanup();
            return;
        }

        EnsureArrays();

        // Re-verify real-time pointer while animating hover magnification so missed MouseLeave never sticks
        if (!_isDragReordering && _isHoverActive)
        {
            var liveX = SampleRealTimeMouseX();
            if (liveX is null)
            {
                _isHoverActive = false;
                _mouseX = null;
                Array.Fill(_targetScales, 1.0);
            }
            else if (!_mouseX.HasValue || Math.Abs(liveX.Value - _mouseX.Value) >= 0.25)
            {
                _mouseX = liveX;
                RecomputeTargets();
            }
        }
        else
        {
            _isHoverActive = false;
            _mouseX = null;
            Array.Fill(_targetScales, 1.0);
        }

        var settled = true;
        const double lerpFactor = 0.24;

        for (var i = 0; i < _currentScales.Length; i++)
        {
            var diff = _targetScales[i] - _currentScales[i];
            if (Math.Abs(diff) > ScaleSettleEpsilon)
            {
                _currentScales[i] += diff * lerpFactor;
                settled = false;
            }
            else
            {
                _currentScales[i] = _targetScales[i];
            }
        }

        for (var i = 0; i < _currentOffsetX.Length && i < _targetOffsetX.Length; i++)
        {
            var diffX = _targetOffsetX[i] - _currentOffsetX[i];
            if (Math.Abs(diffX) > OffsetSettleEpsilon)
            {
                _currentOffsetX[i] += diffX * 0.28;
                settled = false;
            }
            else
            {
                _currentOffsetX[i] = _targetOffsetX[i];
            }
        }

        ApplyTransforms();

        // Always detach CompositionTarget.Rendering once animation settles (0% CPU when idle or cursor stationary)
        if (settled)
        {
            StopAnimation();
            StartHoverWatchdogIfNeeded();
        }
    }

    private void ApplyTransforms()
    {
        var liftPx = 14.0 * DockLayoutMetrics.ClampScale(DockScale);
        for (var i = 0; i < Children.Count && i < _currentScales.Length; i++)
        {
            if (Children[i] is not FrameworkElement fe)
                continue;

            var s = _currentScales[i];
            var offsetX = i < _currentOffsetX.Length ? _currentOffsetX[i] : 0.0;

            if (fe.RenderTransform is not TransformGroup group || group.Children.Count != 2)
            {
                fe.RenderTransformOrigin = new Point(0.5, 1.0);
                group = new TransformGroup();
                group.Children.Add(new ScaleTransform(1, 1));
                group.Children.Add(new TranslateTransform(0, 0));
                fe.RenderTransform = group;
            }

            var scaleTx = (ScaleTransform)group.Children[0];
            var transTx = (TranslateTransform)group.Children[1];
            var targetX = Math.Abs(offsetX) <= 0.05 ? 0.0 : offsetX;
            var targetY = Math.Abs(s - 1.0) <= 0.001 ? 0.0 : -(s - 1.0) * liftPx;
            var targetZ = Math.Abs(s - 1.0) <= 0.001 ? 0 : (int)Math.Round(s * 100);

            if (Math.Abs(scaleTx.ScaleX - s) > TransformWriteEpsilon)
                scaleTx.ScaleX = s;
            if (Math.Abs(scaleTx.ScaleY - s) > TransformWriteEpsilon)
                scaleTx.ScaleY = s;
            if (Math.Abs(transTx.X - targetX) > 0.02)
                transTx.X = targetX;
            if (Math.Abs(transTx.Y - targetY) > 0.02)
                transTx.Y = targetY;

            if (Panel.GetZIndex(fe) != targetZ)
                Panel.SetZIndex(fe, targetZ);
        }
    }
}
