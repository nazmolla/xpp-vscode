using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace XppVsCode.Server.Tracing
{
    internal sealed class EventField
    {
        public string Name;
        public string InType;
    }

    internal sealed class EventDefinition
    {
        public string Provider;
        public string Name;
        public EventField[] Fields;

        public int IndexOf(string field) => Array.FindIndex(Fields, f => string.Equals(f.Name, field, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Event layouts from instrumentation manifests (.man): provider GUID + event id to the event's name and fields, and the
    /// decoder for the fields' binary payload.
    /// </summary>
    internal sealed class EventManifest
    {
        private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events";
        private readonly Dictionary<(Guid, ushort), EventDefinition> _events = new Dictionary<(Guid, ushort), EventDefinition>();

        public int Count => _events.Count;

        public EventDefinition Find(Guid provider, ushort id) => _events.TryGetValue((provider, id), out var d) ? d : null;

        /// <summary>Every *.man file of the folder; unreadable files are skipped.</summary>
        public static EventManifest Load(string folder)
        {
            var manifest = new EventManifest();
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                foreach (string file in Directory.EnumerateFiles(folder, "*.man"))
                {
                    try
                    {
                        manifest.Add(XDocument.Load(file));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Xml.XmlException || ex is FormatException)
                    {
                        Log.Write("Skipping manifest " + file + ": " + ex.Message);
                    }
                }
            return manifest;
        }

        internal void Add(XDocument doc)
        {
            foreach (var provider in doc.Descendants(Ns + "provider"))
            {
                var guid = Guid.Parse((string)provider.Attribute("guid"));
                string providerName = (string)provider.Attribute("name");
                var templates = provider.Descendants(Ns + "template").ToDictionary(
                    t => (string)t.Attribute("tid"),
                    t => t.Elements(Ns + "data").Select(d => new EventField { Name = (string)d.Attribute("name"), InType = (string)d.Attribute("inType") }).ToArray());
                foreach (var ev in provider.Descendants(Ns + "event"))
                {
                    string tid = (string)ev.Attribute("template");
                    _events[(guid, ushort.Parse((string)ev.Attribute("value")))] = new EventDefinition
                    {
                        Provider = providerName,
                        Name = (string)ev.Attribute("symbol") ?? (string)ev.Attribute("value"),
                        Fields = tid != null && templates.TryGetValue(tid, out var fields) ? fields : new EventField[0],
                    };
                }
            }
        }

        /// <summary>The first <paramref name="fieldCount"/> field values (null after a field of an unknown type or past the payload).</summary>
        public static object[] Decode(EtwEvent e, EventDefinition definition, int fieldCount)
        {
            int count = Math.Min(fieldCount, definition.Fields.Length);
            var values = new object[count];
            int offset = 0, length = e.UserDataLength;
            IntPtr p = e.UserData;
            for (int i = 0; i < count; i++)
            {
                switch (definition.Fields[i].InType)
                {
                    case "win:UnicodeString":
                        int start = offset;
                        while (offset + 1 < length && Marshal.ReadInt16(p, offset) != 0) offset += 2;
                        if (offset + 1 >= length) return values;
                        values[i] = Marshal.PtrToStringUni(p + start, (offset - start) / 2);
                        offset += 2;
                        break;
                    case "win:AnsiString":
                        int ansiStart = offset;
                        while (offset < length && Marshal.ReadByte(p, offset) != 0) offset++;
                        if (offset >= length) return values;
                        values[i] = Marshal.PtrToStringAnsi(p + ansiStart, offset - ansiStart);
                        offset++;
                        break;
                    default:
                        int size = SizeOf(definition.Fields[i].InType, e.Is64Bit);
                        if (size == 0 || offset + size > length) return values;
                        values[i] = ReadFixed(definition.Fields[i].InType, p, offset);
                        offset += size;
                        break;
                }
            }
            return values;
        }

        private static int SizeOf(string inType, bool is64Bit)
        {
            switch (inType)
            {
                case "win:Int8": case "win:UInt8": return 1;
                case "win:Int16": case "win:UInt16": return 2;
                case "win:Int32": case "win:UInt32": case "win:HexInt32": case "win:Float": case "win:Boolean": return 4;
                case "win:Int64": case "win:UInt64": case "win:HexInt64": case "win:Double": case "win:FILETIME": return 8;
                case "win:GUID": case "win:SYSTEMTIME": return 16;
                case "win:Pointer": return is64Bit ? 8 : 4;
                default: return 0;
            }
        }

        private static object ReadFixed(string inType, IntPtr p, int offset)
        {
            switch (inType)
            {
                case "win:Int8": return (sbyte)Marshal.ReadByte(p, offset);
                case "win:UInt8": return Marshal.ReadByte(p, offset);
                case "win:Int16": return Marshal.ReadInt16(p, offset);
                case "win:UInt16": return (ushort)Marshal.ReadInt16(p, offset);
                case "win:Int32": return Marshal.ReadInt32(p, offset);
                case "win:UInt32": case "win:HexInt32": return (uint)Marshal.ReadInt32(p, offset);
                case "win:Boolean": return Marshal.ReadInt32(p, offset) != 0;
                case "win:Float": return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(p, offset)), 0);
                case "win:Int64": case "win:FILETIME": return Marshal.ReadInt64(p, offset);
                case "win:UInt64": case "win:HexInt64": return (ulong)Marshal.ReadInt64(p, offset);
                case "win:Double": return BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, offset));
                case "win:Pointer": return Marshal.ReadIntPtr(p, offset).ToInt64();
                case "win:GUID":
                    var bytes = new byte[16];
                    Marshal.Copy(p + offset, bytes, 0, 16);
                    return new Guid(bytes);
                default:
                    var raw = new byte[16];
                    Marshal.Copy(p + offset, raw, 0, 16);
                    return raw;
            }
        }
    }
}
