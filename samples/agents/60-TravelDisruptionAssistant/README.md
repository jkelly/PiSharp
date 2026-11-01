# Travel disruption assistant

Native tools inspect fixture schedules, reject infeasible routes, rank by integer-cent cost then UTC arrival, and save `travel-trip-1.json`. Checks include offsets, route continuity, minimum connections, disrupted services, mode/refund policy, departure/arrival windows and budget.

Run `dotnet run --project samples/agents/60-TravelDisruptionAssistant -- --demo` or `--self-test`. The demo saves a proposal; adding `--approve` runs a fourth native tool call to simulate the cheapest feasible selection locally. `rebook_local` requires the reviewed proposal hash and a feasible alternative ID. It writes one idempotent `local-rebooking-ID.json` simulation; a changed proposal or conflicting repeat is rejected. No airline, booking system or payment service is contacted. Availability and prices are fixture inputs, not live quotes.
