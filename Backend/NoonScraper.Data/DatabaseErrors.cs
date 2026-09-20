using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace NoonScraper.Data;

public static class DatabaseErrors
{
    // A unique index (or constraint) rejected the write. With a constraint name,
    // only that one - so a caller handling "someone else already inserted this URL"
    // doesn't also swallow an unrelated violation.
    public static bool IsUniqueViolation(DbUpdateException ex, string? constraintName = null) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && (constraintName is null || pg.ConstraintName == constraintName);
}
