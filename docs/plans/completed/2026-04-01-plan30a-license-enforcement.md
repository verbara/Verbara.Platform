# Plan 30A: License Enforcement

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Activate licensing enforcement with periodic runtime re-validation.

**Architecture:** Add ILicenseStatus interface + LicenseStatusTracker + LicenseRevalidationService to SDK Pro. Update Platform Program.cs to use config-driven licensing. Enrich management API license endpoint.

**Tech Stack:** .NET 10 Native AOT, ECDSA P-256, Dapper.

**Spec:** `docs/superpowers/specs/2026-04-01-v130-integration-compliance-design.md` — Sub-project A.

**Prerequisite:** None (first sub-project).

---

### Task 1: Add ILicenseStatus interface to SDK Pro Licensing

**Files:**
- Create: `/media/Data/Source/Verbara/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Licensing/ILicenseStatus.cs`

- [ ] **Step 1: Create ILicenseStatus.cs**

```csharp
namespace Asterisk.Sdk.Pro.Licensing;

/// <summary>
/// Queryable snapshot of the current license validation state.
/// Updated by <see cref="LicenseValidationHostedService"/> at startup and
/// <see cref="LicenseRevalidationService"/> periodically.
/// </summary>
public interface ILicenseStatus
{
    /// <summary>Whether the license is currently valid (Valid or GracePeriod).</summary>
    bool IsValid { get; }

    /// <summary>The result of the last validation attempt.</summary>
    LicenseValidationResult LastResult { get; }

    /// <summary>When the license expires (null if no license loaded).</summary>
    DateTimeOffset? ExpiresAt { get; }

    /// <summary>Bitwise-OR of all features included in the license.</summary>
    LicenseFeature LicensedFeatures { get; }

    /// <summary>Maximum cluster nodes allowed by the license.</summary>
    int MaxNodes { get; }

    /// <summary>The licensee name from the license key (null if no license loaded).</summary>
    string? Licensee { get; }

    /// <summary>The license ID (null if no license loaded).</summary>
    string? LicenseId { get; }

    /// <summary>When the last validation was performed.</summary>
    DateTimeOffset LastValidatedAt { get; }
}
```

---

### Task 2: Add LicenseStatusTracker implementation to SDK Pro Licensing

**Files:**
- Create: `/media/Data/Source/Verbara/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Licensing/LicenseStatusTracker.cs`

- [ ] **Step 1: Create LicenseStatusTracker.cs**

```csharp
namespace Asterisk.Sdk.Pro.Licensing;

/// <summary>
/// Thread-safe singleton that tracks the current license validation state.
/// Updated by hosted services; queried by endpoints and middleware.
/// </summary>
public sealed class LicenseStatusTracker : ILicenseStatus
{
    private readonly object _lock = new();
    private LicenseValidationResult _lastResult = LicenseValidationResult.Invalid;
    private DateTimeOffset? _expiresAt;
    private LicenseFeature _licensedFeatures = LicenseFeature.None;
    private int _maxNodes;
    private string? _licensee;
    private string? _licenseId;
    private DateTimeOffset _lastValidatedAt;

    /// <inheritdoc/>
    public bool IsValid
    {
        get
        {
            lock (_lock)
            {
                return _lastResult is LicenseValidationResult.Valid or LicenseValidationResult.GracePeriod;
            }
        }
    }

    /// <inheritdoc/>
    public LicenseValidationResult LastResult
    {
        get { lock (_lock) { return _lastResult; } }
    }

    /// <inheritdoc/>
    public DateTimeOffset? ExpiresAt
    {
        get { lock (_lock) { return _expiresAt; } }
    }

    /// <inheritdoc/>
    public LicenseFeature LicensedFeatures
    {
        get { lock (_lock) { return _licensedFeatures; } }
    }

    /// <inheritdoc/>
    public int MaxNodes
    {
        get { lock (_lock) { return _maxNodes; } }
    }

    /// <inheritdoc/>
    public string? Licensee
    {
        get { lock (_lock) { return _licensee; } }
    }

    /// <inheritdoc/>
    public string? LicenseId
    {
        get { lock (_lock) { return _licenseId; } }
    }

    /// <inheritdoc/>
    public DateTimeOffset LastValidatedAt
    {
        get { lock (_lock) { return _lastValidatedAt; } }
    }

    /// <summary>
    /// Updates the tracked license state. Called by hosted services after validation.
    /// </summary>
    /// <param name="result">The validation result.</param>
    /// <param name="key">The license key (null if no license was loaded).</param>
    public void Update(LicenseValidationResult result, LicenseKey? key)
    {
        lock (_lock)
        {
            _lastResult = result;
            _expiresAt = key?.ExpiresAt;
            _licensedFeatures = key?.Features ?? LicenseFeature.None;
            _maxNodes = key?.MaxNodes ?? 0;
            _licensee = key?.Licensee;
            _licenseId = key?.LicenseId;
            _lastValidatedAt = DateTimeOffset.UtcNow;
        }
    }
}
```

---

### Task 3: Add RevalidationInterval to LicenseOptions

**Files:**
- Modify: `/media/Data/Source/Verbara/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Licensing/LicenseOptions.cs`

- [ ] **Step 1: Add RevalidationInterval property**

In `/media/Data/Source/Verbara/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Licensing/LicenseOptions.cs`, replace the entire file:

