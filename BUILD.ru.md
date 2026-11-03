# Руководство по сборке

[English](BUILD.md) | [Русский](BUILD.ru.md) · [Об игре](README.ru.md)

Ниже отдельно описаны сборки **из Linux** и **из Windows**. Команды выполняются
из корня проекта. Перед сборкой закройте другие экземпляры Unity с этим проектом.

## Общие требования

- Unity **7000.0.0a8**, ревизия **c93e369fcf1c**, с вашей активированной лицензией.
  Точная версия закреплена в `ProjectSettings/ProjectVersion.txt`.
- Git и **Git LFS**, установленные до клонирования; Python 3.
- Для Android: Android Build Support, SDK/NDK Tools и OpenJDK из установки
  именно этого редактора. Плеер: ARM64, Android 8.0 / API 26 и новее.
- Для Linux-плеера: Linux Build Support **IL2CPP**, Linux-редактор.
- Для Windows-плеера: нативный Windows-редактор, Windows Build Support **IL2CPP**,
  Visual Studio C++ Build Tools и Windows SDK. Wine/Mono не являются заменой.
- Доступ к Git/LFS-серверу, реестру пакетов Unity и GitHub для загрузки Sentry.

Версии зависимостей заданы в `Packages/manifest.json`, `Packages/packages-lock.json`
и встроенных пакетах `Packages/`. Конвертированные ассеты, Photon Fusion,
LiteNetLib, Obfuz, правила обфускации/исходник VM, bootstrap-сцена и .NET-аудитор
уже включены в Git. Godot-проект, архивы оригинального клиента и корневой Photon
`.unitypackage` **не нужны для обычной сборки**. Они могут требоваться для
повторного переноса ассетов; запускать перенос ради сборки не нужно.

## Локальная конфигурация `.env`

Скопируйте `.env.example` в **`.env`** и заполните только нужные интеграции.
Python-драйвер загружает файл в обеих ОС. Поддерживаются буквальные `KEY=value`,
необязательный `export`, комментарии и одинарные/двойные кавычки, **но не shell-
подстановки или команды**. Используйте абсолютные пути; в Windows удобнее прямые
слеши. Явно заданные переменные процесса приоритетнее файла, включая пустое
значение. `BLITZ_ENV_FILE` выбирает другой файл. При отсутствии `.env` сохраняется
совместимость со старым `.env.release.local`.

| Переменные | Назначение / куда попадают |
| --- | --- |
| `BLITZ_SENTRY_DSN` | Публичный HTTPS DSN приёма событий; пустой отключает телеметрию |
| `BLITZ_DIRECT_SERVER` | Адрес своего сервера `host[:port]` или `[IPv6]:port`; пустой отключает этот настроенный адрес |
| `BLITZ_PHOTON_APP_ID` | GUID вашего приложения Fusion; пустой отключает Photon |
| `BLITZ_ANDROID_KEYSTORE`, `BLITZ_ANDROID_KEYSTORE_PASSWORD`, `BLITZ_ANDROID_KEY_ALIAS`, `BLITZ_ANDROID_KEY_PASSWORD` | Только локальная подпись Android |
| `SENTRY_ORG`, `SENTRY_PROJECT`, `SENTRY_AUTH_TOKEN` | Только локальная отправка нативных символов; при пустом токене берётся вход `sentry-cli login` (`~/.sentryclirc`) |
| `BLITZ_DIRECT_DEPLOY_TARGET`, `SSH_KEY`, `DIRECT_HOST`, `DIRECT_SSH_USER`, `DIRECT_SSH_KEY`, `DIRECT_PORT` | Необязательное обслуживание своего сервера, не компиляция плеера |
| `UNITY`, `UNITY_HUB_EDITORS`, `BLITZ_DOTNET` | Необязательные пути к редактору/SDK |

В игнорируемые Unity-ресурсы записываются **только три публичных параметра
подключения**. Они читаются из готовой игры: `.env` не делает DSN, адрес и App ID
секретами. Пароли, ключи подписи, токен Sentry и полное окружение **никогда**
не сериализуются в ресурсы. Пустые значения удаляют старые сгенерированные
ресурсы; настройка телеметрии также очищает устаревшие нативные Sentry options.
Игра не читает `.env` с вашего компьютера: Android получает публичные параметры
во время сборки. На ПК возможны runtime-переопределения соответствующими
переменными окружения; адрес сервера также меняется через
`-blitzServerAddress host:port`.

Не коммитьте `.env`, секреты, лицензию Unity или keystore. Для обновления уже
установленного APK нужен исходный релизный ключ. Все Android-точки сборки
останавливаются без настроенной релизной подписи: автоматического перехода
на debug-сертификат Unity нет. Для независимой личной сборки можно создать свой
релизный keystore через `keytool -genkeypair`; не заменяйте существующий ключ
распространения и не указывайте пароли в командной строке.

