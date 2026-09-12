# Work Queue

| ID | Work | Status | Tags | Priority |
|---|---|---|---|---|
| ROS-INSTALL-2-0-1-main-78-1 | ROS-INSTALL-2-0-1-main-78-1 | complete |  |  |
| WI-0001 | Install State-Directed Engineering (SDE) v1.1.1 methodology inputs | complete | tooling, methodology | medium |
| WI-0002 | Convert Strata design notebook into ROS requirements, decisions, experiments and work items | complete | strata, planning, requirements | high |
| WI-0003 | S1: Strata four-tier F# solution skeleton with mechanical architecture check | complete | strata, slice-s1 | high |
| WI-0004 | S1: canonical semantic model for PostgreSQL schema objects | complete | strata, slice-s1 | high |
| WI-0005 | S1: PostgreSQL catalog introspection adapter | complete | strata, slice-s1 | high |
| WI-0006 | S1: deterministic semantic snapshot serialization | complete | strata, slice-s1 | medium |
| WI-0007 | S2: PostgreSQL parser adapter over pgsqlparser | complete | strata, slice-s2 | high |
| WI-0008 | S2: lexical scope resolution over the parse tree | complete | strata, slice-s2 | high |
| WI-0009 | S2: reference extraction with explicit resolution states | complete | strata, slice-s2 | high |
| WI-0010 | S2: effect classification by consequence | complete | strata, slice-s2 | medium |
| WI-0011 | S2: SQL corpus indexing with provenance | complete | strata, slice-s2 | medium |
| WI-0012 | S3: dependency and relationship graph with evidence | complete | strata, slice-s3 | medium |
| WI-0013 | S3: targeted retrieval queries and CLI output | complete | strata, slice-s3 | medium |
| WI-0014 | Spike B remainder: live-server binding comparison | complete | strata, spike | high |
| WI-0015 | Record engineering metrics and SDE routing artefacts for the first Strata increment | complete | strata, documentation | medium |
| WI-0016 | Record final metrics and routing artefacts for slices S1-S3 | complete | strata, documentation | medium |
| WI-0017 | S2/S3: index a SQL corpus from disk and build observed relationships from real data | complete | strata, slice-s3 | high |
| WI-0018 | Report unsupported join shapes as explicit analysis gaps | complete | strata, honesty | high |
| WI-0019 | Surface parser/server dialect divergence as a first-class analysis state | complete | strata, honesty | high |
| WI-0020 | Spike D: measure agent context cost of Strata retrieval versus raw corpus | complete | strata, spike | high |
| WI-0021 | Reduce repeated scope overhead without weakening the safety property | complete | strata, optimisation | high |
| WI-0022 | Evaluate whether targeted retrieval loses information the raw context has | complete | strata, spike | high |
| WI-0023 | Spike D correctness half: run agents under both conditions | complete | strata, spike | high |
| WI-0024 | Spike D-2: does Strata help with cross-module seam errors? | complete | strata, spike | high |
| WI-0025 | Column-level reader tracking: answer 'what breaks if I drop this column' | complete | strata, slice-s3 | high |
| WI-0026 | Spike D-3: seam correctness at a scale where reading everything is infeasible | complete | strata, spike | high |
| WI-0027 | Correct the indexing-time figure in the scale evidence records | complete | strata, documentation | low |
| WI-0028 | Spike D-4: messy corpus — unqualified names, shadowing, dynamic SQL, cross-schema collisions | complete | strata, spike | high |
| WI-0029 | Build a deterministic pre-deployment gate and test HY-STRATA-2026-6F14 | complete | strata, slice-s4 | high |
| WI-0030 | Profile and fix indexing cost: single-pass extraction and hand-written gap formatting | complete | strata, performance, profiling | high |
| WI-0031 | Qualified operator reported as both a join and an unmodelled shape | complete | strata, parser, correctness | medium |
| WI-0032 | Record Q-003 decision: desired state is DACPAC-style object files | complete | strata, desired-state, decision | high |
| WI-0033 | Survey the declarative schema landscape and candidate fixtures | complete | strata, competitive-landscape, fixtures | high |
| WI-0034 | Slice V: validate candidate SQL against the schema (PR-018) | complete | strata | high |
| WI-0035 | Slice P: strata.json project file and desired-state loader (PR-025) | complete | strata | high |
| WI-0036 | Slice D: diff desired against actual with dry-run output (PR-022, PR-023) | complete | strata | high |
| WI-0037 | Slice X: execute a plan against the database (PR-024) | complete | strata | high |
| WI-0038 | Requirements and decisions for validation and deployment capabilities | complete | strata, requirements | high |
| WI-0039 | Record PostgreSQL-only scope and the dialect port's known leaks | complete | strata, dialect, scope | medium |
| WI-0040 | Make the project manifest optional and require opt-in for removals | complete | strata, project | high |
| WI-0041 | Validate SQL routine bodies instead of reporting them unverifiable | complete | strata, validation | high |
| WI-0042 | Probe diff coverage: false clean on constraints, 96% of tables uncreatable | complete | strata, diff, coverage | high |
| WI-0043 | Compare constraints and defaults; report what cannot be compared | complete | strata, diff | high |
| WI-0044 | Create tables from the declaring file; canonicalise serial pseudo-types | complete | strata, deployment | high |
| WI-0045 | Views and routines as desired state; disclose uncompared indexes | complete | strata, desired-state | high |
| WI-0046 | Compare view definitions by normalising declared DDL through the server | complete | strata, diff | high |
| WI-0047 | Compare routine bodies directly from prosrc | complete | strata, diff | high |
| WI-0048 | Compare default and check expressions via shadow normalisation | complete | strata, diff | high |
| WI-0049 | Rename detection via -- strata:renamed_from (Q-004) | complete | strata, rename | high |
| WI-0050 | Indexes as desired state | complete | strata, desired-state | high |
| WI-0051 | Triggers as desired state | complete | strata, desired-state | high |
| WI-0052 | Table creations are not ordered by foreign key | complete | strata, diff | high |
| WI-0053 | The live test fixture is not reproducible | complete | strata, testing | medium |
| WI-0054 | An unnamed constraint never converges | complete | strata, diff | high |
| WI-0055 | apply has no way to accept a requires-approval finding | complete | strata, deployment | high |
| WI-0056 | Reference data as desired state | complete | strata, desired-state | high |
| WI-0057 | A declared row colliding with an undeclared deployed row is caught late | complete | strata, reference-data | medium |
| WI-0058 | Constraint differences cannot be applied | complete | strata, diff | high |
| WI-0059 | CI builds nothing and runs no tests | complete | strata, ci | high |
| WI-0060 | Every PR commit ran CI twice | complete | strata, ci | medium |
| WI-0061 | ros work complete does not update queue.md when an item is captured and completed in the same run | blocked | ros, tooling | low |
| WI-0062 | CLI help claims narrower behaviour than the code has | complete | strata, docs | low |
| WI-0063 | Record metrics for the validation and deployment increment | complete | strata, metrics | medium |
| WI-0064 | Strata cannot create a schema | complete | strata, desired-state | high |
| WI-0065 | Sequences as desired state | complete | strata, desired-state | high |
| WI-0066 | Grants as desired state | complete | strata, desired-state | high |
| WI-0067 | A column-level GRANT was widened to the whole table | complete | strata, security | high |
| WI-0068 | Awkward-forms corpus: the server as oracle for declaration reading | complete | testing, postgres, er-008 | high |
| WI-0069 | Schema and routine grants as desired state | complete | postgres, privileges, er-008 | high |
| WI-0070 | Attribute the awkward-forms corpus fixtures to a work item | complete | ros, process | medium |
| WI-0071 | State each awkward-forms fixture's contract in the file itself | complete | ros, process | medium |
| WI-0072 | Make local ROS validation match CI's scope | complete | ros, ci | high |
| WI-0073 | Column-level privileges as desired state | complete | postgres, privileges, er-008 | high |
| WI-0074 | SchemaDiff.run took sixteen positional arguments, two of them transposable | complete | refactor, safety | high |
| WI-0075 | Report row-level security instead of not looking at it | complete | postgres, security, er-008 | high |
| WI-0076 | Row-level security policies as desired state | complete | postgres, security, er-008 | high |
| WI-0077 | Extensions as desired state | complete | postgres, er-008 | medium |
| WI-0078 | Destructive changes never receive an Allow verdict | ready | strata | high |
| WI-0079 | Require an explicit include list in strata.json | captured | strata | high |
| WI-0080 | Derive the managed schema set from directory names and remove managedSchemas | captured | strata | high |
| WI-0081 | Project adoption and reconciliation commands | captured | strata | medium |
| WI-0082 | Hoist shadow normalisation out of the CLI ahead of the compile split | captured | strata | high |
| WI-0083 | strata compile emits a deployable artifact | captured | strata | high |
| WI-0084 | strata deploy reads an artifact rather than the source tree | captured | strata | high |
| WI-0085 | strata validate against an artifact, with no database | captured | strata | medium |
| WI-0086 | strata drift reports whether a server still matches an artifact | captured | strata | low |
| WI-0087 | Type-check queries with PREPARE | captured | strata | medium |
| WI-0088 | Declared project invariants as compile-time checks | captured | strata | low |
| WI-0089 | Sign the compiled artifact | captured | strata | low |
| WI-0090 | Record the compile/deploy design decisions | complete | strata, decisions | high |