```csharp
namespace Asterisk.Sdk.Pro.Licensing;

public sealed class LicenseOptions
{
    public string? LicenseFilePath { get; set; }
    public EnforcementMode EnforcementMode { get; set; } = EnforcementMode.Enforce;
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How often the license file is re-validated at runtime.
    /// Default: every 6 hours. Set to <see cref="TimeSpan.Zero"/> or
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable periodic re-validation.
    /// </summary>
    public TimeSpan RevalidationInterval { get; set; } = TimeSpan.FromHours(6);
}

public enum EnforcementMode
{
    Enforce,
    WarnOnly,
    Disabled,
}
```

---

### Task 4: Add LicenseRevalidationService hosted service

**Files:**
- Create: `/media/Data/Source/Verbara/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Licensing/LicenseRevalidationService.cs`

- [ ] **Step 1: Create LicenseRevalidationService.cs**

```csharp
namespace Asterisk.Sdk.Pro.Licensing;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Periodically re-validates the license file and updates <see cref="ILicenseStatus"/>.
/// Does NOT kill the process on failure — allows graceful degradation.
/// </summary>
public sealed partial class LicenseRevalidationService : IHostedService, IDisposable
{
    private readonly LicenseOptions _options;
    private readonly LicenseStatusTracker _tracker;
    private readonly byte[] _publicKey;
    private readonly ILogger<LicenseRevalidationService> _logger;
    private Timer? _timer;

    public LicenseRevalidationService(
        IOptions<LicenseOptions> options,
        LicenseStatusTracker tracker,
        byte[] publicKey,
        ILogger<LicenseRevalidationService> logger)
    {
        _options = options.Value;
        _tracker = tracker;
        _publicKey = publicKey;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.EnforcementMode == EnforcementMode.Disabled)
        {
            LogRevalidationDisabled(_logger);
            return Task.CompletedTask;
        }

        var interval = _options.RevalidationInterval;
        if (interval <= TimeSpan.Zero || interval == Timeout.InfiniteTimeSpan)
        {
            LogRevalidationDisabled(_logger);
            return Task.CompletedTask;
        }

        LogRevalidationStarted(_logger, interval);
        _timer = new Timer(Revalidate, null, interval, interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    private void Revalidate(object? state)
    {
        try
        {
            var path = _options.LicenseFilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                LogLicenseFileMissing(_logger, path ?? "(null)");
                _tracker.Update(LicenseValidationResult.Invalid, null);
                return;
            }

            var key = LicenseReader.Load(path);
            var result = LicenseValidator.Validate(key, _publicKey, _options.GracePeriod);
            _tracker.Update(result, key);

            switch (result)
            {
                case LicenseValidationResult.Valid:
                    LogRevalidationValid(_logger, key.LicenseId, key.ExpiresAt);
                    break;

                case LicenseValidationResult.GracePeriod:
                    LogRevalidationGracePeriod(_logger, key.LicenseId, key.ExpiresAt);
                    break;

                case LicenseValidationResult.Expired:
                    LogRevalidationExpired(_logger, key.LicenseId, key.ExpiresAt);
                    break;

                case LicenseValidationResult.Invalid:
                    LogRevalidationInvalid(_logger, key.LicenseId);
                    break;

                case LicenseValidationResult.MissingFeature:
                    LogRevalidationMissingFeature(_logger, key.LicenseId);
                    break;
            }
        }
        catch (Exception ex)
        {
            LogRevalidationError(_logger, ex);
            _tracker.Update(LicenseValidationResult.Invalid, null);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "License re-validation is disabled.")]
    private static partial void LogRevalidationDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "License re-validation started with interval {Interval}.")]
    private static partial void LogRevalidationStarted(ILogger logger, TimeSpan interval);

    [LoggerMessage(Level = LogLevel.Warning, Message = "License file not found during re-validation: '{Path}'.")]
    private static partial void LogLicenseFileMissing(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "License '{LicenseId}' re-validated successfully, expires {ExpiresAt:O}.")]
    private static partial void LogRevalidationValid(ILogger logger, string licenseId, DateTimeOffset expiresAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "License '{LicenseId}' expired at {ExpiresAt:O} but is within grace period.")]
    private static partial void LogRevalidationGracePeriod(ILogger logger, string licenseId, DateTimeOffset expiresAt);

    [LoggerMessage(Level = LogLevel.Critical, Message = "License '{LicenseId}' expired at {ExpiresAt:O} and grace period has elapsed.")]
    private static partial void LogRevalidationExpired(ILogger logger, string licenseId, DateTimeOffset expiresAt);

    [LoggerMessage(Level = LogLevel.Critical, Message = "License '{LicenseId}' has an invalid signature during re-validation.")]
    private static partial void LogRevalidationInvalid(ILogger logger, string licenseId);

    [LoggerMessage(Level = LogLevel.Critical, Message = "License '{LicenseId}' is missing required features during re-validation.")]
    private static partial void LogRevalidationMissingFeature(ILogger logger, string licenseId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error during license re-validation.")]
    private static partial void LogRevalidationError(ILogger logger, Exception ex);
}
```

---

### Task 5: Update LicenseValidationHostedService to update LicenseStatusTracker

**Files:**
- Modify: `/media/Data/Source/Verbara/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Licensing/LicenseValidationHostedService.cs`

- [ ] **Step 1: Replace entire LicenseValidationHostedService.cs**

