# ProductionDebug 2.0b1 / 205

Все три productionDebug таргета используют IL2CPP, BuildOptions.None и BLITZ_DEBUG_TOOLS. Windows строго собирается Windows editor: Wine/Mono fallback запрещён. IL2CPP усложняет восстановление C#, но не гарантирует защиту от реверс-инжиниринга.

## Локальный CI

```sh
export SDL_HIDAPI_JOYSTICK_DISABLE_UDEV=1
export BLITZ_WINDOWS_IL2CPP_COMMAND="$PWD/Tools/windows_il2cpp_runner.sh"
Tools/local_release.sh --production-debug --allow-untagged
```

Для первого прогона на Arch при отсутствии libncurses.so.6:

```sh
Tools/bootstrap_linux_il2cpp_compat.sh
```

Две библиотеки из проверенного Debian apt repository извлекаются только в .ci-cache; системные библиотеки не заменяются. Tools/unity.sh использует этот путь для Unity Clang. Для Linux IL2CPP явно задаётся -buildTarget Linux64, иначе Unity, открытая под Android, не регистрирует Sysroot API.

## Windows runner

Windows 11 работает в Docker/KVM контейнере blitz-windows-builder, не в Linux userspace. Нужны активированная Unity той же версии, что в ProjectSettings/ProjectVersion.txt (runner выбирает её сам), Windows Build Support IL2CPP, Visual Studio C++ Build Tools и Windows SDK. Пользователь активирует собственную Unity-лицензию; ключи/учётные данные лицензии не извлекаются.

Runner использует отдельный SSH key из внешней директории сборочной ВМ. PasswordAuthentication отключён. ВМ использует user-mode сеть QEMU (SLIRP), чтобы интернет в Windows работал при включённом VPN на хосте; guest IP с хоста недоступен, поэтому SSH идёт через loopback-проброс QEMU 127.0.0.1:2222 → guest:22, без публикации порта наружу. В Windows эта сеть считается Public, поэтому SSH разрешает отдельное правило firewall Blitz-CI-SSH (TCP 22 только с адреса SLIRP-хоста 172.30.99.1). Host key закреплён в automation/known_hosts под псевдонимом blitz-windows-builder. Адрес/порт переопределяются BLITZ_WINDOWS_SSH_HOST/BLITZ_WINDOWS_SSH_PORT; для прежней bridge-сети BLITZ_WINDOWS_SSH_MODE=container включает docker exec/nc на guest IP. Папка VM по умолчанию ../blitz-windows-builder; её можно переопределить BLITZ_WINDOWS_BUILDER_DIR, контейнер — BLITZ_WINDOWS_CONTAINER.

Передаются только Assets, Packages, ProjectSettings, Tools и tutorial_mods.txt. .env, APK keystore, host private key, сохранения и Git metadata не копируются. Tools/windows_il2cpp.ps1 переносит snapshot на локальный C:\BlitzBuild\project, проверяет C++ SDK, выполняет batch build и возвращает результат в shared/windows-output. Runner проверяет version/buildId/revision/backend. Главный CI проверяет GameAssembly.dll/global-metadata.dat и отсутствие shipped BlitzLegacy.Runtime.dll.

## Что раздавать

Только файлы из productiondebug-assets: APK, Linux ZIP, Windows ZIP, tutorial_mods.txt и Онлайн_как_играть.txt (памятка игроку; источник — одноимённый файл в корне репозитория). Сверить SHA256SUMS. BUILD_INFO.txt содержит версию, code, build ID и source revision.

Не раздавать Build/* целиком: Unity оставляет managed DLL и сгенерированный C++ в *_BackUpThisFolder_ButDontShipItWithYourGame. ZIP исключает эту папку, BurstDebugInformation и .pdb/.debug. Native symbols загружаются отдельно в настроенный Sentry project; managed DLL из backup не включаются в upload.

Старые Build/debug-assets/release-assets были удалены перед подготовкой этой версии. Локальный CI очищает текущую productiondebug-assets и выходные директории каждого таргета перед сборкой. Это не затрагивает исходники или сохранения.

Android CLI также явно задаёт -buildTarget Android: платформенный UNITY_ANDROID_API зависит от загруженного target. Windows EXE ProductVersion — 2.0b1, числовой PE FileVersion — 2.0.0.205 (последнее поле — код релиза); версии UnityPlayer.dll остаются версиями движка. Resource stamping использует electron/rcedit v2.0.0 с закреплённым SHA-256, проверяет итоговый FileVersionInfo. Внутренняя Application.version всех игроков — 2.0b1, APK versionCode — 205.
