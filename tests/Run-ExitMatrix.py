"""Exercise 1, 2 and 10 tagged VPN exits through local core processes."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack
import importlib.util
import json
from pathlib import Path
import socket
import socketserver
import subprocess
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ("fixture", "sing-box", "reality", "workdir"):
        parser.add_argument(f"--{argument}", type=Path, required=True)
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location("fixture", args.fixture.resolve(strict=True))
    fixture = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(fixture)
    args.workdir.mkdir(parents=True, exist_ok=True)
    for core, reality in ((args.sing_box, False), (args.reality, True)):
        for count in (1, 2, 10):
            run(core.resolve(strict=True), reality, fixture, count, args.workdir.resolve())


if __name__ == "__main__":
    main()
