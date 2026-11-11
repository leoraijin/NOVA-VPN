using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml;

namespace NovaVpn
{
    // Kept compatible with C# 5 so installer servicing can share the ownership rules.
    public sealed class StartupTaskSnapshot
    {
        public readonly string Name, Xml, Sddl;
        public StartupTaskSnapshot(string name, string xml, string sddl)
        {
            if (!WindowsStartupTaskPolicy.IsKnownName(name) || xml == null || xml.Length > 131072 || sddl == null || sddl.Length > 65536) throw new ArgumentException("Некорректный снимок задания автозапуска.");
            Name = name; Xml = xml; Sddl = sddl;
        }
        public string Fingerprint
        {
            get { return WindowsStartupTaskPolicy.Hash(Name.ToUpperInvariant() + "\0" + Xml + "\0" + Sddl); }
        }
    }
    public static class WindowsStartupTaskPolicy
    {
        public const string LegacyName = "NOVA VPN Startup";
        public const string Prefix = "NOVA VPN Startup - ";
        public const string Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        public static string NameForSid(string sid) { return Prefix + Hash(new SecurityIdentifier(sid).Value); }
        internal static string Hash(string text) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", ""); }
        public static bool IsKnownName(string name)
        {
            if (String.Equals(name, LegacyName, StringComparison.OrdinalIgnoreCase)) return true;
            if (name == null || !name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || name.Length != Prefix.Length + 64) return false;
            return name.Substring(Prefix.Length).All(c => c >= '0' && c <= '9' || c >= 'A' && c <= 'F' || c >= 'a' && c <= 'f');
        }
        public static string ExecutablePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.Length > 32760 || path.Any(Char.IsControl) || path.IndexOf('"') >= 0 || !Path.IsPathRooted(path) || path.Length < 4 || !(path[0] >= 'A' && path[0] <= 'Z' || path[0] >= 'a' && path[0] <= 'z') || path[1] != ':' || path[2] != '\\' && path[2] != '/' || path.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("Нужен абсолютный локальный путь приложения NOVA.");
            string full = Path.GetFullPath(path);
            if (!String.Equals(Path.GetFileName(full), "NOVA VPN.exe", StringComparison.OrdinalIgnoreCase) && !String.Equals(Path.GetFileName(full), "NOVA.VPN.exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Задание должно запускать приложение NOVA.");
            return full;
        }
        public static string PrivateSddl(string sid)
        {
            string user = new SecurityIdentifier(sid).Value;
            return "O:" + user + "G:" + user + "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;" + user + ")";
        }
        public static string BuildXml(string executable, string sid, bool connect)
        {
            executable = ExecutablePath(executable); sid = new SecurityIdentifier(sid).Value;
            var text = new StringBuilder();
            using (var xml = XmlWriter.Create(text, new XmlWriterSettings { Indent = true }))
            {
                xml.WriteStartElement("Task", Namespace); xml.WriteAttributeString("version", "1.2");
                xml.WriteStartElement("Triggers"); xml.WriteStartElement("LogonTrigger"); xml.WriteElementString("Enabled", "true"); xml.WriteElementString("UserId", sid); xml.WriteElementString("Delay", "PT10S"); xml.WriteEndElement(); xml.WriteEndElement();
                xml.WriteStartElement("Principals"); xml.WriteStartElement("Principal"); xml.WriteAttributeString("id", "Author"); xml.WriteElementString("UserId", sid); xml.WriteElementString("LogonType", "InteractiveToken"); xml.WriteElementString("RunLevel", "HighestAvailable"); xml.WriteEndElement(); xml.WriteEndElement();
                xml.WriteStartElement("Settings"); xml.WriteElementString("MultipleInstancesPolicy", "IgnoreNew"); xml.WriteElementString("DisallowStartIfOnBatteries", "false"); xml.WriteElementString("StopIfGoingOnBatteries", "false"); xml.WriteElementString("StartWhenAvailable", "true"); xml.WriteElementString("ExecutionTimeLimit", "PT0S"); xml.WriteEndElement();
                xml.WriteStartElement("Actions"); xml.WriteAttributeString("Context", "Author"); xml.WriteStartElement("Exec"); xml.WriteElementString("Command", executable); xml.WriteElementString("Arguments", connect ? "--windows-startup --background-connect" : "--windows-startup"); xml.WriteElementString("WorkingDirectory", Path.GetDirectoryName(executable)); xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
            }
            return text.ToString();
        }
        static XmlDocument Parse(string xml)
        {
            if (xml == null || xml.Length > 131072) throw new InvalidDataException("Слишком большое описание автозапуска.");
            var document = new XmlDocument(); document.XmlResolver = null;
            using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 131072 })) document.Load(reader);
            if (document.DocumentElement == null || document.DocumentElement.LocalName != "Task" || document.DocumentElement.NamespaceURI != Namespace) throw new InvalidDataException("Неизвестная схема задания.");
            foreach (XmlNode node in document.SelectNodes("//*")) if (node.NamespaceURI != Namespace) throw new InvalidDataException("Смешанная схема задания.");
            return document;
        }
        static XmlNamespaceManager Names(XmlDocument doc) { var ns = new XmlNamespaceManager(doc.NameTable); ns.AddNamespace("t", Namespace); return ns; }
        static XmlElement One(XmlDocument doc, XmlNamespaceManager ns, string path)
        { var nodes = doc.SelectNodes(path, ns); return nodes.Count == 1 ? nodes[0] as XmlElement : null; }
        static string Value(XmlDocument doc, XmlNamespaceManager ns, string path)
        { var node = One(doc, ns, path); return node == null || node.ChildNodes.Cast<XmlNode>().Any(n => n.NodeType == XmlNodeType.Element) ? null : node.InnerText; }
        static bool User(string value, string sid, string account)
        { return value == sid || !String.IsNullOrEmpty(account) && String.Equals(value, account, StringComparison.OrdinalIgnoreCase); }
        public static bool HasCurrentPrincipal(string xml, string sid, string account)
        { var doc = Parse(xml); return User(Value(doc, Names(doc), "/t:Task/t:Principals/t:Principal/t:UserId"), new SecurityIdentifier(sid).Value, account); }
        public static bool IsOwned(StartupTaskSnapshot task, string sid, string account, string executable, bool strict)
        {
            if (task == null) return false;
            sid = new SecurityIdentifier(sid).Value; executable = ExecutablePath(executable);
            if (!String.Equals(task.Name, strict ? NameForSid(sid) : LegacyName, StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                var doc = Parse(task.Xml); var ns = Names(doc);
                if (doc.DocumentElement.ChildNodes.Cast<XmlNode>().Where(n => n.NodeType == XmlNodeType.Element).Any(n => !new[] { "RegistrationInfo", "Triggers", "Principals", "Settings", "Actions" }.Contains(n.LocalName))) return false;
                if (doc.DocumentElement.ChildNodes.Cast<XmlNode>().Where(n => n.NodeType == XmlNodeType.Element).GroupBy(n => n.LocalName).Any(g => g.Count() != 1)) return false;
                var actions = One(doc, ns, "/t:Task/t:Actions"); var principal = One(doc, ns, "/t:Task/t:Principals/t:Principal"); var trigger = One(doc, ns, "/t:Task/t:Triggers/t:LogonTrigger");
                if (actions == null || principal == null || trigger == null || principal.GetAttribute("id") != "Author" || actions.GetAttribute("Context") != "Author" ||
                    doc.SelectNodes("/t:Task/t:Principals/*", ns).Count != 1 || doc.SelectNodes("/t:Task/t:Triggers/*", ns).Count != 1 || actions.ChildNodes.Cast<XmlNode>().Count(n => n.NodeType == XmlNodeType.Element) != 1 || One(doc, ns, "/t:Task/t:Actions/t:Exec") == null) return false;
                if (!Only(principal, "UserId,LogonType,RunLevel,DisplayName") || !Only(trigger, "UserId,Enabled,Delay,StartBoundary,EndBoundary,ExecutionTimeLimit") || !Only(One(doc, ns, "/t:Task/t:Actions/t:Exec"), "Command,Arguments,WorkingDirectory")) return false;
                if (!User(Value(doc, ns, "/t:Task/t:Principals/t:Principal/t:UserId"), sid, account) || !User(Value(doc, ns, "/t:Task/t:Triggers/t:LogonTrigger/t:UserId"), sid, account) ||
                    Value(doc, ns, "/t:Task/t:Principals/t:Principal/t:LogonType") != "InteractiveToken" || Value(doc, ns, "/t:Task/t:Principals/t:Principal/t:RunLevel") != "HighestAvailable") return false;
                string command = Value(doc, ns, "/t:Task/t:Actions/t:Exec/t:Command");
                string directory = Value(doc, ns, "/t:Task/t:Actions/t:Exec/t:WorkingDirectory");
                string arguments = Value(doc, ns, "/t:Task/t:Actions/t:Exec/t:Arguments");
                if (arguments != "--windows-startup" && arguments != "--windows-startup --background-connect") return false;
                if (!String.Equals(ExecutablePath(command), executable, StringComparison.OrdinalIgnoreCase) || directory == null || !String.Equals(Path.GetFullPath(directory), Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase)) return false;
                ValidateSecurity(task.Sddl, sid, strict); return true;
            }
            catch (ArgumentException) { return false; } catch (XmlException) { return false; } catch (InvalidDataException) { return false; } catch (UnauthorizedAccessException) { return false; }
        }
        static bool Only(XmlElement parent, string names)
        {
            if (parent == null) return false;
            var allowed = new HashSet<string>(names.Split(',')); var seen = new HashSet<string>();
            foreach (XmlNode child in parent.ChildNodes) if (child.NodeType == XmlNodeType.Element && (!allowed.Contains(child.LocalName) || !seen.Add(child.LocalName))) return false;
            return true;
        }
        public static void ValidateSecurity(string sddl, string sid, bool strict)
        {
            var user = new SecurityIdentifier(sid); var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null); var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var security = new RawSecurityDescriptor(sddl);
            if (security.Owner == null || !security.Owner.Equals(user) && (strict || !security.Owner.Equals(system) && !security.Owner.Equals(admin)) || security.DiscretionaryAcl == null ||
                strict && (security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0) throw new UnauthorizedAccessException("Недоверенный владелец задания автозапуска.");
            bool userFull = false, systemFull = false, adminFull = false; int count = 0; var seen = new HashSet<string>();
            foreach (GenericAce value in security.DiscretionaryAcl)
            {
                var ace = value as CommonAce; count++;
                if (ace == null || ace.IsCallback || ace.AceQualifier != AceQualifier.AccessAllowed || !ace.SecurityIdentifier.Equals(user) && !ace.SecurityIdentifier.Equals(system) && !ace.SecurityIdentifier.Equals(admin) || !seen.Add(ace.SecurityIdentifier.Value)) throw new UnauthorizedAccessException("Чужие права задания автозапуска.");
                bool full = ace.AccessMask == 0x10000000 || ace.AccessMask == 0x1F01FF;
                if (strict && (!full || ace.AceFlags != AceFlags.None)) throw new UnauthorizedAccessException("Неполные права задания автозапуска.");
                if (ace.SecurityIdentifier.Equals(user) && full) userFull = true;
                if (ace.SecurityIdentifier.Equals(system) && full) systemFull = true;
                if (ace.SecurityIdentifier.Equals(admin) && full) adminFull = true;
            }
            if (strict && (!userFull || !systemFull || count != 2 && !(count == 3 && adminFull))) throw new UnauthorizedAccessException("Неполные права задания автозапуска.");
        }
        public static string SecurityKey(string sddl)
        {
            var security = new RawSecurityDescriptor(sddl);
            if (security.DiscretionaryAcl == null) throw new UnauthorizedAccessException("Отсутствуют права задания.");
            foreach (GenericAce value in security.DiscretionaryAcl)
            { var ace = value as CommonAce; if (ace != null && ace.AccessMask == 0x10000000) ace.AccessMask = 0x1F01FF; }
            return security.GetSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        }
        public static string DefinitionKey(string xml, string sid, string account)
        {
            var doc = Parse(xml); var ns = Names(doc);
            foreach (XmlNode registration in doc.SelectNodes("/t:Task/t:RegistrationInfo", ns).Cast<XmlNode>().ToArray())
            {
                // Scheduler-generated identity/time metadata may change on registration.
                // Keep description/source/security and unknown metadata so a concurrent
                // user edit cannot be mistaken for our own write during rollback.
                foreach (XmlNode child in registration.ChildNodes.Cast<XmlNode>().Where(n => n.NodeType == XmlNodeType.Element && new[] { "Date", "Author", "URI" }.Contains(n.LocalName)).ToArray()) registration.RemoveChild(child);
                if (!registration.ChildNodes.Cast<XmlNode>().Any(n => n.NodeType == XmlNodeType.Element) && registration.Attributes.Count == 0) registration.ParentNode.RemoveChild(registration);
            }
            foreach (string path in new[] { "/t:Task/t:Principals/t:Principal/t:UserId", "/t:Task/t:Triggers/t:LogonTrigger/t:UserId" })
            { var node = One(doc, ns, path); if (node != null && User(node.InnerText, sid, account)) node.InnerText = sid; }
            return doc.DocumentElement.OuterXml;
        }
    }
}
