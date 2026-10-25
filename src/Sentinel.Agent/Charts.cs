using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Sentinel.Agent;

/// <summary>
/// 用量页那几张图：折线、横向柱状、饼图。
///
/// <para>
/// 手画，不引第三方图表库 —— 安装包不大，也不多一层依赖。所有图都画进一个
/// <see cref="Canvas"/>：调用前先给 canvas 一个高度，宽度取它当前的 ActualWidth，
/// 所以窗口一改尺寸就重画一次（见 <c>UsageCanvas_SizeChanged</c>）。
/// </para>
/// <para>
/// 悬停提示是自绘的（<see cref="ChartTooltip"/>），不是 WPF 自带的 ToolTip：
/// 自带那个弹出延迟长、样式也跟不上，而且没法做"换元素时淡入"的动画。
/// </para>
/// </summary>
internal static class Charts
{
    /// <summary>悬停卡片的正文：一个标题 + 若干行「标签：值」。</summary>
    public sealed record Tip(string Title, IReadOnlyList<string> Lines);

    /// <summary>系列配色。浅底色上都能看清，相邻两色差别够大。</summary>
    private static readonly Brush[] Palette =
    {
        Freeze("#4A6FA5"), Freeze("#C4703E"), Freeze("#4E9A73"), Freeze("#95609B"),
        Freeze("#B8863B"), Freeze("#3F8A9B"), Freeze("#A45B6B"), Freeze("#7A7A7A"),
    };

    private static readonly Brush Axis = Freeze("#E3E3E3");
    private static readonly Brush Ink = Freeze("#6B6B6B");
    private static readonly Brush InkFaint = Freeze("#9A9A9A");

    private const double LabelSize = 10.5;

    public sealed record Series(string Name, Brush Brush, double[] Values);

    public static Brush ColorOf(int index) => Palette[Math.Abs(index) % Palette.Length];

    /// <summary>折线图：横轴是日期/时段，每个系列一条线。悬停某一列看那一列的明细。</summary>
    public static void Line(Canvas canvas, IReadOnlyList<string> labels, IReadOnlyList<Series> series,
                            Func<int, Tip?>? tip = null)
    {
        Begin(canvas);
        canvas.Children.Clear();

        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;

        if (width < 80 || height < 60)
            return;

        const double left = 46;
        const double right = 14;
        const double top = 12;
        const double bottom = 26;

        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;

        if (plotWidth <= 10 || plotHeight <= 10)
            return;

        var max = NiceMax(series.SelectMany(s => s.Values).DefaultIfEmpty(0).Max());

        DrawYAxis(canvas, left, top, plotWidth, plotHeight, max);

        // 横轴标签：点太多就隔几个画一个，免得糊成一片。
        var step = Math.Max(1, (int)Math.Ceiling(labels.Count / 7.0));

        for (var i = 0; i < labels.Count; i += step)
        {
            var x = labels.Count == 1 ? left + plotWidth / 2 : left + plotWidth * i / (labels.Count - 1);
            AddLabel(canvas, Shorten(labels[i]), x, top + plotHeight + 6, centerOn: true);
        }

        foreach (var item in series)
        {
            var points = new PointCollection();

            for (var i = 0; i < item.Values.Length; i++)
            {
                var x = item.Values.Length == 1
                    ? left + plotWidth / 2
                    : left + plotWidth * i / (item.Values.Length - 1);

                var y = top + plotHeight - plotHeight * (item.Values[i] / max);
                points.Add(new Point(x, y));
            }

            canvas.Children.Add(new Polyline
            {
                Points = points,
                Stroke = item.Brush,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
            });

            foreach (var point in points)
            {
                var dot = new Ellipse { Width = 6, Height = 6, Fill = item.Brush };
                Canvas.SetLeft(dot, point.X - 3);
                Canvas.SetTop(dot, point.Y - 3);
                canvas.Children.Add(dot);
            }
        }

        // 每一列铺一块透明的"命中区"：悬停整列都算，而不是只对着那几个小圆点。
        if (tip is null || labels.Count == 0)
            return;

        var columnWidth = labels.Count == 1 ? plotWidth : plotWidth / (double)(labels.Count - 1);

        for (var i = 0; i < labels.Count; i++)
        {
            var centerX = labels.Count == 1 ? left + plotWidth / 2 : left + plotWidth * i / (labels.Count - 1);
            var columnLeft = Math.Max(left, centerX - columnWidth / 2);

            var hit = new Rectangle
            {
                Width = Math.Max(4, Math.Min(columnWidth, left + plotWidth - columnLeft)),
                Height = plotHeight,
                Fill = Brushes.Transparent,
            };

            Canvas.SetLeft(hit, columnLeft);
            Canvas.SetTop(hit, top);
            canvas.Children.Add(hit);

            // **必须拷贝到局部变量**：for 的循环变量是所有 lambda 共用的，循环结束后
            // 它停在 labels.Count —— 鼠标一移上去就是越界下标，直接崩进程。
            var column = i;
            Wire(canvas, hit, () => tip(column));
        }
    }

