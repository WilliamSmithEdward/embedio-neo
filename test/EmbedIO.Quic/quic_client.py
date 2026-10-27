"""aioquic's connect() with an IPv4 client socket for an IPv4 server.

aioquic binds every client to an IPv6 dual-stack socket on port 0. macOS chooses that
port without checking IPv4 sockets, so the client can share its port with one, the
test host's own listener included. Every reply the server sends to 127.0.0.1 at that
port then reaches the IPv4 socket, and the client times out without hearing from the
server. An IPv4 socket is never given a port another IPv4 socket holds.
"""
import asyncio
import ipaddress
import socket
from contextlib import asynccontextmanager

from aioquic.asyncio import QuicConnectionProtocol, connect as connect_dual_stack
from aioquic.quic.configuration import QuicConfiguration
from aioquic.quic.connection import QuicConnection


def client_socket(address, local_port=0):
    """An IPv4 UDP socket for a client of the IPv4 server at address."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.bind(('127.0.0.1' if ipaddress.ip_address(address[0]).is_loopback else '0.0.0.0', local_port))
    except BaseException:
        sock.close()
        raise
    return sock


@asynccontextmanager
async def connect(host, port, *, configuration=None, create_protocol=QuicConnectionProtocol,
                  session_ticket_handler=None, stream_handler=None, token_handler=None,
                  wait_connected=True, local_port=0):
    """Takes aioquic 1.6.1's connect() arguments and follows its steps, except for the socket."""
    loop = asyncio.get_running_loop()
    infos = await loop.getaddrinfo(host, port, type=socket.SOCK_DGRAM)
    address = infos[0][4]
    if len(address) != 2:
        async with connect_dual_stack(host, port, configuration=configuration, create_protocol=create_protocol,
                                      session_ticket_handler=session_ticket_handler, stream_handler=stream_handler,
                                      token_handler=token_handler, wait_connected=wait_connected,
                                      local_port=local_port) as protocol:
            yield protocol
        return
    if configuration is None:
        configuration = QuicConfiguration(is_client=True)
    if configuration.server_name is None:
        configuration.server_name = host
    connection = QuicConnection(configuration=configuration, session_ticket_handler=session_ticket_handler,
                                token_handler=token_handler)
    transport, protocol = await loop.create_datagram_endpoint(
        lambda: create_protocol(connection, stream_handler=stream_handler), sock=client_socket(address, local_port))
    try:
        protocol.connect(address, transmit=wait_connected)
        if wait_connected:
            await protocol.wait_connected()
        yield protocol
    finally:
        protocol.close()
        await protocol.wait_closed()
        transport.close()
