using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// Human-friendly durations, shared by the repop timer and the Manager's
/// duration fields: parses "660", "11m", "90s", "9m12s", "1h20m", "9:12" or
/// "1:20:00", and formats seconds back to the compact "9m12s" style.
/// </summary>
public static class DurationText
{
    private static readonly Regex UnitsRx = new(
        @"^(?:(?<h>\d+(?:\.\d+)?)\s*h)?\s*(?:(?<m>\d+(?:\.\d+)?)\s*m)?\s*(?:(?<s>\d+(?:\.\d+)?)\s*s?)?$",
        RegexOptions.Compiled);

    public static double? Parse(string? text)
    {
        text = (text ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0) return null;

        if (text.Contains(':'))
        {
            var parts = text.Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[0], out int m) && m >= 0
                && int.TryParse(parts[1], out int s) && s is >= 0 and < 60)
                return m * 60 + s;
            if (parts.Length == 3
                && int.TryParse(parts[0], out int h) && h >= 0
                && int.TryParse(parts[1], out int m2) && m2 is >= 0 and < 60
                && int.TryParse(parts[2], out int s2) && s2 is >= 0 and < 60)
                return h * 3600 + m2 * 60 + s2;
            return null;
        }

        var match = UnitsRx.Match(text);
        if (!match.Success) return null;

        double total = 0;
        if (match.Groups["h"].Success) total += Num(match.Groups["h"].Value) * 3600;
        if (match.Groups["m"].Success) total += Num(match.Groups["m"].Value) * 60;
        if (match.Groups["s"].Success) total += Num(match.Groups["s"].Value);
        return total > 0 ? total : null;

        static double Num(string v) => double.Parse(v, CultureInfo.InvariantCulture);
    }

    /// <summary>660 → "11m", 552 → "9m12s", 45 → "45s", 4805 → "1h20m5s".</summary>
    public static string Compact(double seconds)
    {
        if (seconds <= 0) return "0s";

        // Odd fractional values (rare) fall back to plain seconds so nothing is lost.
        if (seconds < 60 || Math.Abs(seconds - Math.Round(seconds)) > 0.001)
            return seconds.ToString("0.##", CultureInfo.InvariantCulture) + "s";

        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        var sb = new StringBuilder();
        if ((int)t.TotalHours > 0) sb.Append((int)t.TotalHours).Append('h');
        if (t.Minutes > 0) sb.Append(t.Minutes).Append('m');
        if (t.Seconds > 0) sb.Append(t.Seconds).Append('s');
        return sb.ToString();
    }
}
