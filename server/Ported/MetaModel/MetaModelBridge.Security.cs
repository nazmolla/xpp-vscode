using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Dynamics.AX.Metadata.Core.Collections;
using Microsoft.Dynamics.AX.Metadata.Core.MetaModel;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

namespace XppVsCode.Server.MetaModel
{
    // Security: privileges (entry points, data entity permissions), duties, roles and their extensions.
    public sealed partial class MetaModelBridge
    {
        private const string PrivilegeNotExtensibleHint =
            "Privileges cannot be extended. Create a new privilege in your model and add it to the standard duty or role "
            + "with an AxSecurityDutyExtension or AxSecurityRoleExtension.";

        private static bool IsSecurityType(string objectType) =>
            objectType == "AxSecurityPrivilege" || objectType == "AxSecurityDuty" || objectType == "AxSecurityRole"
            || objectType == "AxSecurityDutyExtension" || objectType == "AxSecurityRoleExtension";

        private MetaModelResult CreateSecurityDutyExtension(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            string error = ValidateNewExtension(req, "AxSecurityDuty",
                baseName => MetaService.GetSecurityDuty(baseName) != null,
                () => MetadataProvider.SecurityDutyExtensions.Exists(req.ObjectName));
            if (error != null) return Fail(error);

            MetadataProvider.SecurityDutyExtensions.Create(new AxSecurityDutyExtension { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxSecurityDutyExtension '" + req.ObjectName + "'.");
        }

        private MetaModelResult CreateSecurityRoleExtension(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            string error = ValidateNewExtension(req, "AxSecurityRole",
                baseName => MetaService.GetSecurityRole(baseName) != null,
                () => MetadataProvider.SecurityRoleExtensions.Exists(req.ObjectName));
            if (error != null) return Fail(error);

            MetadataProvider.SecurityRoleExtensions.Create(new AxSecurityRoleExtension { Name = req.ObjectName }, saveInfo);
            return Ok("Created AxSecurityRoleExtension '" + req.ObjectName + "'.");
        }

        private MetaModelResult UpdateSecurityPrivilege(UpdateObjectRequest req)
        {
            var priv = MetaService.GetSecurityPrivilege(req.ObjectName);
            if (priv == null) return Fail("SecurityPrivilege '" + req.ObjectName + "' not found.");

            var saveInfo = GetOwnedObjectSaveInfo("AxSecurityPrivilege", req.ObjectName, PrivilegeNotExtensibleHint, out string error);
            if (saveInfo == null) return Fail(error);
            if ((error = ValidateSecurityRequest(req, priv)) != null) return Fail(error);

            ApplyTypedMetadata(priv, req);
            MetaService.UpdateSecurityPrivilege(priv, saveInfo);
            return Ok("Updated AxSecurityPrivilege '" + req.ObjectName + "'.\n" + DescribeSecurity(priv));
        }

        private MetaModelResult UpdateSecurityDuty(UpdateObjectRequest req)
        {
            var duty = MetaService.GetSecurityDuty(req.ObjectName);
            if (duty == null) return Fail("SecurityDuty '" + req.ObjectName + "' not found.");

            var saveInfo = GetOwnedObjectSaveInfo("AxSecurityDuty", req.ObjectName,
                "Add privileges to it with an AxSecurityDutyExtension named '" + req.ObjectName + ".<Suffix>' in your model.", out string error);
            if (saveInfo == null) return Fail(error);
            if ((error = ValidateSecurityRequest(req)) != null) return Fail(error);

            ApplyTypedMetadata(duty, req);
            MetaService.UpdateSecurityDuty(duty, saveInfo);
            return Ok("Updated AxSecurityDuty '" + req.ObjectName + "'.\n" + DescribeSecurity(duty));
        }

        private MetaModelResult UpdateSecurityRole(UpdateObjectRequest req)
        {
            var role = MetaService.GetSecurityRole(req.ObjectName);
            if (role == null) return Fail("SecurityRole '" + req.ObjectName + "' not found.");

            var saveInfo = GetOwnedObjectSaveInfo("AxSecurityRole", req.ObjectName,
                "Add duties or privileges to it with an AxSecurityRoleExtension named '" + req.ObjectName + ".<Suffix>' in your model.", out string error);
            if (saveInfo == null) return Fail(error);
            if ((error = ValidateSecurityRequest(req)) != null) return Fail(error);

            ApplyTypedMetadata(role, req);
            MetaService.UpdateSecurityRole(role, saveInfo);
            return Ok("Updated AxSecurityRole '" + req.ObjectName + "'.\n" + DescribeSecurity(role));
        }

        private MetaModelResult UpdateSecurityDutyExtension(UpdateObjectRequest req)
        {
            var ext = MetaService.GetSecurityDutyExtension(req.ObjectName);
            if (ext == null) return Fail("SecurityDutyExtension '" + req.ObjectName + "' not found.");

            var saveInfo = GetExtensionSaveInfo("AxSecurityDutyExtension", req.ObjectName, out string error);
            if (saveInfo == null) return Fail(error);
            if ((error = ValidateSecurityRequest(req)) != null) return Fail(error);

            ApplyTypedMetadata(ext, req);
            MetadataProvider.SecurityDutyExtensions.Update(ext, saveInfo);
            return Ok("Updated AxSecurityDutyExtension '" + req.ObjectName + "'.\n" + DescribeSecurity(ext));
        }

        private MetaModelResult UpdateSecurityRoleExtension(UpdateObjectRequest req)
        {
            var ext = MetaService.GetSecurityRoleExtension(req.ObjectName);
            if (ext == null) return Fail("SecurityRoleExtension '" + req.ObjectName + "' not found.");

            var saveInfo = GetExtensionSaveInfo("AxSecurityRoleExtension", req.ObjectName, out string error);
            if (saveInfo == null) return Fail(error);
            if ((error = ValidateSecurityRequest(req)) != null) return Fail(error);

            ApplyTypedMetadata(ext, req);
            MetadataProvider.SecurityRoleExtensions.Update(ext, saveInfo);
            return Ok("Updated AxSecurityRoleExtension '" + req.ObjectName + "'.\n" + DescribeSecurity(ext));
        }

        /// <summary>Save info for a non-extension object, refusing objects outside custom models.</summary>
        private ModelSaveInfo GetOwnedObjectSaveInfo(string objectType, string objectName, string standardModelHint, out string error)
        {
            error = null;
            string modelName = GetObjectModelName(objectType, objectName);
            if (string.IsNullOrEmpty(modelName))
            {
                error = "Cannot resolve model for '" + objectName + "'.";
                return null;
            }
            if (!IsCustomModel(modelName))
            {
                error = objectType + " '" + objectName + "' belongs to standard model '" + modelName + "' and cannot be changed. "
                    + standardModelHint;
                return null;
            }
            var saveInfo = GetModelSaveInfo(modelName);
            if (saveInfo == null) error = "Cannot resolve model for '" + objectName + "'.";
            return saveInfo;
        }

        /// <summary>Checks every referenced object and grant before anything is saved. target is null for creates.</summary>
        private string ValidateSecurityRequest(UpdateObjectRequest req, object target = null)
        {
            var errors = new List<string>();
            string t = req.ObjectType;
            bool isPrivilege = t == "AxSecurityPrivilege";
            bool takesPrivileges = t == "AxSecurityDuty" || t == "AxSecurityRole" || t == "AxSecurityDutyExtension" || t == "AxSecurityRoleExtension";
            bool takesDuties = t == "AxSecurityRole" || t == "AxSecurityRoleExtension";

            if (req.EntryPoints != null && !isPrivilege)
                errors.Add("entryPoints apply only to AxSecurityPrivilege.");
            if (req.DataEntityPermissions != null && !isPrivilege)
                errors.Add("dataEntityPermissions apply only to AxSecurityPrivilege.");
            if (req.Privileges != null && !takesPrivileges)
                errors.Add("privileges apply only to AxSecurityDuty, AxSecurityRole and their extensions.");
            if (req.Duties != null && !takesDuties)
                errors.Add("duties apply only to AxSecurityRole and AxSecurityRoleExtension.");

            foreach (var ep in req.EntryPoints ?? new List<EntryPointDto>())
            {
                bool exists = (target as AxSecurityPrivilege)?.EntryPoints
                    .Any(e => string.Equals(e.Name, ep.Name, StringComparison.OrdinalIgnoreCase)) == true;
                // Changing the grant of an existing entry point needs only its name.
                bool grantOnly = exists && string.IsNullOrEmpty(ep.ObjectType) && string.IsNullOrEmpty(ep.ObjectName);

                if (!grantOnly)
                {
                    if (!TryParseEntryPointType(ep.ObjectType, out EntryPointType type))
                        errors.Add("Entry point '" + ep.Name + "': objectType must be MenuItemDisplay, MenuItemOutput, MenuItemAction or ServiceOperation.");
                    else if (string.IsNullOrEmpty(ep.ObjectName))
                        errors.Add("Entry point '" + ep.Name + "': objectName is required.");
                    else if (!EntryPointTargetExists(type, ep.ObjectName))
                        errors.Add("Entry point '" + ep.Name + "': " + type + " '" + ep.ObjectName + "' not found.");
                }
                if (ep.Grant != null && !TryParseGrant(ep.Grant, out _, out string grantError))
                    errors.Add("Entry point '" + ep.Name + "': " + grantError);
            }

            foreach (string name in req.Privileges ?? new List<string>())
                if (MetaService.GetSecurityPrivilege(name) == null)
                    errors.Add("Privilege '" + name + "' not found.");

            foreach (string name in req.Duties ?? new List<string>())
                if (MetaService.GetSecurityDuty(name) == null)
                    errors.Add("Duty '" + name + "' not found.");

            foreach (var perm in req.DataEntityPermissions ?? new List<DataEntityPermissionDto>())
            {
                if (MetaService.GetDataEntityView(perm.Name) == null)
                    errors.Add("Data entity '" + perm.Name + "' not found.");
                if (perm.Grant != null && !TryParseGrant(perm.Grant, out _, out string grantError))
                    errors.Add("Data entity permission '" + perm.Name + "': " + grantError);
                if (perm.IntegrationMode != null && !Enum.TryParse(perm.IntegrationMode, true, out IntegrationMode _))
                    errors.Add("Data entity permission '" + perm.Name + "': integrationMode must be All, DataServices or DataManagement.");
            }

            return errors.Count == 0 ? null : string.Join("\n", errors);
        }

        private bool EntryPointTargetExists(EntryPointType type, string objectName)
        {
            switch (type)
            {
                case EntryPointType.MenuItemDisplay: return MetaService.GetMenuItemDisplay(objectName) != null;
                case EntryPointType.MenuItemOutput: return MetaService.GetMenuItemOutput(objectName) != null;
                case EntryPointType.MenuItemAction: return MetaService.GetMenuItemAction(objectName) != null;
                // Service operations are named Service.Operation; the service must exist.
                case EntryPointType.ServiceOperation: return MetaService.GetService(objectName.Split('.')[0]) != null;
                default: return false;
            }
        }

        private static bool TryParseEntryPointType(string value, out EntryPointType type)
        {
            type = EntryPointType.None;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string v = value.Trim();
            if (v.StartsWith("Ax", StringComparison.OrdinalIgnoreCase)) v = v.Substring(2);
            return Enum.TryParse(v, true, out type) && type != EntryPointType.None;
        }

        /// <summary>Parses "Read", "Delete", "Invoke", "All" or a comma-separated combination such as "Read,Update".</summary>
        private static bool TryParseGrant(string value, out AccessGrant grant, out string error)
        {
            grant = AccessGrant.Empty();
            error = null;
            var tokens = (value ?? "").Split(new[] { ',', ';', '|', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                error = "grant is empty. Use Read, Update, Create, Correct, Delete, Invoke or All.";
                return false;
            }
            foreach (string token in tokens)
            {
                switch (token.ToLowerInvariant())
                {
                    case "all": grant = AccessGrant.Merge(grant, AccessGrant.ConstructGrantAll()); break;
                    case "read": grant.Read = AccessGrantPermission.Allow; break;
                    case "update": grant.Update = AccessGrantPermission.Allow; break;
                    case "create": grant.Create = AccessGrantPermission.Allow; break;
                    case "correct": grant.Correct = AccessGrantPermission.Allow; break;
                    case "delete": grant.Delete = AccessGrantPermission.Allow; break;
                    case "invoke": grant.Invoke = AccessGrantPermission.Allow; break;
                    default:
                        error = "unknown grant '" + token + "'. Use Read, Update, Create, Correct, Delete, Invoke or All (comma-separated for several).";
                        return false;
                }
            }
            return true;
        }

        private static AccessGrant DefaultGrant(EntryPointType type)
        {
            var grant = AccessGrant.Empty();
            if (type == EntryPointType.MenuItemAction || type == EntryPointType.ServiceOperation)
                grant.Invoke = AccessGrantPermission.Allow;
            else
                grant.Read = AccessGrantPermission.Allow;
            return grant;
        }

        private static string GrantToString(AccessGrant grant)
        {
            var parts = new List<string>();
            void Add(string name, AccessGrantPermission p)
            {
                if (p == AccessGrantPermission.Allow) parts.Add(name);
                else if (p == AccessGrantPermission.Deny) parts.Add("Deny" + name);
            }
            Add("Read", grant.Read);
            Add("Update", grant.Update);
            Add("Create", grant.Create);
            Add("Correct", grant.Correct);
            Add("Delete", grant.Delete);
            Add("Invoke", grant.Invoke);
            return parts.Count == 0 ? "None" : string.Join(",", parts);
        }

        private static bool GrantsEqual(string expected, string actual)
        {
            if (!TryParseGrant(expected, out AccessGrant e, out _)) return false;
            if (!TryParseGrant(actual, out AccessGrant a, out _)) return false;
            return GrantToString(e) == GrantToString(a);
        }

        /// <summary>Adds or updates entry points by name; new ones without a grant get Read (Invoke for actions).</summary>
        private static void ApplyEntryPoints(object secObj, List<EntryPointDto> entryPoints)
        {
            var priv = secObj as AxSecurityPrivilege;
            if (priv == null || entryPoints == null) return;

            foreach (var ep in entryPoints)
            {
                if (string.IsNullOrEmpty(ep.Name)) continue;
                var existing = priv.EntryPoints.FirstOrDefault(e => string.Equals(e.Name, ep.Name, StringComparison.OrdinalIgnoreCase));
                var axEp = existing ?? new AxSecurityEntryPointReference { Name = ep.Name };

                if (TryParseEntryPointType(ep.ObjectType, out EntryPointType type))
                    axEp.ObjectType = type;
                if (!string.IsNullOrEmpty(ep.ObjectName))
                {
                    int dot = ep.ObjectName.IndexOf('.');
                    if (axEp.ObjectType == EntryPointType.ServiceOperation && dot > 0)
                    {
                        axEp.ObjectName = ep.ObjectName.Substring(0, dot);
                        axEp.ObjectChildName = ep.ObjectName.Substring(dot + 1);
                    }
                    else
                    {
                        axEp.ObjectName = ep.ObjectName;
                    }
                }

                if (ep.Grant != null && TryParseGrant(ep.Grant, out AccessGrant grant, out _))
                    axEp.Grant = grant;
                else if (existing == null)
                    axEp.Grant = DefaultGrant(axEp.ObjectType);

                if (existing == null) priv.EntryPoints.Add(axEp);
            }
        }

        private static void ApplySecurityReferences(object target, UpdateObjectRequest req)
        {
            var privileges = (target as AxSecurityDuty)?.Privileges
                ?? (target as AxSecurityRole)?.Privileges
                ?? (target as AxSecurityDutyExtension)?.Privileges
                ?? (target as AxSecurityRoleExtension)?.Privileges;
            var duties = (target as AxSecurityRole)?.Duties ?? (target as AxSecurityRoleExtension)?.Duties;
            var priv = target as AxSecurityPrivilege;

            if (privileges != null)
                foreach (string name in req.Privileges ?? new List<string>())
                    if (!privileges.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                        privileges.Add(new AxSecurityPrivilegeReference { Name = name });

            if (duties != null)
                foreach (string name in req.Duties ?? new List<string>())
                    if (!duties.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
                        duties.Add(new AxSecurityDutyReference { Name = name });

            if (priv != null)
            {
                foreach (var dto in req.DataEntityPermissions ?? new List<DataEntityPermissionDto>())
                {
                    var perm = priv.DataEntityPermissions.FirstOrDefault(p => string.Equals(p.Name, dto.Name, StringComparison.OrdinalIgnoreCase));
                    bool isNew = perm == null;
                    if (isNew) perm = new AxSecurityDataEntityPermission { Name = dto.Name };
                    if (TryParseGrant(dto.Grant ?? "Read", out AccessGrant grant, out _))
                        perm.Grant = grant;
                    if (dto.IntegrationMode != null && Enum.TryParse(dto.IntegrationMode, true, out IntegrationMode mode))
                        perm.IntegrationMode = mode;
                    if (isNew) priv.DataEntityPermissions.Add(perm);
                }
            }

            foreach (string name in req.RemoveSecurityItems ?? new List<string>())
            {
                if (priv != null)
                {
                    RemoveByName(priv.EntryPoints, name, e => e.Name);
                    RemoveByName(priv.DataEntityPermissions, name, p => p.Name);
                }
                if (privileges != null) RemoveByName(privileges, name, p => p.Name);
                if (duties != null) RemoveByName(duties, name, d => d.Name);
            }
        }

        private static void RemoveByName<T>(KeyedObjectCollection<T> collection, string name, Func<T, string> nameOf)
            where T : class, INamedObject
        {
            var item = collection.FirstOrDefault(i => string.Equals(nameOf(i), name, StringComparison.OrdinalIgnoreCase));
            if (item != null) collection.Remove(item);
        }

        private static List<EntryPointDto> ExtractEntryPoints(AxSecurityPrivilege priv) =>
            priv.EntryPoints.Select(e => new EntryPointDto
            {
                Name = e.Name,
                ObjectType = e.ObjectType.ToString(),
                ObjectName = string.IsNullOrEmpty(e.ObjectChildName) ? e.ObjectName : e.ObjectName + "." + e.ObjectChildName,
                Grant = GrantToString(e.Grant)
            }).ToList();

        /// <summary>Human-readable summary of what a security object grants after an update.</summary>
        private static string DescribeSecurity(object target)
        {
            var sb = new StringBuilder();
            if (target is AxSecurityPrivilege priv)
            {
                sb.AppendLine("Entry points (" + priv.EntryPoints.Count + "):");
                foreach (var ep in ExtractEntryPoints(priv))
                    sb.AppendLine("  - " + ep.Name + ": " + ep.ObjectType + " " + ep.ObjectName + ", grant " + ep.Grant);
                if (priv.DataEntityPermissions.Count > 0)
                {
                    sb.AppendLine("Data entity permissions (" + priv.DataEntityPermissions.Count + "):");
                    foreach (var p in priv.DataEntityPermissions)
                        sb.AppendLine("  - " + p.Name + ": grant " + GrantToString(p.Grant) + ", integration " + p.IntegrationMode);
                }
            }

            var privileges = (target as AxSecurityDuty)?.Privileges
                ?? (target as AxSecurityRole)?.Privileges
                ?? (target as AxSecurityDutyExtension)?.Privileges
                ?? (target as AxSecurityRoleExtension)?.Privileges;
            if (privileges != null)
                sb.AppendLine("Privileges (" + privileges.Count + "): " + string.Join(", ", privileges.Select(p => p.Name)));

            var duties = (target as AxSecurityRole)?.Duties ?? (target as AxSecurityRoleExtension)?.Duties;
            if (duties != null)
                sb.AppendLine("Duties (" + duties.Count + "): " + string.Join(", ", duties.Select(d => d.Name)));

            return sb.ToString().TrimEnd();
        }
    }
}
