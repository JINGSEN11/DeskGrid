namespace DeskGrid.Core.Services;

public static class DockMagnificationCalculator
{
    /// <summary>
    /// Computes smooth macOS-style cosine wave magnification scales for a row of Dock icons.
    /// Returns 1.0 for every icon when disabled or when mouseX is null.
    /// </summary>
    public static double[] ComputeScales(
        double? mouseX,
        IReadOnlyList<double> iconCentersX,
        bool enabled,
        double maxScale = 1.42,
        double radius = 130.0)
    {
        var count = iconCentersX.Count;
        var scales = new double[count];

        if (count == 0)
            return scales;

        var clampedMax = Math.Clamp(maxScale, 1.0, 2.0);
        if (!enabled || mouseX is null || clampedMax <= 1.001 || radius <= 1.0)
        {
            Array.Fill(scales, 1.0);
            return scales;
        }

        var mx = mouseX.Value;
        var delta = clampedMax - 1.0;

        for (var i = 0; i < count; i++)
        {
            var distance = Math.Abs(mx - iconCentersX[i]);
            if (distance >= radius)
            {
                scales[i] = 1.0;
            }
            else
            {
                // Smooth raised-cosine bell curve: 1.0 at distance=0, 0.0 at distance=radius, zero derivative at both ends
                var normalized = distance / radius;
                var weight = 0.5 * (1.0 + Math.Cos(Math.PI * normalized));
                scales[i] = 1.0 + delta * weight;
            }
        }

        return scales;
    }
}
