namespace Blog.Posts.Application.Common;

// Raised by a repository when a save lost an optimistic-concurrency race, so the application layer
// can answer 409 without knowing which persistence library detected it.
public class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
