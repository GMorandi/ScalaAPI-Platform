# ScalaAPI Rewrite Verification

## Current evidence

The source-built smoke above is the current release evidence. Rows later in this
document that mention earlier classifier projects or migration counts are retained
as historical checkpoints and do not define the current bootstrap or release gate.

The latest source snapshot is Platform/Admin Web/User Web `ef2ff8c` and Gateway
`d9638b4`.

The latest source-built project `scalaapi-responses-fault-0810e` exited zero.
In addition to the root Responses JSON/SSE and GET/DELETE control checks, it
sent a source-owned malformed non-stream success response through the public
`scenario=malformed` query. Gateway returned HTTP 502 with the public
`provider_error` envelope, PostgreSQL retained exactly one
`reconciliation_needed` lease, and the request produced no usage event, usage
log, or NUMERIC usage debit. The run then passed the full existing restart,
fault, reconciliation/operator, media/S3, policy, and realtime matrix and
removed its Compose project and volumes.

The latest source-built project `scalaapi-responses-delete-0810` exited zero. In
addition to the root OpenAI Responses JSON and SSE checks, it issued `GET` and
`DELETE /v1/responses/{id}` and verified the response and deletion envelopes.
PostgreSQL showed one aborted subresource lease and one released hold per
control request, with zero usage events, usage logs, or NUMERIC usage debits.
The full existing restart,
reconciliation, Provider-fault, media/S3, content-policy, and realtime checks
also passed and the project cleaned all containers.

The latest source-built project `scalaapi-responses-0810a` exited zero. In
addition to the provider-group checks below, it sent OpenAI Responses JSON and
SSE requests through Gateway -> Platform -> Provider mock. The JSON response
passed completed metadata/output/usage validation; the SSE response delivered
`response.created`, `response.output_text.delta`, and `response.completed` with
nested usage. PostgreSQL showed exactly two completed leases, usage events,
usage logs, committed holds, and NUMERIC usage debits for the Responses pair.
The same source smoke passed the existing restart, reconciliation, Provider
fault, media/S3, content-policy, and realtime checks and cleaned all containers.

The latest source-built project `scalaapi-provider-groups-0810l` exited zero.
It built the current Gateway image and ran the empty-volume stack through seeded
Anthropic and Gemini groups. Anthropic `count_tokens`, Messages JSON, and
Messages SSE returned valid protocol-native responses; Gemini model discovery,
`generateContent`, and `streamGenerateContent` returned valid catalog/content
and SSE payloads. Four billable leases each produced exactly one completed
lease, usage event, usage log, committed hold, and NUMERIC `usage_debit`. The
non-billable count/catalog controls each produced one aborted lease and one
released hold with zero usage/debit rows. The same run passed the prior
restart/recovery, content-policy, provider-fault, reconciliation/operator,
media/S3, and four-session realtime checks. A retryable usage event with a
missing price snapshot no longer blocks later independent reports; the durable
outbox keeps it for retry and continues the batch. The Garnet authenticated
health probe now sends RESP CRLF bytes correctly. The smoke exited zero, removed
its named project and volumes, and left `podman ps -a` empty.

The source-built project `scalaapi-garnet-tls-0810e` exited zero. Its checked-in
TLS override enabled Garnet server TLS with a password-protected PFX, mounted the
CA read-only into Platform and Gateway, and passed readiness through the
production Platform TLS RESP client with DNS identity validation. The same run
applied 47 empty-volume migration records and skipped them on the second run,
passed OAuth, content policy, API-key, Embeddings, Realtime, restart/recovery,
Provider 429/500/malformed/disconnect/timeout/client-cancellation cases,
reconciliation/operator resolution, and S3 persistence. The smoke trap removed
the isolated project; `podman ps -a` was empty afterward. The follow-up source-built
project `scalaapi-garnet-rotation-0810` then replaced the mounted PFX with a
second same-CA certificate through Garnet's refresh period, forced Platform/Gateway
reconnects, observed readiness rejection for a wrong-SAN bundle and an expired
bundle, restored the valid certificate, and completed one new billable request.
Partitioned multi-process convergence remains a release gate; that project also
exited cleanly with no retained containers.

The follow-up source-built project `scalaapi-realtime-soak-0810b` exited zero.
It opened four concurrent `/v1/responses` WebSocket sessions at the seeded user's
concurrency limit, validated deterministic `session.created` and `response.done`
usage frames, held each upgraded connection for three seconds, and closed each
client cleanly. PostgreSQL then contained exactly four completed leases, usage
events, usage logs, committed holds, and NUMERIC `usage_debit` rows for the soak
prefix. The smoke continued through restart, Provider fault, reconciliation,
media, and Garnet TLS rotation checks; its cleanup trap left `podman ps -a`
empty.

Platform `a5cb552` adds the first public User Web surface for UI-06, and
Platform `ec502a2` adds its source-built runtime gate. The
unauthenticated `/models` route reads the Gateway public `/v1/models` contract,
`/status` reads Gateway `/ready`, and `/terms` plus `/privacy` render versioned
legal notices. The User Web nginx image proxies only those two Gateway paths;
authenticated `/auth` and `/user` paths remain unchanged. `npm run typecheck`,
`npm run build`, and the checked-in Playwright Chromium smoke passed (`2/2`),
covering public navigation, table caption/scoped headers, status feedback, legal
navigation, and access without a session. The source-built project
`scalaapi-public-ui-0810b` then passed the live-only Chromium case (`1/1`): the
container-served pages received HTTP 200 through the nginx-to-Gateway
`/v1/models` and `/ready` proxies and rendered both legal routes. The smoke exited
zero and its trap removed every named container. A deployment-specific
accessibility scan and legal-text configuration remain required before UI-06 can
be promoted to `implemented`.

The follow-up source-built project `scalaapi-user-portal-0810b` registered a new
user and passed both the public and authenticated live-only Chromium cases (`2/2`).
The authenticated case signs in through `/auth/login`, renders Dashboard balance
and identity data, and navigates Usage, API keys, and Profile through the built
nginx proxy. The smoke exited zero and cleaned the isolated project. Recovery-mail,
backup-code, Passkey ceremony, payment completion, mutation, and deployment
accessibility workflows remain separate gates.

Platform `0dd49cf` adds the authenticated Admin Web Operations route. The page
loads bounded metric summaries and policy-alert evidence from the existing
`/admin/ops-metrics` endpoints, filters alerts by kind/severity, and supports an
explicit refresh without exposing write controls or sensitive labels. `npm run`
`typecheck`, `npm run build`, and the Chromium suite passed `2/2`, including the
existing Content Policy route and a new Operations test covering summary rows,
alert rendering, filtering, and refresh. Collector ingestion, cross-service
correlation, delivery/recovery, and live authorization remain separate gates.

Platform `4f78b71` adds the authenticated Admin Web Channel Monitors route. The
page lists bounded recent health-check evidence, filters by account, and submits
bounded status/latency/error checks through the existing audited API. The same
`npm run typecheck`, `npm run build`, and Chromium suite passed `3/3`, covering
Content Policy, Operations, and Channel Monitors; scheduled runners, monitor
templates, feedback delivery, and live authorization remain separate gates.

The latest source-built project `scalaapi-policy-reclaim-0810e` exited zero. It
built the current source images, applied 44 empty-volume migration records and
skipped all 44 on the second migrator run, authenticated Garnet routing, passed
the OpenAI/external policy gates, restart/recovery, realtime, audited
reconciliation, MinIO persistence, and the complete Provider fault matrix. The
`disconnect_before_output` case now returns 503 with one retained unknown-charge
hold; its Provider fixture uses a normal zero-length EOF so the result is not
masked by a host socket timeout. Platform was also terminated immediately after
claiming a content-policy change event; the same container was restarted and the
expired claim was reclaimed before Garnet publication. The smoke trap removed all
named containers and anonymous volumes; `podman ps -a` was empty afterward.

After that historical smoke, Platform `49f68d5` added migrations 044-045 for
instance/sequence-idempotent OpenAI classifier snapshots and durable budget alerts.
A temporary PostgreSQL 17 database applied all 45 product records and the second
migrator run skipped all 45; the Compose baseline count is now 46 including the
image-owned Orleans schema. The Release solution build had zero warnings/errors and
the full Platform suite passed 239/239 (69 Grain, 77 Host, 45 Admin, 48 Provider);
the real-PostgreSQL classifier/schema selection passed 24/24. The store replay test
proves duplicate sequence insertion is harmless, two instances aggregate once,
budget alert state is updated atomically, `/metrics` renders fixed-label
unavailable-ratio/p95 breach gauges, and no content/rule/endpoint/credential value
is serialized. Platform `49f68d5` also re-evaluates the configured window when no
new requests arrive, resolves expired budget events, and tests two independent
workers plus a restarted worker with one sequence-one snapshot each.

The source-built project `scalaapi-metrics-process-0810f` then exercised this
boundary with two Platform silos and two Gateway processes. The second pair used
an independent Cap'n Proto socket; after one OpenAI Moderation response request,
both secondary processes were restarted and a second request completed. The gate
asserted two persisted snapshots, two distinct instance IDs, sequence 1 for each
instance, two classifier requests, two usage events, and two NUMERIC
`usage_debit` ledger effects. It exited 0 and removed all Compose resources;
`podman ps -a` was empty afterward. Credential rotation/redaction, deployed
malformed/oversized/timeout/cancellation scenarios, and long-stream metrics remain
separate release gates.

