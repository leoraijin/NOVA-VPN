using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace NovaVpn;

public static class RoutingListImporter
{
	public static List<string> Parse(IEnumerable<string> lines)
	{
		List<string> result = new List<string>();
		foreach (string source in lines ?? Enumerable.Empty<string>())
		{
			string line = (source ?? "").Trim();
			if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("!"))
			{
				continue;
			}
			int comment = line.IndexOf('#');
			if (comment >= 0) line = line.Substring(0, comment).Trim();
			string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
			IPAddress ignored;
			if (parts.Length >= 2 && IPAddress.TryParse(parts[0], out ignored))
			{
				line = parts[1];
			}
			string domain = RoutingInspector.NormalizeDomainRule(line);
			if (!string.IsNullOrWhiteSpace(domain) && !result.Contains(domain, StringComparer.OrdinalIgnoreCase))
			{
				result.Add(domain);
			}
		}
		return result;
	}
}
