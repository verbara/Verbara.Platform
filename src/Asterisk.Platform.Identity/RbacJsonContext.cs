using System.Text.Json.Serialization;

namespace Asterisk.Platform.Identity;

[JsonSerializable(typeof(PermissionDefinition))]
[JsonSerializable(typeof(IReadOnlyList<PermissionDefinition>))]
[JsonSerializable(typeof(RoleTemplate))]
[JsonSerializable(typeof(IReadOnlyList<RoleTemplate>))]
[JsonSerializable(typeof(TenantRole))]
[JsonSerializable(typeof(IReadOnlyList<TenantRole>))]
[JsonSerializable(typeof(UserRoleAssignment))]
[JsonSerializable(typeof(IReadOnlyList<UserRoleAssignment>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlySet<string>))]
internal partial class RbacJsonContext : JsonSerializerContext;