```csharp
namespace Asterisk.Sdk.Pro.Licensing;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Validates the license at application startup. Throws <see cref="LicenseException"/> when
/// <see cref="EnforcementMode.Enforce"/> is active and validation fails.
/// Updates <see cref="LicenseStatusTracker"/> with the validation result.
/// </summary>
public sealed partial class LicenseValidationHostedService : IHostedService
{
    private readonly LicenseOptions _options;
    private readonly IEnumerable<RequiredFeatureMarker> _markers;
    private readonly LicenseStatusTracker _tracker;
    private readonly byte[] _publicKey;
    private readonly ILogger<LicenseValidationHostedService> _logger;

    public LicenseValidationHostedService(
        IOptions<LicenseOptions> options,
        IEnumerable<RequiredFeatureMarker> markers,
        LicenseStatusTracker tracker,
        byte[] publicKey,
        ILogger<LicenseValidationHostedService> logger)
    {
        _options = options.Value;
        _markers = markers;
        _tracker = tracker;
        _publicKey = publicKey;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.EnforcementMode == EnforcementMode.Disabled)
        {
            LogDisabled(_logger);
            _tracker.Update(LicenseValidationResult.Valid, null);
            return Task.CompletedTask;
        }

        var path = _options.LicenseFilePath
            ?? throw new LicenseException("LicenseOptions.LicenseFilePath is not configured.");

        var key = LicenseReader.Load(path);
        var result = LicenseValidator.Validate(key, _publicKey, _options.GracePeriod);

        switch (result)
        {
            case LicenseValidationResult.Valid:
                LogValid(_logger, key.LicenseId, key.Licensee, key.ExpiresAt);
                break;

            case LicenseValidationResult.GracePeriod:
                LogGracePeriod(_logger, key.LicenseId, key.ExpiresAt, _options.GracePeriod);
                // Grace period — warn but allow startup to proceed.
                break;

            case LicenseValidationResult.Expired:
                _tracker.Update(result, key);
                HandleFailure(result, $"License '{key.LicenseId}' expired at {key.ExpiresAt:O} and the grace period has elapsed.");
                return Task.CompletedTask;

            case LicenseValidationResult.Invalid:
                _tracker.Update(result, key);
                HandleFailure(result, $"License '{key.LicenseId}' has an invalid signature.");
                return Task.CompletedTask;

            case LicenseValidationResult.MissingFeature:
                _tracker.Update(result, key);
                HandleFailure(result, $"License '{key.LicenseId}' is missing required features.");
                return Task.CompletedTask;
        }

        // Check required features declared by Pro packages via RequiredFeatureMarker.
        foreach (var marker in _markers)
        {
            if (!key.Features.HasFlag(marker.Feature))
            {
                _tracker.Update(LicenseValidationResult.MissingFeature, key);
                HandleFailure(
                    LicenseValidationResult.MissingFeature,
                    $"License '{key.LicenseId}' does not include the '{marker.Feature}' feature.");
                return Task.CompletedTask;
            }
        }

        _tracker.Update(result, key);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void HandleFailure(LicenseValidationResult result, string message)
    {
        if (_options.EnforcementMode == EnforcementMode.Enforce)
        {
            LogEnforcementFailed(_logger, result, message);
            throw new LicenseException(message);
        }

        LogWarnOnly(_logger, result, message);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "License enforcement is disabled — skipping validation.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "License '{LicenseId}' is valid for '{Licensee}', expires {ExpiresAt:O}.")]
    private static partial void LogValid(ILogger logger, string licenseId, string licensee, DateTimeOffset expiresAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "License '{LicenseId}' expired at {ExpiresAt:O} but is within the {GracePeriod} grace period.")]
    private static partial void LogGracePeriod(ILogger logger, string licenseId, DateTimeOffset expiresAt, TimeSpan gracePeriod);

    [LoggerMessage(Level = LogLevel.Error, Message = "License validation failed ({Result}): {Message}. Throwing LicenseException.")]
    private static partial void LogEnforcementFailed(ILogger logger, LicenseValidationResult result, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "License validation failed ({Result}): {Message}. Running in WarnOnly mode.")]
    private static partial void LogWarnOnly(ILogger logger, LicenseValidationResult result, string message);
}
```

---

### Task 6: Update DI registration in SDK Pro Licensing

**Files:**
- Modify: `/media/Data/Source/Verbara/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Licensing/DependencyInjection/LicensingServiceCollectionExtensions.cs`

- [ ] **Step 1: Replace entire LicensingServiceCollectionExtensions.cs**

```csharp
namespace Asterisk.Sdk.Pro.Licensing.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static class LicensingServiceCollectionExtensions
{
    /// <summary>
    /// Registers license validation services including <see cref="IFeatureRegistry"/>,
    /// <see cref="LicenseValidator"/>, <see cref="LicenseReader"/>,
    /// <see cref="ILicenseStatus"/> (as <see cref="LicenseStatusTracker"/>),
    /// <see cref="LicenseValidationHostedService"/>, and <see cref="LicenseRevalidationService"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional delegate to configure <see cref="LicenseOptions"/>.</param>
    public static IServiceCollection AddProLicensing(
        this IServiceCollection services,
        Action<LicenseOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);

        services.AddSingleton<IFeatureRegistry, FeatureRegistry>();
        services.AddSingleton<LicenseValidator>();
        services.AddSingleton<LicenseReader>();
        services.AddSingleton<LicenseStatusTracker>();
        services.AddSingleton<ILicenseStatus>(sp => sp.GetRequiredService<LicenseStatusTracker>());
        services.AddSingleton<IHostedService, LicenseValidationHostedService>();
        services.AddSingleton<IHostedService, LicenseRevalidationService>();

        return services;
    }

    /// <summary>
    /// Declares that the calling Pro package requires <paramref name="feature"/> to be present in the license.
    /// The <see cref="LicenseValidationHostedService"/> checks these markers at startup.
    /// </summary>
    public static IServiceCollection RequireLicenseFeature(this IServiceCollection services, LicenseFeature feature)
    {
        services.AddSingleton(new RequiredFeatureMarker(feature));
        return services;
    }
}
```

