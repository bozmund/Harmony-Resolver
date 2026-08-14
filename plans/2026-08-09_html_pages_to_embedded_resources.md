# Move the two served HTML pages out of C# string literals into embedded `.html` files

## Context

Both .NET backends serve a complete HTML document from a raw string literal in a `.cs` file. Inside a
string literal the markup gets no syntax highlighting, no formatter, no HTML or CSS validation, and no
JavaScript tooling — and the Resolver's page carries a ~55-line script that badly wants all four.

There are exactly two, and no other HTML anywhere across the four repos:

| Page | Location | Size | Dynamic parts |
|---|---|---|---|
| Windows sign-in landing page | [`WindowsAuthCallbackPage.Render()`](src/Harmony.Cloud.Api/Domain/WindowsAuthCallbackPage.cs) (Harmony-Cloud) | ~30 lines | Redirect URL, twice (HTML-escaped, JS-escaped) |
| Resolver admin console | `AdminConsolePage.Html` in [`AdminRetryEndpoints.cs:106-195`](src/Harmony.Resolver.Api/Endpoints/AdminRetryEndpoints.cs) (Harmony-Resolver) | ~85 lines | **None** — a pure `const` |

Neither repo has any static-file or templating infrastructure: no `wwwroot`, no `UseStaticFiles`, no
Razor, no `<EmbeddedResource>` anywhere. Both publish framework-dependent into `mcr.microsoft.com/dotnet/aspnet:10.0`
with no trimming, AOT, or single-file, so a loose file *would* survive to runtime — but nothing opts
one in today.

**Chosen mechanism: embedded resource.** A real `.html` file compiled into the assembly, read once
into a cached static, with `string.Replace` for the two substitutions the Cloud page needs.

Razor was rejected: `AddRazorPages`/`MapRazorPages` plus view compilation is a lot of framework to add
to two minimal-API services for two documents. `wwwroot` + `UseStaticFiles` was rejected for the
callback page specifically: it needs a **server-side** substitution and, more importantly, server-side
scheme validation. Serving it as a plain static asset would push the allowlist into client-side
JavaScript and weaken the open-redirect guard that stops a crafted link launching an arbitrary
protocol handler. An embedded resource also cannot go missing at runtime — relevant because both
containers run non-root as `$APP_UID` with read-only access.

Intended outcome: the markup lives in `.html` files with full editor support, and behaviour is
byte-for-byte unchanged.

---

## Harmony-Cloud

**New file** `src/Harmony.Cloud.Api/Domain/WindowsAuthCallbackPage.html` — the current markup verbatim,
with the two interpolation holes replaced by literal placeholders:

- `{{REDIRECT_HREF}}` where `{{attribute}}` is today (the `meta http-equiv="refresh"` URL and the
  `<a href>`), and
- `{{REDIRECT_JS}}` inside `location.replace('…')`.

Both sit in positions where they parse as ordinary text, so the file stays valid, lintable HTML.

**`Harmony.Cloud.Api.csproj`** — add an `<EmbeddedResource>` with an explicit `LogicalName`, so the
resource key does not silently change if the file is ever moved between folders:

```xml
<ItemGroup>
  <EmbeddedResource Include="Domain\WindowsAuthCallbackPage.html" LogicalName="WindowsAuthCallbackPage.html" />
</ItemGroup>
```

> If the build reports duplicate items (`NETSDK1022`), the Web SDK is already globbing the file in as
> `Content`; add `<Content Remove="Domain\WindowsAuthCallbackPage.html" />` in the same `ItemGroup`.

**`WindowsAuthCallbackPage.cs`** — delete the literal. Add a private cached template loaded from the
manifest stream, and reduce `Render` to the two escapes it already computes plus two `Replace` calls:

- `private static readonly string Template = LoadTemplate();`
- `LoadTemplate()` throws `InvalidOperationException` naming the resource when the stream is null, so a
  fumbled csproj entry fails loudly rather than serving an empty page.
- **`Render(string redirect)` keeps its exact signature and output**, so every existing test in
  `WindowsAuthCallbackPageTests.cs` continues to apply unchanged — that is the regression net for this
  refactor.
- The escaping stays where it is: `WebUtility.HtmlEncode` for the attribute hole, the existing
  backslash/quote/`<` escaping for the script hole. Do not "simplify" these into one value.

`BuildRedirect`, the scheme allowlist, and the route in `Program.cs` are untouched.

---

## Harmony-Resolver

