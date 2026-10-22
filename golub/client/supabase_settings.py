"""Настройки подключения к Supabase.

Публичный (publishable) ключ специально предназначен для приложений — его
безопасно хранить в коде: доступ к данным закрывают правила RLS из schema.sql.
СЕКРЕТНЫЙ ключ (sb_secret_...) сюда класть нельзя ни при каких условиях.
"""
import os

# Проект: uuokiexpadllowhzbhen
PROJECT_URL = os.environ.get(
    "GOLUB_SUPABASE_URL", "https://uuokiexpadllowhzbhen.supabase.co"
).rstrip("/")

PUBLISHABLE_KEY = os.environ.get(
    "GOLUB_SUPABASE_KEY", "sb_publishable_vN-PMLzYyyle0_PxyWxHew_-RxytBoq"
)

# Имя для входа «петя» превращается в почту «петя@golub.local» — так Supabase
# Auth хранит пользователей. Домен должен совпадать с тем, что в admin.py.
EMAIL_DOMAIN = os.environ.get("GOLUB_EMAIL_DOMAIN", "golub.local")


def email_for(username: str) -> str:
    return "%s@%s" % (username.strip(), EMAIL_DOMAIN)
