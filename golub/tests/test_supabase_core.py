#!/usr/bin/env python3
"""
Тесты ядра на Supabase-протоколе: настоящий HTTP, локальная заглушка Supabase.

Проверяем то же главное обещание — сообщения не теряются при плохом интернете,
а теперь ещё и то, что регистрация закрыта (без верного пароля входа нет).

    cd golub && python3 -m unittest tests.test_supabase_core -v
"""
import os
import pathlib
import socket
import sys
import tempfile
import threading
import time
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "client"))
sys.path.insert(0, str(ROOT / "tests"))

import supabase_core  # noqa: E402

# ускоряем таймеры, чтобы тесты шли секунды
supabase_core.POLL_MESSAGES = 0.25
supabase_core.POLL_READS = 0.3
supabase_core.TOUCH_EVERY = 0.5
supabase_core.BACKOFF = [0.2, 0.3, 0.5, 0.8, 1.2]
supabase_core.ONLINE_WINDOW = 120.0

from mock_supabase import MockSupabase  # noqa: E402
from storage import SqliteStorage  # noqa: E402
from supabase_core import SupabaseCore  # noqa: E402

KEY = "sb_publishable_test_key"


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def wait_until(fn, timeout=20.0, interval=0.03, what="условие"):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if fn():
            return True
        time.sleep(interval)
    raise AssertionError("не дождались: %s" % what)


class Events:
    def __init__(self):
        self.items = []
        self._lock = threading.Lock()

    def __call__(self, kind, payload):
        with self._lock:
            self.items.append((kind, payload))

    def kinds(self):
        with self._lock:
            return [k for k, _ in self.items]


class BaseCase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.port = free_port()
        self.mock = MockSupabase(self.port, {"аня": "pass1234", "боря": "pass1234"}).start()
        self.addCleanup(self._stop_mock)
        self.cores = []

    def _stop_mock(self):
        try:
            self.mock.stop()
        except Exception:
            pass

    def make_core(self, name, password="pass1234", db=None, port=None):
        storage = SqliteStorage(db or os.path.join(self.tmp.name, name + ".db"))
        events = Events()
        core = SupabaseCore(
            storage=storage,
            url="http://127.0.0.1:%d" % (port or self.port),
            key=KEY,
            username=name,
            password=password,
            listener=events,
        )
        core.events = events
        self.cores.append(core)
        return core

    def tearDown(self):
        for core in self.cores:
            try:
                core.stop()
            except Exception:
                pass

    def wait_online(self, core, timeout=15):
        wait_until(lambda: core.snapshot()["status"] == "online", timeout,
                   what="%s онлайн" % core.username)


class LoginTest(BaseCase):
    def test_login_ok_and_no_self_signup(self):
        anna = self.make_core("аня")
        anna.start()
        self.wait_online(anna)
        self.assertEqual(anna.snapshot()["name"], "аня")

        # профиль второго человека виден в списке
        wait_until(lambda: any(u["name"] == "боря" for u in anna.snapshot()["users"]),
                   10, what="в списке есть боря")

    def test_unknown_user_cannot_register_himself(self):
        """Кого нет в базе — тот не войдёт: аккаунты создаёт только владелец."""
        stranger = self.make_core("чужой", password="whatever1")
        stranger.start()
        wait_until(lambda: stranger.auth_failed == "bad_pass", 10,
                   what="отказ во входе")
        time.sleep(1.0)
        self.assertEqual(stranger.snapshot()["status"], "offline")
        self.assertIn("auth_error", stranger.events.kinds())

    def test_wrong_password_rejected(self):
        bad = self.make_core("аня", password="wrongpass", db=os.path.join(self.tmp.name, "bad.db"))
        bad.start()
        wait_until(lambda: bad.auth_failed == "bad_pass", 10, what="отказ по паролю")
        time.sleep(1.0)
        self.assertEqual(bad.auth_failed, "bad_pass")


