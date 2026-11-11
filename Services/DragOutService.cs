using System;
using System.IO;
using System.Windows;
using VajraShelf.Models;

namespace VajraShelf.Services;

public sealed class DragOutService
{
    public IDataObject CreateDataObject(ShelfItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var data = new DataObject();

        switch (item.Kind)
        {
            case ShelfItemKind.File:
            case ShelfItemKind.Folder:
                AddFileReference(data, item.Payload);
                break;

            case ShelfItemKind.Image:
                string imagePath = !string.IsNullOrWhiteSpace(item.OwnedCachePath)
                    ? item.OwnedCachePath
                    : item.Payload;
                AddFileReference(data, imagePath);
                break;

            case ShelfItemKind.Link:
            case ShelfItemKind.Color:
            case ShelfItemKind.Text:
            default:
                AddText(data, item.Payload);
                break;
        }

        return data;
    }

    private static void AddFileReference(DataObject data, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string canonical;
        try
        {
            canonical = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            canonical = path;
        }

        data.SetData(DataFormats.FileDrop, new[] { canonical }, false);
        AddText(data, canonical);
    }

    private static void AddText(DataObject data, string? text)
    {
        string value = text ?? string.Empty;
        data.SetData(DataFormats.UnicodeText, value, false);
        data.SetData(DataFormats.Text, value, false);
    }
}
