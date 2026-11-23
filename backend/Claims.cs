namespace Claims.Api;

public enum ClaimCategory { Motor, Property }

public enum ClaimStatus { Submitted, InReview, InformationRequested, Settled, Rejected }

public sealed record Claim(
    Guid Id,
    string ClaimantId,
    ClaimCategory Category,
    DateOnly IncidentDate,
    string Description,
    long ReportedLossMinor,
    ClaimStatus Status,
    string? AssigneeId,
    int Version,
    DateTimeOffset UpdatedAt);

public sealed record ClaimHistoryEntry(
    int Version, ClaimStatus Status, string Actor, string? Note, DateTimeOffset OccurredAt);

public sealed record ClaimDetail(Claim Claim, IReadOnlyList<ClaimHistoryEntry> History);

public sealed record CreateClaim(ClaimCategory? Category, DateOnly? IncidentDate, string? Description, decimal? ReportedLoss);

public sealed record ClaimAction(int ExpectedVersion, string? Note);

public sealed record ClaimDecision(int ExpectedVersion, ClaimStatus? Status, string? Note);

public sealed record Workload(int Submitted, int InReview, int InformationRequested, int Settled, int Rejected,
    long OutstandingReportedLossMinor, string Currency);

public sealed record StatusChanged(Guid EventId, Guid ClaimId, int Version, ClaimStatus? PreviousStatus,
    ClaimStatus Status, DateTimeOffset OccurredAt);

public enum ClaimOperation { Assign, RequestInformation, Respond, Decide }

public sealed class ClaimConflict(string message) : Exception(message)
{
}

public static class ClaimWorkflow
{
    public static Claim Apply(Claim claim, ClaimOperation operation, DemoUser actor, int expectedVersion,
        ClaimStatus? decision, DateTimeOffset now)
    {
        if (claim.Version != expectedVersion)
            throw new ClaimConflict("This claim has changed. Refresh it and try again.");

        var nextStatus = (operation, claim.Status) switch
        {
            (ClaimOperation.Assign, ClaimStatus.Submitted) => ClaimStatus.InReview,
            (ClaimOperation.RequestInformation, ClaimStatus.InReview) when claim.AssigneeId == actor.Id =>
                ClaimStatus.InformationRequested,
            (ClaimOperation.Respond, ClaimStatus.InformationRequested) => ClaimStatus.InReview,
            (ClaimOperation.Decide, ClaimStatus.InReview) when claim.AssigneeId == actor.Id
                && decision is ClaimStatus.Settled or ClaimStatus.Rejected => decision.Value,
            _ => throw new ClaimConflict("That action is unavailable for this claim and account.")
        };

        return claim with
        {
            Status = nextStatus,
            AssigneeId = operation == ClaimOperation.Assign ? actor.Id : claim.AssigneeId,
            Version = claim.Version + 1,
            UpdatedAt = now
        };
    }
}
