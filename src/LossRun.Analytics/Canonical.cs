using System.Globalization;
using System.Text;

namespace LossRun.Analytics;

/// <summary>
/// The canonical JSON both engines write (brief 10 parity): compact, keys in the order written, strings escaped exactly as
/// the TypeScript twin escapes them, and every number formatted from an integer at a fixed scale — never from a double —
/// so C# and TypeScript output is byte-identical. The twin is <c>src/web/packages/metrics/src/canonical.ts</c>.
/// </summary>
public sealed class CanonicalWriter
{
    private readonly StringBuilder _sb = new();
    private readonly Stack<bool> _needComma = new();
    private bool _afterName;

    public override string ToString() => _sb.ToString();

    public CanonicalWriter StartObject()
    {
        BeforeValue();
        _sb.Append('{');
        _needComma.Push(false);
        return this;
    }

    public CanonicalWriter EndObject()
    {
        _needComma.Pop();
        _sb.Append('}');
        return this;
    }

    public CanonicalWriter StartArray()
    {
        BeforeValue();
        _sb.Append('[');
        _needComma.Push(false);
        return this;
    }

    public CanonicalWriter EndArray()
    {
        _needComma.Pop();
        _sb.Append(']');
        return this;
    }

    /// <summary>Writes <c>"name":</c>; the value written next belongs to it.</summary>
    public CanonicalWriter Name(string name)
    {
        BeforeValue();
        WriteString(name);
        _sb.Append(':');
        _afterName = true;
        return this;
    }

    public CanonicalWriter String(string? value)
    {
        BeforeValue();
        if (value is null)
        {
            _sb.Append("null");
        }
        else
        {
            WriteString(value);
        }

        return this;
    }

    public CanonicalWriter Bool(bool? value)
    {
        BeforeValue();
        _sb.Append(value is null ? "null" : value.Value ? "true" : "false");
        return this;
    }

    public CanonicalWriter Null()
    {
        BeforeValue();
        _sb.Append("null");
        return this;
    }

    /// <summary>An integer scaled by 10^<paramref name="scale"/>, printed as a decimal with trailing zeros trimmed (12345, 2 → 123.45).</summary>
    public CanonicalWriter Scaled(long? value, int scale)
    {
        BeforeValue();
        _sb.Append(value is null ? "null" : FormatScaled(value.Value, scale));
        return this;
    }

    /// <summary>Already-canonical JSON (another writer's output) as the next value.</summary>
    public CanonicalWriter Raw(string json)
    {
        BeforeValue();
        _sb.Append(json);
        return this;
    }

    public static string FormatScaled(long value, int scale)
    {
        if (scale == 0)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        var negative = value < 0;
        Int128 magnitude = negative ? -(Int128)value : value;
        Int128 divisor = 1;
        for (var i = 0; i < scale; i++)
        {
            divisor *= 10;
        }

        var whole = magnitude / divisor;
        var fraction = (magnitude % divisor).ToString(CultureInfo.InvariantCulture).PadLeft(scale, '0').TrimEnd('0');
        var text = whole.ToString(CultureInfo.InvariantCulture) + (fraction.Length > 0 ? "." + fraction : "");
        return negative ? "-" + text : text;
    }

    private void BeforeValue()
    {
        if (_afterName)
        {
            _afterName = false;
            return;
        }

        if (_needComma.Count > 0)
        {
            if (_needComma.Peek())
            {
                _sb.Append(',');
            }
            else
            {
                _needComma.Pop();
                _needComma.Push(true);
            }
        }
    }

    private void WriteString(string value)
    {
        _sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': _sb.Append("\\\""); break;
                case '\\': _sb.Append("\\\\"); break;
                case '\b': _sb.Append("\\b"); break;
                case '\f': _sb.Append("\\f"); break;
                case '\n': _sb.Append("\\n"); break;
                case '\r': _sb.Append("\\r"); break;
                case '\t': _sb.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                    {
                        _sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        _sb.Append(ch);
                    }

                    break;
            }
        }

        _sb.Append('"');
    }
}

/// <summary>Integer arithmetic shared (by definition) with the TypeScript twin.</summary>
public static class IntMath
{
    /// <summary>n / d rounded half away from zero; null when d is 0.</summary>
    public static long? RoundDiv(Int128 n, Int128 d)
    {
        if (d == 0)
        {
            return null;
        }

        var negative = (n < 0) != (d < 0);
        var an = n < 0 ? -n : n;
        var ad = d < 0 ? -d : d;
        var q = (an * 2 + ad) / (ad * 2);
        return (long)(negative ? -q : q);
    }

    /// <summary>x × bps / 10,000, rounded half away from zero.</summary>
    public static long MulBps(long x, long bps) => RoundDiv((Int128)x * bps, 10_000)!.Value;

    private static readonly int Epoch = new DateOnly(1970, 1, 1).DayNumber;

    /// <summary>Days since 1970-01-01 of an ISO date; null when absent or not a date.</summary>
    public static int? Day(string? iso) =>
        iso is { Length: 10 } && DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.DayNumber - Epoch : null;

    /// <summary>Median of whole days in tenths (mean of the two middle values for an even count).</summary>
    public static long? MedianTenths(List<long> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var n = values.Count;
        return n % 2 == 1 ? values[n / 2] * 10 : (values[n / 2 - 1] + values[n / 2]) * 5;
    }

    /// <summary>Nearest-rank percentile of whole days, in tenths.</summary>
    public static long? PercentileTenths(List<long> values, int percentile)
    {
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var rank = (percentile * values.Count + 99) / 100; // ceil(p × n / 100)
        return values[Math.Max(1, rank) - 1] * 10;
    }
}
