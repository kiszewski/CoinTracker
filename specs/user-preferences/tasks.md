# User Preferences and Discord Delivery Tasks

Status: Proposed  
System specification: [`spec.md`](spec.md)  
Technical plan: [`plan.md`](plan.md)

## Phase 1 — MongoDB user model and secret configuration

- [x] `USR-01` Add MongoDB user, favorite-coin preference, and Discord endpoint documents with stable user and endpoint IDs.
- [x] `USR-02` Add normalized email storage and a MongoDB unique index enforcing case-insensitive email uniqueness.
- [x] `USR-03` Add MongoDB indexes for favorite-coin lookup, report audience, and delivery identifiers; enforce duplicate favorite codes and canonical Discord URLs in application validation before atomic user-document updates.
- [x] `USR-04` Add encryption/decryption for Discord webhook URLs, with a local development key setting and production secret-manager configuration.
- [x] `USR-05` Ensure required indexes are created idempotently at startup; do not seed or migrate the existing hard-coded user.
- [ ] `USR-06` Add persistence tests for empty user store, unique email, case normalization, favorites, endpoint IDs, and encrypted endpoint round-tripping.

Exit condition: MongoDB can store and retrieve profiles safely, enforce unique normalized emails, and keep Discord credentials encrypted; startup creates the required indexes without adding a user.

## Phase 2 — User profile API

- [x] `API-01` Add the User Preferences module and request/response DTOs.
- [x] `API-02` Implement `POST /api/users` for profile creation, including name, unique email, an optional favorite-coin threshold list, and zero to five Discord webhook URLs; return the generated user ID.
- [x] `API-03` Implement `POST /api/users/lookup` to accept an email in the request body and return only its matching user ID, without a password.
- [x] `API-04` Implement `GET /api/users/{userId}` to return safe profile data without exposing webhook tokens.
- [x] `API-05` Implement `PUT /api/users/{userId}` for profile updates, including atomic email changes and updates to favorites, thresholds, and Discord endpoints.
- [x] `API-06` Validate email format, positive per-coin thresholds, normalized unique coin codes, Discord endpoint count, and duplicate endpoints; return clear validation/conflict/not-found responses.
- [x] `API-07` Return stable endpoint IDs and safe masked identifiers so a user can identify endpoints to replace or remove.
- [x] `API-08` Prevent profile emails and Discord URLs/tokens from appearing in request logs, application logs, errors, and response bodies; never put email in a route path.
- [ ] `API-09` Add API tests for create, email-to-ID lookup, read/update by ID, duplicate email in different casing, email change conflict, invalid thresholds, duplicate coins/endpoints, endpoint limit, and safe response redaction.

Exit condition: profiles can be created, looked up by email to obtain an ID, viewed and updated by ID; validation rules are enforced and no email or credential is logged or returned unnecessarily.

## Phase 3 — Per-user report thresholds and audience snapshot

- [x] `RPT-01` Remove global movement-threshold configuration and its startup validation; retain the 24-hour lookback configuration.
- [x] `RPT-02` Update the analysis rule to compute the greatest absolute movement in the effective lookback window without a global threshold.
- [x] `RPT-03` Load matching favorite-coin preferences and compare movement against each user's configured threshold; equality must qualify.
- [x] `RPT-04` Create no report when no matching user threshold is met; otherwise create one report per coin/latest snapshot, regardless of how many users qualify.
- [x] `RPT-05` Persist one `ReportAudienceDocument` per qualified user in `report_audiences`, with a unique `(reportId, userId)` index, in the same MongoDB transaction as the report and `report.created.v1` outbox event.
- [x] `RPT-06` Preserve report idempotency by rule version, coin, and latest snapshot ID, including duplicate and concurrent event handling.
- [x] `RPT-07` Keep the qualified audience fixed at analysis time and keep user/email records out of the RabbitMQ event payload.
- [ ] `RPT-08` Add analysis tests for different user thresholds, exact-threshold qualification, no users/no matching favorites, no threshold met, multiple qualifying users, one logical report, duplicate events, and the existing 24-hour window reset.

Exit condition: per-user thresholds exclusively determine report eligibility; an eligible movement creates one report and an immutable qualified audience, and an ineligible movement creates none.

## Phase 4 — Delivery fan-out and email

