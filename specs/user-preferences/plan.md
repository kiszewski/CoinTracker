# User Preferences and Discord Delivery Plan

Status: Proposed  
System specification: [`spec.md`](spec.md)  
Related system plan: [`../event-driven-architecture/plan.md`](../event-driven-architecture/plan.md)
Implementation tasks: [`tasks.md`](tasks.md)

## 1. Purpose

This plan translates the user-preferences specification into technical decisions for the existing CoinTracker .NET modular monolith. It extends the current MongoDB, RabbitMQ, outbox, report-analysis, and delivery flow without changing the deployment model.

## 2. Technical decisions

### TD-01 — Persist user profiles in MongoDB

Add a `users` collection. Each document has a stable generated user ID, name, normalized email, favorite coin preferences (coin code plus positive percentage threshold), and up to five Discord webhook endpoints.

Give each Discord endpoint a stable endpoint ID. Treat its URL as a credential because the URL contains the token that authorizes posting. Store the value encrypted at rest using an application encryption key supplied by local environment configuration in development and secret storage in production. Never return the full stored URL after it has been registered and never log it.

Normalize email consistently before lookups and writes, and enforce uniqueness with a MongoDB unique index on the normalized value as well as API validation. Normalize favorite coin codes to lowercase and add a non-unique multikey index on favorite coin code for threshold lookups. Favorites and Discord endpoints are bounded arrays in one user document, so reject duplicate coin codes and duplicate canonical webhook URLs in application validation before the atomic document update; do not rely on multikey unique indexes to enforce uniqueness within one user's arrays. Enforce the five-endpoint maximum on every create/update path.

Do not seed or migrate the existing hard-coded recipient. The collection starts empty. Add a profile creation operation so callers can create profiles. Provide an email-based ID lookup with no password, then use the returned user ID for profile reads and updates. V1 has no authentication or proof of email ownership; the email lookup and user ID are locators only, not authorization credentials, which remains an explicit security limitation.

### TD-02 — Add profile API operations

Add a small user-preferences module with create, email-to-ID lookup, and read/update-by-ID operations:

- `POST /api/users` creates a profile.
- `POST /api/users/lookup` accepts an email address in the request body and returns the matching user ID only.
- `GET /api/users/{userId}` returns a profile, omitting webhook URL tokens.
- `PUT /api/users/{userId}` updates name, email, favorites/thresholds, and endpoint list.

Email changes must preserve uniqueness and be atomic. Return a conflict for an email already assigned to another profile, invalid threshold/coin/URL data, duplicate values, or more than five endpoints. Do not include email values or Discord tokens in request logs, exception messages, or error responses. Do not put email addresses in route paths; redact lookup request bodies in logs and telemetry.

Authentication and profile ownership/authorization checks are explicitly out of scope in V1. These endpoints require no credentials. The lookup endpoint returns a user ID to any caller who knows the email, and the ID is only a record locator, not an authorization credential. Do not imply that either value verifies the caller's identity.

### TD-03 — Resolve and validate Discord endpoints narrowly

Accept only HTTPS Discord incoming webhook URLs in the official form `https://discord.com/api/webhooks/{webhook-id}/{token}`. Reject other hosts, schemes, ports, user-info components, malformed paths, query strings, and fragments. The application adds `wait=true` when sending; callers do not control request query parameters.

Disable HTTP redirects. The exact host and path validation limits outbound requests to Discord rather than general user-provided URLs. If Discord adds a supported official host or URL form in the future, extend the allowlist deliberately.

Do not add a separate signing secret to webhook requests. Native Discord incoming webhooks authorize the sender through the secret-bearing endpoint URL/token. The Discord URL/token is the only delivery credential to protect; a separate signature mechanism is outside this Discord-only scope.

### TD-04 — Make per-user thresholds the only report threshold

Remove the global `MovementThresholdPercent` option. Keep the 24-hour lookback and existing rule for comparing the newest stored price with all earlier stored prices in the effective window.

For each coin, the analysis component will:

1. Read the latest stored market snapshots and current MongoDB user preferences.
2. Find users who have favorited the coin and identify their configured thresholds.
3. Calculate the greatest absolute price movement in the effective lookback window using the existing report rule.
4. If that movement meets no matching user's threshold, create no report.
5. If it meets one or more thresholds, create one report for the coin and latest snapshot, and record the IDs of users whose thresholds were met in the report's audience snapshot.

In the modular monolith, Reporting reads a projection of user ID, favorite coin, and threshold through its own read-only repository/port against MongoDB; it does not call User Preferences application services. If modules are separated into services later, replace this shared-database read with a user-preferences event-fed Reporting read model.

Keep the report's idempotency key based on rule version, coin, and latest snapshot ID, so one logical movement creates at most one report even when many users qualify. Thresholds are compared using the absolute percent change; a movement equal to a user's threshold qualifies.

The qualified user audience is fixed when the report is analyzed. Later favorite or threshold changes do not retroactively change who qualified for that report. Resolve each qualified user's current email address and current Discord endpoint list when the Report Created event is fanned out into delivery records. Changes made after those delivery records are created do not rewrite pending deliveries. If the profile no longer exists, record that recipient as unavailable rather than failing delivery for other users.

