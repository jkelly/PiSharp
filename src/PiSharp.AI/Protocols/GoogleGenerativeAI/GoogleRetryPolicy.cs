// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/provider-retry.ts and packages/ai/src/api/google-shared.ts.
namespace PiSharp.AI.Protocols.GoogleGenerativeAI;

/// <summary>
/// google-shared.ts retryGoogleRequest: the @google/genai ApiError carries a status but no headers, so the wrapper sets
/// <c>headers = undefined</c> before retryProviderRequest sees it. x-should-retry, retry-after-ms and retry-after are therefore
/// never read for Google: retryability is the status alone and every delay is the exponential backoff.
/// </summary>
internal static class GoogleRetryPolicy
{
    internal static bool Retryable(HttpResponseMessage response, GoogleGenerativeAIOptions options) =>
        ((int)response.StatusCode is 408 or 409 or 429 || (int)response.StatusCode >= 500) &&
        !options.NoRetryStatuses.Contains((int)response.StatusCode);

    internal static TimeSpan Delay(GoogleGenerativeAIOptions options, int retryIndex)
    {
        var jitter = options.RetryJitterSample();
        if (!double.IsFinite(jitter) || jitter is < 0 or >= 1) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        return TimeSpan.FromMilliseconds(Math.Min(500 * Math.Pow(2, retryIndex), 8000) * (1 - jitter * 0.25));
    }
}
