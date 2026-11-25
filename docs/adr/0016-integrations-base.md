# ADR 0016 — Integration base layer (REST, SOAP/WSDL, signed webhooks)
Status: Accepted and implemented

## Context
FlowLedger needs a generic way to talk to other services without a bespoke connector per service. Home Assistant (ADR 0012) proved
the pattern for one webhook; this generalises it.

## Decision
A per-tenant **integration** is one of three kinds, defined in the UI (**Settings → Integrations**) or `/api/integrations`:

| Kind | What it does |
|---|---|
| REST | Sends on-demand requests to `base URL + path` (GET/POST/PUT/PATCH/DELETE/HEAD) with bearer, basic or API-key auth and static headers. |
| SOAP | Fetches a WSDL 1.1, lists operations (SOAP 1.1 and 1.2), builds the envelope and `SOAPAction`, surfaces SOAP faults as errors. Imports are not followed. |
| Webhook | Pushes `{source, version, event, event_id, occurred_at, data}` to a URL for subscribed events (`import.completed`, and `sync.completed` from both the manual and the scheduled sync; `alert.raised` is reserved and not yet dispatched). |

Webhook deliveries are signed: `X-FlowLedger-Signature: sha256=hex(HMAC-SHA256(secret, timestamp + "." + body))` with
`X-FlowLedger-Timestamp`, `X-FlowLedger-Event`, `X-FlowLedger-Delivery`. The receiver rejects stale timestamps (replay).

## Security (same model as ADR 0012)
* **SSRF**: the household owner and every other tenant use *separate* HTTP clients (separate connection pools), so a connection opened under the
  local-network policy is never reused for another tenant. Dispatch to one webhook is capped at 8 s so a hung receiver cannot stall an import or
  sync. Every URL (base, WSDL, the endpoint named *inside* a WSDL) goes through `IntegrationUrl` and the same connect-time guard
  as Home Assistant (`SocketsHttpHandler.ConnectCallback` pins the socket to a checked address). Link-local/metadata/multicast are
  refused for everyone; loopback/private/`.local` only for the household owner; everything else must be https. No redirects,
  20 s timeout, responses capped at 256 KB, REST paths cannot leave the base host.
* **Secrets are write-only**: credentials and signing secrets are AES-256-GCM encrypted (`Integrations:EncryptionKey`, falling back to
  `HomeAssistant:EncryptionKey`, which the AppHost already forwards); with no key they are refused. No read model contains them. A webhook's signing secret is shown once
  (creation or rotation).
* Auth/framing/signature headers cannot be set as custom headers. XML is parsed with DTDs prohibited and no resolver.
* The **delivery log** (last 100 per integration) stores method, host+path (never the query), status and timing, never bodies.
* Tables `integrations` / `integration_deliveries` are ad-hoc per-tenant tables (in every snapshot). Dispatch never throws into the
  action that raised the event (import, sync).

## Not done
Inbound webhook receiver, OAuth2 flows, retries/backoff queue for failed webhook deliveries, scheduled REST pulls, WSDL imports/XSD
type-driven forms, `alert.raised` events. All fit on top of the same store and transport.
