// Test-only functional check of zero-map Darwin datapath initialization.
#if defined(__APPLE__)
#include <atomic>
#include <cstring>
#include <sys/socket.h>
#include <unistd.h>

namespace {
const uint8_t EmbedIOConfigPayload[] = {0x00, 0x73, 0x80, 0xff};

struct EmbedIOConfigReceive {
    CXPLAT_EVENT Received;
    std::atomic<uint32_t> Count{0};
    std::atomic<bool> Valid{true};
    EmbedIOConfigReceive() { CxPlatEventInitialize(&Received, TRUE, FALSE); }
    ~EmbedIOConfigReceive() { CxPlatEventUninitialize(Received); }
    static void Receive(CXPLAT_SOCKET*, void* Context, CXPLAT_RECV_DATA* Chain) {
        auto& Self = *static_cast<EmbedIOConfigReceive*>(Context);
        for (auto* Item = Chain; Item; Item = Item->Next) {
            if (Item->BufferLength != sizeof(EmbedIOConfigPayload) ||
                std::memcmp(Item->Buffer, EmbedIOConfigPayload, sizeof(EmbedIOConfigPayload)) != 0) {
                Self.Valid.store(false);
            }
            Self.Count.fetch_add(1);
        }
        CxPlatRecvDataReturn(Chain);
        CxPlatEventSet(Self.Received);
    }
    static void Unreachable(CXPLAT_SOCKET*, void*, const QUIC_ADDR*) { }
};

struct EmbedIOConfigResources {
    CXPLAT_WORKER_POOL* WorkerPool{nullptr};
    CXPLAT_DATAPATH* Datapath{nullptr};
    CXPLAT_SOCKET* Socket{nullptr};
    int Sender{-1};
    ~EmbedIOConfigResources() {
        if (Sender >= 0) close(Sender);
        if (Socket) CxPlatSocketDelete(Socket);
        if (Datapath) CxPlatDataPathUninitialize(Datapath);
        if (WorkerPool) CxPlatWorkerPoolDelete(WorkerPool, CXPLAT_WORKER_POOL_REF_TOOL);
    }
};
}

TEST_F(DataPathTest, EmbedIOKqueueZeroMapConfigurationReceivesDatagram)
{
    EmbedIOConfigReceive Received;
    {
        EmbedIOConfigResources Resources;
        Resources.WorkerPool = CxPlatWorkerPoolCreate(nullptr, CXPLAT_WORKER_POOL_REF_TOOL);
        ASSERT_NE(nullptr, Resources.WorkerPool);
        // Exactly zero configuration, including optional receive features.
        CXPLAT_DATAPATH_INIT_CONFIG InitConfig{};
        const CXPLAT_UDP_DATAPATH_CALLBACKS Callbacks{
            EmbedIOConfigReceive::Receive, EmbedIOConfigReceive::Unreachable};
        VERIFY_QUIC_SUCCESS(CxPlatDataPathInitialize(
            0, &Callbacks, nullptr, Resources.WorkerPool, &InitConfig, &Resources.Datapath));
        ASSERT_NE(nullptr, Resources.Datapath);
        auto Local = GetNewLocalIPv4(false);
        CXPLAT_UDP_CONFIG SocketConfig{};
        SocketConfig.LocalAddress = &Local.SockAddr;
        SocketConfig.CallbackContext = &Received;
        VERIFY_QUIC_SUCCESS(CxPlatSocketCreateUdp(Resources.Datapath, &SocketConfig, &Resources.Socket));
        ASSERT_NE(nullptr, Resources.Socket);
        QUIC_ADDR Destination{};
        CxPlatSocketGetLocalAddress(Resources.Socket, &Destination);
        ASSERT_EQ(QUIC_ADDRESS_FAMILY_INET, QuicAddrGetFamily(&Destination));
        ASSERT_NE(0, Destination.Ipv4.sin_port);
        Resources.Sender = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        ASSERT_GE(Resources.Sender, 0);
        ASSERT_EQ(static_cast<ssize_t>(sizeof(EmbedIOConfigPayload)),
            sendto(Resources.Sender, EmbedIOConfigPayload, sizeof(EmbedIOConfigPayload), 0,
                reinterpret_cast<const sockaddr*>(&Destination.Ipv4), sizeof(sockaddr_in)));
        ASSERT_TRUE(CxPlatEventWaitWithTimeout(Received.Received, 2000));
    }
    // Resource destruction drains callbacks before final content/count checks.
    EXPECT_TRUE(Received.Valid.load());
    EXPECT_EQ(1u, Received.Count.load());
}
#endif
