using System.Globalization;
using System.Text.RegularExpressions;
using AiFootBall.Core.Models;

namespace AiFootBall.Sources;

/// <summary>解析 kaijiang.500.com 胜负彩开奖页。彩果是 14 个 cfont5 的 3/1/0，奖金表是一等奖、二等奖、任九。</summary>
public static partial class SfcDrawParser
{
    public static SfcDraw? Parse(string? html, string expect)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var balls = BallRegex().Matches(html);
        if (balls.Count < 14) return null;
        var result = string.Concat(balls.Take(14).Select(m => m.Groups[1].Value));
        if (result.Length != 14 || result.Any(c => c is not ('3' or '1' or '0'))) return null;
        if (!TryPrize(html, "一等奖", out _, out var first)) return null;
        if (!TryPrize(html, "二等奖", out _, out var second)) return null;
        if (!TryPrize(html, "任九", out _, out var renjiu) && !TryPrize(html, "任选九", out _, out renjiu))
            return null;
        return new SfcDraw
        {
            Expect = expect,
            Result = result,
            FirstPrize = first,
            SecondPrize = second,
            RenjiuPrize = renjiu,
            Complete = true
        };
    }

    private static bool TryPrize(string html, string name, out int count, out decimal amount)
    {
        count = 0;
        amount = 0;
        var m = Regex.Match(html, name + @"\s*</td>\s*<td[^>]*>\s*([\d,]+)\s*</td>\s*<td[^>]*>\s*([\d,\.]+)\s*</td>", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
            return false;
        return decimal.TryParse(m.Groups[2].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    [GeneratedRegex(@"class=""cfont5[^""]*"">\s*([310])\s*<", RegexOptions.IgnoreCase)]
    private static partial Regex BallRegex();
}
