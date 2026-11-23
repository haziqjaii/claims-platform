namespace Claims.Api;

public static class ClaimEndpoints
{
    public static void MapClaimEndpoints(this RouteGroupBuilder api)
    {
        var claims = api.MapGroup("/claims").RequireAuthorization();
        claims.MapGet("", (HttpContext context, ClaimsStore store) =>
            Results.Ok(store.List(DemoAccounts.FromPrincipal(context.User))));
        claims.MapGet("/{id:guid}", (Guid id, HttpContext context, ClaimsStore store) =>
            DetailResponse(store.Get(id, DemoAccounts.FromPrincipal(context.User))));
        claims.MapPost("", Submit).RequireAuthorization(policy => policy.RequireRole(DemoAccounts.Claimant));

        var officer = claims.MapGroup("").RequireAuthorization(policy => policy.RequireRole(DemoAccounts.Officer));
        officer.MapPost("/{id:guid}/assignment", (Guid id, ClaimAction request, HttpContext context, ClaimsStore store) =>
            Change(id, context, store, ClaimOperation.Assign, request.ExpectedVersion, null));
        officer.MapPost("/{id:guid}/information-request", (Guid id, ClaimAction request, HttpContext context, ClaimsStore store) =>
            Change(id, context, store, ClaimOperation.RequestInformation, request.ExpectedVersion, request.Note));
        officer.MapPost("/{id:guid}/decision", (Guid id, ClaimDecision request, HttpContext context, ClaimsStore store) =>
        {
            if (request.Status is not (ClaimStatus.Settled or ClaimStatus.Rejected))
                return Invalid("status", "Choose settled or rejected.");
            return Change(id, context, store, ClaimOperation.Decide, request.ExpectedVersion, request.Note, request.Status);
        });

        claims.MapPost("/{id:guid}/information-response", (Guid id, ClaimAction request, HttpContext context, ClaimsStore store) =>
            Change(id, context, store, ClaimOperation.Respond, request.ExpectedVersion, request.Note))
            .RequireAuthorization(policy => policy.RequireRole(DemoAccounts.Claimant));
        api.MapGet("/workload", (ClaimsStore store) => Results.Ok(store.GetWorkload()))
            .RequireAuthorization(policy => policy.RequireRole(DemoAccounts.Officer));
    }

    private static IResult Submit(CreateClaim request, HttpContext context, ClaimsStore store, TimeProvider clock)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddHours(8));
        var description = request.Description?.Trim();
        if (request.Category is null) return Invalid("category", "Choose motor or property.");
        if (request.IncidentDate is null || request.IncidentDate > today)
            return Invalid("incidentDate", "Enter an incident date on or before today in Malaysia.");
        if (string.IsNullOrWhiteSpace(description) || description.Length > 2000)
            return Invalid("description", "Enter an incident description of 1 to 2,000 characters.");
        if (request.ReportedLoss is not decimal loss || loss <= 0 || loss > 100_000_000 || loss != decimal.Round(loss, 2))
            return Invalid("reportedLoss", "Enter a MYR amount from 0.01 to 100,000,000.00 with up to two decimal places.");

        var result = store.Create(DemoAccounts.FromPrincipal(context.User), request.Category.Value,
            request.IncidentDate.Value, description, (long)(loss * 100));
        return Results.Created($"/api/claims/{result.Claim.Id}", result);
    }

    private static IResult Change(Guid id, HttpContext context, ClaimsStore store, ClaimOperation operation,
        int expectedVersion, string? note, ClaimStatus? decision = null)
    {
        if (expectedVersion < 1) return Invalid("expectedVersion", "Provide the current claim version.");
        note = note?.Trim();
        if (operation != ClaimOperation.Assign && (string.IsNullOrWhiteSpace(note) || note.Length > 1000))
            return Invalid("note", "Enter a note of 1 to 1,000 characters.");

        try
        {
            return DetailResponse(store.Change(id, DemoAccounts.FromPrincipal(context.User), operation, expectedVersion, note, decision));
        }
        catch (ClaimConflict error)
        {
            return Results.Problem(error.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult DetailResponse(ClaimDetail? detail) => detail is null ? Results.NotFound() : Results.Ok(detail);

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
