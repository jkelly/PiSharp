# Calendar Scheduling Coordinator

Find a shared meeting time from explicit local availability, busy intervals and time zones. The fixture's `meeting-a` needs 30 minutes for Alex (Chicago) and Sam (New York). Its first shared slot is **2026-11-02 15:30 UTC**. Windows require UTC `Z` timestamps; local calendar times are resolved using each person's time zone, rejecting ambiguous or nonexistent DST times.

From the repository root:

```powershell
dotnet run --project samples/agents/33-CalendarSchedulingCoordinator -- --demo
dotnet run --project samples/agents/33-CalendarSchedulingCoordinator -- --demo --approve
dotnet run --project samples/agents/33-CalendarSchedulingCoordinator -- --self-test
```

`inspect_calendars` validates the requested participants, `find_shared_slots` scans 15-minute candidate starts and checks whole-interval coverage/overlap, and `save_proposal` writes `proposal-meeting-a.json` in the selected workspace. Candidate starts are anchored to the requested UTC window start. Up to five slots include each participant's local display time. Empty results explicitly report `no-shared-slot`.

With `--approve`, `book_local_meeting` records the earliest slot in `booking-meeting-a.json`. This is a **workspace-only booking**, with no invitations or remote calendar updates. Both host and callback enforce approval. Repeated calls with the same ID and payload return the same receipt; changed payloads fail. Other local booking files block conflicting participant slots. Use a dedicated workspace for this sequential demonstration; it is not a multi-process calendar service.

Use `--input FILE --workspace DIR` for another fixture and `--live --model MODEL` for native provider orchestration. Input requires `requestId`, uniquely identified `requests` and `people`, named time zones, explicit availability/busy arrays and a positive meeting window no longer than seven days. The fixture `task` requests the same concrete ID as the deterministic demo.

Self-tests exercise native three/four-stage execution, timezone and DST behavior, missing/ambiguous IDs, offsetless UTC rejection, second request freshness, overlapping local bookings, callback approval and replay conflicts.