The previous source-built project `scalaapi-openai-moderation-0810e` exited zero.
It built the current Gateway, Platform, Admin Web, and User Web images, applied
44 empty-volume migration records (the image-owned Orleans baseline plus product
migrations 001-043), and skipped all 44 on the second migrator run. It authenticated
the Gateway-to-Platform path through Garnet and exercised the OpenAI Moderation
adapter against the Provider mock for both flagged response content and an upstream
503/unavailable response. The run verified propagated rules, public 400 and 503
errors, redacted OpenAI audits, warning/critical alerts, one completed lease, one
usage event, one NUMERIC debit, and idempotent response rows. Chat, Embeddings,
Realtime, the Provider fault matrix, restart/recovery, reconciliation/operator
resolution, and S3 object persistence also passed. The smoke trap removed every
named container and volume; `podman ps -a` was empty after cleanup. Production
OpenAI remains HTTPS-only; HTTP is enabled only by the explicit smoke development
switch.

The preceding OpenAI smoke was built from the earlier Platform `3da2e29` image;
the later `30cc8dc` classifier metrics code was verified separately against the
same classifier contract, and the latest `e639b50` smoke covers the full runtime
fault/recovery matrix.
Platform `75c4908` then made revision publication monotonic and replay-safe under
the shared PostgreSQL advisory lock used by propagation and Garnet rebuild. Full
Host tests at that checkpoint passed 73/73, including duplicate/stale/rebuild cases. A source smoke
using real Garnet passed the policy propagation, OpenAI match/unavailable, and
response-policy gates after this change; the later Provider
`disconnect_before_output` case timed out at the host boundary and returned 000,
so that rerun is not a full-stack green release result.
Platform `32e9576` additionally narrows the advisory lock to one publication while
keeping claims independent with `SKIP LOCKED`; the full Host suite remains 73/73.
Platform `30cc8dc` adds fixed-label `OpenAiModerationMetrics` counters and a
bounded Prometheus latency histogram to `/metrics`, recording match, no-match,
unavailable/protocol-error, and cancellation outcomes without content, rules,
endpoints, or credentials. Release build and the full Host suite pass with
0 warnings/0 errors and 74/74 Host tests; migrations 044-045 later add persisted
cross-process snapshots, retryable flushing, aggregate p95/unavailable-ratio
telemetry, durable budget alerts, and rolling-window recovery, while runtime
restart evidence remains open.
The SEC-01 slice now extends the single revision-3 Cap'n Proto contract with
bounded request and response content and a dedicated response-evaluation method.
Platform evaluates request rules before scheduling and non-stream response rules
after Provider validation but before delivery. Gateway hides blocked Provider
output, keeps normal Provider usage settlement, and replays the client-facing 400
through the existing idempotency response store. Gateway and generated Platform
contracts match byte-for-byte; the current smoke asserts request zero-lease
blocking, response audit/output withholding, one normal debit, and exact replay.

The prior source-built project `scalaapi-classifier-20260809d` exited zero.
It applied 31 empty-volume migration records (Orleans plus product migrations
001-030), skipped all 31 on the second migrator run, authenticated Gateway through
Garnet, passed the complete Provider fault matrix including deterministic
`disconnect_before_output -> 503`, completed media object persistence, ran
reconciliation, settled and replayed one operator resolution, and proved new
billable requests after Platform and Gateway restart. Its trap removed the project
containers, volumes, network, and stack-specific images; baseline `apitf_*`
resources and `scalaapi-gateway:dev` were retained.
The source contract slice now also validates OpenAI model-list entries, Gemini
model names/methods/token limits, and Anthropic positive bounded `input_tokens`;
the Provider mock exposes deterministic malformed, duplicate, and zero-count
profiles for these contracts.

The current Passkey slice adds product migration 031 and a real empty-schema
PostgreSQL check. The first migrator run applied 32 total records (Orleans plus
001-031), the replay skipped all 32, and `PasskeyStoreTests` passed 1/1. The
Fido2-backed endpoints persist bounded one-shot registration/authentication
challenges, public credentials, monotonic signature counters, and transactionally
paired registration/revocation audits. User Web `45b75f8` converts browser options
and responses for registration, revocation, and login; browser ceremony and
anti-enumeration are not yet release evidence.

The current maintenance slice adds product migration 032 and a real empty-schema
PostgreSQL check. The first migrator run applied 33 total records (Orleans plus
001-032), the replay skipped all 33, and `MaintenanceStoreTests` passed 1/1.
The export omits password, refresh-token, and API-key hashes; cleanup is bounded by
retention and row limit, supports dry-run and actor-scoped replay/conflict, and
deletes only expired authentication/ceremony data with an audit row.

The current announcements slice adds product migration 033 and a real empty-schema
PostgreSQL check. The first migrator run applied 34 total records (Orleans plus
001-033), the replay skipped all 34, and `AnnouncementStoreTests` passed 1/1.
Published, unexpired announcements expose read state; the first read writes one
`announcement_reads` row and one `announcement.read` audit event, while duplicate
reads replay the stored timestamp. User Web renders the list and read action on the
Dashboard; targeting/scheduling and browser authorization remain open.

The authentication notification slice added product migration 034 and, at its
checkpoint, a real empty-schema PostgreSQL check applied 35 total records (Orleans
plus 001-034), skipped all 35 on replay, and `EmailDeliveryTests` passed
3/3. Password-reset and email-verification requests persist only AES-GCM protected
token material in the outbox; a worker builds signed action links, retries failed
delivery with bounded backoff, marks successful rows sent, and suppresses pending
messages superseded by a newer token. User Web hydrates the token from those links.
Live SMTP/provider delivery, browser mail receipt, delivery metrics, and broader
abuse/anti-enumeration controls remain release gates.

The current subscription-entitlement slice at Platform `ad6ac20` adds migration
035. An empty PostgreSQL 17 database applied 000 plus 001-035, skipped all 36 on
replay, and `MigrationSchemaTests` plus `SubscriptionQuotaTests` passed. Two
concurrent lease creations against one 1.00 USD grant produced one reservation and
one `quotaExhausted` rejection; settlement moved the actual NUMERIC cost to
`quota_used_usd`, and a no-charge abort released the remaining reservation. User
subscription responses and Billing/Dashboard render reserved and remaining quota.
Payment-provider coupling, cross-Silo evidence, and browser authorization remain
open; the renewal worker lifecycle is covered below.

Platform `e05ed40` adds the hosted subscription lifecycle worker. Its real
PostgreSQL test runs two workers concurrently and proves deterministic one-time
processing for an internal auto-renewal, expiry for a non-renewing row, recovery
of an auto-renewal already marked `expired`, and `past_due` deferral while a lease
reservation remains. Clearing the reservation allows the worker to reset the plan
grant and append one renewal event; non-internal providers remain `past_due` until
a payment adapter confirms renewal.

Platform `d71fe8b` adds migration 036 and a provider pricing catalog boundary.
The bounded client accepts only a versioned JSON `data` array of fixed-scale
non-negative decimal rates, authenticates with a bearer header, rejects duplicate
or oversized responses, and derives a deterministic checksum/version per model.
The refresh transaction inserts new source versions, closes only the previous open
version for that provider/model, and replays an identical snapshot without new
rows. A real PostgreSQL 17 run applied and replayed all 36 product migrations and
the Host history test proved two snapshots, four rows, two closed rows, and two
current rows. Provider-specific adapters and tokenizer authority remain open.

Platform `44d2096` adds migration 037 and a metadata-only media object
reconciliation boundary. A PostgreSQL `SKIP LOCKED` batch claims due succeeded
media rows, verifies the signed S3-compatible `HEAD` response against stored
ETag/size metadata, and records missing, mismatched, or transient failures as
retryable object state without changing the settled operation or lease. A later
valid `HEAD` restores `object_status=stored` and clears the error. Object
listing/orphan cleanup, restore, cancellation/restart, and full MinIO lifecycle
evidence remain open.

Platform `6bc411b` adds the Admin Web reconciliation workflow on top of the
existing authenticated API. Operators can filter open/resolved incidents, run a
manual reconciliation, and submit a reason plus explicit evidence for settle or
release. The page sends a fresh `Idempotency-Key` for each command and handles
  accepted, duplicate, and conflict responses, preserving the same key across form retries; browser automation and the broader
operations dashboard remain open.

