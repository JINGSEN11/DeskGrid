using DeskGrid.Core.Models;
using DeskGrid.Core.Services;

namespace DeskGrid.Tests;

public sealed class BoxAndGridTests
{
    [TestFact]
    public void GridSnap_AlignsToGridAndSiblingEdges()
    {
        var workArea = new RectBounds(0, 0, 1920, 1040);
        var sibling = new RectBounds(48, 48, 360, 280);
        var dragged = new RectBounds(418, 51, 360, 280); // near sibling.Right + 12 = 420 and sibling.Top = 48

        var snapped = GridSnapHelper.SnapPosition(dragged, workArea, [sibling], enableGridSnap: true, gridStep: 12);
        AssertEx.Near(420, snapped.Left);
        AssertEx.Near(48, snapped.Top);
    }

    [TestFact]
    public void VirtualBox_RemoveItemDoesNotDeleteDiskFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskGridTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var sampleFile = Path.Combine(tempDir, "keep-me.txt");
            File.WriteAllText(sampleFile, "important data");

            var cfgSvc = new ConfigService(Path.Combine(tempDir, "cfg"));
            var cfg = new AppConfig();
            using var mgr = new BoxManager(cfg, cfgSvc);

            var box = mgr.CreateBox("测试虚拟盒", BoxMode.Virtual);
            AssertEx.True(mgr.AddVirtualItem(box.Id, sampleFile));
            AssertEx.False(mgr.AddVirtualItem(box.Id, sampleFile), "Duplicate virtual path should be ignored.");

            var items = mgr.GetItems(box.Id);
            AssertEx.Equal(1, items.Count);
            AssertEx.False(items[0].IsMissing);

