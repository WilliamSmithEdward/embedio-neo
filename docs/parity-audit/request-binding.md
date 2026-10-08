# Request binding parity

Query collections preserve repeated `a`/`a[]` values, empty values, valueless
flags, Unicode and plus-to-space decoding. Query fields and URL-encoded forms
agree. Conventional JSON DTOs, quoted integers, literal CRLF string content,
trailing commas, duplicate properties, empty input and legacy number spellings
also agree in the tested POST requests, on both listeners and Neo assets.

The following input differences belong to the characterized JSON migration
boundaries; exact old/new outcomes are held in the reviewed contract:

| Input | Upstream | Neo |
| --- | --- | --- |
| Lowercase `id`, `name`, `amount` | Keeps default DTO values | Binds the supplied values |
| `{"Id":"oops"}` | 200 with default Id 0 | 400 |
| `{"Id":1}garbage` | 200 with Id 1 | 400 |
| JSON root `null` | 400 | 200 with JSON null |

Neo's deliberate boundaries are described in
[JSON migration compatibility](../user-reports/json-migration-compatibility.md).
The null-root result characterizes the default nullable reference DTO endpoint;
it is not a recommendation to accept null application inputs without validation.
Do not change these boundaries merely to make upstream comparison green.

Multipart uploads, body-size limits, stream/chunk framing, custom deserializers,
all target CLR types and application input validation are outside this probe.
Evidence: `post-*`, `query` and `query-field*` HTTP cases. See the [audit method](README.md).
