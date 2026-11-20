using System.Globalization;

namespace BluettiWatcher.Tray;

/// <summary>
/// The tray icon as a square 32-bit ARGB pixel buffer (row-major, top row first): a dark tile with the charge percent on
/// top and a bar under it whose fill is the charge, green on mains, amber on battery, grey with no fresh reading.
/// Pure: no handle is made here (<see cref="TrayHost"/> turns the buffer into an icon).
/// </summary>
internal static class TrayIconRenderer
{
    public const uint Clear = 0x00000000;
    public const uint Tile = 0xFF202020;
    public const uint Track = 0xFF505050;
    public const uint Digit = 0xFFFFFFFF;
    public const uint StaleDigit = 0xFF9A9A9A;
    public const uint MainsFill = 0xFF2EA043;
    public const uint BatteryFill = 0xFFE3A008;
    public const uint UnknownFill = 0xFF8A8A8A;

    /// <summary>A 3x5 font, one row per entry, bit 2 = the left column. Index 10 is "?".</summary>
    private static readonly byte[][] Glyphs =
    [
        [7, 5, 5, 5, 7], [2, 6, 2, 2, 7], [7, 1, 7, 4, 7], [7, 1, 7, 1, 7], [5, 5, 7, 1, 1],
        [7, 4, 7, 1, 7], [7, 4, 7, 5, 7], [7, 1, 1, 1, 1], [7, 5, 7, 5, 7], [7, 5, 7, 1, 7],
        [7, 1, 3, 0, 2],
    ];

    /// <summary>The digits shown: the charge rounded, within 0..100; "?" with no charge known.</summary>
    public static string Text(double? soc) =>
        soc is { } v && double.IsFinite(v) ? ((int)Math.Round(Math.Clamp(v, 0, 100), MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) : "?";

    public static uint FillColor(TrayPower power) => power switch
    {
        TrayPower.Mains => MainsFill,
        TrayPower.Battery => BatteryFill,
        _ => UnknownFill,
    };

    /// <summary>Everything the picture depends on: an equal key draws the same pixels, so the icon is not rebuilt.</summary>
    public static IconKey Key(TraySnapshot s, int size)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new IconKey(Text(s.Soc), s.Power, s.Stale, size);
    }

    public static uint[] Render(TraySnapshot s, int size) => Render(Key(s, size));

    public static uint[] Render(IconKey key)
    {
        int size = key.Size;
        if (size < 8) throw new ArgumentOutOfRangeException(nameof(key), size, "an icon is at least 8 pixels");
        var px = new uint[size * size];
        Array.Fill(px, Tile);
        foreach (int corner in new[] { 0, size - 1, size * (size - 1), size * size - 1 }) px[corner] = Clear;

        int barH = Math.Max(2, size / 5);
        int barTop = size - 1 - barH;
        int barW = size - 2;
        Fill(px, size, 1, barTop, barW, barH, Track);
        // The bar follows the digits shown, so the key holds everything the picture depends on.
        int filled = int.TryParse(key.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int pct) ? (pct * barW + 50) / 100 : 0;
        Fill(px, size, 1, barTop, filled, barH, FillColor(key.Power));

        string text = key.Text;
        int areaH = barTop - 1;   // rows 1 .. barTop-1, one row of tile above the bar
        int scale = Math.Max(1, Math.Min(areaH / 5, (size - 2) / (4 * text.Length - 1)));
        int textW = scale * (4 * text.Length - 1);
        int x0 = (size - textW) / 2;
        int y0 = 1 + Math.Max(0, (areaH - 5 * scale) / 2);
        uint ink = key.Stale ? StaleDigit : Digit;
        for (int i = 0; i < text.Length; i++)
        {
            byte[] glyph = Glyphs[text[i] == '?' ? 10 : text[i] - '0'];
            for (int row = 0; row < 5; row++)
                for (int col = 0; col < 3; col++)
                    if ((glyph[row] >> (2 - col) & 1) == 1) Fill(px, size, x0 + (i * 4 + col) * scale, y0 + row * scale, scale, scale, ink);
        }
        return px;
    }

    private static void Fill(uint[] px, int size, int x, int y, int w, int h, uint color)
    {
        for (int r = Math.Max(0, y); r < Math.Min(size, y + h); r++)
            for (int c = Math.Max(0, x); c < Math.Min(size, x + w); c++)
                px[r * size + c] = color;
    }
}