            AssertEx.True(mgr.RemoveVirtualItem(box.Id, items[0].Id));
            AssertEx.Equal(0, mgr.GetItems(box.Id).Count);
            AssertEx.True(File.Exists(sampleFile), "Removing from virtual box MUST NOT delete disk file!");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestFact]
    public void VirtualBox_BlocksDuplicateShortcutsIgnoringCaseTrailingSlashAndQuotes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskGridTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfgSvc = new ConfigService(Path.Combine(tempDir, "cfg"));
            var cfg = new AppConfig();
            using var mgr = new BoxManager(cfg, cfgSvc);

            var box = mgr.CreateBox("去重测试盒", BoxMode.Virtual);

            // File / shortcut path deduplication (case, slash direction, quotes)
            AssertEx.True(mgr.AddVirtualItem(box.Id, @"C:\Users\Demo\Desktop\App.lnk"));
            AssertEx.False(mgr.AddVirtualItem(box.Id, @"c:\users\demo\desktop\app.lnk"), "Case-only duplicate must be blocked.");
            AssertEx.False(mgr.AddVirtualItem(box.Id, "  \"C:/Users/Demo/Desktop/App.lnk\"  "), "Quoted/slash-normalized duplicate must be blocked.");

            // Folder path deduplication (trailing slash)
            AssertEx.True(mgr.AddVirtualItem(box.Id, @"C:\Projects\DeskGrid\"));
            AssertEx.False(mgr.AddVirtualItem(box.Id, @"C:\Projects\DeskGrid"), "Trailing-slash folder duplicate must be blocked.");
            AssertEx.False(mgr.AddVirtualItem(box.Id, @"c:/projects/deskgrid///"), "Multiple trailing slashes duplicate must be blocked.");

            // URL deduplication (case and trailing slash)
            AssertEx.True(mgr.AddVirtualItem(box.Id, "https://github.com/JINGSEN11/DeskGrid/"));
            AssertEx.False(mgr.AddVirtualItem(box.Id, "HTTPS://GITHUB.COM/JINGSEN11/DeskGrid"), "URL trailing-slash/case duplicate must be blocked.");

            AssertEx.Equal(3, box.VirtualItems.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestFact]
    public void FolderPortalBox_ReadsRealFolderAndSortsFoldersFirst()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskGridTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var portalDir = Path.Combine(tempDir, "portal");
            Directory.CreateDirectory(Path.Combine(portalDir, "ZFolder"));
            File.WriteAllText(Path.Combine(portalDir, "a-file.txt"), "hello");
            File.WriteAllText(Path.Combine(portalDir, "desktop.ini"), "ignored");

            var cfgSvc = new ConfigService(Path.Combine(tempDir, "cfg"));
            var cfg = new AppConfig();
            using var mgr = new BoxManager(cfg, cfgSvc);

            var box = mgr.CreateBox("目录盒", BoxMode.FolderPortal, portalDir);
            var items = mgr.GetItems(box.Id);

            AssertEx.Equal(2, items.Count, "desktop.ini should be skipped.");
            AssertEx.Equal("ZFolder", items[0].DisplayName, "Folders should appear before files in NameAsc.");
            AssertEx.Equal(BoxItemKind.Folder, items[0].Kind);
            AssertEx.Equal("a-file.txt", items[1].DisplayName);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestFact]
    public void VirtualBox_ReorderItem_SwitchesToManualAndPersistsOrder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskGridTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var fA = Path.Combine(tempDir, "alpha.txt");
            var fB = Path.Combine(tempDir, "beta.txt");
            var fC = Path.Combine(tempDir, "gamma.txt");
            File.WriteAllText(fA, "a");
            File.WriteAllText(fB, "b");
            File.WriteAllText(fC, "c");

            var cfgSvc = new ConfigService(Path.Combine(tempDir, "cfg"));
            var cfg = new AppConfig();
            using var mgr = new BoxManager(cfg, cfgSvc);

            var box = mgr.CreateBox("测试虚拟盒", BoxMode.Virtual);
            mgr.AddVirtualItem(box.Id, fC, "Gamma");
            mgr.AddVirtualItem(box.Id, fA, "Alpha");
            mgr.AddVirtualItem(box.Id, fB, "Beta");

            // Start sorted by NameAsc: visual order is [Alpha, Beta, Gamma]
            box.SortMode = BoxSortMode.NameAsc;
            mgr.UpdateBox(box);
            AssertEx.Equal("Alpha,Beta,Gamma", string.Join(",", mgr.GetItems(box.Id).Select(i => i.DisplayName)));

            // Drag visual index 2 (Gamma) to index 0 => [Gamma, Alpha, Beta], SortMode auto-switches to Manual
            AssertEx.True(mgr.ReorderItem(box.Id, 2, 0));
            AssertEx.Equal(BoxSortMode.Manual, box.SortMode);
            AssertEx.Equal("Gamma,Alpha,Beta", string.Join(",", mgr.GetItems(box.Id).Select(i => i.DisplayName)));
            AssertEx.Equal("Gamma,Alpha,Beta", string.Join(",", box.VirtualItems.Select(i => i.DisplayName)));

            // Reload from disk to verify persistence
            var reloaded = cfgSvc.LoadOrCreate(() => new AppConfig());
            using var mgr2 = new BoxManager(reloaded, cfgSvc);
            AssertEx.Equal(BoxSortMode.Manual, reloaded.Boxes[0].SortMode);
            AssertEx.Equal("Gamma,Alpha,Beta", string.Join(",", mgr2.GetItems(box.Id).Select(i => i.DisplayName)));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestFact]
    public void FolderPortalBox_ReorderItem_PersistsCustomItemOrderAndAppendsNewFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskGridTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var portalDir = Path.Combine(tempDir, "portal");
            Directory.CreateDirectory(portalDir);
            var f1 = Path.Combine(portalDir, "a.txt");
            var f2 = Path.Combine(portalDir, "b.txt");
            var f3 = Path.Combine(portalDir, "c.txt");
            File.WriteAllText(f1, "1");
            File.WriteAllText(f2, "2");
            File.WriteAllText(f3, "3");

            var cfgSvc = new ConfigService(Path.Combine(tempDir, "cfg"));
            var cfg = new AppConfig();
            using var mgr = new BoxManager(cfg, cfgSvc);

            var box = mgr.CreateBox("目录映射盒", BoxMode.FolderPortal, portalDir);
            AssertEx.Equal(BoxSortMode.NameAsc, box.SortMode);
            AssertEx.Equal("a.txt,b.txt,c.txt", string.Join(",", mgr.GetItems(box.Id).Select(i => i.DisplayName)));

            // Drag c.txt (index 2) to index 0 => [c.txt, a.txt, b.txt]
            AssertEx.True(mgr.ReorderItem(box.Id, 2, 0));
            AssertEx.Equal(BoxSortMode.Manual, box.SortMode);
            AssertEx.Equal("c.txt,a.txt,b.txt", string.Join(",", box.CustomItemOrder));
            AssertEx.Equal("c.txt,a.txt,b.txt", string.Join(",", mgr.GetItems(box.Id).Select(i => i.DisplayName)));

            // Disk files remain untouched
            AssertEx.True(File.Exists(f1) && File.Exists(f2) && File.Exists(f3));

            // Add a brand new file on disk (even starting with "0" alphabetically): must appear at tail!
            var fNew = Path.Combine(portalDir, "0-new.txt");
            File.WriteAllText(fNew, "new");
            AssertEx.Equal("c.txt,a.txt,b.txt,0-new.txt", string.Join(",", mgr.GetItems(box.Id).Select(i => i.DisplayName)));

            // Rename keeps slot position via UpdateFolderCustomOrderOnRename
            var f3Renamed = Path.Combine(portalDir, "z-renamed.txt");
            File.Move(f3, f3Renamed);
            mgr.UpdateFolderCustomOrderOnRename(box.Id, f3, "z-renamed.txt");
            AssertEx.Equal("z-renamed.txt,a.txt,b.txt,0-new.txt", string.Join(",", mgr.GetItems(box.Id).Select(i => i.DisplayName)));

            // Reload config from disk to verify persistence
            var reloaded = cfgSvc.LoadOrCreate(() => new AppConfig());
            using var mgr2 = new BoxManager(reloaded, cfgSvc);
            AssertEx.Equal(BoxSortMode.Manual, reloaded.Boxes[0].SortMode);
            AssertEx.Equal("z-renamed.txt,a.txt,b.txt", string.Join(",", reloaded.Boxes[0].CustomItemOrder));
            AssertEx.Equal("z-renamed.txt,a.txt,b.txt,0-new.txt", string.Join(",", mgr2.GetItems(box.Id).Select(i => i.DisplayName)));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestFact]
    public void DragReorderCalculator_WrapGridMultiRowDropTargets()
    {
        // 2 rows: Row 0 has items 0,1,2 (y=2..82); Row 1 has items 3,4 (y=86..166)
        var rects = new RectBounds[]
        {
            new(2, 2, 74, 80),
            new(80, 2, 74, 80),
            new(158, 2, 74, 80),
            new(2, 86, 74, 80),
            new(80, 86, 74, 80)
        };

        // End of Row 0 (right half of item 2): slot 3, indicator stays on Row 0 right of item 2
        var row0End = DragReorderCalculator.ComputeWrapGridDropTarget(220, 40, rects);
        AssertEx.Equal(3, row0End.InsertionSlot);
        AssertEx.Near(234, row0End.IndicatorX);
        AssertEx.Near(6, row0End.IndicatorY);

        // Start of Row 1 (left half of item 3): slot 3, indicator stays on Row 1 left of item 3
        var row1Start = DragReorderCalculator.ComputeWrapGridDropTarget(10, 120, rects);
        AssertEx.Equal(3, row1Start.InsertionSlot);
        AssertEx.Near(0, row1Start.IndicatorX);
        AssertEx.Near(90, row1Start.IndicatorY);

        // Below last row: snaps to end of last row (slot 5)
        var belowAll = DragReorderCalculator.ComputeWrapGridDropTarget(15, 210, rects);
        AssertEx.Equal(5, belowAll.InsertionSlot);
        AssertEx.Near(156, belowAll.IndicatorX);
    }
}
