# NOVA VPN

**Русский** | [English](README.en.md)

[![Release](https://img.shields.io/github/v/release/leoraijin/NOVA-VPN?label=release&color=7863C5)](https://github.com/leoraijin/NOVA-VPN/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/leoraijin/NOVA-VPN/total?color=7863C5)](https://github.com/leoraijin/NOVA-VPN/releases)
![C#](https://img.shields.io/badge/C%23-.NET%20Framework%204.8.1-512BD4?logo=dotnet)
![UI](https://img.shields.io/badge/UI-WPF-7863C5)
![Platform](https://img.shields.io/badge/platform-Windows-0078D4)

Графический VPN-клиент для Windows на **C# + WPF** с ядром [**sing-box**](https://github.com/SagerNet/sing-box).
Добавьте свой профиль или подписку, выберите сервер и нажмите кнопку подключения.
Настройте маршруты для сайтов и приложений, выберите оформление и проверяйте обновления прямо в клиенте.

**[Скачать последнюю версию](https://github.com/leoraijin/NOVA-VPN/releases/latest)** · [Что нового](CHANGELOG.md) · [Помощь](SUPPORT.md)

<p align="center">
  <img src="docs/images/windows-light.png" alt="Главный экран NOVA VPN — Windows Light" width="900">
</p>

<p align="center">
  <img src="docs/images/liquid-glass.png" alt="NOVA VPN — Liquid Glass" width="900">
</p>

Скриншоты версии 2.3.16 сделаны в демонстрационном режиме. Серверы и маршруты — примеры; подключение не запускалось.

## Что как называется

| Компонент | Назначение |
| --- | --- |
| **NOVA VPN** | Интерфейс, профили, маршрутизация, диагностика и обновления |
| **sing-box** | Сетевое ядро: туннель, DNS и применение маршрутов |
| **Zapret** | Отдельный компонент обработки трафика для соответствующих режимов |
| **NOVA-VPN-Android** | [Отдельный Android-клиент](https://github.com/leoraijin/NOVA-VPN-Android) |

Этот репозиторий содержит **исходный код Windows-клиента**, документацию и выпуски с установщиками. Архивы GitHub «Source code» содержат исходники; готовый установщик скачивается отдельно из Assets последнего выпуска.

## Сборка из исходников

Клиент написан на C# и WPF для .NET Framework 4.8.1. Откройте `NOVA VPN.csproj` в Visual Studio 2022 с компонентами разработки .NET Desktop или соберите его через MSBuild. Для полного установщика дополнительно нужны внешние payload-файлы sing-box и Zapret; их лицензии и уведомления перечислены в [THIRD_PARTY.md](THIRD_PARTY.md). VPN-профили и пользовательские ключи в репозиторий не включены.

## Платформы и статус

| Платформа | Где скачать | Статус |
| --- | --- | --- |
| **Windows** | [GitHub Releases](https://github.com/leoraijin/NOVA-VPN/releases/latest) | Выпущена 2.3.17; проверки — в [STATUS](docs/STATUS.md) |
| **Android** | [Отдельный репозиторий](https://github.com/leoraijin/NOVA-VPN-Android) | Развивается отдельно; Windows-установщик для телефона не подходит |

## Возможности

- **Профили и подписки.** Добавление конфигураций, выбор сервера и переключение профилей.
- **Маршрутизация.** Правила для сайтов и приложений; правило сайта применяется раньше правила браузера.
- **Режимы.** VPN, VPN + Zapret и Zapret с явным отображением выбранного режима.
- **Диагностика.** Состояние туннеля и DNS, проверки соединения, журнал событий.
- **Хранение профилей.** Состояние защищается Windows DPAPI в учётной записи пользователя. Полный экспорт с профилями содержит секреты.
- **Оформление.** Liquid Glass, One UI, Windows Light, Windows Dark и Amber Glass; карта региона использует палитру темы.
- **Обновления.** Проверка выпусков GitHub и сводка изменений после обновления.

## Быстрый старт

1. Откройте [последний выпуск](https://github.com/leoraijin/NOVA-VPN/releases/latest) и скачайте **установщик `.exe` из Assets**.
2. Запустите установщик и выберите папку. Windows может запросить права администратора для сетевых компонентов.
3. В разделе **Серверы** добавьте ваш профиль или подписку.
4. Выберите сервер и режим, затем нажмите **Подключить**.
5. Правила сайтов и приложений задаются в разделе **Маршрутизация**.

Установщик не содержит VPN-ключей или подписок. Для подключения нужна собственная конфигурация сервера.

Подробнее: [установка и обновления](docs/INSTALLATION.md) · [приоритет маршрутов](docs/ROUTING.md).

## Оформление

Темы сохраняют расположение основных действий. Изменяются палитра, материалы, форма кнопок и визуальные эффекты.

| Windows Light | Windows Dark |
| --- | --- |
| ![Windows Light](docs/images/windows-light.png) | ![Windows Dark](docs/images/windows-dark.png) |

| One UI | Amber Glass |
| --- | --- |
| ![One UI](docs/images/one-ui.png) | ![Amber Glass](docs/images/amber-glass.png) |

[Подробнее о темах](docs/DESIGN.md). Названия стилей обозначают оформления NOVA; приложение не связано с Apple, Samsung или Microsoft.

## Как это устроено

```mermaid
flowchart LR
  UI["NOVA · WPF"] --> Profiles["Профили и настройки"]
  UI --> Routing["Правила маршрутизации"]
  Profiles --> Config["Конфигурация sing-box"]
  Routing --> Config
  Config --> Core["sing-box · VPN"]
  UI --> Zapret["Zapret · выбранный режим"]
  UI --> Updates["GitHub Releases · обновления"]
```

## Документация

| Файл | О чём |
| --- | --- |
| [docs/README.md](docs/README.md) | Оглавление |
| [docs/INSTALLATION.md](docs/INSTALLATION.md) | Установка, обновление и перенос |
| [docs/ROUTING.md](docs/ROUTING.md) | Правила сайтов и приложений |
| [docs/DESIGN.md](docs/DESIGN.md) | Пять тем оформления |
| [docs/STATUS.md](docs/STATUS.md) | Проверки и ограничения |
| [SUPPORT.md](SUPPORT.md) | Сообщения об ошибках |
| [CHANGELOG.md](CHANGELOG.md) | Последние изменения |
| [THIRD_PARTY.md](THIRD_PARTY.md) | Сторонние компоненты |

## Лицензирование

Собственный код NOVA VPN распространяется по **GNU GPL версии 3 (GPL-3.0-only)**; полный текст находится в [`LICENSE`](LICENSE). Сторонние компоненты и данные сохраняют свои исходные лицензии — см. [THIRD_PARTY.md](THIRD_PARTY.md).