    /// <summary>横向柱状图：左边名字、右边条和数值。</summary>
    public static void Bars(Canvas canvas, IReadOnlyList<(string Label, double Value)> items,
                            Func<int, Tip?>? tip = null)
    {
        Begin(canvas);
        canvas.Children.Clear();

        if (items.Count == 0)
        {
            AddCentered(canvas, "还没有数据");
            return;
        }

        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;

        if (width < 120 || height < 40)
            return;

        const double labelWidth = 130;
        const double valueWidth = 70;
        const double gap = 6;

        var barWidth = width - labelWidth - valueWidth;
        var rowHeight = height / items.Count;
        var barHeight = Math.Min(22, rowHeight - 8);

        var max = NiceMax(items.Max(i => i.Value));

        for (var i = 0; i < items.Count; i++)
        {
            var (label, value) = items[i];
            var centerY = rowHeight * i + rowHeight / 2;

            AddLabel(canvas, Shorten(label, 16), 6, centerY - LabelSize * 0.8, maxWidth: labelWidth - 10);

            var length = barWidth * (value / max);

            if (length > 0.5)
            {
                var bar = new Rectangle
                {
                    Width = length,
                    Height = barHeight,
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = ColorOf(i),
                };

                Canvas.SetLeft(bar, labelWidth);
                Canvas.SetTop(bar, centerY - barHeight / 2);
                canvas.Children.Add(bar);
            }

            AddLabel(canvas, value.ToString("N0"), labelWidth + length + gap, centerY - LabelSize * 0.8);

            // 整行都能悬停 —— 值为 0 时没有条，也还是想看到那行是谁。
            if (tip is null)
                continue;

            var hit = new Rectangle { Width = width, Height = rowHeight, Fill = Brushes.Transparent };
            Canvas.SetLeft(hit, 0);
            Canvas.SetTop(hit, rowHeight * i);
            canvas.Children.Add(hit);

            var row = i;   // 同上：循环变量不能直接进闭包
            Wire(canvas, hit, () => tip(row));
        }
    }

    /// <summary>饼图：右边带图例（名字 + 百分比）。</summary>
    public static void Pie(Canvas canvas, IReadOnlyList<(string Label, double Value)> items,
                           Func<int, Tip?>? tip = null)
    {
        Begin(canvas);
        canvas.Children.Clear();

        var total = items.Sum(i => i.Value);

        if (total <= 0)
        {
            AddCentered(canvas, "还没有数据");
            return;
        }

        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;

        if (width < 120 || height < 80)
            return;

        var radius = Math.Min(height / 2 - 10, width / 4);
        var center = new Point(radius + 14, height / 2);

        var startAngle = -90.0;

        for (var i = 0; i < items.Count; i++)
        {
            var sweep = 360.0 * items[i].Value / total;

            if (sweep <= 0)
                continue;

            var slice = Slice(center, radius, startAngle, sweep, ColorOf(i));
            canvas.Children.Add(slice);

            if (tip is not null)
            {
                var index = i;
                Wire(canvas, slice, () => tip(index));
            }

            startAngle += sweep;
        }

        // 图例
        var legendX = center.X + radius + 24;
        var legendY = height / 2 - items.Count * 9;

        for (var i = 0; i < items.Count; i++)
        {
            var y = legendY + i * 18;

            var swatch = new Rectangle
            {
                Width = 10,
                Height = 10,
                RadiusX = 2,
                RadiusY = 2,
                Fill = ColorOf(i),
            };

            Canvas.SetLeft(swatch, legendX);
            Canvas.SetTop(swatch, y);
            canvas.Children.Add(swatch);

            var percent = total > 0 ? items[i].Value / total * 100 : 0;
            AddLabel(canvas, Shorten(items[i].Label, 14) + $"  {percent:0.#}%", legendX + 16, y - 2);
        }
    }

    // ------------------------------------------------------------------ 悬停

    private static void Begin(Canvas canvas)
    {
        // 重画之前先把上一次的悬浮窗收掉：元素都要被清掉了，收不到 MouseLeave。
        if (canvas.Tag is ChartTooltip previous)
        {
            previous.Close();
            canvas.Tag = null;
        }
    }

    private static void Wire(Canvas canvas, UIElement element, Func<Tip?> content)
    {
        var tooltip = canvas.Tag as ChartTooltip;

        if (tooltip is null)
        {
            tooltip = new ChartTooltip(canvas);
            canvas.Tag = tooltip;
        }

        var hover = tooltip;

        element.MouseEnter += (_, _) =>
        {
            try
            {
                if (content() is { } tip)
                    hover.Show(element, tip, Mouse.GetPosition(canvas));
            }
            catch
            {
                // 一次悬浮窗出错不该让整个程序死 —— 这张图只是没有提示。
                hover.Close();
            }
        };

        element.MouseMove += (_, _) => hover.Move(Mouse.GetPosition(canvas));
        element.MouseLeave += (_, _) => hover.Hide(element);
    }

    // ------------------------------------------------------------------ 内部

