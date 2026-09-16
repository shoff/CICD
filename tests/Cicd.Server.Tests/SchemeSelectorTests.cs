using Cicd.Server.Security;
using Microsoft.AspNetCore.Http;

namespace Cicd.Server.Tests;

public class SchemeSelectorTests
{
    private const string Jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln";

    private static HttpRequest Request(Action<HttpRequest>? configure = null)
    {
        var request = new DefaultHttpContext().Request;
        configure?.Invoke(request);
        return request;
    }

    [Fact]
    public void No_credential_is_a_cookie_request() =>
        Assert.Equal("Cookies", SchemeSelector.Select(Request(), oidcConfigured: true));

    [Fact]
    public void Jwt_bearer_goes_to_the_idp_validator() =>
        Assert.Equal("Bearer", SchemeSelector.Select(Request(r => r.Headers.Authorization = $"Bearer {Jwt}"), oidcConfigured: true));

    [Fact]
    public void Opaque_bearer_is_a_static_token() =>
        Assert.Equal("Token", SchemeSelector.Select(Request(r => r.Headers.Authorization = "Bearer dev-token"), oidcConfigured: true));

    [Fact]
    public void Api_key_header_is_a_static_token() =>
        Assert.Equal("Token", SchemeSelector.Select(Request(r => r.Headers["X-Api-Key"] = "dev-token"), oidcConfigured: true));

    [Fact]
    public void Hub_query_token_follows_its_shape()
    {
        Assert.Equal("Bearer", SchemeSelector.Select(Request(r => { r.Path = "/hubs/builds"; r.QueryString = new QueryString($"?access_token={Jwt}"); }), oidcConfigured: true));
        Assert.Equal("Token", SchemeSelector.Select(Request(r => { r.Path = "/hubs/agents"; r.QueryString = new QueryString("?access_token=dev"); }), oidcConfigured: true));
    }

    [Fact]
    public void Query_token_outside_hubs_is_ignored() =>
        Assert.Equal("Cookies", SchemeSelector.Select(Request(r => { r.Path = "/api/v1/builds"; r.QueryString = new QueryString("?access_token=dev"); }), oidcConfigured: true));

    [Fact]
    public void Jwt_without_oidc_falls_back_to_the_static_token_handler() =>
        Assert.Equal("Token", SchemeSelector.Select(Request(r => r.Headers.Authorization = $"Bearer {Jwt}"), oidcConfigured: false));

    [Theory]
    [InlineData(Jwt, true)]
    [InlineData("dev", false)]
    [InlineData("a.b", false)]
    [InlineData("a..c", false)]
    [InlineData("a.b.c.d", false)]
    [InlineData("a b.c.d", false)]
    public void Jwt_shape(string token, bool expected) => Assert.Equal(expected, SchemeSelector.LooksLikeJwt(token));
}