The policy operations slice at Platform `9fb449c` adds migration 030. Platform
`caa719e` serializes revision publication across workers, and `7fca582` adds the
source-owned external classifier adapter and Provider mock HTTP contract. Admin rule
mutations now atomically record revision, actor/IP audit, and a PostgreSQL change
outbox. A hosted worker claims and retries those changes, publishes the revision
and invalidation counter to authenticated Garnet, and exposes paged Admin change
history. Policy blocks and unavailable classifiers append deterministic alert rows;
the empty-stack smoke waited for propagated changes and queried both warning block
and critical classifier-unavailable evidence. Host tests cover successful Garnet
propagation and failure/retry recovery, bounded classifier requests, status/schema/
timeout mapping, and caller cancellation. The Provider mock tests match/no-match
and deterministic fault fixtures; the empty-stack gate proves external match/block
400 and outage 503 with redacted audit plus one normal settlement/replay. A
Platform `dcdca5e` additionally provides an optional OpenAI Moderation adapter and
an official-shaped `/v1/moderations` Provider mock fixture; Host and Provider HTTP
tests cover Bearer authentication, model/input bounds, single-result `flagged`
parsing, 429/5xx, malformed, oversized, and timeout behavior. Platform `2992964`
adds migration 043 plus a tested Admin rule normalizer; a real empty PostgreSQL
run accepted, evaluated, and redacted an `openai` rule, and all 43 product
migrations skipped on replay. Platform `94e0db8` makes policy revision writes
non-expiring and restores the PostgreSQL revision plus a fresh invalidation version
through the authenticated cache rebuild. A dedicated RemoteGarnet run deleted both
keys and read the rebuilt values back. Migrations 044-045 now supply idempotent
cross-instance classifier metric snapshots, aggregate p95/unavailable-ratio
telemetry, durable budget breach state, and rolling-window recovery; runtime
ordering/failure, restart evidence, browser evidence, and long-stream classifier
metrics remain release gates.
Platform `c029b3c` adds versioned runtime configuration snapshots, bounded key/value
validation, secret/connection-string rejection, boolean-only feature flags, stale
version conflict handling, and Admin actor/IP audit persistence; the Grain suite
covers snapshot isolation and validation.
The latest source-built project `scalaapi-embeddings-20260809b` passed the
full empty-volume gate: 27 migration files applied and skipped on the second
run, Garnet authenticated the Gateway -> Platform path, two three-dimensional
float Embeddings and one two-dimensional base64 Embedding settled with the
active NUMERIC price version, and a shape-invalid Provider response returned
`502/provider_protocol_error` while retaining one unknown-charge lease. The
same run passed OAuth, API-key lifecycle, realtime, restarts, the Provider fault
matrix, reconciliation, and MinIO persistence; the cleanup trap removed its
temporary stack and tags.
The current source-built project `scalaapi-api-key-audit-verified` passed the
full 27-migration empty-volume gate and authenticated the API-key audit query:
the filtered denied event returned actor/action metadata and no plaintext-key
field. The complete Garnet, API-key replay/concurrency/expiry, auth/OAuth,
realtime, restart/recovery, Provider fault, reconciliation, and MinIO matrix
remained green; all temporary stack resources and images were removed.
The follow-up `scalaapi-api-key-lifecycle-verified` smoke passed the API-key
lifecycle assertions: Admin ownership guard/update/revoke, updated-key Chat,
revoked-key 401, and user self-service rotation with old/new state and audit
invariants. The lifecycle slice is evidence-backed. A later clean run also
passed the full Provider fault matrix, including `disconnect_before_output`.
The current source-built project `scalaapi-key-http-verified` passed the full
27-migration empty-volume gate with authenticated HTTP API-key replay and expiry
checks. Two simultaneous Chat requests sharing one idempotency key produced one
completed lease/idempotency row and no duplicate billing; a short-lived key was
rejected after expiry with HTTP 401 `authentication_error` before any lease was
created. The same run passed Garnet, auth/OAuth, realtime, restart/recovery,
Provider faults, reconciliation, and MinIO object assertions; temporary stack
resources and tags were removed.
The current source-built project `scalaapi-auth-abuse-verified3` passed the
27-migration empty-volume gate. Malformed registration returned HTTP 400; five
failed logins for one unknown email returned HTTP 401 and the sixth returned
HTTP 429 with `Retry-After`, backed by the new hash-only PostgreSQL
`auth_abuse_counters` table. The same run passed authenticated Garnet,
rotating-session and OAuth flows, realtime settlement, Platform/Gateway restart
recovery, the complete Provider fault matrix, audited reconciliation, and MinIO
signed object persistence; its cleanup trap removed the temporary stack and
stack-specific image tags.
The current source-built project `scalaapi-key-policy-verified` passed the
26-migration empty-volume gate. A key scoped only to `models` received HTTP 403
`permission_error` for Chat, produced one denied API-key audit row, and created
no request lease; the full accounting, restart, realtime, reconciliation,
Provider failure, and object-storage assertions also passed.
The current source-built project `scalaapi-oauth-refresh-20260809` passed the
full empty-volume gate after correcting the smoke DTO path to `.oAuth`: a seeded
expired `mock-access-v1` credential rotated over real HTTP to version 2 before
the first Chat dispatch, the request settled once, and Admin details remained
secret-free. The cleanup trap removed all project resources and temporary image
tags; the named `apitf_*` development resources were retained.
The current source project `scalaapi-scheduling-verified` also passed the full
26-migration empty-volume gate with authenticated Garnet, rotating auth sessions,
OAuth refresh, realtime settlement, Platform/Gateway restart recovery, the complete
Provider fault matrix, audited reconciliation, and MinIO signed object persistence.
Its cleanup removed all containers, volumes, networks, and stack-specific image tags;
only the named `apitf_*` development resources remain.
The Platform `3572abd` auth/scheduling/policy slice passes the full 143-test suite and Release
build with zero warnings: API-key scope normalization and projection tests pass,
unknown scopes are rejected, capability denials are classified before scheduling,
and migration/schema checks require the new scope, expiry, append-only audit, and
auth-abuse counter state. Authenticated HTTP replay/concurrency and expired-key
cases pass in `scalaapi-key-http-verified`; the authenticated API-key audit query
and lifecycle update/revoke/rotation pass in `scalaapi-key-lifecycle-verified`;
multi-instance contention and browser cases remain open.
Gateway CTest is 104/104. The complete empty-volume gate passed in
`scalaapi-realtime-smoke-20260809` with the 22-migration double-run,
Garnet-authenticated request path, realtime WebSocket settlement, nine
unknown-charge fault scenarios, and the `disconnect_after_usage` case: valid
usage observed before truncated SSE EOF settled exactly once through the durable
outbox. The gate ended with nine unknown-charge incidents, one audited operator
resolution, and eight remaining open incidents. Temporary smoke containers,
volumes, networks, and tags were removed by the cleanup trap; baseline `apitf_*`
development resources remain.
The canonical/vendor Cap'n Proto digest check passes for all three schemas. The
host `verify-generated-contracts.sh` command remains an environment gate on this
machine because the pinned `capnp` compiler is not installed; CI or the build
container must run the byte-identical C# generation check before release.
The recovery hook matrix also passed in `scalaapi-recovery-after-commit-0901`
with `platform.after_settlement_commit` and in
`scalaapi-recovery-debug-0903` with `platform.before_outbox_ack`: both observed
the Platform process exit, restarted the same container, and ended with one
terminal debit and eight remaining open incidents. Both temporary stacks,
volumes, networks, and image tags were removed after evidence capture.
The current source-built project `scalaapi-gateway-recovery-0907` also enabled
`gateway.after_provider_completion`: the Gateway request failed with an empty
transport reply, the same container was explicitly started, its marker survived,
and the forwarded lease reached `reconciliation_needed` with an active hold and
no usage/debit. The full gate ended with ten unknown-charge incidents, one
audited operator settlement, and nine remaining open incidents. Its containers,
volumes, network, and image tags were removed; baseline `apitf_*` resources were
retained.
The follow-up source-built project `scalaapi-gateway-dispatch-recovery-0911`
enabled `gateway.before_provider_dispatch`: Gateway terminated before Provider
contact, the same container was explicitly started, and the held lease safely
became `expired` with its hold/idempotency released and no usage/debit or new
incident. The full gate passed with nine unknown-charge incidents, one audited
settlement, and eight remaining open incidents; its temporary resources and image
tags were removed.
The current source-built project `scalaapi-platform-dispatch-recovery-0912`
enabled `platform.before_provider_dispatch`: Platform terminated after creating
the SQL lease/hold but before returning the upstream target. The same container
was explicitly started, the marker survived, and TTL recovery changed the lease
from `held` to `expired`, released the hold and idempotency row, and produced no
usage, debit, or incident. The complete gate passed with nine unknown-charge
incidents, one audited operator settlement, and eight remaining open incidents;
the cleanup trap removed the temporary project and image tags.
The current source-built project `scalaapi-platform-worker-recovery-0913`
enabled `platform.after_outbox_claim`: Platform completed the SQL settlement,
claimed the durable `complete` outbox item, and terminated before any Grain side
effect. The same container was explicitly started; the expired claim was
reclaimed and the outbox was applied once with no duplicate usage, debit, or hold
transition. The complete gate passed with nine unknown-charge incidents, one
audited operator settlement, and eight remaining open incidents; all temporary
resources and image tags were removed.
The current source-built project `scalaapi-platform-dispatch-retry-0914`
enabled `platform.before_provider_dispatch`: Platform terminated after the
lease/hold commit, Gateway retried the same request under the same idempotency
identity, and the replacement Platform rebuilt the active lease target. The
request settled exactly one lease, usage event, usage log, and NUMERIC debit;
the full matrix passed with nine unknown-charge incidents, one audited operator
settlement, and eight remaining open incidents. This smoke used a clean runtime
image assembled from Gateway `9c7171f`; full FetchContent history is required
because the pinned Photon commit is not advertised on an upstream discoverable
ref. All temporary resources and tags were removed.
The Platform `dd23bb4` Provider.Mock local WebSocket probe also passed: a real
HTTP/1.1 upgrade to `/v1/responses`, masked `session.update` input, deterministic
`session.created` and `response.done` usage frames (7 input/5 output), and a
clean close all completed successfully. The subsequent
`scalaapi-realtime-smoke-20260809` gate also validated Gateway-to-Provider
forwarding and settlement; long-connection soak remains.
The checked-in `deploy/stack/realtime_smoke.py` client performs the same protocol
probe without third-party dependencies, and the full-stack invocation now
asserts one realtime lease, usage event, usage log, committed hold, and ledger
debit. Gateway `9c7171f` builds cleanly from the immutable Photon pin by using a
full FetchContent checkout; the upstream ref does not advertise the pinned
commit, so shallow checkout is intentionally disabled.
The older detailed rows below retain prior checkpoint evidence; this snapshot
supersedes them where commit, image, or late-usage results differ.

