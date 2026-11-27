------------------------------- MODULE SyncLock -------------------------------
(***************************************************************************)
(* INV-09: at most one sync per tenant runs at a time, and the sync cursor *)
(* advances only after the fetched page set is durably committed.          *)
(*                                                                         *)
(* Models GuardedSyncService (database advisory lock held for the whole    *)
(* sync) plus the cursor store. Processes are the API, the webhook job     *)
(* and the Worker. A process may crash at any step; the advisory lock is   *)
(* session scoped, so a crashed process releases it. A retry is simply a   *)
(* later iteration of the same process loop.                               *)
(***************************************************************************)
EXTENDS Naturals, FiniteSets

CONSTANTS Procs,       \* the processes that can run a sync, e.g. {p1, p2, p3}
          MaxCursor    \* bound on the number of commits explored

VARIABLES pc,          \* process -> "idle" | "locked" | "fetched" | "committed"
          lockHolder,  \* "none" or a process
          cursor,      \* the durable cursor
          staged,      \* process -> cursor value the process would commit (0 = nothing fetched)
          commits      \* number of durable commits so far

vars == <<pc, lockHolder, cursor, staged, commits>>

Init == /\ pc = [p \in Procs |-> "idle"]
        /\ lockHolder = "none"
        /\ cursor = 0
        /\ staged = [p \in Procs |-> 0]
        /\ commits = 0

\* pg_try_advisory_lock: acquire or give up (the service throws SyncAlreadyRunningException).
TryLock(p) == /\ pc[p] = "idle"
              /\ lockHolder = "none"
              /\ lockHolder' = p
              /\ pc' = [pc EXCEPT ![p] = "locked"]
              /\ UNCHANGED <<cursor, staged, commits>>

\* Lock not available: skipped, never queued; nothing changes.
Skip(p) == /\ pc[p] = "idle"
           /\ lockHolder # "none"
           /\ UNCHANGED vars

\* The provider is called and pages are staged in memory. Nothing durable yet.
Fetch(p) == /\ pc[p] = "locked"
            /\ staged' = [staged EXCEPT ![p] = cursor + 1]
            /\ pc' = [pc EXCEPT ![p] = "fetched"]
            /\ UNCHANGED <<lockHolder, cursor, commits>>

\* One transaction writes the rows and the new cursor together: both or neither.
Commit(p) == /\ pc[p] = "fetched"
             /\ commits < MaxCursor
             /\ cursor' = staged[p]
             /\ commits' = commits + 1
             /\ pc' = [pc EXCEPT ![p] = "committed"]
             /\ UNCHANGED <<lockHolder, staged>>

\* The lease is disposed in the finally block.
Release(p) == /\ pc[p] = "committed"
              /\ lockHolder = p
              /\ lockHolder' = "none"
              /\ pc' = [pc EXCEPT ![p] = "idle"]
              /\ staged' = [staged EXCEPT ![p] = 0]
              /\ UNCHANGED <<cursor, commits>>

\* A sync that throws after the lock (provider error, throttle, cancellation) releases it without committing.
Fail(p) == /\ pc[p] \in {"locked", "fetched"}
           /\ lockHolder = p
           /\ lockHolder' = "none"
           /\ pc' = [pc EXCEPT ![p] = "idle"]
           /\ staged' = [staged EXCEPT ![p] = 0]
           /\ UNCHANGED <<cursor, commits>>

\* The process dies at any point: the session ends, the advisory lock is dropped, staged data is lost.
Crash(p) == /\ pc[p] # "idle"
            /\ lockHolder' = IF lockHolder = p THEN "none" ELSE lockHolder
            /\ pc' = [pc EXCEPT ![p] = "idle"]
            /\ staged' = [staged EXCEPT ![p] = 0]
            /\ UNCHANGED <<cursor, commits>>

Next == \E p \in Procs : TryLock(p) \/ Skip(p) \/ Fetch(p) \/ Commit(p) \/ Release(p) \/ Fail(p) \/ Crash(p)

Spec == Init /\ [][Next]_vars

-----------------------------------------------------------------------------
TypeOK == /\ pc \in [Procs -> {"idle", "locked", "fetched", "committed"}]
          /\ lockHolder \in Procs \cup {"none"}
          /\ cursor \in Nat
          /\ staged \in [Procs -> Nat]
          /\ commits \in Nat

\* INV-09a: at most one process is past the lock for this tenant.
MutualExclusion == Cardinality({p \in Procs : pc[p] # "idle"}) <= 1

\* The holder, and only the holder, is past the lock.
LockMatchesPhase == \A p \in Procs : (pc[p] # "idle") => lockHolder = p

\* INV-09b: the cursor equals the number of durable commits, so it never moves without one,
\* and a staged-but-uncommitted page set never shows through the cursor.
CursorOnlyAfterCommit == cursor = commits

\* The cursor never goes backwards.
CursorMonotonic == [][cursor' >= cursor]_vars

=============================================================================
