using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

/// <summary>
/// Procedural 3D-head-tracked outfits and seasonal wardrobe ported from Coucou.
/// </summary>
internal static class MochiOutfits
{
    public static MochiOutfit ResolveOutfit(MochiOutfit selection, DateTime now)
        => selection == MochiOutfit.Auto ? SeasonalOutfit(now) : selection;

    /// <summary>
    /// Seasonal schedule matching Coucou's wardrobe.ts:
    /// New Year's (Dec 31 - Jan 3) -> PartyHat
    /// Easter (3 days before to 1 day after Easter Sunday) -> Bow
    /// Halloween (Oct 24 - Nov 1) -> WitchHat (Oct 31 -> Pumpkin)
    /// Christmas (Dec 15 - Dec 26) -> SantaHat
    /// Otherwise -> None.
    /// </summary>
    public static MochiOutfit SeasonalOutfit(DateTime date)
    {
        int m = date.Month, d = date.Day;
        if ((m == 12 && d >= 31) || (m == 1 && d <= 3))
            return MochiOutfit.PartyHat;
        if (m == 12 && d >= 15 && d <= 26)
            return MochiOutfit.SantaHat;
        if (m == 10 && d == 31)
            return MochiOutfit.Pumpkin;
        if ((m == 10 && d >= 24) || (m == 11 && d == 1))
            return MochiOutfit.WitchHat;

        var easter = EasterSunday(date.Year);
        double daysFromEaster = (date.Date - easter).TotalDays;
        if (daysFromEaster >= -3 && daysFromEaster <= 1)
            return MochiOutfit.Bow;

        return MochiOutfit.None;
    }

    private static DateTime EasterSunday(int year)
    {
        int a = year % 19;
        int b = year / 100;
        int c = year % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31;
        int day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateTime(year, month, day);
    }

    public static bool IsPumpkin(MochiOutfit resolved) => resolved == MochiOutfit.Pumpkin;

