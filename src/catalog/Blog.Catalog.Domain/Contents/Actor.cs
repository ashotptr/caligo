using Blog.Catalog.Domain.Users;

namespace Blog.Catalog.Domain.Contents;

public class Actor
{
    public int ActorId { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public DateOnly Dob { get; set; }

    public string Nationality { get; set; } = string.Empty;

    public string Biography { get; set; } = string.Empty;

    public Gender Gender { get; set; }

    public string? ProfilePictureUri { get; set; }
}

public class ContentActor
{
    public int ContentId { get; set; }

    public int ActorId { get; set; }

    public float? Payment { get; set; }
}