The report remains a generic immutable movement record; do not duplicate user profile data in the report-created event. Persist one `report_audiences` document per qualified user, keyed by report ID and user ID, in the same transaction as the report and outbox event. Keep the event payload as a report reference.

### TD-05 — Keep report delivery event-driven; fan out independently

Continue consuming `report.created.v1`. The delivery handler loads the report and its stored audience, then creates idempotent delivery records for each qualified user:

- One email delivery per report and user.
- One Discord delivery per report, user, and Discord endpoint ID.

Use a unique delivery ID/index based on those keys. Persist all pending deliveries before acknowledging the RabbitMQ event, in the same MongoDB transaction as the processed-message marker. A failure for one user, email, or endpoint must not prevent records for the others from being created or processed.

Keep channel-specific behavior behind delivery-channel implementations. Add a Discord delivery channel alongside the existing email channel; neither report analysis nor ingestion sends notifications directly.

### TD-06 — Format Discord report messages

Render each report as a Discord embed with the coin name/code, movement direction and percentage, previous and latest USD prices, and comparison time range. Keep the payload within Discord's message/embed limits; do not include user data or credentials. Use the existing immutable report record as the rendering source.

Post to the validated Discord URL with `wait=true`. Apply a per-request timeout of 10 seconds. Only HTTP 200 is success. Other HTTP statuses (including rate-limit responses), network errors, and timeout are recorded as terminal failures and are not retried. Redact the endpoint URL from diagnostics. Store only safe response details such as status code and a sanitized error category.

Before making the external request, atomically persist that the single Discord attempt has started. If the worker restarts or its lease expires with an attempt still in progress, mark the outcome as unknown/terminal and do not send again. This avoids duplicate posts at the cost of possibly missing a post if the process fails after recording the attempt but before sending it. Discord does not provide request-signature verification or idempotency for this operation.

### TD-07 — Preserve email retry behavior

Email keeps the existing five retries at 30-second intervals after the initial attempt (at most six total attempts). Permanent SMTP failures remain terminal according to the existing email behavior. Discord failures do not enter the email retry path, and an email failure does not delay Discord attempts.

### TD-08 — Maintain transaction and event guarantees

Use MongoDB transactions for profile changes that touch more than one persisted record, report plus outbox writes, and delivery fan-out plus processed-message markers. Ensure the local MongoDB replica set remains enabled. Keep report/event idempotency and RabbitMQ acknowledgment behavior from the existing event-driven plan.

Do not include full user records, emails, or Discord tokens in RabbitMQ event payloads. The `report.created.v1` event continues to carry a report ID and correlation metadata; delivery looks up the report and its audience in MongoDB.

## 3. Data changes

- Add `UserDocument` with stable user ID, name, normalized email, favorite coin preferences and endpoint metadata.
- Add endpoint IDs so delivery records remain stable when a URL is replaced.
- Add a `ReportAudienceDocument` in `report_audiences`, with a unique compound index on report ID and user ID. Keep the immutable `CoinReportDocument` generic and free of recipient data.
- Add a delivery channel value for `discord` and a Discord endpoint ID to delivery records. Never put the full webhook URL in a delivery record if it can be resolved from the encrypted user endpoint record at send time.
- Add MongoDB indexes: unique normalized email, unique `(reportId, userId)` audience association, and unique delivery idempotency keys.
- Do not create a seed user or migrate the hard-coded user.

## 4. API behavior

Profile creation requires name and email, with an optional list of favorite coin/threshold entries and zero to five Discord webhook URLs. Email and endpoint values are validated before persistence. The API returns the generated profile identifier and safe profile data; it does not return complete webhook URLs in subsequent read/update responses. It may return endpoint IDs and a masked Discord host/path so a user can identify which endpoint to update or remove.

Profile lookup accepts email in the request body and returns only the matching user ID. Profile read/update accept that ID in the route. When email is changed, the user ID identifies the existing profile and the body supplies the new email. A successful update returns the updated safe profile. Use conflict responses for duplicate email and invalid profile state; use not-found when the email or ID does not exist. Do not put email addresses in route paths, and redact lookup request bodies in logs and telemetry.

Because V1 has no authentication or ownership checks, callers can claim profiles with arbitrary emails and can look up IDs and read/update those profiles. V1 deployments must keep profile endpoints on a trusted/private network; this is an operational exposure limit, not authentication or authorization. Public exposure is outside the V1 rollout scope.

## 5. Implementation sequence

1. Add user, favorite-threshold, and Discord endpoint domain/data models; add MongoDB indexes and encryption configuration.
2. Implement profile creation, lookup, and update API behavior with validation and email uniqueness.
3. Remove the global movement-threshold option and update analysis to evaluate per-user coin thresholds and persist the qualified audience.
4. Update report delivery fan-out and idempotency for multiple users and endpoints.
5. Add Discord URL validation, secret handling, Discord embed rendering, and the `wait=true` HTTP client with 10-second timeout and no retries.
6. Retain and verify email retry behavior (five retries, 30 seconds apart).
7. Update readiness/configuration documentation and operator guidance, including the unauthenticated profile-access risk and Discord credential key management.

The implementation backlog and acceptance checks should be maintained in `tasks.md` after this plan is accepted.
