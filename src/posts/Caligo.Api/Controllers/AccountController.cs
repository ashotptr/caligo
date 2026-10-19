using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using Caligo.Posts.Infrastructure.Identity;
using Caligo.Api.Dtos;
using Caligo.Api.Services;
using Google.Apis.Auth;

namespace Caligo.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AccountController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ITokenService _tokenService;
    private readonly IRefreshSessionService _sessions;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        UserManager<ApplicationUser> userManager,
        ITokenService tokenService,
        IRefreshSessionService sessions,
        IConfiguration configuration,
        ILogger<AccountController> logger)
    {
        _userManager = userManager;
        _tokenService = tokenService;
        _sessions = sessions;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("register")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Register(RegisterDto registerDto)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var user = new ApplicationUser { UserName = registerDto.Username, Email = registerDto.Email };

        var result = await _userManager.CreateAsync(user, registerDto.Password);

        if (!result.Succeeded)
        {
            return BadRequest(result.Errors);
        }

        await _userManager.AddToRoleAsync(user, "Reader");

        return Ok(new { Message = "User registered successfully!" });
    }

    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(LoginDto loginDto, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var user = await _userManager.FindByNameAsync(loginDto.Username);

        if (user is null || !await _userManager.CheckPasswordAsync(user, loginDto.Password))
        {
            return Unauthorized("Invalid credentials");
        }

        return Ok(await IssueTokensAsync(user, cancellationToken));
    }

    [HttpPost("refresh")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Refresh(TokenDto tokenDto, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tokenDto.AccessToken) || string.IsNullOrWhiteSpace(tokenDto.RefreshToken))
        {
            return BadRequest("Invalid client request");
        }

        var claimedUserId = _tokenService.GetUserIdFromExpiredToken(tokenDto.AccessToken);

        if (claimedUserId is null)
        {
            return BadRequest("Invalid token");
        }

        var result = await _sessions.RotateAsync(
            tokenDto.RefreshToken, RemoteIp(), Request.Headers.UserAgent.ToString(), cancellationToken);

        if (result.Outcome != RefreshOutcome.Success || result.User is null)
        {
            if (result.Outcome == RefreshOutcome.Reused)
            {
                return Unauthorized("This session has been revoked. Please sign in again.");
            }

            return Unauthorized("Invalid client request");
        }

        if (result.User.Id != claimedUserId)
        {
            _logger.LogWarning(
                "Refresh presented an access token for {ClaimedUserId} with a refresh token for {ActualUserId}.",
                claimedUserId, result.User.Id);

            await _sessions.RevokeAllAsync(result.User.Id, cancellationToken);

            return Unauthorized("Invalid client request");
        }

        var roles = await _userManager.GetRolesAsync(result.User);

        return Ok(new TokenDto
        {
            AccessToken = _tokenService.CreateAccessToken(result.User, roles),
            RefreshToken = result.RefreshToken
        });
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is not null)
        {
            await _sessions.RevokeAllAsync(userId, cancellationToken);
        }

        return NoContent();
    }

    [HttpPost("google-login")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> GoogleLogin([FromBody] string credential, CancellationToken cancellationToken)
    {
        var clientId = _configuration["Authentication:Google:ClientId"];

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return StatusCode(503, "Google sign-in is not configured on this server.");
        }

        var settings = new GoogleJsonWebSignature.ValidationSettings
        {
            Audience = new List<string> { clientId }
        };

        GoogleJsonWebSignature.Payload payload;

        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(credential, settings);
        }
        catch (InvalidJwtException)
        {
            return Unauthorized("Invalid Google credential.");
        }

        var user = await _userManager.FindByEmailAsync(payload.Email);

        if (user is null)
        {
            user = new ApplicationUser { UserName = payload.Email, Email = payload.Email, EmailConfirmed = payload.EmailVerified };

            var result = await _userManager.CreateAsync(user);

            if (!result.Succeeded)
            {
                return BadRequest(result.Errors);
            }

            await _userManager.AddToRoleAsync(user, "Reader");
        }

        return Ok(await IssueTokensAsync(user, cancellationToken));
    }

    private async Task<TokenDto> IssueTokensAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var roles = await _userManager.GetRolesAsync(user);

        return new TokenDto
        {
            AccessToken = _tokenService.CreateAccessToken(user, roles),
            RefreshToken = await _sessions.IssueAsync(
                user, RemoteIp(), Request.Headers.UserAgent.ToString(), cancellationToken)
        };
    }

    private string? RemoteIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
