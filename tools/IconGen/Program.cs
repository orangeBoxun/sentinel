// Renders the Sentinel mark at every size Windows asks for and packs them into one .ico.
//
// A tool rather than seven committed bitmaps: the mark is a squircle, a gradient and a shield, and
// every size is derived from those three, so changing the palette or the proportions is an edit here
// and a re-run — not a redraw in an image editor seven times over.
//
//   dotnet run --project tools/IconGen -- <out.ico> [preview.png]

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

var output = args.Length > 0 ? args[0] : "sentinel.ico";
int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };

var images = new List<(int size, byte[] bytes)>();
foreach (var size in sizes)
{
    using var bmp = Render(size);
    images.Add((size, Encode(bmp, asDib: size <= 128)));
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllBytes(output, Pack(images));
Console.WriteLine($"{output} — {images.Count} 个尺寸，{new FileInfo(output).Length / 1024} KB");

if (args.Length > 1)
{
    // Every size drawn at 1:1 on a light background, biggest first. Judging an icon at 256 alone
    // says nothing about the size that actually matters: 16px in a taskbar.
    using var sheet = new Bitmap(700, 296, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(sheet);
    g.Clear(Color.FromArgb(255, 0xF3, 0xF4, 0xF6));

    var x = 20;
    foreach (var size in sizes.Reverse())
    {
        using var bmp = Render(size);
        g.DrawImage(bmp, x, 20, size, size);
        x += size + 16;
    }

    sheet.Save(args[1], ImageFormat.Png);
    Console.WriteLine($"预览：{args[1]}（从左到右 256 → 16，均为原始像素）");
}

// ------------------------------------------------------------------ 画

static Bitmap Render(int n)
{
    var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);

    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);

    using (var tile = RoundedSquare(n))
    using (var fill = new LinearGradientBrush(
               new RectangleF(0, 0, n, n),
               Color.FromArgb(255, 0x2F, 0x6B, 0xFF),
               Color.FromArgb(255, 0x7A, 0x4D, 0xF5),
               58f))
    {
        g.FillPath(fill, tile);
    }

    // White on the gradient, and nothing else. A shield is the most-read security mark there is,
    // which is the point: this icon has to survive being 16px wide in a taskbar. Detail added at
    // 256 would be mud at 16, and the tile is what carries the "current" look anyway.
    using (var shield = Shield(n))
    using (var white = new SolidBrush(Color.White))
    {
        g.FillPath(white, shield);
    }

    return bmp;
}

static GraphicsPath RoundedSquare(int n)
{
    var path = new GraphicsPath();
    var w = (float)n;
    var d = w * 0.225f * 2;

    path.AddArc(0, 0, d, d, 180, 90);
    path.AddArc(w - d, 0, d, d, 270, 90);
    path.AddArc(w - d, w - d, d, d, 0, 90);
    path.AddArc(0, w - d, d, d, 90, 90);
    path.CloseFigure();

    return path;
}

static GraphicsPath Shield(int n)
{
    var path = new GraphicsPath();
    var u = (float)n;

    var left = 0.280f * u;
    var right = 0.720f * u;
    var middle = 0.500f * u;
    var top = 0.175f * u;
    var waist = 0.430f * u;   // where the straight sides stop and the point begins
    var tip = 0.850f * u;

    path.AddLine(left, top, right, top);
    path.AddLine(right, top, right, waist);

    // The bottom point. The last control point deliberately sits *above* the tip instead of level
    // with it: level control points leave the two halves meeting tangentially, which closes the
    // outline in a curve and reads as a rounded rectangle rather than a shield.
    path.AddBezier(right, waist, right, 0.700f * u, 0.620f * u, 0.800f * u, middle, tip);
    path.AddBezier(middle, tip, 0.380f * u, 0.800f * u, left, 0.700f * u, left, waist);

    path.AddLine(left, waist, left, top);
    path.CloseFigure();

    return path;
}

// ------------------------------------------------------------------ 编码

// Small sizes as DIB, 256 as PNG. Windows has always understood DIB entries; PNG is only the
// convention for the largest one, and there is no reason to push the format further than it has to
// go — a shell that choked on a PNG entry would show a blank icon at that one size.
static byte[] Encode(Bitmap bmp, bool asDib)
{
    if (!asDib)
    {
        using var png = new MemoryStream();
        bmp.Save(png, ImageFormat.Png);
        return png.ToArray();
    }

    int w = bmp.Width, h = bmp.Height;
    int xorSize = w * 4 * h;
    int andSize = (w + 31) / 32 * 4 * h;

    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);

    bw.Write(40);                    // BITMAPINFOHEADER
    bw.Write(w);
    bw.Write(h * 2);                 // twice the height: colour rows, then the AND mask
    bw.Write((short)1);              // planes
    bw.Write((short)32);             // bits per pixel
    bw.Write(0);                     // BI_RGB
    bw.Write(xorSize + andSize);
    bw.Write(0);
    bw.Write(0);                     // pixels per metre
    bw.Write(0);
    bw.Write(0);                     // palette

    // Bottom-up BGRA, which is what a 32bpp ARGB bitmap already holds in memory — so the rows can go
    // out as they are, just in the other direction.
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try
    {
        var row = new byte[w * 4];
        for (var y = h - 1; y >= 0; y--)
        {
            Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
            bw.Write(row);
        }
    }
    finally
    {
        bmp.UnlockBits(data);
    }

    // The AND mask exists for 1-bit displays. A 32bpp entry carries its own alpha, so all-zeroes is
    // the correct no-op rather than an omission.
    bw.Write(new byte[andSize]);
    bw.Flush();

    return ms.ToArray();
}

static byte[] Pack(List<(int size, byte[] bytes)> images)
{
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);

    bw.Write((ushort)0);             // reserved
    bw.Write((ushort)1);             // 1 = icon
    bw.Write((ushort)images.Count);

    // Each entry stores the offset of its own image, so the whole directory has to be laid out
    // before the first byte of pixel data can be written.
    var offset = 6 + 16 * images.Count;
    foreach (var (size, bytes) in images)
    {
        var dimension = (byte)(size >= 256 ? 0 : size);   // 0 is how 256 is spelled here
        bw.Write(dimension);
        bw.Write(dimension);
        bw.Write((byte)0);           // palette size
        bw.Write((byte)0);           // reserved
        bw.Write((ushort)1);         // planes
        bw.Write((ushort)32);        // bits per pixel
        bw.Write(bytes.Length);
        bw.Write(offset);
        offset += bytes.Length;
    }

    foreach (var (_, bytes) in images)
        bw.Write(bytes);

    bw.Flush();
    return ms.ToArray();
}
