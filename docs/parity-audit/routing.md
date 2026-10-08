# Routing parity

Successful integer binding, negative numbers, optional parameters, escaped spaces,
Unicode route values, exact-route rejection of extra segments and base-route
subpaths agree in both listeners and Neo assets. GET, PUT and DELETE use the same
controller declarations. Unsupported methods return 405 in the tested controller;
missing routes return 404, and an application exception remains 500. A subsequent
valid request succeeds.

`/api/number/no` and `/api/number/2147483648` return upstream 500 and Neo 400.
This is the explicitly approved malformed typed-route change in
[issue #163](https://github.com/WilliamSmithEdward/embedio-neo/issues/163) and
[PR #165](https://github.com/WilliamSmithEdward/embedio-neo/pull/165), not an
unexplained regression. [Typed-route guidance](../user-reports/typed-route-validation.md)
documents the broader validated correction.

The probe does not compare every numeric/date/enum culture conversion, custom
route resolver, registration conflict or route-cache concurrency scenario. The
independent Neo suite covers additional cases without establishing full upstream
equivalence. Evidence: named `http/<listener>/` route/verb cases in the reports;
see the [audit method](README.md).
