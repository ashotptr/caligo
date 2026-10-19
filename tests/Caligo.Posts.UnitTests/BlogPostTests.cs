using Caligo.Posts.Domain.Events;
using Caligo.Posts.Domain.Posts;
using Xunit;

namespace Caligo.Posts.UnitTests;

public class BlogPostTests
{
    private static readonly DateTime Now = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Publish_RaisesAnEvent_ThatIsNotBuiltUntilItIsDrained()
    {
        var post = BlogPost.Publish("Title", "Content", new[] { "kafka" }, "author-1", Now);

        Assert.True(post.HasPendingEvents);

        var raised = Assert.Single(post.DrainDomainEvents());
        var published = Assert.IsType<PostPublished>(raised);

        Assert.Equal("Title", published.Title);
        Assert.Equal("author-1", published.AuthorId);
        Assert.Equal(new[] { "kafka" }, published.Tags);
    }

    [Fact]
    public void Draining_ClearsThePendingEvents_SoNothingIsPublishedTwice()
    {
        var post = BlogPost.Publish("Title", "Content", null, null, Now);

        Assert.Single(post.DrainDomainEvents());
        Assert.Empty(post.DrainDomainEvents());
        Assert.False(post.HasPendingEvents);
    }

    [Fact]
    public void EachEvent_CarriesItsOwnIdentity()
    {
        var post = BlogPost.Publish("Title", "Content", null, null, Now);
        post.Revise("Title", "Revised", null);

        var events = post.DrainDomainEvents();

        Assert.Equal(2, events.Count);
        Assert.Distinct(events.Select(e => e.EventId));
    }

