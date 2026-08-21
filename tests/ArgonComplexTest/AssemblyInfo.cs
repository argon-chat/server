using NUnit.Framework;

// Fixtures run concurrently; the tests inside a fixture do not.
//
// That split is deliberate. Fixtures are independent — each owns its own Ion client, its own bearer
// token and its own freshly registered users — so nothing stops them overlapping, and overlapping is
// the whole point now that the server and its containers are started once for the assembly rather
// than once per fixture.
//
// Tests *within* a fixture stay serial because they legitimately share fixture state: `Order(n)`
// sequences, `FakedTestCreds` carried from the registration step into the assertion step, and the
// ambient token set by `SetAuthToken`. Tests that genuinely need two identities at once should ask
// for `CreateSessionAsync()` rather than reach for ParallelScope.All.
[assembly: Parallelizable(ParallelScope.Fixtures)]

// Four concurrent fixtures against a single Argon host: enough to collapse the run time, low enough
// that the shared Postgres/Redis/NATS containers and the Orleans silo are not the bottleneck.
// Override per machine with NUnit's `NumberOfTestWorkers` runsettings parameter.
[assembly: LevelOfParallelism(4)]