    private static Shape Slice(Point center, double radius, double startAngle, double sweep, Brush fill)
    {
        var start = PointOn(center, radius, startAngle);
        var end = PointOn(center, radius, startAngle + sweep);

        var figure = new PathFigure { StartPoint = center, IsClosed = true };
        figure.Segments.Add(new LineSegment(start, true));
        figure.Segments.Add(new ArcSegment(
            end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        return new Path
        {
            Data = geometry,
            Fill = fill,
            Stroke = Brushes.White,
            StrokeThickness = 1,
        };
    }

    private static Point PointOn(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static void DrawYAxis(Canvas canvas, double left, double top, double plotWidth, double plotHeight, double max)
    {
        const int lines = 4;

        for (var i = 0; i <= lines; i++)
        {
            var value = max * i / lines;
            var y = top + plotHeight - plotHeight * i / lines;

            canvas.Children.Add(new Line
            {
                X1 = left,
                X2 = left + plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = Axis,
                StrokeThickness = 1,
            });

            var label = AddLabel(canvas, value.ToString("N0"), 6, y - LabelSize * 0.8);
            Canvas.SetLeft(label, Math.Max(2, left - 8 - label.DesiredSize.Width));
        }
    }

    private static TextBlock AddLabel(Canvas canvas, string text, double x, double y,
                                      bool centerOn = false, double maxWidth = 0)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = LabelSize,
            Foreground = Ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        if (maxWidth > 0)
            block.MaxWidth = maxWidth;

        canvas.Children.Add(block);
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Canvas.SetLeft(block, centerOn ? x - block.DesiredSize.Width / 2 : x);
        Canvas.SetTop(block, y);

        return block;
    }

    private static void AddCentered(Canvas canvas, string text)
    {
        var block = AddLabel(canvas, text, canvas.ActualWidth / 2, canvas.ActualHeight / 2 - 8, centerOn: true);
        block.Foreground = InkFaint;
    }

    /// <summary>取一个好看的刻度上限（1 / 2 / 2.5 / 5 × 10^k）。</summary>
    private static double NiceMax(double max)
    {
        if (max <= 0)
            return 1;

        var unit = Math.Pow(10, Math.Floor(Math.Log10(max)));

        foreach (var step in new[] { 1.0, 2, 2.5, 5, 10 })
        {
            if (max <= step * unit)
                return step * unit;
        }

        return 10 * unit;
    }

    private static string Shorten(string text, int max = 10) =>
        text.Length <= max ? text : text[..max] + "…";

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// 图表上的悬停卡片：跟着鼠标走、换一个元素时淡入 + 轻微上移，而不是直接闪出来。
///
/// <para>
/// 不用 WPF 自带的 ToolTip：它有自己的弹出延迟和样式，还没法做"同一个窗口里换内容"的动画。
/// 这里用一个跟着位置走的 <see cref="Popup"/>，内容是每次重建的几行文字。
/// </para>
/// </summary>
internal sealed class ChartTooltip
{
    private readonly Popup _popup;
    private readonly Border _card;
    private readonly StackPanel _rows;
    private readonly Canvas _owner;

    private UIElement? _current;

    public ChartTooltip(Canvas owner)
    {
        _owner = owner;

        _rows = new StackPanel();

        _card = new Border
        {
            Child = _rows,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE3)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Opacity = 0.16,
            },
        };

        _popup = new Popup
        {
            Child = _card,
            PlacementTarget = owner,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            StaysOpen = true,
            IsHitTestVisible = false,
        };
    }

    /// <summary>现在是不是开着（离线检查用它验一下悬浮窗真的弹出来了）。</summary>
    public bool IsOpen => _popup.IsOpen;

    public void Show(UIElement element, Charts.Tip tip, Point position)
    {
        if (ReferenceEquals(_current, element))
        {
            Move(position);
            return;
        }

        _current = element;

        _rows.Children.Clear();

        _rows.Children.Add(new TextBlock
        {
            Text = tip.Title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            Margin = new Thickness(0, 0, 0, 4),
        });

        foreach (var line in tip.Lines)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)),
                Margin = new Thickness(0, 1, 0, 0),
            });
        }

        _popup.IsOpen = true;
        Move(position);
        AnimateIn();
    }

    public void Move(Point position)
    {
        // 靠右边/下边时翻到另一侧，别被画布边缘切掉。
        const double cardWidth = 210;
        const double cardHeight = 96;

        var x = position.X + 16;
        var y = position.Y + 16;

        if (x + cardWidth > _owner.ActualWidth)
            x = Math.Max(4, position.X - cardWidth - 12);

        if (y + cardHeight > _owner.ActualHeight)
            y = Math.Max(4, position.Y - cardHeight - 12);

        _popup.HorizontalOffset = x;
        _popup.VerticalOffset = y;
    }

    public void Hide(UIElement element)
    {
        if (!ReferenceEquals(_current, element))
            return;

        _current = null;
        _popup.IsOpen = false;
    }

    public void Close()
    {
        _current = null;
        _popup.IsOpen = false;
    }

    private void AnimateIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        _card.Opacity = 0;

        var slide = new TranslateTransform(0, 6);
        _card.RenderTransform = slide;

        _card.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });

        slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
    }
}
