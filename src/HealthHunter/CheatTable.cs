using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HealthHunter;

/// <summary>
/// Reads and writes Cheat Engine tables (.CT). Only plain pointer/address entries are supported:
/// script entries (Auto Assembler, Lua) and addresses built from table symbols are skipped.
/// </summary>
public static class CheatTable
{
    public sealed class ImportResult
    {
        public List<TableEntry> Entries = new();
        public int SkippedScripts;
        public int SkippedUnsupported;
    }

    private static readonly Regex ModuleAddr = new(@"^\s*""?(?<mod>[^""+\[\]]+?\.(exe|dll))""?\s*\+\s*(0x)?(?<off>[0-9A-Fa-f]+)\s*$", RegexOptions.IgnoreCase);

    public static ImportResult Import(string path)
    {
        var res = new ImportResult();
        var doc = XDocument.Load(path);
        foreach (var ce in doc.Descendants("CheatEntry"))
        {
            var typeText = (string)ce.Element("VariableType");
            if (string.Equals(typeText, "Auto Assembler Script", StringComparison.OrdinalIgnoreCase)) { res.SkippedScripts++; continue; }
            var addrText = (string)ce.Element("Address");
            if (typeText == null || addrText == null) continue; // group header
            var kind = ParseType(typeText);
            var m = ModuleAddr.Match(addrText);
            if (kind == null || !m.Success) { res.SkippedUnsupported++; continue; }

            // CE lists offsets from the value back to the base, so reverse them into apply order.
            var offs = ce.Element("Offsets")?.Elements("Offset")
                         .Select(o => ParseHex((string)o)).Reverse().ToList() ?? new List<long?>();
            if (offs.Any(o => o == null)) { res.SkippedUnsupported++; continue; }

            string name = ((string)ce.Element("Description") ?? "Entry").Trim().Trim('"');
            // Nested inside a group header → keep the header's name as the group.
            var parent = ce.Parent?.Parent;
            string group = parent?.Name == "CheatEntry" ? ((string)parent.Element("Description") ?? "").Trim().Trim('"') : "";
            res.Entries.Add(new TableEntry
            {
                Name = name,
                Group = group,
                Kind = kind.Value,
                Chain = new PointerChain
                {
                    Module = m.Groups["mod"].Value.Trim(),
                    ModuleOffset = long.Parse(m.Groups["off"].Value, NumberStyles.HexNumber),
                    Offsets = offs.Select(o => o.Value).ToList()
                },
                Mode = FreezeMode.FreezeCurrent
            });
        }
        return res;
    }

    public static void Export(string path, IEnumerable<TableEntry> entries)
    {
        int id = 0;
        var list = new XElement("CheatEntries");
        var groups = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries.Where(e => e.Chain != null))
        {
            var parent = list;
            if (!string.IsNullOrWhiteSpace(e.Group))
            {
                if (!groups.TryGetValue(e.Group, out var children))
                {
                    children = new XElement("CheatEntries");
                    list.Add(new XElement("CheatEntry",
                        new XElement("ID", id++),
                        new XElement("Description", $"\"{e.Group}\""),
                        new XElement("Options", new XAttribute("moHideChildren", "0")),
                        new XElement("GroupHeader", 1),
                        children));
                    groups[e.Group] = children;
                }
                parent = children;
            }
            var ce = new XElement("CheatEntry",
                new XElement("ID", id++),
                new XElement("Description", $"\"{e.Name}\""),
                new XElement("VariableType", TypeName(e.Kind)),
                new XElement("Address", $"\"{e.Chain.Module}\"+{e.Chain.ModuleOffset:X}"));
            if (e.Chain.Offsets.Count > 0)
                ce.Add(new XElement("Offsets", Enumerable.Reverse(e.Chain.Offsets).Select(o => new XElement("Offset", o < 0 ? "-" + (-o).ToString("X") : o.ToString("X")))));
            parent.Add(ce);
        }
        new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("CheatTable", new XAttribute("CheatEngineTableVersion", "45"), list)).Save(path);
    }

    private static ValueKind? ParseType(string t) => t.Trim().ToLowerInvariant() switch
    {
        "byte" => ValueKind.Byte,
        "4 bytes" => ValueKind.Int32,
        "2 bytes" => ValueKind.Int16,
        "8 bytes" => ValueKind.Int64,
        "float" => ValueKind.Float,
        "double" => ValueKind.Double,
        _ => null
    };

    private static string TypeName(ValueKind k) => k switch
    {
        ValueKind.Byte => "Byte",
        ValueKind.Int16 or ValueKind.UInt16 => "2 Bytes",
        ValueKind.Int64 => "8 Bytes",
        ValueKind.Float => "Float",
        ValueKind.Double => "Double",
        _ => "4 Bytes"
    };

    private static long? ParseHex(string s)
    {
        s = (s ?? "").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        bool neg = s.StartsWith('-');
        if (neg) s = s[1..];
        return long.TryParse(s, NumberStyles.HexNumber, null, out var v) ? (neg ? -v : v) : null;
    }
}
