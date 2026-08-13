using System.Text.RegularExpressions;

namespace Undertow.WireDiff;

/// <summary>
/// The shadow differ: compares two recorded transcript directories (e.g. Gleam
/// Floodgate vs Undertow driven through the same script) frame by frame, after
/// masking fields that legitimately differ run to run (ids, timestamps,
/// signatures). Converts "N tests fail" into "this byte of this frame differs".
/// </summary>
public static partial class WireDiffer
{
    [GeneratedRegex(@"(""sid""|""clientId""|""jti""|""nonce""|""jwt""|""token"")\s*:\s*""[^""]*""")]
    private static partial Regex IdFields();

    [GeneratedRegex(@"(""timestamp""|""iat""|""exp""|""expiresIn"")\s*:\s*\d+")]
    private static partial Regex TimeFields();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+")]
    private static partial Regex JwtLiterals();

    [GeneratedRegex(@"\b[0-9A-F]{32}\b")]
    private static partial Regex HexIds();

    [GeneratedRegex(@"\b[0-9a-f]{40}\b")]
    private static partial Regex Sha1s();

    /// <summary>Mask run-variable content so structural differences stand out.</summary>
    internal static string Mask(string line)
    {
        line = JwtLiterals().Replace(line, "<jwt>");
        line = IdFields().Replace(line, "$1:\"<id>\"");
        // masked after ids so quoted hex inside id fields is already gone
        line = TimeFields().Replace(line, "$1:<t>");
        line = HexIds().Replace(line, "<hexid>");
        line = Sha1s().Replace(line, "<sha1>");
        return line;
    }

    public static int Run(string leftDir, string rightDir)
    {
        if (!Directory.Exists(leftDir) || !Directory.Exists(rightDir))
        {
            Console.Error.WriteLine("diff: both --left and --right must be existing directories");
            return 2;
        }

        var differences = 0;
        var leftFiles = Directory.GetFiles(leftDir, "*.txt").Select(Path.GetFileName).Order().ToList();
        foreach (var name in leftFiles)
        {
            if (name is null or "SOURCE.txt")
                continue;

            var rightPath = Path.Combine(rightDir, name);
            if (!File.Exists(rightPath))
            {
                Console.WriteLine($"{name}: missing on right");
                differences++;
                continue;
            }

            var left = File.ReadAllLines(Path.Combine(leftDir, name)).Select(Mask).ToArray();
            var right = File.ReadAllLines(rightPath).Select(Mask).ToArray();
            for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                var l = i < left.Length ? left[i] : "<absent>";
                var r = i < right.Length ? right[i] : "<absent>";
                if (l == r)
                    continue;

                differences++;
                var firstDiff = 0;
                while (firstDiff < l.Length && firstDiff < r.Length && l[firstDiff] == r[firstDiff])
                    firstDiff++;
                Console.WriteLine($"{name}:{i + 1}: differs at byte {firstDiff}");
                Console.WriteLine($"  left : {Excerpt(l, firstDiff)}");
                Console.WriteLine($"  right: {Excerpt(r, firstDiff)}");
            }
        }

        Console.WriteLine(differences == 0 ? "transcripts match" : $"{differences} difference(s)");
        return differences == 0 ? 0 : 1;
    }

    private static string Excerpt(string line, int at)
    {
        var start = Math.Max(0, at - 40);
        var end = Math.Min(line.Length, at + 80);
        return (start > 0 ? "…" : "") + line[start..end] + (end < line.Length ? "…" : "");
    }
}
