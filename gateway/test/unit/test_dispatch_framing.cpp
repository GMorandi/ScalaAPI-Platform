#include <gtest/gtest.h>
#include "dispatch/capnp_dispatch_client.h"

#include <capnp/message.h>
#include <capnp/serialize.h>
#include "dispatch.capnp.h"

#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <functional>
#include <string>
#include <thread>
#include <vector>

using namespace gateway::dispatch;

namespace {

// Minimal fake Platform endpoint: accepts connections on a Unix domain socket
// and runs the given handler on each accepted connection.
class FakePlatform {
public:
    using Handler = std::function<void(int)>;

    ~FakePlatform() { stop(); }

    bool start(Handler handler) {
        static std::atomic<int> next_id{0};
        path_ = "/tmp/gateway-framing-test-" + std::to_string(::getpid()) + "-"
            + std::to_string(next_id.fetch_add(1)) + ".sock";
        ::unlink(path_.c_str());
        listen_fd_ = ::socket(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK, 0);
        if (listen_fd_ < 0) return false;
        sockaddr_un addr{};
        addr.sun_family = AF_UNIX;
        std::strncpy(addr.sun_path, path_.c_str(), sizeof(addr.sun_path) - 1);
        if (::bind(listen_fd_, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) < 0
            || ::listen(listen_fd_, 8) < 0) {
            stop();
            return false;
        }
        running_.store(true);
        thread_ = std::thread([this, handler = std::move(handler)] {
            while (running_.load()) {
                int fd = ::accept(listen_fd_, nullptr, nullptr);
                if (fd < 0) {
                    if (running_.load())
                        std::this_thread::sleep_for(std::chrono::milliseconds(1));
                    continue;
                }
                timeval tv{2, 0};
                ::setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
                ::setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof(tv));
                handler(fd);
                ::close(fd);
            }
        });
        return true;
    }

    void stop() {
        if (!running_.exchange(false)) return;
        if (thread_.joinable()) thread_.join();
        if (listen_fd_ >= 0) ::close(listen_fd_);
        listen_fd_ = -1;
        if (!path_.empty()) ::unlink(path_.c_str());
    }

    const std::string& path() const { return path_; }

private:
    std::string path_;
    int listen_fd_ = -1;
    std::atomic<bool> running_{false};
    std::thread thread_;
};

bool read_exact(int fd, uint8_t* buf, size_t n) {
    size_t got = 0;
    while (got < n) {
        ssize_t r = ::recv(fd, buf + got, n - got, 0);
        if (r <= 0) return false;
        got += static_cast<size_t>(r);
    }
    return true;
}

std::vector<uint8_t> read_frame(int fd) {
    uint8_t hdr[4];
    if (!read_exact(fd, hdr, 4)) return {};
    uint32_t len = hdr[0] | (hdr[1] << 8) | (hdr[2] << 16) | (hdr[3] << 24);
    if (len == 0) return {};
    std::vector<uint8_t> payload(len);
    if (!read_exact(fd, payload.data(), len)) return {};
    return payload;
}

bool write_all(int fd, const uint8_t* data, size_t size) {
    size_t written = 0;
    while (written < size) {
        ssize_t n = ::send(fd, data + written, size - written, MSG_NOSIGNAL);
        if (n <= 0) return false;
        written += static_cast<size_t>(n);
    }
    return true;
}

std::vector<uint8_t> build_frame(uint8_t method, capnp::MallocMessageBuilder& msg) {
    auto words = capnp::messageToFlatArray(msg);
    auto bytes = words.asBytes();
    uint32_t len = static_cast<uint32_t>(bytes.size() + 1);
    std::vector<uint8_t> frame(4 + len);
    frame[0] = static_cast<uint8_t>(len & 0xFF);
    frame[1] = static_cast<uint8_t>((len >> 8) & 0xFF);
    frame[2] = static_cast<uint8_t>((len >> 16) & 0xFF);
    frame[3] = static_cast<uint8_t>((len >> 24) & 0xFF);
    frame[4] = method;
    std::memcpy(frame.data() + 5, bytes.begin(), bytes.size());
    return frame;
}

bool wait_until(const std::function<bool()>& pred, int timeout_ms = 3000) {
    auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
    while (std::chrono::steady_clock::now() < deadline) {
        if (pred()) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    return pred();
}

}  // namespace

