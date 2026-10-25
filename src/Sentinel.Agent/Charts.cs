using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
/// </summary>
internal static class Charts
{
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

    /// <summary>折线图：横轴是日期，每个系列一条线。</summary>
    public static void Line(Canvas canvas, IReadOnlyList<string> labels, IReadOnlyList<Series> series)
    {
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
                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = item.Brush,
                    ToolTip = item.Name,
                };

                Canvas.SetLeft(dot, point.X - 3);
                Canvas.SetTop(dot, point.Y - 3);
                canvas.Children.Add(dot);
            }
        }
    }

    /// <summary>横向柱状图：左边名字、右边条和数值。名字很长也不挤。</summary>
    public static void Bars(Canvas canvas, IReadOnlyList<(string Label, double Value)> items)
    {
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
        }
    }

    /// <summary>饼图：右边带图例（名字 + 百分比）。总量为 0 时画一个灰环。</summary>
    public static void Pie(Canvas canvas, IReadOnlyList<(string Label, double Value)> items)
    {
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

            canvas.Children.Add(Slice(center, radius, startAngle, sweep, ColorOf(i), items[i].Label));
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

    // ------------------------------------------------------------------ 内部

    private static Shape Slice(Point center, double radius, double startAngle, double sweep, Brush fill, string tip)
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
            ToolTip = tip,
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

            var line = new Line
            {
                X1 = left,
                X2 = left + plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = Axis,
                StrokeThickness = 1,
            };

            canvas.Children.Add(line);

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
