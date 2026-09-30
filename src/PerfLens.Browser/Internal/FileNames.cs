using System.Text;

namespace PerfLens.Browser.Internal;

internal static class FileNames
{
    private const int MaxSlugLength = 60;

    /// <summary>Lower-case, filesystem-safe slug: [a-z0-9-] only. "/" becomes "root".</summary>
    public static string Slugify(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > MaxSlugLength) slug = slug[..MaxSlugLength].TrimEnd('-');
        if (slug.Length == 0) slug = name.Trim() == "/" ? "root" : "page";
        return slug;
    }

    /// <summary>Slugify and de-duplicate against <paramref name="used"/> by appending -2, -3, ...</summary>
    public static string UniqueSlug(string name, ISet<string> used)
    {
        var baseSlug = Slugify(name);
        var slug = baseSlug;
        for (var n = 2; !used.Add(slug); n++) slug = $"{baseSlug}-{n}";
        return slug;
    }
}
