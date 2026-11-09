using Caligo.Api.Catalog;
using Xunit;

namespace Caligo.Api.Tests;

public class DemoPlanTests
{
    private const string Plan = """
        {"version": 1, "seed": 7, "zone": "Asia/Yerevan", "start": "2026-08-09T14:00:00", "end": "2026-11-07T14:00:00",
         "titles": [{"kind": "film", "title": "Inception", "year": 2010},
                    {"kind": "episode", "series": "Breaking Bad", "season": 1, "episode": 2},
                    {"kind": "song", "uri": "/mixtape/nirvana/about-a-girl.mp3"}],
         "users": [{"user_name": "lena.king05", "first_name": "Lena", "last_name": "King", "dob": "2005-05-31",
                    "gender": "Female", "email": "lena.king05@demo.invalid", "joined": "2026-08-12T18:26:00",
                    "watches": [[0, "2026-09-09T10:45:00", 2122.0], [2, "2026-09-10T20:00:00", null]],
                    "ratings": [[0, true, 10.0, "2026-09-09T12:00:00"], [2, true, null, "2026-09-10T20:03:00"]],
                    "comments": [[0, "A classic for a reason.", "2026-09-09T12:10:00"]],
                    "collections": [{"name": "Late night", "created": "2026-09-11T21:00:00",
                                     "titles": [[2, "2026-09-11T21:02:00"]]}]}],
         "report": {"users": 1}}
        """;

    [Fact]
    public void ParsePlan_ReadsWhatTheGeneratorWrites()
    {
        var plan = DemoActivity.ParsePlan(Plan);
        var person = Assert.Single(plan.Users);

        Assert.Equal(new DemoTitle("film", "Inception", 2010, null, null, null, null), plan.Titles[0]);
        Assert.Equal(new DemoTitle("episode", null, null, "Breaking Bad", 1, 2, null), plan.Titles[1]);
        Assert.Equal("/mixtape/nirvana/about-a-girl.mp3", plan.Titles[2].Uri);
        Assert.Equal("lena.king05", person.UserName);
        Assert.Equal("lena.king05@demo.invalid", person.Email);
        Assert.Equal(new DateOnly(2005, 5, 31), person.Dob);
        Assert.Equal("Female", person.Gender);
        Assert.Equal(2, person.Watches.Count);
        Assert.Equal(2122f, person.Watches[0][2].GetSingle());
        Assert.Equal("A classic for a reason.", person.Comments[0][1].GetString());
        Assert.Equal("Late night", Assert.Single(person.Collections).Name);
    }

    [Fact]
    public void Schedule_ListsEveryStepInTimeOrder()
    {
        var schedule = DemoActivity.Schedule(DemoActivity.ParsePlan(Plan));

        Assert.Equal(
            new[] { "Watch", "Rate", "Comment", "Watch", "Rate", "Shelve", "File" },
            schedule.Select(step => step.Kind).ToArray());
        Assert.All(schedule, step => Assert.Equal("lena.king05", step.UserName));
        Assert.Equal(new DateTime(2026, 9, 9, 10, 45, 0), schedule[0].At);
    }

    [Theory]
    [InlineData(null, "2026-09-09T10:45:00", "2026-09-09T10:45:00", true)]
    [InlineData("2026-09-09T10:45:00", "2026-09-09T10:45:00", "2026-09-10T00:00:00", false)]
    [InlineData("2026-09-09T10:45:00", "2026-09-09T10:46:00", "2026-09-10T00:00:00", true)]
    [InlineData(null, "2026-09-10T00:00:01", "2026-09-10T00:00:00", false)]
    public void IsDue_TakesEachStepOnceAndNeverEarly(string? after, string at, string until, bool expected) =>
        Assert.Equal(expected, DemoActivity.IsDue(
            DateTime.Parse(at), after is null ? null : DateTime.Parse(after), DateTime.Parse(until)));

    [Fact]
    public void ReceiptFor_SitsNextToThePlan() =>
        Assert.Equal(
            Path.Combine(Path.GetFullPath("demo"), DemoActivity.ReceiptName),
            DemoActivity.ReceiptFor(Path.Combine("demo", "plan.json")));

    [Fact]
    public void ParsePlan_RefusesAnotherVersion() =>
        Assert.Throws<InvalidOperationException>(() => DemoActivity.ParsePlan("""{"version": 2, "titles": [], "users": []}"""));

    [Theory]
    [InlineData(new[] { "seed-demo", "plan.json" }, true)]
    [InlineData(new[] { "unseed-demo" }, true)]
    [InlineData(new[] { "advance-demo", "plan.json" }, true)]
    [InlineData(new[] { "import-caligo-accounts" }, false)]
    [InlineData(new string[0], false)]
    public void Requested_OnlyForTheDemoCommands(string[] args, bool expected) =>
        Assert.Equal(expected, DemoActivity.Requested(args));
}
