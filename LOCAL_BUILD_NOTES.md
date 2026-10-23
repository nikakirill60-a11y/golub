# Локальная сборка APK в песочнице Arena — протокол проверки (2026-10-23)

Задача: собрать `golub-1.0.0-*-debug.apk` через buildozer/python-for-android
внутри песочницы, без GitHub Actions.

**Вывод: невозможно.** Ниже — фактические замеры, а не предположения.

## 1. Сеть: разрешены только github.com, pypi.org, registry.npmjs.org

| Хост | Зачем нужен | Ответ |
|---|---|---|
| `pypi.org/simple/` | buildozer, cython | **200 OK** |
| `files.pythonhosted.org` | скачивание пакетов | **доступен** |
| `api.github.com`, `github.com` | релизы Adoptium JDK | **200 OK** |
| `dl.google.com/android/repository/` | Android SDK, build-tools, aapt2 | **недоступен** |
| `dl.google.com/dl/android/maven2/` | Android Gradle Plugin, AndroidX | **недоступен** |
| `repo.maven.apache.org/maven2/` | зависимости Gradle | **недоступен** |
| `services.gradle.org/distributions/` | сам Gradle | **недоступен** |
| `www.python.org/ftp/python/...` | исходники Python для p4a | **недоступен** |
| `deb.debian.org` | `apt-get install` | **недоступен** |

Gradle-шаг сборки APK не может пройти в принципе: ни Gradle, ни Android Gradle
Plugin, ни `aapt2` (из build-tools) скачать негде.

## 2. Тулчейн в песочнице

| Компонент | Состояние |
|---|---|
| `java` / `javac` | **нет** (apt недоступен; JDK 17 можно взять с github.com/adoptium/temurin17-binaries — 192 МБ) |
| `gcc`, `g++`, `make` | есть |
| `autoconf`, `automake`, `libtool` | **нет** — нужны p4a для сборки Python/openssl/libffi под Android |
| `pkg-config`, `cmake` | **нет** |
| `Python.h` (python3-dev) | **нет** — p4a не соберёт ни один C-расширяемый рецепт |
| Заголовки `ffi.h`, `openssl/ssl.h`, `zlib.h`, `sqlite3.h`, `ltdl.h` | **нет** (все пять) |
| Android SDK / NDK | **нет**, `~/.buildozer` отсутствует; официальные источники заблокированы |

## 3. Ресурсы

* CPU: 2 ядра, RAM: 3.9 ГБ, диск: 20 ГБ свободно.
* Даже при открытом интернете buildozer для двух архитектур
  (`arm64-v8a` + `armeabi-v7a` из `buildozer.spec`) на 2 ядрах идёт 1.5–3 часа;
  Gradle с `-Xmx3g` на 3.9 ГБ RAM работал бы на грани OOM.

## 4. Что реально сделано вместо этого

* `.github/workflows/build-apk.yml` — сборка APK в облаке GitHub (тесты → buildozer → артефакт `golub-apk`).
* Тесты прогнаны локально: `python3 -m unittest discover -s tests` → **Ran 18 tests, OK** (16.9 с).
* `python3 -m compileall` по всему проекту — без ошибок.
* PR #1: https://github.com/nikakirill60-a11y/golub/pull/1

GitHub регистрирует workflow только с ветки по умолчанию, поэтому кнопка
**Run workflow** появится после мержа PR в `main`.
