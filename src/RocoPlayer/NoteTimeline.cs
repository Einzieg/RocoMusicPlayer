using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RocoPlayer.Music;
using Binding = RocoPlayer.Music.Binding;

namespace RocoPlayer;

public sealed class NoteTimeline : FrameworkElement
{
    private const double Gutter = 28;
    private readonly Typeface labelFace = new("Consolas");
    private readonly Pen gridLine, timeLine, playhead;
    private readonly Brush noteBrush;
    private DrawingGroup? drawing;
    private Size drawingSize;
    private Score? score;
    private IReadOnlyList<Binding>? mapping;
    private double position;

    public NoteTimeline()
    {
        gridLine = new Pen(new SolidColorBrush(Color.FromRgb(210, 220, 208)), 1);
        timeLine = gridLine.Clone();
        timeLine.DashStyle = new DashStyle([2, 4], 0);
        playhead = new Pen(new SolidColorBrush(Color.FromRgb(45, 85, 64)), 1.5);
        noteBrush = new SolidColorBrush(Color.FromRgb(105, 154, 120));
        gridLine.Freeze(); timeLine.Freeze(); playhead.Freeze(); noteBrush.Freeze();
    }

    public void SetScore(Score? value, IReadOnlyList<Binding>? keys)
    {
        if (ReferenceEquals(score, value) && ReferenceEquals(mapping, keys)) return;
        score = value;
        mapping = keys;
        drawing = null;
        InvalidateVisual();
    }

    public void SetPosition(double seconds)
    {
        if (position == seconds) return;
        position = seconds;
        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        drawing = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        if (score is null || RenderSize.Width <= Gutter || RenderSize.Height < 1) return;
        if (drawing == null || drawingSize != RenderSize)
        {
            drawingSize = RenderSize;
            drawing = DrawScore(score, RenderSize.Width, RenderSize.Height);
        }
        context.DrawDrawing(drawing);
        if (position > 0)
        {
            double x = Gutter + (RenderSize.Width - Gutter) * Math.Min(1, position / score.Duration);
            context.DrawLine(playhead, new(x, 0), new(x, RenderSize.Height));
        }
    }

    private DrawingGroup DrawScore(Score value, double width, double height)
    {
        var labels = mapping?.ToDictionary(b => b.Pitch, b => b.Key);
        var pitches = (labels?.Keys.AsEnumerable() ?? value.Notes.Select(n => n.Pitch)).Distinct().OrderDescending().ToArray();
        var rows = pitches.Select((p, i) => (p, i)).ToDictionary(row => row.p, row => row.i);
        double lane = height / pitches.Length, span = width - Gutter;
        var result = new DrawingGroup();
        using (var canvas = result.Open())
        {
            int labelStep = labels == null ? Math.Max(1, (int)Math.Ceiling(14 / lane)) : 1;
            for (int i = 0; i < pitches.Length; i += labelStep)
            {
                double y = i * lane;
                canvas.DrawLine(gridLine, new(Gutter, y + .5), new(width, y + .5));
                string caption = labels?.GetValueOrDefault(pitches[i]) ?? ScoreIO.PitchName(pitches[i]);
                var text = new FormattedText(caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    labelFace, 10, playhead.Brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                canvas.DrawText(text, new(0, Math.Clamp(y + (lane - text.Height) / 2, 0, Math.Max(0, height - text.Height))));
            }
            for (int i = 0; i <= 8; i++)
            {
                double x = Gutter + span * i / 8;
                canvas.DrawLine(timeLine, new(x, 0), new(x, height));
            }
            var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var ink = geometry.Open())
            {
                double bar = Math.Min(13, Math.Max(1, lane * .55));
                foreach (var note in value.Notes)
                {
                    if (!rows.TryGetValue(note.Pitch, out int row)) continue;
                    double x = Gutter + span * note.Start / value.Duration;
                    if (x >= width) continue;
                    double durationWidth = span * note.Duration / value.Duration;
                    double end = Math.Min(width, x + Math.Max(1.5, durationWidth - .65));
                    double y = (row + .5) * lane - bar / 2;
                    double r = Math.Min(1.7, Math.Min(end - x, bar) / 2);
                    ink.BeginFigure(new(x + r, y), true, true);
                    ink.LineTo(new(end - r, y), false, false);
                    ink.QuadraticBezierTo(new(end, y), new(end, y + r), false, false);
                    ink.LineTo(new(end, y + bar - r), false, false);
                    ink.QuadraticBezierTo(new(end, y + bar), new(end - r, y + bar), false, false);
                    ink.LineTo(new(x + r, y + bar), false, false);
                    ink.QuadraticBezierTo(new(x, y + bar), new(x, y + bar - r), false, false);
                    ink.LineTo(new(x, y + r), false, false);
                    ink.QuadraticBezierTo(new(x, y), new(x + r, y), false, false);
                }
            }
            geometry.Freeze();
            canvas.DrawGeometry(noteBrush, null, geometry);
        }
        result.Freeze();
        return result;
    }
}
