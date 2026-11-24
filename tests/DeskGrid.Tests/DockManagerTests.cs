using DeskGrid.Core.Models;
using DeskGrid.Core.Services;

namespace DeskGrid.Tests;

public sealed class DockManagerTests
{
    [TestFact]
    public void BuildSnapshot_MergesPinnedAndSeparatesRunningOnly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskGridTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfgSvc = new ConfigService(tempDir);
            var cfg = new AppConfig();
            cfg.Dock.PinnedItems.Add(new DockPinnedItem
            {
                Id = "pin-code",
                DisplayName = "VS Code",
                TargetPath = @"C:\Apps\Code.exe",
                AppKey = "code"
            });

            var mgr = new DockManager(cfg, cfgSvc);
            var running = new List<RunningWindowInfo>
            {
                new() { Handle = 101, ProcessName = "Code", ExecutablePath = @"C:\Apps\Code.exe", AppKey = "code", Title = "DeskGrid - VS Code", IsActive = true },
                new() { Handle = 102, ProcessName = "Code", ExecutablePath = @"C:\Apps\Code.exe", AppKey = "code", Title = "Other - VS Code", IsActive = false },
                new() { Handle = 201, ProcessName = "notepad", ExecutablePath = @"C:\Windows\notepad.exe", AppKey = "notepad", Title = "Untitled - Notepad", IsActive = false }
            };

            var snap = mgr.BuildSnapshot(running);
            AssertEx.Equal(1, snap.PinnedSlots.Count);
            AssertEx.Equal(2, snap.PinnedSlots[0].RunningCount);
            AssertEx.True(snap.PinnedSlots[0].IsActive);

