#!/usr/bin/env python3
"""
Тесты скрипта владельца admin.py: создание, смена пароля, удаление аккаунтов.
Работают на локальной заглушке Supabase, без настоящего проекта.

    cd golub && python3 -m unittest tests.test_admin -v
"""
import io
import os
import pathlib
import socket
import sys
import tempfile
import time
import unittest
from contextlib import redirect_stdout

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "client"))
sys.path.insert(0, str(ROOT / "tests"))

import admin as admin_mod  # noqa: E402
import supabase_core  # noqa: E402
from mock_supabase import MockSupabase  # noqa: E402
from storage import SqliteStorage  # noqa: E402
from supabase_core import SupabaseCore  # noqa: E402

supabase_core.POLL_MESSAGES = 0.2
supabase_core.BACKOFF = [0.2, 0.4, 0.8]

SECRET = "sb_secret_test"


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def wait_until(fn, timeout=15.0, what="условие"):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if fn():
            return True
        time.sleep(0.03)
    raise AssertionError("не дождались: %s" % what)


class AdminTest(unittest.TestCase):
    def setUp(self):
        self.port = free_port()
        self.mock = MockSupabase(self.port, {"аня": "pass1234"}, secret=SECRET).start()
        self.addCleanup(self.mock.stop)
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.admin = admin_mod.Admin(self.mock.url, SECRET)
        self.admin.domain = "golub.local"

    # ------------------------------------------------------------------
    def run_cmd(self, fn, *args):
        out = io.StringIO()
        with redirect_stdout(out):
            code = fn(*args)
        return code, out.getvalue()

    def ns(self, **kw):
        return type("Args", (), kw)()

    def login_works(self, username, password):
        storage = SqliteStorage(os.path.join(self.tmp.name, "%s_%s.db" % (username, password)))
        core = SupabaseCore(storage, self.mock.url, "sb_publishable_test", username,
                            password)
        core.start()
        try:
            wait_until(lambda: core.snapshot()["status"] == "online"
                       or core.auth_failed is not None, 10, what="попытка входа")
            return core.snapshot()["status"] == "online"
        finally:
            core.stop()

    # ------------------------------------------------------------------
    def test_add_user_then_login(self):
        code, out = self.run_cmd(self.admin.cmd_add, self.ns(
            username="петя", name="Петя", password="длинныйпароль1"))
        self.assertEqual(code, 0, out)
        self.assertIn("Готово", out)
        self.assertTrue(self.login_works("петя", "длинныйпароль1"))
        # чужой пароль по-прежнему не подходит
        self.assertFalse(self.login_works("петя", "неверныйпароль"))

    def test_duplicate_name_refused(self):
        self.run_cmd(self.admin.cmd_add, self.ns(username="петя", name=None,
                                                 password="длинныйпароль1"))
        code, out = self.run_cmd(self.admin.cmd_add, self.ns(
            username="петя", name=None, password="другойпароль1"))
        self.assertEqual(code, 1)
        self.assertIn("уже существует", out)

    def test_short_password_refused(self):
        code, out = self.run_cmd(self.admin.cmd_add, self.ns(
            username="петя", name=None, password="123"))
        self.assertEqual(code, 1)

    def test_bad_username_refused(self):
        code, out = self.run_cmd(self.admin.cmd_add, self.ns(
            username="п е т я!", name=None, password="длинныйпароль1"))
        self.assertEqual(code, 1)

    def test_change_password(self):
        self.run_cmd(self.admin.cmd_add, self.ns(username="петя", name="Петя",
                                                 password="старыйпароль1"))
        code, out = self.run_cmd(self.admin.cmd_passwd, self.ns(
            username="петя", password="новыйпароль22"))
        self.assertEqual(code, 0, out)
        self.assertTrue(self.login_works("петя", "новыйпароль22"))
        self.assertFalse(self.login_works("петя", "старыйпароль1"))

    def test_remove_user(self):
        self.run_cmd(self.admin.cmd_add, self.ns(username="петя", name=None,
                                                 password="длинныйпароль1"))
        code, out = self.run_cmd(self.admin.cmd_remove, self.ns(username="петя",
                                                                yes=True))
        self.assertEqual(code, 0, out)
        self.assertFalse(self.login_works("петя", "длинныйпароль1"))
        self.assertEqual(self.mock.users and len(self.mock.users), 1)  # осталась аня

    def test_check_and_list(self):
        code, out = self.run_cmd(self.admin.cmd_check, self.ns())
        self.assertEqual(code, 0, out)
        self.assertIn("Пользователей: 1", out)
        self.run_cmd(self.admin.cmd_add, self.ns(username="петя", name="Петя",
                                                 password="длинныйпароль1"))
        code, out = self.run_cmd(self.admin.cmd_list, self.ns())
        self.assertEqual(code, 0)
        self.assertIn("петя@golub.local", out)

    def test_wrong_secret_refused(self):
        bad = admin_mod.Admin(self.mock.url, "sb_secret_wrong")
        bad.domain = "golub.local"
        out = io.StringIO()
        with redirect_stdout(out):
            code = bad.cmd_check(self.ns())
        self.assertEqual(code, 1)
        self.assertIn("ключ", out.getvalue().lower())


if __name__ == "__main__":
    unittest.main(verbosity=2)
