using System.Text.Json.Serialization;
using Asterisk.Platform.Identity.Auth.Jwt;

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
// R5.4 S5.9 — JWT signing-key rotation pool entries persisted via IJwtKeyStore.
[JsonSerializable(typeof(JwtKeyEntry))]
[JsonSerializable(typeof(IReadOnlyList<JwtKeyEntry>))]
internal partial class RbacJsonContext : JsonSerializerContext;
