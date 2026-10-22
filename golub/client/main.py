#!/usr/bin/env python3
"""
Голубь — мобильный клиент мессенджера (Kivy, Android + ПК).

Интерфейс намеренно простой: список чатов, переписка, всё на русском.
Вся «магия» плохого интернета — в core.py: этот файл только рисует.

Запуск на компьютере (для проверки):
    cd client && python3 main.py
"""
import os
import sys
import time

os.environ.setdefault("KIVY_NO_ARGS", "1")

from kivy.config import Config
Config.set("graphics", "width", "420")
Config.set("graphics", "height", "820")
Config.set("graphics", "resizable", "1")
Config.set("input", "mouse", "mouse,multitouch_on_demand")

from kivy.app import App
from kivy.clock import Clock
from kivy.core.window import Window
from kivy.graphics import Color, Ellipse, RoundedRectangle
from kivy.metrics import dp, sp
from kivy.properties import ListProperty, NumericProperty, StringProperty
from kivy.uix.behaviors import ButtonBehavior
from kivy.uix.boxlayout import BoxLayout
from kivy.uix.label import Label
from kivy.uix.screenmanager import Screen, ScreenManager, SlideTransition
from kivy.uix.scrollview import ScrollView
from kivy.uix.textinput import TextInput
from kivy.uix.widget import Widget
from kivy.utils import get_color_from_hex as C

from storage import make_storage
from supabase_core import SupabaseCore
from supabase_settings import PROJECT_URL, PUBLISHABLE_KEY

# ---------------------------------------------------------------- палитра
BG = C("#0d1118")
CARD = C("#161c28")
CARD_HI = C("#1e2636")
MINE = C("#3f6cf4")
THEIRS = C("#222b3c")
TEXT = C("#e9eef7")
MUTED = C("#8592a8")
ACCENT = C("#5b8cff")
GREEN = C("#2fd07a")
AMBER = C("#ffb020")
RED = C("#ff5d5d")
WHITE = [1, 1, 1, 1]


def fmt_time(ts):
    t = time.localtime(ts)
    lt = time.localtime()
    hm = time.strftime("%H:%M", t)
    if (t.tm_year, t.tm_yday) == (lt.tm_year, lt.tm_yday):
        return hm
    if t.tm_year == lt.tm_year and lt.tm_yday - t.tm_yday == 1:
        return "вчера"
    return time.strftime("%d.%m", t)


def fmt_msg_time(ts):
    t = time.localtime(ts)
    lt = time.localtime()
    hm = time.strftime("%H:%M", t)
    if (t.tm_year, t.tm_yday) == (lt.tm_year, lt.tm_yday):
        return hm
    return time.strftime("%d.%m %H:%M", t)


# ---------------------------------------------------------------- виджеты
class Panel(BoxLayout):
    """Прямоугольник со скруглёнными углами."""
    bg = ListProperty(list(CARD))
    radius = NumericProperty(16)

    def __init__(self, **kw):
        super().__init__(**kw)
        with self.canvas.before:
            self._color = Color(rgba=self.bg)
            self._rect = RoundedRectangle(radius=[dp(self.radius)])
        self.bind(pos=self._sync, size=self._sync, bg=self._sync_color)
        self._sync()

    def _sync(self, *args):
        self._rect.pos = self.pos
        self._rect.size = self.size
        self._rect.radius = [dp(self.radius)]

    def _sync_color(self, *args):
        self._color.rgba = self.bg


class Btn(ButtonBehavior, Panel):
    def __init__(self, text="", cb=None, bg=None, fg=None, font_size=16,
                 bold=True, **kw):
        kw.setdefault("size_hint_y", None)
        kw.setdefault("height", dp(50))
        super().__init__(bg=bg or ACCENT, **kw)
        self.cb = cb
        self.label = Label(text=text, color=fg or WHITE, bold=bold,
                           font_size=sp(font_size))
        self.add_widget(self.label)
        self.bind(state=self._on_state)

    def _on_state(self, *_):
        self.opacity = 0.75 if self.state == "down" else 1.0

    def on_release(self):
        if self.cb:
            self.cb()


