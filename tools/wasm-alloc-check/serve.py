"""Static file server for the published app that also accepts the page's result as a POST to /result."""
import functools
import http.server
import sys

root, port, result_path = sys.argv[1], int(sys.argv[2]), sys.argv[3]


class Handler(http.server.SimpleHTTPRequestHandler):
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        with open(result_path + ".tmp", "wb") as f:
            f.write(body)
        import os
        os.replace(result_path + ".tmp", result_path)
        self.send_response(204)
        self.end_headers()

    def log_message(self, *args):
        pass


http.server.ThreadingHTTPServer(("127.0.0.1", port), functools.partial(Handler, directory=root)).serve_forever()
