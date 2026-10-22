#!/usr/bin/env python3
"""
Локальная «заглушка» Supabase для автотестов.

Повторяет ровно те эндпоинты, которые использует приложение:
  POST /auth/v1/token?grant_type=password|refresh_token
  GET  /rest/v1/profiles           PATCH /rest/v1/profiles?id=eq.UUID
  GET  /rest/v1/messages           POST  /rest/v1/messages
  GET  /rest/v1/reads              POST  /rest/v1/reads

Позволяет остановить и поднять «сервер» в середине теста — это и есть
имитация пропавшего интернета.
"""
import json
import threading
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse


class MockSupabase:
    def __init__(self, port, users=None, secret="sb_secret_test"):
        self.port = port
        self.secret = secret
        self.lock = threading.RLock()
        self.users = {}        # uuid -> {username, password, display_name, last_seen}
        self.by_username = {}
        self.messages = []     # {id, mid, sender, recipient, body, created_at}
        self.reads = {}        # (user_id, peer_id) -> last_id
        self.next_id = 1
        for username, password in (users or {}).items():
            self.add_user(username, password)
        self.httpd = None
        self.thread = None

    # ------------------------------------------------------------- данные
    def add_user(self, username, password, display_name=None):
        uid = str(uuid.uuid4())
        self.users[uid] = {
            "username": username,
            "password": password,
            "display_name": display_name or username,
            "last_seen": "2026-01-01T00:00:00+00:00",
        }
        self.by_username[username.lower()] = uid
        return uid

    def user_by_token(self, token):
        """Токен — ASCII, как настоящий JWT: tok:<uuid>."""
        if not token or not token.startswith("Bearer tok:"):
            return None
        uid = token.split("tok:", 1)[1]
        return uid if uid in self.users else None

    @property
    def url(self):
        return "http://127.0.0.1:%d" % self.port

    # ------------------------------------------------------------- сервер
    def start(self):
        handler = _make_handler(self)
        self.httpd = ThreadingHTTPServer(("127.0.0.1", self.port), handler)
        self.httpd.daemon_threads = True
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)
        self.thread.start()
        return self

    def stop(self):
        if self.httpd:
            self.httpd.shutdown()
            self.httpd.server_close()
            self.httpd = None
        self.thread = None

    def messages_between(self, a, b):
        ua = self.by_username[a.lower()]
        ub = self.by_username[b.lower()]
        with self.lock:
            return [m for m in self.messages
                    if {m["sender"], m["recipient"]} == {ua, ub}]


