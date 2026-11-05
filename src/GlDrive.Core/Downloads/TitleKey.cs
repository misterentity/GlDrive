using System.Text;

namespace GlDrive.Downloads;

/// <summary>
/// The comparison key for a media title: NFKC-normalized, then lower-case letters and digits only.
/// NFKC folds full-width forms and ligatures ("ＦＯＯ", "ﬁ") into their plain letters first, so every
/// consumer (wishlist, library index, nuke watch, metadata lookups) derives the same key.
/// Unlike <see cref="SceneNameParser"/>'s title match it keeps leading articles and drops spaces.
/// </summary>
public static class TitleKey
{
    public static string Normalize(string? title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        var builder = new StringBuilder(title.Length);
        foreach (var c in title.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        return builder.ToString();
    }
}
