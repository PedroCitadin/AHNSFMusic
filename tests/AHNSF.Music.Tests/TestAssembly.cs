using Xunit;

// The original player test verifies exclusive access to the shared silence.mp3 fixture.
// Other collections copy/read that same file, and SQLite fixtures clear global pools.
// Explicit concurrency scenarios still run parallel tasks inside their own tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