class Avatar(Widget):
    """Кружок с первой буквой имени."""
    letter = StringProperty("?")

    def __init__(self, letter="?", color=None, size=44, **kw):
        super().__init__(size_hint=(None, None), size=(dp(size), dp(size)), **kw)
        self.letter = letter
        self.base = color or ACCENT
        with self.canvas:
            self._bg = Color(rgba=self.base)
            self._cir = Ellipse()
            self._fg = Color(rgba=WHITE)
            self._txt = None
        self._label = Label(text=self.letter, bold=True, color=WHITE,
                            font_size=sp(size * 0.42), size=self.size, pos=self.pos)
        self.add_widget(self._label)
        self.bind(pos=self._sync, size=self._sync,
                  letter=lambda _w, val: setattr(self._label, "text", val))

    def _sync(self, *args):
        self._cir.pos = self.pos
        self._cir.size = self.size
        self._label.pos = self.pos
        self._label.size = self.size


class Dot(Widget):
    def __init__(self, color=GREEN, size=9, **kw):
        super().__init__(size_hint=(None, None), size=(dp(size), dp(size)), **kw)
        with self.canvas:
            self._c = Color(rgba=color)
            self._e = Ellipse()
        self.bind(pos=self._sync, size=self._sync)

    def _sync(self, *args):
        self._e.pos = self.pos
        self._e.size = self.size

    def set_color(self, color):
        self._c.rgba = color


class Input(TextInput):
    def __init__(self, **kw):
        super().__init__(**kw)
        self.background_normal = ""
        self.background_active = ""
        self.background_color = (0, 0, 0, 0)
        self.foreground_color = TEXT
        self.cursor_color = ACCENT
        self.hint_text_color = list(MUTED)
        self.selection_color = list(ACCENT)
        self.font_size = sp(15)
        self.multiline = False
        self.write_tab = False
        self.padding = [dp(14), dp(14), dp(14), dp(12)]


class Field(Panel):
    """Поле ввода в «карточке»."""
    def __init__(self, hint="", password=False, **kw):
        kw.setdefault("size_hint_y", None)
        kw.setdefault("height", dp(52))
        super().__init__(bg=CARD_HI, radius=14, padding=[dp(2), dp(2)], **kw)
        self.input = Input(hint_text=hint, password=password)
        self.add_widget(self.input)


class ChatRow(ButtonBehavior, Panel):
    def __init__(self, chat, on_open, **kw):
        kw.setdefault("size_hint_y", None)
        kw.setdefault("height", dp(72))
        super().__init__(bg=CARD, radius=16, padding=[dp(12), dp(10)], **kw)
        self.peer = chat["peer"]
        self.on_open = on_open
        display = chat.get("display") or chat["peer"]
        letters = display[:1].upper()
        color = ACCENT
        row = BoxLayout(spacing=dp(12))
        row.add_widget(Avatar(letters, color=color, size=46))
        middle = BoxLayout(orientation="vertical", spacing=dp(2))
        name_row = BoxLayout(size_hint_y=None, height=dp(24), spacing=dp(6))
        name = Label(text=display, color=TEXT, bold=True, font_size=sp(16),
                     halign="left", valign="middle", size_hint_x=1, shorten=True,
                     shorten_from="right")
        name.bind(size=lambda w, *_: setattr(w, "text_size", (w.width, w.height)))
        name_row.add_widget(name)
        if chat["online"]:
            name_row.add_widget(Dot(GREEN, 8))
        middle.add_widget(name_row)
        preview = chat["last"].replace("\n", " ")
        preview = ("Вы: " + preview) if chat["last_mine"] else preview
        if chat["pending"]:
            preview = "• " + preview
        pv = Label(text=preview, color=MUTED, font_size=sp(13), halign="left",
                   valign="middle", size_hint_y=None, height=dp(22),
                   shorten=True, shorten_from="right")
        pv.bind(size=lambda w, *_: setattr(w, "text_size", (w.width, w.height)))
        middle.add_widget(pv)
        row.add_widget(middle)
        right = BoxLayout(orientation="vertical", size_hint_x=None, width=dp(46),
                          spacing=dp(6))
        tl = Label(text=fmt_time(chat["last_ts"]), color=MUTED, font_size=sp(11),
                   size_hint_y=None, height=dp(18), halign="right")
        tl.bind(size=lambda w, *_: setattr(w, "text_size", (w.width, w.height)))
        right.add_widget(tl)
        if chat["unread"] > 0:
            badge = Panel(bg=ACCENT, radius=12, size_hint=(None, dp(22)),
                          width=dp(24), pos_hint={"right": 1})
            badge.add_widget(Label(text=str(min(chat["unread"], 99)),
                                   color=WHITE, font_size=sp(11), bold=True))
            right.add_widget(badge)
        row.add_widget(right)
        self.add_widget(row)
        self.bind(state=self._on_state)

    def _on_state(self, *_):
        self.bg = CARD_HI if self.state == "down" else CARD

    def on_release(self):
        self.on_open(self.peer)


