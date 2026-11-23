# AI working journal

## 1. Agreeing the scope and stack

- **Asked:** Explain the brief, compare the preferred backend options, and ask before starting. Then build the assessment in a fresh Git repository after confirmation.
- **Accepted:** Haziq approved C# and ASP.NET Core and agreed to start with an end-to-end claims workflow. The assistant proposed Minimal APIs so the routes stay easy to follow in the walkthrough.
- **Changed:** The assistant initially described the work as two UI views around submit, review and decide. The implementation will also cover one request-for-information loop because it is a core claimant action in the brief, with the API completed before the screens.
- **Scope:** Local synthetic data, a single service, and one currency. SQLite will persist claims. Kafka will carry status-change events asynchronously. Use a committed journal to record what the assistant generated and what validation changed.
