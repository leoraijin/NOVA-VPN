using System;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace NovaVpn;

// Installed by an elevated installer from its authenticated user's token. No IPC
// request can select or replace the owner. Group SIDs never match pipe TokenUser.
public sealed class GuardOwnerPolicy
{
    public string OwnerSid { get; }
    public Guid InstallationId { get; }
    public GuardOwnerPolicy(string ownerSid, Guid installationId)
    {
        OwnerSid = new SecurityIdentifier(ownerSid).Value;
        if (installationId == Guid.Empty) throw new FormatException("Missing guard installation ID.");
        InstallationId = installationId;
    }
    public byte[] Encode()
    {
        var sid = new SecurityIdentifier(OwnerSid); byte[] bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        { writer.Write(Encoding.ASCII.GetBytes("NOVAGRD1")); writer.Write(InstallationId.ToByteArray()); writer.Write((ushort)bytes.Length); writer.Write(bytes); return stream.ToArray(); }
    }
    public static GuardOwnerPolicy Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 34 || bytes.Length > 94) throw new FormatException("Invalid owner policy size.");
        using (var stream = new MemoryStream(bytes, false))
        using (var reader = new BinaryReader(stream))
        {
            if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "NOVAGRD1") throw new FormatException("Invalid owner policy schema.");
            var installation = new Guid(reader.ReadBytes(16)); int length = reader.ReadUInt16();
            if (length < 8 || length > 68 || stream.Length - stream.Position != length) throw new FormatException("Invalid owner SID length.");
            try
            {
                var sid = new SecurityIdentifier(reader.ReadBytes(length), 0);
                if (sid.BinaryLength != length) throw new FormatException("Trailing owner policy bytes.");
                return new GuardOwnerPolicy(sid.Value, installation);
            }
            catch (ArgumentException error) { throw new FormatException("Invalid owner SID.", error); }
        }
    }
}
