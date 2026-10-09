// Test-only macOS syscall ordering probe. Never linked into production packages.
#include <arpa/inet.h>
#include <errno.h>
#include <stdatomic.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

#define TRACE_LIMIT 131072u
#define FD_LIMIT 65536u
struct trace_event {
    uint64_t start, end;
    int kind, fd, port, requested, result, error, cycle;
};
static struct trace_event events[TRACE_LIMIT];
static atomic_uint ready[TRACE_LIMIT], ports[FD_LIMIT], next_event, untracked, enabled = 1;

static uint64_t now_ns(void)
{
    struct timespec t;
    if (clock_gettime(CLOCK_MONOTONIC, &t) != 0) return 0;
    return (uint64_t)t.tv_sec * 1000000000u + (uint64_t)t.tv_nsec;
}
static void record_event(struct trace_event event)
{
    if (!atomic_load_explicit(&enabled, memory_order_relaxed)) return;
    unsigned index = atomic_fetch_add_explicit(&next_event, 1, memory_order_relaxed);
    if (index >= TRACE_LIMIT) return;
    events[index] = event;
    atomic_store_explicit(&ready[index], 1, memory_order_release);
}
static int port_of(const struct sockaddr *address, socklen_t length)
{
    if (address == NULL || length < offsetof(struct sockaddr, sa_family) + sizeof(address->sa_family)) return 0;
    if (address->sa_family == AF_INET && length >= sizeof(struct sockaddr_in))
        return ntohs(((const struct sockaddr_in *)address)->sin_port);
    if (address->sa_family == AF_INET6 && length >= sizeof(struct sockaddr_in6))
        return ntohs(((const struct sockaddr_in6 *)address)->sin6_port);
    return 0;
}
static int traced_bind(int fd, const struct sockaddr *address, socklen_t length)
{
    int original_error = errno, type = 0;
    socklen_t size = sizeof(type);
    int udp = getsockopt(fd, SOL_SOCKET, SO_TYPE, &type, &size) == 0 && type == SOCK_DGRAM;
    int requested = udp ? port_of(address, length) : 0;
    errno = original_error;
    uint64_t start = now_ns();
    int result = bind(fd, address, length), saved_error = errno;
    uint64_t end = now_ns();
    if (udp) {
        int port = requested;
        struct sockaddr_storage local;
        size = sizeof(local);
        if (result == 0 && getsockname(fd, (struct sockaddr *)&local, &size) == 0)
            port = port_of((struct sockaddr *)&local, size);
        if (result == 0) {
            if (fd >= 0 && (unsigned)fd < FD_LIMIT)
                atomic_store_explicit(&ports[fd], (unsigned)port, memory_order_relaxed);
            else atomic_fetch_add_explicit(&untracked, 1, memory_order_relaxed);
        }
        record_event((struct trace_event){start, end, 1, fd, port, requested, result, result == 0 ? 0 : saved_error, 0});
    }
    errno = saved_error;
    return result;
}
static int traced_close(int fd)
{
    unsigned port = fd >= 0 && (unsigned)fd < FD_LIMIT
        ? atomic_exchange_explicit(&ports[fd], 0, memory_order_relaxed) : 0;
    uint64_t start = port ? now_ns() : 0;
    int result = close(fd), saved_error = errno;
    if (port) record_event((struct trace_event){start, now_ns(), 2, fd, (int)port, 0, result, result == 0 ? 0 : saved_error, 0});
    errno = saved_error;
    return result;
}

// dyld does not interpose calls originating in this image, so the wrappers
// above call the original libc functions without dlsym or recursive hooks.
#if defined(__APPLE__)
#if defined(__arm64e__)
#error This test tracer does not implement the arm64e authenticated interpose layout.
#endif
__attribute__((used, section("__DATA,__interpose,interposing")))
static const struct { const void *replacement; const void *original; } interpose[] = {
    {(const void *)(uintptr_t)&traced_bind, (const void *)(uintptr_t)&bind},
    {(const void *)(uintptr_t)&traced_close, (const void *)(uintptr_t)&close}
};
#endif

__attribute__((visibility("default")))
void embedio_quic_trace_marker(int phase, int cycle, int port)
{
    uint64_t stamp = now_ns();
    record_event((struct trace_event){stamp, stamp, 100 + phase, -1, port, 0, 0, 0, cycle});
}
__attribute__((visibility("default")))
int embedio_quic_trace_flush(void)
{
    atomic_store_explicit(&enabled, 0, memory_order_relaxed);
    unsigned count = atomic_load_explicit(&next_event, memory_order_relaxed);
    unsigned available = count < TRACE_LIMIT ? count : TRACE_LIMIT;
    unsigned incomplete = 0, binds = 0, closes = 0;
    const char *path = getenv("EMBEDIO_QUIC_TRACE_OUTPUT");
    if (path == NULL) return -1;
    FILE *output = fopen(path, "w");
    if (output == NULL) return -1;
    for (unsigned i = 0; i < available; ++i) {
        if (!atomic_load_explicit(&ready[i], memory_order_acquire)) { ++incomplete; continue; }
        struct trace_event e = events[i];
        binds += e.kind == 1;
        closes += e.kind == 2;
        fprintf(output, "{\"kind\":%d,\"fd\":%d,\"port\":%d,\"requested\":%d,\"result\":%d,\"error\":%d,\"cycle\":%d,\"start\":%llu,\"end\":%llu}\n",
            e.kind, e.fd, e.port, e.requested, e.result, e.error, e.cycle,
            (unsigned long long)e.start, (unsigned long long)e.end);
    }
    fprintf(output, "{\"summary\":true,\"pid\":%d,\"events\":%u,\"dropped\":%u,\"incomplete\":%u,\"binds\":%u,\"closes\":%u}\n",
        getpid(), count, count - available, incomplete, binds, closes);
    unsigned missing = atomic_load_explicit(&untracked, memory_order_relaxed);
    fprintf(output, "{\"untracked_descriptors\":%u}\n", missing);
    int failed = ferror(output);
    if (fclose(output) != 0) failed = 1;
    return failed || missing || count > TRACE_LIMIT || incomplete || binds < 2 || !closes ? -1 : 0;
}

#if defined(TRACE_SELF_TEST)
int main(void)
{
    int fd = socket(AF_INET, SOCK_DGRAM, 0);
    if (fd < 0) return 1;
    struct sockaddr_in local = {0};
    local.sin_family = AF_INET;
    local.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    embedio_quic_trace_marker(1, 0, 0);
    if (traced_bind(fd, (struct sockaddr *)&local, sizeof(local)) != 0) return 2;
    socklen_t size = sizeof(local);
    if (getsockname(fd, (struct sockaddr *)&local, &size) != 0) return 3;
    int other = socket(AF_INET, SOCK_DGRAM, 0);
    if (other < 0) return 4;
    if (traced_bind(other, (struct sockaddr *)&local, sizeof(local)) == 0 || errno != EADDRINUSE) return 5;
    if (traced_close(other) != 0 || traced_close(fd) != 0) return 6;
    embedio_quic_trace_marker(4, 0, 0);
    return embedio_quic_trace_flush() == 0 ? 0 : 4;
}
#endif
