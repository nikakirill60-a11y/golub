"""
Ядро клиента «Голубь» поверх Supabase — без своего сервера.

Тот же принцип, что и в версии с WebSocket: сообщение сначала пишется в память
телефона, и только потом пытается уйти. Supabase тут — просто хранилище и
вход в аккаунт; устойчивость к плохому интернету обеспечивает этот файл.

Что делает:
  * вход по имени и паролю через Supabase Auth (+ авто-продление сессии);
  * очередь исходящих на диске, повторы с нарастающей паузой;
  * защита от дублей на уровне базы (unique по mid);
  * докачка всего пропущенного по возрастающему номеру сообщения (id);
  * отметки «прочитано» для собеседника;
  * «кто в сети» по времени последней активности.
"""
from __future__ import annotations

import json
import random
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from datetime import datetime, timezone

from supabase_settings import email_for

BACKOFF = [1, 2, 3, 5, 8, 13, 20, 25, 30]
ONLINE_WINDOW = 120.0     # сколько секунд считаем человека «в сети»
POLL_MESSAGES = 3.0       # как часто проверяем новые сообщения
POLL_READS = 10.0         # как часто смотрим отметки «прочитано»
TOUCH_EVERY = 45.0        # как часто отмечаемся «я тут»
HISTORY_CAP = 300
PAGE = 200


# ------------------------------------------------------------------ ошибки
class NetworkError(Exception):
    """Нет связи / таймаут / сервер ответил 5xx — пробуем позже."""


class AuthError(Exception):
    """Неверный логин или пароль — повторять бессмысленно."""

    def __init__(self, code):
        super().__init__(code)
        self.code = code


class ApiError(Exception):
    def __init__(self, status, payload):
        self.status = status
        self.payload = payload or {}
        msg = self.payload.get("message") or self.payload.get("error_description") \
            or str(self.payload)[:200]
        super().__init__("HTTP %s: %s" % (status, msg))

    @property
    def code(self):
        return self.payload.get("code")

    @property
    def is_duplicate(self):
        text = json.dumps(self.payload, ensure_ascii=False)
        return self.status == 409 or self.code == "23505" or "duplicate" in text.lower()


# ------------------------------------------------------------------ сеть
def _qs(params):
    out = []
    for key, value in params.items():
        out.append("%s=%s" % (
            urllib.parse.quote(str(key), safe=""),
            urllib.parse.quote(str(value), safe="(),.*:$-"),
        ))
    return "&".join(out)


class SupabaseApi:
    """Мини-клиент Supabase на стандартной библиотеке (никаких зависимостей)."""

    def __init__(self, url, key, timeout=20):
        base = url.strip().rstrip("/")
        if base.endswith("/rest/v1"):
            base = base[: -len("/rest/v1")]
        self.base = base
        self.key = key
        self.timeout = timeout

    # ---------------------------------------------------------------- запрос
    def request(self, method, path, body=None, token=None, params=None,
                prefer=None, ok=(200, 201, 204, 206)):
        url = self.base + path
        if params:
            url += "?" + _qs(params)
        data = None
        if body is not None:
            data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        req = urllib.request.Request(url, data=data, method=method)
        req.add_header("apikey", self.key)
        req.add_header("Accept", "application/json")
        if data is not None:
            req.add_header("Content-Type", "application/json")
        if token:
            req.add_header("Authorization", "Bearer " + token)
        if prefer:
            req.add_header("Prefer", prefer)
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                raw = resp.read()
                payload = json.loads(raw) if raw else None
                if resp.status not in ok:
                    raise ApiError(resp.status, payload or {})
                return payload
        except urllib.error.HTTPError as exc:
            raw = exc.read()
            try:
                payload = json.loads(raw) if raw else {}
            except ValueError:
                payload = {"message": raw.decode("utf-8", "replace")[:300]}
            if exc.code >= 500:
                raise NetworkError("сервер Supabase ответил %s" % exc.code)
            raise ApiError(exc.code, payload)
        except (urllib.error.URLError, TimeoutError, OSError, ValueError) as exc:
            raise NetworkError(str(exc))

    # ---------------------------------------------------------------- auth
    def login(self, email, password):
        try:
            return self.request(
                "POST", "/auth/v1/token",
                params={"grant_type": "password"},
                body={"email": email, "password": password},
            )
        except ApiError as exc:
            if exc.status in (400, 401, 422):
                raise AuthError("bad_pass")
            if exc.status == 429:
                raise NetworkError("слишком много попыток входа, подожди немного")
            raise

    def refresh(self, refresh_token):
        try:
            return self.request(
                "POST", "/auth/v1/token",
                params={"grant_type": "refresh_token"},
                body={"refresh_token": refresh_token},
            )
        except ApiError as exc:
            if exc.status in (400, 401, 403, 422):
                raise AuthError("session_expired")
            if exc.status == 429:
                raise NetworkError("слишком много запросов, подожди немного")
            raise

    # ---------------------------------------------------------------- данные
    def select(self, table, token, params):
        return self.request("GET", "/rest/v1/" + table, token=token, params=params) or []

    def insert(self, table, token, rows, prefer="return=representation"):
        return self.request("POST", "/rest/v1/" + table, body=rows, token=token,
                            prefer=prefer)

    def update(self, table, token, params, patch, prefer="return=minimal"):
        return self.request("PATCH", "/rest/v1/" + table, body=patch, token=token,
                            params=params, prefer=prefer)


