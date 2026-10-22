using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Stanza.Core.Contracts;

namespace Stanza.Agent.Enrollment;

public enum EnrollmentStatus { NotStarted, Retrying, Pending, Approved, Rejected, Revoked, Incompatible, Unavailable }

public interface IEnrollmentClient
{
    Task<EnrollmentStatus> RegisterAsync(Uri controlPlane, AgentEnrollmentRequest request, CancellationToken cancellationToken);
}

public sealed class EnrollmentClient(HttpClient httpClient, TimeProvider? timeProvider = null) : IEnrollmentClient
{
    public async Task<EnrollmentStatus> RegisterAsync(Uri controlPlane, AgentEnrollmentRequest request, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), timeProvider ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(controlPlane, AgentEnrollmentProtocol.Endpoint))
            {
                Content = JsonContent.Create(request)
            };
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return EnrollmentStatus.Rejected;
            }

            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UpgradeRequired)
            {
                return EnrollmentStatus.Incompatible;
            }

            if (!response.IsSuccessStatusCode)
            {
                return EnrollmentStatus.Retrying;
            }

            // Bound even chunked responses; never include response bodies in diagnostics.
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            var buffer = new byte[4097];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), linked.Token);
                if (read == 0) { break; }
                length += read;
            }

            if (length == buffer.Length) { return EnrollmentStatus.Retrying; }
            var result = JsonSerializer.Deserialize<AgentEnrollmentResponse>(buffer.AsSpan(0, length), JsonSerializerOptions.Web);
            if (result is null) { return EnrollmentStatus.Retrying; }
            if (result.ProtocolVersion != AgentEnrollmentProtocol.Version) { return EnrollmentStatus.Incompatible; }
            return result.Status switch
            {
                AgentEnrollmentProtocol.Pending => EnrollmentStatus.Pending,
                AgentEnrollmentProtocol.Approved => EnrollmentStatus.Approved,
                AgentEnrollmentProtocol.Rejected => EnrollmentStatus.Rejected,
                AgentEnrollmentProtocol.Revoked => EnrollmentStatus.Revoked,
                _ => EnrollmentStatus.Incompatible
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return EnrollmentStatus.Retrying;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            return EnrollmentStatus.Retrying;
        }
    }
}
