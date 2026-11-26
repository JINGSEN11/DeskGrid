namespace DeskGrid.Core.Services;

/// <summary>
/// Computes proportional layout metrics and magnification headroom for DockWindow
/// across the supported DockScale range (0.70 - 1.30).
/// </summary>
public readonly record struct DockLayoutMetrics(
    double Scale,
    double IconSize,
    double IconVisualSize,
    double TileWidth,
    double TileHeight,
    double TileHorizontalMargin,
    double TilePaddingHorizontal,
    double TilePaddingTop,
    double TilePaddingBottom,
    double TileCornerRadius,
    double IndicatorRowHeight,
    double IndicatorHeight,
    double IndicatorActiveWidth,
    double IndicatorMultiWidth,
    double IndicatorSingleWidth,
    double ToolButtonSize,
    double ToolButtonFontSize,
    double ToolButtonSpacing,
    double DividerHeight,
    double DividerHorizontalMargin,
    double PillCornerRadius,
    double PillPaddingHorizontal,
    double PillPaddingVertical,
    double ClockTimeFontSize,
    double ClockDateFontSize,
    double ClockPaddingHorizontal,
    double ClockPaddingVertical,
    double ClockCornerRadius,
    double EffectiveMagnificationRadius,
    double MagnificationLiftPixels,
    double DragPartShiftPixels,
    double TopBufferHeight,
    double SideBufferWidth,
    double BottomBufferHeight)
{
    public const double MinScale = 0.70;
    public const double MaxScale = 1.30;
    public const double DefaultScale = 1.00;
    public const double ReferenceIconSize = 40.0;

    public static double ClampScale(double dockScale)
    {
        if (!double.IsFinite(dockScale) || dockScale <= 0)
            return DefaultScale;

        return Math.Round(Math.Clamp(dockScale, MinScale, MaxScale), 2);
    }

    public static double ComputeTopBufferHeight(
        double dockScale,
        double maxMagnificationScale,
        bool enableMagnification)
    {
        var scale = ClampScale(dockScale);
        var tileHeight = Math.Round(ReferenceIconSize * scale + 14.0 * scale);
        var liftPx = 14.0 * scale;
        var effectiveMag = enableMagnification && double.IsFinite(maxMagnificationScale)
            ? Math.Clamp(maxMagnificationScale, 1.0, 2.0)
            : 1.0;

        // Icons scale from RenderTransformOrigin (0.5, 1.0) and lift upward by (s - 1) * liftPx.
        var upwardProtrusion = (effectiveMag - 1.0) * (tileHeight + liftPx);
        var minHeadroom = 28.0 * scale;
        return Math.Ceiling(Math.Max(minHeadroom, upwardProtrusion + 18.0 * scale));
    }

    public static DockLayoutMetrics Calculate(
        double dockScale,
        double maxMagnificationScale = 1.42,
        bool enableMagnification = true,
        double baseMagnificationRadius = 130.0)
    {
        var scale = ClampScale(dockScale);
        var iconSize = Math.Round(ReferenceIconSize * scale, 1);
        var iconVisualSize = Math.Round(Math.Max(18.0, iconSize - 4.0 * scale), 1);
        var tileWidth = Math.Round(iconSize + 12.0 * scale);
        var tileHeight = Math.Round(iconSize + 14.0 * scale);
        var tileHorizontalMargin = Math.Round(4.0 * scale, 1);
        var tilePaddingHorizontal = Math.Round(4.0 * scale, 1);
        var tilePaddingTop = Math.Round(3.0 * scale, 1);
        var tilePaddingBottom = Math.Round(2.0 * scale, 1);
        var tileCornerRadius = Math.Round(12.0 * scale, 1);

        var indicatorRowHeight = Math.Round(Math.Max(5.0, 7.0 * scale), 1);
        var indicatorHeight = Math.Round(Math.Max(2.5, 3.5 * scale), 1);
        var indicatorActiveWidth = Math.Round(16.0 * scale, 1);
        var indicatorMultiWidth = Math.Round(12.0 * scale, 1);
        var indicatorSingleWidth = Math.Round(5.0 * scale, 1);

        var toolButtonSize = Math.Round(38.0 * scale);
        var toolButtonFontSize = Math.Round(16.0 * scale, 1);
        var toolButtonSpacing = Math.Round(3.0 * scale, 1);

        var dividerHeight = Math.Round(28.0 * scale);
        var dividerHorizontalMargin = Math.Round(10.0 * scale, 1);

        var pillCornerRadius = Math.Round(22.0 * scale, 1);
        var pillPaddingHorizontal = Math.Round(12.0 * scale, 1);
        var pillPaddingVertical = Math.Round(8.0 * scale, 1);

        var clockTimeFontSize = Math.Round(13.0 * scale, 1);
        var clockDateFontSize = Math.Round(10.0 * scale, 1);
        var clockPaddingHorizontal = Math.Round(10.0 * scale, 1);
        var clockPaddingVertical = Math.Round(4.0 * scale, 1);
        var clockCornerRadius = Math.Round(10.0 * scale, 1);

        var safeRadius = double.IsFinite(baseMagnificationRadius)
            ? Math.Clamp(baseMagnificationRadius, 60.0, 260.0)
            : 130.0;
        var effectiveMagRadius = Math.Round(safeRadius * scale, 1);
        var magnificationLiftPixels = Math.Round(14.0 * scale, 1);
        var dragPartShiftPixels = Math.Round(6.0 * scale, 1);

        var topBufferHeight = ComputeTopBufferHeight(scale, maxMagnificationScale, enableMagnification);
        var sideBufferWidth = Math.Ceiling(Math.Max(18.0, 20.0 * scale));
        var bottomBufferHeight = Math.Ceiling(Math.Max(10.0, 14.0 * scale));

        return new DockLayoutMetrics(
            Scale: scale,
            IconSize: iconSize,
            IconVisualSize: iconVisualSize,
            TileWidth: tileWidth,
            TileHeight: tileHeight,
            TileHorizontalMargin: tileHorizontalMargin,
            TilePaddingHorizontal: tilePaddingHorizontal,
            TilePaddingTop: tilePaddingTop,
            TilePaddingBottom: tilePaddingBottom,
            TileCornerRadius: tileCornerRadius,
            IndicatorRowHeight: indicatorRowHeight,
            IndicatorHeight: indicatorHeight,
            IndicatorActiveWidth: indicatorActiveWidth,
            IndicatorMultiWidth: indicatorMultiWidth,
            IndicatorSingleWidth: indicatorSingleWidth,
            ToolButtonSize: toolButtonSize,
            ToolButtonFontSize: toolButtonFontSize,
            ToolButtonSpacing: toolButtonSpacing,
            DividerHeight: dividerHeight,
            DividerHorizontalMargin: dividerHorizontalMargin,
            PillCornerRadius: pillCornerRadius,
            PillPaddingHorizontal: pillPaddingHorizontal,
            PillPaddingVertical: pillPaddingVertical,
            ClockTimeFontSize: clockTimeFontSize,
            ClockDateFontSize: clockDateFontSize,
            ClockPaddingHorizontal: clockPaddingHorizontal,
            ClockPaddingVertical: clockPaddingVertical,
            ClockCornerRadius: clockCornerRadius,
            EffectiveMagnificationRadius: effectiveMagRadius,
            MagnificationLiftPixels: magnificationLiftPixels,
            DragPartShiftPixels: dragPartShiftPixels,
            TopBufferHeight: topBufferHeight,
            SideBufferWidth: sideBufferWidth,
            BottomBufferHeight: bottomBufferHeight);
    }
}
