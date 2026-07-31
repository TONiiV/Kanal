using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kanal.Host.Controls;

/// <summary>
/// A circular flag glyph for a language code, drawn as flat vector bands — no bitmaps,
/// no emoji (Windows renders flag emoji as letter pairs). Unknown codes fall back to the
/// two-letter code on a paper disc, so the tag is always readable without the flag.
/// </summary>
public sealed class FlagIcon : Control
{
    public static readonly StyledProperty<string?> CodeProperty =
        AvaloniaProperty.Register<FlagIcon, string?>(nameof(Code));

    static FlagIcon() => AffectsRender<FlagIcon>(CodeProperty);

    public string? Code
    {
        get => GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public override void Render(DrawingContext ctx)
    {
        var d = Math.Min(Bounds.Width, Bounds.Height);
        if (d <= 0)
            return;

        var rect = new Rect(0, 0, d, d);
        var clip = new EllipseGeometry(rect);
        using (ctx.PushGeometryClip(clip))
        {
            DrawFlag(ctx, (Code ?? "").ToLowerInvariant(), d);
        }

        // hairline ring keeps light flags (pl, ja) from dissolving into the sheet
        var ring = new Pen(new SolidColorBrush(Color.Parse("#D5DCE1")), 1);
        ctx.DrawEllipse(null, ring, rect.Center, d / 2 - 0.5, d / 2 - 0.5);
    }

    private void DrawFlag(DrawingContext ctx, string code, double d)
    {
        switch (code)
        {
            case "zh":
                Fill(ctx, "#DE2910", 0, 0, d, d);
                Star(ctx, "#FFDE00", d * 0.34, d * 0.36, d * 0.17);
                Star(ctx, "#FFDE00", d * 0.62, d * 0.16, d * 0.055);
                Star(ctx, "#FFDE00", d * 0.72, d * 0.30, d * 0.055);
                Star(ctx, "#FFDE00", d * 0.72, d * 0.48, d * 0.055);
                Star(ctx, "#FFDE00", d * 0.62, d * 0.62, d * 0.055);
                break;
            case "de":
                Fill(ctx, "#1A1A1A", 0, 0, d, d / 3);
                Fill(ctx, "#DD0000", 0, d / 3, d, d / 3);
                Fill(ctx, "#FFCE00", 0, d * 2 / 3, d, d / 3);
                break;
            case "pl":
                Fill(ctx, "#FFFFFF", 0, 0, d, d / 2);
                Fill(ctx, "#DC143C", 0, d / 2, d, d / 2);
                break;
            case "en":
                Fill(ctx, "#012169", 0, 0, d, d);
                Diagonals(ctx, "#FFFFFF", d, d * 0.22);
                Diagonals(ctx, "#C8102E", d, d * 0.08);
                Cross(ctx, "#FFFFFF", d, d * 0.32);
                Cross(ctx, "#C8102E", d, d * 0.18);
                break;
            case "fr":
                Fill(ctx, "#002395", 0, 0, d / 3, d);
                Fill(ctx, "#FFFFFF", d / 3, 0, d / 3, d);
                Fill(ctx, "#ED2939", d * 2 / 3, 0, d / 3, d);
                break;
            case "es":
                Fill(ctx, "#AA151B", 0, 0, d, d / 4);
                Fill(ctx, "#F1BF00", 0, d / 4, d, d / 2);
                Fill(ctx, "#AA151B", 0, d * 3 / 4, d, d / 4);
                break;
            case "it":
                Fill(ctx, "#009246", 0, 0, d / 3, d);
                Fill(ctx, "#FFFFFF", d / 3, 0, d / 3, d);
                Fill(ctx, "#CE2B37", d * 2 / 3, 0, d / 3, d);
                break;
            case "cs":
                Fill(ctx, "#FFFFFF", 0, 0, d, d / 2);
                Fill(ctx, "#D7141A", 0, d / 2, d, d / 2);
                var triangle = new StreamGeometry();
                using (var g = triangle.Open())
                {
                    g.BeginFigure(new Point(0, 0), true);
                    g.LineTo(new Point(d * 0.55, d / 2));
                    g.LineTo(new Point(0, d));
                    g.EndFigure(true);
                }

                ctx.DrawGeometry(Brush("#11457E"), null, triangle);
                break;
            case "uk":
                Fill(ctx, "#005BBB", 0, 0, d, d / 2);
                Fill(ctx, "#FFD500", 0, d / 2, d, d / 2);
                break;
            case "ru":
                Fill(ctx, "#FFFFFF", 0, 0, d, d / 3);
                Fill(ctx, "#0039A6", 0, d / 3, d, d / 3);
                Fill(ctx, "#D52B1E", 0, d * 2 / 3, d, d / 3);
                break;
            case "ja":
                Fill(ctx, "#FFFFFF", 0, 0, d, d);
                ctx.DrawEllipse(Brush("#BC002D"), null, new Point(d / 2, d / 2), d * 0.27, d * 0.27);
                break;
            case "ko":
                Fill(ctx, "#FFFFFF", 0, 0, d, d);
                var center = new Point(d / 2, d / 2);
                var r = d * 0.27;
                ctx.DrawEllipse(Brush("#0047A0"), null, center, r, r);
                var upper = new StreamGeometry();
                using (var g = upper.Open())
                {
                    g.BeginFigure(new Point(d / 2 - r, d / 2), true);
                    g.ArcTo(new Point(d / 2 + r, d / 2), new Size(r, r), 0, false, SweepDirection.Clockwise);
                    g.EndFigure(true);
                }

                ctx.DrawGeometry(Brush("#CD2E3A"), null, upper);
                ctx.DrawEllipse(Brush("#CD2E3A"), null, new Point(d / 2 - r / 2, d / 2), r / 2, r / 2);
                ctx.DrawEllipse(Brush("#0047A0"), null, new Point(d / 2 + r / 2, d / 2), r / 2, r / 2);
                break;
            default:
                Fill(ctx, "#F1F3F5", 0, 0, d, d);
                var label = new FormattedText(
                    code.ToUpperInvariant(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, weight: FontWeight.Bold), d * 0.34,
                    Brush("#4A5862"));
                ctx.DrawText(label, new Point((d - label.Width) / 2, (d - label.Height) / 2));
                break;
        }
    }

    private static void Fill(DrawingContext ctx, string hex, double x, double y, double w, double h) =>
        ctx.FillRectangle(Brush(hex), new Rect(x, y, w, h));

    private static void Diagonals(DrawingContext ctx, string hex, double d, double thickness)
    {
        var pen = new Pen(Brush(hex), thickness);
        ctx.DrawLine(pen, new Point(0, 0), new Point(d, d));
        ctx.DrawLine(pen, new Point(d, 0), new Point(0, d));
    }

    private static void Cross(DrawingContext ctx, string hex, double d, double thickness)
    {
        var pen = new Pen(Brush(hex), thickness);
        ctx.DrawLine(pen, new Point(d / 2, 0), new Point(d / 2, d));
        ctx.DrawLine(pen, new Point(0, d / 2), new Point(d, d / 2));
    }

    private static void Star(DrawingContext ctx, string hex, double cx, double cy, double r)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var i = 0; i < 10; i++)
            {
                var radius = i % 2 == 0 ? r : r * 0.4;
                var angle = -Math.PI / 2 + i * Math.PI / 5;
                var p = new Point(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
                if (i == 0)
                    g.BeginFigure(p, true);
                else
                    g.LineTo(p);
            }

            g.EndFigure(true);
        }

        ctx.DrawGeometry(Brush(hex), null, geometry);
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
