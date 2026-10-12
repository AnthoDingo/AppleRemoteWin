// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AppleRemote.App.Controls;

/// <summary>Apple "beachball" busy indicator: six swirled color wedges spinning while the control is visible.</summary>
public sealed class Beachball : FrameworkElement
{
    // Clockwise from the upper left: yellow, orange, red, purple, blue, green.
    private static readonly Pen[] Wedges = new[] { "#FFD000", "#FFA000", "#FF4B45", "#B65CD5", "#0A99F2", "#44C21C" }
        .Select(c => { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)); var p = new Pen(b, 0.5); p.Freeze(); return p; })
        .ToArray();

    private readonly RotateTransform _spin = new();

    public Beachball()
    {
        RenderTransform = _spin;
        RenderTransformOrigin = new Point(0.5, 0.5);
        // IsVisible also turns false when unloaded: no animation runs off screen.
        IsVisibleChanged += (_, _) => _spin.BeginAnimation(RotateTransform.AngleProperty, IsVisible
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    protected override Size MeasureOverride(Size availableSize) => new(32, 32);

    protected override void OnRender(DrawingContext dc)
    {
        var r = Math.Min(ActualWidth, ActualHeight) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        Point At(double deg, double radius) => new(c.X + radius * Math.Cos(deg * Math.PI / 180), c.Y + radius * Math.Sin(deg * Math.PI / 180));

        for (var i = 0; i < Wedges.Length; i++)
        {
            double a0 = -150 + 60 * i, a1 = a0 + 60;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                // Each edge bows from the center toward the rim, ahead of the rotation: the swirl.
                ctx.BeginFigure(c, true, true);
                ctx.QuadraticBezierTo(At(a0 + 50, r * 0.6), At(a0, r), true, false);
                ctx.ArcTo(At(a1, r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                ctx.QuadraticBezierTo(At(a1 + 50, r * 0.6), c, true, false);
            }
            g.Freeze();
            // The thin same-color stroke hides the antialiasing seams between wedges.
            dc.DrawGeometry(Wedges[i].Brush, Wedges[i], g);
        }
    }
}
