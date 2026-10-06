<div align="center">

# NOVA VPN

### VPN-клиент для Windows с понятной маршрутизацией и несколькими стилями оформления

[![Latest release](https://img.shields.io/github/v/release/leoraijin/NOVA-VPN?label=Windows&color=7863C5)](https://github.com/leoraijin/NOVA-VPN/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/leoraijin/NOVA-VPN/total?color=7863C5)](https://github.com/leoraijin/NOVA-VPN/releases)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D4)](https://github.com/leoraijin/NOVA-VPN/releases/latest)

**[Скачать установщик →](https://github.com/leoraijin/NOVA-VPN/releases/latest)** · [Все версии](https://github.com/leoraijin/NOVA-VPN/releases) · [Сообщить о проблеме](https://github.com/leoraijin/NOVA-VPN/issues)

</div>

---

## Установка

1. Откройте [последний выпуск](https://github.com/leoraijin/NOVA-VPN/releases/latest).
2. В блоке **Assets** скачайте установщик `.exe`.
3. Запустите его и следуйте инструкциям. Windows может запросить права администратора для работы сетевых компонентов.
4. Добавьте свой VPN-профиль или ссылку подписки в разделе **Серверы**.
5. Выберите режим и нажмите **Подключить**.

Установщик не содержит VPN-ключей, подписок или пользовательских профилей. Для подключения нужен ваш собственный сервер или конфигурация.

## Возможности

- **Маршрутизация сайтов и приложений.** Правило сайта применяется раньше правила браузера: например, сайт можно направить через VPN, даже если браузер назначен напрямую.
- **Три режима трафика:** VPN, VPN + Zapret и Zapret.
- **Профили и подписки.** Импорт конфигураций, выбор сервера и переключение профилей.
- **Персонализация.** Оформления Liquid Glass, One UI, Windows Light, Windows Dark и Amber Glass.
- **Диагностика.** Проверка соединения, состояния туннеля и DNS, журнал событий.
- **Обновления.** Проверка новых выпусков на GitHub из приложения.

Названия оформлений обозначают визуальные стили NOVA; это не продукты Apple, Samsung или Microsoft.

## Обновление клиента

Откройте **Настройки → Обновление приложения** или скачайте установщик из [GitHub Releases](https://github.com/leoraijin/NOVA-VPN/releases/latest). Краткая сводка изменений публикуется в описании каждого выпуска.

Обновляйте приложение в прежней папке установки. Перед переносом на другой компьютер используйте экспорт настроек и отдельно перенесите VPN-профили.

## Другие платформы

| Клиент | Репозиторий |
| --- | --- |
| Windows | Этот репозиторий |
| Android | [NOVA-VPN-Android](https://github.com/leoraijin/NOVA-VPN-Android) |

Android развивается отдельно. Windows-установщик не подходит для телефонов и часов.

## Если возникла проблема

Создайте [issue](https://github.com/leoraijin/NOVA-VPN/issues) и укажите версию NOVA, версию Windows, выбранный режим и шаги воспроизведения. При необходимости приложите скриншот или обезличенный фрагмент журнала.

**Не публикуйте VPN-ключи, ссылки подписок, пароли и экспорт с профилями.** Перед отправкой журнала удалите секреты и персональные данные.

## Состав поставки

В установщик включены NOVA VPN, [sing-box](https://github.com/SagerNet/sing-box) и [Zapret](https://github.com/Flowseal/zapret-discord-youtube). Уведомления о сторонних компонентах находятся в папке установки.

Этот репозиторий служит для распространения Windows-выпусков. Архивы **Source code**, автоматически создаваемые GitHub, содержат файлы репозитория и не являются установщиками.
