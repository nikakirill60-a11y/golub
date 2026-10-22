#!/usr/bin/env python3
"""
Голубь — управление аккаунтами. Только для владельца.

Обычный вход в приложении по имени и паролю, а НОВЫЕ аккаунты может создавать
только этот скрипт (или ты лично в панели Supabase). Регистрация из приложения
выключена — см. supabase/schema.sql и шаг «выключи Allow new users to sign up».

Секретный ключ нужен только здесь, на твоём компьютере. В приложение и в GitHub
он не попадает никогда.

    # 1. возьми секретный ключ: панель Supabase → Settings → API Keys → Secret keys
    export SUPABASE_SECRET_KEY=sb_secret_xxxxxxxx

    # 2. команды
    python3 admin.py check
    python3 admin.py list
    python3 admin.py add петя --name "Петя" --password "длинный-пароль"
    python3 admin.py passwd петя --password "новый-пароль"
    python3 admin.py remove петя
"""
import argparse
import getpass
import json
import os
import re
import sys
import urllib.error
import urllib.request

DEFAULT_URL = "https://uuokiexpadllowhzbhen.supabase.co"
DEFAULT_DOMAIN = "golub.local"
NAME_RE = re.compile(r"^[0-9A-Za-zА-Яа-яЁё_.\-]{2,24}$")


class Admin:
    def __init__(self, url, secret):
        self.url = url.rstrip("/").replace("/rest/v1", "")
        self.secret = secret.strip()

    # ------------------------------------------------------------- запросы
    def call(self, method, path, body=None, ok=(200, 201, 204)):
        data = None if body is None else json.dumps(body, ensure_ascii=False).encode("utf-8")
        req = urllib.request.Request(self.url + path, data=data, method=method)
        req.add_header("apikey", self.secret)
        # для административных запросов ключ передаётся и как Bearer
        req.add_header("Authorization", "Bearer " + self.secret)
        req.add_header("Content-Type", "application/json")
        req.add_header("Accept", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=25) as resp:
                raw = resp.read()
                return resp.status, (json.loads(raw) if raw else None)
        except urllib.error.HTTPError as exc:
            raw = exc.read()
            try:
                payload = json.loads(raw) if raw else {}
            except ValueError:
                payload = {"message": raw.decode("utf-8", "replace")[:300]}
            return exc.code, payload
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            print("Нет связи с Supabase: %s" % exc)
            sys.exit(1)

    def fail(self, status, payload, context=""):
        msg = (payload or {}).get("msg") or (payload or {}).get("message") \
            or (payload or {}).get("error_description") or str(payload)[:300]
        low = msg.lower()
        if "already" in low and ("registered" in low or "exists" in low):
            msg = "такое имя уже занято"
        elif status == 401 or status == 403:
            msg = "ключ не подошёл (нужен СЕКРЕТНЫЙ ключ sb_secret_...), либо он отключён"
        elif "relation" in low and "does not exist" in low:
            msg = ("в базе нет нужных таблиц — сначала выполни supabase/schema.sql "
                   "в SQL Editor панели Supabase")
        print("Ошибка %s%s: %s" % (status, (" (" + context + ")") if context else "", msg))
        return 1

    # ------------------------------------------------------------- команды
    def users(self):
        status, payload = self.call("GET", "/auth/v1/admin/users?page=1&per_page=1000")
        if status != 200:
            self.fail(status, payload, "чтение пользователей")
            return []
        if isinstance(payload, dict):
            return payload.get("users") or []
        return payload or []

    def find(self, username):
        email = "%s@%s" % (username, self.domain)
        for user in self.users():
            if (user.get("email") or "").lower() == email.lower():
                return user
        return None

    def upsert_profile(self, uid, username, display_name):
        status, payload = self.call(
            "POST", "/rest/v1/profiles?on_conflict=id",
            [{"id": uid, "username": username, "display_name": display_name}],
        )
        if status not in (200, 201, 204):
            # не ошибка: профиль обычно создаёт триггер из schema.sql
            print("  (профиль не обновлён автоматически: %s — сделай шаг schema.sql)"
                  % ((payload or {}).get("message") or status))
            return False
        return True

    def cmd_check(self, args):
        status, payload = self.call("GET", "/rest/v1/profiles?select=id&limit=1")
        if status not in (200, 206):
            return self.fail(status, payload, "таблица profiles")
        status2, payload2 = self.call("GET", "/auth/v1/admin/users?page=1&per_page=1")
        if status2 != 200:
            return self.fail(status2, payload2, "админ-доступ к Auth")
        users = self.users()
        print("Подключение в порядке.")
        print("Проект: %s" % self.url)
        print("Пользователей: %d" % len(users))
        if not users:
            print("Создай первого: python3 admin.py add петя --password \"...\"")
        return 0

    def cmd_list(self, args):
        users = self.users()
        if not users:
            print("Пользователей пока нет.")
            return 0
        print("%-20s %-28s %s" % ("ИМЯ", "ПОЧТА (внутренняя)", "СОЗДАН"))
        for user in sorted(users, key=lambda u: (u.get("email") or "")):
            email = user.get("email") or ""
            name = email.split("@")[0]
            print("%-20s %-28s %s" % (name, email, (user.get("created_at") or "")[:10]))
        return 0

    def cmd_add(self, args):
        username = args.username.strip()
        if not NAME_RE.match(username):
            print("Имя: 2–24 символа, буквы/цифры/точка/дефис/подчёркивание.")
            return 1
        password = args.password or _ask_password()
        if password is None:
            return 1
        if len(password) < 8:
            print("Пароль короче 8 символов — слишком слабо для аккаунта.")
            return 1
        display = args.name or username
        email = "%s@%s" % (username, self.domain)

        if self.find(username):
            print("Пользователь «%s» уже существует. Смени пароль: "
                  "python3 admin.py passwd %s" % (username, username))
            return 1

        status, payload = self.call("POST", "/auth/v1/admin/users", {
            "email": email,
            "password": password,
            "email_confirm": True,          # почта не нужна, вход по имени
            "user_metadata": {"username": username, "display_name": display},
        })
        if status not in (200, 201):
            return self.fail(status, payload, "создание пользователя")

        uid = (payload or {}).get("id")
        ok = self.upsert_profile(uid, username, display)
        print("Готово: создан аккаунт «%s»." % username)
        print("  вход в приложении: имя «%s» и пароль, который ты задал" % username)
        if not ok:
            print("  профиль появится, когда выполнишь supabase/schema.sql")
        return 0

    def cmd_passwd(self, args):
        user = self.find(args.username)
        if not user:
            print("Нет такого пользователя: %s" % args.username)
            return 1
        password = args.password or _ask_password()
        if password is None:
            return 1
        if len(password) < 8:
            print("Пароль короче 8 символов — слишком слабо для аккаунта.")
            return 1
        status, payload = self.call("PUT", "/auth/v1/admin/users/" + user["id"],
                                    {"password": password})
        if status not in (200, 201):
            return self.fail(status, payload, "смена пароля")
        print("Пароль для «%s» изменён." % args.username)
        return 0

    def cmd_remove(self, args):
        user = self.find(args.username)
        if not user:
            print("Нет такого пользователя: %s" % args.username)
            return 1
        if not args.yes:
            answer = input("Удалить «%s» вместе с его перепиской? [да/нет] " % args.username)
            if answer.strip().lower() not in ("да", "д", "yes", "y"):
                print("Отменено.")
                return 0
        status, payload = self.call("DELETE", "/auth/v1/admin/users/" + user["id"])
        if status not in (200, 204):
            return self.fail(status, payload, "удаление")
        print("Пользователь «%s» удалён (сообщения удалятся каскадом)." % args.username)
        return 0


