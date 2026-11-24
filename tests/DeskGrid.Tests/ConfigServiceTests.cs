using DeskGrid.Core.Models;
using DeskGrid.Core.Services;

namespace DeskGrid.Tests;

public sealed class ConfigServiceTests
{
    [TestFact]
    public void SaveAndLoad_AtomicBackupAndCorruptRecovery()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskGridTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new ConfigService(tempDir);
            var initial = svc.LoadOrCreate(FirstRunInitializer.CreateDefaultConfig);
            AssertEx.Equal(2, initial.Boxes.Count);

            initial.Boxes[0].Title = "已修改的桌面";
            svc.Save(initial);

            initial.Boxes[0].Title = "第二次修改";
            svc.Save(initial);

            // Corrupt main config file; LoadOrCreate must recover from config.bak1.json
            File.WriteAllText(svc.ConfigFilePath, "{ broken json !!!");
            var recovered = svc.LoadOrCreate(() => new AppConfig());
            AssertEx.Equal("已修改的桌面", recovered.Boxes[0].Title, "Should recover from backup 1.");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestFact]
    public void ExportAndImport_RoundTripPreservesBoxesAndDock()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskGridTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new ConfigService(tempDir);
            var cfg = FirstRunInitializer.CreateDefaultConfig();
            cfg.Dock.MagnificationScale = 1.55;
            cfg.Boxes.Add(new BoxConfig { Title = "项目区", Mode = BoxMode.Virtual });

            var exportFile = Path.Combine(tempDir, "backup.deskgrid");
            svc.ExportTo(exportFile, cfg);
            AssertEx.True(File.Exists(exportFile));

            var imported = svc.ImportFrom(exportFile);
            AssertEx.Equal(3, imported.Boxes.Count);
            AssertEx.Equal("项目区", imported.Boxes[2].Title);
            AssertEx.Near(1.55, imported.Dock.MagnificationScale);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestFact]
    public void Migration_UpgradesSchemaV1HeavyOpacityToLiquidGlassV2()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskGridTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var v1Json = """
            {
              "schemaVersion": 1,
              "appearance": { "defaultBoxOpacity": 0.78 },
              "boxes": [
                { "id": "box-desktop-portal", "title": "桌面文件", "opacity": 0.78, "left": 36, "top": 36, "width": 384, "height": 312 },
                { "id": "box-quick-virtual", "title": "常用快捷", "opacity": 0.78, "left": 444, "top": 36, "width": 348, "height": 312 }
              ]
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "config.json"), v1Json);

            var svc = new ConfigService(tempDir);
            var migrated = svc.LoadOrCreate(() => new AppConfig());
            AssertEx.Equal(2, migrated.SchemaVersion);
            AssertEx.Near(0.35, migrated.Appearance.DefaultBoxOpacity);
            AssertEx.Near(0.35, migrated.Boxes[0].Opacity);
            AssertEx.Near(340, migrated.Boxes[0].Width);
            AssertEx.Near(260, migrated.Boxes[0].Height);
            AssertEx.Near(20, migrated.Boxes[1].Top - (migrated.Boxes[0].Top + migrated.Boxes[0].Height));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