## Linux: клонирование, настройка и проверки

Установите через Unity Hub точный редактор с Android/Linux-модулями, а также
Git LFS, Python 3 и Bash. Затем:

```sh
git lfs install
git clone <repository-url> blitz-legacy-unity
cd blitz-legacy-unity
git lfs pull
git lfs fsck
cp .env.example .env
chmod 600 .env
# Заполните .env локально. Для офлайн-сборки оставьте внешние сервисы пустыми.
export UNITY="$HOME/Unity/Hub/Editor/7000.0.0a8/Editor/Unity"
python3 Tools/bootstrap_sentry_sdk.py
Tools/unity.sh import
Tools/unity.sh compile
Tools/unity.sh test
```

`Tools/unity.sh` передаёт загрузку окружения `Tools/unity.py`, сохраняя все Linux-
команды smoke/рендера. Также работает `python3 Tools/unity.py <команда>`.
Пути с пробелами передаются отдельными аргументами. Без `UNITY` shell-драйвер
ищет редакторы в `~/Unity/Hub/Editor`; установите и выберите точную версию,
не полагаясь на запасной выбор другого редактора.

Результаты EditMode: `Logs/editmode-results.xml`. Дополнительные проверки:

```sh
python3 -m unittest discover -s Tools -p test_build_env.py -v
python3 -m unittest discover -s Tools -p test_unity_driver.py -v
BLITZ_TEST_FILTER=LocalizationCoverageTests Tools/unity.sh test
Tools/unity.sh smoke
Tools/unity.sh hangar-smoke
Tools/unity.sh battle-smoke
Tools/unity.sh battle-smoke-mobile
```

Рендеру и профилированию нужна графическая сессия; headless-тест не подтверждает
визуальную корректность. Не запускайте вслепую все Python-аудиты ассетов:
часть требует внешних Godot/клиентских архивов. Зависимости и тесты конвертера
модов отдельно описаны в `Tools/modpack/README.md`.

### Linux: сборка Android и Linux-плеера

После настройки переменных релизной подписи в `.env`:

```sh
Tools/unity.sh apk-production-debug > /tmp/blitz-apk-build.log 2>&1
Tools/unity.sh linux-production-debug > /tmp/blitz-linux-build.log 2>&1
```

Результаты:

- `Build/Android/BlitzLegacy-productiondebug.apk`.
- `Build/Linux-productiondebug/BlitzLegacy` и вся соседняя папка данных/библиотек.

В Android-логе проверьте `[Blitz] android signing: keystore ...`. Командой SDK
`apksigner verify --verbose --print-certs` проверьте APK и сравните сертификат
с нужным релизным ключом. Перед публикацией логов проверьте их содержимое.
Если подключён телефон:

```sh
adb devices
adb install -r Build/Android/BlitzLegacy-productiondebug.apk
```

Запуск Linux-игры с сохранением всей выходной директории:

```sh
chmod +x Build/Linux-productiondebug/BlitzLegacy
./Build/Linux-productiondebug/BlitzLegacy
```

Если Unity Clang не хватает `libncurses.so.6`, например на Arch, выполните
`Tools/bootstrap_linux_il2cpp_compat.sh`. Проверенные по контрольной сумме
библиотеки попадут только в `.ci-cache/`, без замены системных. Закреплённые
Linux SDK/toolchain восстанавливаются UPM, а не копированием чужой `Library/`.

## Windows: клонирование, настройка и проверки

Установите Git for Windows с Git LFS, Python 3 и точную Unity с нужными модулями.
Для Windows IL2CPP установите Visual Studio C++ Build Tools и Windows SDK,
активируйте свою лицензию Unity. Для Android добавьте Android Build Support
с SDK/NDK/OpenJDK. Используйте **PowerShell**, а не Linux-команды:

```powershell
git lfs install
git clone <repository-url> blitz-legacy-unity
Set-Location blitz-legacy-unity
git lfs pull
git lfs fsck
Copy-Item .env.example .env
# Заполните .env; пути задавайте абсолютными в кавычках (C:/... переносимее).
$env:UNITY = 'C:/Program Files/Unity/Hub/Editor/7000.0.0a8/Editor/Unity.exe'
py -3 Tools/bootstrap_sentry_sdk.py
py -3 Tools/unity.py import
py -3 Tools/unity.py compile
py -3 Tools/unity.py test
if ($LASTEXITCODE -ne 0) { throw 'Unity tests failed; inspect Logs' }
```

Python-драйвер дожидается `Unity.exe`, возвращает его код завершения и пишет
логи в `Logs/<команда>.log`. Результаты тестов: `Logs/editmode-results.xml`.
Для отдельной группы задайте `$env:BLITZ_TEST_FILTER = 'LocalizationCoverageTests'`;
после проверки удалите фильтр через `Remove-Item Env:BLITZ_TEST_FILTER`.

