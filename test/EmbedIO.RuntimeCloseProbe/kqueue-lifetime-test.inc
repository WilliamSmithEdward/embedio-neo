// Test-only extension to the pinned MsQuic platform test translation unit.
// Both control and candidate compile this identical fixture.
#if defined(__APPLE__)
#include <atomic>
#include <cstring>
#include <sys/socket.h>
#include <unistd.h>

namespace {
struct EmbedIOBlockedReceive {
    CXPLAT_EVENT Entered;
    CXPLAT_EVENT Release;
    std::atomic<bool> TimedOut{false};
    EmbedIOBlockedReceive() {
        CxPlatEventInitialize(&Entered, TRUE, FALSE);
        CxPlatEventInitialize(&Release, TRUE, FALSE);
    }
    ~EmbedIOBlockedReceive() {
        CxPlatEventUninitialize(Entered);
        CxPlatEventUninitialize(Release);
    }
    static void Receive(CXPLAT_SOCKET*, void* Context, CXPLAT_RECV_DATA* Data) {
        CxPlatRecvDataReturn(Data);
        if (!Context) return;
        auto& Self = *static_cast<EmbedIOBlockedReceive*>(Context);
        CxPlatEventSet(Self.Entered);
        if (!CxPlatEventWaitWithTimeout(Self.Release, 10000)) Self.TimedOut.store(true);
    }
    static void Unreachable(CXPLAT_SOCKET*, void*, const QUIC_ADDR*) { }
};

struct EmbedIOReleaseReceive {
    EmbedIOBlockedReceive& Context;
    ~EmbedIOReleaseReceive() { CxPlatEventSet(Context.Release); }
};

struct EmbedIORawSocket {
    int Fd{-1};
    ~EmbedIORawSocket() { Close(); }
    void Close() { if (Fd >= 0) { close(Fd); Fd = -1; } }
    void Open() { Fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP); }
};
}

TEST_F(DataPathTest, EmbedIOKqueueDeleteReleasesPortBeforeDeferredCleanup)
{
    // Block the sole configured I/O worker in a DIFFERENT socket's callback.
    // Deletion of the idle socket must release its port even while its queued
    // context reclamation cannot run. No retry or scheduler delay is involved.
    EmbedIOBlockedReceive Gate;
    EmbedIORawSocket Sender;
    EmbedIORawSocket Replacement;
    Sender.Open();
    ASSERT_GE(Sender.Fd, 0);
    QUIC_ADDR ReboundAddress{};
    const CXPLAT_UDP_DATAPATH_CALLBACKS Callbacks{
        EmbedIOBlockedReceive::Receive, EmbedIOBlockedReceive::Unreachable};
    QUIC_GLOBAL_EXECUTION_CONFIG Config{
        QUIC_GLOBAL_EXECUTION_CONFIG_FLAG_NO_IDEAL_PROC, UINT32_MAX, 1, {0}};
    {
        CxPlatDataPath Datapath(&Callbacks, nullptr, 0, &Config);
        VERIFY_QUIC_SUCCESS(Datapath.GetInitStatus());
        auto Local = GetNewLocalIPv4(false);
        // Establish and verify the descriptor slot used by this idle socket.
        // No test passes on an assumption about the descriptor allocator.
        EmbedIORawSocket Slot;
        Slot.Open();
        ASSERT_GE(Slot.Fd, 0);
        const int IdleFd = Slot.Fd;
        Slot.Close();
        CxPlatSocket Idle(Datapath, &Local.SockAddr);
        VERIFY_QUIC_SUCCESS(Idle.GetInitStatus());
        ReboundAddress = Idle.GetLocalAddress();
        ASSERT_EQ(QUIC_ADDRESS_FAMILY_INET, QuicAddrGetFamily(&ReboundAddress));
        // The pinned Darwin datapath binds an IPv6 socket with a mapped IPv4
        // address. Verify that this slot belongs to the endpoint just created.
        sockaddr_in6 SlotAddress{};
        socklen_t SlotSize = sizeof(SlotAddress);
        ASSERT_EQ(0, getsockname(IdleFd, reinterpret_cast<sockaddr*>(&SlotAddress), &SlotSize));
        ASSERT_EQ(AF_INET6, SlotAddress.sin6_family);
        ASSERT_EQ(ReboundAddress.Ipv4.sin_port, SlotAddress.sin6_port);
        ASSERT_TRUE(IN6_IS_ADDR_V4MAPPED(&SlotAddress.sin6_addr));
        ASSERT_EQ(0, std::memcmp(&SlotAddress.sin6_addr.s6_addr[12],
            &ReboundAddress.Ipv4.sin_addr, sizeof(in_addr)));
        CxPlatSocket Busy(Datapath, &Local.SockAddr, nullptr, &Gate);
        VERIFY_QUIC_SUCCESS(Busy.GetInitStatus());
        EmbedIOReleaseReceive ReleaseOnExit{Gate};
        const auto Destination = Busy.GetLocalAddress();
        const char Payload = 'x';
        ASSERT_EQ(1, sendto(Sender.Fd, &Payload, 1, 0,
            reinterpret_cast<const sockaddr*>(&Destination.Ipv4), sizeof(sockaddr_in)));
        ASSERT_TRUE(CxPlatEventWaitWithTimeout(Gate.Entered, 2000));
        CxPlatSocketDelete(Idle.Socket);
        Idle.Socket = nullptr;

        // Require reuse of the verified descriptor slot after deletion.
        // Deferred cleanup must never close this independently owned socket.
        Replacement.Open();
        ASSERT_GE(Replacement.Fd, 0);
        EXPECT_EQ(IdleFd, Replacement.Fd);
        EXPECT_EQ(0, bind(Replacement.Fd,
            reinterpret_cast<const sockaddr*>(&ReboundAddress.Ipv4), sizeof(sockaddr_in)));
    }
    // Datapath destruction drains the queued context cleanup, after the guard
    // releases the blocked callback. The replacement must remain bound/alive.
    EXPECT_FALSE(Gate.TimedOut.load());
    sockaddr_in Actual{};
    socklen_t Size = sizeof(Actual);
    ASSERT_EQ(0, getsockname(Replacement.Fd, reinterpret_cast<sockaddr*>(&Actual), &Size));
    EXPECT_EQ(ReboundAddress.Ipv4.sin_port, Actual.sin_port);
    EXPECT_EQ(ReboundAddress.Ipv4.sin_addr.s_addr, Actual.sin_addr.s_addr);
}
#endif