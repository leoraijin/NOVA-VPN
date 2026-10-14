using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NovaVpn;

public sealed class ReleaseNoteEntry
{
	public Version Version { get; private set; }
	public IReadOnlyList<string> Items { get; private set; }

	public ReleaseNoteEntry(Version version, params string[] items)
	{
		Version = version;
		Items = items ?? Array.Empty<string>();
	}
}

public static class ReleaseNotesService
{
	private static readonly List<ReleaseNoteEntry> Notes = new List<ReleaseNoteEntry>
	{
		new ReleaseNoteEntry(new Version(2, 3, 21),
			"Настройки → Запуск: два режима старта с Windows — с окном без подключения или в фоне с подключением VPN. Режимы взаимоисключающие.",
			"В фоне проверяется последний использованный профиль; при отказе выбирается лучший из доступных по HTTPS-проверке. Если все недоступны, приложение остаётся в трее.",
			"Переключатели маршрутизации приложений стали аккуратнее: без квадратной рамки, с мягкой тенью и тремя различимыми положениями.",
			"Обновления: Настройки → Обновление приложения."),
		new ReleaseNoteEntry(new Version(2, 3, 20),
			"Главная → Все серверы на карте: Ctrl + колёсико приближает карту под курсором, перетаскивание левой кнопкой перемещает её.",
			"Добавлены кнопки +/− и «Вся карта» для возврата к исходному масштабу. Перетаскивание не выбирает сервер.",
			"Обновления: Настройки → Обновление приложения."),
		new ReleaseNoteEntry(new Version(2, 3, 19),
			"На карте зелёные точки обозначают свежий успешный HTTPS-тест, красные — неудачный, серые — отсутствие свежей проверки.",
			"Выбранный сервер проверяется первым; ошибки активного соединения отражаются на карте и в статусе интернета.",
			"Запущенный туннель больше не означает «Вы защищены»: доступ в интернет и HTTPS через профиль проверяются отдельно.",
			"Обновления: Настройки → Обновление приложения."),
		new ReleaseNoteEntry(new Version(2, 3, 18),
			"Главная → карта региона → Все серверы на карте: выбор точки или страны, список серверов и автоматическое переключение профиля.",
			"Яркие маркеры подтверждают два HTTPS-запроса через конкретный профиль; подсказки показывают задержку и свежесть проверки.",
			"Фоновые проверки выполняются без изменения действующего туннеля. Полные sing-box конфигурации проверяются при подключении.",
			"Обновления: Настройки → Обновление приложения."),
		new ReleaseNoteEntry(new Version(2, 3, 17),
			"Сайты из списка «Напрямую» и их поддомены используют отдельный прямой выход и локальный DNS без возврата на VPN.",
			"Прямой маршрут имеет приоритет над правилами браузера и VPN-доменов, также в импортированных конфигурациях.",
			"Для точного разделения сайтов отключите защищённый DNS браузера; отдельные CDN-домены добавляйте в «Напрямую» отдельно.",
			"Обновления: Настройки → Обновление приложения."),
		new ReleaseNoteEntry(new Version(2, 3, 12),
			"Добавлена реальная офлайн-карта: она выделяет страну и известный город выбранного сервера.",
			"Карта работает локально и не отправляет IP-адрес стороннему геолокационному сервису."),
		new ReleaseNoteEntry(new Version(2, 3, 13),
			"После обновления при первом запуске показывается сводка нововведений; пропущенные версии объединяются в одну сводку.",
			"В сводке есть переход к настройкам приложения, где можно скачать актуальный установщик."),
		new ReleaseNoteEntry(new Version(2, 3, 15),
			"Карта региона подключения теперь отображается в широкой раскладке во всех пяти профилях оформления.",
			"Цвет страны, маршрута и маркера автоматически берётся из штатной палитры выбранного профиля.",
			"Проверить обновления и открыть установщик можно в разделе Настройки → Обновление приложения.")
	};

	public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;

	public static string CurrentVersionText => CurrentVersion.ToString(3);

	public static IReadOnlyList<ReleaseNoteEntry> GetPendingNotes(string lastSeenVersion, Version currentVersion)
	{
		if (currentVersion == null) throw new ArgumentNullException(nameof(currentVersion));
		Version lastSeen;
		bool hasLastSeen = Version.TryParse(lastSeenVersion, out lastSeen);
		if (hasLastSeen && lastSeen.Revision < 0) lastSeen = new Version(lastSeen.Major, lastSeen.Minor, lastSeen.Build, 0);
		return Notes
			.Where(note => note.Version.CompareTo(currentVersion) <= 0 && (!hasLastSeen || note.Version.CompareTo(lastSeen) > 0))
			.OrderBy(note => note.Version)
			.ToList();
	}

	public static bool HasNotesFor(Version version)
	{
		return version != null && Notes.Any(note => Normalize(note.Version) == Normalize(version));
	}

	private static Version Normalize(Version version)
	{
		return version.Revision < 0 ? new Version(version.Major, version.Minor, version.Build, 0) : version;
	}
}
