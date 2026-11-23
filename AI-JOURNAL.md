# AI working journal

## 1. Agreeing the scope and stack

- **Asked:** Explain the brief, compare the preferred backend options, and ask before starting. Then build the assessment in a fresh Git repository after confirmation.
- **Accepted:** Haziq approved C# and ASP.NET Core and agreed to start with an end-to-end claims workflow. The assistant proposed Minimal APIs so the routes stay easy to follow in the walkthrough.
- **Changed:** The assistant initially described the work as two UI views around submit, review and decide. The implementation will also cover one request-for-information loop because it is a core claimant action in the brief, with the API completed before the screens.
- **Scope:** Local synthetic data, a single service, and one currency. SQLite will persist claims. Kafka will carry status-change events asynchronously. Use a committed journal to record what the assistant generated and what validation changed.

## 2. Implementing the API

- **Asked:** Build the agreed workflow after the stack discussion. The assistant drafted the ASP.NET Core routes, SQLite statements and claim transitions.
- **Accepted:** Keep the workflow in one service. Use server-authenticated demo accounts and check ownership in the server read and write paths. Queue a status event in the same SQLite transaction as the claim and history.
- **Challenged:** Treating status as any editable field would allow skipped steps. Each action now has its own status and role checks. The action also includes the current claim version so two officers acting on the same view cannot overwrite each other.
- **Changed:** Keep the reported loss in integer MYR minor units. The workload summary labels it as reported loss because the brief leaves the reserve and payout models open.

## 3. Adding the Angular views

- **Asked:** Continue the backend-first build with a local frontend for the same claim lifecycle. The assistant generated a shared Angular workspace with role-specific actions and a status history.
- **Accepted:** Keep the current claims and workload views on REST. Use an explicit refresh for changes made in another session. Kafka transports status events for downstream use, while the browser reads the committed state.