TEST(DispatchFraming, OversizeReportUsageIsRejectedWithoutWriting) {
    FakePlatform server;
    std::atomic<int> frames_received{0};
    std::atomic<int> handlers_done{0};
    ASSERT_TRUE(server.start([&](int fd) {
        if (!read_frame(fd).empty()) frames_received.fetch_add(1);
        handlers_done.fetch_add(1);
    }));

    auto client = CapnpDispatchClient::connect(server.path());
    UsageReportData report;
    report.lease_token = "lease-oversize";
    report.response_body.assign(8 * 1024 * 1024, 'x');
    auto ack = client->report_usage(report);
    EXPECT_FALSE(ack.acknowledged());
    EXPECT_FALSE(ack.retryable);
    EXPECT_EQ(ack.error_code, "request_too_large");
    // The self-check must not tear down a healthy connection.
    EXPECT_TRUE(client->is_connected());

    ASSERT_TRUE(wait_until([&] { return handlers_done.load() >= 1; }));
    EXPECT_EQ(frames_received.load(), 0);
}

TEST(DispatchFraming, LargeReportUsageRoundTrips) {
    FakePlatform server;
    std::atomic<int> frames_received{0};
    std::atomic<size_t> last_frame_bytes{0};
    std::atomic<int> last_method{0};
    ASSERT_TRUE(server.start([&](int fd) {
        auto frame = read_frame(fd);
        if (frame.empty()) return;
        frames_received.fetch_add(1);
        last_method = frame[0];
        last_frame_bytes = frame.size();
        capnp::MallocMessageBuilder msg;
        auto ack = msg.initRoot<::WriteAck>();
        ack.setAccepted(true);
        auto response = build_frame(0x82, msg);
        write_all(fd, response.data(), response.size());
    }));

    auto client = CapnpDispatchClient::connect(server.path());
    UsageReportData report;
    report.lease_token = "lease-large";
    report.response_body.assign(1536 * 1024, 'x');
    auto ack = client->report_usage(report);
    EXPECT_TRUE(ack.acknowledged());
    EXPECT_TRUE(ack.accepted);
    EXPECT_EQ(last_method.load(), 0x02);
    // The frame is over the old 1 MiB cap and must pass under 8 MiB.
    EXPECT_GT(last_frame_bytes.load(), 1024 * 1024u);
}

TEST(DispatchFraming, LargeReplayBodyRoundTrips) {
    FakePlatform server;
    const std::string big_body(1536 * 1024, 'y');
    ASSERT_TRUE(server.start([&](int fd) {
        auto frame = read_frame(fd);
        if (frame.empty()) return;
        capnp::MallocMessageBuilder msg;
        auto resp = msg.initRoot<::DispatchResponse>();
        resp.setOutcome(::DispatchResponse::Outcome::REJECTED);
        resp.setProtocolVersion(3);
        auto reject = resp.initReject();
        reject.setCode(::RejectInfo::RejectCode::IDEMPOTENCY_REPLAY);
        reject.setMessage("replay");
        resp.setReplayStatusCode(200);
        resp.setReplayContentType("application/json");
        resp.setReplayBody(big_body);
        auto response = build_frame(0x81, msg);
        write_all(fd, response.data(), response.size());
    }));

    auto client = CapnpDispatchClient::connect(server.path());
    gateway::dispatch::DispatchRequest req;
    req.request_id = "req-replay";
    auto result = client->dispatch(req);
    EXPECT_EQ(result.outcome, DispatchResult::Outcome::Rejected);
    EXPECT_EQ(result.reject_code, 10);
    EXPECT_EQ(result.replay_status_code, 200);
    EXPECT_EQ(result.replay_body, big_body);
}

TEST(DispatchFraming, OversizeInboundFrameFailsDeterministically) {
    FakePlatform server;
    std::atomic<int> frames_received{0};
    std::atomic<int> oversize_replies{0};
    ASSERT_TRUE(server.start([&](int fd) {
        auto frame = read_frame(fd);
        if (frame.empty()) return;
        frames_received.fetch_add(1);
        // Declare a payload one byte over the cap; the client must reject the
        // frame from the header alone without waiting for the body.
        uint32_t len = 8u * 1024 * 1024 + 1;
        uint8_t hdr[4] = {
            static_cast<uint8_t>(len & 0xFF),
            static_cast<uint8_t>((len >> 8) & 0xFF),
            static_cast<uint8_t>((len >> 16) & 0xFF),
            static_cast<uint8_t>((len >> 24) & 0xFF),
        };
        if (write_all(fd, hdr, sizeof(hdr))) oversize_replies.fetch_add(1);
    }));

    auto client = CapnpDispatchClient::connect(server.path());
    UsageReportData report;
    report.lease_token = "lease-oversize-inbound";
    auto ack = client->report_usage(report);
    EXPECT_FALSE(ack.acknowledged());
    EXPECT_EQ(ack.error_code, "invalid_ack");
    // Transport-level garbage stays retryable; only deterministic rejections
    // (validation, oversize) map to retryable=false and dead-letter.
    EXPECT_TRUE(ack.retryable);
    ASSERT_TRUE(wait_until([&] { return oversize_replies.load() >= 1; }));
    EXPECT_EQ(frames_received.load(), 1);
}
