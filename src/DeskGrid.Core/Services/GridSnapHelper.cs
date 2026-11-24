namespace DeskGrid.Core.Services;

public readonly record struct RectBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public static class GridSnapHelper
{
    public static double SnapToGrid(double value, int step)
    {
        if (step <= 1) return Math.Round(value);
        return Math.Round(value / step) * step;
    }

    public static RectBounds SnapPosition(
        RectBounds target,
        RectBounds workArea,
        IEnumerable<RectBounds> siblingBoxes,
        bool enableGridSnap,
        int gridStep = 12,
        double magnetThreshold = 10,
        double screenPadding = 12)
    {
        var left = enableGridSnap ? SnapToGrid(target.Left, gridStep) : target.Left;
        var top = enableGridSnap ? SnapToGrid(target.Top, gridStep) : target.Top;

        // Magnetic alignment to work area edges (with comfortable padding)
        if (Math.Abs(left - (workArea.Left + screenPadding)) <= magnetThreshold)
            left = workArea.Left + screenPadding;
        else if (Math.Abs((left + target.Width) - (workArea.Right - screenPadding)) <= magnetThreshold)
            left = workArea.Right - screenPadding - target.Width;

        if (Math.Abs(top - (workArea.Top + screenPadding)) <= magnetThreshold)
            top = workArea.Top + screenPadding;
        else if (Math.Abs((top + target.Height) - (workArea.Bottom - screenPadding)) <= magnetThreshold)
            top = workArea.Bottom - screenPadding - target.Height;

        // Magnetic alignment to sibling boxes
        foreach (var other in siblingBoxes)
        {
            // Left-to-left or left-to-right(+gap)
            if (Math.Abs(left - other.Left) <= magnetThreshold)
                left = other.Left;
            else if (Math.Abs(left - (other.Right + screenPadding)) <= magnetThreshold)
                left = other.Right + screenPadding;
            else if (Math.Abs((left + target.Width + screenPadding) - other.Left) <= magnetThreshold)
                left = other.Left - target.Width - screenPadding;

            // Top-to-top or top-to-bottom(+gap)
            if (Math.Abs(top - other.Top) <= magnetThreshold)
                top = other.Top;
            else if (Math.Abs(top - (other.Bottom + screenPadding)) <= magnetThreshold)
                top = other.Bottom + screenPadding;
            else if (Math.Abs((top + target.Height + screenPadding) - other.Top) <= magnetThreshold)
                top = other.Top - target.Height - screenPadding;
        }

        // Clamp inside work area
        var maxLeft = Math.Max(workArea.Left, workArea.Right - target.Width);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - target.Height);
        left = Math.Clamp(left, workArea.Left, maxLeft);
        top = Math.Clamp(top, workArea.Top, maxTop);

        return new RectBounds(left, top, target.Width, target.Height);
    }

    public static (double Width, double Height) SnapSize(
        double width,
        double height,
        bool enableGridSnap,
        int gridStep = 12,
        double minWidth = 220,
        double minHeight = 140)
    {
        var w = Math.Max(minWidth, enableGridSnap ? SnapToGrid(width, gridStep) : width);
        var h = Math.Max(minHeight, enableGridSnap ? SnapToGrid(height, gridStep) : height);
        return (w, h);
    }
}
