"""Whether a dual-stack UDP socket bound to port 0 can take a port an IPv4 socket holds.

aioquic's connect() binds an AF_INET6 socket to ('::', 0) with IPV6_V6ONLY off. If the
kernel gives that socket a port an IPv4 socket already holds, a datagram sent to
127.0.0.1 at that port can reach the IPv4 socket instead of the client.
"""
import argparse
import errno
import json
import platform
import select
import socket
import sys
import time


def raise_descriptor_limit(wanted):
    try:
        import resource
    except ImportError:
        return None
    soft, hard = resource.getrlimit(resource.RLIMIT_NOFILE)
    target = wanted if hard == resource.RLIM_INFINITY else min(wanted, hard)
    if soft < target:
        resource.setrlimit(resource.RLIMIT_NOFILE, (target, hard))
    return list(resource.getrlimit(resource.RLIMIT_NOFILE))


def delivery(sender, port, sockets):
    """Sends one datagram to 127.0.0.1:port and names the socket that receives it."""
    payload = f'probe-{port}-{time.monotonic_ns()}'.encode()
    sender.sendto(payload, ('127.0.0.1', port))
    readable, _, _ = select.select(list(sockets), [], [], 1.0)
    return [sockets[sock] for sock in readable if sock.recv(2048) == payload]


def dual_stack():
    sock = socket.socket(socket.AF_INET6, socket.SOCK_DGRAM)
    sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
    sock.bind(('::', 0, 0, 0))
    return sock


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--holders', type=int, default=256)
    parser.add_argument('--binds', type=int, default=8000)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    limit = raise_descriptor_limit(args.holders + 128)
    sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sender.bind(('127.0.0.1', 0))

    # IPv4 sockets on ephemeral ports, half on the loopback address as the .NET
    # listener binds, half on the wildcard as many daemons do.
    holders = {}
    for index in range(args.holders):
        address = '127.0.0.1' if index % 2 == 0 else '0.0.0.0'
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.bind((address, 0))
        holders[sock.getsockname()[1]] = (sock, address)
    low, high = min(holders), max(holders)

    # aioquic's client socket, bound and closed once per case as the harness does.
    collisions = []
    ports = set()
    for _ in range(args.binds):
        sock = dual_stack()
        try:
            port = sock.getsockname()[1]
            ports.add(port)
            if port in holders:
                holder, address = holders[port]
                collisions.append({'port': port, 'holder': address,
                                   'receivers': delivery(sender, port, {sock: 'dual-stack', holder: 'ipv4-' + address})})
        finally:
            sock.close()

    # The other order: an explicit IPv4 bind to a port a dual-stack socket holds.
    explicit = []
    for _ in range(32):
        sock = dual_stack()
        port = sock.getsockname()[1]
        outcome = {'port': port}
        for address in ('127.0.0.1', '0.0.0.0'):
            other = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            try:
                other.bind((address, port))
                outcome[address] = 'bound'
                outcome[address + ' receivers'] = delivery(sender, port, {sock: 'dual-stack', other: 'ipv4-' + address})
            except OSError as error:
                outcome[address] = errno.errorcode.get(error.errno, str(error.errno))
            finally:
                other.close()
        explicit.append(outcome)
        sock.close()

    received = {}
    for collision in collisions:
        key = collision['holder'] + ' -> ' + (','.join(collision['receivers']) or 'none')
        received[key] = received.get(key, 0) + 1
    result = {
        'platform': platform.platform(), 'python': sys.version.split()[0], 'descriptorLimit': limit,
        'holders': len(holders), 'holderPortSpan': [low, high], 'binds': args.binds,
        'distinctDualStackPorts': len(ports), 'dualStackPortSpan': [min(ports), max(ports)],
        'collisions': len(collisions), 'collisionReceivers': received, 'collisionSamples': collisions[:20],
        'explicitIpv4Bind': {
            address: {state: sum(1 for item in explicit if item[address] == state)
                      for state in sorted({item[address] for item in explicit})}
            for address in ('127.0.0.1', '0.0.0.0')
        },
        'explicitSamples': explicit[:4],
    }
    with open(args.output, 'w', encoding='utf-8') as handle:
        json.dump(result, handle, indent=2)
    print(json.dumps({key: result[key] for key in ('platform', 'holders', 'binds', 'collisions',
                                                    'collisionReceivers', 'explicitIpv4Bind')}))


if __name__ == '__main__':
    main()
