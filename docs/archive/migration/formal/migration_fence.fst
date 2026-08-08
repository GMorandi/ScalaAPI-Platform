module MigrationFence

open FStar.Pervasives

// This is the finite-state model for the runtime MigrationFenceStore.
// It deliberately models control-plane safety, not CDC throughput or SQL cost.

type primary =
  | Sub2Api
  | Platform

type mode =
  | LegacyPrimary
  | TargetCanary
  | TargetPrimary
  | LegacyReadOnly

type fence = {
  epoch: nat;
  write_primary: primary;
  mode: mode
}

type readiness = {
  snapshot_completed: bool;
  outstanding_messages: nat;
  unreplayed_dead_letters: nat
}

let mode_primary_consistent (p:primary) (m:mode) : bool =
  match (p, m) with
  | (Sub2Api, LegacyPrimary) -> true
  | (Sub2Api, LegacyReadOnly) -> true
  | (Platform, TargetCanary) -> true
  | (Platform, TargetPrimary) -> true
  | _ -> false

let legal_transition (current:mode) (next:mode) : bool =
  match (current, next) with
  | (LegacyPrimary, TargetCanary) -> true
  | (TargetCanary, TargetPrimary) -> true
  | (TargetCanary, LegacyPrimary) -> true
  | (TargetPrimary, LegacyReadOnly) -> true
  | (LegacyReadOnly, TargetPrimary) -> true
  | (LegacyReadOnly, LegacyPrimary) -> true
  | _ -> false

let target_primary_ready (r:readiness) : bool =
  r.snapshot_completed
  && r.outstanding_messages = 0
  && r.unreplayed_dead_letters = 0

let transition_allowed (current:fence) (next_primary:primary)
  (next_mode:mode) (r:readiness) : bool =
  mode_primary_consistent current.write_primary current.mode
  && mode_primary_consistent next_primary next_mode
  && legal_transition current.mode next_mode
  && not (current.write_primary = next_primary && current.mode = next_mode)
  && (not (next_mode = TargetPrimary) || target_primary_ready r)

let writes_enabled (f:fence) : bool =
  f.write_primary = Platform && f.mode = TargetPrimary

// The runtime applies this decision inside a PostgreSQL row-locking transaction.
// None represents a rejected transition; Some is the next committed fence.
let transition (current:fence) (next_primary:primary) (next_mode:mode)
  (r:readiness) : option fence =
  if transition_allowed current next_primary next_mode r then
    Some { epoch = current.epoch + 1;
          write_primary = next_primary;
          mode = next_mode }
  else
    None

val transition_preserves_consistency:
  current:fence -> next_primary:primary -> next_mode:mode -> r:readiness ->
  Lemma (match transition current next_primary next_mode r with
         | Some next -> mode_primary_consistent next.write_primary next.mode
         | None -> true)

let transition_preserves_consistency current next_primary next_mode r = ()

val transition_increments_epoch:
  current:fence -> next_primary:primary -> next_mode:mode -> r:readiness ->
  Lemma (match transition current next_primary next_mode r with
         | Some next -> next.epoch = current.epoch + 1
         | None -> true)

let transition_increments_epoch current next_primary next_mode r = ()

val target_primary_requires_readiness:
  current:fence -> r:readiness ->
  Lemma (match transition current Platform TargetPrimary r with
         | Some _ -> target_primary_ready r
         | None -> true)

let target_primary_requires_readiness current r = ()

val accepted_target_is_the_only_write_state:
  current:fence -> next_primary:primary -> next_mode:mode -> r:readiness ->
  Lemma (match transition current next_primary next_mode r with
         | Some next -> writes_enabled next =
             (next.write_primary = Platform && next.mode = TargetPrimary)
         | None -> true)

let accepted_target_is_the_only_write_state current next_primary next_mode r = ()