---

### Task 7: Update SDK Pro Licensing tests for LicenseStatusTracker

**Files:**
- Modify: `/media/Data/Source/Verbara/Asterisk.Sdk.Pro/tests/Asterisk.Sdk.Pro.Licensing.Tests/LicenseValidationHostedServiceTests.cs`
- Create: `/media/Data/Source/Verbara/Asterisk.Sdk.Pro/tests/Asterisk.Sdk.Pro.Licensing.Tests/LicenseStatusTrackerTests.cs`
- Create: `/media/Data/Source/Verbara/Asterisk.Sdk.Pro/tests/Asterisk.Sdk.Pro.Licensing.Tests/LicenseRevalidationServiceTests.cs`

- [ ] **Step 1: Create LicenseStatusTrackerTests.cs**

```csharp
using System.Security.Cryptography;
using Asterisk.Sdk.Pro.Licensing;
using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.Pro.Licensing.Tests;

public sealed class LicenseStatusTrackerTests
{
    [Fact]
    public void Update_ShouldSetIsValidTrue_WhenResultIsValid()
    {
        var tracker = new LicenseStatusTracker();
        var key = CreateKey(DateTimeOffset.UtcNow.AddDays(30));

        tracker.Update(LicenseValidationResult.Valid, key);

        tracker.IsValid.Should().BeTrue();
        tracker.LastResult.Should().Be(LicenseValidationResult.Valid);
        tracker.ExpiresAt.Should().Be(key.ExpiresAt);
        tracker.LicensedFeatures.Should().Be(key.Features);
        tracker.MaxNodes.Should().Be(key.MaxNodes);
        tracker.Licensee.Should().Be(key.Licensee);
        tracker.LicenseId.Should().Be(key.LicenseId);
        tracker.LastValidatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Update_ShouldSetIsValidTrue_WhenResultIsGracePeriod()
    {
        var tracker = new LicenseStatusTracker();
        var key = CreateKey(DateTimeOffset.UtcNow.AddDays(-3));

        tracker.Update(LicenseValidationResult.GracePeriod, key);

        tracker.IsValid.Should().BeTrue();
        tracker.LastResult.Should().Be(LicenseValidationResult.GracePeriod);
    }

    [Fact]
    public void Update_ShouldSetIsValidFalse_WhenResultIsExpired()
    {
        var tracker = new LicenseStatusTracker();
        var key = CreateKey(DateTimeOffset.UtcNow.AddDays(-30));

        tracker.Update(LicenseValidationResult.Expired, key);

        tracker.IsValid.Should().BeFalse();
        tracker.LastResult.Should().Be(LicenseValidationResult.Expired);
    }

    [Fact]
    public void Update_ShouldSetIsValidFalse_WhenResultIsInvalid()
    {
        var tracker = new LicenseStatusTracker();

        tracker.Update(LicenseValidationResult.Invalid, null);

        tracker.IsValid.Should().BeFalse();
        tracker.LastResult.Should().Be(LicenseValidationResult.Invalid);
        tracker.ExpiresAt.Should().BeNull();
        tracker.LicensedFeatures.Should().Be(LicenseFeature.None);
        tracker.MaxNodes.Should().Be(0);
        tracker.Licensee.Should().BeNull();
        tracker.LicenseId.Should().BeNull();
    }

    [Fact]
    public void Update_ShouldTransitionFromValidToExpired()
    {
        var tracker = new LicenseStatusTracker();
        var validKey = CreateKey(DateTimeOffset.UtcNow.AddDays(30));
        var expiredKey = CreateKey(DateTimeOffset.UtcNow.AddDays(-30));

        tracker.Update(LicenseValidationResult.Valid, validKey);
        tracker.IsValid.Should().BeTrue();

        tracker.Update(LicenseValidationResult.Expired, expiredKey);
        tracker.IsValid.Should().BeFalse();
        tracker.LastResult.Should().Be(LicenseValidationResult.Expired);
    }

    [Fact]
    public void IsValid_ShouldBeFalse_WhenNeverUpdated()
    {
        var tracker = new LicenseStatusTracker();

        tracker.IsValid.Should().BeFalse();
        tracker.LastResult.Should().Be(LicenseValidationResult.Invalid);
    }

    private static LicenseKey CreateKey(DateTimeOffset expiresAt) =>
        new("lic-test", "Test Corp", expiresAt, LicenseFeature.All, 5, "sig");
}
```

- [ ] **Step 2: Create LicenseRevalidationServiceTests.cs**

