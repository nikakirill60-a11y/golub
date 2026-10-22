[app]

# Название на экране телефона
title = Голубь

# Идентификатор пакета: org.golub.golub
package.name = golub
package.domain = org.golub

source.dir = .
source.include_exts = py,png,jpg,kv,atlas,ttf,json

version = 1.0.0

# Сеть — стандартная библиотека Python (urllib), поэтому зависимостей мало.
# plyer — уведомления Android, openssl/certifi — HTTPS до Supabase.
requirements = python3,kivy==2.3.1,plyer,openssl,certifi

orientation = portrait
fullscreen = 0
icon.filename = %(source.dir)s/assets/icon.png
android.presplash_color = #0d1118

# Разрешения: интернет (Supabase), состояние сети, уведомления, вибрация
android.permissions = INTERNET,ACCESS_NETWORK_STATE,POST_NOTIFICATIONS,VIBRATE,WAKE_LOCK

android.api = 33
android.minapi = 23
android.ndk_api = 23
android.archs = arm64-v8a, armeabi-v7a
android.accept_sdk_license = True
android.private_storage = True

# p4a по умолчанию собирает свежую стабильную ветку — оставляем как есть

[buildozer]
log_level = 2
warn_on_root = 1
