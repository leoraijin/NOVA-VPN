using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfPath = System.Windows.Shapes.Path;

namespace NovaVpn;

public sealed class ServerRegionSelection
{
	public string IsoCode { get; internal set; } = "";
	public string CountryName { get; internal set; } = "";
	public string CityName { get; internal set; } = "";
	public bool HasCountry { get; internal set; }
	public bool IsEuropeRegion { get; internal set; }
	public bool HasCityCoordinates { get; internal set; }
	public double Longitude { get; internal set; }
	public double Latitude { get; internal set; }
	internal MapCountry Country { get; set; }
}

internal sealed class MapCountry
{
	public string IsoCode;
	public string Iso3;
	public string Name;
	public string EnglishName;
	public string Continent;
	public double LabelLongitude;
	public double LabelLatitude;
	public readonly List<List<GeoPoint>> Outlines = new List<List<GeoPoint>>();
}

internal sealed class MapCity
{
	public string Name;
	public string EnglishName;
	public string Iso3;
	public double Longitude;
	public double Latitude;
}

internal struct GeoPoint
{
	public double Longitude;
	public double Latitude;

	public GeoPoint(double longitude, double latitude)
	{
		Longitude = longitude;
		Latitude = latitude;
	}
}

public static class ServerRegionMapService
{
	private const string CountryResource = "NovaVpn.Assets.ne_110m_admin_0_countries.geojson";
	private const string CityResource = "NovaVpn.Assets.ne_50m_populated_places.geojson";
	private static readonly Lazy<List<MapCountry>> Countries = new Lazy<List<MapCountry>>(LoadCountries, true);
	private static readonly Lazy<List<MapCity>> Cities = new Lazy<List<MapCity>>(LoadCities, true);
	private static readonly Regex IsoToken = new Regex(@"(?<![A-Za-z])([A-Za-z]{2})(?![A-Za-z])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public static ServerRegionSelection Resolve(VpnProfile profile)
	{
		List<MapCountry> countries = Countries.Value;
		string profileName = profile?.Name ?? "";
		Match codeMatch = IsoToken.Match(profileName);
		string requestedCode = codeMatch.Success ? codeMatch.Groups[1].Value.ToUpperInvariant() : "";
		if (requestedCode == "UK") requestedCode = "GB";

		MapCountry country = null;
		if (requestedCode.Length == 2 && requestedCode != "EU")
			country = countries.FirstOrDefault(item => string.Equals(item.IsoCode, requestedCode, StringComparison.OrdinalIgnoreCase));

		MapCity city = FindCity(profileName, country, Cities.Value);
		if (city != null)
		{
			if (country == null || !string.Equals(country.Iso3, city.Iso3, StringComparison.OrdinalIgnoreCase))
				country = countries.FirstOrDefault(item => string.Equals(item.Iso3, city.Iso3, StringComparison.OrdinalIgnoreCase));
		}

		if (country == null && requestedCode != "EU")
		{
			country = FindCountryByName(profileName, countries);
			if (country != null && city == null)
				city = FindCity(profileName, country, Cities.Value);
		}

		if (country != null)
		{
			return new ServerRegionSelection
			{
				IsoCode = country.IsoCode,
				CountryName = country.Name,
				CityName = city?.Name ?? "",
				HasCountry = true,
				HasCityCoordinates = city != null,
				Longitude = city?.Longitude ?? country.LabelLongitude,
				Latitude = city?.Latitude ?? country.LabelLatitude,
				Country = country
			};
		}

		if (requestedCode == "EU" || profileName.IndexOf("Europe", StringComparison.OrdinalIgnoreCase) >= 0 || profileName.IndexOf("Европ", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return new ServerRegionSelection
			{
				IsoCode = "EU",
				CountryName = "Европа",
				IsEuropeRegion = true,
				Longitude = 15.0,
				Latitude = 53.0
			};
		}

		return new ServerRegionSelection { CountryName = "Регион не указан в профиле" };
	}

	public static Canvas BuildCanvas(ServerRegionSelection selection, double width, double height, Color landColor, Color selectedColor, Color borderColor, Color markerColor)
	{
		width = Math.Max(120, width);
		height = Math.Max(80, height);
		Canvas canvas = new Canvas { Width = width, Height = height, ClipToBounds = true, IsHitTestVisible = false, Tag = "offline-geographic-map" };
		try
		{
			List<MapCountry> countries = Countries.Value;
			Viewport viewport = CreateViewport(selection, width, height);
			foreach (MapCountry country in countries)
			{
				bool highlighted = IsHighlighted(country, selection);
				StreamGeometry geometry = CreateGeometry(country, viewport, width, height);
				if (geometry == null || geometry.Bounds.IsEmpty) continue;
				Rect bounds = geometry.Bounds;
				if (bounds.Right < -8 || bounds.Bottom < -8 || bounds.Left > width + 8 || bounds.Top > height + 8) continue;
				WpfPath shape = new WpfPath
				{
					Data = geometry,
					Fill = new SolidColorBrush(highlighted ? selectedColor : landColor),
					Stroke = new SolidColorBrush(highlighted ? markerColor : borderColor),
					StrokeThickness = highlighted ? 1.0 : 0.55,
					Opacity = highlighted ? 0.98 : 0.86,
					Tag = country.IsoCode
				};
				if (geometry.CanFreeze) geometry.Freeze();
				Canvas.SetZIndex(shape, highlighted ? 2 : 1);
				canvas.Children.Add(shape);
			}

			if (selection != null && (selection.HasCountry || selection.HasCityCoordinates))
				AddLocationMarker(canvas, Project(selection.Longitude, selection.Latitude, viewport), markerColor, selection.HasCityCoordinates ? "server-city-marker" : "server-country-marker");
		}
		catch (Exception ex)
		{
			canvas.Children.Clear();
			canvas.Tag = "offline-geographic-map-unavailable";
			canvas.ToolTip = "Не удалось отобразить встроенную офлайн-карту: " + ex.Message;
		}
		return canvas;
	}

	public static Canvas BuildThemedCanvas(ServerRegionSelection selection, double width, double height, ThemePalette palette)
	{
		if (palette == null) throw new ArgumentNullException(nameof(palette));
		Color land = Color.FromArgb(58, palette.Accent.R, palette.Accent.G, palette.Accent.B);
		Color selected = Color.FromArgb(190, palette.Accent.R, palette.Accent.G, palette.Accent.B);
		Color outline = Color.FromArgb(110, palette.Text.R, palette.Text.G, palette.Text.B);
		return BuildCanvas(selection, width, height, land, selected, outline, palette.AccentLight);
	}

	private static bool IsHighlighted(MapCountry country, ServerRegionSelection selection)
	{
		if (selection == null) return false;
		if (selection.IsEuropeRegion) return string.Equals(country.Continent, "Europe", StringComparison.OrdinalIgnoreCase);
		return selection.HasCountry && string.Equals(country.IsoCode, selection.IsoCode, StringComparison.OrdinalIgnoreCase);
	}

	private static Viewport CreateViewport(ServerRegionSelection selection, double width, double height)
	{
		double centerLongitude = selection?.Longitude ?? 0.0;
		double centerLatitude = selection?.Latitude ?? 20.0;
		double halfLongitudeProjected;
		double halfLatitude;
		bool globalView = selection == null || (!selection.HasCountry && !selection.IsEuropeRegion);

		if (globalView)
		{
			centerLongitude = 0;
			centerLatitude = 10;
			halfLongitudeProjected = 180;
			halfLatitude = 90;
		}
		else if (selection.IsEuropeRegion)
		{
			centerLongitude = 15;
			centerLatitude = 53;
			halfLongitudeProjected = 38 * Math.Cos(centerLatitude * Math.PI / 180.0);
			halfLatitude = 27;
		}
		else
		{
			double maxX = 0;
			double maxY = 0;
			foreach (List<GeoPoint> ring in selection.Country.Outlines)
			{
				foreach (GeoPoint point in ring)
				{
					double dx = Math.Abs(ShortestLongitude(point.Longitude, centerLongitude)) * Math.Cos(centerLatitude * Math.PI / 180.0);
					double dy = Math.Abs(point.Latitude - centerLatitude);
					if (dx <= 42 && dy <= 36)
					{
						maxX = Math.Max(maxX, dx);
						maxY = Math.Max(maxY, dy);
					}
				}
			}
			halfLongitudeProjected = Math.Max(5.5, maxX * 1.65);
			halfLatitude = Math.Max(4.5, maxY * 1.65);
		}

		double usableWidth = Math.Max(1, width - 18);
		double usableHeight = Math.Max(1, height - 18);
		double scale = Math.Min(usableWidth / Math.Max(1, halfLongitudeProjected * 2), usableHeight / Math.Max(1, halfLatitude * 2));
		return new Viewport
		{
			CenterLongitude = centerLongitude,
			CenterLatitude = centerLatitude,
			LongitudeScale = Math.Cos(centerLatitude * Math.PI / 180.0) * scale,
			LatitudeScale = scale,
			OffsetX = width / 2,
			OffsetY = height / 2
		};
	}

	private static StreamGeometry CreateGeometry(MapCountry country, Viewport viewport, double width, double height)
	{
		StreamGeometry geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
		bool hasFigures = false;
		using (StreamGeometryContext context = geometry.Open())
		{
			foreach (List<GeoPoint> ring in country.Outlines)
			{
				if (ring.Count < 4) continue;
				Point[] points = ProjectRing(ring, viewport);
				if (points.Length < 4) continue;
				double minX = points.Min(point => point.X);
				double maxX = points.Max(point => point.X);
				double minY = points.Min(point => point.Y);
				double maxY = points.Max(point => point.Y);
				if (maxX < -8 || maxY < -8 || minX > width + 8 || minY > height + 8) continue;
				context.BeginFigure(points[0], true, true);
				context.PolyLineTo(points.Skip(1).ToArray(), true, true);
				hasFigures = true;
			}
		}
		if (!hasFigures) return null;
		if (geometry.CanFreeze) geometry.Freeze();
		return geometry;
	}

	private static Point[] ProjectRing(List<GeoPoint> ring, Viewport viewport)
	{
		List<double> longitudes = new List<double>(ring.Count);
		double first = viewport.CenterLongitude + ShortestLongitude(ring[0].Longitude, viewport.CenterLongitude);
		longitudes.Add(first);
		double previousRaw = ring[0].Longitude;
		double previousUnwrapped = first;
		for (int index = 1; index < ring.Count; index++)
		{
			double delta = ring[index].Longitude - previousRaw;
			if (delta > 180) delta -= 360;
			else if (delta < -180) delta += 360;
			previousUnwrapped += delta;
			longitudes.Add(previousUnwrapped);
			previousRaw = ring[index].Longitude;
		}
		double average = longitudes.Average();
		double shift = Math.Floor((viewport.CenterLongitude - average) / 360.0 + 0.5) * 360.0;
		Point[] result = new Point[ring.Count];
		for (int index = 0; index < ring.Count; index++)
		{
			double x = viewport.OffsetX + (longitudes[index] + shift - viewport.CenterLongitude) * viewport.LongitudeScale;
			double y = viewport.OffsetY - (ring[index].Latitude - viewport.CenterLatitude) * viewport.LatitudeScale;
			result[index] = new Point(x, y);
		}
		return result;
	}

	private static Point Project(double longitude, double latitude, Viewport viewport)
	{
		return new Point(
			viewport.OffsetX + ShortestLongitude(longitude, viewport.CenterLongitude) * viewport.LongitudeScale,
			viewport.OffsetY - (latitude - viewport.CenterLatitude) * viewport.LatitudeScale);
	}

	private static void AddLocationMarker(Canvas canvas, Point point, Color color, string tag)
	{
		if (point.X < -6 || point.Y < -6 || point.X > canvas.Width + 6 || point.Y > canvas.Height + 6) return;
		Ellipse halo = new Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(Color.FromArgb(54, color.R, color.G, color.B)), Tag = tag + "-halo" };
		Canvas.SetLeft(halo, point.X - 9);
		Canvas.SetTop(halo, point.Y - 9);
		Canvas.SetZIndex(halo, 20);
		canvas.Children.Add(halo);
		Ellipse dot = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(color), Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1.4, Tag = tag };
		Canvas.SetLeft(dot, point.X - 3.5);
		Canvas.SetTop(dot, point.Y - 3.5);
		Canvas.SetZIndex(dot, 21);
		canvas.Children.Add(dot);
	}

