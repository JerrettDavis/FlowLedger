# 0019 - FluentAssertions licensing: pin to 7.x

Status: accepted

## Context
FluentAssertions 8.x moved to a commercial licence (Xceed) for non-open-source use. 7.x remains Apache-2.0. The test projects used 8.11.0.
No 8.x-only API was in use; the test projects compile and pass unchanged on 7.x.

## Decision
Pin FluentAssertions to the latest 7.x (7.2.2, Apache-2.0) in `Directory.Packages.props`. Do not upgrade to 8.x.
Dependabot should ignore FluentAssertions major version bumps.

## Consequences
- No licence exposure now; zero test code change.
- 7.x receives no new features.
- Revisit later: migrating to AwesomeAssertions (Apache-2.0 fork, near-identical API; mostly a package and namespace swap) is the planned long-term path.