            AssertEx.Equal(1, snap.RunningOnlySlots.Count);
            AssertEx.Equal("notepad", snap.RunningOnlySlots[0].AppKey);
            AssertEx.False(snap.RunningOnlySlots[0].IsPinned);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestFact]
    public void DecideClickAction_CoversLaunchActivateMinimizeAndMultiWindowPopup()
    {
        var notRunning = new DockSlotItem { RunningWindows = [] };
        AssertEx.Equal(DockClickActionKind.Launch, DockManager.DecideClickAction(notRunning).Action);

        var singleInactive = new DockSlotItem
        {
            RunningWindows = [new RunningWindowInfo { Handle = 11, IsActive = false, IsMinimized = false }]
        };
        var actDecision = DockManager.DecideClickAction(singleInactive);
        AssertEx.Equal(DockClickActionKind.ActivateWindow, actDecision.Action);
        AssertEx.Equal((nint)11, actDecision.WindowHandle);

        var singleActive = new DockSlotItem
        {
            RunningWindows = [new RunningWindowInfo { Handle = 22, IsActive = true, IsMinimized = false }]
        };
        var minDecision = DockManager.DecideClickAction(singleActive);
        AssertEx.Equal(DockClickActionKind.MinimizeWindow, minDecision.Action);
        AssertEx.Equal((nint)22, minDecision.WindowHandle);

        var multi = new DockSlotItem
        {
            RunningWindows =
            [
                new RunningWindowInfo { Handle = 31, IsActive = true },
                new RunningWindowInfo { Handle = 32, IsActive = false }
            ]
        };
        AssertEx.Equal(DockClickActionKind.ShowWindowListPopup, DockManager.DecideClickAction(multi).Action);
    }

    [TestFact]
    public void MagnificationCalculator_IsSymmetricSmoothAndClamped()
    {
        var centers = new double[] { 50, 100, 150, 200, 250 };
        var scales = DockMagnificationCalculator.ComputeScales(
            mouseX: 150,
            iconCentersX: centers,
            enabled: true,
            maxScale: 1.5,
            radius: 100);

        AssertEx.Near(1.5, scales[2]);
        AssertEx.Near(scales[1], scales[3]);
        AssertEx.True(scales[1] > 1.0 && scales[1] < 1.5);
        AssertEx.Near(1.0, scales[0]);
        AssertEx.Near(1.0, scales[4]);

        var disabled = DockMagnificationCalculator.ComputeScales(150, centers, enabled: false, maxScale: 1.5, radius: 100);
        foreach (var s in disabled)
            AssertEx.Near(1.0, s);
    }

    [TestFact]
    public void BuildSnapshot_PreservesStrictFifoOrderForRunningAppsOnActivationAndWindowSwitch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskGridTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfgSvc = new ConfigService(tempDir);
            var cfg = new AppConfig();
            var mgr = new DockManager(cfg, cfgSvc);

            // Initial snapshot: Chrome -> Terminal -> Notepad
            var snap1 = mgr.BuildSnapshot(
            [
                new RunningWindowInfo { Handle = 10, ProcessName = "chrome", ExecutablePath = @"C:\Apps\chrome.exe", AppKey = "chrome", Title = "Tab 1", IsActive = true },
                new RunningWindowInfo { Handle = 20, ProcessName = "wt", ExecutablePath = @"C:\Apps\wt.exe", AppKey = "wt", Title = "Terminal", IsActive = false },
                new RunningWindowInfo { Handle = 30, ProcessName = "notepad", ExecutablePath = @"C:\Windows\notepad.exe", AppKey = "notepad", Title = "Notes", IsActive = false }
            ]);
            AssertEx.Equal("chrome,wt,notepad", string.Join(",", snap1.RunningOnlySlots.Select(s => s.AppKey)));

            // Simulate user clicking Notepad -> EnumWindows returns Notepad first (Z-order)
            var snap2 = mgr.BuildSnapshot(
            [
                new RunningWindowInfo { Handle = 30, ProcessName = "notepad", ExecutablePath = @"C:\Windows\notepad.exe", AppKey = "notepad", Title = "Notes", IsActive = true },
                new RunningWindowInfo { Handle = 10, ProcessName = "chrome", ExecutablePath = @"C:\Apps\chrome.exe", AppKey = "chrome", Title = "Tab 1", IsActive = false },
                new RunningWindowInfo { Handle = 20, ProcessName = "wt", ExecutablePath = @"C:\Apps\wt.exe", AppKey = "wt", Title = "Terminal", IsActive = false }
            ]);
            AssertEx.Equal("chrome,wt,notepad", string.Join(",", snap2.RunningOnlySlots.Select(s => s.AppKey)));
            AssertEx.True(snap2.RunningOnlySlots[2].IsActive);
            AssertEx.False(snap2.RunningOnlySlots[0].IsActive);

            // Open a new app (calc) and close middle app (wt) -> calc appends to tail, wt is removed without reordering others
            var snap3 = mgr.BuildSnapshot(
            [
                new RunningWindowInfo { Handle = 40, ProcessName = "calc", ExecutablePath = @"C:\Windows\calc.exe", AppKey = "calc", Title = "Calculator", IsActive = true },
                new RunningWindowInfo { Handle = 30, ProcessName = "notepad", ExecutablePath = @"C:\Windows\notepad.exe", AppKey = "notepad", Title = "Notes", IsActive = false },
                new RunningWindowInfo { Handle = 10, ProcessName = "chrome", ExecutablePath = @"C:\Apps\chrome.exe", AppKey = "chrome", Title = "Tab 1", IsActive = false }
            ]);
            AssertEx.Equal("chrome,notepad,calc", string.Join(",", snap3.RunningOnlySlots.Select(s => s.AppKey)));

            // Reopen wt -> appends to tail after calc
            var snap4 = mgr.BuildSnapshot(
            [
                new RunningWindowInfo { Handle = 21, ProcessName = "wt", ExecutablePath = @"C:\Apps\wt.exe", AppKey = "wt", Title = "Terminal 2", IsActive = true },
                new RunningWindowInfo { Handle = 40, ProcessName = "calc", ExecutablePath = @"C:\Windows\calc.exe", AppKey = "calc", Title = "Calculator", IsActive = false },
                new RunningWindowInfo { Handle = 30, ProcessName = "notepad", ExecutablePath = @"C:\Windows\notepad.exe", AppKey = "notepad", Title = "Notes", IsActive = false },
                new RunningWindowInfo { Handle = 10, ProcessName = "chrome", ExecutablePath = @"C:\Apps\chrome.exe", AppKey = "chrome", Title = "Tab 1", IsActive = false }
            ]);
            AssertEx.Equal("chrome,notepad,calc,wt", string.Join(",", snap4.RunningOnlySlots.Select(s => s.AppKey)));

            // Multi-window FIFO handle order stability when second window is activated
            var snap5 = mgr.BuildSnapshot(
            [
                new RunningWindowInfo { Handle = 10, ProcessName = "chrome", ExecutablePath = @"C:\Apps\chrome.exe", AppKey = "chrome", Title = "Tab 1", IsActive = false },
                new RunningWindowInfo { Handle = 11, ProcessName = "chrome", ExecutablePath = @"C:\Apps\chrome.exe", AppKey = "chrome", Title = "Tab 2", IsActive = true }
            ]);
            AssertEx.Equal("10,11", string.Join(",", snap5.RunningOnlySlots[0].RunningWindows.Select(w => w.Handle)));

            var snap6 = mgr.BuildSnapshot(
            [
                new RunningWindowInfo { Handle = 11, ProcessName = "chrome", ExecutablePath = @"C:\Apps\chrome.exe", AppKey = "chrome", Title = "Tab 2", IsActive = true },
                new RunningWindowInfo { Handle = 10, ProcessName = "chrome", ExecutablePath = @"C:\Apps\chrome.exe", AppKey = "chrome", Title = "Tab 1", IsActive = false }
            ]);
            AssertEx.Equal("10,11", string.Join(",", snap6.RunningOnlySlots[0].RunningWindows.Select(w => w.Handle)));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestFact]
    public void ReorderPinnedItem_UpdatesOrderFiresEventAndPersistsToDisk()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskGridTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfgSvc = new ConfigService(tempDir);
            var cfg = new AppConfig();
            cfg.Dock.PinnedItems.AddRange(
            [
                new DockPinnedItem { Id = "p1", DisplayName = "Explorer", TargetPath = @"C:\Windows\explorer.exe", AppKey = "explorer" },
                new DockPinnedItem { Id = "p2", DisplayName = "Edge", TargetPath = @"C:\Apps\msedge.exe", AppKey = "msedge" },
                new DockPinnedItem { Id = "p3", DisplayName = "Notepad", TargetPath = @"C:\Windows\notepad.exe", AppKey = "notepad" },
                new DockPinnedItem { Id = "p4", DisplayName = "Terminal", TargetPath = @"C:\Apps\wt.exe", AppKey = "wt" }
            ]);

            var mgr = new DockManager(cfg, cfgSvc);
            var eventCount = 0;
            mgr.DockChanged += (_, _) => eventCount++;

            // Out-of-range and no-op calls should return false and not notify
            AssertEx.False(mgr.ReorderPinnedItem(-1, 2));
            AssertEx.False(mgr.ReorderPinnedItem(0, 99));
            AssertEx.False(mgr.ReorderPinnedItem(1, 1));
            AssertEx.Equal(0, eventCount);

            // Drag p1 (0) -> index 2 => [p2, p3, p1, p4]
            AssertEx.True(mgr.ReorderPinnedItem(0, 2));
            AssertEx.Equal("p2,p3,p1,p4", string.Join(",", cfg.Dock.PinnedItems.Select(p => p.Id)));
            AssertEx.Equal(1, eventCount);

            // Drag p4 (3) -> index 0 => [p4, p2, p3, p1]
            AssertEx.True(mgr.ReorderPinnedItem(3, 0));
            AssertEx.Equal("p4,p2,p3,p1", string.Join(",", cfg.Dock.PinnedItems.Select(p => p.Id)));
            AssertEx.Equal(2, eventCount);

            // Reload from disk to verify immediate persistence via ConfigService
            var reloaded = cfgSvc.LoadOrCreate(() => new AppConfig());
            AssertEx.Equal("p4,p2,p3,p1", string.Join(",", reloaded.Dock.PinnedItems.Select(p => p.Id)));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestFact]
    public void DragReorderCalculator_HorizontalSlotAndTargetIndexResolution()
    {
        var bounds = new HorizontalSlotBound[]
        {
            new(4, 56),   // [4..60], center 32
            new(68, 56),  // [68..124], center 96
            new(132, 56), // [132..188], center 160
            new(196, 56)  // [196..252], center 224
        };

        var beforeFirst = DragReorderCalculator.ComputeHorizontalDropTarget(10, bounds);
        AssertEx.Equal(0, beforeFirst.InsertionSlot);
        AssertEx.Near(2.0, beforeFirst.IndicatorX);

        var between1And2 = DragReorderCalculator.ComputeHorizontalDropTarget(128, bounds);
        AssertEx.Equal(2, between1And2.InsertionSlot);
        AssertEx.Near(128.0, between1And2.IndicatorX);

        var afterLast = DragReorderCalculator.ComputeHorizontalDropTarget(240, bounds);
        AssertEx.Equal(4, afterLast.InsertionSlot);
        AssertEx.Near(254.0, afterLast.IndicatorX);

        // Target index conversions
        AssertEx.Equal(0, DragReorderCalculator.ResolveTargetIndex(0, 0, 4)); // before self -> no-op
        AssertEx.Equal(0, DragReorderCalculator.ResolveTargetIndex(0, 1, 4)); // after self -> no-op
        AssertEx.Equal(1, DragReorderCalculator.ResolveTargetIndex(0, 2, 4)); // drag right past 1
        AssertEx.Equal(3, DragReorderCalculator.ResolveTargetIndex(0, 4, 4)); // drag to tail
        AssertEx.Equal(0, DragReorderCalculator.ResolveTargetIndex(3, 0, 4)); // drag left to head
        AssertEx.Equal(1, DragReorderCalculator.ResolveTargetIndex(3, 1, 4)); // drag left before 1
    }
}
