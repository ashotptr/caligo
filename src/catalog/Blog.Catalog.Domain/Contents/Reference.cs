namespace Blog.Catalog.Domain.Contents;

public class Genre
{
    public int GenreId { get; set; }

    public string Name { get; set; } = string.Empty;
}

public class Language
{
    public int LanguageId { get; set; }

    public string Name { get; set; } = string.Empty;
}

public class ProductionCompany
{
    public const int MaxNameLength = 50;

    public int ProductionCompanyId { get; set; }

    public string Name { get; set; } = string.Empty;
}

public class DistributingCompany
{
    public int DistributingCompanyId { get; set; }

    public string Name { get; set; } = string.Empty;
}

public class ContentGenre
{
    public int ContentId { get; set; }

    public int GenreId { get; set; }
}

public class ContentLanguage
{
    public int ContentId { get; set; }

    public int LanguageId { get; set; }
}

public class ContentDistributingCompany
{
    public int ContentId { get; set; }

    public int DistributingCompanyId { get; set; }
}
