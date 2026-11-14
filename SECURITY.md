# Security policy

BQL runs queries that people, programs and AI assistants type. A host should be able to hand it
untrusted query text. If you find a query that breaks that, we want to know.

## Reporting

Report privately through GitHub: **Security → Report a vulnerability** on this repository.
Please do not open a public issue for a vulnerability.

Include the query (or the smallest one you found), what it did, and the host setup if it matters:
which source, which `BqlEngineOptions`. We acknowledge within two working days, assess within
five, and aim to release a fix for critical and high issues within thirty days. We credit
reporters unless asked not to.

## In scope

- A query that makes the engine use unbounded memory or time despite `BqlEngineOptions` limits.
- A query that crashes the host process (stack overflow), or makes the engine throw anything other
  than `BqlParseException` or its subclasses (`BqlExecutionException`, `BqlLimitExceededException`).
- A query that reads rows outside `AllowedDatasets`.
- A query that reaches the file system, the network, processes or the environment. The engine has
  no such access; anything that gets it is a bug.
- Data from one query appearing in another.

## Out of scope

- What a host's own `IBqlSource`, `IBqlKnowledgeProvider` or `IBqlGeoIpLookup` does.
- Limits a host raised or turned off itself.

## Supported versions

Only the latest release, and `main`.