| Gate | Result | Interpretation |
| --- | --- | --- |
| OpenAI Responses runtime gate | `scalaapi-responses-fault-0810e`, exit 0 | Platform `ef2ff8c` and Gateway `d9638b4` built from source on empty volumes. Provider mock Responses emits a terminal SSE event with usage; JSON and SSE requests returned valid envelopes and each settled one lease/event/log/committed hold/NUMERIC debit. GET `/v1/responses/{id}` replayed the stored response and DELETE `/v1/responses/{id}` returned `response.deleted`; each released one non-billable lease with no usage/debit. A malformed non-stream 2xx fixture returned HTTP 502 `provider_error`, retained one reconciliation hold, and produced no usage/debit before the audited resolution pass. Remaining mutation semantics, provider-group faults, and live adapters remain open |
| Anthropic and Gemini provider-group gate | `scalaapi-provider-groups-0810l`, exit 0 | Platform `7c4836a` and Gateway `7b8b03c` built from source on empty volumes. Anthropic count-tokens, Messages JSON, and Messages SSE plus Gemini catalog, JSON generation, and SSE generation returned valid protocol-native payloads. Four billable requests each produced exactly one completed lease, usage event, usage log, committed hold, and NUMERIC debit. The two non-billable controls each aborted and released their hold with zero usage/debit rows. The same run passed restart/recovery, content policy, Provider fault, audited reconciliation, media/S3, and realtime soak checks; retryable usage reports no longer starve independent leases, and the authenticated Garnet RESP health probe emits correct CRLF bytes. Cleanup left `podman ps -a` empty |
| Current source-built policy and stack gate | `scalaapi-policy-reclaim-0810e`, exit 0 | Platform `926b98e` and Gateway `3da0d33` built from source; 44 empty-volume migration records applied and skipped on replay; Garnet-authenticated routing, OpenAI response match/unavailable 400/503, redacted audits, warning/critical alerts, normal settlement/replay, Chat/Embeddings/Realtime, Provider faults including `disconnect_before_output -> 503`, restart/recovery, reconciliation/operator resolution, and S3 persistence passed. Platform also crashed after a policy outbox claim and its replacement reclaimed/published the event. The trap removed all named containers and anonymous volumes and `podman ps -a` was empty afterward |
| Garnet TLS source deployment gate | `scalaapi-garnet-tls-0810e`, exit 0 | Platform `bcf80e7` and Gateway `3da0d33` built from source. The TLS override enabled Garnet PFX server mode with explicit password authentication and no client-certificate requirement, mounted the CA/PFX read-only with rootless relabeling, and passed the full source smoke through Platform's authenticated TLS readiness endpoint. Empty-volume migrations, OAuth, policy, API-key lifecycle, Embeddings, Realtime, restart/recovery, Provider fault matrix, audited reconciliation/operator resolution, and S3 persistence passed; the trap removed the isolated project and `podman ps -a` was empty. Rotation/expiry is covered by the newer `scalaapi-garnet-rotation-0810` row; partition recovery remains open |
| Garnet TLS rotation and failure recovery | `scalaapi-garnet-rotation-0810`, exit 0 | Platform `8ca919b` and Gateway `3da0d33` built from source. The smoke replaced the PFX with a same-CA rotated certificate and reconnected both clients, rejected wrong-SAN and expired bundles with Platform readiness `503`, restored the valid bundle, reconnected clients, and settled a new billable request. The full preceding empty-volume matrix also passed; the trap removed the project and `podman ps -a` was empty. Partitioned multi-process convergence remains open |
| Realtime WebSocket concurrent soak | `scalaapi-realtime-soak-0810b`, exit 0 | Platform `43eb52c` and Gateway `3da0d33` built from source. Four concurrent sessions held their upgraded connections for 3 seconds, validated Provider usage, and settled exactly one lease/event/log/committed hold/NUMERIC debit per session. The complete matrix, restart/recovery, Provider faults, reconciliation, media, and TLS rotation passed; temporary resources were removed and `podman ps -a` was empty |
| Multi-process classifier metric flush/restart | `scalaapi-metrics-process-0810f`, exit 0 | Source-built Compose started a second Platform silo and Gateway on an independent Cap'n Proto socket, sent an OpenAI Moderation response-policy request, restarted both secondary processes, then repeated the request. PostgreSQL contained two instance IDs, sequence 1 for each, two snapshots and two requests; usage_events and NUMERIC `usage_debit` ledger rows each counted two. The smoke cleanup removed the extra and baseline resources and `podman ps -a` was empty |
| Cross-Gateway idempotency and Silo rejoin | `scalaapi-multi-gateway-0810b`, exit 0 | The source-owned empty-volume smoke started two Platform silos and two Gateways, restarted the secondary pair, then sent concurrent normal Chat requests through both Gateways with one API-key idempotency key. It stopped the secondary pair, proved the primary request still settled with one active Silo, restarted the original containers, verified two active Silos, and settled through the rejoined secondary Gateway. PostgreSQL proved one shared-idempotency lease/debit plus two outage/rejoin leases, usage events, and NUMERIC `usage_debit` rows. The trap removed the isolated project and `podman ps -a` was empty |
| PostgreSQL backup and isolated restore | `scalaapi-backup-0810b`, exit 0 | The source-owned empty-volume gate applied and replay-skipped all 46 product migrations, registered a fresh user, created an Admin `postgres` backup through the new idempotent command, verified a non-empty artifact and 64-character SHA-256, replayed the same create key, restored into a separate `platform_restore` database with PostgreSQL 17 `pg_restore`, replayed the restore key, and verified the restored user row. The Admin API image now pins the PGDG PostgreSQL 17 client to match the Compose server. The trap removed the stack and `podman ps -a` was empty |
| Current Platform tests | 244/244, exit 0; Release build 0 warnings/0 errors | 69 Grain, 81 Host, 46 Admin, and 48 Provider tests; Host adds the Anthropic seed-price alias regression around the existing in-process TLS RESP listener, authenticated Garnet traffic, server-name rejection, and TLS/CA configuration guard; the source smoke supplies terminal control-operation release evidence. Existing coverage includes the PostgreSQL-backed administrative-price precedence regression test, monotonic Garnet revision publication, fixed-label OpenAI classifier metrics/secret-redaction assertions, migrations 044-046 cross-instance snapshot/p95/error-ratio/budget-alert/window-recovery/hosted-worker replay, and backup schema coverage |
| Admin Web backup controls | Platform `840636e`, `npm run typecheck`, `npm run build`, Playwright backup route `1/1` | `/backups` lists status, artifact size, and SHA-256 prefix, shows whether an isolated restore target is configured, creates PostgreSQL backups with an idempotency key, and exposes restore only for completed artifacts with a configured target. Live browser authorization, restore-failure UX, and signed/offsite artifact management remain |
| Gateway provider-completion crash recovery | `scalaapi-gateway-recovery-0907` source-built smoke passed; Gateway terminated after Provider completion, was explicitly restarted, and the same request retained one active hold in `reconciliation_needed` with no usage/debit | Durable lease evidence survives Gateway process loss; the one-shot marker prevents a restart loop, and the normal reconciliation/operator path remains authoritative |
| Gateway before-provider-dispatch crash recovery | `scalaapi-gateway-dispatch-recovery-0911` source-built smoke passed; Gateway terminated before Provider contact, was explicitly restarted, and the same held lease became `expired` with released hold/idempotency and no usage/debit | Never-forwarded work remains safe to expire after Gateway loss and does not create an unknown-charge incident |
| Platform before-provider-dispatch crash recovery | `scalaapi-platform-dispatch-recovery-0912` source-built smoke passed; Platform terminated after persisting an unforwarded lease/hold, the same container was restarted, and TTL changed `held` to `expired` with released hold/idempotency and no usage/debit/incident | Platform loss before the dispatch RPC response is safe to reclaim and cannot create a billable or unknown-charge outcome |
| Platform outbox-claim worker recovery | `scalaapi-platform-worker-recovery-0913` source-built smoke passed; Platform terminated immediately after claiming the completed settlement outbox, the same container was restarted, and the expired claim was reclaimed and applied exactly once | A worker crash before external Grain effects does not lose the durable event or duplicate the financial settlement |
| Platform dispatch retry and active-lease recovery | `scalaapi-platform-dispatch-retry-0914` source-built smoke passed; Platform died after the lease/hold commit, Gateway retried with the same request/idempotency identity, and replacement Platform recovered and settled the existing lease with exactly one lease, usage event, usage log, and debit | A lost dispatch response is retryable without allocating a second lease or billing twice |
| Embeddings contract smoke | `scalaapi-embeddings-20260809b`, exit 0 | Gateway `40cb02f` and Platform `ef1e474` returned two three-dimensional float vectors and one two-dimensional base64 vector, settled both against the active NUMERIC Embeddings price, and mapped a shape-invalid Provider response to `502/provider_protocol_error` with one retained `reconciliation_needed` hold; the same run applied and re-ran all 27 migrations and passed the full Garnet, OAuth, restart, Provider, reconciliation, and MinIO matrix |
| Gateway build and CTest | Clean local build; 125/125, exit 0 | Adds versioned OpenAI Chat/Responses, Anthropic Messages, and Gemini request/response/SSE/error golden fixtures, all sixteen request pairs, all sixteen response pairs with target-envelope validation, sixteen cross-protocol error conversions with same-protocol passthrough, standard HTTP status precedence, and cross-protocol conversion assertions to the existing classifier-outage fail-closed disposition, bounded event-boundary streaming policy buffering, protocol-shaped policy errors, response replay, model catalog, Embeddings, nested Anthropic usage, SSE, timeout, cancellation, Garnet, retry, and routing coverage |
| Platform tests | 239/239, exit 0; Release build 0 warnings/0 errors | 69 Grain tests, 77 Host tests, 45 Admin tests, and 48 Provider mock tests; Host coverage includes versioned Unicode normalization, source-owned and OpenAI Moderation classifier request/status/schema/timeout/cancellation behavior, persisted `openai` rule evaluation, classifier constraint migration evidence, no-TTL policy revision publication, PostgreSQL-backed revision rebuild, dedicated RemoteGarnet key-loss recovery, redacted audit metadata, successful/retryable propagation, concurrent two-worker claim serialization, cross-instance classifier snapshots and budget-alert recovery, staged audits, accounting, media lifecycle, metadata-only object reconciliation and recovery, reconciliation, serialized subscription quota reservation/settlement, deterministic provider pricing parsing, bounded provider responses, native mock/Stripe payment checkout routing and idempotency boundaries, payment provider reference migration/schema replay, payment refund schema/replay/conflict/claim-reclaim evidence, two independent partial refund settlements with cumulative order status, and real PostgreSQL pricing history/replay and administrative-price precedence; Admin coverage adds explicit local/external/openai rule normalization and rejection of unknown classifiers, atomic referral reward replay/conflict evidence, audited operational-metric summaries/alerts, bounded redacted audit export evidence, encrypted proxy/TLS profile validation, bounded active-account monitor checks, Passkey challenge/credential/counter lifecycle evidence, maintenance export redaction/cleanup/replay/conflict evidence, announcement read-state duplicate replay/audit evidence, encrypted email outbox delivery/supersession/retry evidence, concurrent subscription renewal/expiry/past_due lifecycle evidence, Stripe raw-body signature timestamp checks, Stripe checkout/refund event normalization including incremental/cumulative partial refunds, and Provider refund adapter contracts; Provider HTTP tests cover source-owned and OpenAI Moderation classifier, pricing catalog, deterministic checkout, and deterministic refund contracts plus deterministic fixtures |
| Maintenance export and cleanup | Platform `80ab783`; temporary PostgreSQL, product migrations 001-032 applied then skipped, targeted Admin and schema tests 1/1 each passed | `/user/export` returns bounded account/API-key metadata, usage, sessions, and Passkey metadata without password, refresh-token, or API-key hashes; `/admin/maintenance/cleanup` supports dry-run, retention/row limits, actor-scoped idempotency replay/conflict, and removes expired session/token/challenge/counter records transactionally with audit evidence |
| Announcement read tracking | Platform `acb1c66`; temporary PostgreSQL, product migrations 001-033 applied then skipped, targeted Admin and schema tests 1/1 each passed; User Web typecheck/build passed | Authenticated users list published, unexpired announcements with read state; the first read inserts one state row and one audit event, duplicate reads return the same timestamp without a second audit; Dashboard exposes unread items and the read action. Targeting, scheduling, and browser authorization remain open |
| Email notification delivery | Platform `857ef3b`; temporary PostgreSQL, product migrations 001-034 applied then skipped, targeted Admin tests 3/3 and schema test 1/1 passed | Password-reset and email-verification requests enqueue encrypted token material only; the worker delivers one signed action email, marks it sent, retries a simulated SMTP outage, and cancels stale pending mail after a newer token; User Web consumes `?token=` links. Live SMTP/provider delivery, browser receipt, metrics, and public abuse limits remain open |
| Subscription quota reservation | Platform `ad6ac20`; temporary PostgreSQL, 000 plus product migrations 001-035 applied then skipped, `MigrationSchemaTests` and `SubscriptionQuotaTests` passed | Active subscriptions reserve maximum lease cost under a row lock, concurrent over-allocation returns `quotaExhausted`, settlement consumes actual NUMERIC cost, and no-charge abort releases reservation; User Web exposes reserved/remaining quota. Payment coupling, quota reconciliation, multi-Silo and browser evidence remain open |
| Subscription renewal lifecycle | Platform `e05ed40`; `SubscriptionRenewalServiceTests` passed against empty PostgreSQL schema | Two concurrent workers process one due row, internal auto-renew resets plan grant after reservations drain, no-renew rows expire, stale `expired` auto-renew rows recover, and held quota becomes `past_due`; deterministic `subscription_events` make committed transitions replay-safe. External payment confirmation, quota reconciliation, and browser evidence remain open |
| Provider pricing catalog refresh | Platform `d71fe8b`; migration 036, temporary PostgreSQL 17, first apply 36 product migrations and replay skip 36, targeted Host tests 3/3 plus Provider Mock tests 1/1 | Provider Mock exposes three decimal quotes; the adapter authenticates with bearer credentials, bounds response size, rejects malformed/duplicate/out-of-range rates, derives deterministic source checksums, inserts per-model immutable versions, closes prior same-source versions, and replays identical snapshots without new rows. Provider-specific pricing rules, tokenizer/golden fixtures, and multi-provider runtime E2E remain |
| Passkey persistence and ceremony state | Platform `f06cccc` + User Web `45b75f8`; temporary PostgreSQL, product migrations 001-031 applied then skipped, targeted Admin and schema tests 1/1 each passed; User Web typecheck/build passed | `passkey_challenges` enforces flow-scoped five-minute one-shot challenges; atomic registration consumes the challenge, persists public material, and audits the mutation together; `passkey_credentials` stores monotonic signature counters. Fido2 registration/authentication endpoints issue normal sessions without private key storage, and User Web converts browser creation/assertion payloads for registration, revocation, and sign-in. Real browser ceremony, anti-enumeration, and distributed abuse limits remain open |
| Concurrent policy propagation contract | Platform `15cdfc0` plus `926b98e`; temporary PostgreSQL, product migrations 001-030 applied then skipped, 3/3 targeted tests and source smoke passed | Two workers claim distinct events with `SKIP LOCKED`; the Host test asserts advisory-lock serialization, exactly one Garnet revision publication per event, ordered revisions, and one invalidation increment per event. The `scalaapi-policy-reclaim-0810e` process hook additionally terminates Platform after a policy-event claim, restarts the same container, and proves the claim is reclaimed and published. The temporary database/container resources were removed after verification |
| Versioned content policy | Gateway `8f33790`; Platform `75c4908` | `unicode-confusable-v1` normalizes NFKC/case/format/confusable forms before local matching; rules persist classifier/evaluator/redaction and a monotonic policy revision. The configured external adapter and optional OpenAI Moderation adapter use bounded UTF-8 input and a 100-5000ms timeout; OpenAI uses Bearer auth, a configured model, and validates one `results[].flagged` response. Migration 043 and the Admin normalizer make `openai` an explicit persisted classifier, with real PostgreSQL constraint/evaluation/redaction evidence. Policy revision publication has no TTL, and authenticated cache rebuild restores the PostgreSQL revision plus invalidation version after dedicated RemoteGarnet key loss. 429/5xx and transport timeout map to retryable HTTP 503, while malformed/unknown/oversized schema fails closed as protocol error. Request Unicode blocking creates no lease; response match/block and SSE buffering retain their billing evidence. Migration 030 durably records rule mutations, serializes worker publication, retries Garnet revision/invalidation propagation, and persists warning/critical alert evidence queried by Admin. Platform `75c4908` additionally makes revision publication monotonic/replay-safe under the shared PostgreSQL lock; the current source smoke additionally proves OpenAI response match/unavailable 400/503 with redacted audits, normal settlement/replay, and alerts; `3da2e29` includes authenticated-route Admin Web rule CRUD plus changes/alerts tabs and a passing API-intercepted Chromium smoke. Separate-process policy ordering/failure, browser authorization against a live API, credential redaction/rotation, and long-stream classifier metrics remain open |
| Runtime configuration contract | Platform `c029b3c` Grain tests and Admin Release build | `feature.*` values are boolean-only, sensitive keys and connection strings are rejected, updates use an expected version, returned snapshots are independent copies, and successful Admin writes persist `config.update` actor/IP audit rows; dynamic consumer reload and browser controls remain |
| Authentication abuse smoke | `scalaapi-auth-abuse-verified3`, exit 0 | Fresh PostgreSQL applies 000 plus 001-026, second migrator run skips all 27; malformed registration is 400, five bad logins are 401, the sixth is 429 with `Retry-After`, and the full Garnet/protocol/restart/Provider/reconciliation/MinIO matrix remains green |
| API-key HTTP replay and expiry smoke | `scalaapi-key-http-verified`, exit 0 | Two concurrent Chat requests share one idempotency key and leave one completed lease/idempotency row; a short-lived key returns 401 `authentication_error` after expiry with no lease; the complete stack matrix remains green |
| API-key authenticated audit smoke | `scalaapi-api-key-audit-verified`, exit 0 | Admin token reads a filtered denied event by key hash, receives actor/action metadata without plaintext-key fields, and the complete empty-volume Garnet/Provider/restart/reconciliation/MinIO matrix remains green |
| Realtime smoke client | `python3 deploy/stack/realtime_smoke.py` passed against Release `Provider.Mock`; `bash -n deploy/stack/smoke.sh` and `git diff --check` passed | Validates HTTP/1.1 upgrade, masked session input, deterministic session/usage frames, close handling, and full-stack exactly-once lease/usage/hold/ledger settlement using the clean Gateway image |
| Platform Release build | Passed, 0 warnings and 0 errors | Includes Platform Host, Admin API, migrator, Provider mock, and benchmark assembly |
| Admin Web | Platform `4f78b71`; typecheck, production build, and Playwright Chromium smoke passed (`3/3`) | Provider account form supports static headers and OAuth refresh metadata/replacement while list/details expose health but not stored secrets; reconciliation filters incidents, runs manual checks, and submits evidence-backed settle/release commands with a stable key per selected command and replay-safe responses. Content Policy manages rules and exposes propagation changes and alert filters. Operations renders bounded metric summaries and policy-alert evidence with kind/severity filters and explicit refresh. Channel Monitors lists bounded history, filters accounts, and submits bounded health checks. The browser smoke intercepts the Content Policy, Operations, and Channel Monitor contracts, verifies route navigation and tab rendering; live authorization/API and scheduled/feedback workflows remain |
| User Web | Platform `4ed1d5b`; `npm run typecheck`, `npm run build`, and the local `npm run test:e2e` passed (`2/2`, live-only cases skipped); source-built `scalaapi-public-ui-0810b` passed the public live case (`1/1`); source-built `scalaapi-user-portal-0810b` passed public plus authenticated live cases (`2/2`) | Independent Solid client covers auth, OAuth callback, password recovery with action-link hydration, email verification with action-link hydration, dashboard announcements/read tracking, scoped usage, API keys, provider-selectable payment checkout links, active-plan subscription purchase/cancel/renew with reserved/remaining quota, redeem codes, referral summary, profile, TOTP setup/verify/disable, Passkey registration/revocation/sign-in option conversion, plus public model/status/terms/privacy routes. Source-built browser evidence now covers login, Dashboard balance/identity, Usage, API keys, Profile, and the nginx-to-Gateway catalog/readiness proxy; recovery-mail, backup-code sign-in, real authenticator ceremony, payment completion, mutation workflows, deployment-specific accessibility scanning, and legal-text configuration remain |
| Provider OAuth credential refresh | Grain, Host, and Provider Mock tests pass for a single-account refresh lease, concurrent exclusion, CAS completion, rotated token/header hydration, safe metadata updates, persisted bounded failure/backoff, HTTPS-only token endpoint, bounded form response, secret-free errors, and real HTTP rotation/revocation/malformed/oversized response contracts. The `scalaapi-oauth-refresh-20260809` empty-stack gate proves an expired seeded credential rotates to version 2 before a billable Chat dispatch and never appears in Admin details | Add provider-specific parameters, refresh audit history, multi-Silo contention, and real provider adapter fixtures |
| Scheduler benchmark dry run | 4/4, exit 0; no-match negative probe exits 1 | Dependency injection and child-result propagation pass; not performance evidence |
| Contract generation and digest | Canonical and Gateway vendor schemas match at the content-policy extension; fixed-scale pricing round-trip passed; official Cap'n Proto 1.0.2 commit `1a0e12c0` plus local `capnpc-csharp` 1.3.118 regenerated all three C# files byte-identically; an intentional drift probe exited 1 with a unified diff | Platform's single-repository generated-output gate is blocking; atomic cross-private-repository schema release coordination remains |
| PostgreSQL migrator | The current baseline is product migrations 001-046 plus the image-owned Orleans baseline (47 records); the `scalaapi-backup-0810b` empty PostgreSQL run applied and replay-skipped all 46 product migrations. Migration 043 widens only the greenfield classifier constraints to local/external/openai, 044-045 add cross-instance classifier metric snapshots and durable budget-alert state, and 046 adds backup/restore job state | The current empty-volume bootstrap evidence is authoritative; no source database, CDC, compatibility table, snapshot, or old key was used |
| Empty-volume Compose gate | `deploy/stack/smoke.sh` passed from Platform `e639b50`/Gateway `3da0d33` in unique project `scalaapi-fault-isolation-0810b`; the stack applied/skipped all 44 records, authenticated Garnet, waited for policy-change outbox propagation, queried policy-block and classifier-outage alerts, proved OpenAI match HTTP 400 and upstream-unavailable HTTP 503 with redacted audit, one normal settlement, and exact replay, settled/replayed Chat and realtime WebSocket, matched and redacted Unicode request content with no lease, withheld the first blocked response SSE event while retaining its unknown-charge hold, exercised restart/recovery and the complete Provider matrix including deterministic zero-output `disconnect_before_output -> 503`, resolved one incident, and persisted/downloaded the MinIO object | Source-owned gate proves ordered accounts, evidence-backed holds, exactly-once recovery including dispatch retry and outbox claim reclaim, versioned/redacted content-policy behavior, bounded external classifier faults, safe never-forwarded expiry, late usage settlement, actual client cancellation retention, serialized policy revision publication, audited alert evidence, and audited unknown-charge resolution. Hosted CI, runtime WebSocket soak, multi-instance ordering, production classifier, browser evidence, and cross-protocol automation remain |
| Garnet smoke | Auth, PING, SET/GET, PX, INCR, DEL passed | Official digest; no Redis or embedded server |
| Empty-volume Embeddings gate | `deploy/stack/smoke.sh` passed from Platform `ef1e474` in unique project `scalaapi-embeddings-20260809b` | The current source applied all 27 migrations and skipped all 27 on the second run, authenticated Garnet, settled two float and one base64 Embeddings request against the NUMERIC price version, retained one unknown-charge hold for a shape-invalid response, and passed the full restart, Provider, reconciliation, OAuth, realtime, and MinIO matrix |
| Garnet outage/recovery | Platform readiness 503 then 200 | Automatic TCP reconnect verified |
| Garnet projection rebuild | Platform `94e0db8`; existing auth rebuild returned `discovered=15`, `written=15`, `deleted=0`, `errors=0`; the result now also reports `policyRevision`, `policyInvalidationVersion`, and `policyRevisionWritten`. A dedicated PostgreSQL/Garnet run deleted `scalaapi:v1:content-policy:revision` and the invalidation key, rebuilt through production `RemoteGarnetService`, and read both restored values back; Gateway CTest covers auth version change and deleted-version flush/recovery | Multi-client outage ordering and partition recovery remain |
| Garnet TLS client trust | Platform `e5c341d`; `RemoteGarnetTlsTests` 3/3 | The production Platform RESP client loads an optional PEM CA trust anchor, builds a custom-root chain, preserves configured DNS server-name validation, rejects a trusted certificate with the wrong name, and rejects a CA path when TLS is disabled. The deployment-level server/mount evidence is recorded in the newer `scalaapi-garnet-tls-0810e` row |
| Provider mock | Health, OpenAI Chat/Responses/models/embeddings, Anthropic Messages/count-tokens/SSE, Gemini models/generation/SSE, synchronous media, asynchronous image/video tasks, deterministic OAuth form token rotation/revocation/malformed/oversized/timeout contracts, official-shaped OpenAI `/v1/moderations`, and a three-model decimal pricing catalog are source-owned; normalized Chat input selects deterministic faults and nine independent seed groups isolate scheduler state. Non-stream and streaming OpenAI 429/500 explicitly reject and release without debit; malformed success, timeout, and invalid streaming content type retain unknown-charge holds without retry. Direct non-stream and zero-output streaming resets return 503/provider_unavailable; partial SSE resets may end as 000/200/502/503 after output has begun, while all retain the hold. The `disconnect_after_usage` profile emits valid usage and closes before `[DONE]`, and the smoke proves one late settlement. Final Anthropic SSE settlement stored 32 input/5 output tokens and `0.00017100` cost | Gateway terminal-event, exact media-type, cancellation classification, normalized connection errors, incomplete chunked-body handling, bounded pre-header timeout response, independent inter-chunk/total timer tests, truncated-stream usage parsing, idempotent usage reconnect backoff, and shared bounded retry for HTTP/realtime Platform dispatch loss are in `9c7171f`; Platform `dcdca5e` Provider Mock HTTP contract tests cover OpenAI Moderation authentication/status/schema/bounds/timeout as well as rotation/revocation/malformed/oversized/timeout responses, while the `9320320` empty-stack gate proves expired credential rotation before dispatch, its secret-free audit row, and secret-free Admin reads. Source-owned protocol goldens and the pairwise request/response matrix are in Gateway `8f33790`; provider-specific fixtures, runtime WebSocket soak, real adapters, and remaining Gateway crash boundaries remain |
| External OAuth Provider mock smoke | Provider mock authorization codes are single-use and bind the configured client, exact redirect URI, and S256 verifier; `scalaapi-oauth-20260809b` passed start -> authorize -> callback, persisted `github` / `mock-oauth-user` account binding, returned the user session, and rejected a second callback with `oauth_state_replayed` (400) | Account-link collision policy, production redirect allowlists, and browser callback automation remain |
| Embeddings Provider contract | Source-owned mock and HTTP tests at Platform `ef1e474` | The mock returns one deterministic vector per input, honors requested dimensions and `float`/`base64` encoding, reports input usage, and exposes deterministic 429/500/malformed/shape-invalid profiles; Gateway `40cb02f` validates the response before settlement and retains an unknown-charge lease on malformed success. Provider-specific tokenizers, catalog fixtures, and live adapter evidence remain |
| Model catalog and token-count contracts | Gateway `6243b2d` CTest and Platform `d126ea5`/`d71fe8b` Provider mock tests | OpenAI model-list entries require unique IDs, positive creation timestamps, and owner metadata; Gemini list/detail entries require `models/` names, methods, and positive input/output token limits; Anthropic count-token responses require positive bounded `input_tokens`; the pricing catalog now has a separate bounded decimal source contract. Malformed, duplicate, zero, and invalid profiles fail closed before settlement; provider-specific catalog authority and tokenizer/golden fixtures remain |
| Protocol golden fixtures | Gateway `3da0d33` CTest, 125/125 | Versioned source-owned OpenAI Chat/Responses, Anthropic Messages, and Gemini request/response/SSE/error files are parsed, validated, usage/terminal events are checked, all sixteen request plus all sixteen response conversions are target-validated, and cross-protocol errors are normalized into target OpenAI/Anthropic/Gemini envelopes with status precedence while explicit same-format Provider errors pass through unchanged; Gateway-generated transport/protocol failures are translated to the inbound envelope; provider-specific catalogs/tokenizers, live adapters, and runtime E2E remain |
| Responses envelope contract | Gateway `b27965f` and `8f33790` CTest | Direct non-stream Responses success requires a completed response object, model/id metadata, non-empty typed output, and consistent positive usage; malformed payloads are mapped to `502/provider_protocol_error` with the charge retained for reconciliation, while the matching streaming event sequence is frozen in the Responses golden fixture |
| Gateway dispatch smoke | Seeded OpenAI Chat, Responses, models, embeddings, synchronous image, and asynchronous image/video requests returned success; independent Anthropic and Gemini groups returned 200 and settled against their own price versions; protocol-native JSON-on-stream injection returned bounded 503, four aborted leases/released holds, zero usage/debits, and no Photon overflow | Full runtime cross-protocol conversion/failure matrix and empty-stack automation for non-Chat protocols remain; source-level pairwise request/response and error goldens plus generated-error translation are now in `3da0d33` |
| Media lifecycle smoke | Image and video create calls returned durable `med_*` IDs; Platform polling copied provider bytes to MinIO, persisted `object_status=stored`, object key/ETag/size, and returned one-hour SigV4 URLs that downloaded `image/png` or `video/mp4`; batch `delete_outputs` returned 200, removed the object (old URL 404), cleared metadata, and terminal operation delete returned 204; migration 037 metadata-only reconciliation now verifies signed HEAD existence/size/ETag and restores a failed metadata row after the object returns; a signature failure remained retryable without settlement | Object listing/orphan cleanup, restore workflows, cancel/failure/restart, full MinIO restart, and batch create coverage remain |
| Billable settlement smoke | JSON, SSE, and realtime WebSocket completed; SQL-authoritative holds committed; usage outbox processed; one versioned NUMERIC ledger debit per successful lease; account balance equalled ledger sum, max version equalled account version, versions were contiguous/distinct, and projection backlog reached 0. Platform dispatch retry after process loss, before-provider-dispatch, after-outbox-claim, pre-settlement, post-settlement, and before-outbox-ack crashes plus Gateway before-provider-dispatch and after-provider-completion crashes were recovered by explicitly starting the same container; never-forwarded work expired safely at both dispatch boundaries, while forwarded ambiguity retained one reconciliation hold. Explicit non-stream and streaming 429/500 attempts released; malformed/disconnect/timeout-before-headers plus streaming disconnect, disconnect-before-output, malformed-usage, invalid-content-type, and downstream client-cancel attempts each retained one unknown hold. The truncated-stream profile settled valid usage exactly once before EOF; Admin settle replay returned `duplicate`; realtime settled exactly one lease/event/log/hold/debit; the latest gate ended with eight open incidents after nine were created. The separate 4-session/3-second realtime soak also passed with one complete financial effect per session | Longer load/backpressure soak, multi-instance recovery, and clean hosted CI image reproducibility remain |
| Ordered accounting and reconciliation | All current money effects use one per-user serialized store. Real-database tests prove 20 concurrent versions, replay/conflict, hold oversubscription, protected debit, final account/ledger equality, safe terminal-hold and projection repair, mismatch/unknown-charge incidents, retained unknown-charge hold/idempotency, late settlement, audited settle/release, same-key conflict, concurrent decision serialization, later incident resolution, and subscription quota reservation/settlement/release. A PostgreSQL advisory lock serializes scheduled and Admin runs; the pre-settlement fault smoke proves reconnect/outbox replay remains exactly-once | Add multi-Silo lock evidence, collector alert delivery, and all remaining deterministic crash hooks; subscription payment/renewal and affiliate effects must adopt the same authority contract |
| Administrative balance adjustment | New users started at zero; the first authenticated adjustment returned balance 1000 and ledger version 1, exact replay returned `duplicate=true`, changed replay returned 409, and an excessive debit returned 409. PostgreSQL held one `admin_adjustment` NUMERIC row and one `balance.adjust` actor audit. The real-database store test also covered an active hold that prevents a debit | Browser assertions and authorization matrix remain |
| Request idempotency smoke | Concurrent same-key calls produced one 200 and one active-lease 409; after settlement a matching retry returned the original body; different fingerprint produced 409. Platform dispatch retry, Platform and Gateway before-provider-dispatch loss safely reused or expired their original lease/key; Platform after-outbox-claim recovery reclaimed the completed event; forwarded/unknown outcomes stay blocked in `reconciliation_needed`; late completion and truncated-stream usage finalize the original key once. Platform pre/post/pre-ack and Gateway after-provider-completion crash replays each produced one terminal debit or one retained reconciliation hold; Gateway source tests also pin the shared HTTP/realtime retry identity policy | Streaming cancellation/partial output is non-retryable in Gateway source; runtime WebSocket replay/settlement and multi-instance replay remain |
| Price snapshot smoke | Lease persisted `runtime-v1` and NUMERIC input/output rates; changing the in-memory price to `runtime-v2` before completion left the original cost unchanged; Admin published/closed a version, rebuilt Platform loaded `stage2-live-1786199990`, mock embedding/image/video leases stored their active database versions and NUMERIC rates, and Platform `d71fe8b` replayed provider catalog history transactionally | Media-unit pricing, provider-specific pricing rules, tokenizer/golden fixtures, and multi-provider runtime evidence remain |
| Quota projection coherence | A low-quota key completed one current-image request; after settlement and projection rebuild, the next request returned `401 authentication_error` with `Quota exhausted` instead of using a stale Gateway auth cache | Subscription entitlements, grant lifecycle, and distributed concurrent reservation remain |
| Payment webhook state machine | Current source parser covers Stripe incremental `refund.created` and cumulative `charge.refunded` amounts plus provider refund IDs; the real PostgreSQL refund store test settles 6 + 4 as two independent NUMERIC effects and persists `partially_refunded -> refunded` with one order accumulator; webhook refund rows use the same provider/event idempotency key and ledger effect boundary | Full Admin HTTP webhook smoke still needs an Orleans-backed stack, plus additional provider adapters, browser completion, and exact SQL/cluster crash injection |
| Provider payment checkout, webhook, and refund command | Platform `94e0db8`; migrations 038-042 applied on an empty PostgreSQL 17 database and skipped on replay; Admin refund/provider/parser tests and real PostgreSQL replay/conflict/claim-reclaim/partial-settlement coverage pass, including distinct-key active-refund rejection; Host schema 1/1, Provider Mock refund contract passes, User Web typecheck/build remains green, and full Release suite 234/234 passed | `/user/payments/create` supports mock and Stripe providers, persists a pending order before the provider call, enforces exact idempotency payload replay/conflict, retries pending orders without a second order, and persists checkout URL plus Stripe payment intent ID. Stripe raw-body signatures enforce a 30-900 second timestamp window; checkout session, payment-intent success, and incremental/cumulative charge-refund events normalize to the common idempotent ledger path. `/admin/payments/{id}/refund` accepts any positive two-decimal amount within the remaining order balance, persists a command before Provider contact, retries ambiguous Provider transport with the same command key, rejects a competing unresolved refund, validates Stripe minor units, and settles one audited `payment_refund` effect per refund ID. Migration 041 adds `SKIP LOCKED` recovery with expiring claims; migration 042 accumulates order refunds and transitions through `partially_refunded` before `refunded`. Additional production adapters, browser payment completion, and exact-boundary crash evidence remain |
| Referral reward settlement | Platform `6344f88` replaces the direct Admin referral insert with a PostgreSQL transaction. The real `ReferralRewardStoreTests` empty-volume run passed 1/1: one active referral attribution, one `referral_bonus` NUMERIC ledger effect, updated code counters, actor/IP audit, exact replay, and changed-payload conflict; deterministic locks serialize both users | Signup referral-code attribution, anti-abuse limits, operator browser workflow, and commercial audit/export remain |
| Operational metrics and policy alerts | Platform `9848427` replaces raw metric inserts with an authenticated bounded command. Platform `0dd49cf` adds the authenticated Admin Web Operations route with typed metric summaries, kind/severity alert filters, and explicit refresh. The real `OpsMetricsStoreTests` empty-volume run passed 1/1, and the Admin Web Chromium suite passed 3/3 with summary, alert, filter, refresh, and Channel Monitor coverage | Collector rules, cross-service correlation, alert delivery/recovery, and live authorization workflows remain |
| Audit compliance and safe export | Platform `becf189` routes Admin reads through `AuditLogStore`, clamps normal/export pages, recursively redacts sensitive JSON fields, and removes generic client audit insertion. The real `AuditLogStoreTests` empty-volume run passed 1/1 with redaction and 1,000-row export bounds | Retention/immutability controls, authorization matrix, browser export, and security scanning remain |
| Proxy and TLS profile security | Platform `db770e2` routes Admin proxy/TLS CRUD through `NetworkProfileStore`. The real `NetworkProfileStoreTests` empty-volume run passed 1/1: proxy passwords are AES-GCM ciphertext, list views expose only `has_password`, password retain/clear works, invalid host/port and JA3 are rejected, and actor audits are counted | Provider-specific outbound adapters, actual TLS fingerprint application, secret rotation/retention, browser authorization, and security scanning remain |
| Channel monitor checks | Platform `326fc43` routes monitor checks through `ChannelMonitorStore`. Platform `4f78b71` adds the authenticated Admin Web history/filter/check workflow; the real `ChannelMonitorStoreTests` empty-volume run passed 1/1 and the Admin Web Chromium suite passed 3/3. Active-account checks succeed, invalid latency and missing accounts are rejected, listing is bounded, and one actor audit accompanies each accepted row | Scheduled runners, monitor templates/history, feedback notifications, and live authorization remain |
| Admin settlement queries | Ledger, lease, and hold endpoints returned current PostgreSQL rows with user filters | Pagination/export and browser assertions remain |
| Reconciliation incident lifecycle | A real PostgreSQL test deliberately corrupted one account, one terminal hold, and one Grain projection while creating an additional unknown-charge lease. The Admin API settled one incident with actor/reason/evidence, replayed the same command, and the next run preserved its resolved state. The latest full-stack fault gate persisted nine unknown-charge incidents; Platform and Gateway before-provider-dispatch loss safely expired outside the incident set, Platform dispatch retry recovered its active lease without an incident, Platform after-outbox-claim recovery reclaimed its completed event without an incident, Gateway after-provider-completion loss retained one incident, and one audited decision left eight open incidents. The 4-session realtime soak added concurrent terminal billing evidence | Add collector alert delivery, multi-Silo concurrency, and remaining crash hooks |
| Auth lifecycle smoke | `scalaapi-auth-session-verified` passed login -> refresh rotation, rejected old refresh-token replay with 401, rejected the replaced access token, accepted the replacement, and rejected it after `/user/logout`; the empty-volume gate also completed the full billing/fault matrix | Multi-device session management, browser UX, session audit/retention, and hosted-CI evidence remain |
| TOTP lifecycle | Real PostgreSQL Admin tests enable TOTP, reject same-time-step replay, consume one backup code exactly once, reject backup codes for disable, lock after five failures across two service instances, and recover after the 15-minute lockout; User Web builds the setup/verify/disable and backup-code display workflow | Browser setup and backup-code sign-in tests remain |
| OAuth PKCE and external exchange | Real PostgreSQL Admin tests issue GitHub/Google state, reject provider/redirect/verifier mismatches, consume accepted state once, persist `consumed_at`, and reject expiry after ten minutes. The source-owned Provider mock authorization endpoint persists one-time codes bound to client, redirect URI, and S256 verifier; `scalaapi-oauth-20260809b` drives start -> authorize -> callback, verifies the bound `oauth-user@example.test` identity, and rejects state replay with HTTP 400 | Account-link collision policy, production redirect allowlists, and browser callback tests remain |
| User portal contract | User Web uses refresh-aware `/auth`, password recovery and email verification, scoped `/user/usage` and `/user/usage/balance`, API key, payment, active-plan subscription, redeem, referral, profile, password, and TOTP contracts; repository test proves usage rows cannot cross user boundaries; source-built `scalaapi-user-portal-0810b` proves password login and Dashboard/Usage/API-key/Profile navigation; Admin referral rewards now have atomic audited settlement in Platform `6344f88` | Key/profile mutations, backup-code sign-in, recovery-mail receipt, real payment provider checkout, signup referral attribution, and commercial audit evidence remain |
| Redeem-code settlement smoke | First redemption returned 200; repeat returned 409; after a Silo contract restart a committed redemption remained 409 and replayed its balance effect; one redemption and one NUMERIC ledger row were observed | Concurrent HTTP contention and audit-event assertions remain |
| Provider failover idempotency | Matching external idempotency keys reopen only after explicit Provider rejection or never-forwarded expiry. Active/completed keys retain replay/conflict semantics, while forwarded transport/protocol ambiguity stays blocked in `reconciliation_needed`; Host coverage and the stack fault matrix passed | Cross-protocol failover response replay remains |
| Password recovery | Explicit local debug mode issued a one-time token; first confirmation returned 204, token replay returned 400, and new-password login succeeded; migration 034 now queues only encrypted token material, retries delivery, and User Web consumes the action link | Live SMTP/provider delivery, browser mail receipt, delivery metrics, and public abuse limits remain |
| Email verification | Explicit local debug mode issued a one-time token; first confirmation succeeded, replay returned 400, PostgreSQL persisted `email_verified=true` with a timestamp, migration 034 queues encrypted notification material, and User Web consumes the action link | Live SMTP/provider delivery, browser mail receipt, delivery metrics, and public abuse limits remain |
| Self-service account lifecycle | Fresh user registered; profile read/update returned 200/204, password change returned 204, old password and revoked refresh token returned 401, new password login succeeded, and DELETE `/user/account` returned 204; database retained `deleted` account and three revoked sessions | Concurrent session tests, API-key revocation fixture, retention policy, and browser coverage remain |
| Subscription lifecycle | Plan creation returned a generated identity; purchase returned `active`, exact replay returned `duplicate=true`, a second active purchase returned conflict, cancel and renew each replayed idempotently, and forcing expiry made the next listing `expired`; PostgreSQL held one event for each transition | Payment-provider coupling, quota consumption/grant application to API keys, renewal worker, and browser coverage remain |

