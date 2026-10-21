namespace Caligo.Catalog.Infrastructure.Engine
{
    public static class MixtapePaths
    {
        public static string? Folder(string? uri, string prefix) =>
            Split(uri, prefix) is { } parts ? parts.Folder : null;

        public static (string Folder, string File)? Split(string? uri, string prefix)
        {
            if (uri is null || !uri.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            var rest = uri[prefix.Length..];
            var slash = rest.IndexOf('/');

            if (slash <= 0 || slash == rest.Length - 1)
            {
                return null;
            }

            var folder = Uri.UnescapeDataString(rest[..slash]);
            var file = Uri.UnescapeDataString(rest[(slash + 1)..]);

            return Safe(folder) && Safe(file) ? (folder, file) : null;
        }

        private static bool Safe(string part) =>
            part.Length > 0 && part != "." && part != ".." && part.IndexOfAny(new[] { '/', '\\', '\0' }) < 0;
    }
}