### Windows: сборка Windows- и Android-плееров

```powershell
py -3 Tools/unity.py windows-production-debug
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed; inspect Logs/windows-production-debug.log' }
# Нужны Android-модули и четыре параметра подписи в .env:
py -3 Tools/unity.py apk-production-debug
if ($LASTEXITCODE -ne 0) { throw 'Android build failed; inspect Logs/apk-production-debug.log' }
```

Результаты:

- `Build/Windows-productiondebug/BlitzLegacy.exe` и соседние данные/DLL.
- `Build/Android/BlitzLegacy-productiondebug.apk`.

Запуск Windows-игры с сохранением **всей** папки:

```powershell
& './Build/Windows-productiondebug/BlitzLegacy.exe'
```

Проверьте Android-подпись SDK-командой `apksigner.bat`; при подключённом телефоне
используйте `adb install -r`. Для нативной Windows-сборки не нужны Docker,
SSH, Windows VM, Bash или Godot-проект. Linux IL2CPP-плеер собирается по инструкции
Linux, а не подменяется Windows Mono-сборкой.

Нативный драйвер собирает плеер. Релизная упаковка, маркировка build identity,
версия в PE-ресурсах Windows и отправка символов относятся к релизному runner/
пайплайну ниже, а не к базовой команде сборки.

## Варианты сборки

**productiondebug** — вариант для распространения альфы: IL2CPP, игровые
отладочные инструменты (`BLITZ_DEBUG_TOOLS`), но без Unity Development Build
оверлея. Это не обычная development-сборка. В недевелоперских вариантах
по умолчанию включён Obfuz.

| Команда | Вариант / результат |
| --- | --- |
| `apk-production-debug` | `Build/Android/BlitzLegacy-productiondebug.apk` |
| `apk` / `apk-debug` | Android release / development: `BlitzLegacy.apk` / `BlitzLegacy-debug.apk` |
| `linux-production-debug` | `Build/Linux-productiondebug/BlitzLegacy` |
| `linux` / `linux-debug` | Linux release / development: `Build/Linux/` / `Build/Linux-debug/` |
| `windows-production-debug` | `Build/Windows-productiondebug/BlitzLegacy.exe` в Windows |

На Linux передайте команду в `Tools/unity.sh`, на Windows — в `py -3 Tools/unity.py`.
Для намеренного пересоздания сцены/настроек используйте `setup`, `scenes`,
`net-setup`, затем проверьте Git diff. В обычной копии сцена и настройки уже есть.

## SDK

SDK для модов — инструмент `rempack`, описанный в [`Sdk/README.ru.md`](Sdk/README.ru.md), —
консольная программа на .NET 10, Unity для неё не нужен. Она компилирует код
пакетов игры из `Assets/Blitz/Core/Mods` и запускает тесты пакетов игры, поэтому игра
и SDK всегда одинаково понимают, какой пакет корректен. С .NET 10 SDK (ищется через
`BLITZ_DOTNET`, `PATH` или `~/.dotnet`) на любой из систем:

```sh
python3 Tools/sdk_build.py                # тесты, затем оба архива в Build/Sdk/
python3 Tools/sdk_build.py --platform win-x64 --skip-tests
dotnet test Sdk/Rempack.Sdk.slnx -c Release
```

В Windows запускайте скрипт через `py -3`. Первая сборка скачивает пакеты среды
выполнения .NET из NuGet. Каждый архив, `BlitzRemasterSDK-<версия>-windows-x64.zip` или
`-linux-x64.zip`, содержит самодостаточный урезанный однофайловый исполняемый
файл и README SDK; `<версия>` — это `BlitzVersion.Current`. Для проверки скрипт запускает
Linux-версию на Linux, а Windows-версию на Windows.

## Релизный пайплайн Linux и необязательный Windows VM-runner

Для упаковки нужны `sha256sum`, `zip`, `unzip`, `file`, `keytool` и .NET SDK:
встроенный `DotNetSdk/dotnet` редактора ищется автоматически, либо задайте
`BLITZ_DOTNET`. `Tools/local_release.sh` загружает доверенный `.env` как shell-
код; сохраняйте совместимость с описанным выше буквальным синтаксисом.
По умолчанию нужны чистое дерево и тег, совпадающий с `bundleVersion`:

```sh
Tools/local_release.sh --production-debug --android-only
Tools/local_release.sh --production-debug --unix-only
# Локальная проверка без тега и отправки символов:
Tools/local_release.sh --production-debug --android-only --allow-untagged --no-sentry
```

