using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeskGrid.App.Helpers;
using DeskGrid.Core.Models;
using DeskGrid.Core.Services;
using DeskGrid.Win32.Services;

namespace DeskGrid.App.Views;

public partial class DesktopBoxWindow : Window
{
    public const double ShadowPadding = 14.0;
    private const double CollapsedCardHeight = 40.0;
    private static readonly SolidColorBrush DangerMenuBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0x8A, 0x8A));

    private readonly BoxConfig _box;
    private readonly AppConfig _appConfig;
    private readonly BoxManager _boxManager;
    private readonly DockManager _dockManager;
    private readonly ShellIconService _shellService;
    private readonly Func<string, IEnumerable<RectBounds>> _siblingBoundsProvider;
    private readonly Action _onShowDock;
    private readonly Action _onSafeExit;

    private readonly DispatcherTimer _hoverExpandTimer;
    private readonly DispatcherTimer _hoverCollapseTimer;

    private string? _currentSubfolderPath;
    private bool _isHoverExpanded;
    private bool _isContextMenuOpen;
    private bool _isRenaming;
    private string? _selectedItemId;
    private BoxItem? _selectedItem;
    private Point _dragStartPoint;
    private BoxItem? _dragCandidate;
    private Border? _dragCandidateBorder;
    private bool _isDraggingItem;
    private bool _pendingRefreshAfterDrag;
    private int _dragSourceItemIndex = -1;

    public string BoxId => _box.Id;
    public BoxConfig Config => _box;

    public RectBounds GetLogicalBounds() => new(
        Left + ShadowPadding,
        Top + ShadowPadding,
        Math.Max(220, (ActualWidth > 0 ? ActualWidth : Width) - ShadowPadding * 2),
        Math.Max(CollapsedCardHeight, (ActualHeight > 0 ? ActualHeight : Height) - ShadowPadding * 2));

    public DesktopBoxWindow(
        BoxConfig box,
        AppConfig appConfig,
        BoxManager boxManager,
        DockManager dockManager,
        ShellIconService shellService,
        Func<string, IEnumerable<RectBounds>> siblingBoundsProvider,
        Action onShowDock,
        Action onSafeExit)
    {
        InitializeComponent();

        _box = box;
        _appConfig = appConfig;
        _boxManager = boxManager;
        _dockManager = dockManager;
        _shellService = shellService;
        _siblingBoundsProvider = siblingBoundsProvider;
        _onShowDock = onShowDock;
        _onSafeExit = onSafeExit;

        _hoverExpandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hoverExpandTimer.Tick += (_, _) =>
        {
            _hoverExpandTimer.Stop();
            if (_box.IsCollapsed && !_isHoverExpanded)
            {
                _isHoverExpanded = true;
                ApplyCollapseVisuals();
            }
        };

        _hoverCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _hoverCollapseTimer.Tick += (_, _) =>
        {
            _hoverCollapseTimer.Stop();
            if (_box.IsCollapsed && _isHoverExpanded && !IsMouseOver && !_isContextMenuOpen && !_isRenaming)
            {
                _isHoverExpanded = false;
                ApplyCollapseVisuals();
            }
        };

        var workArea = SystemParameters.WorkArea;
        var logicalWidth = Math.Clamp(_box.Width, 220, Math.Max(220, workArea.Width - 24));
        var logicalHeight = Math.Clamp(_box.Height, 140, Math.Max(140, workArea.Height - 24));
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        var clampedLeft = Math.Clamp(_box.Left, virtualLeft, Math.Max(virtualLeft, virtualRight - logicalWidth));
        var clampedTop = Math.Clamp(_box.Top, virtualTop, Math.Max(virtualTop, virtualBottom - logicalHeight));

        Left = clampedLeft - ShadowPadding;
        Top = clampedTop - ShadowPadding;
        Width = logicalWidth + ShadowPadding * 2;
        Height = (_box.IsCollapsed ? CollapsedCardHeight : logicalHeight) + ShadowPadding * 2;

        SourceInitialized += OnSourceInitialized;
        Deactivated += (_, _) => UnselectAll();
        Loaded += (_, _) =>
        {
            ApplyVisualSettings();
            RefreshItems();
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        DesktopHostService.AttachWindowToDesktopLayer(this);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsLoaded)
            RefreshItems();
    }

    public void ApplyOpacity(double opacity)
    {
        _box.Opacity = Math.Clamp(opacity, 0.15, 0.90);
        var dark = ThemePalette.IsDarkMode(_appConfig.Appearance.Theme);
        ShadowBackdrop.Background = ThemePalette.CreateTintedSurfaceBrush(dark, _box.Opacity);
    }

    public void ApplyVisualSettings()
    {
        var dark = ThemePalette.IsDarkMode(_appConfig.Appearance.Theme);
        ShadowBackdrop.Background = ThemePalette.CreateTintedSurfaceBrush(dark, _box.Opacity);
        RootCard.Background = Brushes.Transparent;

        TitleText.Text = string.IsNullOrWhiteSpace(_currentSubfolderPath)
            ? _box.Title
            : $"{_box.Title} / {Path.GetFileName(_currentSubfolderPath)}";

        BackButton.Visibility = string.IsNullOrWhiteSpace(_currentSubfolderPath)
            ? Visibility.Collapsed
            : Visibility.Visible;

        ModeGlyph.Text = _box.Mode == BoxMode.FolderPortal ? "\uE8B7" : "\uE71D";
        ModeGlyph.ToolTip = _box.Mode == BoxMode.FolderPortal
            ? $"目录映射：{_box.FolderPath}"
            : "虚拟盒子（不移动磁盘原文件）";

        LockButton.Content = _box.IsLocked ? "\uE72E" : "\uE785";
        LockButton.Foreground = _box.IsLocked
            ? (Brush)FindResource("DgAccentBrush")
            : (Brush)FindResource("DgTextPrimaryBrush");

        var resizeVis = _box.IsLocked ? Visibility.Collapsed : Visibility.Visible;
        RightResizeThumb.Visibility = resizeVis;
        BottomResizeThumb.Visibility = resizeVis;
        CornerResizeThumb.Visibility = resizeVis;

        ApplyCollapseVisuals();
    }

    private void ApplyCollapseVisuals()
    {
        var showBody = !_box.IsCollapsed || _isHoverExpanded;
        BodyContainer.Visibility = showBody ? Visibility.Visible : Visibility.Collapsed;
        CollapseButton.Content = _box.IsCollapsed ? "\uE70D" : "\uE70E";
        Height = (showBody ? Math.Max(140, _box.Height) : CollapsedCardHeight) + ShadowPadding * 2;

        var allowResize = showBody && !_box.IsLocked;
        var vis = allowResize ? Visibility.Visible : Visibility.Collapsed;
        RightResizeThumb.Visibility = vis;
        BottomResizeThumb.Visibility = vis;
        CornerResizeThumb.Visibility = vis;
    }

    private void AnimateHeaderActions(bool visible)
    {
        HeaderActionsPanel.IsHitTestVisible = visible;
        var anim = new DoubleAnimation
        {
            To = visible ? 1.0 : 0.0,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        HeaderActionsPanel.BeginAnimation(OpacityProperty, anim);
    }

    public void RefreshItems()
    {
        if (_isDraggingItem)
        {
            _pendingRefreshAfterDrag = true;
            return;
        }

        var items = LoadCurrentItems();
        ItemsWrapPanel.Children.Clear();

        if (_selectedItemId is not null &&
            !items.Any(i => string.Equals(i.Id, _selectedItemId, StringComparison.Ordinal)))
        {
            _selectedItemId = null;
            _selectedItem = null;
        }

        if (items.Count == 0)
        {
            EmptyStatePanel.Visibility = Visibility.Visible;
            EmptyHintText.Text = _box.Mode == BoxMode.FolderPortal
                ? "文件夹为空，可拖入文件或新建内容"
                : "拖拽文件、文件夹或应用到此处";
            return;
        }

        EmptyStatePanel.Visibility = Visibility.Collapsed;
        var (tileWidth, iconPx, fontSize) = GetIconMetrics(_box.IconSize);

        foreach (var item in items)
        {
            var tile = BuildItemTile(item, tileWidth, iconPx, fontSize);
            ItemsWrapPanel.Children.Add(tile);
        }
    }

    private IReadOnlyList<BoxItem> LoadCurrentItems()
    {
        if (_box.Mode == BoxMode.FolderPortal && !string.IsNullOrWhiteSpace(_currentSubfolderPath) && Directory.Exists(_currentSubfolderPath))
        {
            var list = new List<BoxItem>();
            try
            {
                foreach (var entry in new DirectoryInfo(_currentSubfolderPath).EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (entry.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    var isDir = entry is DirectoryInfo;
                    var kind = isDir ? BoxItemKind.Folder : BoxManager.DetectItemKind(entry.FullName);
                    list.Add(new BoxItem
                    {
                        Id = entry.FullName.ToLowerInvariant(),
                        DisplayName = BoxManager.DeriveDisplayName(entry.FullName, kind),
                        TargetPath = entry.FullName,
                        Kind = kind,
                        DateModified = entry.LastWriteTime,
                        Extension = isDir ? string.Empty : entry.Extension
                    });
                }
            }
            catch
            {
                // Ignore transient errors
            }

            return BoxManager.SortItems(list, _box.SortMode);
        }

        return _boxManager.GetItems(_box.Id);
    }

    private static (double TileWidth, int IconSize, double FontSize) GetIconMetrics(BoxIconSize size) => size switch
    {
        BoxIconSize.Small => (68, 32, 11),
        BoxIconSize.Large => (98, 54, 12.5),
        _ => (74, 42, 11.5)
    };

    private Border BuildItemTile(BoxItem item, double tileWidth, int iconPx, double fontSize)
    {
        var isSelected = string.Equals(_selectedItemId, item.Id, StringComparison.Ordinal);

        var border = new Border
        {
            Width = tileWidth,
            Margin = new Thickness(2),
            Padding = new Thickness(5, 7, 5, 7),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = isSelected ? (Brush)FindResource("DgHoverBorderBrush") : Brushes.Transparent,
            Background = isSelected ? (Brush)FindResource("DgSelectedBrush") : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Tag = item,
            ToolTip = item.IsMissing ? $"{item.DisplayName}\n(目标已失效：{item.TargetPath})" : item.TargetPath
        };

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

        var iconContainer = new Grid
        {
            Width = iconPx,
            Height = iconPx,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 5)
        };

        var img = new Image
        {
            Width = iconPx,
            Height = iconPx,
            Stretch = Stretch.Uniform,
            Opacity = item.IsMissing ? 0.42 : 1.0,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        var dpi = VisualTreeHelper.GetDpi(this);
        var requestPx = ShellIconService.ComputePhysicalIconPixels(iconPx, Math.Max(dpi.DpiScaleX, dpi.DpiScaleY));
        if (_shellService.TryGetCachedIconForPath(item.TargetPath, requestPx, out var cachedBitmap) && cachedBitmap is not null)
        {
            img.Source = cachedBitmap;
            iconContainer.Children.Add(img);
        }
        else
        {
            var placeholder = new TextBlock
            {
                Text = item.Kind == BoxItemKind.Folder ? "\uE8B7" : item.Kind == BoxItemKind.Url ? "\uE774" : "\uE8A5",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = iconPx * 0.64,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("DgAccentBrush"),
                Opacity = item.IsMissing ? 0.42 : 0.92
            };
            iconContainer.Children.Add(placeholder);

            var targetPath = item.TargetPath;
            _ = Task.Run(async () =>
            {
                try
                {
                    var loaded = await _shellService.GetIconForPathAsync(targetPath, requestPx).ConfigureAwait(false);
                    if (loaded is null)
                        return;

                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        if (!IsLoaded)
                            return;

                        img.Source = loaded;
                        var idx = iconContainer.Children.IndexOf(placeholder);
                        if (idx >= 0)
                        {
                            iconContainer.Children.RemoveAt(idx);
                            iconContainer.Children.Insert(idx, img);
                        }
                    });
                }
                catch
                {
                    // Keep fallback glyph if extraction fails
                }
            });
        }

        if (item.IsMissing)
        {
            iconContainer.Children.Add(new TextBlock
            {
                Text = "\uE7BA",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = (Brush)FindResource("DgDangerBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            });
        }

        var label = new TextBlock
        {
            Text = item.DisplayName,
            FontSize = fontSize,
            FontWeight = FontWeights.Medium,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = fontSize * 2.85,
            Foreground = (Brush)FindResource("DgTextPrimaryBrush"),
            Opacity = item.IsMissing ? 0.55 : 0.98
        };

        stack.Children.Add(iconContainer);
        stack.Children.Add(label);
        border.Child = stack;

        border.MouseEnter += (_, _) =>
        {
            if (!string.Equals(_selectedItemId, item.Id, StringComparison.Ordinal))
            {
                border.Background = (Brush)FindResource("DgHoverBrush");
                border.BorderBrush = (Brush)FindResource("DgHoverBorderBrush");
            }
        };
        border.MouseLeave += (_, _) =>
        {
            if (!string.Equals(_selectedItemId, item.Id, StringComparison.Ordinal))
            {
                border.Background = Brushes.Transparent;
                border.BorderBrush = Brushes.Transparent;
            }
        };

        border.MouseLeftButtonDown += (_, e) =>
        {
            SelectItem(item, border);
            if (e.ClickCount == 2)
            {
                _dragCandidate = null;
                _dragCandidateBorder = null;
                OpenBoxItem(item);
                e.Handled = true;
            }
            else
            {
                _dragStartPoint = e.GetPosition(this);
                _dragCandidate = item;
                _dragCandidateBorder = border;
                _isDraggingItem = false;
            }
        };

        border.MouseLeftButtonUp += (_, _) =>
        {
            if (!_isDraggingItem)
            {
                _dragCandidate = null;
                _dragCandidateBorder = null;
            }
        };

        border.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null || _isDraggingItem)
                return;

            var current = e.GetPosition(this);
            var thresholdX = Math.Max(5.0, SystemParameters.MinimumHorizontalDragDistance);
            var thresholdY = Math.Max(5.0, SystemParameters.MinimumVerticalDragDistance);
            if (Math.Abs(current.X - _dragStartPoint.X) < thresholdX &&
                Math.Abs(current.Y - _dragStartPoint.Y) < thresholdY)
            {
                return;
            }

            var candidate = _dragCandidate;
            var draggedBorder = _dragCandidateBorder;
            _isDraggingItem = true;
            _pendingRefreshAfterDrag = false;
            _dragSourceItemIndex = FindItemTileIndex(candidate.Id);

            if (draggedBorder is not null)
                draggedBorder.Opacity = 0.38;

            try
            {
                var payload = new InternalDragPayload(
                    InternalDragSource.Box,
                    _box.Id,
                    candidate.Id,
                    candidate.TargetPath,
                    candidate.DisplayName);
                var data = DragDropFormats.CreateDataObject(payload);
                DragDrop.DoDragDrop(border, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            finally
            {
                if (draggedBorder is not null)
                    draggedBorder.Opacity = 1.0;

                HideReorderDropIndicator();
                var needsRefresh = _pendingRefreshAfterDrag;
                _dragCandidate = null;
                _dragCandidateBorder = null;
                _isDraggingItem = false;
                _dragSourceItemIndex = -1;
                _pendingRefreshAfterDrag = false;

                if (needsRefresh)
                    RefreshItems();
            }
        };

        border.MouseRightButtonUp += (_, e) =>
        {
            SelectItem(item, border);
            ShowItemContextMenu(item, border);
            e.Handled = true;
        };

        return border;
    }

    private void SelectItem(BoxItem item, Border selectedBorder)
    {
        _selectedItemId = item.Id;
        _selectedItem = item;
        foreach (var child in ItemsWrapPanel.Children.OfType<Border>())
        {
            var sel = ReferenceEquals(child, selectedBorder);
            child.Background = sel ? (Brush)FindResource("DgSelectedBrush") : Brushes.Transparent;
            child.BorderBrush = sel ? (Brush)FindResource("DgHoverBorderBrush") : Brushes.Transparent;
        }
    }

    public void UnselectAll()
    {
        if (_selectedItemId is null && _selectedItem is null)
            return;

        _selectedItemId = null;
        _selectedItem = null;
        foreach (var child in ItemsWrapPanel.Children.OfType<Border>())
        {
            child.Background = Brushes.Transparent;
            child.BorderBrush = Brushes.Transparent;
        }
    }

    private bool IsInsideItemTile(DependencyObject? source)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, RootCard))
        {
            if (current is Border border &&
                border.Tag is BoxItem &&
                ReferenceEquals(VisualTreeHelper.GetParent(border), ItemsWrapPanel))
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private void OnBoxCardMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsInsideItemTile(e.OriginalSource as DependencyObject))
            UnselectAll();
    }

    private void OpenBoxItem(BoxItem item)
    {
        if (_box.Mode == BoxMode.FolderPortal && item.Kind == BoxItemKind.Folder && Directory.Exists(item.TargetPath))
        {
            UnselectAll();
            _currentSubfolderPath = item.TargetPath;
            ApplyVisualSettings();
            RefreshItems();
            return;
        }

        try
        {
            _shellService.OpenPath(item.TargetPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开 \"{item.DisplayName}\"：{ex.Message}", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnNavigateBack(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentSubfolderPath) || string.IsNullOrWhiteSpace(_box.FolderPath))
            return;

        UnselectAll();
        var parent = Directory.GetParent(_currentSubfolderPath)?.FullName;
        var root = Path.GetFullPath(_box.FolderPath).TrimEnd('\\', '/');
        if (parent is null || string.Equals(parent.TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase))
            _currentSubfolderPath = null;
        else
            _currentSubfolderPath = parent;

        ApplyVisualSettings();
        RefreshItems();
    }

    private void ShowItemContextMenu(BoxItem item, FrameworkElement placementTarget)
    {
        var menu = ThemePalette.CreateThemedContextMenu();
        menu.Opened += (_, _) => _isContextMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            _isContextMenuOpen = false;
            if (!IsMouseOver) AnimateHeaderActions(false);
        };

        var openMi = new MenuItem { Header = "打开" };
        openMi.Click += (_, _) => OpenBoxItem(item);
        menu.Items.Add(openMi);

        if (item.Kind == BoxItemKind.Application && item.TargetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var adminMi = new MenuItem { Header = "以管理员身份运行" };
            adminMi.Click += (_, _) =>
            {
                try { _shellService.OpenPath(item.TargetPath, asAdmin: true); } catch { }
            };
            menu.Items.Add(adminMi);
        }

        var pinMi = new MenuItem { Header = "固定到 Dock 栏" };
        pinMi.Click += (_, _) =>
        {
            var resolved = item.TargetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                ? _shellService.ResolveShortcutTarget(item.TargetPath)
                : null;
            _dockManager.PinItem(item.TargetPath, item.DisplayName, resolved);
        };
        menu.Items.Add(pinMi);

        if (item.Kind != BoxItemKind.Url)
        {
            var revealMi = new MenuItem { Header = "打开文件所在位置" };
            revealMi.Click += (_, _) => _shellService.RevealInExplorer(item.TargetPath);
            menu.Items.Add(revealMi);
        }

        menu.Items.Add(new Separator());

        var renameMi = new MenuItem { Header = "重命名 (F2)" };
        renameMi.Click += (_, _) => BeginInlineRename(item);
        menu.Items.Add(renameMi);

        var deleteMi = new MenuItem
        {
            Header = _box.Mode == BoxMode.Virtual ? "从盒子移除 (Del)" : "删除到回收站 (Del)"
        };
        deleteMi.Click += (_, _) => DeleteOrRemoveItem(item);
        menu.Items.Add(deleteMi);

        menu.PlacementTarget = placementTarget;
        menu.IsOpen = true;
    }

    private void BeginInlineRename(BoxItem item)
    {
        var tile = ItemsWrapPanel.Children.OfType<Border>()
            .FirstOrDefault(b => b.Tag is BoxItem bi && bi.Id == item.Id);
        if (tile?.Child is not StackPanel stack || stack.Children.Count < 2)
            return;

        _isRenaming = true;
        var initialText = _box.Mode == BoxMode.FolderPortal
            ? Path.GetFileName(item.TargetPath)
            : item.DisplayName;

        var tb = new TextBox
        {
            Text = initialText,
            FontSize = 11.5,
            TextAlignment = TextAlignment.Center,
            Padding = new Thickness(2),
            MinWidth = 60
        };

        var originalLabel = stack.Children[1];
        stack.Children[1] = tb;
        tb.Focus();
        tb.SelectAll();

        var committed = false;
        void Finish(bool save)
        {
            if (committed) return;
            committed = true;
            _isRenaming = false;

            if (save && !string.IsNullOrWhiteSpace(tb.Text))
            {
                var newName = tb.Text.Trim();
                try
                {
                    if (_box.Mode == BoxMode.Virtual)
                    {
                        _boxManager.RenameVirtualItem(_box.Id, item.Id, newName);
                    }
                    else
                    {
                        _shellService.RenameFileSystemEntry(item.TargetPath, newName);
                        if (string.IsNullOrWhiteSpace(_currentSubfolderPath))
                            _boxManager.UpdateFolderCustomOrderOnRename(_box.Id, item.TargetPath, newName);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"重命名失败：{ex.Message}", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            stack.Children[1] = originalLabel;
            RefreshItems();
        }

        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        };
        tb.LostFocus += (_, _) => Finish(true);
    }

    private void DeleteOrRemoveItem(BoxItem item)
    {
        if (_box.Mode == BoxMode.Virtual)
        {
            _boxManager.RemoveVirtualItem(_box.Id, item.Id);
            RefreshItems();
            return;
        }

        var confirm = MessageBox.Show(
            $"确定将 \"{item.DisplayName}\" 移入系统回收站吗？",
            "DeskGrid",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            _shellService.MoveToRecycleBin(item.TargetPath);
            RefreshItems();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isRenaming)
            return;

        if (e.Key == Key.Escape)
        {
            if (_selectedItem is not null || _selectedItemId is not null)
            {
                UnselectAll();
                e.Handled = true;
            }
            return;
        }

        if (_selectedItem is null)
            return;

        if (e.Key == Key.F2)
        {
            BeginInlineRename(_selectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            DeleteOrRemoveItem(_selectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OpenBoxItem(_selectedItem);
            e.Handled = true;
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        UnselectAll();

        if (e.ClickCount == 2)
        {
            ToggleCollapse();
            e.Handled = true;
            return;
        }

        if (_box.IsLocked)
            return;

        try
        {
            DragMove();
            SnapAndSaveBounds();
        }
        catch
        {
            // Ignore interrupted drag
        }
    }

    private void SnapAndSaveBounds()
    {
        var workArea = new RectBounds(
            SystemParameters.WorkArea.Left,
            SystemParameters.WorkArea.Top,
            SystemParameters.WorkArea.Width,
            SystemParameters.WorkArea.Height);

        var logical = GetLogicalBounds();
        var snapped = GridSnapHelper.SnapPosition(
            logical,
            workArea,
            _siblingBoundsProvider(_box.Id),
            _appConfig.Behavior.EnableGridSnap,
            _appConfig.Behavior.GridSnapStep);

        Left = snapped.Left - ShadowPadding;
        Top = snapped.Top - ShadowPadding;
        _box.Left = snapped.Left;
        _box.Top = snapped.Top;
        _boxManager.UpdateBox(_box);
    }

    private void OnRightResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_box.IsLocked || (_box.IsCollapsed && !_isHoverExpanded)) return;
        Width = Math.Max(220 + ShadowPadding * 2, Width + e.HorizontalChange);
    }

    private void OnBottomResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_box.IsLocked || (_box.IsCollapsed && !_isHoverExpanded)) return;
        Height = Math.Max(140 + ShadowPadding * 2, Height + e.VerticalChange);
    }

    private void OnCornerResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_box.IsLocked || (_box.IsCollapsed && !_isHoverExpanded)) return;
        Width = Math.Max(220 + ShadowPadding * 2, Width + e.HorizontalChange);
        Height = Math.Max(140 + ShadowPadding * 2, Height + e.VerticalChange);
    }

    private void OnResizeDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_box.IsLocked) return;

        var logicalW = Width - ShadowPadding * 2;
        var logicalH = Height - ShadowPadding * 2;
        var (w, h) = GridSnapHelper.SnapSize(
            logicalW,
            logicalH,
            _appConfig.Behavior.EnableGridSnap,
            _appConfig.Behavior.GridSnapStep);

        Width = w + ShadowPadding * 2;
        Height = h + ShadowPadding * 2;
        _box.Width = w;
        _box.Height = h;
        _box.AutoFitHeight = false;
        _boxManager.UpdateBox(_box);
    }

    private void OnToggleLock(object sender, RoutedEventArgs e)
    {
        _box.IsLocked = !_box.IsLocked;
        _boxManager.UpdateBox(_box);
        ApplyVisualSettings();
    }

    private void OnToggleCollapse(object sender, RoutedEventArgs e) => ToggleCollapse();

    private void ToggleCollapse()
    {
        _box.IsCollapsed = !_box.IsCollapsed;
        _isHoverExpanded = false;
        if (_box.IsCollapsed)
            UnselectAll();
        _boxManager.UpdateBox(_box);
        ApplyCollapseVisuals();
    }

    private void OnBoxMouseEnter(object sender, MouseEventArgs e)
    {
        AnimateHeaderActions(true);
        _hoverCollapseTimer.Stop();
        if (_box.IsCollapsed && !_isHoverExpanded)
            _hoverExpandTimer.Start();
    }

    private void OnBoxMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isContextMenuOpen)
            AnimateHeaderActions(false);

        _hoverExpandTimer.Stop();
        if (_box.IsCollapsed && _isHoverExpanded)
            _hoverCollapseTimer.Start();
    }

    private void OnBoxCardRightClick(object sender, MouseButtonEventArgs e)
    {
        UnselectAll();
        ShowBoxOptionsMenu(RootCard, PlacementMode.MousePoint);
        e.Handled = true;
    }

    private void OnOpenBoxMenu(object sender, RoutedEventArgs e)
    {
        ShowBoxOptionsMenu(MenuButton, PlacementMode.Bottom);
    }

    private void ShowBoxOptionsMenu(FrameworkElement placementTarget, PlacementMode placement)
    {
        var menu = ThemePalette.CreateThemedContextMenu();
        menu.Placement = placement;
        menu.Opened += (_, _) => _isContextMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            _isContextMenuOpen = false;
            if (!IsMouseOver) AnimateHeaderActions(false);
        };

        var renameBoxMi = new MenuItem { Header = "重命名盒子..." };
        renameBoxMi.Click += (_, _) =>
        {
            var dlg = new InputPromptDialog("重命名盒子", "输入新的盒子名称：", _box.Title);
            if (dlg.ShowDialog() == true)
            {
                _box.Title = dlg.ResultText;
                _boxManager.UpdateBox(_box);
                ApplyVisualSettings();
            }
        };
        menu.Items.Add(renameBoxMi);

        var sizeMenu = new MenuItem { Header = "图标大小" };
        AddCheckItem(sizeMenu, "小图标", _box.IconSize == BoxIconSize.Small, () => SetIconSize(BoxIconSize.Small));
        AddCheckItem(sizeMenu, "中图标", _box.IconSize == BoxIconSize.Medium, () => SetIconSize(BoxIconSize.Medium));
        AddCheckItem(sizeMenu, "大图标", _box.IconSize == BoxIconSize.Large, () => SetIconSize(BoxIconSize.Large));
        menu.Items.Add(sizeMenu);

        var sortMenu = new MenuItem { Header = "排序方式" };
        AddCheckItem(sortMenu, "按名称", _box.SortMode == BoxSortMode.NameAsc, () => SetSortMode(BoxSortMode.NameAsc));
        AddCheckItem(sortMenu, "按类型", _box.SortMode == BoxSortMode.TypeAsc, () => SetSortMode(BoxSortMode.TypeAsc));
        AddCheckItem(sortMenu, "按修改时间", _box.SortMode == BoxSortMode.DateModifiedDesc, () => SetSortMode(BoxSortMode.DateModifiedDesc));
        AddCheckItem(sortMenu, "手动顺序", _box.SortMode == BoxSortMode.Manual, () => SetSortMode(BoxSortMode.Manual));
        menu.Items.Add(sortMenu);

        var opacityMenu = new MenuItem { Header = "玻璃通透度" };
        AddCheckItem(opacityMenu, "极透 (22%)", Math.Abs(_box.Opacity - 0.22) < 0.06, () => SetOpacity(0.22));
        AddCheckItem(opacityMenu, "液态玻璃 (35%)", Math.Abs(_box.Opacity - 0.35) < 0.06, () => SetOpacity(0.35));
        AddCheckItem(opacityMenu, "轻磨砂 (50%)", Math.Abs(_box.Opacity - 0.50) < 0.06, () => SetOpacity(0.50));
        AddCheckItem(opacityMenu, "加深 (65%)", Math.Abs(_box.Opacity - 0.65) < 0.06, () => SetOpacity(0.65));
        menu.Items.Add(opacityMenu);

        var lockMi = new MenuItem
        {
            Header = "锁定位置与大小",
            IsCheckable = true,
            IsChecked = _box.IsLocked
        };
        lockMi.Click += (_, _) =>
        {
            _box.IsLocked = !_box.IsLocked;
            _boxManager.UpdateBox(_box);
            ApplyVisualSettings();
        };
        menu.Items.Add(lockMi);

        if (_box.Mode == BoxMode.Virtual)
        {
            var cleanMi = new MenuItem { Header = "清理失效快捷方式" };
            cleanMi.Click += (_, _) =>
            {
                var removed = _boxManager.RemoveMissingVirtualItems(_box.Id);
                RefreshItems();
                if (removed == 0)
                    MessageBox.Show("当前没有失效的条目。", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Information);
            };
            menu.Items.Add(cleanMi);
        }
        else if (!string.IsNullOrWhiteSpace(_box.FolderPath))
        {
            var openFolderMi = new MenuItem { Header = "在资源管理器中打开目录" };
            openFolderMi.Click += (_, _) => _shellService.OpenPath(_currentSubfolderPath ?? _box.FolderPath);
            menu.Items.Add(openFolderMi);
        }

        menu.Items.Add(new Separator());

        var removeBoxMi = new MenuItem { Header = "移除此盒子" };
        removeBoxMi.Click += (_, _) =>
        {
            var tip = _box.Mode == BoxMode.FolderPortal
                ? $"确定移除映射盒子 \"{_box.Title}\" 吗？（不会删除本地文件夹中的任何文件）"
                : $"确定移除虚拟盒子 \"{_box.Title}\" 吗？（不会删除磁盘原文件）";

            if (MessageBox.Show(tip, "DeskGrid", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _boxManager.RemoveBox(_box.Id);
        };
        menu.Items.Add(removeBoxMi);

        // Anti-deadlock fallback: when native taskbar is auto-hidden AND bottom Dock is disabled,
        // guarantee at least one accessible recovery and safe-exit path on desktop box menus.
        if (_appConfig.Dock.AutoHideSystemTaskbar && !_appConfig.ShowDock)
        {
            menu.Items.Add(new Separator());

            var showDockMi = new MenuItem { Header = "显示底部 Dock 栏" };
            showDockMi.Click += (_, _) =>
            {
                menu.IsOpen = false;
                _onShowDock();
            };
            menu.Items.Add(showDockMi);

            var safeExitMi = new MenuItem
            {
                Header = "安全退出 DeskGrid",
                Foreground = DangerMenuBrush,
                FontWeight = FontWeights.SemiBold
            };
            safeExitMi.Click += (_, _) =>
            {
                menu.IsOpen = false;
                _onSafeExit();
            };
            menu.Items.Add(safeExitMi);
        }

        menu.PlacementTarget = placementTarget;
        menu.IsOpen = true;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static void AddCheckItem(MenuItem parent, string header, bool isChecked, Action onClick)
    {
        var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        mi.Click += (_, _) => onClick();
        parent.Items.Add(mi);
    }

    private void SetIconSize(BoxIconSize size)
    {
        _box.IconSize = size;
        _boxManager.UpdateBox(_box);
        RefreshItems();
    }

    private void SetSortMode(BoxSortMode sort)
    {
        _box.SortMode = sort;
        _boxManager.UpdateBox(_box);
        RefreshItems();
    }

    private void SetOpacity(double opacity)
    {
        _box.Opacity = opacity;
        _boxManager.UpdateBox(_box);
        ApplyVisualSettings();
    }

    private int FindItemTileIndex(string itemId)
    {
        for (var i = 0; i < ItemsWrapPanel.Children.Count; i++)
        {
            if (ItemsWrapPanel.Children[i] is Border tile &&
                tile.Tag is BoxItem bi &&
                string.Equals(bi.Id, itemId, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    private bool TryComputeBoxReorder(
        DragEventArgs e,
        string itemId,
        out int oldIndex,
        out int newIndex,
        out GridDropTarget dropTarget)
    {
        oldIndex = _dragSourceItemIndex >= 0 ? _dragSourceItemIndex : FindItemTileIndex(itemId);
        newIndex = oldIndex;
        dropTarget = default;

        if (oldIndex < 0 || ItemsWrapPanel.Children.Count < 2)
            return false;

        var ptInBody = e.GetPosition(BodyContainer);
        if (ptInBody.X < 0 || ptInBody.X > BodyContainer.ActualWidth ||
            ptInBody.Y < -4 || ptInBody.Y > BodyContainer.ActualHeight + 4)
        {
            return false;
        }

        var bounds = new List<RectBounds>(ItemsWrapPanel.Children.Count);
        for (var i = 0; i < ItemsWrapPanel.Children.Count; i++)
        {
            if (ItemsWrapPanel.Children[i] is FrameworkElement fe)
            {
                var offset = VisualTreeHelper.GetOffset(fe);
                bounds.Add(new RectBounds(offset.X, offset.Y, fe.ActualWidth, fe.ActualHeight));
            }
        }

        if (bounds.Count < 2)
            return false;

        var ptInWrap = e.GetPosition(ItemsWrapPanel);
        dropTarget = DragReorderCalculator.ComputeWrapGridDropTarget(ptInWrap.X, ptInWrap.Y, bounds);
        newIndex = DragReorderCalculator.ResolveTargetIndex(oldIndex, dropTarget.InsertionSlot, bounds.Count);
        return true;
    }

    private void AutoScrollDuringDrag(DragEventArgs e)
    {
        if (ItemsScrollViewer.ScrollableHeight <= 0)
            return;

        var pt = e.GetPosition(ItemsScrollViewer);
        const double edgeZone = 24.0;
        const double step = 10.0;

        if (pt.Y < edgeZone)
            ItemsScrollViewer.ScrollToVerticalOffset(Math.Max(0, ItemsScrollViewer.VerticalOffset - step));
        else if (pt.Y > ItemsScrollViewer.ViewportHeight - edgeZone)
            ItemsScrollViewer.ScrollToVerticalOffset(Math.Min(ItemsScrollViewer.ScrollableHeight, ItemsScrollViewer.VerticalOffset + step));
    }

    private void ShowReorderDropIndicator(GridDropTarget target)
    {
        ReorderDropIndicator.Height = target.IndicatorHeight;
        Canvas.SetLeft(ReorderDropIndicator, Math.Round(target.IndicatorX - ReorderDropIndicator.Width * 0.5));
        Canvas.SetTop(ReorderDropIndicator, Math.Round(target.IndicatorY));
        ReorderDropIndicator.Visibility = Visibility.Visible;
    }

    private void HideReorderDropIndicator()
    {
        ReorderDropIndicator.Visibility = Visibility.Collapsed;
    }

    private void OnBoxDragOver(object sender, DragEventArgs e)
    {
        AnimateHeaderActions(true);
        if (_box.IsCollapsed && !_isHoverExpanded)
        {
            _isHoverExpanded = true;
            ApplyCollapseVisuals();
        }

        var internalPayload = DragDropFormats.TryGetInternalPayload(e.Data);
        if (internalPayload is { Source: InternalDragSource.Box } &&
            string.Equals(internalPayload.SourceBoxId, _box.Id, StringComparison.Ordinal))
        {
            DropHighlightBorder.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(_currentSubfolderPath) &&
                TryComputeBoxReorder(e, internalPayload.ItemId, out var oldIndex, out var newIndex, out var dropTarget))
            {
                AutoScrollDuringDrag(e);
                e.Effects = DragDropEffects.Move;
                if (newIndex != oldIndex)
                    ShowReorderDropIndicator(dropTarget);
                else
                    HideReorderDropIndicator();
            }
            else
            {
                HideReorderDropIndicator();
                e.Effects = DragDropEffects.None;
            }

            e.Handled = true;
            return;
        }

        HideReorderDropIndicator();
        var paths = DragDropFormats.GetDroppedPaths(e.Data);
        if (paths.Length > 0)
        {
            e.Effects = _box.Mode == BoxMode.Virtual
                ? DragDropEffects.Link
                : (e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) ? DragDropEffects.Copy : DragDropEffects.Move);
            DropHighlightBorder.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnBoxDragLeave(object sender, DragEventArgs e)
    {
        DropHighlightBorder.Visibility = Visibility.Collapsed;
        HideReorderDropIndicator();
    }

    private void OnBoxDrop(object sender, DragEventArgs e)
    {
        DropHighlightBorder.Visibility = Visibility.Collapsed;
        HideReorderDropIndicator();

        var internalPayload = DragDropFormats.TryGetInternalPayload(e.Data);
        if (internalPayload is { Source: InternalDragSource.Box } &&
            string.Equals(internalPayload.SourceBoxId, _box.Id, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(_currentSubfolderPath) &&
                TryComputeBoxReorder(e, internalPayload.ItemId, out var oldIndex, out var newIndex, out _) &&
                newIndex != oldIndex)
            {
                _pendingRefreshAfterDrag = true;
                _boxManager.ReorderItem(_box.Id, oldIndex, newIndex);
            }

            e.Handled = true;
            return;
        }

        var paths = DragDropFormats.GetDroppedPaths(e.Data);
        if (paths.Length == 0) return;

        if (_box.Mode == BoxMode.Virtual)
        {
            foreach (var p in paths)
            {
                var name = internalPayload is not null && internalPayload.TargetPath == p
                    ? internalPayload.DisplayName
                    : null;
                _boxManager.AddVirtualItem(_box.Id, p, name);
            }
        }
        else
        {
            var destFolder = _currentSubfolderPath ?? _box.FolderPath;
            if (string.IsNullOrWhiteSpace(destFolder) || !Directory.Exists(destFolder))
                return;

            var forceCopy = e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) ||
                            internalPayload?.Source == InternalDragSource.Dock;

            foreach (var p in paths)
            {
                try
                {
                    _shellService.TransferIntoFolder(p, destFolder, forceCopy);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"无法放入 \"{Path.GetFileName(p)}\"：{ex.Message}", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        RefreshItems();
        e.Handled = true;
    }
}
