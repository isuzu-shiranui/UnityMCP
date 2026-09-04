using System.Text.Json;
using System.Text.Json.Nodes;

namespace IsuzuUnityCli.Http;

/// <summary>The Editor's unified response shape: <c>{status, result}</c> or <c>{status, error:{code,message}}</c>.</summary>
public sealed class Envelope
{
    public int HttpStatus { get; }
    public JsonNode Raw { get; }
    public string Status { get; }
    public JsonNode? Result { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }
    public bool Truncated { get; }
    public JsonNode? Next { get; }

    public bool IsError => Status == "error";

    private Envelope(int httpStatus, JsonNode raw)
    {
        HttpStatus = httpStatus;
        Raw = raw;

        var obj = raw as JsonObject;
        Status = StringOf(obj?["status"]) ?? "success";
        Result = obj is null ? raw : obj["result"] ?? (Status == "error" ? null : raw);
        Truncated = obj?["truncated"] is JsonValue t && t.TryGetValue<bool>(out var flag) && flag;
        Next = obj?["next"];

        var error = obj?["error"] as JsonObject;
        ErrorCode = StringOf(error?["code"]);
        ErrorMessage = StringOf(error?["message"]);
    }

    /// <summary>Throws <see cref="UnityError"/> for a non-JSON body or a failing HTTP status, so the caller never sees a bare "HTTP 400".</summary>
    public static Envelope Parse(int httpStatus, string body)
    {
        JsonNode? node = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                node = JsonNode.Parse(body);
            }
        }
        catch (JsonException)
        {
        }

        if (node is null)
        {
            var excerpt = body.Length > 200 ? body.Substring(0, 200) : body;
            throw new UnityError("non_json", $"Unity returned a non-JSON response (HTTP {httpStatus}): {excerpt}", httpStatus);
        }

        var envelope = new Envelope(httpStatus, node);

        if (httpStatus >= 400)
        {
            throw new UnityError(
                envelope.ErrorCode ?? (httpStatus >= 500 ? "server_error" : "client_error"),
                envelope.ErrorMessage ?? $"HTTP {httpStatus}",
                httpStatus);
        }

        return envelope;
    }

    private static string? StringOf(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
    }
}
