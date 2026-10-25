#!/usr/bin/env python3
"""Run opt-in MudPlay tests through a passive wire recorder. Read disposable
Gthief/Gsysop account-password pairs as JSON on stdin; never print them.
Full captures contain test login traffic: keep --evidence-dir outside the repository.
"""
import argparse
import base64
import datetime
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import threading
import time
import uuid


def decode(chunks):
    buffers = {"c2s": bytearray(), "s2c": bytearray()}
    frames = []
    commands = {253: "DO", 254: "DONT", 251: "WILL", 252: "WONT"}
    options = {201: "GMCP", 1: "ECHO", 3: "SGA", 0: "BINARY", 31: "NAWS", 24: "TTYPE", 34: "LINEMODE"}

    def add(t, direction, kind, raw, **fields):
        frames.append(dict(t=t, dir=direction, kind=kind, raw=base64.b64encode(raw).decode(), expect="valid", **fields))

    for timestamp, direction, block in chunks:
        pending = buffers[direction]
        pending.extend(block)
        while pending:
            if pending[0] != 255:
                end = pending.find(255)
                end = len(pending) if end < 0 else end
                raw = bytes(pending[:end])
                del pending[:end]
                add(timestamp, direction, "text", raw, text=raw.decode("cp437"))
                continue
            if len(pending) < 2:
                break
            command = pending[1]
            if command == 255:
                add(timestamp, direction, "text", bytes(pending[:2]), text=bytes([255]).decode("cp437"))
                del pending[:2]
                continue
            if command in commands:
                if len(pending) < 3:
                    break
                add(timestamp, direction, "negotiation", bytes(pending[:3]), telnet="IAC " + commands[command] + " " + options.get(pending[2], str(pending[2])))
                del pending[:3]
                continue
            if command == 250:
                end = 3
                while end + 1 < len(pending):
                    if pending[end] == 255 and pending[end + 1] == 240:
                        break
                    end += 2 if pending[end] == 255 and pending[end + 1] == 255 else 1
                if end + 1 >= len(pending):
                    break
                raw = bytes(pending[:end + 2])
                del pending[:end + 2]
                if raw[2] == 201:
                    package, _, payload = raw[3:-2].replace(bytes([255, 255]), bytes([255])).decode("utf-8").partition(" ")
                    add(timestamp, direction, "gmcp", raw, pkg=package, msg=json.loads(payload))
                else:
                    add(timestamp, direction, "negotiation", raw, telnet="IAC SB " + str(raw[2]))
                continue
            add(timestamp, direction, "negotiation", bytes(pending[:2]), telnet="IAC " + str(command))
            del pending[:2]
    return frames


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence-dir", type=Path, default=Path(tempfile.gettempdir()) / "mudplay-gmcp-live-evidence")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    evidence = args.evidence_dir.resolve()
    if evidence == root or root in evidence.parents:
        parser.error("Full login captures must be outside the repository.")
    evidence.mkdir(parents=True, exist_ok=True)
    accounts = json.load(sys.stdin)
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen()
    listener.settimeout(0.25)
    stop = threading.Event()
    lock = threading.Lock()
    connections, workers = [], []
    started = time.monotonic()

    def pump(source, target, recording, direction):
        try:
            while not stop.is_set():
                block = source.recv(65536)
                if not block:
                    with lock:
                        recording["ended"][direction] = int((time.monotonic() - started) * 1000)
                    break
                with lock:
                    recording["chunks"].append((int((time.monotonic() - started) * 1000), direction, block))
                target.sendall(block)
        except OSError:
            pass  # A peer closing ends this half of the recording.
        finally:
            try:
                target.shutdown(socket.SHUT_WR)
            except OSError:
                pass  # The other pump may already have closed it.

    def accept():
        while not stop.is_set():
            try:
                client, _ = listener.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            server = socket.create_connection(("192.168.1.100", 2350), timeout=10)
            server.settimeout(None)
            recording = {"chunks": [], "sockets": [client, server], "ended": {}}
            connections.append(recording)
            for source, target, direction in [(client, server, "c2s"), (server, client, "s2c")]:
                worker = threading.Thread(target=pump, args=(source, target, recording, direction), daemon=True)
                workers.append(worker)
                worker.start()

    threading.Thread(target=accept, daemon=True).start()
    env = dict(os.environ, MUDPLAY_DATA_ROOT=str(evidence / "data"), MUDPLAY_GMCP_HOST="127.0.0.1", MUDPLAY_GMCP_PORT=str(listener.getsockname()[1]),
               MUDPLAY_GMCP_ACCOUNT=accounts["Gthief"][0], MUDPLAY_GMCP_PASSWORD=accounts["Gthief"][1],
               MUDPLAY_GMCP_SYSOP_ACCOUNT=accounts["Gsysop"][0], MUDPLAY_GMCP_SYSOP_PASSWORD=accounts["Gsysop"][1])
    result = None
    try:
        result = subprocess.run(["dotnet", "test", "MudPlay.Tests/MudPlay.Tests.csproj", "--no-restore", "--filter", "FullyQualifiedName~IsolatedHost_", "--logger", "console;verbosity=normal"], cwd=root, env=env, text=True, capture_output=True, timeout=240)
    finally:
        stop.set()
        listener.close()
        for recording in connections:
            for peer in recording["sockets"]:
                try:
                    peer.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass  # Teardown is allowed to race the peer's own close.
                peer.close()
        for worker in workers:
            worker.join(timeout=2)
        run = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:6]
        for index, recording in enumerate(connections, 1):
            frames = decode(recording["chunks"])
            server = next((frame["msg"] for frame in frames if frame.get("pkg") == "Anomaly.Server"), {})
            header = dict(recording=1, source="captured", contract="1.0.0", scenario="client-active-" + run + "-" + str(index),
                          description="Actual MudPlay live integration through a passive wire recorder; connection " + str(index),
                          server=dict(commit=server.get("build", "server-reported-unknown"), host="gmcp-inbound.patch"),
                          captured=datetime.datetime.now(datetime.timezone.utc).isoformat(), supersededBy=None,
                          client=dict(version="3.186.0", workingTree=True), connectionEnded=recording["ended"], result="passed" if result and result.returncode == 0 else "failed")
            path = evidence / (header["scenario"] + ".jsonl")
            with path.open("x") as stream:
                stream.write(json.dumps(header) + "\n")
                for frame in frames:
                    stream.write(json.dumps(frame, separators=(",", ":")) + "\n")
            print("Capture:", path)
    print(result.stdout[-14000:])
    print(result.stderr[-6000:])
    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
