using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Blog.Api.Catalog;
using Blog.Api.Dtos;
using Blog.Catalog.Application.Accounts;
using Blog.Catalog.Application.Activity;

namespace Blog.Api.Controllers;

[Route("api/me")]
[ApiController]
[Authorize]
[RequireCatalog]
[EnableRateLimiting("reads")]
public class StreamingAccountController : ApiControllerBase
{
    private readonly StreamingAccountService _accounts;
    private readonly ActivityService _activity;

    public StreamingAccountController(StreamingAccountService accounts, ActivityService activity)
    {
        _accounts = accounts;
        _activity = activity;
    }

    [HttpGet("streaming-profile")]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken) =>
        Respond(await _accounts.GetProfileAsync(CurrentCaller(), cancellationToken));

    [HttpPost("streaming-profile")]
    public async Task<IActionResult> CreateProfile(StreamingProfileRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var command = new CreateProfileCommand(
            request.FirstName, request.LastName, request.DateOfBirth, request.Gender, request.SubscriptionPlanId);

        return Respond(await _accounts.CreateProfileAsync(CurrentCaller(), command, cancellationToken));
    }

    [HttpPost("subscription")]
    public async Task<IActionResult> Renew(SubscriptionRequest request, CancellationToken cancellationToken) =>
        Respond(await _accounts.RenewAsync(CurrentCaller(), request.SubscriptionPlanId, cancellationToken));

    [HttpGet("spending")]
    public async Task<IActionResult> GetSpending(CancellationToken cancellationToken) =>
        Respond(await _accounts.GetSpendingAsync(CurrentCaller(), cancellationToken));

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(CancellationToken cancellationToken) =>
        Respond(await _activity.GetHistoryAsync(CurrentCaller(), cancellationToken));

    [HttpGet("recommendations")]
    public async Task<IActionResult> GetRecommendations(CancellationToken cancellationToken) =>
        Respond(await _activity.GetRecommendationsAsync(CurrentCaller(), cancellationToken));

    [HttpGet("notifications")]
    public async Task<IActionResult> GetNotifications(CancellationToken cancellationToken) =>
        Respond(await _activity.GetNotificationsAsync(CurrentCaller(), cancellationToken));
}
