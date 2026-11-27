namespace DeskGrid.Core.Services;

/// <summary>
/// Pure coordinate and state evaluator deciding whether a global mouse-down click
/// should automatically dismiss an open floating popup (e.g., PowerMenuPopup or WindowListPopup).
/// </summary>
public static class PopupDismissEvaluator
{
    public static bool ShouldDismissOnGlobalMouseDown(
        bool isPopupOpen,
        bool isSuppressedByModalDialog,
        int clickScreenX,
        int clickScreenY,
        PhysicalRect? popupBoundsPx,
        PhysicalRect? triggerBoundsPx = null)
    {
        if (!isPopupOpen || isSuppressedByModalDialog)
            return false;

        if (popupBoundsPx.HasValue && popupBoundsPx.Value.Contains(clickScreenX, clickScreenY))
            return false;

        if (triggerBoundsPx.HasValue && triggerBoundsPx.Value.Contains(clickScreenX, clickScreenY))
            return false;

        return true;
    }
}
