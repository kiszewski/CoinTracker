# CoinTracker Technical Plan

Status: Proposed  
System specification: [`spec.md`](spec.md)  
Implementation tasks: [`tasks.md`](tasks.md)
User-preferences plan: [`../user-preferences/plan.md`](../user-preferences/plan.md)

## 1. Purpose

This document translates the system specification into technical decisions and an implementation approach. Product behavior belongs in `spec.md`; actionable work belongs in `tasks.md`.

## 2. Technical decisions

### TD-01 — Modular monolith first

Keep one deployable .NET application for the first release, divided into Data Population, Report Analysis, Delivery, and shared messaging modules.

Business workflows communicate through versioned event contracts; components must not call another component's concrete application services. Explicit read-only data ports may expose the small cross-module projections needed for decisions such as report eligibility, without transferring application-service ownership. The user-threshold read path is defined in [`../user-preferences/plan.md`](../user-preferences/plan.md).

This keeps deployment simple while allowing a component to become an independent service later.

### TD-02 — RabbitMQ is the event transport

Use RabbitMQ to publish and consume system events.

- Use a durable topic exchange named `cointracker.events`.
- Publish persistent messages.
- Use publisher confirms.
- Acknowledge a message only after its handler succeeds.
- Give each consuming component its own durable queue.
- Send messages that exhaust their retries to a dead-letter exchange.

Initial routing:

- `market-data.populated.v1` → Report Analysis queue.
- `report.created.v1` → Delivery queue.

MongoDB stores application data. It is not the event transport.

For local development, Docker Compose runs the .NET application and RabbitMQ together. RabbitMQ must expose its AMQP port to the application and may expose its management interface for local inspection.

The local MongoDB instance must run as a single-node replica set because the application uses MongoDB transactions.

Start the local stack with `docker compose up --build`. Optional provider and email credentials can be supplied through a root `.env` file using `GECKO_API_KEY`, `MAILJET_API_KEY`, `SENDER_EMAIL`, and `SENDER_EMAIL_PASSWORD`. The `.env` file is excluded from the Docker build context. Local readiness reports whether both MongoDB/RabbitMQ and the provider/email configuration are available.

Production uses a managed CloudAMQP instance. The application connects through an `amqps://` connection string supplied through production secret storage. The connection string and credentials must never be committed or logged.

Production configuration uses the `Mongo__ConnectionString`, `RabbitMq__ConnectionString`, `MarketData__ApiKey`, and `Email__*` environment variables or equivalent secret-manager values.

### TD-03 — Reliable publication through an outbox

Writing application data and publishing to RabbitMQ cannot be one transaction. Use a MongoDB transaction for application records and their outbox event, then use the outbox as a publication bridge:

1. In one MongoDB transaction, save the business record and outgoing event together.
2. A publisher reads unpublished outbox records.
3. The publisher sends them to RabbitMQ using publisher confirms.
4. Confirmed records are marked as published.
5. A MongoDB TTL index removes published records five days after publication. Unpublished records have no expiry and are never removed by this retention policy.

Business consumers receive events only from RabbitMQ; they do not poll MongoDB for events.

### TD-04 — At-least-once processing

RabbitMQ and the outbox may deliver an event more than once. Every consumer must therefore be idempotent.

Use these logical uniqueness rules:

- Market snapshot: provider, external asset ID, and observation time.
- Report: rule name/version, coin, and latest snapshot ID used to create the report.
- Delivery: report, recipient, and channel.

The report key prevents an older or duplicate population event from creating another report for the same latest snapshot. MongoDB transactions must atomically record a report and its `report.created.v1` outbox event.

### TD-05 — MongoDB remains the application database

Continue using MongoDB for ingestion runs, market snapshots, user profiles and preferences, reports and qualified-audience snapshots, delivery records, processed-message IDs, and the publication outbox. Do not seed or migrate the former hard-coded recipient. User profile and Discord delivery details are defined in [`../user-preferences/plan.md`](../user-preferences/plan.md).

Store market observations as separate snapshot records instead of allowing arrays inside coin documents to grow indefinitely.

### TD-06 — Delivery channels use a common interface

Define `IDeliveryChannel` with a stable channel name and an asynchronous send operation. V1 implements email and Discord incoming webhooks. Channel-specific configuration and behavior are defined in [`../user-preferences/plan.md`](../user-preferences/plan.md).

Keep email rendering separate from SMTP transport:

- A template renderer converts a report into email content.
- An email sender adapter communicates with Mailjet.
- The email channel coordinates those two operations.

### TD-07 — Hosted workers perform background work

Use awaited .NET `BackgroundService` implementations for scheduled population, outbox publication, RabbitMQ consumers, and delivery execution. Configure population in whole minutes with a 15-minute default interval.

Every worker must support cancellation and graceful shutdown. Scheduled population must prevent overlapping runs.

### TD-08 — Configuration uses typed options