class Bubble(Panel):
    """Облачко сообщения."""
    def __init__(self, msg, mine, max_width, status_text=None, **kw):
        color = MINE if mine else THEIRS
        super().__init__(bg=color, radius=18, orientation="vertical",
                         size_hint=(None, None), padding=[dp(12), dp(8)], spacing=dp(2),
                         **kw)
        body = Label(text=msg["body"], color=WHITE if mine else TEXT,
                     font_size=sp(15), size_hint=(None, None),
                     halign="left", valign="top")
        body.text_size = (max_width - dp(26), None)
        body.texture_update()
        body.size = body.texture_size
        self.add_widget(body)

        meta = fmt_msg_time(msg["ts"])
        if status_text:
            meta += "  " + status_text
        meta_label = Label(text=meta, color=(1, 1, 1, 0.62) if mine else list(MUTED),
                           font_size=sp(10), size_hint=(None, None),
                           halign="right", valign="middle")
        meta_label.text_size = (max_width - dp(26), None)
        meta_label.texture_update()
        meta_label.size = meta_label.texture_size
        self.add_widget(meta_label)

        width = max(body.width, meta_label.width) + dp(26)
        self.size = (min(width, max_width), body.height + meta_label.height + dp(22))


class MessagesArea(ScrollView):
    def __init__(self, **kw):
        super().__init__(**kw)
        self.do_scroll_x = False
        self.bar_width = dp(2)
        self.box = BoxLayout(orientation="vertical", spacing=dp(6),
                             size_hint_y=None, padding=[dp(10), dp(10)])
        self.box.bind(minimum_height=self.box.setter("height"))
        self.add_widget(self.box)