```csharp
using System.Security.Cryptography;
using System.Text.Json;
using Asterisk.Sdk.Pro.Licensing;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Asterisk.Sdk.Pro.Licensing.Tests;

public sealed class LicenseRevalidationServiceTests : IDisposable
{
    private readonly ECDsa _privateKey;
    private readonly byte[] _publicKeyBytes;
    private readonly string _tempDir;
    private readonly ILogger<LicenseRevalidationService> _logger;

    public LicenseRevalidationServiceTests()
    {
        _privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _publicKeyBytes = _privateKey.ExportSubjectPublicKeyInfo();
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _logger = Substitute.For<ILogger<LicenseRevalidationService>>();
    }

    public void Dispose()
    {
        _privateKey.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteLicenseFile(LicenseKey key)
    {
        var path = Path.Combine(_tempDir, "test.lic");
        var json = JsonSerializer.Serialize(key, LicenseJsonContext.Default.LicenseKey);
        File.WriteAllText(path, json);
        return path;
    }

    private LicenseKey CreateValidKey(DateTimeOffset expiresAt, LicenseFeature features = LicenseFeature.All)
    {
        var payload = new LicensePayload("lic-001", "Test Corp", expiresAt, features, 5);
        var signature = LicenseValidator.Sign(payload, _privateKey);
        return new LicenseKey("lic-001", "Test Corp", expiresAt, features, 5, signature);
    }

    private LicenseRevalidationService CreateService(LicenseOptions options, LicenseStatusTracker? tracker = null)
    {
        return new LicenseRevalidationService(
            Options.Create(options),
            tracker ?? new LicenseStatusTracker(),
            _publicKeyBytes,
            _logger);
    }

    [Fact]
    public async Task StartAsync_ShouldNotStart_WhenDisabledMode()
    {
        var options = new LicenseOptions { EnforcementMode = EnforcementMode.Disabled };
        var service = CreateService(options);

        var act = () => service.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        service.Dispose();
    }

    [Fact]
    public async Task StartAsync_ShouldNotStart_WhenIntervalIsZero()
    {
        var key = CreateValidKey(DateTimeOffset.UtcNow.AddDays(30));
        var path = WriteLicenseFile(key);
        var options = new LicenseOptions
        {
            LicenseFilePath = path,
            EnforcementMode = EnforcementMode.Enforce,
            RevalidationInterval = TimeSpan.Zero,
        };
        var service = CreateService(options);

        var act = () => service.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        service.Dispose();
    }

    [Fact]
    public async Task StartAsync_ShouldStart_WhenConfiguredProperly()
    {
        var key = CreateValidKey(DateTimeOffset.UtcNow.AddDays(30));
        var path = WriteLicenseFile(key);
        var options = new LicenseOptions
        {
            LicenseFilePath = path,
            EnforcementMode = EnforcementMode.Enforce,
            RevalidationInterval = TimeSpan.FromHours(6),
        };
        var service = CreateService(options);

        var act = () => service.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        await service.StopAsync(CancellationToken.None);
        service.Dispose();
    }
}
```

- [ ] **Step 3: Update LicenseValidationHostedServiceTests.cs to pass LicenseStatusTracker**

Replace the `CreateService` method and add a tracker field. The full file:

```csharp
using System.Security.Cryptography;
using System.Text.Json;
using Asterisk.Sdk.Pro.Licensing;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Asterisk.Sdk.Pro.Licensing.Tests;

public sealed class LicenseValidationHostedServiceTests : IDisposable
{
    private readonly ECDsa _privateKey;
    private readonly byte[] _publicKeyBytes;
    private readonly string _tempDir;
    private readonly ILogger<LicenseValidationHostedService> _logger;
    private readonly LicenseStatusTracker _tracker;

    public LicenseValidationHostedServiceTests()
    {
        _privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _publicKeyBytes = _privateKey.ExportSubjectPublicKeyInfo();
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _logger = Substitute.For<ILogger<LicenseValidationHostedService>>();
        _tracker = new LicenseStatusTracker();
    }

    public void Dispose()
    {
        _privateKey.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteLicenseFile(LicenseKey key)
    {
        var path = Path.Combine(_tempDir, "test.lic");
        var json = JsonSerializer.Serialize(key, LicenseJsonContext.Default.LicenseKey);
        File.WriteAllText(path, json);
        return path;
    }

    private LicenseKey CreateValidKey(DateTimeOffset expiresAt, LicenseFeature features = LicenseFeature.All)
    {
        var payload = new LicensePayload("lic-001", "Test Corp", expiresAt, features, 5);
        var signature = LicenseValidator.Sign(payload, _privateKey);
        return new LicenseKey("lic-001", "Test Corp", expiresAt, features, 5, signature);
    }

    private LicenseValidationHostedService CreateService(LicenseOptions options, IEnumerable<RequiredFeatureMarker>? markers = null)
    {
        var optionsSnapshot = Options.Create(options);
        return new LicenseValidationHostedService(
            optionsSnapshot,
            markers ?? [],
            _tracker,
            _publicKeyBytes,
            _logger);
    }

    [Fact]
    public async Task StartAsync_ShouldLogInfo_WhenLicenseValid()
    {
        var key = CreateValidKey(DateTimeOffset.UtcNow.AddDays(30));
        var path = WriteLicenseFile(key);
        var options = new LicenseOptions
        {
            LicenseFilePath = path,
            EnforcementMode = EnforcementMode.Enforce,
            GracePeriod = TimeSpan.FromDays(7),
        };
        var service = CreateService(options);

        var act = () => service.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        _tracker.IsValid.Should().BeTrue();
        _tracker.LastResult.Should().Be(LicenseValidationResult.Valid);
    }

    [Fact]
    public async Task StartAsync_ShouldThrow_WhenSignatureInvalid_AndEnforceMode()
    {
        var key = new LicenseKey("lic-001", "Test Corp", DateTimeOffset.UtcNow.AddDays(30), LicenseFeature.All, 5, "invalidsig==");
        var path = WriteLicenseFile(key);
        var options = new LicenseOptions
        {
            LicenseFilePath = path,
            EnforcementMode = EnforcementMode.Enforce,
            GracePeriod = TimeSpan.FromDays(7),
        };
        var service = CreateService(options);

        var act = () => service.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<LicenseException>();
        _tracker.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_ShouldLogWarning_WhenExpiredInGrace()
    {
        var key = CreateValidKey(DateTimeOffset.UtcNow.AddDays(-3)); // expired 3 days ago, within 7-day grace
        var path = WriteLicenseFile(key);
        var options = new LicenseOptions
        {
            LicenseFilePath = path,
            EnforcementMode = EnforcementMode.Enforce,
            GracePeriod = TimeSpan.FromDays(7),
        };
        var service = CreateService(options);

        var act = () => service.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        _tracker.IsValid.Should().BeTrue();
        _tracker.LastResult.Should().Be(LicenseValidationResult.GracePeriod);
        _logger.ReceivedCalls()
            .Any(call => call.GetArguments().OfType<LogLevel>().Any(l => l == LogLevel.Warning))
            .Should().BeTrue("a warning should have been logged for a license in grace period");
    }

    [Fact]
    public async Task StartAsync_ShouldSkipValidation_WhenDisabledMode()
    {
        var options = new LicenseOptions
        {
            LicenseFilePath = null,
            EnforcementMode = EnforcementMode.Disabled,
        };
        var service = CreateService(options);

        var act = () => service.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        _tracker.IsValid.Should().BeTrue("disabled mode should set valid status");
    }

    [Fact]
    public async Task StartAsync_ShouldUpdateTracker_WhenMissingFeature()
    {
        var key = CreateValidKey(DateTimeOffset.UtcNow.AddDays(30), LicenseFeature.Dialer);
        var path = WriteLicenseFile(key);
        var options = new LicenseOptions
        {
            LicenseFilePath = path,
            EnforcementMode = EnforcementMode.WarnOnly,
            GracePeriod = TimeSpan.FromDays(7),
        };
        var markers = new[] { new RequiredFeatureMarker(LicenseFeature.Cluster) };
        var service = CreateService(options, markers);

        var act = () => service.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("WarnOnly mode should not throw");
        _tracker.IsValid.Should().BeFalse();
        _tracker.LastResult.Should().Be(LicenseValidationResult.MissingFeature);
    }
}
```

