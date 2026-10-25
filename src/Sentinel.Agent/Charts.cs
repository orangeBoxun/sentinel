using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
/// 悬停提示是自绘的（<see cref="ChartTooltip"/>），**锚在数据元素上**而不是跟着鼠标走：
/// Popup 是独立窗口，每移动一像素就挪一次会明显抖动，也会离鼠标越来越远。
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
    private static readonly Brush Guide = Freeze("#C9C9C9");
    private static readonly Brush RowHighlight = Freeze("#F2F4F7");

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

        // 每一列的点单独记着，悬停时把那一列的点放大。
        var columnDots = new List<Ellipse>[Math.Max(1, labels.Count)];

        for (var i = 0; i < columnDots.Length; i++)
            columnDots[i] = new List<Ellipse>();

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

            for (var i = 0; i < points.Count && i < columnDots.Length; i++)
            {
                var dot = new Ellipse { Width = 6, Height = 6, Fill = item.Brush };
                Canvas.SetLeft(dot, points[i].X - 3);
                Canvas.SetTop(dot, points[i].Y - 3);
                canvas.Children.Add(dot);
                columnDots[i].Add(dot);
            }
        }

        if (tip is null || labels.Count == 0)
            return;

        // 竖向参考线：悬停时出现，标明看的是哪一列。
        var guide = new Line
        {
            Y1 = top,
            Y2 = top + plotHeight,
            Stroke = Guide,
            StrokeThickness = 1,
            Opacity = 0,
        };

        canvas.Children.Insert(0, guide);

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
            var anchorX = centerX;

            Wire(canvas, hit, () => tip(column), new Point(anchorX, top + 4),
                enter: () =>
                {
                    guide.X1 = anchorX;
                    guide.X2 = anchorX;
                    Fade(guide, 1);

                    foreach (var dot in columnDots[column])
                        Grow(dot, 1.7);
                },
                leave: () =>
                {
                    Fade(guide, 0);

                    foreach (var dot in columnDots[column])
                        Grow(dot, 1);
                });
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

            var rowBrush = new Rectangle
            {
                Width = width,
                Height = Math.Max(6, rowHeight - 2),
                RadiusX = 6,
                RadiusY = 6,
                Fill = RowHighlight,
                Opacity = 0,
            };

            Canvas.SetLeft(rowBrush, 0);
            Canvas.SetTop(rowBrush, rowHeight * i + 1);
            canvas.Children.Add(rowBrush);

            AddLabel(canvas, Shorten(label, 16), 6, centerY - LabelSize * 0.8, maxWidth: labelWidth - 10);

            var length = barWidth * (value / max);
            Rectangle? bar = null;

            if (length > 0.5)
            {
                bar = new Rectangle
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

            var row = i;
            var rowAnchor = new Point(labelWidth, rowHeight * i + 2);
            var highlightedBar = bar;

            var hit = new Rectangle { Width = width, Height = rowHeight, Fill = Brushes.Transparent };
            Canvas.SetLeft(hit, 0);
            Canvas.SetTop(hit, rowHeight * i);
            canvas.Children.Add(hit);

            Wire(canvas, hit, () => tip(row), rowAnchor,
                enter: () =>
                {
                    Fade(rowBrush, 1);

                    if (highlightedBar is not null)
                        Grow(highlightedBar, 1.08);
                },
                leave: () =>
                {
                    Fade(rowBrush, 0);

                    if (highlightedBar is not null)
                        Grow(highlightedBar, 1);
                });
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
                var mid = PointOn(center, radius * 0.75, startAngle + sweep / 2);

                // 悬停时扇区稍微鼓出来一点（以饼心为原点缩放）。
                var transform = new ScaleTransform(1, 1, center.X, center.Y);
                slice.RenderTransform = transform;

                Wire(canvas, slice, () => tip(index), mid,
                    enter: () => Scale(transform, 1.04),
                    leave: () => Scale(transform, 1));
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

    private static ChartTooltip EnsureTip(Canvas canvas)
    {
        if (canvas.Tag is ChartTooltip existing)
            return existing;

        var created = new ChartTooltip(canvas);
        canvas.Tag = created;
        return created;
    }

    private static void Wire(Canvas canvas, UIElement hit, Func<Tip?> content, Point anchor,
                             Action? enter = null, Action? leave = null)
    {
        var tooltip = EnsureTip(canvas);

        hit.MouseEnter += (_, _) =>
        {
            try
            {
                enter?.Invoke();

                if (content() is { } tip)
                    tooltip.Show(hit, tip, anchor);
            }
            catch
            {
                // 一次悬浮窗出错不该让整个程序死 —— 这张图只是没有提示。
                tooltip.Close();
            }
        };

        hit.MouseLeave += (_, _) =>
        {
            try
            {
                leave?.Invoke();
            }
            catch
            {
                // 收尾失败无所谓。
            }

            tooltip.Hide(hit);
        };
    }

    private static void Fade(UIElement element, double to) =>
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

    /// <summary>以自身中心为原点缩放（放大一点表示"被指到了"）。</summary>
    private static void Grow(FrameworkElement element, double scale)
    {
        if (element.RenderTransform is not ScaleTransform transform)
        {
            transform = new ScaleTransform(1, 1, element.Width / 2, element.Height / 2);
            element.RenderTransform = transform;
        }

        Scale(transform, scale);
    }

    private static void Scale(ScaleTransform transform, double to)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        transform.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(100)) { EasingFunction = ease });

        transform.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(100)) { EasingFunction = ease });
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
/// 图表上的悬停卡片：**锚在数据元素上**（不是跟着鼠标），换一个元素时淡入 + 轻微上移。
///
/// <para>
/// 之前每移动一像素就 SetOffset 一次：Popup 是独立窗口，这样挪会明显抖动，而且看起来
/// 总离鼠标很远。图表上的提示本来就该贴着数据，不该追着指针跑。
/// </para>
/// </summary>
internal sealed class ChartTooltip
{
    private const double CardWidth = 210;
    private const double CardHeight = 96;

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

    /// <summary><paramref name="anchor"/> 是画布坐标系里"这块数据"的位置。</summary>
    public void Show(UIElement element, Charts.Tip tip, Point anchor)
    {
        if (ReferenceEquals(_current, element))
            return;

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

        Place(anchor);

        _popup.IsOpen = true;
        AnimateIn();
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

    /// <summary>贴着数据放：默认在锚点右下，靠边时翻到另一侧。</summary>
    private void Place(Point anchor)
    {
        var x = anchor.X + 14;
        var y = anchor.Y + 10;

        if (x + CardWidth > _owner.ActualWidth)
            x = Math.Max(4, anchor.X - CardWidth - 14);

        if (y + CardHeight > _owner.ActualHeight)
            y = Math.Max(4, anchor.Y - CardHeight - 10);

        _popup.HorizontalOffset = x;
        _popup.VerticalOffset = y;
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
