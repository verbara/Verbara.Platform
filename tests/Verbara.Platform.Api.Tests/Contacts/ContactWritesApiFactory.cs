using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests.Contacts;

/// <summary>
/// The host for the contact-write suites (contacts-writes-follow-their-permissions): the real host over the in-memory
/// stores, real JWTs and the real API-key authentication, where every user's server-side permissions are its role
/// template's (<see cref="ConversationOwnershipApiFactory"/>). Adds API keys bound to a user and an unbound management
/// key of the customer tenant, readers for contacts and their audit records, and a switch that makes the next contact
/// save or audit insert fail.
/// </summary>
public class ContactWritesApiFactory : ConversationOwnershipApiFactory
{
    /// <summary>The audit target type every contact record carries (fixtures/contact-opt-out.v1.json <c>audit</c>).</summary>
    public const string ContactTargetType = "contact";

    /// <summary>Faults armed on the contact store and the audit store; each fires once.</summary>
    public FaultSwitch Faults { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);
        Wrap<IContactStore>(services, nameof(IContactStore.SaveAsync));
        Wrap<IAuditStore>(services, nameof(IAuditStore.SaveAsync));
    }

    private void Wrap<T>(IServiceCollection services, string faultedMethod)
        where T : class
    {
        var descriptor = services.Last(d => d.ServiceType == typeof(T));
        services.Remove(descriptor);
        services.AddSingleton(provider =>
        {
            var inner = (T)(descriptor.ImplementationInstance
                ?? descriptor.ImplementationFactory?.Invoke(provider)
                ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));
            return FaultProxy<T>.Create(inner, Faults, faultedMethod);
        });
    }

    /// <summary>Loads one of the change's contract fixtures, copied verbatim under <c>Contacts/Fixtures</c>.</summary>
    public static JsonObject Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Join(AppContext.BaseDirectory, "Contacts", "Fixtures", name)))!.AsObject();

    /// <summary>A client authenticated by a standard API key of the customer tenant bound to <paramref name="owner"/>.</summary>
    public HttpClient UserBoundApiKeyClient(User owner) =>
        KeyClient(ApiKeyType.Standard, owner.UserId, out _);

    /// <summary>A client authenticated by a management API key of the customer tenant bound to no user.</summary>
    public HttpClient UnboundManagementKeyClient(out string keyId) =>
        KeyClient(ApiKeyType.Management, userId: null, out keyId);

    private HttpClient KeyClient(ApiKeyType type, EntityId? userId, out string keyId)
    {
        var rawKey = $"test-contact-key-{Guid.NewGuid():N}";
        keyId = $"k-contact-{Guid.NewGuid():N}";
        using (var scope = Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IApiKeyStore>().SaveAsync(
                new ApiKey
                {
                    KeyId = EntityId.From(keyId),
                    TenantId = Tenant,
                    Name = "Contact write test key",
                    HashedKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))),
                    Scopes = ["*"],
                    UserId = userId,
                    KeyType = type,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                CancellationToken.None).GetAwaiter().GetResult();
        }

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawKey);
        return client;
    }

    /// <summary>The contact as the store holds it now, or <see langword="null"/>.</summary>
    public Contact? LoadContact(EntityId contactId)
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IContactStore>()
            .GetByIdAsync(Tenant, contactId, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Saves <paramref name="contact"/> as it is.</summary>
    public void SaveContact(Contact contact)
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IContactStore>().SaveAsync(contact, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>Every audit record of the customer tenant whose target is the contact <paramref name="contactId"/>.</summary>
    public IReadOnlyList<AuditEntry> ContactAuditOf(EntityId contactId)
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IAuditStore>()
            .GetByEntityAsync(Tenant, ContactTargetType, contactId.Value, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>The faults the next store calls raise; each armed fault fires once, then the store works again.</summary>
    public sealed class FaultSwitch
    {
        private int _contactSave;
        private int _auditSave;

        /// <summary>The next <see cref="IContactStore.SaveAsync"/> throws.</summary>
        public void FailNextContactSave() => Interlocked.Exchange(ref _contactSave, 1);

        /// <summary>The next <see cref="IAuditStore.SaveAsync"/> throws.</summary>
        public void FailNextAuditSave() => Interlocked.Exchange(ref _auditSave, 1);

        internal bool Consume(Type service) =>
            service == typeof(IContactStore) ? Interlocked.Exchange(ref _contactSave, 0) == 1
            : service == typeof(IAuditStore) && Interlocked.Exchange(ref _auditSave, 0) == 1;
    }

    /// <summary>
    /// The fault an armed store raises: a store failure as a database driver raises it, which the host answers 500.
    /// Never an <see cref="InvalidOperationException"/>, which the error middleware answers 400 as a caller error.
    /// </summary>
    public sealed class InjectedStoreFaultException(string message) : Exception(message);

    /// <summary>Forwards every call to the real store, except one method that throws once when its fault is armed.</summary>
    public class FaultProxy<T> : DispatchProxy
        where T : class
    {
        private T _inner = null!;
        private FaultSwitch _faults = null!;
        private string _faultedMethod = null!;

        internal static T Create(T inner, FaultSwitch faults, string faultedMethod)
        {
            var proxy = Create<T, FaultProxy<T>>();
            var self = (FaultProxy<T>)(object)proxy;
            self._inner = inner;
            self._faults = faults;
            self._faultedMethod = faultedMethod;
            return proxy;
        }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == _faultedMethod && _faults.Consume(typeof(T)))
                return Task.FromException(new InjectedStoreFaultException($"Injected fault: {typeof(T).Name}.{_faultedMethod}"));

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
