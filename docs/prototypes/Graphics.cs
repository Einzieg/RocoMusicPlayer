using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RocoPlayer.Prototypes;

internal sealed class NoteRoll(Piece piece, bool original, Action<double> seek) : FrameworkElement
{
    internal double Position { get; set; }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        double span = 16 * 60d / piece.Bpm;
        double page = PageStart(span);
        seek(Math.Clamp(page + (e.GetPosition(this).X - 33) / Math.Max(1, ActualWidth - 36) * span, 0, piece.Duration));
    }
    private double PageStart(double span) => Math.Floor(Math.Min(Position, piece.Duration - .0001) / span) * span;

    protected override void OnRender(DrawingContext d)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;
        double span = 16 * 60d / piece.Bpm, start = PageStart(span);
        var ink = Ui.Brush(original ? "#E7B482" : "#A2DEC5");
        var grid = new Pen(Ui.Brush("#303944"), 1);
        double plotWidth = w - 36, plotHeight = h - 25;
        int[] rows = original ? piece.Hits.Select(n => n.Original).Distinct().OrderDescending().ToArray() : Piece.Pitches.Reverse().ToArray();
        double dy = plotHeight / rows.Length;
        d.DrawRectangle(Ui.Brush("#171D24"), null, new(0, 0, w, h));
        void Label(string text, double x, double y)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), 11, Ui.Brush("#A2B0BC"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            d.DrawText(ft, new Point(x, y));
        }
        for (int i = 0; i < 16; i++)
        {
            var x = 33 + plotWidth * i / 16;
            if (i % 4 == 0) Label(Piece.Clock(start + span * i / 16), x + 3, 2);
            d.DrawLine(i % 4 == 0 ? new Pen(Ui.Brush("#4B5966"), 1) : grid, new(x, 20), new(x, h));
        }
        for (int i = 0; i < rows.Length; i++)
        {
            var y = 21 + i * dy;
            string[] names = ["C", "C♯", "D", "E♭", "E", "F", "F♯", "G", "A♭", "A", "B♭", "B"];
            Label(names[rows[i] % 12] + (rows[i] / 12 - 1), 1, y + Math.Max(0, (dy - 12) * .5));
            d.DrawLine(grid, new(32, y), new(w, y));
        }
        foreach (var note in piece.Hits.Where(n => n.Start < start + span && n.Start + n.Seconds > start))
        {
            var lane = Array.IndexOf(rows, original ? note.Original : Piece.Pitches[note.Lane]);
            double left = Math.Max(0, note.Start - start), right = Math.Min(span, note.Start + note.Seconds - start);
            d.DrawRoundedRectangle(ink, null, new Rect(33 + plotWidth * left / span, 23 + lane * dy, Math.Max(2, (right - left) * plotWidth / span - 1), Math.Max(3, dy - 5)), 2, 2);
        }
        double playX = 33 + plotWidth * (Position - start) / span;
        d.DrawLine(new Pen(Ui.Brush("#F3AC73"), 1.5), new(playX, 17), new(playX, h));
        d.DrawEllipse(Ui.Brush("#F3AC73"), null, new(playX, 17), 3.1, 3.1);
    }
}

internal sealed class RecordSleeve(Piece piece) : FrameworkElement
{
    protected override void OnRender(DrawingContext d)
    {
        double s = Math.Min(ActualWidth / 1.48, ActualHeight * .90);
        if (s <= 2) return;
        double x = (ActualWidth - 1.42 * s) * .5, y = (ActualHeight - s) * .5;
        var center = new Point(x + s * .91, y + s * .492);
        d.DrawEllipse(Ui.Brush("#25272A"), null, center, s * .488, s * .488);
        for (int i = 0; i < 61; i++)
        {
            double radius = s * (.174 + i * .00505);
            d.DrawEllipse(null, new Pen(Ui.Brush(i % 5 == 0 ? "#555459" : "#36383D"), .7), center, radius, radius);
        }
        d.DrawEllipse(Ui.Brush("#C28B7D"), null, center, s * .16, s * .16);
        d.DrawEllipse(Ui.Brush("#1C2223"), null, center, 4, 4);
        d.PushTransform(new RotateTransform(-4, x + s * .5, y + s * .5));
        d.DrawRectangle(Ui.Brush("#D8CDC0"), null, new(x + 3, y + 5, s, s));
        d.DrawImage(CoverArt.Get(piece.Artwork), new(x, y, s, s));
        d.Pop();
    }
}
