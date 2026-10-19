namespace Blog.SharedKernel;

public readonly record struct Caller(string? UserId, bool IsAdministrator)
{
    public static readonly Caller Anonymous = new(null, false);

    public bool IsAuthenticated => UserId is not null;

    public bool CanManage(string? authorId) => IsAdministrator || (UserId is not null && UserId == authorId);
}