**New file** `src/Harmony.Resolver.Api/Endpoints/AdminConsolePage.html` — the current markup verbatim.
No placeholders: this page has no dynamic parts, so it moves across unmodified.

**`Harmony.Resolver.Api.csproj`** — same `<EmbeddedResource>` pattern, `LogicalName="AdminConsolePage.html"`.

**`AdminRetryEndpoints.cs`** — replace `internal const string Html = """…"""` with a
`internal static readonly string Html` (or a get-only property) backed by the same manifest-stream
loader. `const` → `static readonly` is safe: [line 26](src/Harmony.Resolver.Api/Endpoints/AdminRetryEndpoints.cs:26)
is the only reference in the repo, and it is a plain method argument, not an attribute or `case` label.

Keep the existing class comment explaining why the console is served same-origin by the Resolver, and
keep the note that the Auth0 SPA client is pinned and carries no credentials.

Worth knowing but **not** in scope: this page loads `https://cdn.auth0.com/js/auth0-spa-js/2.1/…` from
a CDN, so unlike the Cloud page it is not self-contained. Moving it into a file changes nothing about
that either way.

---

## Tests

**Harmony-Cloud** — `tests/Harmony.Cloud.UnitTests/WindowsAuthCallbackPageTests.cs`. The 23 existing
assertions already exercise `Render` end-to-end and will fail if the resource is missing or malformed,
which is most of the coverage this refactor needs. Add one:

- **No placeholder survives rendering.** Assert the output contains neither `{{REDIRECT_HREF}}` nor
  `{{REDIRECT_JS}}` nor a bare `{{`. A renamed placeholder in the `.html` that nobody updated in the
  `.cs` would otherwise ship a page with a literal `{{REDIRECT_JS}}` in its `location.replace`, which
  no current assertion catches.

**Harmony-Resolver** — new `tests/Harmony.Resolver.UnitTests/AdminConsolePageTests.cs`. There is **no
test for this page at all today**, so without one the embedded-resource move would be entirely
unverified. `InternalsVisibleTo("Harmony.Resolver.UnitTests")` already exists in
`src/Harmony.Resolver.Api/Properties/AssemblyInfo.cs`, so the internal class is reachable. Assert:

- The resource loads: `Html` is non-empty and starts with `<!doctype html`.
- The markup the endpoints depend on is intact — the `#login`/`#retry`/`#more` element ids and the
  `/admin/api/failed` and `/admin/api/retries` paths the script calls. These are the couplings between
  the page and the API that a careless edit to a now-separate file could break silently.

---

## Verification

1. **Build both** — `Directory.Build.props` sets `TreatWarningsAsErrors=true` in both repos, so this is
   a real gate:
   ```bash
   dotnet build C:/MyRepositories/Harmony-Cloud/Harmony.Cloud.slnx
   ```
   ```bash
   dotnet build C:/MyRepositories/Harmony-Resolver
   ```

2. **Unit tests** (these are what actually prove the resources load):
   ```bash
   dotnet test C:/MyRepositories/Harmony-Cloud/tests/Harmony.Cloud.UnitTests/Harmony.Cloud.UnitTests.csproj
   ```
   ```bash
   dotnet test C:/MyRepositories/Harmony-Resolver/tests/Harmony.Resolver.UnitTests
   ```

3. **Integration tests — I cannot run these here.** Both suites use Testcontainers for Postgres and
   Docker is not available on this machine; I will compile them and say so rather than imply they
   passed. Run them where Docker is available if you want the route-level coverage
   (`WindowsAuthCallbackEndpointTests` asserts the callback page is reachable without a bearer token).

4. **Confirm the published output actually contains the resources** — the failure mode this refactor
   introduces is a csproj entry that builds fine and 500s in production. After `dotnet publish`, the
   `.html` files should be *inside* the DLL, not beside it:
   ```bash
   dotnet publish C:/MyRepositories/Harmony-Cloud/src/Harmony.Cloud.Api/Harmony.Cloud.Api.csproj -c Release -o /tmp/cloudpub
   ```
   The unit tests in step 2 cover this transitively, since they load through the same manifest stream.

5. **Manual, after deploying** — unchanged from before, and the page should be byte-identical to what
   it renders today:
   ```bash
   curl -s "https://harmony-resolver.duckdns.org/cloud/auth/windows/callback/harmonymusic-dev?code=test" | head -20
   ```
   For the Resolver console, reach `/admin/retries` through the SSH forward as usual — production
   ingress still 404s `/admin`, so nothing about its exposure changes.