    public static void DrawBehind(DrawingContext dc, MochiOutfit outfit, double r, double yaw, double pitch, double alpha)
    {
        if (outfit == MochiOutfit.None || alpha <= 0.01) return;
        if (outfit == MochiOutfit.Pumpkin)
        {
            // Pumpkin stem behind the top of the head
            double sx = yaw * r * 0.25;
            double sy = -r * 0.88 + pitch * r * 0.14;
            var stem = new StreamGeometry();
            using (var ctx = stem.Open())
            {
                ctx.BeginFigure(new Point(sx - r * 0.10, sy + r * 0.12), true, true);
                ctx.QuadraticBezierTo(new Point(sx - r * 0.08, sy - r * 0.22), new Point(sx + r * 0.08, sy - r * 0.28), true, false);
                ctx.LineTo(new Point(sx + r * 0.16, sy - r * 0.20), true, false);
                ctx.QuadraticBezierTo(new Point(sx + r * 0.06, sy - r * 0.10), new Point(sx + r * 0.10, sy + r * 0.12), true, false);
            }
            stem.Freeze();
            dc.PushOpacity(alpha);
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(82, 130, 52)), null, stem);
            dc.Pop();
        }
    }

    public static void DrawFront(DrawingContext dc, MochiOutfit outfit, double r, double yaw, double pitch, double alpha)
    {
        if (outfit == MochiOutfit.None || alpha <= 0.01) return;

        dc.PushOpacity(alpha);
        switch (outfit)
        {
            case MochiOutfit.SantaHat:
                DrawSantaHat(dc, r, yaw, pitch);
                break;
            case MochiOutfit.PartyHat:
                DrawPartyHat(dc, r, yaw, pitch);
                break;
            case MochiOutfit.WitchHat:
                DrawWitchHat(dc, r, yaw, pitch);
                break;
            case MochiOutfit.Sunglasses:
                DrawSunglasses(dc, r, yaw, pitch);
                break;
            case MochiOutfit.RoundGlasses:
                DrawRoundGlasses(dc, r, yaw, pitch);
                break;
            case MochiOutfit.Bow:
                DrawBow(dc, r, yaw, pitch);
                break;
            case MochiOutfit.Scarf:
                DrawScarf(dc, r, yaw, pitch);
                break;
            case MochiOutfit.Pumpkin:
                DrawPumpkinRibs(dc, r, yaw, pitch);
                break;
        }
        dc.Pop();
    }

    private static void DrawSantaHat(DrawingContext dc, double r, double yaw, double pitch)
    {
        double hx = yaw * r * 0.26;
        double hy = -r * 0.72 + pitch * r * 0.14;
        var cone = new StreamGeometry();
        using (var ctx = cone.Open())
        {
            ctx.BeginFigure(new Point(hx - r * 0.62, hy + r * 0.12), true, true);
            ctx.QuadraticBezierTo(new Point(hx - r * 0.10, hy - r * 0.75), new Point(hx + r * 0.56, hy - r * 0.42), true, false);
            ctx.QuadraticBezierTo(new Point(hx + r * 0.50, hy - r * 0.08), new Point(hx + r * 0.62, hy + r * 0.12), true, false);
        }
        cone.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(220, 38, 38)), null, cone);

        // White fluffy trim
        var trimBrush = new SolidColorBrush(Color.FromRgb(248, 250, 252));
        dc.DrawRoundedRectangle(trimBrush, null, new Rect(hx - r * 0.66, hy + r * 0.02, r * 1.32, r * 0.24), r * 0.12, r * 0.12);
        // Pom-pom
        dc.DrawEllipse(trimBrush, null, new Point(hx + r * 0.58, hy - r * 0.40), r * 0.16, r * 0.16);
    }

    private static void DrawPartyHat(DrawingContext dc, double r, double yaw, double pitch)
    {
        double hx = yaw * r * 0.28;
        double hy = -r * 0.76 + pitch * r * 0.14;
        var cone = new StreamGeometry();
        using (var ctx = cone.Open())
        {
            ctx.BeginFigure(new Point(hx - r * 0.44, hy + r * 0.08), true, true);
            ctx.LineTo(new Point(hx + r * 0.04, hy - r * 0.85), true, false);
            ctx.LineTo(new Point(hx + r * 0.44, hy + r * 0.08), true, false);
        }
        cone.Freeze();
        var grad = new LinearGradientBrush(Color.FromRgb(99, 102, 241), Color.FromRgb(236, 72, 153), 45);
        dc.DrawGeometry(grad, null, cone);

        // Festive polka dots
        var gold = new SolidColorBrush(Color.FromRgb(250, 204, 21));
        var mint = new SolidColorBrush(Color.FromRgb(52, 211, 153));
        dc.DrawEllipse(gold, null, new Point(hx - r * 0.08, hy - r * 0.22), r * 0.07, r * 0.07);
        dc.DrawEllipse(mint, null, new Point(hx + r * 0.14, hy - r * 0.12), r * 0.06, r * 0.06);
        dc.DrawEllipse(gold, null, new Point(hx + r * 0.04, hy - r * 0.85), r * 0.11, r * 0.11);
    }

    private static void DrawWitchHat(DrawingContext dc, double r, double yaw, double pitch)
    {
        double hx = yaw * r * 0.26;
        double hy = -r * 0.68 + pitch * r * 0.14;
        var hatBrush = new SolidColorBrush(Color.FromRgb(46, 36, 68));
        var bandBrush = new SolidColorBrush(Color.FromRgb(168, 85, 247));
        var buckleBrush = new SolidColorBrush(Color.FromRgb(250, 204, 21));

        // Brim
        dc.DrawEllipse(hatBrush, null, new Point(hx, hy + r * 0.08), r * 0.96, r * 0.16);

        // Pointed crown
        var crown = new StreamGeometry();
        using (var ctx = crown.Open())
        {
            ctx.BeginFigure(new Point(hx - r * 0.48, hy + r * 0.06), true, true);
            ctx.QuadraticBezierTo(new Point(hx - r * 0.16, hy - r * 0.56), new Point(hx + r * 0.22, hy - r * 0.90), true, false);
            ctx.QuadraticBezierTo(new Point(hx + r * 0.34, hy - r * 0.35), new Point(hx + r * 0.48, hy + r * 0.06), true, false);
        }
        crown.Freeze();
        dc.DrawGeometry(hatBrush, null, crown);

        // Purple ribbon + gold buckle
        dc.DrawRoundedRectangle(bandBrush, null, new Rect(hx - r * 0.44, hy - r * 0.06, r * 0.88, r * 0.11), r * 0.04, r * 0.04);
        dc.DrawRoundedRectangle(null, new Pen(buckleBrush, Math.Max(1.0, r * 0.04)), new Rect(hx - r * 0.10, hy - r * 0.08, r * 0.20, r * 0.15), r * 0.03, r * 0.03);
    }

    private static void DrawSunglasses(DrawingContext dc, double r, double yaw, double pitch)
    {
        double ex = yaw * r * 0.42;
        double ey = pitch * r * 0.32 - r * 0.02;
        double spread = r * 0.36;
        double lensW = r * 0.46;
        double lensH = r * 0.30;

        var frame = new SolidColorBrush(Color.FromRgb(17, 24, 39));
        var bridgePen = new Pen(frame, Math.Max(1.4, r * 0.07));
        dc.DrawLine(bridgePen, new Point(ex - spread + lensW * 0.4, ey - lensH * 0.2), new Point(ex + spread - lensW * 0.4, ey - lensH * 0.2));

        var lensL = new Rect(ex - spread - lensW / 2, ey - lensH / 2, lensW, lensH);
        var lensR = new Rect(ex + spread - lensW / 2, ey - lensH / 2, lensW, lensH);
        dc.DrawRoundedRectangle(frame, null, lensL, r * 0.08, r * 0.08);
        dc.DrawRoundedRectangle(frame, null, lensR, r * 0.08, r * 0.08);

        // Subtle diagonal sheen
        var sheen = new Pen(new SolidColorBrush(Color.FromArgb(95, 255, 255, 255)), Math.Max(1.0, r * 0.045));
        dc.DrawLine(sheen, new Point(lensL.Left + lensW * 0.22, lensL.Top + lensH * 0.18), new Point(lensL.Left + lensW * 0.48, lensL.Bottom - lensH * 0.22));
        dc.DrawLine(sheen, new Point(lensR.Left + lensW * 0.22, lensR.Top + lensH * 0.18), new Point(lensR.Left + lensW * 0.48, lensR.Bottom - lensH * 0.22));
    }

    private static void DrawRoundGlasses(DrawingContext dc, double r, double yaw, double pitch)
    {
        double ex = yaw * r * 0.42;
        double ey = pitch * r * 0.32 - r * 0.02;
        double spread = r * 0.36;
        double rad = r * 0.24;

        var rimPen = new Pen(new SolidColorBrush(Color.FromRgb(212, 175, 55)), Math.Max(1.2, r * 0.055));
        var glass = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
        dc.DrawEllipse(glass, rimPen, new Point(ex - spread, ey), rad, rad);
        dc.DrawEllipse(glass, rimPen, new Point(ex + spread, ey), rad, rad);
        dc.DrawLine(rimPen, new Point(ex - spread + rad, ey), new Point(ex + spread - rad, ey));
    }

    private static void DrawBow(DrawingContext dc, double r, double yaw, double pitch)
    {
        double bx = yaw * r * 0.32 + r * 0.48;
        double by = -r * 0.66 + pitch * r * 0.16;
        var pink = new SolidColorBrush(Color.FromRgb(244, 114, 182));
        var center = new SolidColorBrush(Color.FromRgb(219, 39, 119));

        var leftWing = new StreamGeometry();
        using (var ctx = leftWing.Open())
        {
            ctx.BeginFigure(new Point(bx, by), true, true);
            ctx.LineTo(new Point(bx - r * 0.26, by - r * 0.16), true, false);
            ctx.LineTo(new Point(bx - r * 0.22, by + r * 0.16), true, false);
        }
        leftWing.Freeze();

        var rightWing = new StreamGeometry();
        using (var ctx = rightWing.Open())
        {
            ctx.BeginFigure(new Point(bx, by), true, true);
            ctx.LineTo(new Point(bx + r * 0.26, by - r * 0.14), true, false);
            ctx.LineTo(new Point(bx + r * 0.22, by + r * 0.18), true, false);
        }
        rightWing.Freeze();

        dc.DrawGeometry(pink, null, leftWing);
        dc.DrawGeometry(pink, null, rightWing);
        dc.DrawEllipse(center, null, new Point(bx, by), r * 0.08, r * 0.08);
    }

    private static void DrawScarf(DrawingContext dc, double r, double yaw, double pitch)
    {
        double sx = yaw * r * 0.25;
        double sy = r * 0.62 + pitch * r * 0.10;
        var red = new SolidColorBrush(Color.FromRgb(220, 38, 38));
        var stripe = new SolidColorBrush(Color.FromRgb(254, 240, 138));

        dc.DrawRoundedRectangle(red, null, new Rect(sx - r * 0.72, sy - r * 0.12, r * 1.44, r * 0.24), r * 0.10, r * 0.10);
        // Hanging tail
        dc.DrawRoundedRectangle(red, null, new Rect(sx + r * 0.18, sy + r * 0.04, r * 0.26, r * 0.42), r * 0.07, r * 0.07);
        dc.DrawRectangle(stripe, null, new Rect(sx + r * 0.18, sy + r * 0.34, r * 0.26, r * 0.06));
    }

    private static void DrawPumpkinRibs(DrawingContext dc, double r, double yaw, double pitch)
    {
        var ribPen = new Pen(new SolidColorBrush(Color.FromArgb(64, 154, 52, 18)), Math.Max(1.0, r * 0.045));
        double shift = yaw * r * 0.22;
        dc.DrawEllipse(null, ribPen, new Point(shift, pitch * r * 0.05), r * 0.48, r * 0.90);
        dc.DrawEllipse(null, ribPen, new Point(shift * 0.6, pitch * r * 0.05), r * 0.80, r * 0.90);
    }
}
