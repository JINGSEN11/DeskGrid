using System.IO;
using System.Windows;

namespace DeskGrid.App.Helpers;

public enum InternalDragSource
{
    Box,
    Dock
}

public sealed record InternalDragPayload(
    InternalDragSource Source,
    string? SourceBoxId,
    string ItemId,
    string TargetPath,
    string DisplayName);

public static class DragDropFormats
{
    public const string InternalItemFormat = "DeskGrid.InternalDragPayload";

    public static DataObject CreateDataObject(InternalDragPayload payload)
    {
        var data = new DataObject();
        data.SetData(InternalItemFormat, payload);

        if (!string.IsNullOrWhiteSpace(payload.TargetPath) &&
            (File.Exists(payload.TargetPath) || Directory.Exists(payload.TargetPath)))
        {
            data.SetFileDropList([payload.TargetPath]);
        }

        return data;
    }

    public static InternalDragPayload? TryGetInternalPayload(IDataObject data)
    {
        if (data.GetDataPresent(InternalItemFormat) &&
            data.GetData(InternalItemFormat) is InternalDragPayload payload)
        {
            return payload;
        }
        return null;
    }

    public static string[] GetDroppedPaths(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) &&
            data.GetData(DataFormats.FileDrop) is string[] files)
        {
            return files;
        }

        var internalPayload = TryGetInternalPayload(data);
        if (internalPayload is not null && !string.IsNullOrWhiteSpace(internalPayload.TargetPath))
            return [internalPayload.TargetPath];

        return [];
    }
}
