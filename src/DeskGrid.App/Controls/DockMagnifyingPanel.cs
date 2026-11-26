using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DeskGrid.Core.Services;
using DeskGrid.Win32.Services;

namespace DeskGrid.App.Controls;

/// <summary>
/// Horizontal Dock icon strip with smooth 60fps raised-cosine wave magnification.
/// Strictly tracks real-time cursor position and immediately smoothly resets back to 1.0
/// whenever the pointer leaves the panel, another window intercepts it, or the item list changes.
/// Only subscribes to CompositionTarget.Rendering while animating, keeping idle CPU at 0%.
/// </summary>
public sealed class DockMagnifyingPanel : StackPanel
{
    private const double DragPartShiftPixels = 6.0;

    private double[] _currentScales = [];
    private double[] _targetScales = [];
    private double[] _currentOffsetX = [];
    private double[] _targetOffsetX = [];
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

        MouseMove += OnPanelMouseMove;
        MouseLeave += OnPanelMouseLeave;
        LostMouseCapture += (_, _) => SmoothResetScales();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                ResetScales();
        };
        Unloaded += (_, _) => StopAnimation();
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

        _isHoverActive = true;
        _mouseX = e.GetPosition(this).X;
        RecomputeTargets();
        StartAnimation();
    }

    private void OnPanelMouseLeave(object sender, MouseEventArgs e)
    {
        if (_isDragReordering)
            return;

        SmoothResetScales();
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
                    !double.IsNaN(st.ScaleX) &&
                    !double.IsInfinity(st.ScaleX))
                {
                    scale = Math.Clamp(st.ScaleX, 1.0, 2.0);
                }

                if (group.Children.Count >= 2 &&
                    group.Children[1] is TranslateTransform tt &&
                    !double.IsNaN(tt.X) &&
                    !double.IsInfinity(tt.X))
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

        var centers = new double[count];
        for (var i = 0; i < count; i++)
        {
            if (Children[i] is FrameworkElement fe && fe.ActualWidth > 0)
            {
                // Use pure layout offset (unaffected by RenderTransform) so centers never drift during animation
                var offset = VisualTreeHelper.GetOffset(fe);
                centers[i] = offset.X + fe.ActualWidth * 0.5;
            }
        }

        var effectiveRadius = EffectRadius * DockLayoutMetrics.ClampScale(DockScale);
        _targetScales = DockMagnificationCalculator.ComputeScales(
            _mouseX,
            centers,
            EnableMagnification,
            MaxScale,
            effectiveRadius);
    }

    private bool AnyTransformNotSettled()
    {
        for (var i = 0; i < _currentScales.Length && i < _targetScales.Length; i++)
        {
            if (Math.Abs(_targetScales[i] - _currentScales[i]) > 0.002)
                return true;
        }
        for (var i = 0; i < _currentOffsetX.Length && i < _targetOffsetX.Length; i++)
        {
            if (Math.Abs(_targetOffsetX[i] - _currentOffsetX[i]) > 0.08)
                return true;
        }
        return false;
    }

    private void StartAnimation()
    {
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
        EnsureArrays();

        // Strictly re-verify real-time mouse position every frame so missed MouseLeave events never cause sticky magnification
        if (!_isDragReordering && _isHoverActive)
        {
            var liveX = SampleRealTimeMouseX();
            if (liveX is null)
            {
                _isHoverActive = false;
                _mouseX = null;
                Array.Fill(_targetScales, 1.0);
            }
            else
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
            if (Math.Abs(diff) > 0.002)
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
            if (Math.Abs(diffX) > 0.08)
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

        if (settled && !_isHoverActive)
            StopAnimation();
    }

    private void ApplyTransforms()
    {
        for (var i = 0; i < Children.Count && i < _currentScales.Length; i++)
        {
            if (Children[i] is not FrameworkElement fe)
                continue;

            var s = _currentScales[i];
            var offsetX = i < _currentOffsetX.Length ? _currentOffsetX[i] : 0.0;
            fe.RenderTransformOrigin = new Point(0.5, 1.0);

            if (fe.RenderTransform is not TransformGroup group || group.Children.Count != 2)
            {
                group = new TransformGroup();
                group.Children.Add(new ScaleTransform(1, 1));
                group.Children.Add(new TranslateTransform(0, 0));
                fe.RenderTransform = group;
            }

            var scaleTx = (ScaleTransform)group.Children[0];
            var transTx = (TranslateTransform)group.Children[1];
            var liftPx = 14.0 * DockLayoutMetrics.ClampScale(DockScale);
            scaleTx.ScaleX = s;
            scaleTx.ScaleY = s;
            transTx.X = Math.Abs(offsetX) <= 0.05 ? 0.0 : offsetX;
            transTx.Y = Math.Abs(s - 1.0) <= 0.001 ? 0.0 : -(s - 1.0) * liftPx;
            Panel.SetZIndex(fe, Math.Abs(s - 1.0) <= 0.001 ? 0 : (int)Math.Round(s * 100));
        }
    }
}
