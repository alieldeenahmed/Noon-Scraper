using Xunit;

// Each test that touches the database gets its own database on one shared server,
// and the race tests open many connections at once. Unbounded parallelism just
// makes them starve each other (timeouts that have nothing to do with the code
// under test), so the degree is capped.
[assembly: CollectionBehavior(MaxParallelThreads = 6)]
