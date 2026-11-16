"""Exercise 1, 2 and 10 tagged VPN exits through local core processes."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack
import importlib.util
import json
import os
from pathlib import Path
import socket
import socketserver
import subprocess
import sys
from tempfile import TemporaryDirectory
import threading
import time


def run(core, reality, fixture, count, workdir):
    with ExitStack() as stack, TemporaryDirectory(prefix="exit-matrix-", dir=workdir) as directory:
        exits = []
        for number in range(1, count + 1):
            server = stack.enter_context(socketserver.ThreadingTCPServer(("127.0.0.1", 0), fixture.Exit))
            server.label = f"vpn{number}"
            server.seen = []
            exits.append(server)
            threading.Thread(target=server.serve_forever, daemon=True).start()
            stack.callback(server.shutdown)
        with socket.socket() as reserve:
            reserve.bind(("127.0.0.1", 0))
            port = reserve.getsockname()[1]
        hosts = [f"exit-{number}.example.test" for number in range(1, count + 1)]
        document = {
            "inbounds": [{"type": "http", "tag": "fixture-in", "listen": "127.0.0.1", "listen_port": port}],
            "outbounds": [{"type": "socks", "tag": server.label, "server": "127.0.0.1",
                           "server_port": server.server_address[1], "version": "5", "network": "tcp"}
                          for server in exits],
            "route": {"rules": [{"domain": [host], "outbound": server.label}
                                for host, server in zip(hosts, exits)], "final": "vpn1"},
        }
        config = Path(directory, "config.json")
        config.write_text(json.dumps(document), encoding="utf-8")
        check = [core, "--config", config, "--check"] if reality else [core, "check", "-c", config]
        launch = [core, "--config", config, "--exit-on-stdin-eof"] if reality else [core, "run", "-c", config]
        subprocess.run(check, check=True, timeout=15)
        with Path(directory, "process.log").open("w+b") as log:
            process = subprocess.Popen(launch, stdin=subprocess.PIPE, stdout=log, stderr=log)
            try:
                deadline = time.monotonic() + 10
                while True:
                    if process.poll() is not None or time.monotonic() >= deadline:
                        raise RuntimeError("Core did not open its fixture listener.")
                    try:
                        with socket.create_connection(("127.0.0.1", port), timeout=0.2):
                            break
                    except OSError:
                        time.sleep(0.05)
                barrier = threading.Barrier(count)

                def request(host):
                    barrier.wait(timeout=10)
                    return fixture.request(port, host)

                with ThreadPoolExecutor(max_workers=count) as clients:
                    answers = list(clients.map(request, hosts))
                assert answers == [server.label for server in exits], answers
                assert all(server.seen == [(host, 80)] for host, server in zip(hosts, exits))
                if count > 1:
                    exits[-1].shutdown()
                    exits[-1].server_close()
                    before = [tuple(server.seen) for server in exits[:-1]]
                    try:
                        fixture.request(port, hosts[-1])
                    except (OSError, EOFError):
                        pass
                    else:
                        raise AssertionError("An unavailable exit unexpectedly used a different route.")
                    assert [tuple(server.seen) for server in exits[:-1]] == before
                    assert fixture.request(port, hosts[0]) == exits[0].label
            except BaseException:
                log.seek(0)
                print(log.read().decode("utf-8", errors="replace"))
                raise
            finally:
                process.stdin.close()
                if not reality and process.poll() is None:
                    process.terminate()
                try:
                    code = process.wait(timeout=10)
                    if reality and code != 0:
                        raise AssertionError(f"Reality Core quit with {code}.")
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
                    raise
        print(f"PASS {'Reality Core' if reality else 'sing-box'}: {count} exit(s), loopback TCP")


def run_windows_tun(core, reality, fixture, count, workdir):
    if sys.platform != "win32" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        raise RuntimeError("Wintun matrix requires an isolated GitHub-hosted Windows runner.")
    addresses = [f"192.0.2.{number}" for number in range(80, 80 + count)]
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as route:
        route.connect((addresses[0], 9))
        underlay = route.getsockname()[0]
    with ExitStack() as stack, TemporaryDirectory(prefix="wintun-matrix-", dir=workdir) as directory:
        exits = []
        for number in range(1, count + 1):
            server = stack.enter_context(socketserver.ThreadingTCPServer((underlay, 0), fixture.Exit))
            server.label, server.seen = f"vpn{number}", []
            exits.append(server)
            threading.Thread(target=server.serve_forever, daemon=True).start()
            stack.callback(server.shutdown)
        config = {
            "log": {"level": "error"},
            "inbounds": [{"type": "tun", "tag": "tun", "interface_name": "nova-test-matrix",
                          "address": ["172.23.62.1/30"], "auto_route": True}],
            "outbounds": [{"type": "socks", "tag": server.label, "server": underlay,
                           "server_port": server.server_address[1], "version": "5", "network": "tcp"}
                          for server in exits] + [{"type": "direct", "tag": "unrelated"}],
            "route": {"rules": [{"ip_cidr": [address + "/32"], "outbound": server.label}
                                for address, server in zip(addresses, exits)], "final": "unrelated"},
        }
        if not reality:
            config["route"]["auto_detect_interface"] = True
        path = Path(directory, "config.json")
        path.write_text(json.dumps(config), encoding="utf-8")
        check = [core, "--check", "-c", path] if reality else [core, "check", "-c", path]
        launch = [core, "-c", path, "--exit-on-stdin-eof"] if reality else [core, "run", "-c", path]
        subprocess.run(check, check=True, timeout=15)
        with Path(directory, "process.log").open("w+b") as log:
            process = subprocess.Popen(launch, cwd=core.parent, stdin=subprocess.PIPE, stdout=log, stderr=log)

            def request(address):
                with socket.create_connection((address, 80), timeout=2) as client:
                    client.sendall(b"GET / HTTP/1.1\r\nHost: fixture\r\nConnection: close\r\n\r\n")
                    with client.makefile("rb") as response:
                        return response.read().decode("ascii").partition("\r\n\r\n")[2]

            try:
                deadline = time.monotonic() + 45
                while True:
                    if process.poll() is not None or time.monotonic() >= deadline:
                        raise RuntimeError("Wintun did not become ready.")
                    try:
                        if request(addresses[0]) == exits[0].label:
                            break
                    except OSError:
                        time.sleep(.2)
                with ThreadPoolExecutor(max_workers=count) as clients:
                    answers = list(clients.map(request, addresses))
                assert answers == [server.label for server in exits], answers
                assert all(server.seen and all(seen == (address, 80) for seen in server.seen)
                           for address, server in zip(addresses, exits))
                if count > 1:
                    exits[-1].shutdown()
                    exits[-1].server_close()
                    before = [tuple(server.seen) for server in exits[:-1]]
                    try:
                        answer = request(addresses[-1])
                    except OSError:
                        answer = ""
                    assert not answer, f"Unavailable exit returned {answer!r}"
                    assert [tuple(server.seen) for server in exits[:-1]] == before
                    assert request(addresses[0]) == exits[0].label
            except BaseException:
                log.seek(0)
                print(log.read().decode("utf-8", errors="replace"), file=sys.stderr)
                raise
            finally:
                process.stdin.close()
                if not reality and process.poll() is None:
                    process.terminate()
                try:
                    code = process.wait(timeout=15)
                    if reality and code != 0:
                        raise AssertionError(f"Reality Core quit with {code}.")
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
                    raise
        print(f"PASS {'Reality Core' if reality else 'sing-box'}: {count} exit(s), Wintun TCP")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ("fixture", "sing-box", "reality", "workdir"):
        parser.add_argument(f"--{argument}", type=Path, required=True)
    parser.add_argument("--windows-tun", action="store_true")
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location("fixture", args.fixture.resolve(strict=True))
    fixture = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(fixture)
    args.workdir.mkdir(parents=True, exist_ok=True)
    for core, reality in ((args.sing_box, False), (args.reality, True)):
        for count in (1, 2, 10):
            exercise = run_windows_tun if args.windows_tun else run
            exercise(core.resolve(strict=True), reality, fixture, count, args.workdir.resolve())


if __name__ == "__main__":
    main()