---

### Task 8: Build and test SDK Pro

- [ ] **Step 1: Build SDK Pro Licensing**

```bash
cd /media/Data/Source/Verbara/Asterisk.Sdk.Pro
dotnet build src/Asterisk.Sdk.Pro.Licensing/
```

Expected: Build succeeds, 0 warnings.

- [ ] **Step 2: Run SDK Pro Licensing tests**

```bash
cd /media/Data/Source/Verbara/Asterisk.Sdk.Pro
dotnet test tests/Asterisk.Sdk.Pro.Licensing.Tests/ -v q
```

Expected: All tests pass (existing 4 + new 9 = 13 tests).

---

### Task 9: Pack SDK Pro and restore in Platform

- [ ] **Step 1: Pack SDK Pro to local NuGet feed**

```bash
cd /media/Data/Source/Verbara/Asterisk.Sdk.Pro
dotnet pack -c Release -o /media/Data/Source/Verbara/local-nuget-feed/
```

Expected: All 19 .nupkg files produced.

- [ ] **Step 2: Clear NuGet cache and restore in Platform**

```bash
rm -rf ~/.nuget/packages/asterisk.sdk.pro*/
cd /media/Data/Source/Verbara/Asterisk.Platform
dotnet restore Asterisk.Platform.slnx
```

Expected: Restore succeeds with new Pro.Licensing package version.

- [ ] **Step 3: Verify Platform still builds**

```bash
cd /media/Data/Source/Verbara/Asterisk.Platform
dotnet build Asterisk.Platform.slnx
```

Expected: Build succeeds. Compilation errors expected in Platform.Api due to `LicenseValidationHostedService` constructor change — will be fixed in Task 10.

---

### Task 10: Update Platform.Api Program.cs licensing configuration

**Files:**
- Modify: `/media/Data/Source/Verbara/Asterisk.Platform/src/Asterisk.Platform.Api/Program.cs`

- [ ] **Step 1: Replace the Pro.Licensing section (lines 78-80)**

Find:
```csharp
// ─── Pro.Licensing ───────────────────────────────────────────────────────────
builder.Services.AddSingleton<byte[]>(Array.Empty<byte>());
builder.Services.AddProLicensing(o => o.EnforcementMode = Asterisk.Sdk.Pro.Licensing.EnforcementMode.Disabled);
```

Replace with:
```csharp
// ─── Pro.Licensing ───────────────────────────────────────────────────────────
var licenseConfig = builder.Configuration.GetSection("Licensing");
var licensePath = licenseConfig["FilePath"] ?? "./license.lic";
var publicKeyPath = licenseConfig["PublicKeyPath"];
var licensePublicKey = !string.IsNullOrEmpty(publicKeyPath) && File.Exists(publicKeyPath)
    ? File.ReadAllBytes(publicKeyPath)
    : Array.Empty<byte>();
builder.Services.AddSingleton(licensePublicKey);

var enforcementMode = Enum.TryParse<Asterisk.Sdk.Pro.Licensing.EnforcementMode>(
    licenseConfig["EnforcementMode"], ignoreCase: true, out var parsedMode)
    ? parsedMode
    : (builder.Environment.IsDevelopment()
        ? Asterisk.Sdk.Pro.Licensing.EnforcementMode.WarnOnly
        : Asterisk.Sdk.Pro.Licensing.EnforcementMode.Enforce);

// If no license file exists, fall back to WarnOnly (community mode) unless explicitly configured
if (!File.Exists(licensePath) && !licenseConfig.Exists())
    enforcementMode = Asterisk.Sdk.Pro.Licensing.EnforcementMode.WarnOnly;

builder.Services.AddProLicensing(o =>
{
    o.LicenseFilePath = licensePath;
    o.EnforcementMode = enforcementMode;
    o.RevalidationInterval = TimeSpan.TryParse(licenseConfig["RevalidationInterval"], out var interval)
        ? interval
        : TimeSpan.FromHours(6);
});
```

---

