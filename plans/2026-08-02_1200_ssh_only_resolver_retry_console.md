+# SSH-only Resolver retry console

## Summary

Provide the protected retry console only through an SSH tunnel. Auth0’s `resolver:admin` permission remains required, but the page and its APIs will not be reachable from the public Resolver URL.

## Key changes

- Add the admin page and protected retry APIs to `Harmony-Resolver` at `/admin/retries` and `/admin/api/*`; require a valid Auth0 access token with `resolver:admin`.
- Configure the page for local-browser Auth0 PKCE login using `http://localhost:8081/admin/` callback/logout URLs and the Resolver API audience.
- Keep the existing failed-track list, checkbox selection, and bulk “Retry now” action. Retry performs a fresh urgent downloader job, bypasses elapsed or unelapsed failure cooldowns, and publishes the normal RabbitMQ notification.
- Publish only one Resolver replica’s port on the server loopback interface, e.g. `127.0.0.1:8081:8080`. It is inaccessible from the internet.
- Explicitly block `/admin/*` at Nginx/Caddy’s public Resolver path, so neither the page nor its APIs can be reached through `https://harmony-resolver.duckdns.org/resolver/...`.
- Access workflow:

```powershell
ssh -i .\Downloads\ssh-key-2026-07-16.key -L 8081:127.0.0.1:8081 ubuntu@92.5.15.68
```

Then open `http://localhost:8081/admin/retries`, sign in through Auth0, and use the console.

- Document the required Auth0 configuration: the assigned admin role must grant `resolver:admin`, and the dedicated admin client must allow localhost callback/logout URLs above.

## Test plan

- Verify the host port binds only to `127.0.0.1`, while public `/resolver/admin/*` returns 404.
- Verify SSH-tunneled access reaches the page; direct external access cannot.
- Verify anonymous/non-admin Auth0 tokens fail, and your admin token succeeds.
- Verify list pagination, selected retry, cooldown bypass, duplicate retry idempotency, and safe skips for ready/actively leased tracks.
- Run the Resolver test suite and repository validation script.

## Assumptions

- The existing Auth0 admin role has been configured to emit the `resolver:admin` permission in Resolver-audience access tokens.
- Logout remains device-local.

