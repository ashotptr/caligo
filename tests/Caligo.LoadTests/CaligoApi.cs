using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Caligo.LoadTests;

public sealed class CaligoApi : IDisposable
{
    private readonly LoadTestSettings _settings;

    public CaligoApi(LoadTestSettings settings)
    {
        _settings = settings;

        Http = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 1024,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            BaseAddress = new Uri(settings.SiteUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public HttpClient Http { get; }

    public async Task<string> LoginAdminAsync()
    {
        var response = await Http.PostAsJsonAsync(
            "/api/Account/login", new { Username = _settings.AdminUser, Password = _settings.AdminPassword });

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.GetProperty("accessToken").GetString()!;
    }

    public async Task<int> CreatePostAsync(string token, string title)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/BlogPosts")
        {
            Content = JsonContent.Create(new { Title = title, Content = "Written by the load tests.", Tags = new[] { "loadtest" } })
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await Http.SendAsync(request);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    public async Task<JsonElement> GetJsonAsync(string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await Http.SendAsync(request);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public string MintToken(string userId, params string[] roles)
    {
        var now = DateTimeOffset.UtcNow;

        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));

        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = userId,
            ["nameid"] = userId,
            ["role"] = roles,
            ["iss"] = _settings.JwtIssuer,
            ["aud"] = _settings.JwtAudience,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["exp"] = now.AddHours(2).ToUnixTimeSeconds()
        }));

        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_settings.JwtKey), Encoding.ASCII.GetBytes($"{header}.{payload}"));

        return $"{header}.{payload}.{Encode(signature)}";
    }

    public void Dispose() => Http.Dispose();

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
