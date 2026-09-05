using System.Reflection;
using Blog.Posts.Application.Posts;
using Blog.Posts.Domain.Posts;
using Xunit;

namespace Blog.Posts.UnitTests;

public class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(BlogPost).Assembly;
    private static readonly Assembly Application = typeof(PostService).Assembly;

    private static readonly string[] ForbiddenPrefixes =
    {
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "MongoDB",
        "RabbitMQ",
        "StackExchange.Redis",
        "Microsoft.AspNetCore"
    };

    [Fact]
    public void TheDomain_ReferencesNothingButTheBaseClassLibrary()
    {
        var offenders = Domain.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !name.StartsWith("System", StringComparison.Ordinal)
                           && !name.Equals("netstandard", StringComparison.Ordinal)
                           && !name.Equals("mscorlib", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The domain must stay dependency free, but it references: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheApplicationLayer_KnowsNothingAboutEfCoreOrTheWeb()
    {
        var offenders = Application.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => ForbiddenPrefixes.Any(bad => name.StartsWith(bad, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The application layer must depend on abstractions only, but it references: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void TheDomain_DoesNotDependOnTheApplicationLayer()
    {
        Assert.DoesNotContain(
            Domain.GetReferencedAssemblies(),
            a => a.Name == Application.GetName().Name);
    }

    [Fact]
    public void EveryPortIsAnInterface_SoItCanBeSubstituted()
    {
        var ports = Application.GetTypes()
            .Where(t => t.Namespace == "Blog.Posts.Application.Abstractions" && t.Name.StartsWith('I'))
            .ToList();

        Assert.NotEmpty(ports);
        Assert.All(ports, port => Assert.True(port.IsInterface, $"{port.Name} is not an interface"));
    }
}
