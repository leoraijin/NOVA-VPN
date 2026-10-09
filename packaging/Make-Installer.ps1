param(
    [string]$ZapretArchive = (Join-Path $PSScriptRoot '..\..\zapret-1.10.3.zip'),
    [string]$AppOutput = (Join-Path $PSScriptRoot '..\bin\Release\NOVA VPN.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\..\outputs'),
    [string]$RecoveredPayloadDirectory = $null
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$zapretHash = '244314AE1C24538A0D751601DA8E0C925C843371EEC4456EB15F14C4FD6B7058'
$actualHash = $null
if ([string]::IsNullOrWhiteSpace($RecoveredPayloadDirectory)) {
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ZapretArchive).Hash
    if ($actualHash -ne $zapretHash) { throw "Официальный архив Zapret не прошёл проверку SHA-256: $actualHash" }
} else {
    $RecoveredPayloadDirectory = [IO.Path]::GetFullPath($RecoveredPayloadDirectory)
    foreach ($required in @('core\sing-box.exe','zapret\current\general.bat','zapret\current\nova-version.txt','THIRD-PARTY-NOTICES.txt','ZAPRET-NOTICE.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $RecoveredPayloadDirectory $required) -PathType Leaf)) {
            throw "В проверенном payload отсутствует обязательный файл: $required"
        }
    }
    if ((Get-Content -LiteralPath (Join-Path $RecoveredPayloadDirectory 'zapret\current\nova-version.txt') -Raw).Trim() -ne '1.10.3') {
        throw 'Recovered payload содержит неожидаемую версию Zapret.'
    }
}
if (-not (Test-Path -LiteralPath $AppOutput -PathType Leaf)) { throw "Не найдена собранная NOVA: $AppOutput" }

$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) { throw 'Не найден компилятор C# для сборки интерфейса установщика.' }
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) { throw 'Не найден MSBuild Visual Studio Build Tools 2022.' }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stageRoot = Join-Path $root ("work\single-file-installer-stage\build-$stamp")
$payload = Join-Path $stageRoot 'payload'
$zapretExtract = Join-Path $stageRoot 'zapret-extracted'
$setupFiles = Join-Path $stageRoot 'setup-files'
New-Item -ItemType Directory -Path $payload,$zapretExtract,$setupFiles -Force | Out-Null

$uninstaller = Join-Path $setupFiles 'NOVA-VPN-Uninstaller.exe'
$installerSource = Join-Path $PSScriptRoot 'InstallerUI.cs'
$manifest = Join-Path $PSScriptRoot 'Installer.manifest'
$icon = Join-Path $PSScriptRoot '..\Assets\nova-v2.ico'
$compilerArgs = @('/nologo','/target:winexe','/platform:anycpu','/optimize+',('/out:' + $uninstaller),
    ('/win32manifest:' + $manifest),('/win32icon:' + $icon),
    '/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll',
    '/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll',
    $installerSource)
& $csc @compilerArgs
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw 'Не удалось собрать компонент удаления.' }

Copy-Item -LiteralPath $AppOutput -Destination (Join-Path $payload 'NOVA VPN.exe')
$payloadAssets = Join-Path $payload 'Assets'
New-Item -ItemType Directory -Path $payloadAssets -Force | Out-Null
foreach ($assetName in @('nova-mark.png','nova-backup.png','nova-v2.ico')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('..\Assets\' + $assetName)) -Destination $payloadAssets
}
if ([string]::IsNullOrWhiteSpace($RecoveredPayloadDirectory)) {
    Copy-Item -LiteralPath (Join-Path $root 'outputs\NOVA VPN Zapret Fix\core') -Destination (Join-Path $payload 'core') -Recurse
    Copy-Item -LiteralPath (Join-Path $root 'outputs\NOVA VPN Zapret Fix\THIRD-PARTY-NOTICES.txt') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $root 'outputs\NOVA VPN Zapret Fix\ASSET-NOTICES.txt') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $root 'outputs\NOVA VPN Zapret Fix\CHANGELOG.txt') -Destination $payload
} else {
    Copy-Item -LiteralPath (Join-Path $RecoveredPayloadDirectory 'core') -Destination (Join-Path $payload 'core') -Recurse
    foreach ($notice in @('THIRD-PARTY-NOTICES.txt','ASSET-NOTICES.txt','CHANGELOG.txt')) {
        Copy-Item -LiteralPath (Join-Path $RecoveredPayloadDirectory $notice) -Destination $payload
    }
}
$releaseNotes = @'
NOVA VPN 2.3.17 — 7 октября 2026
==================================
• Сайты «Напрямую», включая поддомены, имеют отдельный прямой выход и локальный DNS без возврата на VPN.
• Прямое исключение выше правил VPN браузера и доменов, также в импортированных конфигурациях.
• Для разделения сайтов отключите защищённый DNS браузера. Сторонние CDN-домены добавляйте отдельно.
• После изменения маршрутов переподключитесь и закройте старые вкладки/соединения браузера.
• Обновления: Настройки → Обновление приложения.

