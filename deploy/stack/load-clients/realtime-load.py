#!/usr/bin/env python3
"""Realtime WebSocket load generator for the stress test.

Opens concurrent WebSocket sessions at a configurable rate, completes the
realtime usage handshake, and holds connections open briefly before closing.
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import socket
import struct
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from urllib.parse import urlsplit


def send_client_frame(sock: socket.socket, opcode: int, payload: bytes | str) -> None:
    payload = payload if isinstance(payload, bytes) else payload.encode()
    mask = os.urandom(4)
    length = len(payload)
    if length < 126:
        header = bytes((0x80 | opcode, 0x80 | length))
    elif length < 65536:
        header = bytes((0x80 | opcode, 0x80 | 126)) + struct.pack("!H", length)
    else:
        header = bytes((0x80 | opcode, 0x80 | 127)) + struct.pack("!Q", length)
    masked = bytes(value ^ mask[index % 4] for index, value in enumerate(payload))
    sock.sendall(header + mask + masked)


def read_exact(sock: socket.socket, size: int) -> bytes:
    data = bytearray()
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise ConnectionError("unexpected EOF")
        data.extend(chunk)
    return bytes(data)


def receive_frame(sock: socket.socket) -> tuple[int, bytes]:
    first, second = read_exact(sock, 2)
    opcode = first & 0x0F
    length = second & 0x7F
    if length == 126:
        length = struct.unpack("!H", read_exact(sock, 2))[0]
    elif length == 127:
        length = struct.unpack("!Q", read_exact(sock, 8))[0]
    if second & 0x80:
        mask = read_exact(sock, 4)
        payload = bytes(
            value ^ mask[index % 4] for index, value in enumerate(read_exact(sock, length))
        )
    else:
        payload = read_exact(sock, length)
    return opcode, payload


def run_session(gateway_url: str, api_key: str, request_id: str,
                idempotency_key: str, hold_seconds: float) -> None:
    parsed = urlsplit(gateway_url)
    if parsed.scheme != "http" or not parsed.hostname:
        raise ValueError("gateway URL must be an http URL")
    port = parsed.port or 80
    key = base64.b64encode(os.urandom(16)).decode("ascii")
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
            raise RuntimeError(f"invalid upgrade response for {request_id}")

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
                raise RuntimeError(f"Gateway closed before usage for {request_id}")
            if opcode != 0x1:
                continue
            frame = json.loads(payload.decode("utf-8"))
            if frame.get("type") == "session.created":
                got_created = True
            if frame.get("type") == "response.done":
                got_done = True

        if not got_created or not got_done:
            raise RuntimeError(f"realtime usage did not complete for {request_id}")

        hold_deadline = time.monotonic() + hold_seconds
        while time.monotonic() < hold_deadline:
            try:
                opcode, payload = receive_frame(sock)
                if opcode == 0x9:
                    send_client_frame(sock, 0xA, payload)
                elif opcode == 0x8:
                    break
            except socket.timeout:
                pass
        try:
            send_client_frame(sock, 0x8, struct.pack("!H", 1000))
        except OSError:
            pass


def main() -> None:
    gateway_url = os.environ.get("STRESS_GATEWAY_URL", "")
    api_key = os.environ.get("STRESS_API_KEY", "")
    duration_seconds = int(os.environ.get("STRESS_REALTIME_DURATION", "3600"))
    concurrency = int(os.environ.get("STRESS_REALTIME_CONCURRENCY", "4"))
    hold_seconds = float(os.environ.get("STRESS_REALTIME_HOLD", "3"))
    session_interval = float(os.environ.get("STRESS_REALTIME_INTERVAL", "10"))
    prefix = os.environ.get("STRESS_PREFIX", "stress")

    if not gateway_url or not api_key:
        print("realtime-load: STRESS_GATEWAY_URL and STRESS_API_KEY are required",
              file=sys.stderr)
        raise SystemExit(2)

    started_at = time.monotonic()
    session_index = 0
    successes = 0
    failures = 0

    print(f"realtime-load: starting (duration={duration_seconds}s, "
          f"concurrency={concurrency}, hold={hold_seconds}s)")

    while time.monotonic() - started_at < duration_seconds:
        futures = []
        with ThreadPoolExecutor(max_workers=concurrency) as executor:
            for batch_offset in range(concurrency):
                session_index += 1
                request_id = f"{prefix}-realtime-{session_index}"
                idempotency_key = f"{prefix}-realtime-idem-{session_index}"
                futures.append(executor.submit(
                    run_session, gateway_url, api_key,
                    request_id, idempotency_key, hold_seconds,
                ))
            for future in as_completed(futures):
                try:
                    future.result()
                    successes += 1
                except Exception as exc:
                    failures += 1
                    if failures > 50:
                        print(f"realtime-load: too many failures ({failures})",
                              file=sys.stderr)
                        raise SystemExit(1)

        elapsed = time.monotonic() - started_at
        remaining = duration_seconds - elapsed
        if remaining > session_interval:
            time.sleep(session_interval)

    elapsed_total = time.monotonic() - started_at
    print(f"realtime-load: {successes} successes, {failures} failures "
          f"in {elapsed_total:.0f}s")


if __name__ == "__main__":
    main()
