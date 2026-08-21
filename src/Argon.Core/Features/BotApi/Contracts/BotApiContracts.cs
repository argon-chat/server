namespace Argon.Features.BotApi.Contracts;

// ─────────────────────────────────────────────────────────
//  Shared types (used across multiple interfaces)
// ─────────────────────────────────────────────────────────

public sealed record BotApiError(
    string Error,
    string? Message = null);

public sealed record DeletedResponse(
    bool Deleted);
