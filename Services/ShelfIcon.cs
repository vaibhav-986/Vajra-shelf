using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VajraShelf.Services;

/// <summary>Creates Vajra Shelf's original, self-contained tray artwork.</summary>
public static class ShelfIcon
{
    public const int DefaultSize = 32;

    public static Icon Create(int size = DefaultSize)
    {
        if (size < 16 || size > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Icon size must be between 16 and 256 pixels.");
        }

        using Bitmap bitmap = new(size, size, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float scale = size / 32f;
        using SolidBrush back = new(Color.FromArgb(255, 25, 31, 39));
        using SolidBrush shelf = new(Color.FromArgb(255, 58, 69, 82));
        using SolidBrush accent = new(Color.FromArgb(255, 143, 227, 207));
        using SolidBrush highlight = new(Color.FromArgb(255, 235, 250, 246));
        using Pen edge = new(Color.FromArgb(255, 10, 14, 19), Math.Max(1f, 1.2f * scale));

        graphics.FillEllipse(back, 1.5f * scale, 1.5f * scale, 29f * scale, 29f * scale);

        PointF[] vajra =
        {
            new(16f * scale, 6f * scale),
            new(21.5f * scale, 12f * scale),
            new(18.3f * scale, 16f * scale),
            new(22.5f * scale, 20.5f * scale),
            new(16f * scale, 26f * scale),
            new(9.5f * scale, 20.5f * scale),
            new(13.7f * scale, 16f * scale),
            new(10.5f * scale, 12f * scale)
        };
        graphics.FillPolygon(accent, vajra);
        graphics.DrawPolygon(edge, vajra);

        PointF[] center =
        {
            new(16f * scale, 11f * scale),
            new(19.7f * scale, 16f * scale),
            new(16f * scale, 21f * scale),
            new(12.3f * scale, 16f * scale)
        };
        graphics.FillPolygon(highlight, center);

        RectangleF shelfBody = new(5f * scale, 22f * scale, 22f * scale, 5f * scale);
        graphics.FillRectangle(shelf, shelfBody);
        graphics.DrawRectangle(edge, shelfBody.X, shelfBody.Y, shelfBody.Width, shelfBody.Height);
        graphics.FillRectangle(accent, 5f * scale, 22f * scale, 22f * scale, Math.Max(1.5f, 2f * scale));

        IntPtr iconHandle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(iconHandle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            _ = DestroyIcon(iconHandle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
