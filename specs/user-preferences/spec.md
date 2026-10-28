# User Preferences and Webhook Delivery Specification

Status: Proposed  
Related specification: [`../event-driven-architecture/spec.md`](../event-driven-architecture/spec.md)
Technical plan: [`plan.md`](plan.md)
Implementation tasks: [`tasks.md`](tasks.md)

## 1. Purpose

Replace the hard-coded report recipient and favorite-coin list with user-managed preferences stored in MongoDB. Users can manage their profile and choose report delivery through email and registered Discord webhooks.

## 2. User data

Each user record must include:

- Name
- Email address
- A list of favorite coins, each with its own movement threshold
- A list of Discord webhook endpoint URLs

The system must persist user records in MongoDB. Callers can view and update profile data by user ID; V1 does not verify that the caller owns the profile.

## 3. Functional requirements

### User profile and preferences

- `USR-01` The system must store user records in MongoDB with a name, email address, favorite coin list, and webhook endpoint list.
- `USR-02` A caller must be able to create a user profile.
- `USR-03` A caller must be able to retrieve a user's ID by submitting the user's email address to a lookup operation; no password is required.
- `USR-04` A caller must be able to view and update a profile using its user ID, including name, email address, favorite coins and their thresholds, and webhook endpoints.
- `USR-05` Authentication and profile ownership/authorization checks are out of scope for V1. Neither the email lookup nor knowledge of a user ID verifies the caller's identity or ownership of the profile.
- `USR-06` Email addresses must be valid and unique across users, compared case-insensitively.
- `USR-07` Favorite coin codes must be normalized consistently so report matching is case-insensitive and duplicate favorites are not stored.
- `USR-08` Each favorite coin must have an independently configurable movement threshold, expressed as a positive percentage.
- `USR-09` Webhook endpoint entries must be valid Discord incoming webhook URLs and duplicates must not be stored.
- `USR-10` A user may register no more than five webhook endpoints.
- `USR-11` V1 must not seed or migrate the existing hard-coded user; the user collection may start empty.

### Report delivery

- `USR-12` When a report is created, the system must find MongoDB users whose favorite coins include the report's coin.
- `USR-13` The system must send a matching report to each matching user's email address.
- `USR-14` The system must call each Discord webhook endpoint belonging to a matching user with a Discord-compatible representation of the report.
- `USR-15` A user's report delivery must not depend on another user's endpoint or delivery succeeding.
- `USR-16` Email and webhook delivery outcomes must be recorded so failures can be diagnosed without recreating the report.
- `USR-17` Reprocessing a report event must not create duplicate logical deliveries for the same report, user, and channel/endpoint.
- `USR-18` The threshold associated with each user's favorite coin must determine whether that user qualifies for a report about that coin. There is no system-wide movement threshold.
- `USR-19` If a coin movement meets one or more users' thresholds, the system must create one logical report for that movement and deliver it only to users whose thresholds are met.
- `USR-20` If a coin movement meets no user's threshold, the system must not create a report for that movement.

## 4. Webhook behavior

- V1 supports only Discord incoming webhooks. Other webhook providers and arbitrary endpoint URLs are not supported.
- Webhooks are triggered only for reports matching a user's favorite coin list and threshold.
- Each request must include a Discord-compatible message containing the report information needed by the recipient.
- A webhook failure must not block email delivery or deliveries to other users/endpoints.
- Webhook calls must not be retried after a failed attempt in V1.
- Each Discord delivery may make at most one HTTP attempt, including after a worker restart; an interrupted attempt with unknown outcome is recorded as terminal and not resent.
- Each webhook attempt must have a 10-second timeout.
- Discord webhook requests must use `wait=true` so Discord confirms the message creation. Only an HTTP `200 OK` response counts as a successful delivery; any other status or a timeout is recorded as a failure, with no retry.
- Each delivery must have a stable internal identifier for tracking and deduplication.

