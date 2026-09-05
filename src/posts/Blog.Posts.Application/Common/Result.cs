namespace Blog.Posts.Application.Common;

public enum ResultStatus
{
    Ok,
    NotFound,
    Forbidden,
    Invalid,
    Unauthorized
}

public readonly record struct Result<T>(ResultStatus Status, T? Value, string? Error)
{
    public bool IsSuccess => Status == ResultStatus.Ok;

    public static Result<T> Ok(T value) => new(ResultStatus.Ok, value, null);

    public static Result<T> NotFound(string? error = null) => new(ResultStatus.NotFound, default, error);

    public static Result<T> Forbidden(string? error = null) => new(ResultStatus.Forbidden, default, error);

    public static Result<T> Invalid(string error) => new(ResultStatus.Invalid, default, error);

    public static Result<T> Unauthorized(string? error = null) => new(ResultStatus.Unauthorized, default, error);
}

public readonly record struct Result(ResultStatus Status, string? Error)
{
    public bool IsSuccess => Status == ResultStatus.Ok;

    public static Result Ok() => new(ResultStatus.Ok, null);

    public static Result NotFound(string? error = null) => new(ResultStatus.NotFound, error);

    public static Result Forbidden(string? error = null) => new(ResultStatus.Forbidden, error);

    public static Result Invalid(string error) => new(ResultStatus.Invalid, error);

    public static Result Unauthorized(string? error = null) => new(ResultStatus.Unauthorized, error);
}
