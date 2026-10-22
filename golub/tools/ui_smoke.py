#!/usr/bin/env python3
"""
Автотест интерфейса без монитора (SDL offscreen) + скриншоты.

Поднимает локальную заглушку Supabase, входит как «аня», «боря» пишет ей
сообщения, затем «интернет пропадает» прямо во время переписки.

    cd golub && python3 tools/ui_smoke.py /tmp/golub_shots
"""
import os
import sys
import tempfile
import threading
import time

os.environ.setdefault("SDL_VIDEODRIVER", "offscreen")
os.environ.setdefault("KIVY_NO_ARGS", "1")
os.environ.setdefault("KIVY_LOG_LEVEL", "warning")

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(ROOT, "client"))
sys.path.insert(0, os.path.join(ROOT, "tests"))

import supabase_core  # noqa: E402

supabase_core.POLL_MESSAGES = 0.4
supabase_core.POLL_READS = 0.5
supabase_core.BACKOFF = [0.4, 0.8, 1.2, 2.0]

from mock_supabase import MockSupabase  # noqa: E402

SHOTS = sys.argv[1] if len(sys.argv) > 1 else os.path.join(tempfile.mkdtemp(), "shots")
os.makedirs(SHOTS, exist_ok=True)
TMP = tempfile.mkdtemp(prefix="golub_ui_")
os.environ["GOLUB_DB"] = os.path.join(TMP, "ui.db")

PORT = 8799
MOCK = MockSupabase(PORT, {"аня": "pass1234", "боря": "pass1234"}).start()
URL = MOCK.url
KEY = "sb_publishable_test_key"

import main as client_main  # noqa: E402  (первым: он задаёт размер окна)
from kivy.core.window import Window  # noqa: E402
from supabase_core import SupabaseCore  # noqa: E402
from storage import make_storage  # noqa: E402


def bot_thread():
    """Второй телефон — «боря» пишет Ане."""
    storage = make_storage(os.path.join(TMP, "bot.db"))
    bot = SupabaseCore(storage, URL, KEY, "боря", "pass1234", listener=None)
    bot.start()
    try:
        deadline = time.time() + 20
        while time.time() < deadline and bot.snapshot()["status"] != "online":
            time.sleep(0.1)
        time.sleep(1.0)
        bot.send_text("аня", "Привет! Проверяю связь.")
        time.sleep(1.5)
        bot.send_text("аня", "Если сеть пропадёт — сообщения всё равно дойдут.")
        time.sleep(40)
    finally:
        bot.stop()


def snap(tag):
    Window.screenshot(name=os.path.join(SHOTS, tag + ".png"))


app = client_main.Application()


def on_start(*_):
    threading.Thread(target=bot_thread, daemon=True).start()

    def at(t, fn):
        from kivy.clock import Clock
        Clock.schedule_once(fn, t)

    at(0.8, lambda *_: snap("01-login"))
    at(1.0, lambda *_: app.login_screen.toggle_settings())
    at(1.4, lambda *_: snap("02-login-settings"))
    at(1.8, lambda *_: app.login_screen.toggle_settings())
    at(2.0, lambda *_: app.login(URL, KEY, "аня", "pass1234"))
    at(4.5, lambda *_: snap("03-chats"))
    at(5.0, lambda *_: app.open_chat("боря"))
    at(6.5, lambda *_: snap("04-chat"))

    def type_and_send(*_):
        app.chat_screen.input.text = "Привет, боря! У меня всё работает."
        app.chat_screen.send()

    at(7.0, type_and_send)
    at(8.0, lambda *_: snap("05-chat-sent"))
    at(8.5, lambda *_: app.open_users())
    at(9.5, lambda *_: snap("06-users"))

    # --- главная фича: сеть пропала, сообщение ждёт в очереди ---
    at(10.0, lambda *_: app.open_chat("боря"))
    at(10.6, lambda *_: MOCK.stop())

    def offline_send(*_):
        app.chat_screen.input.text = "Свет отключили, а я пишу. Дойдёт позже."
        app.chat_screen.send()

    at(12.5, offline_send)
    at(14.5, lambda *_: snap("07-offline-queue"))
    at(15.0, lambda *_: MOCK.start())
    at(21.0, lambda *_: snap("08-back-online"))
    at(22.0, lambda *_: app.stop())


app.on_start = on_start
try:
    app.run()
finally:
    MOCK.stop()
    print("Скриншоты:", SHOTS)
    for name in sorted(os.listdir(SHOTS)):
        print("  ", name, os.path.getsize(os.path.join(SHOTS, name)), "байт")
