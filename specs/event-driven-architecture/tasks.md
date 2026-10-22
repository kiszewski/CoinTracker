# CoinTracker Implementation Tasks

Status: Proposed  
System specification: [`spec.md`](spec.md)  
Technical plan: [`plan.md`](plan.md)

## Phase 0 — Foundation

- [ ] `FOUND-01` Add unit and integration test projects.
- [x] `FOUND-02` Add Docker Compose to run the .NET application, RabbitMQ, and a single-node replica-set MongoDB locally.
- [ ] `FOUND-03` Add RabbitMQ and MongoDB integration-test infrastructure.
- [ ] `FOUND-04` Capture current daily analysis behavior with characterization tests.
- [x] `FOUND-05` Introduce validated typed configuration, including local AMQP and production CloudAMQP connection settings.
- [x] `FOUND-06` Establish module namespaces and dependency rules.

Exit condition: tests run locally, invalid configuration fails at startup, and module ownership is clear.

## Phase 1 — RabbitMQ messaging

- [x] `MSG-01` Add the RabbitMQ client and connection management.
- [x] `MSG-02` Define the shared event envelope and serializer.
- [x] `MSG-03` Declare exchanges, queues, bindings, and dead-letter queues at startup.
- [x] `MSG-04` Implement publisher confirms and persistent messages.
- [x] `MSG-05` Implement the MongoDB publication outbox and transactionally save it with business records.
- [x] `MSG-06` Implement an outbox publisher worker.
- [x] `MSG-07` Implement a reusable RabbitMQ consumer host with manual acknowledgements.
- [x] `MSG-08` Record processed message IDs for consumer idempotency.
- [x] `MSG-09` Add five retries at fixed 30-second intervals, followed by dead-letter routing.
- [x] `MSG-10` Remove published outbox records five days after publication; never expire unpublished records.
- [ ] `MSG-11` Test publication failure, duplicate delivery, retry timing and exhaustion, restart, dead-letter routing, and outbox cleanup.

Exit condition: a test event survives application and RabbitMQ restarts, reaches its consumer, and duplicate delivery has no additional business effect.

## Phase 2 — Data Population

- [x] `ING-01` Extract `IMarketDataProvider` from the current repository.
- [x] `ING-02` Implement the CoinGecko provider adapter.
- [x] `ING-03` Separate external API access from MongoDB persistence.
- [x] `ING-04` Add ingestion-run and snapshot records with uniqueness rules.
- [x] `ING-05` Replace the current timer callback with an awaited, non-overlapping worker.
- [x] `ING-06` Save `market-data.populated.v1` through the outbox after successful persistence.
- [x] `ING-07` Add timeout, cancellation, response validation, and provider error handling.
- [ ] `ING-08` Test successful, empty, invalid, failed, and duplicate population runs.

Exit condition: one successful population run stores unique snapshots and publishes one RabbitMQ event; a failed run publishes none.

## Phase 3 — Report Analysis

- [x] `RPT-01` Define immutable reports and an `IReportRule` contract.
- [x] `RPT-02` Implement the configurable daily movement rule with an initial 5% threshold and 24-hour lookback period; exactly 5% qualifies.
- [x] `RPT-03` On event handling, query MongoDB for the latest stored snapshot per coin, compare it with every earlier snapshot in the effective window, and use the greatest absolute percentage change as the report reference.
- [x] `RPT-04` Reset a coin's effective window to immediately after its most recent daily movement report when that report was created within the preceding 24 hours.
- [x] `RPT-05` Limit each analysis run to one new daily movement report per coin.
- [x] `RPT-06` Consume `market-data.populated.v1` from RabbitMQ.
- [x] `RPT-07` Persist reports with a unique key on rule name/version, coin, and latest snapshot ID.
- [x] `RPT-08` Save each report and its `report.created.v1` outbox event in one MongoDB transaction.
- [x] `RPT-09` Correct the existing percentage-change calculation, which currently never assigns `PercentChanged`.
- [ ] `RPT-10` Test upward and downward movements, the exact 5% boundary, non-positive price rejection, insufficient data, delayed and duplicate events, concurrent analysis, multiple qualifying prices, and the post-report window reset.

Exit condition: replaying a population event creates neither duplicate reports nor duplicate report events.

## Phase 4 — Delivery and email

- [x] `DLV-01` Use the existing hard-coded user and favorite-coin list to resolve initial email recipients.
- [x] `DLV-02` Define `IDeliveryChannel` and its result model.
- [x] `DLV-03` Consume `report.created.v1` from RabbitMQ.
- [x] `DLV-04` Create one delivery record per matching recipient and enabled channel.
- [x] `DLV-05` Implement delivery retry and terminal-failure states; apply five retries at 30-second intervals for temporary email failures and do not retry permanent failures.
- [x] `DLV-06` Extract the daily report email template renderer.
- [x] `DLV-07` Place Mailjet SMTP behind an `IEmailSender` adapter.
- [x] `DLV-08` Register the email channel through dependency injection.
- [ ] `DLV-09` Test hard-coded recipient matching, duplicate events, retry timing and exhaustion, permanent SMTP errors, and templates.

Exit condition: one report event creates one logical email delivery for the hard-coded recipient when the report matches their favorite-coin list, even after retry or replay.

## Phase 5 — Operations and rollout

- [x] `OPS-01` Add health checks for MongoDB, RabbitMQ, CoinGecko configuration, and Mailjet configuration.
- [x] `OPS-02` Add metrics for population outcomes, queue depth, consumer failures, reports, and deliveries.
- [ ] `OPS-03` Add structured logs using event and correlation IDs.
- [ ] `OPS-04` Add an end-to-end test using a fake provider and fake email sender with real MongoDB and RabbitMQ containers.
- [x] `OPS-05` Migrate existing embedded coin snapshots.
- [x] `OPS-06` Remove `/testEmail`, the direct `/process` orchestration flow, and superseded services.
- [x] `OPS-07` Document local RabbitMQ setup, event contracts, operations, and adding a channel.

Exit condition: the complete flow is observable, recoverable, documented, and no endpoint directly coordinates all components.

## Definition of done

A task is complete when:

- Automated tests cover its expected and failure behavior.
- Duplicate message delivery is safe.
- Cancellation and graceful shutdown are supported where applicable.
- Logs include the relevant correlation and business identifiers.
- Sensitive values are excluded from logs.
- Configuration changes are documented.
- Event-contract changes are versioned.
