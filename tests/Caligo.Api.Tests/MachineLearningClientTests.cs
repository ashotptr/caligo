using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Caligo.Api.MachineLearning;
using Caligo.Catalog.Application.Contents;
using Xunit;

namespace Caligo.Api.Tests;

public class MachineLearningClientTests
{
    private static readonly CommentDto Comment = new(7, 3, "listener", "Nice song");

    [Fact]
    public async Task ScoreComment_SendsTheCommentInSnakeCase()
    {
        var handler = new Recorder(TimeSpan.Zero);

        await Client(handler).ScoreCommentAsync(Comment);

        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(request.Body);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/moderation/comments", request.Path);
        Assert.Equal(7, body.RootElement.GetProperty("comment_id").GetInt32());
        Assert.Equal(3, body.RootElement.GetProperty("content_id").GetInt32());
        Assert.Equal("Nice song", body.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ScoreComment_GivesUpQuicklyWhenTheServiceIsSlow()
    {
        var watch = Stopwatch.StartNew();

        await Client(new Recorder(TimeSpan.FromSeconds(10))).ScoreCommentAsync(Comment);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task ScoreComment_ShrugsOffAnUnreachableService()
    {
        await Client(new Unreachable()).ScoreCommentAsync(Comment);
    }

    [Fact]
    public async Task ScoreComment_SendsNothingWithoutAServiceAddress()
    {
        var handler = new Recorder(TimeSpan.Zero);
        var client = Client(handler, null);

        await client.ScoreCommentAsync(Comment);

        Assert.False(client.IsConfigured);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Send_PassesTheStatusAndBodyBack()
    {
        var handler = new Recorder(TimeSpan.Zero, HttpStatusCode.NotFound, "{\"detail\":\"missing\"}");

        var (status, body) = await Client(handler).SendAsync(HttpMethod.Post, "songs/label", new { Uri = "/music/a.mp3" });

        Assert.Equal(404, status);
        Assert.Equal("{\"detail\":\"missing\"}", body);
        Assert.Equal("{\"uri\":\"/music/a.mp3\"}", Assert.Single(handler.Requests).Body);
    }

    private static MachineLearningClient Client(HttpMessageHandler handler, string? address = "http://caligo-ml:8080/") =>
        new(new HttpClient(handler) { BaseAddress = address is null ? null : new Uri(address) },
            NullLogger<MachineLearningClient>.Instance);

    private sealed record Sent(HttpMethod Method, string Path, string Body);

    private sealed class Recorder(TimeSpan delay, HttpStatusCode status = HttpStatusCode.OK, string reply = "{}")
        : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Sent(request.Method, request.RequestUri!.AbsolutePath, body));
            await Task.Delay(delay, cancellationToken);

            return new HttpResponseMessage(status) { Content = new StringContent(reply) };
        }
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
