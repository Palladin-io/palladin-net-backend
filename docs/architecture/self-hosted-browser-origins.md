# Self-hosted browser origins

`Networking:AllowedOrigins` is the administrator-owned CORS allowlist. Entries
must be exact canonical HTTP/HTTPS origins (scheme, host and port), without paths,
credentials, queries, fragments or wildcards. Generic runtime defaults are empty;
Development/Testing explicitly list their local or synthetic clients. CORS never
replaces endpoint authentication or the client-side browser/MK binding.

Configure `AllowedHosts` for the API host and, for example,
`Networking__AllowedOrigins__0=https://panel.example.test` in the deployment
environment. API and panel path prefixes are not CORS origins. A self-hosted HTTP
API additionally requires administrator opt-in `Networking__AllowInsecureHttp=true`
outside Development/Testing; HTTPS enforcement remains the default. This setting
skips API HTTPS redirection/HSTS only. It does not change client URL selection,
trust an invalid certificate or override the extension's separate user consent
for its exact API/panel pair. Existing HSTS/browser policies can still prevent HTTP.

Before deploying this change to staging, populate `Networking__AllowedOrigins__0`
in its approved runtime environment parameter with the staging panel origin.
The deployment preflight now requires that key. This code change does not modify
SSM values. Keep `AllowInsecureHttp` false on hosted staging/production and retain
the existing trusted-proxy configuration. Review any self-hosted HTTP exposure
explicitly: network attackers can replace the panel and observe its unlocked data.

The browser connection/crypto rollout is separate. Remote HTTP requires portable
client crypto and safe cross-tab state serialization; backend acceptance alone is
not evidence of a working or authenticated browser handoff.
