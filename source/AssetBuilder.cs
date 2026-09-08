using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Deterministic Windows icon packaging: preserve the supplied artwork, fit its visible bounds.
internal static class AssetBuilder
{
    private static Bitmap Fit(Bitmap source, int size, bool crop)
    {
        Rectangle bounds = new Rectangle(0, 0, source.Width, source.Height);
        if (crop)
        {
            int left = source.Width, top = source.Height, right = 0, bottom = 0;
            for (int y = 0; y < source.Height; y++)
                for (int x = 0; x < source.Width; x++)
                    if (source.GetPixel(x, y).A > 24) { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
            if (right >= left && bottom >= top) bounds = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }
        Bitmap output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(output))
        using (ImageAttributes attributes = new ImageAttributes())
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            double scale = (crop ? size * 0.9 : size) / Math.Max(bounds.Width, bounds.Height);
            int width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
            int height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
            graphics.DrawImage(source, new Rectangle((size - width) / 2, (size - height) / 2, width, height), bounds.X, bounds.Y, bounds.Width, bounds.Height, GraphicsUnit.Pixel, attributes);
        }
        return output;
    }

    public static void Main(string[] args)
    {
        string assets = args[0], output = args[1];
        foreach (string name in new[] { "starting", "running", "restarting", "stopping", "error" })
            using (Bitmap original = new Bitmap(Path.Combine(assets, name + ".png")))
            using (Bitmap icon = Fit(original, 64, true)) icon.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        List<byte[]> frames = new List<byte[]>();
        using (Bitmap original = new Bitmap(Path.Combine(assets, "wubuntu-logo.png")))
            foreach (int size in sizes)
                using (Bitmap icon = Fit(original, size, false))
                using (MemoryStream stream = new MemoryStream()) { icon.Save(stream, ImageFormat.Png); frames.Add(stream.ToArray()); }
        using (BinaryWriter writer = new BinaryWriter(File.Create(Path.Combine(output, "Wubuntu.ico"))))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + sizes.Length * 16;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) writer.Write(frame);
        }
    }
}
