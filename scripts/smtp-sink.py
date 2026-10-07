#!/usr/bin/env python3
"""
Minimal SMTP sink for local testing.

Accepts every message (no TLS, no auth), never delivers anything, and prints how many messages
arrived and the peak number of simultaneous connections, which is how the smoke test verifies that
Notify really sends with N parallel connections.

Usage:  python3 scripts/smtp-sink.py [port=2525] [seconds=120] [bind=127.0.0.1]
Point Notify at it with Host=127.0.0.1, Port=<port>, Security=None, empty username/password.
Use bind=0.0.0.0 when Notify runs in a container and connects via host.docker.internal.
"""
import socketserver
import sys
import threading
import time

lock = threading.Lock()
stats = {"messages": 0, "active": 0, "peak": 0}


class Handler(socketserver.StreamRequestHandler):
    def send(self, line: str) -> None:
        self.wfile.write((line + "\r\n").encode())

    def handle(self) -> None:
        with lock:
            stats["active"] += 1
            stats["peak"] = max(stats["peak"], stats["active"])
        try:
            self.send("220 sink ESMTP")
            in_data = False
            while True:
                raw = self.rfile.readline()
                if not raw:
                    break
                line = raw.decode(errors="replace").rstrip("\r\n")
                if in_data:
                    if line == ".":
                        in_data = False
                        time.sleep(0.2)  # slow server so parallelism is observable
                        with lock:
                            stats["messages"] += 1
                        self.send("250 OK queued")
                    continue
                cmd = line.split(" ", 1)[0].upper()
                if cmd == "EHLO":
                    self.send("250-sink")
                    self.send("250 8BITMIME")
                elif cmd == "HELO":
                    self.send("250 sink")
                elif cmd in ("MAIL", "RCPT", "NOOP", "RSET"):
                    self.send("250 OK")
                elif cmd == "DATA":
                    in_data = True
                    self.send("354 End data with <CR><LF>.<CR><LF>")
                elif cmd == "QUIT":
                    self.send("221 Bye")
                    break
                else:
                    self.send("500 Unknown command")
        finally:
            with lock:
                stats["active"] -= 1


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main() -> None:
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 2525
    duration = int(sys.argv[2]) if len(sys.argv) > 2 else 120
    bind = sys.argv[3] if len(sys.argv) > 3 else "127.0.0.1"
    server = Server((bind, port), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    print(f"sink listening on {bind}:{port} for {duration}s", flush=True)
    start = time.time()
    last = -1
    while time.time() - start < duration:
        time.sleep(1)
        if stats["messages"] != last:
            last = stats["messages"]
            print(f"messages={stats['messages']} peak_concurrent_connections={stats['peak']}", flush=True)
    server.shutdown()
    print(f"FINAL messages={stats['messages']} peak_concurrent_connections={stats['peak']}", flush=True)


if __name__ == "__main__":
    main()
