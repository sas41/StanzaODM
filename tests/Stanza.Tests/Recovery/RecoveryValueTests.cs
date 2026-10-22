using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Controllers;
using Stanza.ControlPlane.Models;
using Stanza.ControlPlane.Recovery;

namespace Stanza.Tests.Recovery;

public sealed class RecoveryValueTests
{
    private static readonly Type CellType = typeof(RecoveryService).Assembly.GetType("Stanza.ControlPlane.Recovery.RecoveryCell")!;

    public static TheoryData<object> Values => new()
    {
        DBNull.Value, true, false, (short)-123, int.MinValue, long.MaxValue,
        1234567890.123456789m, 12.5f, -12345.6789d,
        Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
        new DateTime(2026, 10, 22, 12, 30, 0, DateTimeKind.Utc),
        new DateTimeOffset(2026, 10, 22, 12, 30, 0, TimeSpan.FromHours(3)),
        "secret-sentinel-with-\n-unicode-☃", new byte[] { 0, 1, 128, 255 }
    };

    [Theory, MemberData(nameof(Values))]
    public void RawCellCodecPreservesSupportedProviderValuesAndNeverPrintsValues(object value)
    {
        var cell = CellType.GetMethod("From")!.Invoke(null, [value])!;
        var decoded = CellType.GetMethod("Decode")!.Invoke(cell, null);
        Assert.Equal(value, decoded);
        Assert.Equal(CellType.FullName, cell.ToString());
    }

    [Fact]
    public void UnsupportedAndMalformedValuesFailRatherThanLosingPrecisionOrGuessingTypes()
    {
        foreach (var value in new object[] { new object(), double.NaN, float.PositiveInfinity, DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified) })
        {
            var error = Assert.Throws<TargetInvocationException>(() => CellType.GetMethod("From")!.Invoke(null, [value]));
            Assert.IsType<RecoveryOperationException>(error.InnerException);
        }
        var cell = Activator.CreateInstance(CellType)!;
        CellType.GetProperty("Type")!.SetValue(cell, "utc");
        CellType.GetProperty("Value")!.SetValue(cell, "2026-10-22T12:30:00.0000000");
        var malformed = Assert.Throws<TargetInvocationException>(() => CellType.GetMethod("Decode")!.Invoke(cell, null));
        Assert.IsType<FormatException>(malformed.InnerException);
        Assert.Equal("RecoveryModel", new RecoveryModel { Passphrase = "do-not-print-this-passphrase" }.ToString());
    }

    [Fact]
    public void BrowserEndpointsHaveExplicitRequestSizeAndMultipartLimits()
    {
        var restore = typeof(RecoveryController).GetMethod(nameof(RecoveryController.Restore))!;
        var sizeLimit = restore.GetCustomAttributesData().Single(x => x.AttributeType == typeof(RequestSizeLimitAttribute));
        Assert.Equal((long)RecoveryService.MaximumArchiveBytes + 64 * 1024, sizeLimit.ConstructorArguments[0].Value);
        Assert.Equal(RecoveryService.MaximumArchiveBytes + 32 * 1024, restore.GetCustomAttribute<RequestFormLimitsAttribute>()!.MultipartBodyLengthLimit);
        Assert.NotNull(restore.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.NotNull(typeof(RecoveryController).GetMethod(nameof(RecoveryController.Backup))!.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    [Fact]
    public async Task UploadAndSerializationStreamsEnforceTheSameBoundForEveryWriteApi()
    {
        var streamType = typeof(RecoveryService).Assembly.GetType("Stanza.ControlPlane.Recovery.BoundedRecoveryStream")!;
        using var stream = (Stream)Activator.CreateInstance(streamType, [4L])!;
        stream.Write(new byte[4]);
        Assert.Throws<RecoveryOperationException>(() => stream.WriteByte(0));
        Assert.Throws<RecoveryOperationException>(() => stream.SetLength(5));
        Assert.Throws<RecoveryOperationException>(() => stream.Write(new byte[1], 0, 1));
        await Assert.ThrowsAsync<RecoveryOperationException>(() => stream.WriteAsync(new byte[1], 0, 1));
        await Assert.ThrowsAsync<RecoveryOperationException>(() => stream.WriteAsync(new byte[1].AsMemory()).AsTask());
        Assert.Equal(4, stream.Length);
    }

    [Fact]
    public void RegistrationIsIdempotentAndPreservesTheApplicationsAuditSink()
    {
        var services = new ServiceCollection();
        var sink = new TestRecoveryAudit();
        services.AddSingleton<IRecoveryAuditSink>(sink);
        services.AddStanzaRecovery().AddStanzaRecovery();
        Assert.Single(services, x => x.ServiceType == typeof(RecoveryService));
        Assert.Single(services, x => x.ServiceType == typeof(IRecoveryAuditSink));
        using var provider = services.BuildServiceProvider();
        Assert.Same(sink, provider.GetRequiredService<IRecoveryAuditSink>());
    }
}
