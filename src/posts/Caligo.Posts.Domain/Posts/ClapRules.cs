namespace Caligo.Posts.Domain.Posts;

public static class ClapRules
{
    public const int MaxPerReader = 50;

    public static bool IsValidRequest(int count) => count is >= 1 and <= MaxPerReader;
}