NOVA VPN 2.3.16 — 7 октября 2026
==================================
• Устранены рывки и лишняя нагрузка при рисовании окна персонализации и настройке оформления.
• Предпросмотр объединяет быстрые изменения и обновляется с ограниченной частотой, без перестройки на каждом кадре.
• Фон, стеклянная подложка и размытие переиспользуют визуальные ресурсы, если соответствующие параметры не менялись.

NOVA VPN 2.3.15 — 6 октября 2026
==================================
• Карта региона подключения теперь отображается в широкой раскладке во всех пяти профилях оформления.
• Цвет страны, маршрута и маркера карты автоматически соответствует штатной палитре выбранного профиля.
• Карта остаётся офлайн и не отправляет IP-адрес стороннему геолокационному сервису.
• Проверить обновления и открыть установщик можно в разделе Настройки → Обновление приложения.

NOVA VPN 2.3.14 — 5 октября 2026
==================================
• Исправлена маршрутизация сайтов: доменные правила теперь применяются раньше правил по пути и имени браузера.
• Если браузер назначен напрямую, сайт с правилом «Через VPN» всё равно проходит через VPN; и наоборот, сайт с прямым правилом не уходит в VPN вместе с браузером.
• Приоритет отражён в диагностическом объяснении маршрута и проверен для обычных и импортированных sing-box конфигураций.
• Сводка проверенных изменений: Настройки → Обновление приложения → «Что нового».

NOVA VPN 2.3.13 — 5 октября 2026
==================================
• После обновления при первом запуске показывается краткая сводка изменений; если пропущено несколько версий, их заметки объединяются.
• В сводке указано, где проверить и скачать обновление: Настройки → Обновление приложения → «Обновить».

NOVA VPN 2.3.12 — 5 октября 2026
==================================
• Декоративная схема на главной заменена настоящей офлайн-картой стран с подсветкой региона выбранного VPN-профиля.
• Если в имени профиля распознан город, маркер ставится по географическим данным населённого пункта; иначе — по точке подписи страны.
• Карта и названия стран встроены в приложение: интернет и сторонние картографические сервисы для отображения не нужны.
• Карта не определяет фактические координаты IP/дата-центра и явно сообщает, когда в названии профиля недостаточно данных.
• Проверены распознавание Бельгии/Брюсселя и других профилей, отрисовка страны и города, неизвестное местоположение и офлайн-состав установщика.

NOVA VPN 2.3.11 — 4 октября 2026
==================================
• Добавлен пятый профиль оформления Amber Glass по предоставленному примеру: янтарно-оранжевая палитра, кремовые стеклянные карточки и мягкие сине-голубые блики.
• Главная кнопка подключения в Amber Glass — круглая объёмная кнопка с тёплым градиентом, тенями и физическим откликом.
• На главной добавлена декоративная схема маршрута; в узкой раскладке она скрывается, чтобы оставить достаточно места режимам и подписям.
• Параметры стекла и оформления Amber Glass сохраняются отдельно от остальных тем.
• Проверены пять доступных профилей, геометрия подключения и обновлённая персонализация.

