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
