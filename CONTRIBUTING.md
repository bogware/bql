# Contributing

Read [CLAUDE.md](CLAUDE.md) first. Its invariants hold for people too: no field names in the
engine, no magic numbers, pushdown is a promise, a source that does nothing gets the right answer.

## Working

1. Branch from `main`.
2. `dotnet build -c Release` with no warnings, and `dotnet test` green.
3. A new stage or function: AST, parser, engine, the guide, and a test in `BqlLanguageTests`.
   Every query in `docs/BQL-GUIDE.md` runs as a test, so a guide example that does not work fails
   the build.
4. A bug fix comes with a test that failed before it.
5. Anything a query can make bigger (memory, time, depth) gets a limit in `BqlEngineOptions`,
   `BqlLimits` or `BqlParserLimits`, with a default and a doc comment.
6. Conventional commit titles. Squash merge.

## Dependencies

The libraries take no package dependencies; keep it that way. Test packages are pinned in
`Directory.Packages.props` and updated by hand.

## Writing

Plain and short. Say what a thing does.

## License

Contributions are licensed under AGPL-3.0, as the project is.
