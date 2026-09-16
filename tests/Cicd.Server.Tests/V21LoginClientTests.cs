using System.Net;
using System.Text;
using System.Text.Json;
using Cicd.Server.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cicd.Server.Tests;

public class V21LoginClientTests
{
    private const string Authority = "https://idp.example";
    private const string DerivedLoginUrl = "https://idp.example/api/v21/accountv21/login";
    private const string CallbackUrl = "http://localhost:7003/callback.html";

    /// <summary>Records the request (body included) and returns a canned response, or throws the given transport error.</summary>
    private sealed class FakeHandler(HttpStatusCode status, string body, Exception? throws = null) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            if (throws is not null)
            {
                throw throws;
            }
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static string Jwt(object payload)
    {
        static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{header}.{body}.c2ln";
    }

    private static V21LoginClient Client(FakeHandler handler, Action<IdentityProviderOptions>? configure = null)
    {
        var options = new IdentityProviderOptions { Authority = Authority, ReturnUrl = CallbackUrl };
        configure?.Invoke(options);
        return new V21LoginClient(new HttpClient(handler), Options.Create(options), NullLogger<V21LoginClient>.Instance);
    }

    private static FakeHandler TokenHandler(object payload, string property = "access_token") =>
        new(HttpStatusCode.OK, JsonSerializer.Serialize(new Dictionary<string, string> { [property] = Jwt(payload) }));

    [Fact]
    public async Task Posts_pascal_case_credentials_to_the_derived_login_url()
    {
        var handler = TokenHandler(new { sub = "42" });
        var result = await Client(handler).LoginAsync("asmith", "s3cret", CancellationToken.None);

        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(DerivedLoginUrl, handler.LastRequest.RequestUri!.ToString());

        using var body = JsonDocument.Parse(handler.LastBody!);
        var properties = body.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["UserName", "Password", "ReturnUrl"], properties);
        Assert.Equal("asmith", body.RootElement.GetProperty("UserName").GetString());
        Assert.Equal("s3cret", body.RootElement.GetProperty("Password").GetString());
        Assert.Equal(CallbackUrl, body.RootElement.GetProperty("ReturnUrl").GetString());
    }

    [Theory]
    [InlineData("access_token")]
    [InlineData("accessToken")]
    [InlineData("AccessToken")]
    [InlineData("token")]
    [InlineData("Token")]
    public async Task Reads_the_token_from_any_known_property(string property)
    {
        var handler = TokenHandler(new { sub = "42" }, property);
        var result = await Client(handler).LoginAsync("asmith", "s3cret", CancellationToken.None);

        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.Equal("42", result.Identity!.Subject);
    }

    [Fact]
    public async Task Maps_the_identity_claims_from_the_token()
    {
        var handler = TokenHandler(new
        {
            sub = "42",
            iss = "https://idp.example",
            name = "Alice Smith",
            unique_name = "asmith",
            email = "a@example.com",
        });
        var identity = (await Client(handler).LoginAsync("asmith", "s3cret", CancellationToken.None)).Identity;

        Assert.NotNull(identity);
        Assert.Equal("https://idp.example", identity!.Issuer);
        Assert.Equal("42", identity.Subject);
        Assert.Equal("asmith", identity.Username);
        Assert.Equal("a@example.com", identity.Email);
        Assert.Equal("Alice Smith", identity.DisplayName);
    }

    [Fact]
    public async Task Falls_back_to_the_configured_authority_when_the_token_has_no_issuer()
    {
        var handler = TokenHandler(new { sub = "42" });
        var identity = (await Client(handler, o => o.Authority = "https://idp.example/")
            .LoginAsync("asmith", "s3cret", CancellationToken.None)).Identity;

        Assert.NotNull(identity);
        Assert.Equal("https://idp.example", identity!.Issuer);
    }

    [Fact]
    public async Task Username_claim_wins_over_unique_name()
    {
        var handler = TokenHandler(new { sub = "42", username = "alice", unique_name = "asmith" });
        var identity = (await Client(handler).LoginAsync("alice", "s3cret", CancellationToken.None)).Identity;

        Assert.NotNull(identity);
        Assert.Equal("alice", identity!.Username);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Rejected_credentials_are_invalid(HttpStatusCode status)
    {
        var handler = new FakeHandler(status, """{"error":"invalid_grant"}""");
        var result = await Client(handler).LoginAsync("asmith", "wrong", CancellationToken.None);

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task Success_without_a_token_is_unavailable() =>
        Assert.Equal(LoginOutcome.ProviderUnavailable,
            (await Client(new FakeHandler(HttpStatusCode.OK, """{"refresh_token":"r"}""")).LoginAsync("asmith", "s3cret", CancellationToken.None)).Outcome);

    [Fact]
    public async Task Token_without_a_subject_is_unavailable() =>
        Assert.Equal(LoginOutcome.ProviderUnavailable,
            (await Client(TokenHandler(new { name = "Alice Smith" })).LoginAsync("asmith", "s3cret", CancellationToken.None)).Outcome);

    [Fact]
    public async Task Transport_failure_is_unavailable()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "", new HttpRequestException("connection refused"));
        var result = await Client(handler).LoginAsync("asmith", "s3cret", CancellationToken.None);

        Assert.Equal(LoginOutcome.ProviderUnavailable, result.Outcome);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task Timed_out_call_is_unavailable()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "", new TaskCanceledException("timed out"));
        Assert.Equal(LoginOutcome.ProviderUnavailable,
            (await Client(handler).LoginAsync("asmith", "s3cret", CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Opaque_reference_token_is_unavailable()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"access_token":"opaque-reference-token"}""");
        var result = await Client(handler).LoginAsync("asmith", "s3cret", CancellationToken.None);

        Assert.Equal(LoginOutcome.ProviderUnavailable, result.Outcome);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task Password_whitespace_is_sent_unaltered()
    {
        var handler = TokenHandler(new { sub = "42" });
        await Client(handler).LoginAsync("asmith", "  pad ded  ", CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("  pad ded  ", body.RootElement.GetProperty("Password").GetString());
    }

    [Fact]
    public async Task Plain_http_login_url_is_refused_when_https_is_required()
    {
        var handler = TokenHandler(new { sub = "42" });
        var client = Client(handler, o => o.LoginUrl = "http://idp.example/login");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.LoginAsync("asmith", "s3cret", CancellationToken.None));
        Assert.Contains("https", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Explicit_login_url_is_used_verbatim()
    {
        var handler = TokenHandler(new { sub = "42" });
        var result = await Client(handler, o => o.LoginUrl = "https://idp.example/custom/v21/login")
            .LoginAsync("asmith", "s3cret", CancellationToken.None);

        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.Equal("https://idp.example/custom/v21/login", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public void FromToken_returns_null_without_a_subject_and_maps_full_name()
    {
        Assert.Null(V21LoginClient.FromToken(Jwt(new { name = "Alice" }), Authority));

        var identity = V21LoginClient.FromToken(Jwt(new { sub = "42", full_name = "Alice Smith" }), Authority);
        Assert.NotNull(identity);
        Assert.Equal("Alice Smith", identity!.DisplayName);
        Assert.Equal(Authority, identity.Issuer);
    }
}
