#nullable enable
using System;
using System.Globalization;

namespace YSharp;

/// <summary>
/// JS <c>Number::toString</c> for radix 10 (ECMA-262 6.1.6.1.20), docs/research/json.md section 2. .NET's "R" gives the same shortest
/// round-trip digits as V8; only the layout (exponent threshold, <c>e+</c>/<c>e-</c>, no padding) is redone here. -0 is "0"; the caller maps
/// NaN and the infinities (null for JSON, names for XML attribute text).
/// </summary>
internal static class JsNumberFormatter
{
    /// <summary>More than the longest text a finite double takes (a sign, 21 digits, or 17 digits with a point and an exponent).</summary>
    public const int MaxLength = 40;

    public static string ToString(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "Infinity";
        if (double.IsNegativeInfinity(v)) return "-Infinity";
        Span<char> buf = stackalloc char[MaxLength];
        var n = Format(v, buf);
        return new string(buf[..n]);
    }

    /// <summary>Formats a finite <paramref name="v"/> into <paramref name="dest"/> (at least <see cref="MaxLength"/> chars) and returns the length.</summary>
    public static int Format(double v, Span<char> dest)
    {
        if (v == 0) { dest[0] = '0'; return 1; }
        var pos = 0;
        if (v < 0) { dest[pos++] = '-'; v = -v; }

        Span<char> r = stackalloc char[32];
        if (!v.TryFormat(r, out var rl, "R", CultureInfo.InvariantCulture)) throw new InvalidOperationException();
        r = r[..rl];

        // split "d.dddE+xx" into the digit string and the decimal exponent
        var e10 = 0;
        var ei = r.IndexOfAny('E', 'e');
        var mantissa = r;
        if (ei >= 0)
        {
            e10 = int.Parse(r[(ei + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            mantissa = r[..ei];
        }
        Span<char> digits = stackalloc char[32];
        var k = 0;
        var dot = mantissa.IndexOf('.');
        var pointPos = dot >= 0 ? dot : mantissa.Length;
        foreach (var c in mantissa) if (c != '.') digits[k++] = c;
        var lead = 0;
        while (lead < k - 1 && digits[lead] == '0') lead++;
        pointPos -= lead;
        var ds = digits[lead..k];
        k = ds.Length;
        while (k > 1 && ds[k - 1] == '0') k--;
        ds = ds[..k];
        var n = pointPos + e10;   // value = 0.digits * 10^n

        if (k <= n && n <= 21)
        {
            ds.CopyTo(dest[pos..]); pos += k;
            for (var i = 0; i < n - k; i++) dest[pos++] = '0';
        }
        else if (0 < n && n <= 21)
        {
            ds[..n].CopyTo(dest[pos..]); pos += n;
            dest[pos++] = '.';
            ds[n..].CopyTo(dest[pos..]); pos += k - n;
        }
        else if (-6 < n && n <= 0)
        {
            dest[pos++] = '0'; dest[pos++] = '.';
            for (var i = 0; i < -n; i++) dest[pos++] = '0';
            ds.CopyTo(dest[pos..]); pos += k;
        }
        else
        {
            dest[pos++] = ds[0];
            if (k > 1) { dest[pos++] = '.'; ds[1..].CopyTo(dest[pos..]); pos += k - 1; }
            dest[pos++] = 'e';
            var exp = n - 1;
            dest[pos++] = exp < 0 ? '-' : '+';
            var text = Math.Abs(exp).ToString(CultureInfo.InvariantCulture);
            text.CopyTo(dest[pos..]);
            pos += text.Length;
        }
        return pos;
    }
}