- [x] `DLV-01` Update the `report.created.v1` handler to load the report's qualified audience instead of the hard-coded `InitialRecipient`.
- [x] `DLV-02` Create one email delivery per qualified user using the current email at fan-out time.
- [x] `DLV-03` Create one Discord delivery per qualified user and current endpoint ID at fan-out time.
- [x] `DLV-04` Add stable unique delivery IDs/indexes by report, user, channel, and Discord endpoint where applicable.
- [x] `DLV-05` Persist all fan-out delivery records and the processed-message marker transactionally before acknowledging the event.
- [x] `DLV-06` Make missing/deleted profiles and one recipient's delivery failure independent from other recipients and channels.
- [x] `DLV-07` Preserve email's five retries at 30-second intervals after the initial attempt; keep terminal email failures terminal.
- [x] `DLV-08` Remove the hard-coded user/favorite list and update dependency registration and delivery interfaces.
- [ ] `DLV-09` Add tests for matching audiences, email change before fan-out, removed/missing users, multiple users/channels, duplicate report event, and independent failures.
- [ ] `DLV-10` Test email retry scheduling, exhaustion, and permanent SMTP failure behavior.

Exit condition: one report fans out to exactly the qualified users and their current delivery channels/endpoints; replaying the event creates no duplicate logical deliveries.

## Phase 5 — Discord delivery

- [x] `DSC-01` Add strict Discord incoming webhook URL validation: HTTPS, official `discord.com` host, expected webhook path, and no user-info, caller-supplied query, fragment, or redirects.
- [x] `DSC-02` Add a Discord report embed renderer with the report's coin, direction, percentage, prices, and period; ensure output stays within Discord payload limits.
- [x] `DSC-03` Add the Discord delivery channel and HTTP client; append `wait=true` to each validated URL.
- [x] `DSC-04` Enforce a 10-second request timeout; only HTTP 200 is success, and all other statuses/network/timeouts are terminal with no retry.
- [x] `DSC-05` Record Discord delivery outcomes and sanitized status/error details without storing or logging the full webhook URL in delivery diagnostics.
- [ ] `DSC-06` Add Discord client/channel tests for valid and invalid endpoints, redirects, payload format, HTTP 200, non-200 responses, timeout, and no retry.
- [x] `DSC-07` Persist the single-attempt claim before calling Discord; if a worker restarts or a lease expires after the claim, record an unknown/terminal outcome and do not resend.

Exit condition: only valid Discord incoming webhooks can be called; a delivery is attempted at most once, successful delivery requires confirmed HTTP 200, and failed or interrupted attempts are recorded without retry.

## Phase 6 — Operations and release readiness

- [ ] `OPS-01` Update health/readiness configuration for user API and Discord credential-encryption configuration without exposing secrets.
- [ ] `OPS-02` Add metrics for user profile operations, report audience size, and email versus Discord delivery outcomes.
- [ ] `OPS-03` Add structured logs with correlation/report/delivery IDs while excluding email values and Discord URLs/tokens.
- [ ] `OPS-04` Update local setup and production secret documentation for the Discord credential-encryption key and user API.
- [x] `OPS-05` Document that authentication and profile ownership checks are out of scope; bind the local API port to loopback and require trusted/private-network restriction in V1 deployments.
- [ ] `OPS-06` Add end-to-end coverage for population through report creation and per-user email/Discord delivery using fake HTTP/SMTP endpoints plus MongoDB and RabbitMQ containers.
- [ ] `OPS-07` Run the full test suite and review the task acceptance conditions before marking the feature complete.

Exit condition: profile management and threshold-based report delivery are covered end to end, observable, and documented with the unauthenticated-access and Discord-delivery limitations clearly stated.

## Definition of done

- Every functional requirement in `spec.md` has an automated test or an explicitly documented operational limitation.
- Email uniqueness, endpoint limits, threshold qualification, delivery idempotency, and transaction behavior are enforced by persistence constraints and/or application validation.
- Discord URLs/tokens and user email addresses are absent from logs and error responses.
- Discord delivery uses the exact V1 policy: Discord-only, `wait=true`, 10-second timeout, HTTP 200 success, at most one attempt including after restart, and no retry.
- Email retains five retries at 30-second intervals after the initial attempt.
- No hard-coded user is seeded or migrated, and the global movement threshold is removed.
- Local and production configuration requirements are documented.
