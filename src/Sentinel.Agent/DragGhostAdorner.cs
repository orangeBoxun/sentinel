using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sentinel.Agent;

/// <summary>
/// 拖动判断链时跟着鼠标走的那一行。
///
/// <para>
/// 用**静态快照**画，不直接用 <see cref="VisualBrush"/>：原行在拖动期间会被隐去，
/// VisualBrush 会把那个"看不见"一起画进来，影子就成了一张透明图。
/// </para>
/// <para>
/// 位置更新必须同时 <see cref="UIElement.InvalidateVisual"/> —— 只调 InvalidateArrange
/// 只保证重新排版，不保证重画，影子会停在起点不动（外面看着就是"影子没了"）。
/// </para>
/// </summary>
public sealed class DragGhostAdorner : Adorner
{
    private readonly ImageBrush _brush;
    private readonly Size _size;
    private double _x;
    private double _y;

    public DragGhostAdorner(UIElement adornedElement, FrameworkElement source)
        : base(adornedElement)
    {
        IsHitTestVisible = false;

        var width = Math.Max(1, (int)Math.Ceiling(source.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(source.ActualHeight));

        _size = new Size(width, height);

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(source);

        _brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
    }

    public void MoveTo(double x, double y)
    {
        _x = x;
        _y = y;

        InvalidateVisual();
        InvalidateArrange();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.PushOpacity(0.92);
        drawingContext.DrawRectangle(_brush, null, new Rect(_x, _y, _size.Width, _size.Height));
        drawingContext.Pop();
    }
}
