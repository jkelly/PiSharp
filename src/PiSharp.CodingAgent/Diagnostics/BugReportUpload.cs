// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/bug-report-upload.ts and src/core/radius.ts.
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI.Authentication.OAuth;

namespace PiSharp.CodingAgent.Diagnostics;

/// <summary><c>UploadBugReportOptions</c>: an optional Radius bearer token and gateway override.</summary>
public sealed record BugReportUploadOptions(string? Token = null, string? GatewayUrl = null);

/// <summary>The upload failed; <see cref="Exception.Message"/> is the source's <c>Bug report upload failed: …</c> text.</summary>
public sealed class BugReportUploadException(string message) : Exception(message);

public static class BugReportUpload
{
    /// <summary>radius.ts <c>RADIUS_PROVIDER_ID</c>.</summary>
    public const string RadiusProviderId = "radius";
    /// <summary>radius.ts <c>ENV_RADIUS_GATEWAY</c>.</summary>
    public const string RadiusGatewayVariable = "PI_RADIUS_GATEWAY";

    /// <summary>radius.ts <c>getRadiusGatewayUrl</c>: <c>PI_RADIUS_GATEWAY</c> (when set, even empty) or the default gateway,
    /// normalized (https:// unless a scheme is present, trailing slashes removed).</summary>
    public static string GetRadiusGatewayUrl(Func<string, string?>? environment = null) =>
        RadiusOAuth.NormalizeGateway((environment ?? Environment.GetEnvironmentVariable)(RadiusGatewayVariable) ?? RadiusOAuth.DefaultGateway);

    /// <summary>Upload a report as multipart form data (one part per <see cref="BugReport.BugReportFiles"/> file, named and
    /// filed as the file, with its content type), anonymously or attributed to a Radius account, to
    /// <c>POST {gateway}/v1/bug-reports</c>. Returns the report id from <c>{"ok":true,"bug_report":{"id":…}}</c>.</summary>
    public static async Task<string> UploadBugReportAsync(BugReportBundle bundle, BugReportUploadOptions? options = null,
        HttpMessageInvoker? http = null, Func<string, string?>? environment = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        options ??= new();
        var boundary = "----formdata-undici-0" + RandomNumberGenerator.GetInt32(0, int.MaxValue).ToString("D11", CultureInfo.InvariantCulture);
        using var body = new MultipartContent("form-data", boundary);
        foreach (var file in BugReport.BugReportFiles(bundle))
        {
            var part = new ByteArrayContent(Encoding.UTF8.GetBytes(file.Data));
            part.Headers.ContentDisposition = new("form-data") { Name = Quote(file.Name), FileName = Quote(file.Name) };
            part.Headers.ContentType = new(file.ContentType);
            body.Add(part);
        }
        body.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + boundary);
        var gateway = options.GatewayUrl ?? GetRadiusGatewayUrl(environment);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(gateway), "/v1/bug-reports")) { Content = body };
        if (!string.IsNullOrEmpty(options.Token)) request.Headers.Authorization = new("Bearer", options.Token);
        var owned = http is null ? new HttpClient() : null;
        try
        {
            using var response = await (http ?? owned!).SendAsync(request, cancellationToken).ConfigureAwait(false);
            JsonElement? json = null;
            try
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(text);
                json = document.RootElement.Clone();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { json = null; }
            var ok = json is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("ok", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (response.IsSuccessStatusCode && ok && json!.Value.TryGetProperty("bug_report", out var report) && report.ValueKind == JsonValueKind.Object &&
                report.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                return id.GetString()!;
            string? detail = null;
            if (json is { } value && Truthy(value) && !(value.ValueKind == JsonValueKind.Object && value.TryGetProperty("ok", out var stated) && Truthy(stated)))
                detail = value.ValueKind == JsonValueKind.Object ? Field(value, "description") ?? Field(value, "error") : null;
            var reason = response.ReasonPhrase;
            throw new BugReportUploadException("Bug report upload failed: " + (!string.IsNullOrEmpty(detail) ? detail
                : !string.IsNullOrEmpty(reason) ? reason : ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)));
        }
        finally { owned?.Dispose(); }
    }

    private static string Quote(string value) => "\"" + value + "\"";

    /// <summary>A truthy field in JavaScript template-string form.</summary>
    private static string? Field(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && Truthy(field) ? field.ValueKind switch
        {
            JsonValueKind.String => field.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.Number => field.GetRawText(),
            JsonValueKind.Array => string.Join(",", field.EnumerateArray().Select(item => item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(), JsonValueKind.Null => "", JsonValueKind.Object => "[object Object]", _ => item.GetRawText()
            })),
            _ => "[object Object]"
        } : null;

    private static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => false,
        JsonValueKind.String => value.GetString()!.Length != 0,
        JsonValueKind.Number => value.GetDouble() != 0,
        _ => true
    };
}
