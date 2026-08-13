# G0 Data Mapping Matrix

| Sub2API source | Target authority | Target key | Core mapping and consistency |
| --- | --- | --- | --- |
| `users` | `IUserGrain` + user projection | numeric `id` | role/status/concurrency/RPM/allowed groups; balance changes must also be represented by ledger/hold events; synchronous |
| `api_keys` via `migration_cdc_outbox` | `IApiKeyGrain` + Garnet auth projection | SHA-256 key hash; source `id` retained as `api_key_id` | user/group, status, quota, expiry and rate windows; rotation revokes the old hash before creating the new hash; plaintext and deletion tombstones excluded |
| `groups` | `IGroupGrain` | numeric `id` | platform, routing, membership, limits and status; synchronous |
| `accounts` | `IAccountGrain` | numeric `id` | scheduling metadata through normal CDC; credentials only through restricted encrypted channel; synchronous for selection safety |
| `account_groups` | group/account semantic projection | `(account_id, group_id)` | membership and priority; apply after referenced aggregates exist |
| `scheduler_outbox` | scheduler semantic handler | source outbox id | source semantic scheduling invalidation; asynchronous but ordered by source LSN |
| `auth_cache_invalidation_outbox` | Garnet invalidation publisher | source outbox id | async cache invalidation; cache can be rebuilt from grains |
| `usage_logs` | `RequestLeaseStore`/usage projection | request/lease id | token counts and cost; asynchronous, idempotent by lease/request |
| balance hold/settlement events | `IUserGrain` + `balance_holds`/`settlement_effects` | hold/lease id | amount is decimal text/NUMERIC(20,8), one effect per `(lease,effect_type)`; synchronous acknowledgement |

### Identity and deletion

Source numeric IDs are retained; no target-side remapping is allowed during G0.
Deletes are explicit tombstone events and map to Grain `Delete`/`Revoke` only
after the event has been fenced to the current epoch. Soft-delete columns remain
in the source mapping and are not converted to a physical delete without a
separate retention decision.

### Schema and secret rules

Money uses PostgreSQL `NUMERIC(20,8)` (or a decimal string in an event), never a
binary floating-point value in the CDC contract. Password hashes, upstream
credentials, account `extra`, OAuth refresh tokens, proxy passwords and TOTP
secrets are excluded from ordinary CDC. Usage client IP and user-agent fields
are also excluded because they are not needed to rebuild the billing projection.
