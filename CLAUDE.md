# Argon server — instructions for agents

## A new integration fixture must be added to the shard partition (mandatory)

`tests/ArgonComplexTest` is run in parallel shards, and the fixture-to-shard partition is a literal
in `scipts/test-shards.ps1`, not something discovered at run time. `run-tests.ps1` verifies it before
every sharded run and **fails the whole job** when a `[TestFixture]` in the tree is in no shard —
CI then produces no test results at all, in every shard, which reads as "everything is broken"
rather than "one list is out of date":

```
The shard partition and tests/ArgonComplexTest disagree — in the tree
and in no shard: InviteCardEndpointTests. Fix scipts/test-shards.ps1
```

So, in the same change that adds, renames or deletes a fixture:

- Add its class name to `$generalShards` in `scipts/test-shards.ps1` — to the shard with the smaller
  measured total, which is written in the comment above each list.
- A `[NonParallelizable]` fixture, or one that pins ports or changes the schema, goes in
  `$topologyFixtures` instead; a `Presence*` fixture goes in `$presenceFixtures`.
- A fixture that is deliberately never run (every test `[Explicit]`) goes in `$excludedFixtures`
  **with its reason** — the check counts it as accounted for, not as unassigned.
- Then confirm it locally, which needs no containers and takes a second:

  ```
  ./scipts/test-shards.ps1 -Verify
  ```

  It prints the fixture counts per shard and exits non-zero on an unassigned, doubly-assigned or
  vanished fixture. After a run whose timings have shifted materially, re-balance instead of
  hand-editing: `./scipts/test-shards.ps1 -FromTrx artifacts/test-results -Shards 4` prints a fresh
  `$generalShards` literal to paste over the old one.