def _make_handler(mock):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        # не засорять вывод тестов
        def log_message(self, *args):
            pass

        # ------------------------------------------------------------ utils
        def _send(self, status, payload):
            raw = b"" if payload is None else json.dumps(
                payload, ensure_ascii=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(raw)))
            self.end_headers()
            if raw:
                self.wfile.write(raw)

        def _body(self):
            length = int(self.headers.get("Content-Length") or 0)
            if not length:
                return {}
            try:
                return json.loads(self.rfile.read(length) or b"{}")
            except ValueError:
                return {}

        def _me(self):
            return mock.user_by_token(self.headers.get("Authorization"))

        def _query(self):
            return parse_qs(urlparse(self.path).query, keep_blank_values=True)

        def _unauthorized(self):
            self._send(401, {"message": "Unauthorized"})

        # ------------------------------------------------------------ auth
        def do_POST_auth(self, query):
            body = self._body()
            grant = (query.get("grant_type") or [""])[0]
            if grant == "password":
                email = (body.get("email") or "").strip().lower()
                password = body.get("password") or ""
                username = email.split("@")[0]
                uid = mock.by_username.get(username)
                if not uid or mock.users[uid]["password"] != password:
                    self._send(400, {"error_description": "Invalid login credentials",
                                     "message": "Invalid login credentials"})
                    return
            elif grant == "refresh_token":
                token = body.get("refresh_token") or ""
                if not token.startswith("ref:"):
                    self._send(400, {"message": "Invalid Refresh Token"})
                    return
                uid = token[4:]
                if uid not in mock.users:
                    self._send(400, {"message": "Invalid Refresh Token"})
                    return
            else:
                self._send(400, {"message": "unsupported grant_type"})
                return
            username = mock.users[uid]["username"]
            self._send(200, {
                "access_token": "tok:" + uid,
                "refresh_token": "ref:" + uid,
                "token_type": "bearer",
                "expires_in": 3600,
                "user": {"id": uid, "email": username + "@golub.local"},
            })

        # ------------------------------------------------------------ данные
        def do_GET_profiles(self):
            with mock.lock:
                rows = [{
                    "id": uid,
                    "username": info["username"],
                    "display_name": info["display_name"],
                    "last_seen": info["last_seen"],
                } for uid, info in mock.users.items()]
            self._send(200, rows)

        def do_PATCH_profiles(self, query):
            me = self._me()
            if not me:
                return self._unauthorized()
            body = self._body()
            uid = (query.get("id") or ["eq."])[0].replace("eq.", "")
            with mock.lock:
                if uid in mock.users and uid == me:
                    if "last_seen" in body:
                        mock.users[uid]["last_seen"] = body["last_seen"]
            self._send(204, None)

        def do_GET_messages(self, query):
            me = self._me()
            if not me:
                return self._unauthorized()
            cursor = 0
            if query.get("id"):
                raw = query["id"][0]
                if raw.startswith("gt."):
                    cursor = int(raw[3:])
            limit = int((query.get("limit") or ["200"])[0])
            with mock.lock:
                rows = [dict(m) for m in mock.messages
                        if m["id"] > cursor
                        and (m["sender"] == me or m["recipient"] == me)]
            rows.sort(key=lambda m: m["id"])
            self._send(200, rows[:limit])

        def do_POST_messages(self):
            me = self._me()
            if not me:
                return self._unauthorized()
            body = self._body()
            rows = body if isinstance(body, list) else [body]
            out = []
            with mock.lock:
                for row in rows:
                    if row.get("sender") != me:
                        self._send(403, {"message": "row-level security"})
                        return
                    if any(m["mid"] == row.get("mid") for m in mock.messages):
                        self._send(409, {"code": "23505",
                                         "message": "duplicate key value violates "
                                                    "unique constraint \"messages_mid_key\""})
                        return
                    rec = {
                        "id": mock.next_id,
                        "mid": row.get("mid"),
                        "sender": me,
                        "recipient": row.get("recipient"),
                        "body": row.get("body"),
                        "created_at": "2026-10-22T10:00:%02d+00:00" % (mock.next_id % 60),
                    }
                    mock.next_id += 1
                    mock.messages.append(rec)
                    out.append(dict(rec))
            self._send(201, out)

        def do_GET_reads(self):
            me = self._me()
            if not me:
                return self._unauthorized()
            with mock.lock:
                rows = [{"user_id": u, "peer_id": p, "last_id": last}
                        for (u, p), last in mock.reads.items()
                        if u == me or p == me]
            self._send(200, rows)

        def do_POST_reads(self):
            me = self._me()
            if not me:
                return self._unauthorized()
            body = self._body()
            rows = body if isinstance(body, list) else [body]
            with mock.lock:
                for row in rows:
                    if row.get("user_id") != me:
                        self._send(403, {"message": "row-level security"})
                        return
                    key = (me, row.get("peer_id"))
                    mock.reads[key] = max(mock.reads.get(key, 0),
                                          int(row.get("last_id") or 0))
            self._send(201, None)

        # ------------------------------------------------------------ админка
        def _is_admin(self):
            auth = self.headers.get("Authorization") or ""
            return auth.startswith("Bearer ") and auth[7:].strip() == mock.secret

        def _public_user(self, uid):
            info = mock.users[uid]
            return {
                "id": uid,
                "email": info["username"] + "@golub.local",
                "created_at": "2026-10-01T12:00:00+00:00",
                "user_metadata": {
                    "username": info["username"],
                    "display_name": info["display_name"],
                },
            }

        def do_admin_users(self, method, uid=None):
            if not self._is_admin():
                self._send(401, {"message": "invalid secret key"})
                return
            if method == "GET":
                with mock.lock:
                    rows = [self._public_user(u) for u in mock.users]
                self._send(200, {"users": rows})
                return
            if method == "POST":
                body = self._body()
                email = (body.get("email") or "").lower()
                username = email.split("@")[0]
                if username in mock.by_username:
                    self._send(422, {"msg": "User already registered"})
                    return
                meta = body.get("user_metadata") or {}
                new_uid = mock.add_user(username, body.get("password") or "",
                                        meta.get("display_name") or username)
                self._send(200, self._public_user(new_uid))
                return
            if method == "PUT":
                if uid not in mock.users:
                    self._send(404, {"msg": "User not found"})
                    return
                body = self._body()
                if body.get("password"):
                    mock.users[uid]["password"] = body["password"]
                self._send(200, self._public_user(uid))
                return
            if method == "DELETE":
                if uid not in mock.users:
                    self._send(404, {"msg": "User not found"})
                    return
                with mock.lock:
                    name = mock.users[uid]["username"].lower()
                    mock.by_username.pop(name, None)
                    mock.users.pop(uid, None)
                self._send(200, {"message": "deleted"})
                return
            self._send(405, {"message": "method not allowed"})

        # ------------------------------------------------------------ маршруты
        def do_GET(self):
            path = urlparse(self.path).path
            query = self._query()
            if path.startswith("/auth/v1/admin/users"):
                return self.do_admin_users("GET")
            if path == "/rest/v1/profiles":
                return self.do_GET_profiles()
            if path == "/rest/v1/messages":
                return self.do_GET_messages(query)
            if path == "/rest/v1/reads":
                return self.do_GET_reads()
            self._send(404, {"message": "not found: " + path})

        def do_POST(self):
            path = urlparse(self.path).path
            query = self._query()
            if path.startswith("/auth/v1/admin/users"):
                return self.do_admin_users("POST")
            if path == "/auth/v1/token":
                return self.do_POST_auth(query)
            if path == "/rest/v1/messages":
                return self.do_POST_messages()
            if path == "/rest/v1/reads":
                return self.do_POST_reads()
            self._send(404, {"message": "not found: " + path})

        def do_PATCH(self):
            path = urlparse(self.path).path
            query = self._query()
            if path == "/rest/v1/profiles":
                return self.do_PATCH_profiles(query)
            self._send(404, {"message": "not found: " + path})

        def do_PUT(self):
            path = urlparse(self.path).path
            if path.startswith("/auth/v1/admin/users/"):
                return self.do_admin_users("PUT", path.rsplit("/", 1)[-1])
            self._send(404, {"message": "not found: " + path})

        def do_DELETE(self):
            path = urlparse(self.path).path
            if path.startswith("/auth/v1/admin/users/"):
                return self.do_admin_users("DELETE", path.rsplit("/", 1)[-1])
            self._send(404, {"message": "not found: " + path})

    return Handler


if __name__ == "__main__":
    mock = MockSupabase(8765, {"аня": "pass1234", "боря": "pass1234"}).start()
    print("Заглушка Supabase: http://127.0.0.1:8765")
    try:
        threading.Event().wait()
    except KeyboardInterrupt:
        mock.stop()
