namespace NovaVpn;

public sealed class DiagnosticItem
{
	public string Name { get; set; }

	public bool Success { get; set; }

	public string Details { get; set; }

	public override string ToString()
	{
		return (Success ? "[OK] " : "[!] ") + Name + ": " + Details;
	}
}
