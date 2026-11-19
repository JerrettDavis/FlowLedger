# ADR 0012 — Home Assistant alerts (push via webhook, alerts not numbers)
Status: Accepted and implemented

## Context
Households want FlowLedger's heads-ups (a bill coming due, a paycheck landing, an overdraft) on their smart-home
dashboards and phones through Home Assistant (HA). Financial data is sensitive, and HA often has more integrations, add-ons
and users than FlowLedger, so sharing must be narrow by default.

## Decision
FlowLedger **pushes** events to an HA **webhook trigger** (`POST {ha}/api/webhook/<id>`), rather than HA polling FlowLedger or
FlowLedger holding an HA long-lived token.

* A webhook id can only fire the one automation it belongs to. A long-lived access token acts as a full HA user, so a leaked
  FlowLedger database would expose far more. Push also needs no inbound access to FlowLedger and no FlowLedger credential in HA.
* Nothing is sent until the household enables it AND saves a webhook URL.

## What is shared
| Kind | When |
|---|---|
| `bill_due_soon` / `paycheck_expected` | the next scheduled date of an active recurring flow is within `LeadDays` (default 3) and not yet paid |
| `bill_paid` / `paycheck_received` | a recent transaction matches the flow's name/counterparty (same direction) within 5 days of a scheduled date |
| `overdraft` | a checking, savings or cash account has a negative balance |
| `low_balance_forecast` | the 7-day forecast dips below zero while nothing is overdrawn yet |
| `budget_alert` | a monthly budget is >= 80% used or over |

**Default payload carries no numbers**: `source, version, kind, severity, title, message, event_id` plus (if allowed)
`subject, date, due_in_days`. `amount`/`balance` exist only when the household turns on *Also share amounts and balances*
(per household, off by default). Names (`subject`, and in the message) can be withheld with *Include names*.
Disabled and hidden accounts are never used (ADR 0011).

## Security
* The webhook URL is **write-only**: no endpoint returns it (the UI shows only the host); it is validated on save and on every
  send (`http(s)`, path `/api/webhook/<id>` with an id of 16+ chars, no credentials/query/fragment, **https required unless on the
  local network**, link-local/cloud-metadata addresses refused).
* Encrypted at rest with AES-256-GCM when `HomeAssistant:EncryptionKey` (base64, 32 bytes) is set; without a key it is stored as-is
  (still excluded from every API response and log). A value that cannot be decrypted fails closed (no send).
* The HTTP client does not follow redirects, times out after 10 s, and errors are reduced to short fixed strings (never the URL or
  the response body).
* Settings and the "already sent" ledger live in per-tenant ad-hoc tables (`home_assistant_settings`, `home_assistant_sent`), so they
  are tenant-isolated and part of every backup. The ledger holds opaque dedupe keys, never amounts.

## Delivery
`ScheduledHomeAssistantJob` (Worker, hourly at :05 by default; `HomeAssistant:CronExpression`, `HomeAssistant:Enabled`) fans out
per tenant. Each event is *claimed* by its dedupe key before sending (once per condition, e.g. per bill and due date), a failed
send releases the claim to retry next run and stops the batch (no hammering). Claims older than 90 days are pruned.

## API
`GET/PUT /api/home-assistant`, `GET /api/home-assistant/preview` (what would be sent, never with amounts),
`POST /api/home-assistant/test`, `POST /api/home-assistant/send`. UI: **Home Assistant** in the nav.

## Not done
No inbound HA to FlowLedger channel, no HA custom integration/MQTT discovery; both are possible later on top of the same events.
