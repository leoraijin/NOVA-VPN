"""Loopback HTTPS resolver for the native multi-exit DNS test."""
import argparse
import base64
import http.server
import json
import pathlib
import re
import ssl
import struct
import subprocess
import threading
import urllib.parse


def run(args):
    output = pathlib.Path(args.output).resolve()
    tls = pathlib.Path(args.tls).resolve()
    queries = []

    class Handler(http.server.BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def handle(self):
            try:
                super().handle()
            except ConnectionAbortedError:
                # A canceled spare dial can close before its HTTP request.
                pass
        def log_message(self, *_):
            pass

        def do_GET(self):
            url = urllib.parse.urlsplit(self.path)
            encoded = urllib.parse.parse_qs(url.query)["dns"][0]
            self.respond(url.path, base64.urlsafe_b64decode(encoded + "=" * (-len(encoded) % 4)))

        def do_POST(self):
            length = int(self.headers["Content-Length"])
            if not 12 < length < 4096:
                self.send_error(400)
                return
            self.respond(self.path, self.rfile.read(length))

        def respond(self, path, query):
            ident, _, questions, _, _, _ = struct.unpack_from("!6H", query)
            if questions != 1:
                self.send_error(400)
                return
            offset = 12
            labels = []
            while query[offset]:
                length = query[offset]
                offset += 1
                if length > 63:
                    self.send_error(400)
                    return
                labels.append(query[offset:offset + length].decode("ascii"))
                offset += length
            offset += 1
            kind, dns_class = struct.unpack_from("!2H", query, offset)
            if kind != (16 if args.apps else 1) or dns_class != 1:
                self.send_error(400)
                return
            queries.append({"path": path, "name": ".".join(labels), "type": kind})
            if args.apps:
                value = path.removeprefix("/").encode("ascii")
                data = bytes([len(value)]) + value
            else:
                data = b"\x7f\x00\x00\x01"
            response = (
                struct.pack("!6H", ident, 0x8180, 1, 1, 0, 0)
                + query[12:offset + 4] + b"\xc0\x0c"
                + struct.pack("!HHIH", kind, 1, 60 if args.apps else 0, len(data)) + data
            )
            self.send_response(200)
            self.send_header("Content-Type", "application/dns-message")
            self.send_header("Content-Length", str(len(response)))
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(response)
            self.close_connection = True

    with http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(tls / "server.pem", tls / "server-key.pem")
        context.set_alpn_protocols(["http/1.1"])
        server.socket = context.wrap_socket(server.socket, server_side=True)
        worker = threading.Thread(target=server.serve_forever, name="owned-doh")
        worker.start()
        try:
            with (output / "run.log").open("wb") as log:
                result = subprocess.run([
                    args.fixture, args.core, str(output), "--apps-doh" if args.apps else "--doh",
                    str(server.server_port), str(tls / "ca.pem")
                ], stdout=log, stderr=subprocess.STDOUT, check=False)
        finally:
            server.shutdown()
            worker.join()
            (output / "doh-queries.json").write_text(json.dumps(queries, indent=2), encoding="utf-8")

    print((output / "run.log").read_text(encoding="utf-8", errors="replace"))
    if result.returncode:
        raise SystemExit(result.returncode)
    if args.apps:
        expected = [
            ("/us", "shared-txt.example.test", 16), ("/fr", "shared-txt.example.test", 16),
            ("/fr", "site-fr.example.test", 16), ("/us", "fresh-txt.example.test", 16),
        ]
        if [(query["path"], query["name"], query["type"]) for query in queries] != expected:
            raise AssertionError(f"Unexpected application DoH requests: {queries}")
        trace = re.sub(r"\x1b\[[0-9;]*m", "", (output / "client.log").read_text(encoding="utf-8-sig"))
        expected_flows = [
            ("us", "shared-txt.example.test", "nova-exit-1"),
            ("default", "shared-txt.example.test", "nova-exit-0"),
            ("us", "site-fr.example.test", "nova-exit-0"),
            ("blocked", "site-us.example.test", None),
            ("missing", "missing-txt.example.test", None),
            ("fr", "failed-txt.example.test", "nova-exit-0"),
            ("us", "fresh-txt.example.test", "nova-exit-1"),
        ]
        flows = re.findall(r"\[(\d+) [^\]]+\][^\r\n]*dns: exchange ([\w.-]+)\. IN TXT", trace)
        if len(flows) != len(expected_flows):
            raise AssertionError(f"Unexpected number of native DNS flows: {flows}")
        for (identifier, name), (owner, expected_name, tag) in zip(flows, expected_flows):
            lines = [line for line in trace.splitlines() if f"[{identifier} " in line]
            tags = set(re.findall(r"outbound/[^\[]+\[([^\]]+)\]: outbound connection to", "\n".join(lines)))
            expected_tags = {tag} if tag else set()
            if name != expected_name or not any(line.endswith(f"fixture-app-{owner}.exe") for line in lines):
                raise AssertionError(f"Unexpected DNS flow owner or name for {expected_name}: {lines}")
            if tags != expected_tags:
                raise AssertionError(f"Unexpected outbound for {name}: {tags}")
        print("PASS four exact application DoH requests and seven verified DNS flow owners, with no fallback")
        return
    expected = [
        ("/fr", "other.example.test"), ("/us", "site.example.test"),
        ("/direct", "direct.example.test"), ("/us", "fresh.example.test")
    ]
    if [(query["path"], query["name"]) for query in queries] != expected:
        raise AssertionError(f"Unexpected DoH requests: {queries}")
    trace = re.sub(r"\x1b\[[0-9;]*m", "", (output / "client.log").read_text(encoding="utf-8-sig"))
    expected_tags = {
        "other.example.test": {"nova-exit-0"},
        "site.example.test": {"nova-exit-1"},
        "direct.example.test": {"nova-direct"},
        "blocked.example.test": set(),
        "missing.example.test": set(),
        "fr-failure.example.test": {"nova-exit-0"},
        "fresh.example.test": {"nova-exit-1"},
    }
    actual_tags = {}
    for host in expected_tags:
        ids = re.findall(r"\[(\d+) [^\]]+\].*inbound connection to " + re.escape(host) + r":\d+", trace)
        if len(ids) != 1:
            raise AssertionError(f"Expected one inbound request for {host}, found {ids}")
        actual_tags[host] = set(re.findall(
            r"\[" + ids[0] + r" [^\]]+\].*outbound/[^\[]+\[([^\]]+)\]: outbound connection to",
            trace,
        ))
    if actual_tags != expected_tags:
        raise AssertionError(f"Unexpected native outbound tags: {actual_tags}")
    (output / "outbound-tags.json").write_text(
        json.dumps({host: sorted(tags) for host, tags in actual_tags.items()}, indent=2), encoding="utf-8"
    )
    print("PASS four exact TLS-protected DNS requests and no DNS fallback")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for key in ("core", "fixture", "output", "tls"):
        parser.add_argument("--" + key, required=True)
    parser.add_argument("--apps", action="store_true")
    run(parser.parse_args())
