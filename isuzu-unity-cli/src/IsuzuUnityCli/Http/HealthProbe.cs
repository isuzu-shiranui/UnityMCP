using IsuzuUnityCli.Discovery;

namespace IsuzuUnityCli.Http;

/// <summary>
/// Whether an Editor answers /health with its own token, which only the Editor that published the
/// token can do.
/// </summary>
public static class HealthProbe
{
    public static bool Answers(InstanceDescriptor descriptor, TimeSpan timeout, CancellationToken cancellation = default)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(timeout);

            var (status, _) = LoopbackHttp.Send(
                descriptor.Endpoint.TrimEnd('/'), "GET", "/health", descriptor.Token, null, limit.Token, limit.Token);

            return status == 200;
        }
        catch (Exception)
        {
            // Refused, reset, timed out, cancelled or unreadable: in every case it is not an answer.
            return false;
        }
    }
}
