"""Локальное хранилище клиента: SQLite. Переживает закрытие приложения и
перезагрузку телефона — очередь сообщений и история не теряются."""
from __future__ import annotations

import json
import sqlite3
import threading


class SqliteStorage:
    def __init__(self, path):
        self._lock = threading.RLock()
        self.db = sqlite3.connect(path, check_same_thread=False)
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.executescript(
            """
            CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT);
            CREATE TABLE IF NOT EXISTS chats(
                peer   TEXT NOT NULL,
                mid    TEXT NOT NULL,
                sender TEXT NOT NULL,
                body   TEXT NOT NULL,
                ts     REAL NOT NULL,
                seq    INTEGER,
                status TEXT NOT NULL,
                PRIMARY KEY(peer, mid)
            );
            CREATE TABLE IF NOT EXISTS outbox(
                peer TEXT NOT NULL,
                mid  TEXT PRIMARY KEY,
                body TEXT NOT NULL,
                ts   REAL NOT NULL,
                tries INTEGER NOT NULL DEFAULT 0
            );
            """
        )
        self.db.commit()

    # ---- meta ----
    def get_meta(self, key, default=None):
        with self._lock:
            row = self.db.execute("SELECT v FROM meta WHERE k=?", (key,)).fetchone()
        return row[0] if row else default

    def set_meta(self, key, value):
        with self._lock:
            self.db.execute(
                "INSERT INTO meta(k, v) VALUES(?,?) "
                "ON CONFLICT(k) DO UPDATE SET v=excluded.v",
                (key, str(value)),
            )
            self.db.commit()

    def get_json(self, key, default):
        raw = self.get_meta(key)
        if not raw:
            return default
        try:
            return json.loads(raw)
        except ValueError:
            return default

    def set_json(self, key, value):
        self.set_meta(key, json.dumps(value, ensure_ascii=False))

    # ---- сообщения ----
    def upsert_message(self, peer, msg):
        with self._lock:
            self.db.execute(
                "INSERT INTO chats(peer, mid, sender, body, ts, seq, status) "
                "VALUES(?,?,?,?,?,?,?) "
                "ON CONFLICT(peer, mid) DO UPDATE SET seq=excluded.seq, status=excluded.status",
                (peer, msg["mid"], msg["sender"], msg["body"], msg["ts"],
                 msg.get("seq"), msg.get("status", "in")),
            )
            self.db.commit()

    def update_status(self, peer, mid, status, seq=None):
        with self._lock:
            self.db.execute(
                "UPDATE chats SET status=?, seq=COALESCE(?, seq) WHERE peer=? AND mid=?",
                (status, seq, peer, mid),
            )
            self.db.commit()

    def load_all_recent(self, limit_per_chat):
        with self._lock:
            rows = self.db.execute(
                """
                SELECT peer, mid, sender, body, ts, seq, status FROM (
                    SELECT *, ROW_NUMBER() OVER (
                        PARTITION BY peer ORDER BY
                        CASE WHEN seq IS NULL THEN 1 ELSE 0 END, seq DESC, ts DESC
                    ) AS rn FROM chats
                ) WHERE rn <= ? ORDER BY ts ASC
                """,
                (limit_per_chat,),
            ).fetchall()
        out = {}
        for peer, mid, sender, body, ts, seq, status in rows:
            out.setdefault(peer, []).append({
                "mid": mid, "sender": sender, "body": body, "ts": ts,
                "seq": seq, "status": status,
            })
        return out

    def clear_chat(self, peer):
        with self._lock:
            self.db.execute("DELETE FROM chats WHERE peer=?", (peer,))
            self.db.commit()

    # ---- курсоры ----
    def set_cursor(self, peer, seq):
        self.set_meta("cursor:" + peer, int(seq))

    def get_cursor(self, peer):
        try:
            return int(self.get_meta("cursor:" + peer, 0) or 0)
        except ValueError:
            return 0

    def all_cursors(self):
        with self._lock:
            rows = self.db.execute("SELECT k, v FROM meta WHERE k LIKE 'cursor:%'").fetchall()
        out = {}
        for key, value in rows:
            try:
                out[key[len("cursor:"):]] = int(value)
            except ValueError:
                pass
        return out

    def set_read(self, peer, seq):
        self.set_meta("read:" + peer, int(seq))

    def get_read(self, peer):
        try:
            return int(self.get_meta("read:" + peer, 0) or 0)
        except ValueError:
            return 0

    def all_reads(self):
        with self._lock:
            rows = self.db.execute("SELECT k, v FROM meta WHERE k LIKE 'read:%'").fetchall()
        out = {}
        for key, value in rows:
            try:
                out[key[len("read:"):]] = int(value)
            except ValueError:
                pass
        return out

    # ---- очередь отправки ----
    def outbox_add(self, peer, mid, body, ts):
        with self._lock:
            self.db.execute(
                "INSERT OR IGNORE INTO outbox(peer, mid, body, ts) VALUES(?,?,?,?)",
                (peer, mid, body, ts),
            )
            self.db.commit()

    def outbox_remove(self, mid):
        with self._lock:
            self.db.execute("DELETE FROM outbox WHERE mid=?", (mid,))
            self.db.commit()

    def outbox_list(self):
        with self._lock:
            rows = self.db.execute(
                "SELECT peer, mid, body, ts FROM outbox ORDER BY ts ASC"
            ).fetchall()
        return [
            {"peer": p, "mid": m, "body": b, "ts": t} for p, m, b, t in rows
        ]

    def outbox_len(self):
        with self._lock:
            return self.db.execute("SELECT COUNT(*) FROM outbox").fetchone()[0]

    def close(self):
        with self._lock:
            self.db.close()