NOVA VPN 2.3.10 — 2 октября 2026
==================================
• Windows-клиент адаптирован для сенсорного управления: крупные области нажатия, увеличенные переключатели, вертикальная прокрутка жестом.
• Сенсорные размеры применены к навигации, кнопкам, заголовкам окна, маршрутизации приложений и системным диалогам.
• Проверены сборка, регрессионные тесты сенсорных областей и прокрутки, а также отрисовка страниц при 720/900/1200 DIP во всех четырёх темах.
• При каждом запуске показана плашка «Проверка наличия обновлений…» во время сверки версии с GitHub.
• Предложение обновления содержит «Да», «Возможно позже», «Больше не показывать». Скрытие предложения сохраняется; проверка и ручное обновление остаются доступны.
• Предложения можно снова включить в настройках. Установщик скачивается только после выбора «Да».
• Удалён выбор пользовательских цветов и ввод HEX; старые и импортированные переопределения не меняют штатные палитры четырёх исходных тем.
• Кнопка подключения сохраняет акцент выбранной темы и в подключённом состоянии; индикаторы ошибок и состояния остаются информативными.
• Текст: сглаживание без цветных краёв, увеличенные мелкие подписи и менее тяжёлое начертание небольших заголовков.
• Разделены материалы информационных карточек и стеклянных управляющих поверхностей iOS.
• Добавлен независимый ползунок мягкого контраста с сохранением и экспортом по каждой теме.
• One UI: цельные группы настроек с внутренними разделителями, без изменения порядка и действий.
• Доработаны нажатие и клавиатурный фокус кнопок; меню выбора получили плотный фон и выделение выбранного пункта.
• Измеренная частота обновлений отрисовки кнопки в тесте WPF — около 120/с во всех четырёх исходных профилях. Это не гарантия физического отображения каждого кадра монитором.
• Темы визуально разделены: голубое стекло iOS, бело-лиловый One UI, нейтральный Windows Light и графитовый Windows Dark. Расположение действий сохранено.
• Повышена обычная контрастность iOS; обычные кнопки и селекторы получили небольшие углы вместо овальной формы. Главная кнопка iOS остаётся круглой.
• Убрана синхронная загрузка значков приложений в UI-потоке; список маршрутизации строится порциями с возвратом управления интерфейсу.
• iOS: независимые настройки прозрачности, настоящего размытия подложки, силы теней и бликов. Все значения сохраняются отдельно для профиля.
• Размывается только декоративная подложка: надписи и значки остаются чёткими. Размытие применяется к стеклянным кнопкам, основным карточкам и боковой панели.
• Полностью круглая кнопка подключения, отдельные объёмные кнопки навигации по варианту 2.2.7, синий акцент и верхнее левое освещение.
• Физический отклик работает мышью и клавиатурой; отмена нажатия возвращает кнопку в исходное положение.
• Кольцо вращается только при подключении. В спокойном подключённом состоянии декоративные анимации останавливаются.
• Добавлены профили iOS 27 Liquid Glass, One UI 8.0, Windows 11 Light и Windows 11 Dark.
• Для каждого профиля сохраняются собственные цвета, форма, прозрачность, плотность и параметры движения.
• Окно персонализации показывает быстрый предпросмотр и больше не пересобирает VPN-окно при перемещении ползунков.
• Переключатели и выбор профилей получили короткие анимации с учётом ограничения 60/120 кадров/с и режима уменьшения движения.
• Отмена закрывает черновик без записи; применение и сохранение выполняются один раз.

NOVA VPN 2.1.17 — 29 сентября 2026
==================================
• Новая светлая тема Vanilla Sky: ванильный фон, молочные карточки, голубые и лавандовые акценты.
• Мягкий перламутровый перелив кнопки подключения; отключается настройкой уменьшения движения.
• Тема выбирается в Настройки → Персонализация. Существующие темы и настройки сохранены.

NOVA VPN 2.1.16 — 29 сентября 2026
==================================
• Более компактный главный экран: в узком окне кнопка подключения и состояние расположены рядом.
• Мягкие градиенты карточек, световой оттенок Glass, увеличенные кнопки и тонкая полоса прокрутки.

NOVA VPN 2.1.15 — 29 сентября 2026
==================================
• Добавлены темы NOVA Soft, NOVA Glass и NOVA Color; NOVA Soft используется при первой установке.
• Макет перестраивается по ширине окна: навигация сворачивается, главный экран меняет колонки и карточки.
• Убрано масштабирование всей страницы вместе с текстом; минимальный размер окна снижен для небольших экранов.
• Исправлено мигание страницы при обновлении VPN; сетевые индикаторы обновляются отдельно.

