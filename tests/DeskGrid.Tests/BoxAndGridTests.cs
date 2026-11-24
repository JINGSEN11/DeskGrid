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
}
