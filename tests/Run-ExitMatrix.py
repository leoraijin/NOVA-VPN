"""Exercise 1, 2 and 10 tagged VPN exits through local core processes."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack
import importlib.util
import json
import os
from pathlib import Path
import select
import socket
import socketserver
import struct
import subprocess
import sys
from tempfile import TemporaryDirectory
import threading
import time
import uuid


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


class DatagramExit(socketserver.StreamRequestHandler):
    """A SOCKS5 test exit that replies only to the two synthetic fixture protocols."""

    def handle(self):
        self.request.settimeout(6)
        if self.rfile.read(3) != b"\x05\x01\x00":
            return
        self.wfile.write(b"\x05\x00")
        version, cmd, reserved, family = self.rfile.read(4)
        if (version, cmd, reserved) != (5, 3, 0):
            return
        address_size = {1: 4, 4: 16}.get(family)
        if address_size is None:
            return
        self.rfile.read(address_size + 2)
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as relay:
            relay.bind((self.server.server_address[0], 0))
            host, port = relay.getsockname()
            self.wfile.write(b"\x05\x00\x00\x01" + socket.inet_aton(host) + struct.pack("!H", port))
            while True:
                readable, _, _ = select.select([self.request, relay], [], [], 6)
                if not readable or self.request in readable:
                    return
                packet, peer = relay.recvfrom(2048)
                if len(packet) < 10 or packet[:4] != b"\x00\x00\x00\x01":
                    continue
                target = socket.inet_ntoa(packet[4:8])
                port = struct.unpack("!H", packet[8:10])[0]
                if target != self.server.destination:
                    continue
                payload = packet[10:]
                if port == 5300 and payload == b"nova-wintun-udp":
                    response = self.server.label.encode("ascii")
                elif port == 53 and payload == DNS_QUERY:
                    response = dns_response(self.server.number)
                else:
                    continue
                self.server.seen.append((target, port))
                relay.sendto(packet[:10] + response, peer)


DNS_QUERY = bytes.fromhex("ac1201000001000000000000046e6f766104746573740000010001")


def dns_response(exit_number):
    return (DNS_QUERY[:2] + bytes.fromhex("81800001000100000000") + DNS_QUERY[12:]
            + bytes.fromhex("c00c000100010000003c0004cb0071") + bytes([exit_number]))


def verify_wintun_datagrams(addresses, exits, dns_port=53):
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
        client.settimeout(5)
        for address, exit_server in zip(addresses, exits):
            for port, payload, expected in ((5300, b"nova-wintun-udp", exit_server.label.encode("ascii")),
                                            (dns_port, DNS_QUERY, dns_response(exit_server.number))):
                client.sendto(payload, (address, port))
                response, peer = client.recvfrom(2048)
                assert peer == (address, port) and response == expected, (exit_server.label, port, response)
    assert all(server.seen == [(address, 5300), (address, dns_port)]
               for address, server in zip(addresses, exits)), [server.seen for server in exits]


def verify_reality_fake_dns(address):
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
        client.settimeout(5)
        client.sendto(DNS_QUERY, (address, 53))
        response, peer = client.recvfrom(2048)
        assert peer == (address, 53) and response[:2] == DNS_QUERY[:2], (peer, response)
        assert len(response) >= 20 and response[2] & 0x80 and response[3] & 0x0f == 0, response
        assert response[6:8] == b"\x00\x01" and response[-4] == 198 and response[-3] in (18, 19), response


class VlessDatagramEcho(socketserver.BaseRequestHandler):
    def handle(self):
        payload, udp_socket = self.request
        if payload == b"nova-wintun-udp":
            port, answer = 5300, self.server.label.encode("ascii")
        elif payload == DNS_QUERY:
            port, answer = 5353, dns_response(self.server.number)
        else:
            return
        self.server.seen.append((self.server.destination, port))
        udp_socket.sendto(answer, self.client_address)


def start_vless_datagram_peer(stack, singbox, directory, underlay, addresses):
    fixture_config = {"log": {"level": "error"}, "inbounds": [],
                      "outbounds": [{"type": "direct", "tag": "echo"}],
                      "route": {"rules": [], "final": "echo"}}
    exits, client_outbounds = [], []
    for number, destination in enumerate(addresses, start=1):
        echo = stack.enter_context(socketserver.ThreadingUDPServer((underlay, 0), VlessDatagramEcho))
        echo.label, echo.number, echo.destination = f"vpn{number}", number, destination
        echo.seen = []
        threading.Thread(target=echo.serve_forever, daemon=True).start()
        stack.callback(echo.shutdown)
        exits.append(echo)
        with socket.socket() as reserve:
            reserve.bind((underlay, 0))
            listener = reserve.getsockname()[1]
        tag, credential = f"vless-{number}", str(uuid.uuid4())
        fixture_config["inbounds"].append({"type": "vless", "tag": tag, "listen": underlay,
                                            "listen_port": listener, "users": [{"uuid": credential}]})
        fixture_config["route"]["rules"].append(
            {"inbound": [tag], "network": "udp", "action": "route-options",
             "override_address": underlay, "override_port": echo.server_address[1]})
        client_outbounds.append({"type": "vless", "tag": f"udp-{echo.label}", "server": underlay,
                                 "server_port": listener, "uuid": credential, "packet_encoding": "xudp",
                                 "allow_insecure": True})
    path = Path(directory, "vless-peer.json")
    path.write_text(json.dumps(fixture_config), encoding="utf-8")
    subprocess.run([singbox, "check", "-c", path], timeout=15, check=True)
    log = stack.enter_context(Path(directory, "vless-peer.log").open("w+b"))
    peer = subprocess.Popen([singbox, "run", "-c", path], stdout=log, stderr=log)

    def stop_peer():
        if peer.poll() is None:
            peer.terminate()
        peer.wait(timeout=10)

    stack.callback(stop_peer)
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline and peer.poll() is None:
        try:
            with socket.create_connection((underlay, listener), timeout=.3):
                return exits, client_outbounds
        except OSError:
            time.sleep(.05)
    log.seek(0)
    raise RuntimeError("VLESS datagram peer did not start: " + log.read().decode("utf-8", errors="replace"))


def run_windows_tun(core, reality, fixture, count, workdir, singbox):
    if sys.platform != "win32" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        raise RuntimeError("Wintun matrix requires an isolated GitHub-hosted Windows runner.")
    addresses = [f"192.0.2.{number}" for number in range(80, 80 + count)]
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as route:
        route.connect((addresses[0], 9))
        underlay = route.getsockname()[0]
    with TemporaryDirectory(prefix="wintun-matrix-", dir=workdir) as directory, ExitStack() as stack:
        exits = []
        for number in range(1, count + 1):
            server = stack.enter_context(socketserver.ThreadingTCPServer((underlay, 0), fixture.Exit))
            server.label, server.seen = f"vpn{number}", []
            exits.append(server)
            threading.Thread(target=server.serve_forever, daemon=True).start()
            stack.callback(server.shutdown)
        datagram_exits = []
        # Reality Core currently implements SOCKS5 CONNECT only.
        for number, destination in enumerate([] if reality else addresses, start=1):
            server = stack.enter_context(socketserver.ThreadingTCPServer((underlay, 0), DatagramExit))
            server.label, server.number, server.destination = f"vpn{number}", number, destination
            server.seen = []
            datagram_exits.append(server)
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
        if reality:
            datagram_exits, udp_outbounds = start_vless_datagram_peer(stack, singbox, directory, underlay, addresses)
        else:
            udp_outbounds = [
                {"type": "socks", "tag": f"udp-{server.label}", "server": underlay,
                 "server_port": server.server_address[1], "version": "5", "network": "udp"}
                for server in datagram_exits]
        config["outbounds"].extend(udp_outbounds)
        config["route"]["rules"][:0] = [
            {"network": "udp", "ip_cidr": [address + "/32"], "outbound": f"udp-{server.label}"}
            for address, server in zip(addresses, datagram_exits)]
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
                verify_wintun_datagrams(addresses, datagram_exits, dns_port=5353 if reality else 53)
                if reality:
                    verify_reality_fake_dns(addresses[0])
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
        print(f"PASS {'Reality Core' if reality else 'sing-box'}: {count} exit(s), Wintun TCP, UDP and DNS packets")


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
            parameters = (core.resolve(strict=True), reality, fixture, count, args.workdir.resolve())
            if args.windows_tun:
                run_windows_tun(*parameters, args.sing_box.resolve(strict=True))
            else:
                run(*parameters)


if __name__ == "__main__":
    main()