NOVA VPN 2.1.14 — 27 сентября 2026
==================================
• Более мягкая палитра NOVA Dark, приглушённый фон и свечение, спокойные анимации кнопок.

NOVA VPN 2.1.13 — 27 сентября 2026
==================================
• При запуске клиент проверяет GitHub Releases и предлагает установить обновление или отложить его; конфиги сохраняются.

NOVA VPN 2.1.12 — 27 сентября 2026
==================================
• Исправлено выравнивание переключателей приложений: теперь они стоят в одной колонке при любой длине названия и пути.

NOVA VPN 2.1.11 — 26 сентября 2026
==================================
• При смене VPN-конфига подключённый туннель автоматически перезапускается на выбранном профиле; при ошибке восстанавливается прежний.
• Сохраняется исправление маршрутизации: через Zapret идут только Discord и YouTube, остальные домены и приложения остаются в VPN.

NOVA VPN 2.1.8 — 25 сентября 2026
==================================
• В маршрутизации появился список Windows-приложений с иконками, поиском и переключением «по умолчанию / VPN / напрямую».
• Правила по полному пути к .exe работают во всех режимах VPN; домены Zapret сохраняют приоритет в режиме «VPN + Zapret».
• Изменения маршрута можно применить после проверки конфигурации; при ошибке приложение пытается восстановить прежний маршрут.

NOVA VPN 2.1.7 — 25 сентября 2026
==================================
• В режиме «VPN + Zapret» сайты из списка Zapret временно направляются напрямую в локальный WinDivert, минуя VPN-туннель; после выключения восстанавливаются сохранённые маршруты через VPN.
• Временное правило Zapret имеет приоритет над VPN-правилами доменов и приложений; сохранённые списки пользователя не изменяются.

NOVA VPN 2.1.6 — 25 сентября 2026
==================================
• Кнопка «Проверка обновлений» только проверяет версию Zapret; установка начинается только по кнопке «Обновить Zapret».
• Удалены с главного экрана кнопки добавления и удаления доменов Zapret в маршрутизации VPN.

NOVA VPN 2.1.5 — 25 сентября 2026
==================================
• Исправлено исчезновение доменов Zapret из VPN-маршрутизации в режиме «VPN + Zapret»: домены временно принудительно направляются через VPN, а пользовательские списки не меняются.
• В режиме «Только Zapret» правила VPN не синтезируются.

NOVA VPN 2.1.4 — 24 сентября 2026
==================================
• Кнопка «Обновить» открывает прямое скачивание последнего установщика GitHub Releases.
• Сбор трафика работает только на видимой главной странице; сетевые адаптеры опрашиваются лишь при подключённом VPN.
• Проверка подписок запускается по сроку обновления и приостанавливается на время VPN-сессии.
• В комплект входят NOVA VPN, sing-box и Zapret 1.10.3; VPN-ключи не включены.

'@
$previousNotes = Get-Content -LiteralPath (Join-Path $payload 'CHANGELOG.txt') -Raw
Set-Content -LiteralPath (Join-Path $payload 'CHANGELOG.txt') -Value ($releaseNotes + $previousNotes) -Encoding utf8

$zapretCurrent = Join-Path $payload 'zapret\current'
New-Item -ItemType Directory -Path (Split-Path -Parent $zapretCurrent) -Force | Out-Null
if ([string]::IsNullOrWhiteSpace($RecoveredPayloadDirectory)) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($ZapretArchive), $zapretExtract)
    $bundleRoot = Join-Path $zapretExtract 'zapret-discord-youtube-1.10.3'
    if (-not (Test-Path -LiteralPath (Join-Path $bundleRoot 'bin\winws.exe') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $bundleRoot 'general.bat') -PathType Leaf)) {
        throw 'Содержимое релиза Zapret 1.10.3 не прошло проверку состава.'
    }
    Copy-Item -LiteralPath $bundleRoot -Destination $zapretCurrent -Recurse
    Set-Content -LiteralPath (Join-Path $zapretCurrent 'nova-version.txt') -Value '1.10.3' -Encoding ascii
    $zapretNotice = @'
