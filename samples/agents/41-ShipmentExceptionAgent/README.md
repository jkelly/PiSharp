# Shipment Exception Agent

Three tools sort a selected shipment's events, calculate delivery exceptions, and save grounded customer/carrier drafts at `shipments/<shipmentId>.json`. At the fixture clock, `SHIP-501` is 18 hours overdue and its latest scan is 52 hours old, exceeding the 24-hour stall threshold.

```powershell
dotnet run --project samples/agents/41-ShipmentExceptionAgent -- --demo
dotnet run --project samples/agents/41-ShipmentExceptionAgent -- --self-test
```

Events are ordered by occurrence timestamp rather than input order. Duplicate event IDs, future events, and simultaneous conflicting statuses are rejected. Delivered shipments retain historical overdue hours but do not create an active stall alert. An empty tracking history remains explicitly unknown and requests attention. Each result includes the latest event ID and the full evidence ID set.

Drafts contain observed status, location, event time and promised delivery, while stating that no revised ETA is confirmed. Saving is a report operation, needs no approval, and is deterministic for unchanged inputs. `sent` remains false: the sample sends no notifications and does not call a carrier API. Live model mode also uses local fixtures. A real integration would need carrier authentication, event deduplication/versioning, timezone normalization, authoritative ETA retrieval, and separately authorized outbound messaging.
