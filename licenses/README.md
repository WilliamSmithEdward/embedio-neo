# Embedded source notices

EmbedIO-Neo retains the original EmbedIO copyright and MIT permission text in
the root [LICENSE](../LICENSE). The following code is inherited source compiled
into the core library, not an external runtime package dependency.

| Source | Retained implementation | Notice |
| --- | --- | --- |
| MimeTypeMap | MIME associations in `EmbedIO.MimeType` | [MIT notice](MimeTypeMap-LICENSE) |
| Mono System.Net | Custom HTTP listener, connection, request/response, endpoint and stream implementations | [MIT notices](mono-System.Net-LICENSE) |
| websocket-sharp | Derived WebSocket framing, payload and protocol implementation | [MIT notice](websocket-sharp-LICENSE) |

Full text for these notices is also embedded in the root LICENSE, which every
packable project includes. The CLI package includes the core assembly and this
same LICENSE. Extras and CLI retain their separate original notices in
[embedio-extras-LICENSE](embedio-extras-LICENSE) and
[embedio-cli-LICENSE](embedio-cli-LICENSE); their respective packages include them.

## Verification references

The October 4, 2026 pre-push review used these pinned upstream revisions:

- [MimeTypeMap license](https://github.com/samuelneff/MimeTypeMap/blob/ba8286e081af665479f5d393c0d173c96eb26f41/LICENSE).
- [Mono HttpConnection source and notice](https://github.com/mono/mono/blob/0f53e9e151d92944cacab3e24ac359410c606df6/mcs/class/System/System.Net/HttpConnection.cs).
  The review also checked the headers of `HttpListener`, `HttpListenerContext`,
  `HttpListenerPrefixCollection`, `HttpListenerRequest`, `HttpListenerResponse`,
  `EndPointListener`, `EndPointManager`, `ListenerPrefix`, `RequestStream`, and
  `ResponseStream` at this revision. The combined notice preserves their distinct
  Novell and Xamarin copyright lines and MIT permission text.
- [websocket-sharp license](https://github.com/sta/websocket-sharp/blob/f7904e6afb934cfff9eda1f29ee6c1291b14c4bc/LICENSE.txt).
  Comparing `WebSocketFrame` and `PayloadData` corroborated the ancestry already
  acknowledged by EmbedIO's README, including payload-length encoding, masking,
  and close-code handling. The project-level MIT notice is preserved verbatim.

These are verification references, not claims that EmbedIO imported those exact
revisions. The inherited implementations have been adapted over time; this review
does not reconstruct every historical edit or attribute every line to one author.
No Mono or websocket-sharp implementation was removed or replaced in this cleanup.