### Task 11: Update ManagementSystemEndpoints license endpoint and DTOs

**Files:**
- Modify: `/media/Data/Source/Verbara/Asterisk.Platform/src/Asterisk.Platform.Api/Endpoints/ManagementSystemEndpoints.cs`
- Modify: `/media/Data/Source/Verbara/Asterisk.Platform/src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`

- [ ] **Step 1: Update ManagementSystemEndpoints.cs**

Add the `ILicenseStatus` using at the top of the file:
```csharp
using Asterisk.Sdk.Pro.Licensing;
```

Replace the `GetLicenseInfo` method:
```csharp
    private static IResult GetLicenseInfo([FromServices] ILicenseStatus licenseStatus)
    {
        var features = new List<string>();
        foreach (var feature in Enum.GetValues<LicenseFeature>())
        {
            if (feature != LicenseFeature.None && feature != LicenseFeature.All
                && licenseStatus.LicensedFeatures.HasFlag(feature))
            {
                features.Add(feature.ToString());
            }
        }

        return Results.Ok(new LicenseInfoDto(
            licenseStatus.IsValid,
            licenseStatus.LicenseId,
            licenseStatus.Licensee,
            licenseStatus.LastResult.ToString(),
            licenseStatus.ExpiresAt,
            features,
            licenseStatus.MaxNodes,
            licenseStatus.LastValidatedAt));
    }
```

Replace the `UpdateLicense` method:
```csharp
    private static IResult UpdateLicense(
        [FromBody] UpdateLicenseRequest body,
        [FromServices] ILicenseStatus licenseStatus)
    {
        // Runtime license activation will be implemented in v1.3.1
        return Results.Ok(new MessageResponse("License activation not yet implemented. Place a .lic file at the configured path and restart."));
    }
```

Replace the `LicenseInfoDto` record at the bottom of the file:
```csharp
internal sealed record LicenseInfoDto(
    bool IsValid,
    string? LicenseId,
    string? Licensee,
    string Status,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> LicensedFeatures,
    int MaxNodes,
    DateTimeOffset LastValidatedAt);
```

- [ ] **Step 2: Update ApiJsonContext.cs — LicenseInfoDto is already registered**

The `LicenseInfoDto` entry already exists in `ApiJsonContext.cs`. No change needed since the type name is unchanged (the record shape change is transparent to source-gen).

Verify by searching: the line `[JsonSerializable(typeof(LicenseInfoDto))]` is already present at line 208.

---

### Task 12: Update SystemInfoDto version

**Files:**
- Modify: `/media/Data/Source/Verbara/Asterisk.Platform/src/Asterisk.Platform.Api/Endpoints/ManagementSystemEndpoints.cs`

- [ ] **Step 1: Update version string in GetSystemInfo**

Find:
```csharp
return Results.Ok(new SystemInfoDto("1.1.0", hostTenant?.TenantId, hostTenant?.Name ?? "Asterisk Platform", features.GetFeatures()));
```

Replace with:
```csharp
return Results.Ok(new SystemInfoDto("1.3.0", hostTenant?.TenantId, hostTenant?.Name ?? "Asterisk Platform", features.GetFeatures()));
```

---

### Task 13: Add Platform tests for license endpoint enrichment

**Files:**
- Create: `/media/Data/Source/Verbara/Asterisk.Platform/tests/Asterisk.Platform.Api.Tests/Endpoints/ManagementSystemEndpointsTests.cs`

- [ ] **Step 1: Check if test directory exists**

```bash
ls /media/Data/Source/Verbara/Asterisk.Platform/tests/Asterisk.Platform.Api.Tests/Endpoints/
```

- [ ] **Step 2: Create ManagementSystemEndpointsTests.cs**

```csharp
using Asterisk.Platform.Api.Endpoints;
using Asterisk.Sdk.Pro.Licensing;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Asterisk.Platform.Api.Tests.Endpoints;

public sealed class ManagementSystemEndpointsTests
{
    [Fact]
    public void LicenseInfoDto_ShouldMapAllFields_WhenLicenseIsValid()
    {
        var dto = new LicenseInfoDto(
            IsValid: true,
            LicenseId: "lic-001",
            Licensee: "Acme Corp",
            Status: "Valid",
            ExpiresAt: new DateTimeOffset(2027, 12, 31, 0, 0, 0, TimeSpan.Zero),
            LicensedFeatures: ["Cluster", "Dialer", "Analytics"],
            MaxNodes: 5,
            LastValidatedAt: DateTimeOffset.UtcNow);

        dto.IsValid.Should().BeTrue();
        dto.LicenseId.Should().Be("lic-001");
        dto.Licensee.Should().Be("Acme Corp");
        dto.Status.Should().Be("Valid");
        dto.LicensedFeatures.Should().HaveCount(3);
        dto.MaxNodes.Should().Be(5);
    }

    [Fact]
    public void LicenseInfoDto_ShouldBeInvalid_WhenNoLicenseLoaded()
    {
        var dto = new LicenseInfoDto(
            IsValid: false,
            LicenseId: null,
            Licensee: null,
            Status: "Invalid",
            ExpiresAt: null,
            LicensedFeatures: [],
            MaxNodes: 0,
            LastValidatedAt: default);

        dto.IsValid.Should().BeFalse();
        dto.LicenseId.Should().BeNull();
        dto.Licensee.Should().BeNull();
        dto.LicensedFeatures.Should().BeEmpty();
    }
}
```

---

### Task 14: Build and test Platform

- [ ] **Step 1: Build Platform**

```bash
cd /media/Data/Source/Verbara/Asterisk.Platform
dotnet build Asterisk.Platform.slnx
```