# ---------------------------------------------------------------- экраны
class LoginScreen(Screen):
    def __init__(self, app, **kw):
        super().__init__(**kw)
        self.app = app
        self._adv_open = False

        root = BoxLayout(orientation="vertical", padding=[dp(24), dp(36)], spacing=dp(12))
        root.add_widget(Widget(size_hint_y=0.45))
        root.add_widget(Label(text="Голубь", color=TEXT, bold=True, font_size=sp(38),
                              size_hint_y=None, height=dp(52)))
        root.add_widget(Label(text="работает даже при плохом интернете",
                              color=MUTED, font_size=sp(14), size_hint_y=None,
                              height=dp(26)))
        root.add_widget(Widget(size_hint_y=None, height=dp(8)))

        saved = app.meta.get("account", {}) or {}
        conn = app.meta.get("connection", {}) or {}

        self.f_name = Field(hint="Имя")
        self.f_name.input.text = saved.get("name", "")
        self.f_password = Field(hint="Пароль", password=True)
        self.f_password.input.text = saved.get("password", "")
        root.add_widget(self.f_name)
        root.add_widget(self.f_password)

        self.settings_btn = Btn(text="Настройки подключения", cb=self.toggle_settings,
                                bg=CARD, fg=MUTED, height=dp(40), font_size=13)
        root.add_widget(self.settings_btn)

        self.adv = BoxLayout(orientation="vertical", spacing=dp(10),
                             size_hint_y=None, height=0, opacity=0)
        self.f_url = Field(hint="Адрес проекта: https://xxxx.supabase.co")
        self.f_url.input.text = conn.get("url") or PROJECT_URL
        self.f_key = Field(hint="Публичный ключ sb_publishable_...")
        self.f_key.input.text = conn.get("key") or PUBLISHABLE_KEY
        self.adv.add_widget(self.f_url)
        self.adv.add_widget(self.f_key)
        root.add_widget(self.adv)

        self.error = Label(text="", color=RED, font_size=sp(12.5),
                           size_hint_y=None, height=dp(40), halign="center",
                           valign="middle", text_size=(dp(320), None))
        root.add_widget(self.error)

        self.button = Btn(text="Войти", cb=self.do_login, height=dp(54))
        root.add_widget(self.button)

        hint = Label(text="Аккаунты создаёт только владелец.\n"
                          "Если тебя ещё нет в списке — попроси его добавить.",
                     color=MUTED, font_size=sp(12), size_hint_y=None, height=dp(44),
                     halign="center", valign="middle", text_size=(dp(320), None))
        root.add_widget(hint)
        root.add_widget(Widget(size_hint_y=1.0))
        self.add_widget(root)

    def toggle_settings(self, *_):
        self._adv_open = not self._adv_open
        self.adv.height = dp(114) if self._adv_open else 0
        self.adv.opacity = 1 if self._adv_open else 0

    def do_login(self, *_):
        name = self.f_name.input.text.strip()
        password = self.f_password.input.text
        url = (self.f_url.input.text.strip() or PROJECT_URL).rstrip("/")
        key = self.f_key.input.text.strip() or PUBLISHABLE_KEY
        if len(name) < 2:
            self.error.text = "Введи имя (как тебе его выдал владелец)"
            return
        if len(password) < 6:
            self.error.text = "Пароль слишком короткий"
            return
        if not url.startswith("https://"):
            self.error.text = "Адрес проекта должен начинаться с https://"
            return
        if not key.startswith(("sb_publishable_", "eyJ")):
            self.error.text = "Не похоже на публичный ключ проекта"
            return
        self.error.text = ""
        self.button.label.text = "Подключаюсь..."
        self.app.login(url, key, name, password)


