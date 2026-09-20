namespace NoonScraper.Data.Models;

// Lifecycle shared by every unit of work that is handed to GitHub Actions:
//
//   Pending  - a row exists and a workflow run has been requested
//   Running  - a worker claimed the row (Pending -> Running is one atomic update,
//              so a duplicate delivery of the same request can't run twice)
//   Completed / Failed - terminal
//
// The numeric values are what is stored in the database. Running was added after
// the first three, so it takes the next free number rather than renumbering.
public enum JobStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
    Running = 3
}
