# V1 fuzz-finding review handoff

This record preserves the release-relevant outcome of the 2026-08-04
coverage-guided run without placing triggering inputs in public documentation.
It is a defensive review handoff, not approval to publish.

## Evidence identity

| Field | Value |
| --- | --- |
| Source commit exercised | `06d3a7ee3654930a60a5420d0d2bc17e95087960` |
| Coverage-guided workflow | [Run 30921849042](https://github.com/jphgardner/BlueTusk/actions/runs/30921849042) |
| Build workflow | [Run 30921843607](https://github.com/jphgardner/BlueTusk/actions/runs/30921843607), 23 executed jobs passed |
| Security workflow | [Run 30921843012](https://github.com/jphgardner/BlueTusk/actions/runs/30921843012), both jobs passed |
| Fuzz result | Six targets passed; three targets failed closed |
| Timeouts saved | Zero |
| Initial source-only remediation | Local commit `0032329` |

The failing jobs retained their private GitHub Actions artifacts for independent
security review. Raw findings are intentionally absent from this record and
from the release evidence bundle. AFL's saved-crash count is a triage count,
not proof of the same number of distinct defects or vulnerabilities.

## Defensive triage

| Target | Saved crashes | Source-level boundary reviewed | Initial remediation |
| --- | ---: | --- | --- |
| `binary-copy` | 6 | Binary timestamp values outside the CLR-representable range | Reject out-of-range finite timestamps as malformed values before `DateTime` construction |
| `array-codec` | 7 | Declared element length, text nesting depth and PostgreSQL-to-CLR array-bound translation | Check remaining payload before slicing, enforce six dimensions and range-check CLR bounds |
| `composite-codec` | 1 | Declared record-field length | Check the declared field length against the remaining record payload before slicing |

The same remediation also repairs finding archival so SHA-256 names are
generated with valid PowerShell static-method syntax and zero/one/many finding
sets behave consistently under strict mode.

## Local validation of the remediation

The source-only remediation was validated without recovering or replaying the
retained crash inputs:

- all 121 Release projects built with zero warnings and zero errors;
- 45 test result files recorded 1,816 passes, 381 environment-gated tests and
  zero failures;
- the focused TypeSystem suite passed all 211 tests;
- full-workspace formatting required no changes;
- all 13 Angular tests and the production website build passed;
- the npm audit reported zero vulnerabilities; and
- the unified V1 engineering gate passed all 13 source gates.

These results establish ordinary regression safety. They do not close the fuzz
finding because only an independently reviewed clean coverage-guided run at the
final candidate commit can do that.

## Independent review procedure

The reviewer must:

1. use the retained workflow artifacts in a controlled security workspace;
2. determine whether each saved input maps to the source-level boundaries
   above or identifies an additional defect;
3. keep raw inputs out of public issues, logs and approval records;
4. review the remediation and any further changes independently;
5. ensure accepted minimized regressions are handled through the repository's
   controlled encoded-corpus process;
6. require a new manual `fuzzing.yml` run at the final candidate SHA; and
7. record the successful run ID in the protected V1 candidate workflow.

Any code, dependency, workflow, version or documentation change after the run
changes the candidate SHA and invalidates that run as release evidence.

## Closure criteria

The finding is closed only when all of the following are true:

- the independent security review has no unresolved blocking finding;
- all nine manual fuzz jobs complete for at least 3,600 seconds per target;
- the workflow event is `workflow_dispatch`, its `head_sha` equals the final
  candidate commit, and its conclusion is `success`;
- no crash or hang finding is saved;
- deterministic corpus replay passes in the same workflow;
- the exact run is present once in `candidate.json`; and
- the protected candidate-readiness workflow accepts the complete six-run
  manifest.

Until then, stable publication remains disabled.