class ChatsScreen(Screen):
    def __init__(self, app, **kw):
        super().__init__(**kw)
        self.app = app
        self._signature = None
        root = BoxLayout(orientation="vertical")

        header = BoxLayout(size_hint_y=None, height=dp(88),
                           padding=[dp(16), dp(24), dp(16), dp(6)],
                           spacing=dp(10))
        titles = BoxLayout(orientation="vertical", spacing=dp(1))
        self.title = Label(text="Голубь", color=TEXT, bold=True, font_size=sp(22),
                           halign="left", valign="middle", size_hint_y=None, height=dp(32))
        self.title.bind(size=lambda w, *_: setattr(w, "text_size", (w.width, w.height)))
        status_row = BoxLayout(size_hint_y=None, height=dp(20), spacing=dp(6))
        self.dot = Dot(AMBER, 8)
        self.status = Label(text="подключение…", color=MUTED, font_size=sp(12),
                            halign="left", valign="middle")
        self.status.bind(size=lambda w, *_: setattr(w, "text_size", (w.width, w.height)))
        status_row.add_widget(self.dot)
        status_row.add_widget(self.status)
        status_row.add_widget(Widget())
        titles.add_widget(self.title)
        titles.add_widget(status_row)
        header.add_widget(titles)
        self.logout = Btn(text="Выход", cb=self.app.logout, bg=CARD, fg=MUTED,
                          size_hint=(None, None), width=dp(78), height=dp(38),
                          font_size=13)
        header.add_widget(self.logout)
        root.add_widget(header)

        self.scroll = ScrollView(do_scroll_x=False, bar_width=dp(2))
        self.list = BoxLayout(orientation="vertical", spacing=dp(8),
                              size_hint_y=None, padding=[dp(12), dp(4), dp(12), dp(90)])
        self.list.bind(minimum_height=self.list.setter("height"))
        self.scroll.add_widget(self.list)
        root.add_widget(self.scroll)

        self.new_chat_btn = Btn(text="+  Новый чат", cb=self.app.open_users, height=50)
        holder = BoxLayout(size_hint_y=None, height=dp(72),
                           padding=[dp(12), dp(10), dp(12), dp(12)])
        holder.add_widget(self.new_chat_btn)
        root.add_widget(holder)
        self.add_widget(root)

    def update(self, snap):
        state = snap["status"]
        if state == "online":
            self.dot.set_color(GREEN)
            text = "онлайн"
            if snap["outbox"]:
                text += " · %d в очереди" % snap["outbox"]
        elif state == "connecting":
            self.dot.set_color(AMBER)
            text = "подключаюсь…"
            if snap["outbox"]:
                text += " · %d в очереди" % snap["outbox"]
        else:
            self.dot.set_color(RED)
            hint = self._hint_text(snap.get("error") or "")
            if hint:
                text = hint
            else:
                text = "нет сети — сообщения уйдут сами"
            if snap["outbox"]:
                text += " (%d в очереди)" % snap["outbox"]
        self.status.text = text

    @staticmethod
    def _hint_text(error):
        """Понятная подсказка вместо технической ошибки."""
        low = error.lower()
        if "schema cache" in low or "pgrst205" in low or "does not exist" in low:
            return "в базе нет таблиц — выполни supabase/schema.sql"
        if "401" in low or "unauthorized" in low or "apikey" in low:
            return "ключ не подошёл — проверь настройки подключения"
        if "неверн" in low or "invalid login" in low:
            return "неверное имя или пароль"
        return ""

        chats = snap["chats"]
        # новые собеседники из списка людей, с которыми ещё нет переписки
        sig = tuple((c["peer"], c["last"], c["last_ts"], c["unread"],
                     c["online"], c["pending"]) for c in chats)
        if sig == self._signature:
            return
        self._signature = sig
        self.list.clear_widgets()
        if not chats:
            self.list.add_widget(Label(
                text="Пока пусто.\nНажми «Новый чат» и напиши кому-нибудь.",
                color=MUTED, font_size=sp(14), halign="center", valign="middle",
                size_hint_y=None, height=dp(120), text_size=(dp(300), None)))
            return
        for chat in chats:
            self.list.add_widget(ChatRow(chat, self.app.open_chat))