Use validated options for MongoDB, RabbitMQ, CoinGecko, Mailjet, schedules, analysis settings, and retry limits. The application must fail fast when required configuration is missing.

Secrets remain in environment-specific secret storage and must not be committed.

### TD-09 — Daily movement analysis uses per-user thresholds

The daily price-movement rule uses a 24-hour lookback period. There is no system-wide movement threshold; each user's configured threshold for a favorite coin determines whether that user qualifies. A movement equal to a user's threshold qualifies. Reject non-positive prices during data validation so percentage calculations always have a valid denominator.

Keep the lookback in typed configuration. Store user thresholds with favorite-coin preferences in MongoDB rather than global application configuration. Detailed profile, report audience, and delivery behavior is defined in [`../user-preferences/plan.md`](../user-preferences/plan.md).

When a population event is handled, query MongoDB for the latest stored snapshot for each coin. Use the latest stored data at handling time; the event's ingestion-run ID provides context and correlation, but does not limit analysis to that run's snapshots.

For each coin, calculate the effective comparison window as follows:

1. Set the default start to 24 hours before the newest snapshot.
2. Find the most recent daily movement report for the same coin.
3. If that report was created within the default window, move the window start to the report's creation time.
4. Select snapshots strictly after the effective start and no later than the newest snapshot.
5. Compare the newest snapshot with every earlier snapshot in the selected window using `(newPrice - oldPrice) / oldPrice × 100`.
6. If one or more comparisons meet at least one favoriting user's threshold, create at most one report for that coin and latest snapshot, with a snapshot of the qualifying user IDs.

Use the comparison with the greatest absolute percentage change as the report's reference price and direction. Require at least one earlier snapshot in the effective window; otherwise, do not create a report. Serialize analysis for a given coin or rely on the report uniqueness constraint and transaction so concurrent events cannot create duplicate reports.

This reset point prevents the price movement that caused a recent report from producing the same report again, while still allowing a new report when a later movement meets a qualifying user's threshold after the previous report.

## 3. Event contracts

Every event contains:

- Event ID.
- Event name and schema version.
- UTC occurrence time.
- Correlation ID for the full population-to-delivery flow.
- Optional causation event ID.
- Payload containing references to stored business records.

### `market-data.populated.v1`

Payload:

- Ingestion run ID.
- Provider name.
- Observation time.
- Asset and snapshot counts.

### `report.created.v1`

Payload:

- Report ID.
- Report type.
- Analysis-rule version.
- Coin code.
- Report period.

Events must not contain complete price histories, rendered emails, credentials, or user records.

## 4. RabbitMQ topology

### Exchanges

- `cointracker.events`: durable topic exchange for business events.
- `cointracker.dead-letter`: durable topic exchange for exhausted messages.

### Queues

- `cointracker.reporting.market-data-populated`: handles `market-data.populated.v1`.
- `cointracker.delivery.report-created`: handles `report.created.v1`.
- One dead-letter queue per consumer queue.

Queue names are owned by consumers. Event names are owned by producers.

### Processing policy

- Use a bounded prefetch value to prevent one consumer from taking unlimited work.
- Acknowledge only after the handler's database changes are committed.
- Commit the consumer's business changes and processed-message record in the same MongoDB transaction before acknowledging the RabbitMQ message.
- Retry a transient failure up to five times, waiting 30 seconds between attempts.
- The retry count excludes the initial processing attempt, allowing at most six total processing attempts.
- Send the message to its dead-letter queue after the fifth retry fails.
- Apply five retries at 30-second intervals to temporary email delivery failures. Permanent email failures are not retried. Discord delivery has one attempt and no retry as defined in [`../user-preferences/plan.md`](../user-preferences/plan.md).
- Do not retry permanent validation failures.
- Preserve event ID and correlation ID through retries and dead-lettering.

## 5. Target code organization

```text
Modules/
  Ingestion/
    Application/
    Domain/
    Infrastructure/
    Contracts/
  Reporting/
    Application/
    Domain/
    Infrastructure/
    Contracts/
  Delivery/
    Application/
    Domain/
    Infrastructure/
    Channels/Email/
    Channels/Discord/
  UserPreferences/
    Application/
    Domain/
    Infrastructure/
Shared/
  Messaging/
  Persistence/
  Observability/
Host/
```

The first refactor can use folders and namespaces inside the existing project. Separate .NET projects are optional later when compile-time boundaries provide enough value.

## 6. Implementation sequence

1. Establish test infrastructure, typed configuration, and module boundaries.
2. Implement RabbitMQ messaging, outbox publication, consumer idempotency, retries, and dead-lettering.
3. Move data population behind its module boundary and publish the Data Populated event.
4. Consume new data, create immutable reports, and publish the Report Created event.
5. Resolve MongoDB user preferences, evaluate per-coin thresholds, and deliver qualifying reports through email and Discord channels.
6. Add operational visibility, migration, and remove the old direct orchestration flow.

The detailed backlog and exit conditions are maintained in `tasks.md`.
