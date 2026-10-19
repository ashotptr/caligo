using System.Reflection;
using Caligo.Catalog.Application.Contents;
using Caligo.Catalog.Domain.Contents;
using Xunit;

namespace Caligo.Catalog.UnitTests;

public class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(Content).Assembly;
    private static readonly Assembly Application = typeof(CatalogService).Assembly;

    private static readonly string[] ForbiddenPrefixes =
    {
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "dbup",
        "Microsoft.AspNetCore",
        "Caligo.Posts"
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
            "The catalog domain must stay dependency free, but it references: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheApplicationLayer_KnowsNothingAboutTheDatabaseTheWebOrThePostsContext()
    {
        var offenders = Application.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => ForbiddenPrefixes.Any(bad => name.StartsWith(bad, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The catalog application layer must depend on abstractions only, but it references: "
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
            .Where(t => t.IsPublic && t.Name.Length > 1 && t.Name[0] == 'I' && char.IsUpper(t.Name[1]))
            .ToList();

        Assert.NotEmpty(ports);
        Assert.All(ports, port => Assert.True(port.IsInterface, $"{port.Name} is not an interface"));
    }
}