class ChatScreen(Screen):
    def __init__(self, app, **kw):
        super().__init__(**kw)
        self.app = app
        self.peer = None
        self._sig = None
        self._peer_read = 0
        root = BoxLayout(orientation="vertical")

        header = BoxLayout(size_hint_y=None, height=dp(80),
                           padding=[dp(8), dp(24), dp(8), dp(8)],
                           spacing=dp(8), size_hint_x=1)
        back = Btn(text="<", cb=self.app.back_to_chats, bg=CARD, fg=TEXT,
                   size_hint=(None, None), width=dp(44), height=dp(44), font_size=14)
        header.add_widget(back)
        self.avatar = Avatar("?", size=38)
        header.add_widget(self.avatar)
        names = BoxLayout(orientation="vertical", spacing=0)
        self.peer_name = Label(text="", color=TEXT, bold=True, font_size=sp(16),
                               halign="left", valign="middle", size_hint_y=None, height=dp(26))
        self.peer_name.bind(size=lambda w, *_: setattr(w, "text_size", (w.width, w.height)))
        self.sub = Label(text="", color=MUTED, font_size=sp(11),
                         halign="left", valign="middle", size_hint_y=None, height=dp(18))
        self.sub.bind(size=lambda w, *_: setattr(w, "text_size", (w.width, w.height)))
        names.add_widget(self.peer_name)
        names.add_widget(self.sub)
        header.add_widget(names)
        root.add_widget(header)

        self.messages = MessagesArea()
        root.add_widget(self.messages)

        self.queue_label = Label(text="", color=AMBER, font_size=sp(11),
                                 size_hint_y=None, height=dp(0), halign="center")
        self.queue_label.bind(size=lambda w, *_: setattr(w, "text_size", (w.width, w.height)))
        root.add_widget(self.queue_label)

        composer = BoxLayout(size_hint_y=None, height=dp(64),
                             padding=[dp(10), dp(8), dp(10), dp(8)], spacing=dp(8))
        field = Panel(bg=CARD_HI, radius=20, padding=[dp(2), dp(2)])
        self.input = Input(hint_text="Сообщение…")
        self.input.bind(on_text_validate=lambda *_: self.send())
        field.add_widget(self.input)
        composer.add_widget(field)
        self.send_btn = Btn(text=">", cb=self.send, bg=ACCENT,
                            size_hint=(None, None), width=dp(48), height=dp(48),
                            radius=24, font_size=20)
        composer.add_widget(self.send_btn)
        root.add_widget(composer)
        self.add_widget(root)

    # ------------------------------------------------------------------
    def set_peer(self, peer):
        self.peer = peer
        self._sig = None
        self._peer_read = 0
        self.avatar.letter = peer[:1].upper()
        self.peer_name.text = peer
        self.messages.box.clear_widgets()
        self.input.text = ""

    def on_peer_read(self, upto):
        self._peer_read = int(upto or 0)
        self._sig = None  # перерисовать галочки

    def send(self):
        text = self.input.text.strip()
        if not text or not self.peer:
            return
        self.input.text = ""
        self.app.core.send_text(self.peer, text)
        Clock.schedule_once(lambda *_: self.scroll_to_bottom(), 0.05)

    def scroll_to_bottom(self):
        self.messages.scroll_y = 0.0

    def update(self, snap):
        if not self.peer:
            return
        display = snap.get("active_display") or self.peer
        self.peer_name.text = display
        self.avatar.letter = display[:1].upper()
        self.sub.text = "онлайн" if snap.get("active_online") else "не в сети"
        if snap["outbox"]:
            self.queue_label.height = dp(18)
            self.queue_label.text = "В очереди: %d — отправятся сами" % snap["outbox"]
        else:
            self.queue_label.height = 0
            self.queue_label.text = ""

        msgs = snap["messages"]
        sig = tuple((m["mid"], m.get("seq"), m["status"]) for m in msgs) + (self._peer_read,)
        if sig == self._sig:
            return
        self._sig = sig

        old_count = len(self.messages.box.children)
        at_bottom = self.messages.scroll_y <= 0.06
        self.messages.box.clear_widgets()
        max_w = Window.width * 0.78
        for m in msgs:
            mine = m["sender"] != self.peer
            status_text = None
            if mine:
                if m["status"] == "pending":
                    status_text = "в очереди"
                elif m["status"] == "error":
                    status_text = "не доставлено"
                elif m.get("seq") and self._peer_read >= (m.get("seq") or 0):
                    status_text = "прочитано"
                else:
                    status_text = "отправлено"
            row = BoxLayout(size_hint_y=None, spacing=0)
            bubble = Bubble(m, mine, max_w, status_text)
            row.height = bubble.height
            if mine:
                row.add_widget(Widget())
                row.add_widget(bubble)
            else:
                row.add_widget(bubble)
                row.add_widget(Widget())
            self.messages.box.add_widget(row)

        new_count = len(msgs)
        if at_bottom or new_count != old_count:
            Clock.schedule_once(lambda *_: self.scroll_to_bottom(), 0.05)


