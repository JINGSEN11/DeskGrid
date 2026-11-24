namespace DeskGrid.Core.Services;

public readonly record struct HorizontalSlotBound(double Left, double Width)
{
    public double Right => Left + Width;
    public double Center => Left + Width * 0.5;
}

public readonly record struct HorizontalDropTarget(int InsertionSlot, double IndicatorX);

public readonly record struct GridDropTarget(
    int InsertionSlot,
    double IndicatorX,
    double IndicatorY,
    double IndicatorHeight);

/// <summary>
/// Pure geometry and index-resolution helpers for horizontal Dock strip and 2D WrapPanel drag reordering.
/// </summary>
public static class DragReorderCalculator
{
    /// <summary>
    /// Converts a gap insertion slot (0..itemCount) into the final list index (0..itemCount-1)
    /// after removing the item at <paramref name="oldIndex"/>.
    /// Returns <paramref name="oldIndex"/> when dropping back into the item's own position (no-op).
    /// </summary>
    public static int ResolveTargetIndex(int oldIndex, int insertionSlot, int itemCount)
    {
        if (itemCount <= 1 || oldIndex < 0 || oldIndex >= itemCount)
            return oldIndex;

        var clampedSlot = Math.Clamp(insertionSlot, 0, itemCount);
        if (clampedSlot == oldIndex || clampedSlot == oldIndex + 1)
            return oldIndex;

        return clampedSlot > oldIndex ? clampedSlot - 1 : clampedSlot;
    }

    /// <summary>
    /// Computes the horizontal insertion slot (0..count) and indicator X coordinate
    /// for a horizontal icon strip (e.g. Dock pinned panel).
    /// </summary>
    public static HorizontalDropTarget ComputeHorizontalDropTarget(
        double pointerX,
        IReadOnlyList<HorizontalSlotBound> items)
    {
        if (items.Count == 0)
            return new HorizontalDropTarget(0, 0);

        for (var i = 0; i < items.Count; i++)
        {
            if (pointerX < items[i].Center)
            {
                var indicatorX = i == 0
                    ? Math.Max(0.0, items[0].Left - 2.0)
                    : (items[i - 1].Right + items[i].Left) * 0.5;
                return new HorizontalDropTarget(i, indicatorX);
            }
        }

        var last = items[^1];
        return new HorizontalDropTarget(items.Count, last.Right + 2.0);
    }

    /// <summary>
    /// Computes the insertion slot (0..count) and vertical drop indicator placement
    /// for a 2D horizontal-wrapping grid (e.g. DesktopBox WrapPanel).
    /// Grouping by visual row ensures row-end vs next-row-start drop indicators stay next to the cursor.
    /// </summary>
    public static GridDropTarget ComputeWrapGridDropTarget(
        double pointerX,
        double pointerY,
        IReadOnlyList<RectBounds> items)
    {
        if (items.Count == 0)
            return new GridDropTarget(0, 0, 0, 48);

        // Group consecutive items into visual rows based on Top coordinate
        var rows = new List<(int First, int Last, double Top, double Bottom)>();
        var rowStart = 0;
        var rowTop = items[0].Top;
        var rowBottom = items[0].Bottom;

        for (var i = 1; i < items.Count; i++)
        {
            if (Math.Abs(items[i].Top - rowTop) <= 6.0)
            {
                rowBottom = Math.Max(rowBottom, items[i].Bottom);
            }
            else
            {
                rows.Add((rowStart, i - 1, rowTop, rowBottom));
                rowStart = i;
                rowTop = items[i].Top;
                rowBottom = items[i].Bottom;
            }
        }
        rows.Add((rowStart, items.Count - 1, rowTop, rowBottom));

        // Pick the row matching pointerY (clamped to first/last row)
        var targetRow = rows[0];
        if (pointerY >= rows[^1].Top)
        {
            targetRow = rows[^1];
        }
        else if (pointerY > rows[0].Top)
        {
            var bestDist = double.MaxValue;
            foreach (var row in rows)
            {
                if (pointerY >= row.Top && pointerY <= row.Bottom)
                {
                    targetRow = row;
                    break;
                }

                var centerY = (row.Top + row.Bottom) * 0.5;
                var dist = Math.Abs(pointerY - centerY);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    targetRow = row;
                }
            }
        }

        var indY = targetRow.Top + 4.0;
        var indH = Math.Max(28.0, (targetRow.Bottom - targetRow.Top) - 8.0);

        // If pointer is noticeably below the last row, snap to the end of the last row
        if (targetRow == rows[^1] && pointerY > targetRow.Bottom + 12.0)
        {
            var endItem = items[targetRow.Last];
            return new GridDropTarget(targetRow.Last + 1, endItem.Right + 2.0, indY, indH);
        }

        for (var i = targetRow.First; i <= targetRow.Last; i++)
        {
            var item = items[i];
            var centerX = item.Left + item.Width * 0.5;
            if (pointerX < centerX)
            {
                var indX = i == targetRow.First
                    ? Math.Max(0.0, item.Left - 2.0)
                    : (items[i - 1].Right + item.Left) * 0.5;
                return new GridDropTarget(i, indX, indY, indH);
            }
        }

        var lastInRow = items[targetRow.Last];
        return new GridDropTarget(targetRow.Last + 1, lastInRow.Right + 2.0, indY, indH);
    }
}
