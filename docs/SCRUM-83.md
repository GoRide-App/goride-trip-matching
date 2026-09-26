# SCRUM-83: prevent invalid ride starts

Development work: SCRUM-516 (state checks), SCRUM-517 (validation and errors),
and SCRUM-518 (parameterised ADO.NET access).

## API

Use the existing `POST /matching/offers/{tripId}/status` endpoint:

```json
{"driverId":"driver-id","action":"InProgress"}
```

The lifecycle remains `Accepted -> Arrived -> InProgress -> Completed`.
Trip and driver IDs are required and limited to 64 characters, matching the
database columns. Actions are case-sensitive: `Arrived`, `InProgress`, or
`Completed`. IDs remain opaque strings; a UUID-only format is not required.

| Result | HTTP response |
| --- | --- |
| Valid start | 200 with the updated offer and UTC `startedAt` |
| Missing/malformed body, missing or overlong ID, unsupported action | 400 with ASP.NET validation problem details and field errors |
| No offer for this trip and driver | 404, code `TRIP_NOT_FOUND` |
| Invalid prior state, wrong/missing claim, inconsistent timestamps, or repeat start | 409, code `INVALID_TRIP_TRANSITION` |
| Database failure or timeout | 503, code `OFFERS_STORE_UNAVAILABLE` |

Business/store errors use problem details with `status`, `title`, `code`, and
the existing `error` property for client compatibility. Database exception
details are logged only on the server.

## State and database rules

- Starting requires `Arrived`, an existing arrival timestamp, no start/completion
  timestamp, and a `trip_claims` row owned by this driver.
- Pending, Accepted, Declined, Expired, Failed, Cancelled, InProgress, Completed,
  and unknown states cannot start. A completed timestamp also blocks a start
  when the status is inconsistent.
- The repository independently rejects skipped, backward, and unknown steps.
- The conditional update checks the trip, driver, claim, prior state, and
  timestamps together. All request values use SQL parameters. Timestamp column
  names and guards come only from fixed application mappings.
- The update and read share one transaction. Concurrent starts have one winner;
  rejected/repeated requests preserve timestamps. A failed read rolls back.
- The rider sees the committed status through the existing
  `GET /matching/ride-requests/{tripId}` polling flow. Rejected starts produce no
  trip event or rider notification.

No schema change is needed. The existing SCRUM-82 timestamp columns and SCRUM-62
`trip_claims` table must already be present. Legacy offers with missing claims or
inconsistent timestamps return 409 and need their data corrected before advancing.
Driver identity continues to use the service's existing `driverId` API contract.

## Verification

Run service, repository-validation, and HTTP tests:

```powershell
rtk proxy dotnet test GoRide.Trip.sln -c Release
```

The real MySQL tests are opt-in. Point `SCRUM83_MYSQL` at a **disposable test
server** whose account can create/drop databases. Tests create a unique
`scrum83_<guid>` database and remove only that database afterward. They cover the
full lifecycle, invalid states, ownership, inconsistent timestamps, SQL
metacharacters in IDs, and eight concurrent starts.

Example using a temporary container bound only to localhost:

```powershell
rtk proxy docker run --detach --rm --name goride-scrum83-mysql -p 127.0.0.1:33383:3306 -e MYSQL_ALLOW_EMPTY_PASSWORD=yes mysql:8.4
# Wait until MySQL reports it is ready for connections.
$env:SCRUM83_MYSQL = 'Server=127.0.0.1;Port=33383;User ID=root;SSL Mode=None'
rtk proxy dotnet test GoRide.Trip.sln -c Release
Remove-Item Env:SCRUM83_MYSQL
rtk proxy docker stop goride-scrum83-mysql
```

Without that environment variable, database tests are explicitly skipped; the
remaining tests need neither MySQL nor Kafka.
