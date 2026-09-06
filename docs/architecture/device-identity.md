# Sessions, devices and application ids

How the server knows *what* is connected: which application, on which machine, from where — and
what a session is bound to so that copying a client's data folder to another computer does not
carry the account with it.

Client counterparts: `src/Plugins/Argon.Security` (the native plugin, `libsec`) and
`src/ArgonDesktopModern/client/src/lib/net/clientDescriptor.ts` in the desktop repository.

## The three identifiers

| What | Where it travels | Who writes it | Trusted for |
|---|---|---|---|
| **Machine id** (`colt`) | `ArgonSecure` cookie | native plugin (desktop), server (web) | token binding: hashed into the `mh` claim of every token |
| **Session id** (`scid`) | `ArgonSecure` cookie | native plugin (per launch), server (web) | presence, the devices screen, revocation |
| **Application id** (`ner`) | `ArgonSecure` cookie | native plugin (constant), server (web: the OAuth `client_id`) | naming the session; nothing else |

`ArgonSecure` is a query string: `hwid=…&scid=…&colt=…&ner=…&hwv=…[&dev=…]`. `HttpContextExtensions`
reads it; `ArgonTransactionInterceptor` puts the result into `ArgonRequestContextData`, and
`ArgonOrleansInterceptor` copies what grains need into Orleans' `RequestContext` (`$caller_*`).

### Application ids are a registry

`auth:clientApps` (`ClientAppsOptions`) maps an id to a name and a kind. Two ids ship as defaults:
the desktop client `875180ED6396874C0536D95B30BB7B47` (baked into the `sec` plugin as
`SecurityExports.AppId`, written into `ner`) and the web client
`A37E7A1DB06E9610C9C0BD77C61A821B` (the OAuth `client_id` in `webAuth.ts`, also the value
`WebSession:TrustedAudiences` files web sessions under). The validator warns when the two disagree.

Desktop builds before September 2026 wrote the web id into their cookie. `ClientAppsOptions.Find(appId,
client)` still tells them apart — a Web entry presented by a client that is not a browser and names an
Argon version is read as the desktop application — and the rule can go once those builds are gone.

The kind (`Desktop`, `Web`, `Mobile`, `Bot`) decides how the client's self-description is read: a
desktop id plus `platform=macos` is a Mac; a web id is a browser whatever the OS.

## Two ids per session

A session has **two** ids, in different id spaces, and every gate in the product depends on telling
them apart. The **presence sid** is the `scid` above: the client writes it, an installed client mints
a fresh one on every launch, and it names the row on the devices screen, the presence keys and the
session grain. It is a *label* — a gate keyed on it alone is escaped by sending a different one,
which is exactly how a signed-out device used to walk back in. The **credential sid** is the `sid`
claim `ClassicJwtFlow` writes into the refresh token and, since the same claim rides the access token
minted from it, into every request that presents one. The server mints it, it is inside a signature,
and a caller can neither choose it nor omit it while still being served.

So a revocation is written under **both** and every gate tests both.
`SecurityGrain.EndSessionAsync` tombstones the presence sid the user pressed the button on plus every
credential sid recorded against it (`session:credentials:{u}:{sid}`, written at sign-in and at every
refresh, kept for a month); `ArgonTransactionInterceptor` tests the cookie's sid and the token's
`sid` on every Ion call; the hub ticket carries one `csid` claim per credential the device is known
to hold and `AppHub` tests all of them. And because neither id reaches a credential that was never
registered under either — which is what a password change is aimed at — there is a third handle:
`session:floor:{u}`, an issued-at watermark that kills everything minted at or before it.
`ChangePassword` writes it, and the refresh path, the hub, the session grain and the Ion interceptor
all compare it against the mint time of whatever the caller presented. An access token carries no
`iat` (the token library writes one only when it is given an issued-at), so on that path the
comparison is against `nbf`, which is the same instant. `RevokeAllSessions` deliberately writes no
floor: a watermark cannot spare the caller's own token, and signing the user out of the screen they
are standing on is not what that button means.

## What a client says about itself

First-party clients send `X-Argon-Client` on every request:

```
platform=windows; os=Windows%2011%20Pro; osv=10.0.26100; app=1.4.0; device=DESKTOP-7F2; arch=x64
```

`ClientDescriptor.From(header, userAgent)` parses it and fills anything missing from the
User-Agent — browsers send no header, so for them everything comes from the UA (family, browser
name, an `ArgonChat-Android/…` version). Values are trimmed, stripped of control characters and
capped. **Display-only**: the descriptor names a session on the devices screen and sets the
coarse `DeviceTypeKind` on the device-history row. Nothing is authorised or matched on it.

## Where the request came from

