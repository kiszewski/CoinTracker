# CoinTracker System Specification

Status: Proposed  
Technical plan: [`plan.md`](plan.md)  
Implementation tasks: [`tasks.md`](tasks.md)

## 1. Purpose

CoinTracker collects cryptocurrency market data, analyzes price behavior, creates reports, and delivers those reports to interested users.

The system is divided into independent components that communicate through events. This allows each part of the flow to evolve without creating direct dependencies between data collection, report creation, and delivery.

## 2. System flow

```text
Populate market data
    ↓
Data Populated event
    ↓
Analyze data and create reports
    ↓
Report Created event
    ↓
Deliver report through the user's enabled channels
```

Email is the first delivery channel. Other channels may be added later without changing how reports are created.

## 3. Components

### 3.1 Data Population

The Data Population component:

- Collects current cryptocurrency data from an external provider.
- Validates the received information.
- Stores the new market observations.
- Announces that new data is available for analysis.
- Records whether each population attempt succeeded or failed.

The component does not analyze prices or send notifications.

### 3.2 Report Analysis

The Report Analysis component:

- Reacts when new market data becomes available.
- Applies the enabled analysis rules.
- Creates and stores reports when a rule produces a result.
- Announces each newly created report.

Initial report types:

- Daily price movement: compares the newest price with all recorded prices in the effective lookback window and identifies an increase or decrease of at least 5%.

The component does not know how reports will be delivered.

### 3.3 Delivery

The Delivery component:

- Reacts when a report is created.
- Identifies users interested in the reported cryptocurrency and report type.
- Delivers the report through each user's enabled channels.
- Records the outcome of each delivery.

Initial delivery channel:

- Email

Future examples include push notifications, SMS, and webhooks.

## 4. Events

### 4.1 Data Populated

This event means that a population run completed successfully and new market data is available.

It starts the report-analysis stage.

The event must identify the population run so the analysis component can find the relevant data.

### 4.2 Report Created

This event means that a new report has been created and is ready for delivery.

It starts the delivery stage.

The event must identify the report so the delivery component can find its content and intended audience.

## 5. Functional requirements

### Data population

- `POP-01` The system must collect market data every 15 minutes by default, using a schedule configurable in whole minutes.
- `POP-02` A new run must not start while a previous run is still active.
- `POP-03` Invalid or incomplete external data must not be treated as a successful population; prices must be present and greater than zero.
- `POP-04` A successful run must produce one Data Populated event.
- `POP-05` A failed run must be visible for diagnosis and must not start report analysis.

### Report analysis

- `RPT-01` When a Data Populated event is handled, the system must analyze the latest market data currently stored for each coin, even if the event refers to an older population run.
- `RPT-02` The initial daily analysis rule must use a 5% movement threshold and a 24-hour lookback period.
- `RPT-03` The newest stored price must be compared with every earlier price recorded in the effective lookback window.
- `RPT-04` If no daily movement report exists for the coin in the preceding 24 hours, the effective window is the preceding 24 hours.
- `RPT-05` If a daily movement report exists for the coin in the preceding 24 hours, the effective window starts immediately after the most recent report's creation time.
- `RPT-06` One analysis run must create at most one daily movement report per coin.
- `RPT-07` Reports must preserve the values and period used to create them.
- `RPT-08` A newly created report must produce one Report Created event.
- `RPT-09` Reprocessing the same data must not create duplicate reports.
- `RPT-10` A report must remain available even when its delivery fails.

### Delivery

- `DLV-01` For the initial release, the existing hard-coded user and favorite-coin list determine report recipients.
- `DLV-02` For the initial release, matching reports are delivered by email; user-managed channel preferences are outside the scope.
- `DLV-03` A report must be delivered only when its cryptocurrency matches the initial recipient's hard-coded favorite-coin list.
- `DLV-04` The system must record whether each delivery is pending, successful, or failed.
- `DLV-05` Temporary delivery failures must be attempted again.
- `DLV-06` Reprocessing the same Report Created event must not create duplicate deliveries.
- `DLV-07` Adding a delivery channel must not require changes to data population or report analysis.

### System behavior

- `SYS-01` Each population flow must be traceable from data collection to report delivery.
- `SYS-02` A failure in one component must not lose completed work from another component.
- `SYS-03` Failed work must be recoverable without creating duplicate reports or deliveries.
- `SYS-04` Sensitive configuration and user contact information must not be exposed in logs.

## 6. MVP scope

The first release includes:

- One external market-data provider.
- Scheduled data population.
- Daily price-movement reports.
- The existing hard-coded user and favorite-coin list as the initial recipient source.
- Email delivery.
- Visibility into population, report, and delivery outcomes.

## 7. Outside the initial scope

- Additional delivery channels.
- Multiple market-data providers.
- Weekly summary reports.
- Manual population and operational endpoints.
- A user-facing subscription management interface.
- Independently deployed services.
- Historical analytics beyond the initial daily reports.

## 8. Acceptance scenarios

### Successful daily report

Given the existing hard-coded user has Bitcoin in their favorite-coin list, when new Bitcoin data matches the daily analysis rule, then a daily report is created and delivered by email.

### No matching report

Given new market data does not match any analysis rule, when the data is analyzed, then no report or delivery is created.

### Comparison after a recent report

Given a coin produced a daily movement report less than 24 hours ago, when new data is analyzed, then the newest price is compared only with prices recorded after that report was created.

### New movement after a recent report

Given a coin produced a daily movement report less than 24 hours ago, when its price subsequently moves by at least another 5% relative to a price recorded after that report, then one new daily movement report may be created.

### Duplicate event

Given the same Data Populated or Report Created event is processed more than once, when processing finishes, then the system still contains only one logical report and one logical delivery for each recipient and channel.

### Temporary delivery failure

Given email delivery is temporarily unavailable, when a report is ready, then the report remains stored and the delivery is attempted again later.

### Failed population

Given the external provider returns invalid data or is unavailable, when a population run finishes, then its failure is recorded and report analysis does not begin.

### Scheduled population

Given the population interval uses its default configuration, when the application is running, then market data collection starts immediately and repeats every 15 minutes without overlapping runs.

### New delivery channel

Given a new delivery channel is supported and enabled for a user, when a matching report is created, then it can be delivered through that channel without changing the population or analysis components.

## 9. Success criteria

The first release is successful when:

- New market data automatically starts report analysis.
- New reports automatically start delivery.
- Email is sent only for reports matching the initial recipient's favorite-coin list.
- Repeated events do not produce duplicate business results.
- Failed deliveries can recover without recreating reports.
- A future delivery channel can be added independently.
