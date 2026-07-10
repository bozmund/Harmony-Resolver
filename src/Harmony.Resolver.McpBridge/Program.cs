using System.Net.Http.Headers;
using System.Net.Http.Json;

var endpoint = Environment.GetEnvironmentVariable("HARMONY_MCP_URL") ?? throw new InvalidOperationException("HARMONY_MCP_URL is required");
var domain = Environment.GetEnvironmentVariable("AUTH0_DOMAIN") ?? throw new InvalidOperationException("AUTH0_DOMAIN is required");
var id = Environment.GetEnvironmentVariable("AUTH0_CLIENT_ID") ?? throw new InvalidOperationException("AUTH0_CLIENT_ID is required");
var secret = Environment.GetEnvironmentVariable("AUTH0_CLIENT_SECRET") ?? throw new InvalidOperationException("AUTH0_CLIENT_SECRET is required");
var audience = Environment.GetEnvironmentVariable("AUTH0_AUDIENCE") ?? "https://harmony-resolver-diagnostics";
using var client = new HttpClient();
var tokenResponse = await client.PostAsJsonAsync($"https://{domain.TrimEnd('/')}/oauth/token", new { client_id = id, client_secret = secret, audience, grant_type = "client_credentials" });
tokenResponse.EnsureSuccessStatusCode();
var token = await tokenResponse.Content.ReadFromJsonAsync<Token>() ?? throw new InvalidOperationException("Auth0 returned no token");
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
Console.Error.WriteLine($"Authenticated MCP bridge ready for {new Uri(endpoint).Host}; configure Streamable HTTP clients with the remote endpoint.");

internal sealed record Token(string AccessToken, int ExpiresIn);