NOVA VPN includes the unmodified Windows release archive of Flowseal Zapret.
Project: https://github.com/Flowseal/zapret-discord-youtube
Release: 1.10.3
Release archive: zapret-discord-youtube-1.10.3.zip
SHA-256: 244314ae1c24538a0d751601da8e0c925c843371eec4456eb15f14c4fd6b7058

Zapret and WinDivert are third-party components. Their upstream files are included
in zapret\current. Windows may request administrator approval because WinDivert
uses a packet-filter driver.
'@
    Set-Content -LiteralPath (Join-Path $payload 'ZAPRET-NOTICE.txt') -Value $zapretNotice -Encoding utf8
} else {
    Copy-Item -LiteralPath (Join-Path $RecoveredPayloadDirectory 'zapret\current') -Destination $zapretCurrent -Recurse
    Copy-Item -LiteralPath (Join-Path $RecoveredPayloadDirectory 'ZAPRET-NOTICE.txt') -Destination $payload
    if (-not (Test-Path -LiteralPath (Join-Path $zapretCurrent 'bin\winws.exe') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $zapretCurrent 'general.bat') -PathType Leaf)) {
        throw 'В восстановленном Zapret payload отсутствуют обязательные компоненты.'
    }
}

$instructions = @'
NOVA VPN 2.3.17 — установка
=========================

Запустите «NOVA VPN Setup.exe» и выберите папку установки. Windows запросит
подтверждение администратора: оно нужно приложению для управления VPN TUN и
драйвером WinDivert.

В одном установщике находятся NOVA VPN, sing-box 1.13.14 и официальный Zapret
1.10.3. Обновления Zapret после установки проверяются из GitHub Releases.

Установщик не содержит VPN-профилей, ключей, ссылок подписок или пользовательской
папки %AppData%\NovaVPN. При обновлении он сохраняет уже существующие данные.
Чтобы перенести маршруты, оформление и настройки Zapret на другой компьютер,
в приложении выберите «Экспорт настроек без ключей», а на новом ПК —
«Импорт настроек без ключей». Профили VPN и ключи этим способом не копируются.
Правила по полному пути к .exe на новом ПК нужно выбрать заново.

Для переноса VPN-серверов добавьте их на новом компьютере отдельно. Полный файл
«Экспорт для другого ПК» включает ключи и ссылку подписки — обращайтесь с ним
как с паролем.

Уведомления о сторонних компонентах находятся в папке поставки.
'@
Set-Content -LiteralPath (Join-Path $payload 'ИНСТРУКЦИЯ.txt') -Value $instructions -Encoding utf8

Add-Type -AssemblyName System.IO.Compression.FileSystem
$payloadZip = Join-Path $setupFiles 'NOVA-Payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $payloadZip, [IO.Compression.CompressionLevel]::Optimal, $false)
$finalTarget = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'NOVA VPN Setup 2.3.17.exe'
$stagedSetup = Join-Path $setupFiles 'NOVA-VPN-Setup.exe'
$packagerArgs = @('/nologo','/target:winexe','/platform:anycpu','/optimize+',('/out:' + $stagedSetup),
    ('/win32manifest:' + $manifest),('/win32icon:' + $icon),
    '/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll',
    '/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll',
    ('/resource:' + $payloadZip + ',NOVA.Payload.zip'),
    ('/resource:' + $uninstaller + ',NOVA.Uninstaller.exe'),
    $installerSource)
& $csc @packagerArgs
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $stagedSetup -PathType Leaf)) { throw 'Не удалось собрать единый установщик с встроенными компонентами.' }
if ((Get-Item -LiteralPath $stagedSetup).Length -le (Get-Item -LiteralPath $payloadZip).Length) { throw 'Установщик не содержит полный архив приложения.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Copy-Item -LiteralPath $stagedSetup -Destination $finalTarget -Force

[pscustomobject]@{
    Installer = $finalTarget
    InstallerBytes = (Get-Item -LiteralPath $finalTarget).Length
    InstallerSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $finalTarget).Hash
    ZapretRelease = '1.10.3'
    ZapretArchiveSha256 = $(if ($actualHash) { $actualHash } else { 'Inherited from verified public NOVA VPN 2.3.9 installer payload' })
    Stage = $stageRoot
} | Format-List
