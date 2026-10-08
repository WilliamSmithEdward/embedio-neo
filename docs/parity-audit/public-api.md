# Public API parity

The audit inventories 142 exported upstream core types and 148 Neo core types.
Every upstream exported type remains present. It compares declared public and
protected methods/constructors, visible fields/constants, property/event accessors,
base types and interfaces. Method parameter names, optional flags and default
values are included. Generic base/interface names are compared structurally,
without treating the different package assembly versions as a removed interface.

Twelve upstream entries differ in each Neo asset: five JSON overloads taking SWAN
options/case types, and seven base-type references to
`Swan.Configuration.ConfiguredObject`. These belong to the explicitly approved
[SWAN source/binary migration](../compatibility/migration.md). Use Neo's configuration
base type and .NET JSON options, and rebuild consumers. No other missing entry
was observed. Both Neo assets have identical inventoried surfaces.

Neo additionally exports `PreRequestModule`, `ConfiguredObject`, `Log`, `Json`,
`ServerSentEventWriter` and `WebSocketMessageModule`; additions are recorded in
the report rather than treated as regressions.

This is a reflection inventory and common-consumer compilation check, not a full
binary compatibility certification. Generic constraints, every visibility/virtual
metadata flag, inherited SWAN members, custom attributes, third-party assembly
identity and loadability of previously compiled applications require a deeper
metadata/binary audit. The shared consumer deliberately uses the common session
indexer instead of a SWAN extension method.

Evidence: `api` in each implementation report, `removedApiEntries` and
`addedApiEntries` in `summary.json`, and the exact reviewed entries in
`test/EmbedIO.Compatibility/reviewed-differences.json`. See the [audit method](README.md).
