# Utility parity

All 72 tested utility outcomes match upstream in both Neo assets. Inputs cover
null, empty, whitespace, missing leading slashes, repeated slashes, Unicode,
escaped separators, dot segments, query-looking text and backslashes. Operations
are `UrlPath.IsValid`, `Split`, `Normalize` for exact/base paths, `HasPrefix`,
`StripPrefix`, `Validate.NotNull` and `NotNullOrEmpty`.

Results include returned values and array elements, argument exception types and
parameter names. The supplied `consumerArgument` name is preserved. This comparison
does not equate path normalization with authorization or filesystem containment.
Exception-message wording, stack traces, every Validate overload, MIME maps,
IP parsing, regex routing and randomized utility inputs are not covered here.

Evidence: the `utility/` cases in each implementation JSON. See the
[audit method and reproduction command](README.md).