    [Fact]
    public void Revise_ReplacesContentAndTags()
    {
        var post = BlogPost.Publish("Title", "Content", new[] { "old" }, "author-1", Now);
        post.DrainDomainEvents();

        post.Revise("New title", "New content", new[] { "new" });

        Assert.Equal("New title", post.Title);
        Assert.Equal("New content", post.Content);
        Assert.Equal(new[] { "new" }, post.Tags);
        Assert.IsType<PostUpdated>(Assert.Single(post.DrainDomainEvents()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Publish_RejectsAnEmptyTitle(string title)
    {
        Assert.Throws<ArgumentException>(() => BlogPost.Publish(title, "Content", null, null, Now));
    }

    [Fact]
    public void TheAuthor_MayModifyTheirOwnPost()
    {
        var post = BlogPost.Publish("Title", "Content", null, "author-1", Now);

        Assert.True(post.CanBeModifiedBy("author-1", isAdministrator: false));
    }

    [Fact]
    public void AStranger_MayNot()
    {
        var post = BlogPost.Publish("Title", "Content", null, "author-1", Now);

        Assert.False(post.CanBeModifiedBy("someone-else", isAdministrator: false));
    }

    [Fact]
    public void AnAdministrator_May()
    {
        var post = BlogPost.Publish("Title", "Content", null, "author-1", Now);

        Assert.True(post.CanBeModifiedBy("someone-else", isAdministrator: true));
    }

    [Fact]
    public void AnAnonymousCaller_MayNotModifyAnAuthorlessPost()
    {
        var post = BlogPost.Publish("Title", "Content", null, null, Now);

        Assert.False(post.CanBeModifiedBy(null, isAdministrator: false));
    }

    [Fact]
    public void EveryRevision_BumpsTheVersion()
    {
        var post = BlogPost.Publish("Title", "Content", null, "author-1", Now);

        post.Revise("One", "Content", null);
        post.Revise("Two", "Content", null);

        Assert.Equal(2, post.Version);
    }

    [Fact]
    public void Schedule_AnnouncesNothing_UntilThePostGoesLive()
    {
        var post = BlogPost.Schedule("Title", "Content", null, "author-1", Now.AddHours(1));

        Assert.Equal(PostStatus.Scheduled, post.Status);
        Assert.False(post.HasPendingEvents);
    }

    [Fact]
    public void PublishDue_BeforeItsTime_DoesNothing()
    {
        var post = BlogPost.Schedule("Title", "Content", null, "author-1", Now.AddHours(1));

        Assert.False(post.PublishDue(Now));
        Assert.Equal(PostStatus.Scheduled, post.Status);
        Assert.False(post.HasPendingEvents);
    }

    [Fact]
    public void PublishDue_OnceDue_PublishesAtThatMoment_AndRaisesPublishedExactlyOnce()
    {
        var post = BlogPost.Schedule("Title", "Content", new[] { "kafka" }, "author-1", Now);
        var goLive = Now.AddSeconds(1);

        Assert.True(post.PublishDue(goLive));
        Assert.False(post.PublishDue(goLive));

        Assert.True(post.IsPublished);
        Assert.Equal(goLive, post.PublishedDate);
        Assert.Equal(1, post.Version);
        Assert.IsType<PostPublished>(Assert.Single(post.DrainDomainEvents()));
    }

    [Fact]
    public void RevisingAScheduledPost_AnnouncesNothing()
    {
        var post = BlogPost.Schedule("Title", "Content", null, "author-1", Now.AddHours(1));

        post.Revise("Edited before launch", "Content", null);
        post.MarkDeleted();

        Assert.False(post.HasPendingEvents);
    }

    [Fact]
    public void APublishedPost_CannotBeRescheduled()
    {
        var post = BlogPost.Publish("Title", "Content", null, "author-1", Now);

        Assert.Throws<InvalidOperationException>(() => post.Reschedule(Now.AddDays(1)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public void AClapRequest_MustBeBetweenOneAndTheCap(int count, bool valid)
    {
        Assert.Equal(valid, ClapRules.IsValidRequest(count));
    }
}

public class PostTagsTests
{
    [Fact]
    public void TagsAreTrimmedAndLowercased()
    {
        Assert.Equal(new[] { "c#", "kafka" }, PostTags.Normalize(new[] { "  C#  ", "Kafka" }));
    }

    [Fact]
    public void DuplicatesCollapse_AfterNormalisation()
    {
        Assert.Equal(new[] { "kafka" }, PostTags.Normalize(new[] { "Kafka", "kafka", " KAFKA " }));
    }

    [Fact]
    public void BlankTagsAreDropped()
    {
        Assert.Equal(new[] { "kept" }, PostTags.Normalize(new[] { "", "   ", "kept" }));
    }

    [Fact]
    public void NullIsTreatedAsNoTags()
    {
        Assert.Empty(PostTags.Normalize(null));
    }

    [Fact]
    public void TooManyTagsAreCapped()
    {
        var tags = Enumerable.Range(0, 50).Select(i => $"tag{i}");

        Assert.Equal(PostTags.MaxTagsPerPost, PostTags.Normalize(tags).Count);
    }

    [Fact]
    public void AnOverlongTagIsTruncated_RatherThanRejected()
    {
        var tag = new string('x', 500);

        Assert.Equal(PostTags.MaxTagLength, Assert.Single(PostTags.Normalize(new[] { tag })).Length);
    }
}

public class ExcerptTests
{
    [Fact]
    public void CodeFencesAreStripped()
    {
        var content = "Intro line.\n```csharp\nvar x = 1;\n```\nOutro line.";

        var excerpt = Excerpt.From(content);

        Assert.DoesNotContain("var x = 1", excerpt);
        Assert.Contains("Intro line.", excerpt);
        Assert.Contains("Outro line.", excerpt);
    }

    [Fact]
    public void MarkdownEmphasisAndHeadingsAreRemoved()
    {
        var excerpt = Excerpt.From("# Heading\n**bold** and *italic* and `code`");

        Assert.DoesNotContain("#", excerpt);
        Assert.DoesNotContain("*", excerpt);
        Assert.DoesNotContain("`", excerpt);
        Assert.Contains("bold", excerpt);
    }

    [Fact]
    public void LongContentIsTruncatedWithAnEllipsis()
    {
        var excerpt = Excerpt.From(new string('a', 1000));

        Assert.EndsWith("…", excerpt);
        Assert.True(excerpt.Length <= Excerpt.MaxLength + 1, $"excerpt was {excerpt.Length} characters");
    }

    [Fact]
    public void ShortContentIsLeftAlone()
    {
        Assert.Equal("Just a sentence.", Excerpt.From("Just a sentence."));
    }

    [Fact]
    public void EmptyContentYieldsAnEmptyExcerpt()
    {
        Assert.Equal(string.Empty, Excerpt.From("   "));
    }
}
