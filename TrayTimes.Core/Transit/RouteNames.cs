using System.Text;

namespace TrayTimes.Transit;

public static class RouteNames
{
    /// <summary>The longest name a narrow route badge shows whole ("Blue", "UP-N").</summary>
    private const int BadgeLength = 4;

    private const int ShortLength = 3;

    private const string LineSuffix = "Line";

    /// <summary>True when the route goes by a number alone ("15"), so text names it as a route ("Route 15") rather than as the agency writes it ("30X", "Green").</summary>
    public static bool IsNumber(string displayId) =>
        displayId.Length > 0 && displayId.All(char.IsAsciiDigit);

    /// <summary>
    /// The route's name for a narrow badge, after dropping a trailing "Line" ("PurpleLine", "M-Line" is "M"). A word
    /// of more than four letters becomes three capitals from its consonants ("Brown" is "BRN", "Purple" is "PRP"),
    /// or from its first letters when it has too few ("Yellow" is "YEL"). Anything else ("151", "UP-NW") is left as it is.
    /// </summary>
    public static string Short(string displayId)
    {
        string name = WithoutLineSuffix(displayId.Trim());
        if (name.Length <= BadgeLength || !name.All(char.IsLetter))
        {
            return name;
        }

        StringBuilder consonants = new(name[..1], ShortLength);
        for (int i = 1; i < name.Length && consonants.Length < ShortLength; i++)
        {
            char c = name[i];
            if (!IsVowelLike(c) && char.ToUpperInvariant(c) != char.ToUpperInvariant(consonants[^1]))
            {
                consonants.Append(c);
            }
        }

        return (consonants.Length == ShortLength ? consonants.ToString() : name[..ShortLength]).ToUpperInvariant();
    }

    private static string WithoutLineSuffix(string name)
    {
        if (name.Length <= LineSuffix.Length || !name.EndsWith(LineSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        string stem = name[..^LineSuffix.Length].TrimEnd(' ', '-');
        return stem.Length > 0 ? stem : name;
    }

    /// <summary>A vowel, accents aside, or a letter that often stands in for one ("w" in "Brown", "y" in "Gray").</summary>
    private static bool IsVowelLike(char c) =>
        "aeiouwy".Contains(char.ToLowerInvariant(c.ToString().Normalize(NormalizationForm.FormD)[0]), StringComparison.Ordinal);
}