Discord incoming webhook URLs contain the credential used to authorize posting. Do not add a separate signing secret; treat the URL/token as a credential, keep it out of logs and subsequent responses, and protect it at rest. Discord's [webhook documentation](https://discord.com/developers/docs/resources/webhook#execute-webhook) describes the webhook token and URL used to execute a webhook, including the `wait` parameter for confirmation.

## 5. Security and privacy

- V1 does not authenticate users or verify profile ownership. Email is only a profile lookup key and the user ID is only a record locator, not proof of identity. Callers may create profiles with unverified email addresses, retrieve IDs by email, and view or change profiles. This is a known security limitation.
- Email addresses and webhook URLs must not be exposed in application logs.
- Discord webhook URLs contain credentials and must be treated as secrets. Only the official Discord incoming-webhook host and expected webhook URL path may be accepted; redirects must not be followed.
- User profile data must not be included in report events unless needed for delivery.

## 6. Acceptance scenarios

### Matching user receives email

Given a MongoDB user has Bitcoin as a favorite with a configured threshold, when the Bitcoin movement meets that threshold, then the system creates one report for the movement and attempts email delivery to that user's current email address.

### Matching user receives Discord webhook

Given a MongoDB user has Bitcoin as a favorite with a configured threshold and has registered a Discord webhook URL, when the Bitcoin movement meets that threshold, then the system posts a Discord-compatible report message to that endpoint.

### Non-matching report

Given a user does not have the report's coin in their favorites, when that report is created, then no email or webhook delivery is created for that user.

### Isolated delivery failure

Given one user's webhook is unavailable, when a matching report is delivered, then that failure does not prevent email or webhook delivery for any other recipient.

### Email locates profile in V1

Given a caller submits an email address to the lookup operation, when the email matches a profile, then the system returns that profile's user ID without a password or ownership check.

### Create profile

Given no profile exists for an email address, when a caller submits valid profile data, then the system creates a MongoDB user and returns its user ID.

### User ID locates profile in V1

Given a caller supplies a user ID, when they view or update that profile, then V1 does not verify that the caller owns the profile.

### Webhook failure

Given a webhook call does not complete successfully within 10 seconds, when its outcome is recorded, then V1 treats the attempt as failed and does not retry that endpoint.

### Interrupted Discord attempt

Given a Discord delivery attempt was marked as started before the worker stopped, when processing resumes and the outcome is unknown, then the system records an unknown terminal outcome and does not send another request.

### Different user thresholds

Given two users have the same coin as a favorite with different thresholds, when a price movement meets only the lower threshold, then one report is created and delivered only to the user whose threshold is met.

### Duplicate report event

Given the same report event is handled more than once, when delivery records are created, then there is at most one logical delivery for each report, user, and email or webhook destination.

## 7. Out of scope

- Authentication, authorization/ownership checks, password management, and account recovery.
- Administrative user management.
- Managing report types or notification frequency.
- Exactly-once webhook delivery guarantees.
- Editing report content or analysis rules.

## 8. Confirmed V1 decisions

- Authentication and profile ownership/authorization checks are explicitly out of scope for V1.
- Callers use email addresses to retrieve profile IDs without a password, then use those IDs for profile read/update operations; neither value provides authentication.
- No hard-coded user is seeded or migrated.
- Email delivery keeps the existing five retries at 30-second intervals (in addition to the initial attempt).
- Webhook delivery has a 10-second timeout and no retry.
- The global report threshold is removed. Each user's threshold for a favorite coin determines whether that user qualifies for the report.
- A user may register up to five webhook endpoints.
- V1 accepts Discord incoming webhook URLs only. The Discord webhook URL/token is the delivery credential; no separate signing secret is used.
- Discord delivery requests use `wait=true` and require an HTTP `200 OK` response for successful delivery.

## 9. Open decisions for the plan

- None for the V1 behavior covered by this specification.