`GetGeoLocation()` reads, in order, Cloudflare (`cf-ipcountry`, `cf-region`/`cf-region-code`,
`cf-ipcity`), Traefik's geoip2 plugin (`X-GeoIP2-Country`/`-Region`/`-City`) and a bare
`X-Country`, only when the request arrived through a trusted proxy. Both edges write `XX` when they
know nothing; `GeoLocation.Of` folds every such placeholder into unknown.

`GetRegion()` still returns the ISO country or `"00"` — the CDN router and the registration validator
key on that — and is now just `GetGeoLocation().Country`.

Region and city need a *City* database in front of Traefik (`GeoLite2-City`; the plugin picks the
reader by the file name) and, behind Cloudflare, the "add visitor location headers" managed transform.

## Describing a session

`IUserPresenceService.TouchSessionMetaAsync(userId, sid, UserSessionMeta)` writes one record per
session: client string, country, city, IP, application id and name, platform, OS, app version,
device name, started-at. It is written from `EventBusImpl.PickTicket` — the one call every realtime
connection makes with the whole HTTP request in hand — and from the ion ticket exchange. It is
written **once**: a reconnect finds the record and only extends its lifetime, which keeps
`StartedAt` honest. `HeartbeatAsync` moves `LastSeenAt`.

`SecurityGrain.GetSessionsAsync` turns those into `SessionInfo` for the devices screen; the
registry name wins over the stored one so renaming an application renames sessions already on
screen. The same moment writes the device-history row (`IUserGrain.UpdateUserDeviceHistory`),
which is why the session grain no longer does — it is reached through the hub, whose context has
ids and nothing else, and every row it wrote said "unknown".

## Binding a session to the machine

Every token carries `mh = HMAC(machineId)`, checked against the `colt` the request presents. That
stops a token being used with a *different* cookie, but the cookie is a bearer value too: copy the
folder, copy the cookie, and the check passes.

Desktop Windows closes that with a key that never leaves the TPM (`TpmDeviceKey`, `Microsoft
Platform Crypto Provider`, no software fallback). The client signs `{issuedAt}|{machineId}` and
sends `publicKey.issuedAt.signature` as the **`Sec-Proof` header** on the calls that mint or refresh
a session — `Authorize`, `Registration`, `ResetPassword`, `CreateLoginRequest`, `GetMyAuthorization`.
The cookie's `dev` field is the same proof made at launch and is only a fallback for older builds:
`DeviceProofVerifier` accepts a proof for one minute and once, so a launch-time proof is useless for
every refresh after the first.

* At sign-in, `IdentityInteraction.BindProvenDeviceAsync` verifies the proof and puts its thumbprint
  in the request context (`CallerContext.DeviceThumbprintKey`); `ArgonAuthorizationService` reads it
  and `UserManagerService.GenerateJwt` writes it into the refresh token as `cnf`. QR sign-in does
  the same through `QrLoginRecord.DeviceThumbprint`.
* At refresh, `GetMyAuthorization` requires a bound token to present a verifying proof **from the
  key the token names** (`cnf` compared to the presented key's thumbprint in constant time). A proof
  from another machine's TPM is a valid proof of the wrong thing and is refused. The resolved device
  then rides the access token as `did`, which is what makes a hardware ban enforceable per request.
* A machine with no usable TPM, a browser, an older build: no proof, no `cnf`, the session it always
  had. Refusing them would refuse every Mac and Linux desktop today. macOS needs a Secure Enclave key
  in the plugin before it can be bound the same way; it is not written yet.

Existing sessions stay unbound until their next sign-in — binding is decided when the refresh
token is minted and `GetMyAuthorization` only re-issues access tokens.

## Deploying the presence release

**The presence/session release needs a coordinated restart of `entrypoint` and `core`, not a rolling
one.** Orleans generates an invokable per grain interface method and the response shape is part of
that contract, so two silos on different builds do not agree about it. This release changes
`IUserSessionGrain.AttachConnectionAsync` from `ValueTask` to `ValueTask<bool>` (the hub has to be
able to abort a connection the grain refused) and gives `HeartBeatAsync` and `TouchAsync` answers of
their own, and it adds a seeded overload of `IUserGrain.AggregateAndBroadcastStatusAsync` that
`SpaceGrain` calls on every join. During a rolling deploy the hub lives on `entrypoint` and those
grains live on `core`, so a mismatched pair faults on response deserialization: `OnConnectedAsync`
throws and every client that lands on it fails to connect, and a join announces nobody's status.

Drain and restart both roles together, or accept a connect outage for the length of the rollout.
Whatever gates the rollout has to know this — it is the one change in the release that a canary does
not cover, because the half that breaks is the *old* node.

## Configuration

* `auth:clientApps` — the application registry (`deploy/pconf.d/argon-authorization.json`).
* `auth:deviceMatching` — the fingerprint weights, for machines without a key.
* `ForwardedHeaders:KnownNetworks` — which hops may set the geo and address headers at all.