class OfflineQueueTest(BaseCase):
    def test_message_written_offline_is_delivered_later(self):
        anna = self.make_core("аня")
        borya = self.make_core("боря")
        anna.start()
        borya.start()
        self.wait_online(anna)
        self.wait_online(borya)

        self.mock.stop()                      # интернет пропал

        anna.send_text("боря", "привет, я без интернета")
        snap = anna.snapshot("боря")
        self.assertEqual(snap["outbox"], 1)
        self.assertEqual(snap["messages"][0]["status"], "pending")
        self.assertTrue([c for c in snap["chats"] if c["peer"] == "боря"][0]["pending"])

        self.mock.start()                     # интернет вернулся

        wait_until(lambda: anna.snapshot()["outbox"] == 0, 25, what="очередь ушла")
        wait_until(lambda: any(m["body"] == "привет, я без интернета"
                               for m in borya.snapshot("аня")["messages"]),
                   25, what="боря получил")

        # ровно одно сообщение — без дублей
        rows = self.mock.messages_between("аня", "боря")
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["body"], "привет, я без интернета")

    def test_queue_survives_app_restart(self):
        db = os.path.join(self.tmp.name, "аня_restart.db")
        anna = self.make_core("аня", db=db)
        anna.start()
        self.wait_online(anna)

        self.mock.stop()
        anna.send_text("боря", "написала без сети и закрыла приложение")
        self.assertEqual(anna.snapshot()["outbox"], 1)
        anna.stop()                            # приложение закрыли

        self.mock.start()                      # сеть появилась, приложение закрыто
        anna2 = self.make_core("аня", db=db)
        anna2.start()
        self.wait_online(anna2)
        wait_until(lambda: anna2.snapshot()["outbox"] == 0, 25, what="очередь ушла")
        rows = self.mock.messages_between("аня", "боря")
        self.assertEqual(len(rows), 1)

    def test_messages_while_receiver_offline_are_caught_up(self):
        anna = self.make_core("аня")
        anna.start()
        self.wait_online(anna)

        for i in range(1, 4):
            anna.send_text("боря", "сообщение %d" % i)
        wait_until(lambda: anna.snapshot()["outbox"] == 0, 20, what="очередь опустела")

        # бори кладёт телефон в карман и только потом открывает приложение
        borya = self.make_core("боря")
        borya.start()
        self.wait_online(borya)

        def caught_up():
            return sorted(m["body"] for m in borya.snapshot("аня")["messages"]) == [
                "сообщение 1", "сообщение 2", "сообщение 3"]
        wait_until(caught_up, 25, what="боря докачал")

        chat = [c for c in borya.snapshot()["chats"] if c["peer"] == "аня"][0]
        self.assertEqual(chat["unread"], 3)

        borya.open_chat("аня")
        wait_until(lambda: [c for c in borya.snapshot()["chats"]
                            if c["peer"] == "аня"][0]["unread"] == 0,
                   10, what="чат прочитан")

        # отметка доехала до сервера, и Аня видит «прочитано»
        me = self.mock.by_username["боря"]
        peer = self.mock.by_username["аня"]
        wait_until(lambda: self.mock.reads.get((me, peer), 0) == 3, 15,
                   what="сервер знает про прочтение")
        wait_until(lambda: anna._peer_reads.get("боря", 0) == 3, 15,
                   what="аня видит, что прочитано")


class DuplicateTest(BaseCase):
    def test_same_mid_is_rejected_by_database(self):
        anna = self.make_core("аня")
        anna.start()
        self.wait_online(anna)

        peer = self.mock.by_username["боря"]
        row = {
            "mid": "11111111-1111-1111-1111-111111111111",
            "sender": anna._me,
            "recipient": peer,
            "body": "один раз",
        }
        anna.api.insert("messages", anna._token, [row])
        from supabase_core import ApiError
        with self.assertRaises(ApiError) as ctx:
            anna.api.insert("messages", anna._token, [row])
        self.assertTrue(ctx.exception.is_duplicate)
        self.assertEqual(len(self.mock.messages_between("аня", "боря")), 1)

    def test_client_does_not_duplicate_after_reconnect(self):
        anna = self.make_core("аня")
        anna.start()
        self.wait_online(anna)
        self.mock.stop()
        anna.send_text("боря", "единственное сообщение")
        self.mock.start()
        wait_until(lambda: anna.snapshot()["outbox"] == 0, 25, what="ушло")
        # несколько циклов синхронизации подряд не должны плодить копии
        time.sleep(1.5)
        self.assertEqual(len(self.mock.messages_between("аня", "боря")), 1)


class ReconnectStormTest(BaseCase):
    def test_survives_repeated_drops(self):
        anna = self.make_core("аня")
        borya = self.make_core("боря")
        anna.start()
        borya.start()
        self.wait_online(anna)
        self.wait_online(borya)

        sent = []
        for i in range(3):
            self.mock.stop()
            time.sleep(0.1)
            text = "через обрыв %d" % i
            anna.send_text("боря", text)
            sent.append(text)
            self.mock.start()
            wait_until(lambda: anna.snapshot()["status"] == "online", 20,
                       what="аня снова онлайн")

        wait_until(lambda: anna.snapshot()["outbox"] == 0, 30, what="очередь ушла")

        def all_received():
            bodies = {m["body"] for m in borya.snapshot("аня")["messages"]}
            return set(sent).issubset(bodies)
        wait_until(all_received, 30, what="боря получил все")
        rows = self.mock.messages_between("аня", "боря")
        self.assertEqual(len(rows), len(sent), "ни дублей, ни потерь")


class StorageFallbackTest(unittest.TestCase):
    def test_json_storage_matches_interface(self):
        """Запасное хранилище должно вести себя как SQLite-хранилище."""
        from storage import JsonStorage
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        path = os.path.join(tmp.name, "data.json")
        for cls in (SqliteStorage, JsonStorage):
            store = cls(path + "." + cls.__name__)
            msg = {"mid": "m1", "sender": "я", "body": "привет", "ts": 1.0,
                   "seq": 5, "status": "in"}
            store.upsert_message("петя", msg)
            store.update_status("петя", "m1", "out", 5)
            store.set_cursor("__all__", 5)
            store.set_read("петя", 5)
            store.outbox_add("петя", "m2", "текст", 2.0)
            store.set_json("account", {"name": "я"})

            self.assertEqual(store.load_all_recent(10)["петя"][0]["body"], "привет")
            self.assertEqual(store.get_cursor("__all__"), 5)
            self.assertEqual(store.get_read("петя"), 5)
            self.assertEqual(len(store.outbox_list()), 1)
            self.assertEqual(store.get_json("account", {})["name"], "я")
            store.outbox_remove("m2")
            self.assertEqual(store.outbox_len(), 0)
            store.close()

        # JSON-хранилище переживает перезапуск
        store = JsonStorage(path + ".JsonStorage")
        self.assertEqual(store.get_cursor("__all__"), 5)
        self.assertEqual(store.load_all_recent(10)["петя"][0]["status"], "out")
        store.close()


if __name__ == "__main__":
    unittest.main(verbosity=2)