	private static MapCity FindCity(string profileName, MapCountry country, List<MapCity> cities)
	{
		if (string.IsNullOrWhiteSpace(profileName)) return null;
		MapCity[] candidates = cities
			.Where(city => country == null || string.IsNullOrWhiteSpace(city.Iso3) || string.Equals(city.Iso3, country.Iso3, StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(city => Math.Max((city.Name ?? "").Length, (city.EnglishName ?? "").Length))
			.ToArray();
		foreach (MapCity city in candidates)
		{
			if (MatchesName(profileName, city.Name) || MatchesName(profileName, city.EnglishName)) return city;
		}
		return null;
	}

	private static MapCountry FindCountryByName(string profileName, List<MapCountry> countries)
	{
		return countries
			.Where(country => !string.IsNullOrWhiteSpace(country.Name) || !string.IsNullOrWhiteSpace(country.EnglishName))
			.OrderByDescending(country => Math.Max((country.Name ?? "").Length, (country.EnglishName ?? "").Length))
			.FirstOrDefault(country => MatchesName(profileName, country.Name) || MatchesName(profileName, country.EnglishName));
	}

	private static bool MatchesName(string source, string candidate)
	{
		if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(candidate) || candidate.Length < 3) return false;
		return Regex.IsMatch(source, @"(?<![\p{L}\p{N}])" + Regex.Escape(candidate.Trim()) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	}

	private static List<MapCountry> LoadCountries()
	{
		Dictionary<string, object> root = ReadGeoJson(CountryResource);
		object[] features = root["features"] as object[] ?? new object[0];
		List<MapCountry> countries = new List<MapCountry>(features.Length);
		foreach (object featureObject in features)
		{
			Dictionary<string, object> feature = featureObject as Dictionary<string, object>;
			if (feature == null) continue;
			Dictionary<string, object> properties = feature["properties"] as Dictionary<string, object>;
			Dictionary<string, object> geometry = feature["geometry"] as Dictionary<string, object>;
			if (properties == null || geometry == null) continue;
			string iso = Text(properties, "ISO_A2");
			if (iso.Length != 2 || !char.IsLetter(iso[0]) || !char.IsLetter(iso[1])) iso = Text(properties, "ISO_A2_EH");
			if (iso.Length != 2 || !char.IsLetter(iso[0]) || !char.IsLetter(iso[1])) iso = "";
			MapCountry country = new MapCountry
			{
				IsoCode = iso.ToUpperInvariant(),
				Iso3 = Text(properties, "ADM0_A3"),
				Name = FirstNonEmpty(Text(properties, "NAME_RU"), Text(properties, "NAME"), Text(properties, "ADMIN")),
				EnglishName = FirstNonEmpty(Text(properties, "NAME_EN"), Text(properties, "NAME"), Text(properties, "ADMIN")),
				Continent = Text(properties, "CONTINENT"),
				LabelLongitude = Number(properties, "LABEL_X"),
				LabelLatitude = Number(properties, "LABEL_Y")
			};
			string geometryType = Text(geometry, "type");
			object[] coordinates = geometry["coordinates"] as object[];
			if (geometryType == "Polygon") AddPolygon(country, coordinates);
			else if (geometryType == "MultiPolygon" && coordinates != null)
				foreach (object polygon in coordinates) AddPolygon(country, polygon as object[]);
			if (country.Outlines.Count > 0) countries.Add(country);
		}
		return countries;
	}

	private static void AddPolygon(MapCountry country, object[] rings)
	{
		if (rings == null || rings.Length == 0) return;
		object[] coordinates = rings[0] as object[];
		if (coordinates == null) return;
		List<GeoPoint> outline = new List<GeoPoint>(coordinates.Length);
		foreach (object coordinateObject in coordinates)
		{
			object[] coordinate = coordinateObject as object[];
			if (coordinate == null || coordinate.Length < 2) continue;
			outline.Add(new GeoPoint(Convert.ToDouble(coordinate[0], CultureInfo.InvariantCulture), Convert.ToDouble(coordinate[1], CultureInfo.InvariantCulture)));
		}
		if (outline.Count >= 4) country.Outlines.Add(outline);
	}

	private static List<MapCity> LoadCities()
	{
		Dictionary<string, object> root = ReadGeoJson(CityResource);
		object[] features = root["features"] as object[] ?? new object[0];
		List<MapCity> cities = new List<MapCity>(features.Length);
		foreach (object featureObject in features)
		{
			Dictionary<string, object> feature = featureObject as Dictionary<string, object>;
			if (feature == null) continue;
			Dictionary<string, object> properties = feature["properties"] as Dictionary<string, object>;
			Dictionary<string, object> geometry = feature["geometry"] as Dictionary<string, object>;
			object[] coordinates = geometry?["coordinates"] as object[];
			if (properties == null || coordinates == null || coordinates.Length < 2) continue;
			cities.Add(new MapCity
			{
				Name = FirstNonEmpty(Text(properties, "NAME_RU"), Text(properties, "NAME")),
				EnglishName = FirstNonEmpty(Text(properties, "NAMEASCII"), Text(properties, "NAME")),
				Iso3 = Text(properties, "ADM0_A3"),
				Longitude = Convert.ToDouble(coordinates[0], CultureInfo.InvariantCulture),
				Latitude = Convert.ToDouble(coordinates[1], CultureInfo.InvariantCulture)
			});
		}
		return cities;
	}

	private static Dictionary<string, object> ReadGeoJson(string resourceName)
	{
		using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
		{
			if (stream == null) throw new InvalidDataException("Не найден встроенный ресурс карты " + resourceName);
			using (StreamReader reader = new StreamReader(stream))
			{
				JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 100 };
				return serializer.DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>
					?? throw new InvalidDataException("Некорректный GeoJSON " + resourceName);
			}
		}
	}

	private static string Text(Dictionary<string, object> values, string key)
	{
		return values.TryGetValue(key, out object value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
	}

	private static double Number(Dictionary<string, object> values, string key)
	{
		return values.TryGetValue(key, out object value) && value != null ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : 0;
	}

	private static string FirstNonEmpty(params string[] values)
	{
		return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
	}

	private static double ShortestLongitude(double longitude, double center)
	{
		double delta = (longitude - center) % 360.0;
		if (delta > 180) delta -= 360;
		if (delta < -180) delta += 360;
		return delta;
	}

	private sealed class Viewport
	{
		public double CenterLongitude;
		public double CenterLatitude;
		public double LongitudeScale;
		public double LatitudeScale;
		public double OffsetX;
		public double OffsetY;
	}
}