# ------------------------------------------------------------------ ядро
def _iso_now():
    return datetime.now(timezone.utc).isoformat()


def _parse_ts(value):
    if not value:
        return time.time()
    try:
        return datetime.fromisoformat(str(value).replace("Z", "+00:00")).timestamp()
    except ValueError:
        return time.time()


def _msg(mid, sender, body, ts, seq=None, status="in"):
    return {
        "mid": mid,
        "sender": sender,
        "body": body,
        "ts": float(ts or time.time()),
        "seq": int(seq) if seq else None,
        "status": status,
    }


class SupabaseCore:
    def __init__(self, storage, url, key, username, password,
                 email_domain="golub.local", listener=None):
        self.storage = storage
        self.username = username
        self.username_lc = username.strip().lower()
        self.password = password
        self.email = email_for(username)
        self.api = SupabaseApi(url, key)
        self._listener = listener

        self._lock = threading.RLock()
        self._thread = None
        self._stop = threading.Event()
        self._wake = threading.Event()

        self._status = "offline"
        self._last_error = ""
        self.auth_failed = None

        self._session = None
        self._me = None                 # uuid текущего пользователя
        self._profiles = {}             # uuid -> {username, display_name, last_seen}
        self._by_username = {}          # username_lc -> uuid

        self._users = {}                # имя -> online
        self._chats = {}                # имя -> [msg]
        self._read = {}                 # имя -> последний прочитанный id
        self._peer_reads = {}           # имя -> докуда прочитал собеседник
        self._pending_reads = {}        # имя -> id, ещё не отправленный на сервер
        self._active = None
        self._out_meta = {}             # mid -> имя собеседника

        self._t_poll = 0.0
        self._t_reads = 0.0
        self._t_touch = 0.0
        self._t_profiles = 0.0

    # ------------------------------------------------------------- запуск
    def start(self):
        if self._thread and self._thread.is_alive():
            return
        self._stop.clear()
        self._load_state()
        self._thread = threading.Thread(target=self._run, name="golub-sb", daemon=True)
        self._thread.start()

    def stop(self, timeout=5.0):
        self._stop.set()
        self._wake.set()
        if self._thread:
            self._thread.join(timeout)

    def _load_state(self):
        with self._lock:
            self._session = self.storage.get_json("sb_session", None)
            cached = self.storage.get_json("sb_profiles", {})
            for uid, info in (cached or {}).items():
                self._profiles[uid] = info
                name = (info.get("username") or "").lower()
                if name:
                    self._by_username[name] = uid
            self._read = dict(self.storage.all_reads())
            for peer, msgs in self.storage.load_all_recent(HISTORY_CAP).items():
                self._chats[peer] = [_msg(**m) for m in msgs]
            for item in self.storage.outbox_list():
                self._out_meta[item["mid"]] = item["peer"]
                self._chats.setdefault(item["peer"], [])
                if not any(m["mid"] == item["mid"] for m in self._chats[item["peer"]]):
                    self._chats[item["peer"]].append(
                        _msg(item["mid"], self.username, item["body"], item["ts"],
                             None, "pending"))
            for name in self._chats:
                self._users.setdefault(name, False)

    # ------------------------------------------------------------- цикл
    def _run(self):
        backoff = 0
        while not self._stop.is_set():
            try:
                self._ensure_session()
                if self._status != "online":
                    self._set_status("online")
                    self._emit("login_ok", {"created": False})
                    self._t_profiles = 0
                self._tick()
                backoff = 0
                self._sleep(POLL_MESSAGES)
            except AuthError as exc:
                self.auth_failed = exc.code
                self._last_error = exc.code
                self._set_status("offline")
                self._emit("auth_error", {"code": exc.code})
                return
            except (NetworkError, ApiError) as exc:
                self._last_error = str(exc)
                self._set_status("offline")
                backoff = min(backoff + 1, len(BACKOFF) - 1)
                base = BACKOFF[backoff]
                self._sleep(base + random.uniform(0, 0.3 * base))
            except Exception as exc:  # noqa: BLE001 — ядро не должно падать
                self._last_error = "%s: %s" % (type(exc).__name__, exc)
                self._set_status("offline")
                self._sleep(5)

    def _sleep(self, seconds):
        self._wake.wait(seconds)
        self._wake.clear()

    def _tick(self):
        now = time.time()
        if now - self._t_profiles > 300 or not self._profiles:
            self._sync_profiles()
            self._t_profiles = now
        self._flush_outbox()
        self._poll_messages()
        if now - self._t_reads > POLL_READS:
            self._poll_reads()
            self._t_reads = now
        self._flush_read_marks()
        if now - self._t_touch > TOUCH_EVERY:
            self._touch_presence()
            self._t_touch = now

    # ------------------------------------------------------------- сессия
    @property
    def _token(self):
        return (self._session or {}).get("access_token")

    def _ensure_session(self):
        if self._session and self._session.get("refresh_token"):
            expires = float(self._session.get("expires_at") or 0)
            if expires - 90 > time.time() and self._session.get("access_token"):
                self._apply_session(self._session, save=False)
                return
            try:
                fresh = self.api.refresh(self._session["refresh_token"])
                self._apply_session(fresh)
                return
            except AuthError:
                # просроченный refresh-токен — пробуем войти заново по паролю
                self._session = None
        self._set_status("connecting")
        fresh = self.api.login(self.email, self.password)
        self._apply_session(fresh)

    def _apply_session(self, payload, save=True):
        session = {
            "access_token": payload.get("access_token"),
            "refresh_token": payload.get("refresh_token"),
            "expires_at": time.time() + float(payload.get("expires_in") or 3600),
            "user_id": ((payload.get("user") or {}).get("id")),
        }
        with self._lock:
            if not session["user_id"] and self._session:
                session["user_id"] = self._session.get("user_id")
            self._session = session
            self._me = session.get("user_id")
        if save:
            self.storage.set_json("sb_session", session)

    # ---------------------------------------------------------- профили/сеть
    def _sync_profiles(self):
        rows = self.api.select(
            "profiles", self._token,
            {"select": "id,username,display_name,last_seen", "limit": "500"},
        )
        now = time.time()
        with self._lock:
            profiles = {}
            by_name = {}
            for row in rows:
                info = {
                    "username": row.get("username") or "",
                    "display_name": row.get("display_name") or row.get("username") or "",
                    "last_seen": row.get("last_seen") or "",
                }
                profiles[row["id"]] = info
                key = info["username"].lower()
                if key:
                    by_name[key] = row["id"]
            self._profiles = profiles
            self._by_username = by_name
            self._users = {}
            for info in profiles.values():
                name = info["username"].lower()
                if not name or name == self.username_lc:
                    continue
                seen = _parse_ts(info["last_seen"])
                self._users[name] = (now - seen) < ONLINE_WINDOW
            self.storage.set_json("sb_profiles", profiles)
        self._emit("users")

    def _touch_presence(self):
        if not self._me:
            return
        self.api.update("profiles", self._token, {"id": "eq." + self._me},
                        {"last_seen": _iso_now()})

    # ---------------------------------------------------------- сообщения
    def _flush_outbox(self):
        items = self.storage.outbox_list()
        for item in items:
            if self._stop.is_set():
                return
            peer = item["peer"]
            peer_id = self._by_username.get(peer.lower())
            if peer_id is None:
                self._sync_profiles()
                peer_id = self._by_username.get(peer.lower())
            if peer_id is None:
                continue  # такого человека ещё нет в профилях — попробуем позже
            row = {
                "mid": item["mid"],
                "sender": self._me,
                "recipient": peer_id,
                "body": item["body"],
            }
            try:
                result = self.api.insert("messages", self._token, [row])
                new_id = None
                if isinstance(result, list) and result:
                    new_id = result[0].get("id")
            except ApiError as exc:
                if exc.is_duplicate:
                    new_id = None  # уже доставлено ранее — просто подтверждаем
                else:
                    self._fail_message(item["mid"], exc.code or "error")
                    continue
            with self._lock:
                self._out_meta.pop(item["mid"], None)
                self.storage.outbox_remove(item["mid"])
                self.storage.update_status(peer, item["mid"], "out", new_id)
                for m in self._chats.get(peer, ()):
                    if m["mid"] == item["mid"]:
                        m["status"] = "out"
                        if new_id:
                            m["seq"] = int(new_id)
            self._emit("ack", {"mid": item["mid"]})
            self._emit("messages", {"peer": peer})

    def _fail_message(self, mid, code):
        with self._lock:
            peer = self._out_meta.pop(mid, None)
            if peer:
                self.storage.outbox_remove(mid)
                self.storage.update_status(peer, mid, "error")
                for m in self._chats.get(peer, ()):
                    if m["mid"] == mid:
                        m["status"] = "error"
        self._emit("send_error", {"mid": mid, "code": code})

    def _poll_messages(self):
        cursor = self.storage.get_cursor("__all__")
        pages = 0
        touched = set()
        while pages < 20 and not self._stop.is_set():
            pages += 1
            rows = self.api.select("messages", self._token, {
                "select": "id,mid,sender,recipient,body,created_at",
                "or": "(sender.eq.%s,recipient.eq.%s)" % (self._me, self._me),
                "id": "gt.%d" % cursor,
                "order": "id.asc",
                "limit": str(PAGE),
            })
            if not rows:
                break
            with self._lock:
                for row in rows:
                    mid = row.get("mid")
                    sender_id = row.get("sender")
                    recipient_id = row.get("recipient")
                    if sender_id == self._me:
                        peer = self._name_of(recipient_id)
                        sender = self.username
                        status = "out"
                    else:
                        peer = self._name_of(sender_id)
                        sender = peer
                        status = "in"
                    if not peer:
                        peer = "неизвестный"
                    msg = _msg(mid, sender, row.get("body") or "",
                               _parse_ts(row.get("created_at")), row.get("id"), status)
                    if self._append(peer, msg):
                        touched.add(peer)
                    cursor = int(row["id"])
                self.storage.set_cursor("__all__", cursor)
                if self._active and self._active in touched:
                    pass
            for peer in touched:
                self._emit("messages", {"peer": peer})
            touched.clear()
            if self._active:
                self.mark_read(self._active)
            if len(rows) < PAGE:
                break

    def _name_of(self, uid):
        if not uid:
            return None
        if uid == self._me:
            return self.username_lc
        info = self._profiles.get(uid)
        if info and info.get("username"):
            return info["username"].lower()
        return None

    # ---------------------------------------------------------- прочитано
    def _poll_reads(self):
        rows = self.api.select("reads", self._token, {
            "select": "user_id,peer_id,last_id", "limit": "1000",
        })
        changed = []
        with self._lock:
            for row in rows:
                uid = row.get("user_id")
                peer = row.get("peer_id")
                last = int(row.get("last_id") or 0)
                if peer == self._me:
                    name = self._name_of(uid)
                    if name and self._peer_reads.get(name, -1) != last:
                        self._peer_reads[name] = last
                        changed.append(name)
                elif uid == self._me:
                    name = self._name_of(peer)
                    if name:
                        self._read[name] = max(self._read.get(name, 0), last)
        for name in changed:
            self._emit("peer_read", {"peer": name, "upto": self._peer_reads.get(name)})

    def _flush_read_marks(self):
        with self._lock:
            pending = list(self._pending_reads.items())
        for name, last_id in pending:
            peer_id = self._by_username.get(name.lower())
            if peer_id is None:
                continue
            try:
                self.api.insert(
                    "reads", self._token,
                    [{"user_id": self._me, "peer_id": peer_id, "last_id": last_id,
                      "updated_at": _iso_now()}],
                    prefer="resolution=merge-duplicates,return=minimal",
                )
            except ApiError as exc:
                if exc.is_duplicate:
                    pass
                else:
                    continue
            with self._lock:
                if self._pending_reads.get(name) == last_id:
                    self._pending_reads.pop(name, None)

    # -------------------------------------------------- публичное API
    def snapshot(self, peer=None):
        with self._lock:
            users = []
            for name, online in self._users.items():
                users.append({"name": name, "display": self._display(name),
                              "online": bool(online)})
            users.sort(key=lambda u: (not u["online"], u["display"].lower()))
            chats = []
            for name, msgs in self._chats.items():
                if not msgs:
                    continue
                last = msgs[-1]
                read = self._read.get(name, 0)
                unread = sum(1 for m in msgs if m["sender"] != self.username
                             and (m["seq"] or 0) > read)
                chats.append({
                    "peer": name,
                    "display": self._display(name),
                    "online": self._users.get(name, False),
                    "last": last["body"],
                    "last_ts": last["ts"],
                    "last_mine": last["sender"] == self.username,
                    "pending": last["status"] == "pending",
                    "unread": unread,
                })
            chats.sort(key=lambda c: c["last_ts"], reverse=True)
            active = self._active
            shown = peer if peer is not None else active
            return {
                "status": self._status,
                "error": self._last_error,
                "name": self.username,
                "users": users,
                "chats": chats,
                "active": active,
                "active_display": self._display(active) if active else "",
                "active_online": self._users.get(active, False) if active else False,
                "messages": [dict(m) for m in self._chats.get(shown, [])] if shown else [],
                "outbox": len(self._out_meta),
            }

    def _display(self, name):
        if not name:
            return ""
        uid = self._by_username.get(name.lower())
        info = self._profiles.get(uid) if uid else None
        if info:
            return info.get("display_name") or info.get("username") or name
        return name

    def open_chat(self, peer):
        with self._lock:
            self._active = peer
            self._chats.setdefault(peer, [])
        self.mark_read(peer)
        self._emit("chat_open", {"peer": peer})
        self.kick()

    def close_chat(self, peer=None):
        with self._lock:
            if peer is None or self._active == peer:
                self._active = None
        self._emit("chat_close", {})

    def mark_read(self, peer):
        with self._lock:
            msgs = self._chats.get(peer, [])
            head = max((m["seq"] or 0) for m in msgs) if msgs else 0
            if head <= self._read.get(peer, 0):
                self._read.setdefault(peer, 0)
                return
            self._read[peer] = head
            self._pending_reads[peer] = head
            self.storage.set_read(peer, head)
        self._emit("read", {"peer": peer})
        self.kick()

    def send_text(self, peer, body):
        body = (body or "").strip()
        if not peer or not body:
            return None
        mid = str(uuid.uuid4())
        ts = time.time()
        with self._lock:
            self._chats.setdefault(peer, [])
            self._append(peer, _msg(mid, self.username, body, ts, None, "pending"))
            self._out_meta[mid] = peer
            self.storage.outbox_add(peer, mid, body, ts)
        self._emit("messages", {"peer": peer})
        self._emit("outbox")
        self.kick()
        return mid

    def refresh_users(self):
        self._t_profiles = 0
        self.kick()

    def kick(self):
        self._wake.set()

    # ---------------------------------------------------------- служебное
    def _append(self, peer, msg):
        lst = self._chats.setdefault(peer, [])
        if any(m["mid"] == msg["mid"] for m in lst):
            return False
        lst.append(msg)
        lst.sort(key=lambda m: ((0, m["seq"]) if m["seq"] else (1, m["ts"])))
        if len(lst) > HISTORY_CAP:
            del lst[: len(lst) - HISTORY_CAP]
        self.storage.upsert_message(peer, msg)
        return True

    def _set_status(self, status):
        with self._lock:
            if self._status == status:
                return
            self._status = status
        self._emit("status", {"state": status})

    def _emit(self, kind, payload=None):
        listener = self._listener
        if listener is None:
            return
        try:
            listener(kind, payload or {})
        except Exception:  # noqa: BLE001
            pass
