# Operator scripts

All of these share one output style, defined once in
[`pre-prod/lib/output.sh`](pre-prod/lib/output.sh) and sourced by each: blue phase headers, dim aligned
labels, green `ok`, yellow `warn`, a red `FAILED` on the way out. Colour switches off when stdout is not a
terminal or `NO_COLOR` is set, so piping any of these to a file or a CI log gives plain text.

The terminal is a report, not a transcript. These runs are measured in minutes or hours, and what an
operator needs from one is which phase it is in, what it decided, and what it ended as. Output that a tool
happens to produce along the way (a progress meter, a remote build's layer lines, ARM's JSON) is captured
to a file and named in the failure rather than printed. A conformance test fails if a script redefines a
helper or a colour token, because a second definition wins over the shared one and the two drift from
there.


These are the scripts, not copies of them. They run **from this repository** (lnicoara/cmms#3026); until
that change they were reference copies that ran from a `cmms` checkout, because the tools could not build
here.

## scripts/pre-prod/generate-pre-prod-small.sh

Produces the `small` dataset. One command, and that is the point: the invocation it replaces was four
things to get right together, and the worst of them fails silently. A missing `GENERATOR_PARAMS` runs on
`GeneratorOptions` defaults, whose seed is the **full** profile's, producing a small artifact whose key
space is a strict subset of the full one's. It prints a banner, self-validates, and reports success while
creating exactly the key-collision hazard the seed rule exists to prevent.

```bash
scripts/pre-prod/generate-pre-prod-small.sh                 # to $HOME/git/cmms-data/data/small
scripts/pre-prod/generate-pre-prod-small.sh --out=/abs/path
```

It derives the SDK onto `PATH`, pins the profile, writes the output directory (replacing what is there,
which is what generating means), and verifies the artifact against the pinned model before finishing. That
last check is free here and costs a multi-gigabyte upload and an image build when the load job discovers it
instead.

## scripts/pre-prod/generate-pre-prod-full.sh

Produces the `full` dataset: 4,000,000 work orders and 1,000,000 assets, roughly 42.7 million rows, about
3.7 GB, and around an hour. That is the scale epic lnicoara/cmms#2731 exists to measure.

```bash
scripts/pre-prod/generate-pre-prod-full.sh                 # to $HOME/git/cmms-data/data/full
scripts/pre-prod/generate-pre-prod-full.sh --out=/abs/path
```

A near-twin of the small script rather than a shared engine with a `--profile` flag, because the two differ
in the checks that matter. It refuses up front when free disk is short, rather than an hour in with a
half-written artifact that looks complete. It prints the size and duration before starting, so a long
silence is not read as a hang. And it asserts the generated row count afterwards.

That last one is the important difference, and it runs the opposite way to the small script's hazard. A run
that loses `GENERATOR_PARAMS` falls back to `GeneratorOptions` defaults, whose seed is `cmms-loadtest-v1`,
which is **`full.json`'s own seed**. So the artifact would carry 5,000 work orders under the full seed,
self-validate, and report success, and its manifest would look entirely correct. Keys are a pure function
of `(seed, kind, ordinal)`, so that key space is a strict subset of the real full one's, which is the
permanent-poisoning hazard hard rule 4 exists to prevent. The row count is the only thing that tells them
apart, so the script checks it rather than trusting that the flag was passed.

**Loading the result is separately blocked** by lnicoara/cmms#2970: the bulk copy runs at roughly nine
minutes per 100,000-row chunk, so 438 chunks need about 51 hours against a 12-hour job timeout.
lnicoara/cmms#2910 (index posture) is the likely fix. Generating is useful now; loading waits.

## scripts/pre-prod/load-pre-prod.sh

**It loads.** Typing the name is the intent, so there is no second flag confirming it. `--plan` is the dry
run: it stands everything up, verifies the staged artifact, and writes nothing. `--execute` is still
accepted and means nothing new, because every existing note says it.

```bash
scripts/pre-prod/load-pre-prod.sh --artifact-dir=$HOME/git/cmms-data/data/small
scripts/pre-prod/load-pre-prod.sh --artifact-dir=$HOME/git/cmms-data/data/small --plan
```

**It deletes nothing** (lnicoara/cmms#3025). `--clear-target` is removed and refused with an explanation
rather than silently ignored, so an old command from shell history does not read as a load that cleared
first. See [docs/load.md](../docs/load.md) for what it used to do and why the collision it existed for is
fixed at the generator instead.

**It does not touch the schema** (lnicoara/cmms#2978). It no longer builds, deploys, or starts the
migration job. That step ran on every invocation and is why pre-prod's schema sat 85 commits ahead of the
application it was serving: a load test was deciding what the environment ran. The conformance suite fails
if it comes back. If a load is refused for pending migrations, run the migration deliberately.

Building the image needs no credential. The script packs `Cmms.Domain`, `Cmms.Application` and
`Cmms.Infrastructure` out of your cmms checkout (`CMMS_REPO`, default `~/git/cmms`) into `.packages/` in
the build context, moments before the image is built. It **refuses** if that checkout is not at the commit
`<CmmsVersion>` pins: packing a different one under that version puts a different model behind it, the
build still succeeds, and the artifact then fits nothing.

Its preflights exist because each has already failed a real run: a dirty tree scoped to the paths that
reach the image, the tenant database existing, the artifact matching the pinned model, the staged copy
being the one just uploaded, and the deployed job carrying both the digest just built and no clear target.

## infra/

| template | what it stands up |
|---|---|
| `load-job.bicep` | `caj-cmms-load`, the in-VNet job that reads the artifact and bulk-copies it |
| `modules/load-test-store.bicep` | the storage account holding the staged artifact and the checkpoints |

`migrate-job.bicep` and `seed-job.bicep` are **not** here. They are the deploy path and live in `cmms`.

Regions and resource names are **passed in**, not computed from `uniqueString()`. Two real failures taught
that: a job cannot be relocated by redeploy (`InvalidResourceLocation` when a template defaulted to
`eastus2` against a `centralus` environment), and a computed SQL server name resolved to a server that does
not exist.

## tests/infra/load-job-conformance.test.sh

Static, Azure-free assertions over the delivery chain. Several are behavioural rather than greps, because a
grep for wiring passes while the wiring is unreadable at the far end: it extracts and runs the entrypoint's
`truthy()` against Bicep's literal `string(true)` output.

```bash
bash tests/infra/load-job-conformance.test.sh
```