class UsersScreen(Screen):
    def __init__(self, app, **kw):
        super().__init__(**kw)
        self.app = app
        self._sig = None
        root = BoxLayout(orientation="vertical")
        header = BoxLayout(size_hint_y=None, height=dp(80),
                           padding=[dp(8), dp(24), dp(8), dp(8)],
                           spacing=dp(8))
        back = Btn(text="<", cb=lambda: app.show("chats"), bg=CARD, fg=TEXT,
                   size_hint=(None, None), width=dp(44), height=dp(44), font_size=14)
        header.add_widget(back)
        header.add_widget(Label(text="Кому написать?", color=TEXT, bold=True,
                                font_size=sp(18), halign="left", valign="middle"))
        root.add_widget(header)
        self.scroll = ScrollView(do_scroll_x=False, bar_width=dp(2))
        self.list = BoxLayout(orientation="vertical", spacing=dp(8),
                              size_hint_y=None, padding=[dp(12), dp(4), dp(12), dp(20)])
        self.list.bind(minimum_height=self.list.setter("height"))
        self.scroll.add_widget(self.list)
        root.add_widget(self.scroll)
        self.empty = Label(text="Список появится, когда будет связь с сервером",
                           color=MUTED, font_size=sp(13), size_hint_y=None, height=dp(40))
        root.add_widget(self.empty)
        self.add_widget(root)

    def update(self, snap):
        users = [u for u in snap["users"] if u["name"] != snap["name"]]
        sig = tuple((u["name"], u["online"]) for u in users)
        if sig == self._sig:
            return
        self._sig = sig
        self.empty.text = "" if users else "Список появится, когда будет связь с сервером"
        self.list.clear_widgets()
        for u in users:
            bg = CARD_HI if u["online"] else CARD
            row = Btn(text="", cb=lambda name=u["name"]: self.app.open_chat(name),
                      bg=bg, height=64)
            row.clear_widgets()
            inner = BoxLayout(spacing=dp(12), padding=[dp(10), dp(8)])
            display = u.get("display") or u["name"]
            inner.add_widget(Avatar(display[:1].upper(), size=42))
            labels = BoxLayout(orientation="vertical")
            nm = Label(text=display, color=TEXT, bold=True, font_size=sp(15),
                       halign="left", valign="middle", size_hint_y=None, height=dp(24))
            nm.bind(size=lambda w, *_: setattr(w, "text_size", (w.width, w.height)))
            st = Label(text="онлайн" if u["online"] else "не в сети",
                       color=GREEN if u["online"] else MUTED, font_size=sp(11),
                       halign="left", valign="middle", size_hint_y=None, height=dp(18))
            st.bind(size=lambda w, *_: setattr(w, "text_size", (w.width, w.height)))
            labels.add_widget(nm)
            labels.add_widget(st)
            inner.add_widget(labels)
            row.add_widget(inner)
            self.list.add_widget(row)


