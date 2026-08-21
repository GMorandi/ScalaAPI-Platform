# Release tooling script tests

Fixture-driven tests for the release evidence scripts. They run locally
without a real GitHub Actions run: `fakes/gh` serves the canned API responses
and test result artifacts under `fixtures/`.

Run all tests:

```bash
scripts/tests/run-tests.sh
```

Required commands: `bash`, `jq`, `git`, `zip`, `unzip`, `sha256sum`.

Layout:

- `run-tests.sh` — plain bash runner executing every `test-*.sh` here.
- `fakes/gh` — fake GitHub CLI; set `FAKE_GH_FIXTURES` to the fixture root and
  `FAKE_GH_ARTIFACTS_FILE` to swap the artifacts listing (negative cases).
- `fixtures/jobs.json` — workflow run jobs listing (10 green gates, one
  skipped job, one in-progress job).
- `fixtures/artifacts.json` / `artifacts-missing.json` — run artifact listings.
- `fixtures/artifacts/<name>/` — extracted artifact content (TRX, ctest JUnit
  XML, Playwright JSON, integration assertion log); zipped on demand.
- `fixtures/evidence/` — release manifest and gates.json fixtures for
  `generate-release-evidence.sh`.

The suite is wired into the `.NET Build` workflow as the
"Test release tooling scripts" step.
