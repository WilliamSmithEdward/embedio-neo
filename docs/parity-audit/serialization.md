# Serialization parity

Ordinary property DTOs preserve their tested parsed JSON values across upstream
and both Neo assets. The HTTP fixture additionally characterizes these observable
default serializer migration differences:

| Response graph | Upstream | Neo |
| --- | --- | --- |
| Public field `Value = 42` | `{}` | `{"Value":42}` |
| Derived row inside `List<BaseRow>` | Includes `Extra` and `Id` | Includes only `Id` |
| Same CLR row referenced twice | Second value is a `$circref` marker | Full value is repeated |
| Object cycle | 200 containing a reference marker | 500 serialization failure |
| UTC timestamp with fractional ticks | Omits fractional seconds and `Z` | Preserves them |

NaN emits the same tested named-string value. The public-field and derived-member
differences can expose or omit application data. They are documented boundaries
of the approved serializer migration, not a claim that every old client is safe.
Use concrete response DTOs and inspect member attributes/fields during migration.

The earlier [40-fixture default JSON audit](../user-reports/default-json-preservation-audit.md)
and [migration guide](../compatibility/migration.md) provide broader evidence and
consumer guidance. That audit uses its stated archived-source/SWAN baseline;
this suite executes published EmbedIO 3.5.2 end to end. Their fixture counts and
runtime claims must remain distinct.

Parsed JSON comparison does not establish byte-identical serialization, canonical
member ordering, database/ORM behavior, custom getters/converters, arbitrary
numeric precision or concurrent graph mutation. Evidence: `json-*` and `dto`
HTTP cases; SWAN reference IDs remain raw in reports and are normalized narrowly
for comparison. See the [audit method](README.md).