Expected: Build succeeds, 0 warnings, 0 errors.

- [ ] **Step 2: Run Platform tests**

```bash
cd /media/Data/Source/Verbara/Asterisk.Platform
dotnet test Asterisk.Platform.slnx -v q
```

Expected: All tests pass (existing 1,162 + new 2 = 1,164 tests).

---

### Task 15: Commit SDK Pro changes

- [ ] **Step 1: Commit SDK Pro**

```bash
cd /media/Data/Source/Verbara/Asterisk.Sdk.Pro
git add src/Asterisk.Sdk.Pro.Licensing/ILicenseStatus.cs \
        src/Asterisk.Sdk.Pro.Licensing/LicenseStatusTracker.cs \
        src/Asterisk.Sdk.Pro.Licensing/LicenseRevalidationService.cs \
        src/Asterisk.Sdk.Pro.Licensing/LicenseOptions.cs \
        src/Asterisk.Sdk.Pro.Licensing/LicenseValidationHostedService.cs \
        src/Asterisk.Sdk.Pro.Licensing/DependencyInjection/LicensingServiceCollectionExtensions.cs \
        tests/Asterisk.Sdk.Pro.Licensing.Tests/LicenseStatusTrackerTests.cs \
        tests/Asterisk.Sdk.Pro.Licensing.Tests/LicenseRevalidationServiceTests.cs \
        tests/Asterisk.Sdk.Pro.Licensing.Tests/LicenseValidationHostedServiceTests.cs
git commit -m "feat(licensing): add ILicenseStatus, LicenseStatusTracker, and LicenseRevalidationService

Add ILicenseStatus interface for queryable license state.
Add LicenseStatusTracker singleton (thread-safe) updated by hosted services.
Add LicenseRevalidationService with configurable timer (default 6h).
Update LicenseValidationHostedService to update tracker on startup.
Add LicenseOptions.RevalidationInterval property.
Register ILicenseStatus + LicenseRevalidationService in DI.
Add 9 new tests for tracker and revalidation service."
```

---

### Task 16: Commit Platform changes

- [ ] **Step 1: Commit Platform**

```bash
cd /media/Data/Source/Verbara/Asterisk.Platform
git add src/Asterisk.Platform.Api/Program.cs \
        src/Asterisk.Platform.Api/Endpoints/ManagementSystemEndpoints.cs \
        tests/Asterisk.Platform.Api.Tests/Endpoints/ManagementSystemEndpointsTests.cs
git commit -m "feat(api): activate license enforcement with config-driven setup

Replace hardcoded EnforcementMode.Disabled with configuration-driven
licensing (Licensing:FilePath, Licensing:EnforcementMode, Licensing:PublicKeyPath).
Default: WarnOnly in development, Enforce in production, community mode
when no license file exists.

Enrich GET /api/management/system/license with ILicenseStatus data
(isValid, licensee, licenseId, expiresAt, licensedFeatures, maxNodes).
Update version string to 1.3.0."
```

---

## Summary

| Task | Scope | Files Changed | Tests Added |
|------|-------|---------------|-------------|
| 1 | SDK Pro | 1 created (ILicenseStatus.cs) | 0 |
| 2 | SDK Pro | 1 created (LicenseStatusTracker.cs) | 0 |
| 3 | SDK Pro | 1 modified (LicenseOptions.cs) | 0 |
| 4 | SDK Pro | 1 created (LicenseRevalidationService.cs) | 0 |
| 5 | SDK Pro | 1 modified (LicenseValidationHostedService.cs) | 0 |
| 6 | SDK Pro | 1 modified (LicensingServiceCollectionExtensions.cs) | 0 |
| 7 | SDK Pro | 3 files (1 modified, 2 created) | 9 |
| 8 | SDK Pro | — (build + test) | — |
| 9 | Cross-repo | — (pack + restore) | — |
| 10 | Platform | 1 modified (Program.cs) | 0 |
| 11 | Platform | 1 modified (ManagementSystemEndpoints.cs) | 0 |
| 12 | Platform | 1 modified (ManagementSystemEndpoints.cs) | 0 |
| 13 | Platform | 1 created (ManagementSystemEndpointsTests.cs) | 2 |
| 14 | Platform | — (build + test) | — |
| 15 | SDK Pro | — (commit) | — |
| 16 | Platform | — (commit) | — |

**Totals:** 3 new SDK Pro files, 3 modified SDK Pro files, 1 modified Platform file, 1 new Platform test file. 9 SDK Pro tests + 2 Platform tests = **11 new tests**.

**Risk areas:**
- `LicenseValidationHostedService` constructor signature change is breaking — existing tests must be updated in Task 7 Step 3 before Task 8.
- `Enum.GetValues<LicenseFeature>()` iterates all values including `None` and `All` — filtering is required (done in Task 11).
- `LicenseJsonContext` is `internal` — `LicenseStatusTracker` and `LicenseRevalidationService` can access it since they're in the same assembly.

**Execution order:** Tasks 1-8 (SDK Pro), Task 9 (cross-repo pack/restore), Tasks 10-14 (Platform), Tasks 15-16 (commits).

**Batching for Subagent-Driven Development:**
- **Phase A (batch):** Tasks 1-6 — all SDK Pro source files, no dependencies between them
- **Phase B (individual):** Task 7 — tests require all source files in place
- **Phase C (sequential):** Tasks 8-9 — build, test, pack, restore
- **Phase D (batch):** Tasks 10-12 — independent Platform source changes
- **Phase E (individual):** Task 13 — Platform tests
- **Phase F (sequential):** Tasks 14-16 — build, test, commit