# ---------------------------------------------------------------- приложение
class Application(App):
    title = "Голубь"

    def __init__(self, **kw):
        super().__init__(**kw)
        self.core = None
        self.meta = {}
        self.storage = None
        self.paused = False
        self._started_at = time.time()

    # ---------------------------------------------------------------- старт
    def build(self):
        Window.softinput_mode = "below_target"
        Window.clearcolor = BG
        self._db_path = os.environ.get("GOLUB_DB") or os.path.join(
            self.user_data_dir, "golub.db")
        self.raw = make_storage(self._db_path)
        self.meta = self.raw.get_json("account", {}) or {}

        self.sm = ScreenManager(transition=SlideTransition(duration=0.12))
        self.login_screen = LoginScreen(self, name="login")
        self.chats_screen = ChatsScreen(self, name="chats")
        self.chat_screen = ChatScreen(self, name="chat")
        self.users_screen = UsersScreen(self, name="users")
        for s in (self.login_screen, self.chats_screen, self.chat_screen,
                  self.users_screen):
            self.sm.add_widget(s)

        saved = self.meta.get("account") or {}
        conn = self.meta.get("connection") or {}
        if saved.get("name") and saved.get("password"):
            self._start_core(conn.get("url") or PROJECT_URL,
                             conn.get("key") or PUBLISHABLE_KEY,
                             saved["name"], saved["password"])
            self.sm.current = "chats"
        else:
            self.sm.current = "login"

        Clock.schedule_interval(self.tick, 0.5)
        Window.bind(on_keyboard=self.on_key)
        return self.sm

    # ---------------------------------------------------------------- логика
    def login(self, url, key, name, password):
        self.meta["account"] = {"name": name, "password": password}
        self.meta["connection"] = {"url": url, "key": key}
        self.raw.set_json("account", self.meta["account"])
        self.raw.set_json("connection", self.meta["connection"])
        self._start_core(url, key, name, password)
        self.show("chats")

    def _start_core(self, url, key, name, password):
        if self.core:
            self.core.stop()
        if getattr(self, "storage", None) is not None:
            try:
                self.storage.close()
            except Exception:
                pass
        safe = "".join(c if c.isalnum() else "_" for c in name)[:32]
        base = os.path.dirname(self._db_path)
        self.storage = make_storage(os.path.join(base, "chats_%s.db" % safe))
        self.core = SupabaseCore(
            storage=self.storage,
            url=url,
            key=key,
            username=name,
            password=password,
            listener=self.on_core_event,
        )
        self.core.start()

    def logout(self):
        if self.core:
            self.core.stop()
            self.core = None
        if getattr(self, "storage", None) is not None:
            try:
                self.storage.close()
            except Exception:
                pass
            self.storage = None
        self.meta.pop("account", None)
        self.raw.set_json("account", {})
        self.login_screen.error.text = ""
        self.login_screen.button.label.text = "Войти"
        self.sm.current = "login"

    def show(self, name):
        if self.sm.current != name:
            self.sm.current = name

    def open_chat(self, peer):
        if not self.core:
            return
        self.chat_screen.set_peer(peer)
        self.core.open_chat(peer)
        self.show("chat")

    def open_users(self):
        self.show("users")
        if self.core:
            self.core.refresh_users()

    def back_to_chats(self):
        if self.core and self.chat_screen.peer:
            self.core.close_chat(self.chat_screen.peer)
        self.show("chats")

    # ------------------------------------------------------- уведомления
    def _notify(self, peer, body):
        try:
            from plyer import notification
            notification.notify(
                title="Голубь · %s" % peer,
                message=body[:120],
                app_name="Голубь",
                timeout=5,
            )
        except Exception:
            pass  # на ПК без plyer просто тихо пропускаем

    # ---------------------------------------------------------------- события
    def on_core_event(self, kind, payload):
        Clock.schedule_once(lambda *_: self._on_event_ui(kind, payload), 0)

    def _on_event_ui(self, kind, payload):
        if kind == "auth_error":
            self.login_screen.error.text = (
                "Неверное имя или пароль.\n"
                "Аккаунт создаёт только владелец — попроси его добавить тебя.")
            self.login_screen.button.label.text = "Войти"
            if self.core:
                self.core.stop()
                self.core = None
            self.meta.pop("account", None)
            self.raw.set_json("account", {})
            self.sm.current = "login"
        elif kind == "peer_read" and payload.get("peer") == self.chat_screen.peer:
            self.chat_screen.on_peer_read(payload.get("upto"))
        elif kind == "messages" and self.core:
            peer = payload.get("peer")
            shown = self.core.snapshot(peer)["messages"]
            if not shown:
                return
            last = shown[-1]
            if last["sender"] == self.core.username:
                return
            if last["ts"] < self._started_at - 5:
                return  # это докачка старой истории, не беспокоим
            if self.paused or self.sm.current != "chat" or self.chat_screen.peer != peer:
                display = self.core.snapshot(peer).get("active_display") or peer
                self._notify(display, last["body"])

    def tick(self, _dt):
        if not self.core:
            return
        snap = self.core.snapshot()
        self.chats_screen.update(snap)
        self.users_screen.update(snap)
        if self.sm.current == "chat":
            self.chat_screen.update(snap)

    def on_key(self, _window, key, *_):
        if key == 27:  # Esc / кнопка «назад» на Android
            if self.sm.current in ("chat", "users"):
                if self.sm.current == "chat":
                    self.back_to_chats()
                else:
                    self.show("chats")
                return True
        return False

    def on_pause(self):
        # Android: не рвать соединение при сворачивании, насколько позволит система
        self.paused = True
        return True

    def on_resume(self):
        self.paused = False
        if self.core:
            self.core.kick()
        return True

    def on_stop(self):
        if self.core:
            self.core.stop(timeout=3)
        if getattr(self, "storage", None) is not None:
            try:
                self.storage.close()
            except Exception:
                pass


if __name__ == "__main__":
    Application().run()
