# Finding the former Xamarin.Forms sample

[Upstream #496](https://github.com/unosquare/embedio/issues/496), reported by
om297 in December 2020, says the Xamarin.Forms sample link was broken. The only
subsequent comment is an automated stale notice; there is no maintainer diagnosis
or replacement link in that discussion.

## Historical source

The reported `src/EmbedIO.Forms.Sample` directory exists at the archived
repository's inspected revision. Browse the
[original sample directory](https://github.com/unosquare/embedio/tree/6a6bb4117ae6073779049fafb681c76970aa7314/src/EmbedIO.Forms.Sample)
and its
[README](https://github.com/unosquare/embedio/blob/6a6bb4117ae6073779049fafb681c76970aa7314/src/EmbedIO.Forms.Sample/README.md).
These links pin the inspected revision instead of depending on a moving branch.
This verifies present source availability; it does not establish why the link
failed in 2020 or whether the legacy application builds with a current toolchain.
Credit for that example belongs to the original EmbedIO contributors. Its README
uses the old `Unosquare.Labs.EmbedIO` namespaces and `Xam.Plugin.WebView`;
do not copy its package instructions or code unchanged into a Neo application.

EmbedIO-Neo intentionally removed the legacy Xamarin sample and its platform
and WebView dependencies during the fork baseline. It retains the library's
.NET Standard 2.0 target alongside .NET 10. Target compatibility alone does not
prove a particular Xamarin runtime, linker or TLS provider works. See the
[migration notes](../compatibility/migration.md) for the approved fork changes.

## Maintained examples

Start with the [Getting started programs](../guides/getting-started/README.md)
for complete JSON, static-file and controller examples. They show the server
API, request URLs and shutdown; they are desktop programs, not Xamarin apps.

For a modern mobile application, choose the guidance for the actual platform:

- [MAUI Android](../platforms/maui-android.md): application-owned listener
  lifetime, background work and restart diagnostics.
- [MAUI Mac Catalyst](../platforms/maui-mac-catalyst.md): local hosting and
  sandbox entitlements.
- [MAUI HTTPS validation](../platforms/maui-https-validation.md): native clients,
  WebViews, certificate trust, test-only fixtures and reproduction commands for
  Windows, iOS, Mac Catalyst and Android.

The MAUI fixtures live outside the ordinary solution and production packages.
Their successful CI results demonstrate the recorded modern app models only;
they do not validate the original Xamarin.Forms sample. A remaining legacy
failure needs a minimal runnable application, runtime/platform versions and
sanitized errors. Do not send certificate private keys or passwords.
