using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Blog.Posts.Infrastructure.Identity;

namespace Blog.Api.Services;

public class TokenService : ITokenService
{
    private readonly IConfiguration _configuration;

    public TokenService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string CreateAccessToken(ApplicationUser user, IList<string> roles)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName ?? string.Empty)
        };

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var creds = new SigningCredentials(SigningKey(), SecurityAlgorithms.HmacSha256);

        var minutes = _configuration.GetValue("Jwt:AccessTokenMinutes", 15);

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(minutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string? GetUserIdFromExpiredToken(string accessToken)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,

            // The whole point of a refresh call is that this token has expired.
            ValidateLifetime = false,

            ValidateIssuerSigningKey = true,
            ValidIssuer = _configuration["Jwt:Issuer"],
            ValidAudience = _configuration["Jwt:Audience"],
            IssuerSigningKey = SigningKey(),

            // Pinning the algorithm stops a token that claims "alg": "none" or a swapped
            // asymmetric algorithm from being accepted.
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 }
        };

        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(accessToken, parameters, out var securityToken);

            if (securityToken is not JwtSecurityToken jwt ||
                !jwt.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return principal.FindFirstValue(ClaimTypes.NameIdentifier);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }

    private SymmetricSecurityKey SigningKey() =>
        new(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key is not configured.")));
}
