#!/usr/bin/env python3
"""Dependency-free WebSocket probe for the source-owned realtime smoke path."""

import base64
import hashlib
import json
import os
import socket
import struct
import sys
from urllib.parse import urlsplit


def fail(message):
    raise SystemExit(f"realtime smoke failed: {message}")


def read_exact(sock, size):
    data = bytearray()
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            fail("unexpected EOF")
        data.extend(chunk)
    return bytes(data)


def send_client_frame(sock, opcode, payload):
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


def receive_frame(sock):
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


def main():
    if len(sys.argv) != 5:
        fail("usage: realtime_smoke.py <gateway-url> <api-key> <request-id> <idempotency-key>")
    gateway_url, api_key, request_id, idempotency_key = sys.argv[1:]
    parsed = urlsplit(gateway_url)
    if parsed.scheme != "http" or not parsed.hostname:
        fail("gateway URL must be an http URL")
    port = parsed.port or 80
    path = "/v1/responses"
    key = base64.b64encode(os.urandom(16)).decode("ascii")
    expected_accept = base64.b64encode(
        hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()).digest()
    ).decode("ascii")

    with socket.create_connection((parsed.hostname, port), timeout=15) as sock:
        sock.settimeout(15)
        request = (
            f"GET {path} HTTP/1.1\r\n"
            f"Host: {parsed.hostname}:{port}\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            f"Authorization: Bearer {api_key}\r\n"
            f"X-Request-ID: {request_id}\r\n"
            f"Idempotency-Key: {idempotency_key}\r\n"
            f"Sec-WebSocket-Key: {key}\r\n"
            "Sec-WebSocket-Version: 13\r\n\r\n"
        )
        sock.sendall(request.encode())
        response = bytearray()
        while b"\r\n\r\n" not in response:
            response.extend(sock.recv(4096))
        header = bytes(response).decode("latin1")
        if " 101 " not in header or f"Sec-WebSocket-Accept: {expected_accept}" not in header:
            fail(f"invalid upgrade response: {header!r}")

        send_client_frame(sock, 0x1, json.dumps({
            "type": "session.update",
            "session": {"model": "gpt-4o"},
        }, separators=(",", ":")))
        frames = []
        while len(frames) < 2:
            opcode, payload = receive_frame(sock)
            if opcode == 0x9:
                send_client_frame(sock, 0xA, payload)
                continue
            if opcode == 0x8:
                fail("Gateway closed before realtime usage frames")
            if opcode != 0x1:
                continue
            frames.append(json.loads(payload.decode("utf-8")))

        if frames[0].get("type") != "session.created":
            fail(f"unexpected first frame: {frames[0]!r}")
        usage = frames[1].get("response", {}).get("usage", {})
        if frames[1].get("type") != "response.done" or usage != {
            "input_tokens": 7,
            "output_tokens": 5,
        }:
            fail(f"unexpected usage frame: {frames[1]!r}")
        send_client_frame(sock, 0x8, struct.pack("!H", 1000))
    print("PASS: realtime WebSocket forwarding and Provider usage frames")


if __name__ == "__main__":
    main()