## Remaining release gates

1. Coordinate canonical Platform schema changes, generated C# artifacts, Gateway
   vendor schemas, and both digest gates as one cross-repository release change.
2. Run the checked-in empty-volume Compose gate in hosted CI and record current image
   IDs and checksums. The two private repositories require a dedicated read token or
   an independent release repository; the default per-repository `GITHUB_TOKEN`
   cannot check out its sibling.
3. Extend the now-passing Garnet TLS deployment gate with cache flush and
   stale-version recovery while TLS is enabled, and concurrent Gateway/Platform
   clients across outage/restart and partition scenarios. Certificate rotation,
   wrong-name/expiry rejection, and post-recovery billing are source-smoke proven.
   No Redis process, package, image, CLI, or embedded fallback may appear in the
   stack.
4. Extend the now-normalized `503/provider_unavailable` Provider reset contract to
   replay after process failure across adapters. The distinct inter-chunk/total timer
   contract, actual downstream client socket cancellation, and usage-before-EOF
   settlement are covered by the latest empty-stack gate.
   The hook implementation and Platform pre-, post-, and pre-ack settlement
   recovery are source-smoke proven; Gateway before-provider-dispatch safe expiry
   and after-provider-completion recovery are also covered by the latest smoke.
   Platform dispatch retry and active-lease recovery are proven for regular Chat,
   and the shared retry policy covers realtime at source level. The 4-session,
   3-second runtime WebSocket soak now passes; exercise longer backpressure/load,
   other Gateway crash boundaries, and outbox-acknowledgement
   boundaries. Require each restart outcome to settle, safely
   release, or remain a durable reconciliation incident without redispatch. The
   current audited operator settle/release path must be exercised at those boundaries
   rather than bypassed.
5. Automate the passing Anthropic and Gemini provider-group scenarios in the empty
   stack, add protocol-specific error/disconnect/golden fixtures, then extend the
   S3-compatible media path with cancellation, restart, restore, and metadata/object
   reconciliation assertions.
6. Run auth-session integration scenarios: refresh-token replay, concurrent rotation,
   logout revocation, expired-session rejection, and multi-device session listing.
7. Run unit, integration, UI, load/soak, failure-recovery, backup/restore, and
   security checks. Any failed child scenario or benchmark makes CI non-zero.

## Evidence rules

Healthy old containers, old databases, route presence, table presence, and swallowed
benchmark failures are not release evidence. Every result must record the current
commit or worktree snapshot, image digests, environment shape, and test exit code.
