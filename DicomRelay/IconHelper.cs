using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DicomRelay
{
    /// <summary>
    /// Generates tray icons programmatically — no image files needed.
    /// </summary>
    internal static class IconHelper
    {
        public static Icon MakeRunning() => Make(Color.FromArgb(63, 185, 80));   // green
        public static Icon MakeStopped() => Make(Color.FromArgb(139, 148, 158)); // grey
        public static Icon MakeError()   => Make(Color.FromArgb(248, 81, 73));   // red

        private static Icon Make(Color fill)
        {
            using var bmp = new Bitmap(32, 32);
            using var g   = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Rounded square background
            var rect = new Rectangle(1, 1, 30, 30);
            using var path = RoundedRect(rect, 7);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);

            // "D" letter in white
            using var font = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.White);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("D", font, white, new RectangleF(0, 0, 32, 32), sf);

            return BitmapToIcon(bmp);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Icon BitmapToIcon(Bitmap bmp)
        {
            var hIcon = bmp.GetHicon();
            var icon  = Icon.FromHandle(hIcon);
            // Clone so we can destroy the handle
            var clone = (Icon)icon.Clone();
            DestroyIcon(hIcon);
            return clone;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);
    }
}
