using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// Security and license analysis on the metadata of every package (no environment needed): roles (with sub-roles and
    /// role extensions), duties (with duty extensions), privileges (entry points, data entity and table permissions) and
    /// the license properties of menu items. A user's license is decided by the menu items their roles reach: a Read grant
    /// needs the item's ViewUserLicense, any other grant its MaintainUserLicense.
    /// </summary>
    internal sealed class SecurityAnalysis
    {
        // ── Index ──

        internal sealed class Grant
        {
            public bool Read, Update, Create, Delete, Correct, Invoke;
            public bool Maintain => Update || Create || Delete || Correct || Invoke;
            public string Text => string.Join(",", new[] { Read ? "Read" : null, Update ? "Update" : null, Create ? "Create" : null,
                Correct ? "Correct" : null, Delete ? "Delete" : null, Invoke ? "Invoke" : null }.Where(x => x != null));
            public void Merge(Grant g) { Read |= g.Read; Update |= g.Update; Create |= g.Create; Delete |= g.Delete; Correct |= g.Correct; Invoke |= g.Invoke; }
            public Grant Copy() => (Grant)MemberwiseClone();
        }

        internal sealed class EntryPoint { public string Name, ObjectType, ObjectName; public Grant Grant; }
        internal sealed class Permission { public string Name; public Grant Grant; }

        internal sealed class Privilege
        {
            public string Name, Label, Model;
            public List<EntryPoint> EntryPoints = new List<EntryPoint>();
            public List<Permission> Entities = new List<Permission>();
            public List<Permission> Tables = new List<Permission>();
        }

        internal sealed class Duty { public string Name, Label, Model; public HashSet<string> Privileges = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }

        internal sealed class Role
        {
            public string Name, Label, Model;
            public HashSet<string> Duties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Privileges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SubRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Where duties/privileges/sub-roles came from: name -> extension (null for the role itself).
            public Dictionary<string, string> AddedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Models of the role extensions.
            public HashSet<string> ExtensionModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> ExtensionModelOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        internal sealed class MenuItem { public string Name, Type, Object, ObjectType, Label, Model, ViewLicense, MaintainLicense, ConfigurationKey; }

        internal sealed class Index
        {
            public string Key;
            public Dictionary<string, Role> Roles = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, Duty> Duties = new Dictionary<string, Duty>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, Privilege> Privileges = new Dictionary<string, Privilege>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, MenuItem> MenuItems = new Dictionary<string, MenuItem>(StringComparer.OrdinalIgnoreCase);   // "Display/Name"
            public HashSet<string> CustomModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> EntityModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Reverse maps.
            public Dictionary<string, HashSet<string>> DutyRoles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>> PrivilegeOwners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase); // "Duty:X" / "Role:Y"
            public Dictionary<string, HashSet<string>> ParentRoles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly object IndexLock = new object();
        private static Index _index;

        private static readonly string[] Types =
        {
            "AxSecurityRole", "AxSecurityRoleExtension", "AxSecurityDuty", "AxSecurityDutyExtension", "AxSecurityPrivilege",
            "AxMenuItemDisplay", "AxMenuItemOutput", "AxMenuItemAction", "AxDataEntityView",
        };

        internal static Index Get(bool force = false)
        {
            lock (IndexLock)
            {
                var folders = CodeSearch.Folders(Types, null).ToList();
                string key = folders.Count + "-" + folders.Select(f => Directory.GetLastWriteTimeUtc(f.Key).Ticks).Aggregate(17L, (h, t) => unchecked(h * 31 + t));
                if (!force && _index != null && _index.Key == key) return _index;
                _index = Build(folders, key);
                return _index;
            }
        }

        private static string L(XElement e) => e.Name.LocalName;
        private static string Child(XElement e, string name) => e?.Elements().FirstOrDefault(c => L(c) == name)?.Value;
        private static IEnumerable<XElement> Items(XElement e, string collection) =>
            e?.Elements().FirstOrDefault(c => L(c) == collection)?.Elements() ?? Enumerable.Empty<XElement>();

        private static Grant ParseGrant(XElement owner)
        {
            var g = owner?.Elements().FirstOrDefault(c => L(c) == "Grant");
            bool Allow(string n) => string.Equals(Child(g, n), "Allow", StringComparison.OrdinalIgnoreCase);
            return new Grant { Read = Allow("Read"), Update = Allow("Update"), Create = Allow("Create"), Delete = Allow("Delete"), Correct = Allow("Correct"), Invoke = Allow("Invoke") };
        }

        private static Index Build(List<KeyValuePair<string, string>> folders, string key)
        {
            var index = new Index { Key = key };
            try
            {
                // Packages only (a Descriptor folder): XppSource and other build folders hold copies named after standard models.
                foreach (string package in DynamicsConfigurationReader.ReadActiveConfiguration().CustomPackageFolders())
                    foreach (string model in Directory.GetDirectories(package).Where(m => !string.Equals(Path.GetFileName(m), "Descriptor", StringComparison.OrdinalIgnoreCase)))
                        index.CustomModels.Add(Path.GetFileName(model));
            }
            catch { }

            var docs = new ConcurrentBag<Tuple<string, string, XElement>>();   // type, model, root
            var files = folders.SelectMany(f => SafeFiles(f.Key).Select(file => Tuple.Create(Path.GetFileName(f.Key), f.Value, file))).ToList();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, f =>
            {
                try
                {
                    if (f.Item1 == "AxDataEntityView") { docs.Add(Tuple.Create(f.Item1, f.Item2, new XElement("E", Path.GetFileNameWithoutExtension(f.Item3)))); return; }
                    docs.Add(Tuple.Create(f.Item1, f.Item2, XDocument.Load(f.Item3).Root));
                }
                catch { }
            });

            foreach (var d in docs.Where(x => x.Item1 == "AxSecurityPrivilege"))
            {
                var r = d.Item3;
                var p = new Privilege { Name = Child(r, "Name"), Label = Child(r, "Label"), Model = d.Item2 };
                if (p.Name == null) continue;
                foreach (var ep in Items(r, "EntryPoints"))
                    p.EntryPoints.Add(new EntryPoint { Name = Child(ep, "Name"), ObjectType = Child(ep, "ObjectType"), ObjectName = Child(ep, "ObjectName"), Grant = ParseGrant(ep) });
                foreach (var de in Items(r, "DataEntityPermissions"))
                    p.Entities.Add(new Permission { Name = Child(de, "Name"), Grant = ParseGrant(de) });
                foreach (var t in Items(r, "DirectAccessPermissions"))
                    p.Tables.Add(new Permission { Name = Child(t, "ObjectName") ?? Child(t, "Name"), Grant = ParseGrant(t) });
                index.Privileges[p.Name] = p;
            }
            foreach (var d in docs.Where(x => x.Item1 == "AxSecurityDuty"))
            {
                var duty = new Duty { Name = Child(d.Item3, "Name"), Label = Child(d.Item3, "Label"), Model = d.Item2 };
                if (duty.Name == null) continue;
                foreach (var p in Items(d.Item3, "Privileges")) duty.Privileges.Add(Child(p, "Name"));
                index.Duties[duty.Name] = duty;
            }
            foreach (var d in docs.Where(x => x.Item1 == "AxSecurityRole"))
            {
                var role = new Role { Name = Child(d.Item3, "Name"), Label = Child(d.Item3, "Label"), Model = d.Item2 };
                if (role.Name == null) continue;
                foreach (var x in Items(d.Item3, "Duties")) role.Duties.Add(Child(x, "Name"));
                foreach (var x in Items(d.Item3, "Privileges")) role.Privileges.Add(Child(x, "Name"));
                foreach (var x in Items(d.Item3, "SubRoles")) role.SubRoles.Add(Child(x, "Name"));
                index.Roles[role.Name] = role;
            }
            foreach (var d in docs.Where(x => x.Item1 == "AxSecurityDutyExtension"))
            {
                string name = Child(d.Item3, "Name") ?? "";
                if (!index.Duties.TryGetValue(name.Split('.')[0], out var duty)) continue;
                foreach (var p in Items(d.Item3, "Privileges")) duty.Privileges.Add(Child(p, "Name"));
            }
            foreach (var d in docs.Where(x => x.Item1 == "AxSecurityRoleExtension"))
            {
                string name = Child(d.Item3, "Name") ?? "";
                if (!index.Roles.TryGetValue(name.Split('.')[0], out var role)) continue;
                role.ExtensionModels.Add(d.Item2);
                role.ExtensionModelOf[name] = d.Item2;
                foreach (var x in Items(d.Item3, "Duties")) { string n = Child(x, "Name"); if (role.Duties.Add(n)) role.AddedBy[n] = name; }
                foreach (var x in Items(d.Item3, "Privileges")) { string n = Child(x, "Name"); if (role.Privileges.Add(n)) role.AddedBy[n] = name; }
                foreach (var x in Items(d.Item3, "SubRoles")) { string n = Child(x, "Name"); if (role.SubRoles.Add(n)) role.AddedBy[n] = name; }
            }
            foreach (var d in docs.Where(x => x.Item1.StartsWith("AxMenuItem")))
            {
                var r = d.Item3;
                string type = d.Item1.Substring("AxMenuItem".Length);
                var item = new MenuItem
                {
                    Name = Child(r, "Name"), Type = type, Object = Child(r, "Object"), ObjectType = Child(r, "ObjectType") ?? (type == "Display" ? "Form" : "Class"),
                    Label = Child(r, "Label"), Model = d.Item2, ConfigurationKey = Child(r, "ConfigurationKey"),
                    ViewLicense = Child(r, "ViewUserLicense") ?? "None", MaintainLicense = Child(r, "MaintainUserLicense") ?? "None",
                };
                if (item.Name != null) index.MenuItems[type + "/" + item.Name] = item;
            }
            foreach (var d in docs.Where(x => x.Item1 == "AxDataEntityView")) index.EntityModels[d.Item3.Value] = d.Item2;

            foreach (var role in index.Roles.Values)
            {
                foreach (string duty in role.Duties) Add(index.DutyRoles, duty, role.Name);
                foreach (string p in role.Privileges) Add(index.PrivilegeOwners, p, "Role:" + role.Name);
                foreach (string sub in role.SubRoles) Add(index.ParentRoles, sub, role.Name);
            }
            foreach (var duty in index.Duties.Values)
                foreach (string p in duty.Privileges) Add(index.PrivilegeOwners, p, "Duty:" + duty.Name);
            return index;
        }

        private static void Add(Dictionary<string, HashSet<string>> map, string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!map.TryGetValue(key, out var set)) map[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(value);
        }

        private static IEnumerable<string> SafeFiles(string dir)
        {
            try { return Directory.EnumerateFiles(dir, "*.xml"); } catch { return new string[0]; }
        }

        // ── Licenses ──

        // Ascending: what a user needs at least. Server items run without a user license.
        private static readonly string[] LicenseOrder = { "None", "Server", "SelfServe", "Universal", "Activity", "Enterprise" };

        internal static string LicenseName(string license)
        {
            switch (license)
            {
                case "Universal": return "Team Members";
                case "Activity": return "Activity";
                case "Enterprise": return "Operations";
                case "SelfServe": return "Self-serve";
                case "Server": return "None (server)";
                default: return "None";
            }
        }

        private static int Rank(string license)
        {
            int i = Array.IndexOf(LicenseOrder, license ?? "None");
            return i < 0 ? 0 : i;
        }

        private static string MenuItemTypeOf(string entryPointType)
        {
            switch (entryPointType)
            {
                case "MenuItemDisplay": return "Display";
                case "MenuItemOutput": return "Output";
                case "MenuItemAction": return "Action";
                default: return null;
            }
        }

        /// <summary>License an entry point grant needs (Read: view license; anything more: maintain license).</summary>
        internal static string LicenseOf(Index index, EntryPoint ep, out MenuItem item)
        {
            item = null;
            string type = MenuItemTypeOf(ep.ObjectType);
            if (type == null || !index.MenuItems.TryGetValue(type + "/" + (ep.ObjectName ?? ep.Name), out item)) return "None";
            return ep.Grant.Maintain ? item.MaintainLicense : ep.Grant.Read ? item.ViewLicense : "None";
        }

        // ── Effective access of a role / duty / privilege ──

        internal sealed class Access
        {
            public string Kind, Name;    // EntryPoint (MenuItemDisplay/...), Entity, Table
            public string ObjectType;
            public Grant Grant = new Grant();
            public List<string> Paths = new List<string>();   // e.g. "Duty SalesOrderMaintain > Privilege SalesTableMaintain"
            public string License = "None";
            public MenuItem Item;
        }

        internal sealed class Expansion
        {
            public List<string> Roles = new List<string>(), Duties = new List<string>(), Privileges = new List<string>();
            public Dictionary<string, Access> Access = new Dictionary<string, Access>(StringComparer.OrdinalIgnoreCase);
            public List<string> Missing = new List<string>();
        }

        internal static Expansion Expand(Index index, string kind, string name)
        {
            var x = new Expansion();
            var seenRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenDuties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Priv(string p, string path)
            {
                if (!index.Privileges.TryGetValue(p, out var priv)) { x.Missing.Add("privilege " + p + " (" + path + ")"); return; }
                if (!x.Privileges.Contains(priv.Name, StringComparer.OrdinalIgnoreCase)) x.Privileges.Add(priv.Name);
                string here = (path.Length > 0 ? path + " > " : "") + "Privilege " + priv.Name;
                foreach (var ep in priv.EntryPoints)
                {
                    string key = "EP:" + ep.ObjectType + "/" + (ep.ObjectName ?? ep.Name);
                    if (!x.Access.TryGetValue(key, out var a)) x.Access[key] = a = new Access { Kind = "EntryPoint", ObjectType = ep.ObjectType, Name = ep.ObjectName ?? ep.Name };
                    a.Grant.Merge(ep.Grant);
                    if (a.Paths.Count < 6) a.Paths.Add(here + " [" + ep.Grant.Text + "]");
                    string lic = LicenseOf(index, ep, out var item);
                    a.Item = a.Item ?? item;
                    if (Rank(lic) > Rank(a.License)) a.License = lic;
                }
                foreach (var e in priv.Entities)
                {
                    string key = "DE:" + e.Name;
                    if (!x.Access.TryGetValue(key, out var a)) x.Access[key] = a = new Access { Kind = "Entity", ObjectType = "DataEntity", Name = e.Name };
                    a.Grant.Merge(e.Grant);
                    if (a.Paths.Count < 6) a.Paths.Add(here + " [" + e.Grant.Text + "]");
                }
                foreach (var t in priv.Tables)
                {
                    string key = "T:" + t.Name;
                    if (!x.Access.TryGetValue(key, out var a)) x.Access[key] = a = new Access { Kind = "Table", ObjectType = "Table", Name = t.Name };
                    a.Grant.Merge(t.Grant);
                    if (a.Paths.Count < 6) a.Paths.Add(here + " [" + t.Grant.Text + "]");
                }
            }
            void DutyX(string d, string path)
            {
                if (!index.Duties.TryGetValue(d, out var duty)) { x.Missing.Add("duty " + d + " (" + path + ")"); return; }
                if (!seenDuties.Add(duty.Name)) return;
                x.Duties.Add(duty.Name);
                foreach (string p in duty.Privileges) Priv(p, (path.Length > 0 ? path + " > " : "") + "Duty " + duty.Name);
            }
            void RoleX(string r, string path)
            {
                if (!index.Roles.TryGetValue(r, out var role)) { x.Missing.Add("role " + r + " (" + path + ")"); return; }
                if (!seenRoles.Add(role.Name)) return;
                x.Roles.Add(role.Name);
                string here = (path.Length > 0 ? path + " > " : "") + "Role " + role.Name;
                foreach (string d in role.Duties) DutyX(d, here);
                foreach (string p in role.Privileges) Priv(p, here);
                foreach (string s in role.SubRoles) RoleX(s, here);
            }
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "role": case "axsecurityrole": RoleX(name, ""); break;
                case "duty": case "axsecurityduty": DutyX(name, ""); break;
                default: Priv(name, ""); break;
            }
            return x;
        }

        // ── Public operations (JSON-friendly results) ──

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private static string KindOf(string objectType)
        {
            switch ((objectType ?? "").ToLowerInvariant())
            {
                case "axsecurityrole": case "role": return "Role";
                case "axsecurityduty": case "duty": return "Duty";
                default: return "Privilege";
            }
        }

        /// <summary>License a role/duty/privilege needs, the menu items that drive it and what a lower grant would change.</summary>
        public Dictionary<string, object> License(string objectType, string name)
        {
            var index = Get();
            string kind = KindOf(objectType);
            var x = Expand(index, kind, name);
            if (x.Roles.Count + x.Duties.Count + x.Privileges.Count == 0)
                return Fail(kind + " '" + name + "' not found.");
            var eps = x.Access.Values.Where(a => a.Kind == "EntryPoint").ToList();
            string top = eps.Select(a => a.License).OrderByDescending(Rank).FirstOrDefault() ?? "None";
            var drivers = eps.Where(a => a.License == top && Rank(top) > Rank("Universal")).OrderBy(a => a.Name).ToList();
            var byLicense = eps.GroupBy(a => a.License).OrderByDescending(g => Rank(g.Key))
                .ToDictionary(g => LicenseName(g.Key), g => (object)g.Count());
            // Drivers granted more than Read whose view license is lower: Read-only access would lower the license.
            var hints = drivers.Where(a => a.Grant.Maintain && a.Item != null && Rank(a.Item.ViewLicense) < Rank(top))
                .Select(a => a.Name + ": " + a.Grant.Text + " needs " + LicenseName(top) + "; Read only would need " + LicenseName(a.Item.ViewLicense))
                .Take(25).ToList();
            return new Dictionary<string, object>
            {
                ["success"] = true, ["kind"] = kind, ["name"] = name,
                ["license"] = LicenseName(top), ["licenseValue"] = top,
                ["entryPoints"] = eps.Count, ["byLicense"] = byLicense,
                ["drivers"] = drivers.Take(60).Select(a => new Dictionary<string, object>
                {
                    ["menuItem"] = a.Name, ["type"] = a.ObjectType, ["grant"] = a.Grant.Text, ["license"] = LicenseName(a.License),
                    ["viewLicense"] = a.Item == null ? null : LicenseName(a.Item.ViewLicense), ["maintainLicense"] = a.Item == null ? null : LicenseName(a.Item.MaintainLicense),
                    ["via"] = a.Paths.Take(3).ToList(),
                }).ToList(),
                ["driverCount"] = drivers.Count,
                ["lowerWithReadOnly"] = hints,
                ["roles"] = x.Roles, ["duties"] = x.Duties.Count, ["privileges"] = x.Privileges.Count,
                ["missing"] = x.Missing.Take(20).ToList(),
            };
        }

        /// <summary>Who reaches a menu item, form, table or data entity: privileges, duties and roles, with the grant.</summary>
        public Dictionary<string, object> WhoCanAccess(string objectType, string name)
        {
            var index = Get();
            string t = (objectType ?? "").Replace("Ax", "");
            var privileges = new List<Tuple<Privilege, string>>();   // privilege, grant text
            var menuItems = new List<string>();
            if (t.StartsWith("MenuItem", StringComparison.OrdinalIgnoreCase))
                menuItems.Add(t + "/" + name);
            else if (t.Equals("Form", StringComparison.OrdinalIgnoreCase))
                menuItems.AddRange(index.MenuItems.Values.Where(m => m.ObjectType == "Form" && string.Equals(m.Object, name, StringComparison.OrdinalIgnoreCase))
                    .Select(m => "MenuItem" + m.Type + "/" + m.Name));
            var viaForms = new List<string>();
            if (t.Equals("Table", StringComparison.OrdinalIgnoreCase))
            {
                // Tables are mostly reached through forms: menu items of the forms that have the table as a data source.
                viaForms = FormsUsingTable(name);
                var forms = new HashSet<string>(viaForms, StringComparer.OrdinalIgnoreCase);
                menuItems.AddRange(index.MenuItems.Values.Where(m => m.ObjectType == "Form" && m.Object != null && forms.Contains(m.Object))
                    .Select(m => "MenuItem" + m.Type + "/" + m.Name));
            }
            foreach (var p in index.Privileges.Values)
            {
                foreach (var ep in p.EntryPoints)
                    if (menuItems.Contains(ep.ObjectType + "/" + (ep.ObjectName ?? ep.Name), StringComparer.OrdinalIgnoreCase))
                        privileges.Add(Tuple.Create(p, (ep.ObjectName ?? ep.Name) + ": " + ep.Grant.Text));
                if (t.Equals("DataEntityView", StringComparison.OrdinalIgnoreCase) || t.Equals("DataEntity", StringComparison.OrdinalIgnoreCase))
                    foreach (var e in p.Entities.Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                        privileges.Add(Tuple.Create(p, "entity: " + e.Grant.Text));
                if (t.Equals("Table", StringComparison.OrdinalIgnoreCase))
                    foreach (var tb in p.Tables.Where(tb => string.Equals(tb.Name, name, StringComparison.OrdinalIgnoreCase)))
                        privileges.Add(Tuple.Create(p, "table: " + tb.Grant.Text));
            }
            var rows = new List<Dictionary<string, object>>();
            foreach (var pg in privileges)
            {
                var duties = index.PrivilegeOwners.TryGetValue(pg.Item1.Name, out var owners) ? owners.Where(o => o.StartsWith("Duty:")).Select(o => o.Substring(5)).ToList() : new List<string>();
                var directRoles = owners?.Where(o => o.StartsWith("Role:")).Select(o => o.Substring(5)).ToList() ?? new List<string>();
                var roles = new HashSet<string>(directRoles, StringComparer.OrdinalIgnoreCase);
                foreach (string d in duties) if (index.DutyRoles.TryGetValue(d, out var rs)) roles.UnionWith(rs);
                foreach (string r in roles.ToList()) AddParents(index, r, roles);
                rows.Add(new Dictionary<string, object>
                {
                    ["privilege"] = pg.Item1.Name, ["model"] = pg.Item1.Model, ["grant"] = pg.Item2,
                    ["duties"] = duties.OrderBy(d => d).ToList(), ["roles"] = roles.OrderBy(r => r).ToList(),
                });
            }
            var mi = menuItems.Select(m => index.MenuItems.TryGetValue(m.Replace("MenuItem", ""), out var item) ? item : null).Where(m => m != null).ToList();
            return new Dictionary<string, object>
            {
                ["success"] = true, ["objectType"] = objectType, ["name"] = name,
                ["menuItems"] = mi.Select(m => new Dictionary<string, object>
                {
                    ["name"] = m.Name, ["type"] = m.Type, ["viewLicense"] = LicenseName(m.ViewLicense), ["maintainLicense"] = LicenseName(m.MaintainLicense),
                }).ToList(),
                ["privileges"] = rows.OrderBy(r => (string)r["privilege"]).ToList(),
                ["roleCount"] = rows.SelectMany(r => (List<string>)r["roles"]).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ["viaForms"] = viaForms.OrderBy(f => f).Take(200).ToList(),
            };
        }

        // Table -> forms with a data source on it (cached scan of the forms' DataSources).
        private static Dictionary<string, List<string>> _formTables;
        private static string _formTablesKey;
        private static readonly System.Text.RegularExpressions.Regex DataSourceTable =
            new System.Text.RegularExpressions.Regex(@"<Table>([A-Za-z_][\w]*)</Table>", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static List<string> FormsUsingTable(string table)
        {
            lock (IndexLock)
            {
                var folders = CodeSearch.Folders(new[] { "AxForm" }, null).ToList();
                string key = folders.Count + "-" + folders.Select(f => Directory.GetLastWriteTimeUtc(f.Key).Ticks).Aggregate(17L, (h, t) => unchecked(h * 31 + t));
                if (_formTables == null || _formTablesKey != key)
                {
                    var map = new ConcurrentDictionary<string, ConcurrentBag<string>>(StringComparer.OrdinalIgnoreCase);
                    Parallel.ForEach(folders.SelectMany(f => SafeFiles(f.Key)), new ParallelOptions { MaxDegreeOfParallelism = 4 }, file =>
                    {
                        string text;
                        try { text = File.ReadAllText(file); } catch { return; }
                        int ds = text.IndexOf("<DataSources>", StringComparison.Ordinal);
                        if (ds < 0) return;
                        int end = text.IndexOf("</DataSources>", ds, StringComparison.Ordinal);
                        string form = Path.GetFileNameWithoutExtension(file);
                        foreach (System.Text.RegularExpressions.Match m in DataSourceTable.Matches(end > ds ? text.Substring(ds, end - ds) : text.Substring(ds)))
                            map.GetOrAdd(m.Groups[1].Value, _ => new ConcurrentBag<string>()).Add(form);
                    });
                    _formTables = map.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
                    _formTablesKey = key;
                }
                return _formTables.TryGetValue(table, out var forms) ? forms : new List<string>();
            }
        }

        private static void AddParents(Index index, string role, HashSet<string> roles)
        {
            if (!index.ParentRoles.TryGetValue(role, out var parents)) return;
            foreach (string p in parents) if (roles.Add(p)) AddParents(index, p, roles);
        }

        /// <summary>Security gaps in the given models (default: the custom models).</summary>
        public Dictionary<string, object> Gaps(IEnumerable<string> models)
        {
            var index = Get();
            var wanted = new HashSet<string>(models != null && models.Any() ? models : index.CustomModels, StringComparer.OrdinalIgnoreCase);
            bool Mine(string model) => model != null && wanted.Contains(model);
            var findings = new List<Dictionary<string, object>>();
            void Finding(string severity, string kind, string objectType, string name, string message) =>
                findings.Add(new Dictionary<string, object> { ["severity"] = severity, ["kind"] = kind, ["objectType"] = objectType, ["name"] = name, ["message"] = message });

            var granted = new HashSet<string>(index.Privileges.Values.SelectMany(p => p.EntryPoints).Select(ep => ep.ObjectType + "/" + (ep.ObjectName ?? ep.Name)), StringComparer.OrdinalIgnoreCase);
            foreach (var m in index.MenuItems.Values.Where(m => Mine(m.Model)))
                if (!granted.Contains("MenuItem" + m.Type + "/" + m.Name))
                    Finding("Error", "UngrantedMenuItem", "AxMenuItem" + m.Type, m.Name, "No privilege grants this menu item: users without System administrator cannot open it.");
            foreach (var p in index.Privileges.Values.Where(p => Mine(p.Model)))
            {
                if (!index.PrivilegeOwners.ContainsKey(p.Name))
                    Finding("Warning", "OrphanPrivilege", "AxSecurityPrivilege", p.Name, "The privilege is in no duty or role, so nobody gets it.");
                if (p.EntryPoints.Count + p.Entities.Count + p.Tables.Count == 0)
                    Finding("Warning", "EmptyPrivilege", "AxSecurityPrivilege", p.Name, "The privilege grants nothing.");
                foreach (var ep in p.EntryPoints)
                {
                    string type = MenuItemTypeOf(ep.ObjectType);
                    if (type == "Display" && (ep.Grant.Delete || ep.Grant.Create) && p.Name.EndsWith("View", StringComparison.OrdinalIgnoreCase))
                        Finding("Warning", "OverGrant", "AxSecurityPrivilege", p.Name, "A View privilege grants " + ep.Grant.Text + " on " + ep.ObjectName + "; View privileges should grant Read only.");
                    if (type == "Action" && (ep.Grant.Delete || ep.Grant.Create || ep.Grant.Update))
                        Finding("Info", "OverGrant", "AxSecurityPrivilege", p.Name, "Action menu item " + ep.ObjectName + " is granted " + ep.Grant.Text + "; actions need Invoke (or Delete for delete actions) only.");
                    if (type != null && !index.MenuItems.ContainsKey(type + "/" + (ep.ObjectName ?? ep.Name)))
                        Finding("Error", "MissingEntryPoint", "AxSecurityPrivilege", p.Name, "Entry point " + ep.ObjectType + " " + ep.ObjectName + " does not exist.");
                    string lic = LicenseOf(index, ep, out var item);
                    if (item != null && ep.Grant.Maintain && Rank(item.MaintainLicense) > Rank(item.ViewLicense) && p.Name.EndsWith("View", StringComparison.OrdinalIgnoreCase))
                        Finding("Warning", "LicenseUpgrade", "AxSecurityPrivilege", p.Name, "Granting " + ep.Grant.Text + " on " + ep.ObjectName + " needs " + LicenseName(item.MaintainLicense) + " instead of " + LicenseName(item.ViewLicense) + ".");
                }
            }
            foreach (var d in index.Duties.Values.Where(d => Mine(d.Model)))
            {
                if (!index.DutyRoles.ContainsKey(d.Name))
                    Finding("Warning", "OrphanDuty", "AxSecurityDuty", d.Name, "The duty is in no role. Find the standard roles that do this job with op=placement duty=" + d.Name + " and add it with a role extension.");
                foreach (string p in d.Privileges.Where(p => !index.Privileges.ContainsKey(p)))
                    Finding("Error", "MissingPrivilege", "AxSecurityDuty", d.Name, "Privilege " + p + " does not exist.");
            }
            foreach (var r in index.Roles.Values)
                foreach (string d in r.Duties.Where(d => !index.Duties.ContainsKey(d)))
                    if (Mine(r.Model) || (r.AddedBy.TryGetValue(d, out string ext) && r.ExtensionModelOf.TryGetValue(ext, out string extModel) && Mine(extModel)))
                        Finding("Error", "MissingDuty", "AxSecurityRole", r.Name, "Duty " + d + " does not exist" + (r.AddedBy.TryGetValue(d, out string by) ? " (added by " + by + ")" : "") + ".");
            var entityPermitted = new HashSet<string>(index.Privileges.Values.SelectMany(p => p.Entities).Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var e in index.EntityModels.Where(e => Mine(e.Value)))
                if (!entityPermitted.Contains(e.Key))
                    Finding("Warning", "UnsecuredEntity", "AxDataEntityView", e.Key, "No privilege has permissions on this data entity: only System administrator can use it in data management, OData or Excel.");
            return new Dictionary<string, object>
            {
                ["success"] = true, ["models"] = wanted.OrderBy(m => m).ToList(),
                ["findings"] = findings.OrderBy(f => f["severity"] as string == "Error" ? 0 : f["severity"] as string == "Warning" ? 1 : 2)
                    .ThenBy(f => f["name"] as string).ToList(),
            };
        }

        // ── Segregation of duties ──

        internal sealed class SodRule { public string Name, First, Second, Severity, Reason; }

        // Duty pairs a single user should not hold (classic financial controls). Names are standard duties; rules whose
        // duties do not exist in this version are skipped and reported.
        internal static readonly SodRule[] DefaultSodRules =
        {
            new SodRule { Name = "Vendor master vs vendor payments", First = "VendVendorMasterMaintain", Second = "PaymVendorPaymentsMaintain", Severity = "High", Reason = "Create or change a vendor (bank account) and pay it." },
            new SodRule { Name = "Vendor master vs vendor invoices", First = "VendVendorMasterMaintain", Second = "VendInvoiceVendorInvoicesMaintain", Severity = "High", Reason = "Create a vendor and invoice it." },
            new SodRule { Name = "Purchase orders vs vendor invoices", First = "PurchOrderMaintain", Second = "VendInvoiceVendorInvoicesMaintain", Severity = "Medium", Reason = "Order and invoice the same goods." },
            new SodRule { Name = "Vendor payments vs payment approval", First = "PaymVendorPaymentsMaintain", Second = "PaymVendorPaymentTransactionsApprove", Severity = "High", Reason = "Enter and approve the same payment." },
            new SodRule { Name = "Bank accounts vs vendor payments", First = "BankBankAccountsMaintain", Second = "PaymVendorPaymentsMaintain", Severity = "High", Reason = "Change bank accounts and pay." },
            new SodRule { Name = "Customer master vs customer payments", First = "CustCustomersMaintain", Second = "PaymCustomerPaymentsMaintain", Severity = "Medium", Reason = "Create a customer and post its payments." },
            new SodRule { Name = "Customer payments vs payment approval", First = "PaymCustomerPaymentsMaintain", Second = "PaymCustPaymentTransactionsApprove", Severity = "Medium", Reason = "Enter and approve the same payment." },
            new SodRule { Name = "Credit limits vs sales orders", First = "CredManCustCreditAdjustmentsMaintain", Second = "SalesOrderMaintain", Severity = "Medium", Reason = "Raise a credit limit and sell against it." },
            new SodRule { Name = "Fixed assets vs fixed asset approval", First = "AssetFixedAssetsMaintain", Second = "AssetFixedAssetsApprove", Severity = "Medium", Reason = "Create and approve the same asset." },
            new SodRule { Name = "Security administration vs journals", First = "SysSecSecurityMaintain", Second = "LedgerJournalsAndTransactionsMaintain", Severity = "High", Reason = "Grant yourself access and post." },
        };

        public Dictionary<string, object> SegregationOfDuties(string role, List<SodRule> rules)
        {
            var index = Get();
            rules = rules != null && rules.Count > 0 ? rules : DefaultSodRules.ToList();
            var active = rules.Where(r => index.Duties.ContainsKey(r.First) && index.Duties.ContainsKey(r.Second)).ToList();
            var skipped = rules.Except(active).Select(r => r.Name + " (" + string.Join(", ", new[] { r.First, r.Second }.Where(d => !index.Duties.ContainsKey(d))) + " not found)").ToList();
            var roles = string.IsNullOrEmpty(role)
                ? index.Roles.Values.Where(r => index.CustomModels.Contains(r.Model) || r.ExtensionModels.Overlaps(index.CustomModels)).Select(r => r.Name).ToList()
                : new List<string> { role };
            if (!string.IsNullOrEmpty(role) && !index.Roles.ContainsKey(role)) return Fail("Role '" + role + "' not found.");
            var conflicts = new List<Dictionary<string, object>>();
            foreach (string r in roles)
            {
                var x = Expand(index, "Role", r);
                var duties = new HashSet<string>(x.Duties, StringComparer.OrdinalIgnoreCase);
                foreach (var rule in active.Where(rule => duties.Contains(rule.First) && duties.Contains(rule.Second)))
                    conflicts.Add(new Dictionary<string, object>
                    {
                        ["role"] = r, ["rule"] = rule.Name, ["severity"] = rule.Severity, ["reason"] = rule.Reason,
                        ["first"] = rule.First, ["second"] = rule.Second,
                    });
            }
            return new Dictionary<string, object>
            {
                ["success"] = true, ["roles"] = roles, ["rulesChecked"] = active.Count, ["rulesSkipped"] = skipped, ["conflicts"] = conflicts,
            };
        }

        // ── Report ──

        /// <summary>Markdown (or CSV) security report for roles: license and drivers, duties, privileges, entry points, conflicts.</summary>
        public Dictionary<string, object> Report(List<string> roles, string model, string format)
        {
            var index = Get();
            if ((roles == null || roles.Count == 0) && !string.IsNullOrEmpty(model))
                roles = index.Roles.Values.Where(r => string.Equals(r.Model, model, StringComparison.OrdinalIgnoreCase)
                    || r.ExtensionModels.Contains(model)).Select(r => r.Name).OrderBy(n => n).ToList();
            if (roles == null || roles.Count == 0) return Fail("Give roles, or a model whose roles (or role extensions) to report.");
            bool csv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            if (csv) sb.AppendLine("Role,License,Duty or role path,Privilege,Entry point type,Entry point,Grant,Entry point license");
            else
            {
                sb.AppendLine("# Security report" + (string.IsNullOrEmpty(model) ? "" : ": model " + model));
                sb.AppendLine();
                sb.AppendLine("Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " from the metadata (all packages). Licenses follow the menu items' ViewUserLicense (Read) and MaintainUserLicense (other grants).");
                sb.AppendLine();
                sb.AppendLine("| Role | License | Duties | Privileges | Entry points |");
                sb.AppendLine("|---|---|---|---|---|");
            }
            var details = new StringBuilder();
            foreach (string r in roles)
            {
                var lic = License("Role", r);
                if (!(lic["success"] is bool ok && ok)) { if (!csv) sb.AppendLine("| " + r + " | not found | | | |"); continue; }
                var x = Expand(index, "Role", r);
                var eps = x.Access.Values.Where(a => a.Kind == "EntryPoint").OrderBy(a => a.Name).ToList();
                if (csv)
                {
                    foreach (var a in eps)
                        sb.AppendLine(string.Join(",", new[] { r, (string)lic["license"], a.Paths.FirstOrDefault() ?? "", "", a.ObjectType, a.Name, a.Grant.Text, LicenseName(a.License) }.Select(Csv)));
                    continue;
                }
                sb.AppendLine("| " + r + " | " + lic["license"] + " | " + x.Duties.Count + " | " + x.Privileges.Count + " | " + eps.Count + " |");
                details.AppendLine("## " + r + (index.Roles.TryGetValue(r, out var role) && role.Label != null ? " (" + role.Label + ")" : ""));
                details.AppendLine();
                details.AppendLine("**License:** " + lic["license"] + " (" + string.Join(", ", ((Dictionary<string, object>)lic["byLicense"]).Select(kv => kv.Key + ": " + kv.Value)) + " entry points)");
                var drivers = (List<Dictionary<string, object>>)lic["drivers"];
                if (drivers.Count > 0)
                {
                    details.AppendLine();
                    details.AppendLine("Menu items that require it (first " + drivers.Count + " of " + lic["driverCount"] + "):");
                    foreach (var d in drivers.Take(25)) details.AppendLine("- " + d["menuItem"] + " [" + d["grant"] + "] via " + string.Join("; ", (List<string>)d["via"]));
                }
                var hints = (List<string>)lic["lowerWithReadOnly"];
                if (hints.Count > 0)
                {
                    details.AppendLine();
                    details.AppendLine("Read-only alternatives:");
                    foreach (string h in hints.Take(10)) details.AppendLine("- " + h);
                }
                var sod = SegregationOfDuties(r, null);
                var conflicts = (List<Dictionary<string, object>>)sod["conflicts"];
                details.AppendLine();
                details.AppendLine(conflicts.Count == 0 ? "Segregation of duties: no conflicts with the default rules." : "Segregation-of-duties conflicts:");
                foreach (var c in conflicts) details.AppendLine("- **" + c["severity"] + "** " + c["rule"] + ": " + c["first"] + " + " + c["second"] + " (" + c["reason"] + ")");
                details.AppendLine();
                details.AppendLine("Duties: " + string.Join(", ", x.Duties.OrderBy(d => d)));
                details.AppendLine();
                details.AppendLine("| Entry point | Type | Grant | License |");
                details.AppendLine("|---|---|---|---|");
                foreach (var a in eps.Take(400)) details.AppendLine("| " + a.Name + " | " + a.ObjectType + " | " + a.Grant.Text + " | " + LicenseName(a.License) + " |");
                if (eps.Count > 400) details.AppendLine("| ... " + (eps.Count - 400) + " more | | | |");
                details.AppendLine();
            }
            if (!csv) { sb.AppendLine(); sb.Append(details); }
            return new Dictionary<string, object> { ["success"] = true, ["format"] = csv ? "csv" : "markdown", ["roles"] = roles, ["text"] = sb.ToString() };
        }

        private static string Csv(string v) => v == null ? "" : v.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

        /// <summary>Roles for the Security view: name, label, model, license (computed lazily per request).</summary>
        public Dictionary<string, object> Roles(string filter, bool customOnly, bool withLicense)
        {
            var index = Get();
            var list = index.Roles.Values
                .Where(r => string.IsNullOrEmpty(filter) || r.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                    || (LabelText(r.Label) ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(r => !customOnly || index.CustomModels.Contains(r.Model) || r.ExtensionModels.Overlaps(index.CustomModels))
                .OrderBy(r => r.Name).Take(customOnly ? 500 : 2000).ToList();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["roles"] = list.Select(r => new Dictionary<string, object>
                {
                    ["name"] = r.Name, ["label"] = LabelText(r.Label) ?? r.Label, ["model"] = r.Model, ["custom"] = index.CustomModels.Contains(r.Model),
                    ["extended"] = r.ExtensionModels.Overlaps(index.CustomModels), ["license"] = withLicense ? License("Role", r.Name)["license"] : null,
                }).ToList(),
            };
        }

        // ── Role placement ──

        private static readonly HashSet<string> PlacementStopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "a", "an", "the", "of", "and", "or", "for", "to", "in", "on", "with", "role", "user", "team" };

        /// <summary>
        /// Standard roles to give a new duty: the roles that already reach the standard objects of the same job (the forms the
        /// duty's menu items open, plus the objects given), and how close their label is to the requirement's wording.
        /// Requirements name roles by job title ("Sales administrator", "Fixed asset accountant") that often has no standard
        /// role of that exact name; the role that does the work is the right one to extend.
        /// </summary>
        public Dictionary<string, object> Placement(string dutyName, string roleWording, string[] objects)
        {
            var index = Get();
            var related = new List<Tuple<string, string>>();   // objectType, name
            foreach (string o in objects ?? new string[0])
            {
                var parts = (o ?? "").Split(new[] { ':', '/' }, 2);
                if (parts.Length == 2 && parts[1].Trim().Length > 0) related.Add(Tuple.Create(parts[0].Trim(), parts[1].Trim()));
            }
            if (!string.IsNullOrEmpty(dutyName))
            {
                if (!index.Duties.TryGetValue(dutyName, out var duty)) return Fail("Duty '" + dutyName + "' not found.");
                foreach (string p in duty.Privileges)
                    if (index.Privileges.TryGetValue(p, out var priv))
                        foreach (var ep in priv.EntryPoints)
                        {
                            string type = MenuItemTypeOf(ep.ObjectType);
                            if (type == null || !index.MenuItems.TryGetValue(type + "/" + (ep.ObjectName ?? ep.Name), out var mi)) continue;
                            // The standard form a custom menu item opens (e.g. a filtered list page) is reached by the roles of that job.
                            if (mi.ObjectType == "Form" && !string.IsNullOrEmpty(mi.Object)) related.Add(Tuple.Create("AxForm", mi.Object));
                        }
            }
            related = related.Distinct().ToList();
            if (related.Count == 0 && string.IsNullOrWhiteSpace(roleWording))
                return Fail("Give duty (a duty of your model), role (the requirement's wording) and/or objects (standard objects of the same job, e.g. [\"AxForm:AssetTable\", \"AxMenuItemDisplay:SalesTableListPage\"]).");

            var score = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var reasons = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            void Add(string role, double points, string reason)
            {
                if (!index.Roles.TryGetValue(role, out var r) || index.CustomModels.Contains(r.Model) || role.StartsWith("-")) return;
                score[role] = (score.TryGetValue(role, out double s) ? s : 0) + points;
                if (!reasons.TryGetValue(role, out var list)) reasons[role] = list = new List<string>();
                if (!list.Contains(reason)) list.Add(reason);
            }
            foreach (var o in related)
            {
                var access = WhoCanAccess(o.Item1, o.Item2);
                // Roles reaching an object only through sub-roles or broad roles count less than roles granting it directly.
                var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in (List<Dictionary<string, object>>)access["privileges"])
                    foreach (string role in (List<string>)row["roles"])
                    {
                        bool maintain = ((string)row["grant"]).IndexOf("Read", StringComparison.OrdinalIgnoreCase) < 0 || ((string)row["grant"]).Contains(",");
                        if (direct.Add(role)) Add(role, maintain ? 3 : 2, "reaches " + o.Item2 + (maintain ? " (maintain)" : " (view)"));
                    }
            }
            var stems = (roleWording ?? "").Split(new[] { ' ', '\t', '-', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !PlacementStopWords.Contains(w)).Select(w => w.Length > 6 ? w.Substring(0, Math.Max(5, w.Length - 3)) : w).ToList();
            if (stems.Count > 0)
                foreach (var r in index.Roles.Values)
                {
                    string label = LabelText(r.Label) ?? r.Name;
                    int hits = stems.Count(w => label.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0 || r.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (hits > 0) Add(r.Name, 4.0 * hits / stems.Count, "label '" + label + "' matches " + hits + " of " + stems.Count + " word(s) of '" + roleWording + "'");
                }
            // Broad roles reach almost everything; they rarely are the right home for a job-specific duty.
            var top = score.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(8).ToList();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["related"] = related.Select(o => o.Item1 + " " + o.Item2).ToList(),
                ["roles"] = top.Select(kv => new Dictionary<string, object>
                {
                    ["name"] = kv.Key, ["label"] = LabelText(index.Roles[kv.Key].Label) ?? index.Roles[kv.Key].Label, ["model"] = index.Roles[kv.Key].Model,
                    ["score"] = Math.Round(kv.Value, 1), ["license"] = License("Role", kv.Key)["license"], ["reasons"] = reasons[kv.Key],
                }).ToList(),
            };
        }

        // ── Label texts (en-US) ──

        private static readonly ConcurrentDictionary<string, Dictionary<string, string>> LabelFiles =
            new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _labelFilePaths;

        /// <summary>Text of "@File:Id" or legacy "@SYS1234" in en-US; plain text as is; null when not found.</summary>
        internal static string LabelText(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            if (!label.StartsWith("@")) return label;
            int colon = label.IndexOf(':');
            string file, id;
            if (colon > 1) { file = label.Substring(1, colon - 1); id = label.Substring(colon + 1); }
            else
            {
                int digits = 1;
                while (digits < label.Length && !char.IsDigit(label[digits])) digits++;
                file = label.Substring(1, digits - 1);
                id = label;
            }
            var texts = LabelFiles.GetOrAdd(file, ReadLabelFile);
            return texts.TryGetValue(id, out string text) ? text : null;
        }

        private static Dictionary<string, string> ReadLabelFile(string file)
        {
            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (_labelFilePaths == null)
                {
                    var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var folder in CodeSearch.Folders(new[] { "AxLabelFile" }, null))
                    {
                        string dir = Path.Combine(folder.Key, "LabelResources", "en-US");
                        if (!Directory.Exists(dir)) continue;
                        foreach (string f in Directory.EnumerateFiles(dir, "*.label.txt"))
                        {
                            string name = Path.GetFileName(f);
                            const string suffix = ".en-US.label.txt";
                            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                            name = name.Substring(0, name.Length - suffix.Length);
                            if (!paths.ContainsKey(name)) paths[name] = f;
                        }
                    }
                    _labelFilePaths = paths;
                }
                if (!_labelFilePaths.TryGetValue(file, out string path)) return texts;
                foreach (string line in File.ReadLines(path))
                {
                    if (line.StartsWith(" ;") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq > 0 && !texts.ContainsKey(line.Substring(0, eq))) texts[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            catch { }
            return texts;
        }

        /// <summary>One level of the hierarchy for the Security view.</summary>
        public Dictionary<string, object> Children(string kind, string name)
        {
            var index = Get();
            var items = new List<Dictionary<string, object>>();
            Dictionary<string, object> Item(string k, string n, string label, string model, string detail) =>
                new Dictionary<string, object> { ["kind"] = k, ["name"] = n, ["label"] = label, ["model"] = model, ["detail"] = detail };
            switch (KindOf(kind))
            {
                case "Role":
                    if (!index.Roles.TryGetValue(name, out var role)) return Fail("Role not found.");
                    foreach (string s in role.SubRoles.OrderBy(s => s)) items.Add(Item("Role", s, index.Roles.TryGetValue(s, out var sr) ? sr.Label : null, sr?.Model, "sub-role" + By(role, s)));
                    foreach (string d in role.Duties.OrderBy(d => d)) items.Add(Item("Duty", d, index.Duties.TryGetValue(d, out var du) ? du.Label : null, du?.Model, By(role, d).Trim()));
                    foreach (string p in role.Privileges.OrderBy(p => p)) items.Add(Item("Privilege", p, index.Privileges.TryGetValue(p, out var pr) ? pr.Label : null, pr?.Model, By(role, p).Trim()));
                    break;
                case "Duty":
                    if (!index.Duties.TryGetValue(name, out var duty)) return Fail("Duty not found.");
                    foreach (string p in duty.Privileges.OrderBy(p => p)) items.Add(Item("Privilege", p, index.Privileges.TryGetValue(p, out var pr2) ? pr2.Label : null, pr2?.Model, null));
                    break;
                default:
                    if (!index.Privileges.TryGetValue(name, out var priv)) return Fail("Privilege not found.");
                    foreach (var ep in priv.EntryPoints)
                    {
                        string lic = LicenseOf(index, ep, out var mi);
                        items.Add(Item(ep.ObjectType ?? "EntryPoint", ep.ObjectName ?? ep.Name, mi?.Label, mi?.Model, ep.Grant.Text + " - " + LicenseName(lic)));
                    }
                    foreach (var e in priv.Entities) items.Add(Item("DataEntity", e.Name, null, null, e.Grant.Text));
                    foreach (var t in priv.Tables) items.Add(Item("Table", t.Name, null, null, t.Grant.Text));
                    break;
            }
            return new Dictionary<string, object> { ["success"] = true, ["items"] = items };
        }

        private static string By(Role role, string name) => role.AddedBy.TryGetValue(name, out string ext) ? " (added by " + ext + ")" : "";

        private static Dictionary<string, object> Fail(string message) => new Dictionary<string, object> { ["success"] = false, ["message"] = message };

        // ── Dispatch for xpp/lang and the MCP tool ──

        public Dictionary<string, object> Handle(string action, Func<string, string> str, Func<string, string[]> arr, Func<string, bool> flag)
        {
            switch (action)
            {
                case "license": return License(str("objectType"), str("objectName"));
                case "access": return WhoCanAccess(str("objectType"), str("objectName"));
                case "gaps": return Gaps(arr("models"));
                case "sod":
                    return SegregationOfDuties(str("role"), ParseRules(str("rulesJson")));
                case "report": return Report(arr("roles")?.ToList(), str("model"), str("format"));
                case "roles": return Roles(str("filter"), flag("customOnly"), flag("withLicense"));
                case "children": return Children(str("kind"), str("name"));
                case "placement": return Placement(str("duty"), str("role"), arr("objects"));
                case "refresh": Get(force: true); return new Dictionary<string, object> { ["success"] = true };
                default: return Fail("Unknown security action '" + action + "'. Use license, access, placement, gaps, sod, report.");
            }
        }

        private List<SodRule> ParseRules(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return _json.Deserialize<List<SodRule>>(json); } catch { return null; }
        }
    }
}
