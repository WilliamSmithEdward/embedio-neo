# Structured Field dictionary fixtures

Source: https://github.com/httpwg/structured-field-tests/tree/00462dd7938b43bf596cb2af6a373d9c928a6cbe

`dictionary.json` contains every record whose `header_type` is `dictionary`
from the root JSON files at that revision (432 cases). Records preserve the
original fields and add `source`, the original filename. They were concatenated
in filename order and formatted as JSON; no expected result was changed.
The adjacent license retains the upstream IETF Trust notice and terms.

The priority tests check parsing success/failure and project the expected
Dictionary onto the RFC 9218 `u` and `i` parameters. They do not claim to test
general-purpose serialization or expose a general Structured Fields API.
