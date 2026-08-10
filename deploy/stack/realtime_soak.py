#!/usr/bin/env python3
"""Concurrent, bounded WebSocket soak for the source-owned realtime path."""

from __future__ import annotations

import base64
import hashlib
import json
import socket
import struct
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from urllib.parse import urlsplit

from realtime_smoke import receive_frame, read_exact, send_client_frame


def fail(message: str) -> None:
    raise RuntimeError(message)


def run_session(gateway_url: str, api_key: str, request_id: str,
                idempotency_key: str, hold_seconds: float) -> None:
    parsed = urlsplit(gateway_url)
    if parsed.scheme != "http" or not parsed.hostname:
        fail("gateway URL must be an http URL")
    port = parsed.port or 80
    key = base64.b64encode(hashlib.sha1(request_id.encode()).digest()[:16]).decode("ascii")
    expected_accept = base64.b64encode(
        hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()).digest()
    ).decode("ascii")

    with socket.create_connection((parsed.hostname, port), timeout=15) as sock:
        sock.settimeout(1.0)
        request = (
            f"GET /v1/responses HTTP/1.1\r\n"
            f"Host: {parsed.hostname}:{port}\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            f"Authorization: Bearer {api_key}\r\n"
            f"X-Request-ID: {request_id}\r\n"
            f"Idempotency-Key: {idempotency_key}\r\n"
            f"Sec-WebSocket-Key: {key}\r\n"
            "Sec-WebSocket-Version: 13\r\n\r\n"
        )
        sock.sendall(request.encode("ascii"))
        response = bytearray()
        while b"\r\n\r\n" not in response:
            response.extend(sock.recv(4096))
        header = bytes(response).decode("latin1")
        if " 101 " not in header or f"Sec-WebSocket-Accept: {expected_accept}" not in header:
            fail(f"invalid upgrade response for {request_id}: {header!r}")

        send_client_frame(sock, 0x1, json.dumps({
            "type": "session.update",
            "session": {"model": "gpt-4o"},
        }, separators=(",", ":")))

        got_created = False
        got_done = False
        deadline = time.monotonic() + 15
        while not got_done and time.monotonic() < deadline:
            try:
                opcode, payload = receive_frame(sock)
            except socket.timeout:
                continue
            if opcode == 0x9:
                send_client_frame(sock, 0xA, payload)
                continue
            if opcode == 0x8:
                fail(f"Gateway closed before usage for {request_id}")
            if opcode != 0x1:
                continue
            frame = json.loads(payload.decode("utf-8"))
            if frame.get("type") == "session.created":
                got_created = True
            if frame.get("type") == "response.done":
                usage = frame.get("response", {}).get("usage", {})
                if usage != {"input_tokens": 7, "output_tokens": 5}:
                    fail(f"unexpected usage for {request_id}: {frame!r}")
                got_done = True
        if not got_created or not got_done:
            fail(f"realtime usage did not complete for {request_id}")

        # Keep the upgraded connection open while the Provider and Gateway
        # event loops remain idle, then close it from the client side. This
        # catches premature server closes and makes client cancellation happen
        # after a successful usage report rather than before output.
        hold_deadline = time.monotonic() + hold_seconds
        while time.monotonic() < hold_deadline:
            try:
                opcode, payload = receive_frame(sock)
                if opcode == 0x9:
                    send_client_frame(sock, 0xA, payload)
                elif opcode == 0x8:
                    fail(f"Gateway closed during soak for {request_id}")
            except socket.timeout:
                pass
        send_client_frame(sock, 0x8, struct.pack("!H", 1000))


def main() -> None:
    if len(sys.argv) != 6:
        fail("usage: realtime_soak.py <gateway-url> <api-key> <request-prefix> <count> <hold-seconds>")
    gateway_url, api_key, request_prefix = sys.argv[1:4]
    try:
        count = int(sys.argv[4])
        hold_seconds = float(sys.argv[5])
    except ValueError as exc:
        fail(f"count and hold-seconds must be numeric: {exc}")
    if count < 2 or count > 32 or hold_seconds < 1 or hold_seconds > 30:
        fail("count must be 2..32 and hold-seconds must be 1..30")

    errors: list[str] = []
    with ThreadPoolExecutor(max_workers=count) as executor:
        futures = [executor.submit(
            run_session,
            gateway_url,
            api_key,
            f"{request_prefix}-{index}",
            f"{request_prefix}-idem-{index}",
            hold_seconds,
        ) for index in range(count)]
        for future in as_completed(futures):
            try:
                future.result()
            except Exception as exc:  # noqa: BLE001 - report every failed child
                errors.append(str(exc))
    if errors:
        fail("; ".join(errors))
    print(f"PASS: {count} concurrent realtime sessions held for {hold_seconds:.1f}s")


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:  # noqa: BLE001 - CLI must return non-zero
        print(f"realtime soak failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