def _ask_password():
    first = getpass.getpass("Пароль (ввод не отображается): ")
    if len(first) < 8:
        print("Пароль короче 8 символов — слишком слабо для аккаунта.")
        return None
    second = getpass.getpass("Повтори пароль: ")
    if first != second:
        print("Пароли не совпали.")
        return None
    return first


def main():
    parser = argparse.ArgumentParser(description="Управление аккаунтами «Голубя»")
    parser.add_argument("--url", default=os.environ.get("GOLUB_SUPABASE_URL", DEFAULT_URL))
    parser.add_argument("--domain", default=os.environ.get("GOLUB_EMAIL_DOMAIN", DEFAULT_DOMAIN))
    sub = parser.add_subparsers(dest="cmd", required=True)

    sub.add_parser("check", help="проверить подключение и схему")
    sub.add_parser("list", help="список аккаунтов")

    add = sub.add_parser("add", help="создать аккаунт")
    add.add_argument("username")
    add.add_argument("--name", help="как показывать в чатах (по умолчанию = имя входа)")
    add.add_argument("--password", help="пароль (лучше не писать в командной строке)")

    passwd = sub.add_parser("passwd", help="сменить пароль")
    passwd.add_argument("username")
    passwd.add_argument("--password")

    remove = sub.add_parser("remove", help="удалить аккаунт")
    remove.add_argument("username")
    remove.add_argument("-y", "--yes", action="store_true")

    args = parser.parse_args()
    secret = os.environ.get("SUPABASE_SECRET_KEY", "").strip()
    if not secret:
        print("Нужен секретный ключ проекта.\n"
              "  1) панель Supabase → Settings → API Keys → Secret keys → скопировать sb_secret_...\n"
              "  2) export SUPABASE_SECRET_KEY=sb_secret_...\n"
              "Ключ используется только здесь и никуда не сохраняется.")
        return 2
    if not secret.startswith(("sb_secret_", "eyJ")):
        print("Это не похоже на секретный ключ (нужен sb_secret_...,\n"
              "а не публичный sb_publishable_...).")
        return 2

    admin = Admin(args.url, secret)
    admin.domain = args.domain
    return {
        "check": admin.cmd_check,
        "list": admin.cmd_list,
        "add": admin.cmd_add,
        "passwd": admin.cmd_passwd,
        "remove": admin.cmd_remove,
    }[args.cmd](args) or 0


if __name__ == "__main__":
    sys.exit(main())
