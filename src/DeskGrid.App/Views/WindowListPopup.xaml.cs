using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskGrid.Core.Models;

namespace DeskGrid.App.Views;

public partial class WindowListPopup : Window
{
    private readonly Action<nint> _onActivate;
    private readonly Action<nint> _onClose;
    private bool _closing;

    public WindowListPopup(
        string appName,
        IReadOnlyList<RunningWindowInfo> windows,
        Action<nint> onActivate,
        Action<nint> onClose)
    {
        InitializeComponent();
        _onActivate = onActivate;
        _onClose = onClose;

        AppHeaderText.Text = $"{appName}（{windows.Count} 个窗口）";

        foreach (var win in windows)
            WindowsListPanel.Children.Add(BuildRow(win));
    }

    private Border BuildRow(RunningWindowInfo win)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 6, 6, 6),
            Margin = new Thickness(0, 1, 0, 1),
            Background = win.IsActive ? (Brush)FindResource("DgSelectedBrush") : Brushes.Transparent,
            Cursor = Cursors.Hand
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = win.Title,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("DgTextPrimaryBrush"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(title, 0);

        var closeBtn = new Button
        {
            Content = "\uE8BB",
            Style = (Style)FindResource("DgIconButtonStyle"),
            Width = 22,
            Height = 22,
            FontSize = 10,
            ToolTip = "关闭此窗口"
        };
        closeBtn.Click += (_, e) =>
        {
            e.Handled = true;
            _onClose(win.Handle);
            SafeClose();
        };
        Grid.SetColumn(closeBtn, 1);

        grid.Children.Add(title);
        grid.Children.Add(closeBtn);
        row.Child = grid;

        row.MouseEnter += (_, _) =>
        {
            if (!win.IsActive)
                row.Background = (Brush)FindResource("DgHoverBrush");
        };
        row.MouseLeave += (_, _) =>
        {
            if (!win.IsActive)
                row.Background = Brushes.Transparent;
        };
        row.MouseLeftButtonUp += (_, _) =>
        {
            _onActivate(win.Handle);
            SafeClose();
        };

        return row;
    }

    private void OnPopupDeactivated(object? sender, EventArgs e) => SafeClose();

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); } catch { }
    }
}
