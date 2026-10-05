using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using EmbedIO.WebSockets;

namespace EmbedIO.Cli.TestPlugin
{
    public sealed class PingController : WebApiController
    {
        [Route(HttpVerbs.Get, "/cli-ping")]
        public object Ping() => new { message = "pong" };
    }

    public sealed class EchoSocket : WebSocketModule
    {
        public EchoSocket() : base("/cli-echo", true) { }
        protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            => SendAsync(context, buffer);
    }
}