class JsonStorage:
    """Запасное хранилище на JSON-файле.

    Нужно на случай, если в конкретной сборке Python для Android не окажется
    модуля sqlite3. Интерфейс полностью совпадает с SqliteStorage, поэтому
    остальной код разницы не замечает. Данные пишутся атомарно (tmp + replace),
    так что внезапное выключение телефона их не портит.
    """

    def __init__(self, path):
        import os
        self.path = path
        self._lock = threading.RLock()
        self._data = {"meta": {}, "chats": {}, "outbox": {}}
        try:
            if os.path.exists(path):
                with open(path, "r", encoding="utf-8") as fh:
                    loaded = json.load(fh)
                if isinstance(loaded, dict):
                    self._data["meta"] = loaded.get("meta") or {}
                    self._data["chats"] = loaded.get("chats") or {}
                    self._data["outbox"] = loaded.get("outbox") or {}
        except Exception:
            pass  # битый файл — начинаем с чистого листа, но не падаем

    def _flush(self):
        import os
        self._data = self._data  # no-op для читаемости
        tmp = self.path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as fh:
            json.dump(self._data, fh, ensure_ascii=False)
        os.replace(tmp, self.path)

    # ---- meta ----
    def get_meta(self, key, default=None):
        with self._lock:
            return self._data["meta"].get(key, default)

    def set_meta(self, key, value):
        with self._lock:
            self._data["meta"][key] = str(value)
            self._flush()

    def get_json(self, key, default):
        raw = self.get_meta(key)
        if not raw:
            return default
        try:
            return json.loads(raw)
        except ValueError:
            return default

    def set_json(self, key, value):
        self.set_meta(key, json.dumps(value, ensure_ascii=False))

    # ---- сообщения ----
    def upsert_message(self, peer, msg):
        with self._lock:
            chat = self._data["chats"].setdefault(peer, {})
            chat[msg["mid"]] = {
                "mid": msg["mid"],
                "sender": msg["sender"],
                "body": msg["body"],
                "ts": msg["ts"],
                "seq": msg.get("seq"),
                "status": msg.get("status", "in"),
            }
            self._flush()

    def update_status(self, peer, mid, status, seq=None):
        with self._lock:
            row = self._data["chats"].get(peer, {}).get(mid)
            if row:
                row["status"] = status
                if seq:
                    row["seq"] = seq
                self._flush()

    def load_all_recent(self, limit_per_chat):
        with self._lock:
            out = {}
            for peer, chat in self._data["chats"].items():
                rows = list(chat.values())
                rows.sort(key=lambda m: ((1, m["ts"]) if not m.get("seq") else (0, m["seq"])))
                rows = rows[-limit_per_chat:]
                out[peer] = [dict(r) for r in rows]
            return out

    def clear_chat(self, peer):
        with self._lock:
            self._data["chats"].pop(peer, None)
            self._flush()

    # ---- курсоры ----
    def set_cursor(self, peer, seq):
        self.set_meta("cursor:" + peer, int(seq))

    def get_cursor(self, peer):
        try:
            return int(self.get_meta("cursor:" + peer, 0) or 0)
        except ValueError:
            return 0

    def all_cursors(self):
        with self._lock:
            return {k[len("cursor:"):]: int(v)
                    for k, v in self._data["meta"].items()
                    if k.startswith("cursor:") and str(v).isdigit()}

    def set_read(self, peer, seq):
        self.set_meta("read:" + peer, int(seq))

    def get_read(self, peer):
        try:
            return int(self.get_meta("read:" + peer, 0) or 0)
        except ValueError:
            return 0

    def all_reads(self):
        with self._lock:
            return {k[len("read:"):]: int(v)
                    for k, v in self._data["meta"].items()
                    if k.startswith("read:") and str(v).isdigit()}

    # ---- очередь отправки ----
    def outbox_add(self, peer, mid, body, ts):
        with self._lock:
            self._data["outbox"][mid] = {"peer": peer, "body": body, "ts": ts}
            self._flush()

    def outbox_remove(self, mid):
        with self._lock:
            if self._data["outbox"].pop(mid, None) is not None:
                self._flush()

    def outbox_list(self):
        with self._lock:
            rows = [dict(v, mid=k) for k, v in self._data["outbox"].items()]
            rows.sort(key=lambda r: r["ts"])
            return rows

    def outbox_len(self):
        with self._lock:
            return len(self._data["outbox"])

    def close(self):
        with self._lock:
            try:
                self._flush()
            except Exception:
                pass


def make_storage(path):
    """SQLite, если доступен; иначе JSON. Остальному коду всё равно."""
    try:
        import sqlite3  # noqa: F401
        return SqliteStorage(path)
    except Exception:
        return JsonStorage(path)