`--allow-dirty` предназначен только для намеренной локальной проверки:
ревизия помечается dirty. Для отправки символов нужны `sentry-cli`,
`SENTRY_ORG`/`SENTRY_PROJECT` нужного проекта и токен: `SENTRY_AUTH_TOKEN` либо,
если он пуст, токен `sentry-cli login` из `~/.sentryclirc`. Авторизация
проверяется до начала сборок. `--no-sentry` отключает отправку,
**но не установку SDK**: пайплайн всегда восстанавливает Sentry Unity **4.10.3**
с проверкой суммы. Прямые сборки без этого необязательного SDK компилируются
с отключённой телеметрией.

Все три платформы можно упаковать через **заранее настроенную** нативную
Windows Docker/KVM-машину:

```sh
export BLITZ_WINDOWS_IL2CPP_COMMAND="$PWD/Tools/windows_il2cpp_runner.sh"
Tools/local_release.sh --production-debug
```

VM, активированный редактор/C++ SDK, SSH-ключ и общая SMB-папка — внешняя
инфраструктура, не недостающие исходники игры. Настройте необязательные
`BLITZ_WINDOWS_*` из `.env.example`. По умолчанию runner ожидает соседнюю
папку `blitz-windows-builder`, а в ней `automation/build_key`,
`automation/known_hosts`, `shared/project` и `shared/windows-output`.
См. `Tools/windows_il2cpp_runner.sh` / `Tools/windows_il2cpp.ps1`. SSH идёт через
loopback-проброс ВМ `127.0.0.1:2222` (user-mode сеть QEMU, с которой интернет
в госте работает и при VPN на хосте); `BLITZ_WINDOWS_SSH_MODE=container` обращается
к адресу гостя изнутри контейнера при bridge-сети.
Runner скачивает проверенный `rcedit` 2.0.0, проверяет build identity и IL2CPP;
локальные `.env`, keystore и приватные SSH-ключи не копируются.
Для базовой нативной Windows-сборки этот runner не нужен.

Распространяйте предназначенные игрокам файлы из **`productiondebug-assets/`**,
а не произвольный `Build/*`: пайплайн упаковывает плееры и инструкции, создаёт
`BUILD_INFO.txt` и `SHA256SUMS`, проверяет обфускацию и при необходимости
отправляет нативные символы.

```sh
(cd productiondebug-assets && sha256sum -c SHA256SUMS)
```

Не публикуйте `*_BackUpThisFolder_ButDontShipItWithYourGame`,
`BurstDebugInformation`, сгенерированный C++, карты обфускации и символы внутри
публичного архива игры. Фильтры релизного ZIP исключают эти данные.

## Полнота исходников и диагностика

Храните в Git `Assets/` с `.meta`, версии пакетов/встроенный Obfuz в `Packages/`,
`ProjectSettings/`, `Tools/`, включая `ObfuscationAudit.csproj` и его props,
инструкции игроку, `.env.example`, `AGENTS.md` и правила Git. Submodule нет.
Sentry восстанавливается `bootstrap_sentry_sdk.py` по закреплённому SHA-256;
при ограниченной сети точный ZIP можно заранее положить в `.ci-cache/downloads/`.
LFS-ассеты должны быть восстановлены, а не оставлены текстовыми указателями.

Игнорируются кэши Unity, логи, сборки, виртуальные окружения, генерируемые
публичные параметры/Sentry options, локальные `.env`, ключи и журналы
`TASK_PLAN*` / `PORTING_NOTES*`. Журналы не входят в публичную историю.

| Проблема | Решение |
| --- | --- |
| Ассеты стали текстовыми указателями | `git lfs pull`, затем `git lfs fsck`; проверьте доступ к серверу |
| Другой редактор / нет Android API | Установите точные модули; драйвер выбирает `-buildTarget Android` |
| Нет Linux sysroot provider | Восстановите закреплённые UPM-пакеты; драйвер выбирает `-buildTarget Linux64` |
| Ошибка лицензии/блокировки | Активируйте лицензию / закройте другие редакторы; не удаляйте живые блокировки |
| APK не обновляет установленную игру | Проверьте application ID и исходный ключ, а не только версию |
| Нет онлайна/телеметрии | Задайте публичные интеграции в `.env`, пересоберите и проверьте доступность сервиса |
| Windows VM недоступна | Используйте нативный Windows-редактор либо настройте VM; Mono не заменяет IL2CPP |

Стандартный порт своего сервера — UDP 7777; сборка плеера сервер не разворачивает.
Перед использованием проверьте `Tools/direct_deploy.sh` и связанные эксплуатационные
скрипты: сетевые тесты могут намеренно останавливать/перезапускать удалённый
сервер. Для локальной компиляции развёртывание не требуется.

Проверка чистых исходников: восстановить LFS, установить Sentry и выполнить
import/compile/test и сборки в отдельной копии **без переноса Library/UserSettings
и локальных секретов**. Секреты передаются окружением. Это подтверждает полноту
исходников, но не побайтовую воспроизводимость бинарных файлов Unity.
