# Claims Workspace

A local insurance claims demo. Claimants report incidents, read their claim history and respond to questions. Officers take ownership of claims, request information, record decisions and see the team's workload. The same Angular app serves both roles.

## Run locally

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [Node.js 24](https://nodejs.org/). Start the API and frontend in separate terminals from the repository root.

```sh
dotnet run --project backend
```

```sh
cd frontend
npm ci
npm start
```

Open [http://127.0.0.1:4200](http://127.0.0.1:4200). The Angular dev server forwards `/api` to `127.0.0.1:5080`, so the browser uses one origin for the demo session cookie. The database is created on first run. The default is `data/claims.db` under the API content root.

| Account | Role |
| --- | --- |
| `claimant-a` or `claimant-b` | Claimant |
| `officer-a` or `officer-b` | Claims officer |

All four synthetic accounts use `claims-demo`. Keep this demo on loopback and use synthetic claim information. The shared demo password and local HTTP are scoped to this assessment. Session keys are stored in the API's ignored `data/session-keys` directory.

## Walkthrough

1. Sign in as `claimant-a`, report a motor or property incident and note its claim reference.
2. Sign out, sign in as `officer-a`, open the same claim and select **Assign to me**. Enter a question and select **Request information**.
3. Return as `claimant-a` and reply to the request. The claim goes back to **In review** and keeps the same officer.
4. Return as `officer-a` and settle or reject the claim with a reason. Check the workload counts and the reduction in open reported loss.
5. Return as the claimant to see the decision and history. Use **Refresh** to see changes from another browser session.

## Architecture

One ASP.NET Core service owns the claim, permissions and status transitions. The officer who takes a claim keeps ownership through the information loop. Only that officer can request information or record a decision. A claimant can only see or respond to their own claims. Requests that act on an older claim version return `409 Conflict` so concurrent reviews cannot overwrite each other.

SQLite keeps setup small and gives the claim, history and pending Kafka event a shared transaction. A status change is visible to the UI after that transaction commits. The workload is computed from the current claims. **Open reported loss** sums the claimant-reported MYR loss on open claims. Actual reserves and payout amounts are outside this model.

REST serves the actions and reads for which the UI needs a direct answer. Kafka carries `claims.status-changed.v1` for asynchronous consumers. Each event includes an event ID, claim ID, claim version, previous and current status, and an occurrence time. The claim ID is the Kafka key. The payload omits incident descriptions and claim notes. The background publisher removes an event from the outbox after Kafka acknowledges it. A restart between acknowledgement and removal can cause redelivery, so consumers should deduplicate by `eventId`.

The app uses a single API instance with one outbox publisher. The sprint scope covers one currency, one assignee per claim, text-based information requests and basic workload counts. Attachments, external notifications, multiple markets and financial settlement processing are outside that scope.

### REST endpoints

| Method and path | Use |
| --- | --- |
| `GET /api/health` | Check whether the API is running |
| `POST /api/session` | Sign in with `account` and `password` |
| `GET /api/session`, `DELETE /api/session` | Get the signed-in account or sign out |
| `POST /api/claims` | Submit `category`, `incidentDate` (`YYYY-MM-DD`), `description` and `reportedLoss` (MYR) |
| `GET /api/claims`, `GET /api/claims/{id}` | List visible claims or read one claim and its history |
| `POST /api/claims/{id}/assignment` | Officer assigns a new claim to themself with `expectedVersion` |
| `POST /api/claims/{id}/information-request` | Assigned officer sends `expectedVersion` and a `note` |
| `POST /api/claims/{id}/information-response` | Claimant replies with `expectedVersion` and a `note` |
| `POST /api/claims/{id}/decision` | Assigned officer sends `expectedVersion`, `status` (`settled` or `rejected`) and a `note` |
| `GET /api/workload` | Get counts by status and open reported loss for the team |

Claim responses use integer minor units for amounts such as `reportedLossMinor`. The claim UUID is the API identifier. The frontend displays a shortened reference for scanning the queue.

## Run with Kafka

The REST flow works without a broker. Pending events stay in SQLite until you configure the publisher. With Docker running, start the broker and restart the API with `Kafka__BootstrapServers` set.

```powershell
docker compose up -d kafka
$env:Kafka__BootstrapServers = '127.0.0.1:9092'
dotnet run --project backend
```

On macOS or Linux, set `Kafka__BootstrapServers=127.0.0.1:9092` on the `dotnet run` command. Submit or change a claim before reading the topic. Then inspect published events in another terminal.

```sh
docker compose exec kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --topic claims.status-changed.v1 --from-beginning --property print.key=true
```

Use `Ctrl+C` to stop the consumer and `docker compose down` to stop the broker. The named broker volume keeps the event log between runs.

## Verify the app

Run the smoke check against a local API from the repository root. It signs in through HTTP, creates two synthetic claims, tests ownership and competing updates, and completes both a settlement and a rejection. Set `Claims__DatabasePath` to a separate file in the API terminal before starting it if you want a separate test dataset.

```sh
node scripts/smoke.mjs
dotnet build backend
npm --prefix frontend run build
```

See [the AI working journal](AI-JOURNAL.md) for decisions and checks made during development.
