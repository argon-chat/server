using NUnit.Framework;

// These are pure-logic tests: no containers, no server, nothing shared between fixtures — so run
// everything concurrently, including tests within a fixture.
[assembly: Parallelizable(ParallelScope.All)]
