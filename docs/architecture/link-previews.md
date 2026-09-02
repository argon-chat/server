# Link previews

A link in a message gets a card — site name, title, description, picture — the way Telegram does
it. The metadata comes from **argon-crawler** (separate repository), a Bun service that answers
request/reply over NATS and caches what it finds in Redis and SQLite. This document is the contract
between the desktop client, the server and the crawler.

## The shape of it

```
composer (typing) ──GetLinkPreview(url)──▶ entrypoint ──NATS argon.crawler.crawl──▶ crawler
                                                                                    │ cache warm
send: entities += MessageEntityLinkPreview{url}                                     │
      ──SendMessage──▶ ChannelGrain ──(SendBudget)──▶ crawler ──hit──▶ card in MessageSent
                                       └──timeout──▶ message goes out with a bare stub,
                                                      ResolveLinkPreviewLaterAsync finishes it
                                                      and fires MessageUpdated(message)
```

1. **While typing** the client finds the first link in the draft (`detectLinks.ts`, debounced) and
   calls `LinkPreviewInteraction.GetLinkPreview`. The card shows above the input with an ✕; the
   user may dismiss it for this message. This lookup is what warms the crawler's cache.
2. **On send** the client appends a `MessageEntityLinkPreview` stub. Whatever the composer already
   knows about the page rides along so the optimistic message renders at once; the server
   discards all of it except the URL.
3. **The server** (`LinkPreviewEntities.TakeStub`) keeps at most one stub — the first whose URL is
   an absolute http(s) address and visibly appears in the text — and drops the rest. In a space
   channel the sender also needs `PostEmbeddedLinks`; without it the link stays and the card does
   not. The crawler is asked with `Crawler:SendBudget` (800 ms). A cache hit fills the entity
   before the insert, so `MessageSent` already carries the card.
4. **A miss** is not allowed to hold the message. If the crawler is still on the page (the request
   *timed out*, as opposed to nobody answering), the message is stored with the bare stub and
   `ResolveLinkPreviewLaterAsync` waits the full `Crawler:Timeout`, rewrites the entities row and
   fires `MessageUpdated(spaceId, channelId, message)` — channel-scoped, whole message. A page
   that yields nothing has its stub removed, also through `MessageUpdated`.
5. **Clients** render the card only when the entity has a title, description or image; a bare
   stub renders nothing. `MessageUpdated` replaces the message in place (`_rev` bumps the row's
   memo key).

Direct messages take steps 1–3 only: `UserChatGrain` settles the stub synchronously and drops it
on a miss. There is no `DirectMessageUpdated` yet.

## Why the URL is the only thing a client may say

A card the sender could write is a phishing kit: any title, any picture, pointing anywhere. So the
server owns every field but `url`, requires the URL to be in the message text, and refuses
credentials in it (`user:secret@host`). Images are the crawler's re-hosted S3 copy or nothing —
the page's own image would make every reader fetch from the linked site and show it their address.
`Crawler:AllowExternalImages` turns that on for development, where the crawler runs without S3.

## What runs where

| Piece | Role | Notes |
|---|---|---|
| `LinkPreviewFeature` (`Crawler` section) | core, entrypoint | NATS itself is on every role already |
| `ILinkPreviewInteraction` | entrypoint | per-user limiter, `Crawler:PreviewRequestsPerMinute`, per node |
| `ChannelGrain` / `UserChatGrain` | core | stub settlement, deferred resolution (channels only) |
| `NatsCrawlerService` | both | JSON over `argon.crawler.crawl`; circuit opens after `CircuitFailureThreshold` unanswered requests |

A crawler that is **down** (no responders, connection refused) fails at once and, after a few
in a row, opens the circuit for `CircuitOpenFor`; a crawler that is **slow** (timeout) is treated
as a crawl in progress. That distinction is what keeps a deployment without the crawler from
paying `SendBudget` per link: set `Crawler:Enabled=false` there and nothing is even tried.

Configuration lives in `deploy/pconf.d/link-preview.json`; every knob is documented on
`CrawlerOptions`. The crawler's own settings (user agent, robots handling, S3 re-hosting, TTLs)
are its repository's business.

## Wire additions

All appended, never inserted (`ion.lock.json` guards the indices):

- `MessageEntity.MessageEntityLinkPreview(url, title?, description?, siteName?, imageUrl?, canonicalUrl?)` — case 22, `EntityType.LinkPreview = 22`
- `ArgonEvent.MessageUpdated(spaceId, channelId, message: ArgonMessage)` — case 64
- `LinkPreviewInteraction.GetLinkPreview(url): LinkPreviewResult` — `LinkPreviewReady(preview) | LinkPreviewFailed(error)`

Bot API: `BotEntityType.LinkPreview = 22`, with `Url/Title/Description/SiteName/ImageUrl/CanonicalUrl`
on `BotMessageEntityV1`. `MessageUpdated` is not forwarded to bots.

## Not done yet

- Direct messages have no deferred path; a cache miss at send means no card.
- Bots cannot ask for a preview (Telegram's `link_preview_options`); their messages carry none.
- `docs/bot-api-docs` manifest does not list the new entity type.
- No metrics on either side yet (`crawler.*` on the crawler side exists; nothing for hit/miss/pending on the server).
