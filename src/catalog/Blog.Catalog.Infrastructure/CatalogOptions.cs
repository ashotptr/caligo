namespace Blog.Catalog.Infrastructure
{
    public class CatalogOptions
    {
        public const string SectionName = "Catalog";

        public bool Disabled { get; set; }

        public bool Enabled { get; set; }

        public string ConnectionString { get; set; } = string.Empty;

        public string? CaligoPublicRoot { get; set; }

        public string MusicUrlPrefix { get; set; } = "/music/";

        public string MixtapeUrlPrefix { get; set; } = "/mixtape/";
    }
}
