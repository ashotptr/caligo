namespace Caligo.Posts.Domain.Posts;

public static class Excerpt
{
    public const int MaxLength = 240;

    public static string From(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var plainLines = new List<string>();
        var inCodeFence = false;

        foreach (var line in content.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;

                continue;
            }

            if (!inCodeFence)
            {
                plainLines.Add(line
                    .Replace("**", string.Empty)
                    .Replace("*", string.Empty)
                    .Replace("`", string.Empty)
                    .TrimStart('#', ' '));
            }
        }

        var plain = string.Join(" ", plainLines.Where(line => !string.IsNullOrWhiteSpace(line))).Trim();

        return plain.Length <= MaxLength ? plain : plain[..MaxLength].TrimEnd() + "…";
    }
}
