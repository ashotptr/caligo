using Microsoft.Extensions.Options;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Media;

namespace Caligo.Catalog.Infrastructure.Media
{
    public class MediaInbox : IMediaInbox
    {
        public const int MaxEntries = 1000;

        private readonly string _root;

        public MediaInbox(IOptions<MediaStorageOptions> options)
        {
            _root = string.IsNullOrWhiteSpace(options.Value.IncomingPath)
                ? string.Empty
                : Path.GetFullPath(options.Value.IncomingPath);
        }

        public bool IsConfigured => _root.Length > 0 && Directory.Exists(_root);

        public IReadOnlyList<InboxEntry> List()
        {
            if (!IsConfigured)
            {
                return Array.Empty<InboxEntry>();
            }

            return new DirectoryInfo(_root)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(file => MediaPaths.IsPlainFileName(file.Name) && !file.Attributes.HasFlag(FileAttributes.Hidden))
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxEntries)
                .Select(file => new InboxEntry(file.Name, file.Length, file.LastWriteTimeUtc))
                .ToList();
        }

        public bool Contains(string fileName) =>
            IsConfigured && Locate(_root, fileName) is not null;

        public static string? Locate(string root, string fileName)
        {
            if (!MediaPaths.IsPlainFileName(fileName))
            {
                return null;
            }

            var full = Path.GetFullPath(Path.Combine(root, fileName));

            return string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), PathComparison)
                   && File.Exists(full)
                ? full
                : null;
        }

        private static StringComparison PathComparison =>
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }
}
