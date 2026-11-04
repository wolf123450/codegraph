#!/usr/bin/env python3
"""Local dev server for the codegraph viewer.

Serves this directory statically and exposes GET /api/graph?path=<absolute
path> to read an arbitrary graph.json from disk -- so the viewer isn't
limited to file:// (which blocks fetch()) or a graph.json sitting next to
index.html. Point the viewer's "load" box at any project's graph.json and
it's remembered across reloads.

Usage: python serve.py [port]   (default 8420)

Binds to 127.0.0.1 only. This deliberately reads arbitrary local file paths
on request -- fine for a tool you run yourself, not something to expose to
a network.
"""
import http.server
import json
import os
import sys
import urllib.parse

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8420
ROOT = os.path.dirname(os.path.abspath(__file__))


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=ROOT, **kwargs)

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path == "/api/graph":
            self.handle_graph(parsed)
            return
        super().do_GET()

    def end_headers(self):
        # Static files too: without this, browsers heuristically cache
        # index.html and a plain reload can keep showing an old viewer.
        if "Cache-Control" not in "".join(map(str, getattr(self, "_headers_buffer", []))):
            self.send_header("Cache-Control", "no-cache")
        super().end_headers()

    def handle_graph(self, parsed):
        params = urllib.parse.parse_qs(parsed.query)
        path = (params.get("path") or [""])[0]
        if not path:
            self.send_error(400, "missing 'path' query parameter")
            return
        path = os.path.abspath(path)
        if not os.path.isfile(path):
            self.send_error(404, f"not found: {path}")
            return
        try:
            with open(path, "rb") as f:
                body = f.read()
        except OSError as e:
            self.send_error(500, str(e))
            return
        try:
            json.loads(body)  # fail loudly here rather than confusing the viewer
        except ValueError as e:
            self.send_error(400, f"not valid JSON: {e}")
            return
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)


if __name__ == "__main__":
    with http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler) as httpd:
        print(f"codegraph viewer: http://localhost:{PORT}/")
        print(f"serving {ROOT}")
        print("Ctrl+C to stop.")
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            pass
