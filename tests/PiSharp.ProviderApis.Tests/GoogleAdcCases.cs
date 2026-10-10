// Google Application Default Credentials for Vertex (google-vertex.ts over google-auth-library 10.6.2): external_account (identity pool
// file/url/certificate, AWS, executable), impersonated_service_account and external_account_authorized_user, against in-process fakes.
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PiSharp.AI.Protocols.GoogleVertex;

// Google Application Default Credentials: external_account (identity pool file/url/certificate, aws, executable), impersonated_service_account
// and external_account_authorized_user. Authored expectations written from google-auth-library 10.6.2 (Apache-2.0) build/src/auth
// (baseexternalclient, stscredentials, oauth2common, identitypoolclient, file/url/certificate subject token suppliers, awsclient,
// awsrequestsigner, defaultawssecuritycredentialssupplier, pluggable-auth-client/handler, executable-response, impersonated, googleauth
// fromImpersonatedJSON, externalAccountAuthorizedUserClient). Every HTTP peer, file, executable and mTLS client is an in-process fake.
// Register with: cases = [.. cases, .. GoogleAdcCases].
internal static partial class Program
{
    internal static readonly (string Id, Func<Task> Run)[] GoogleAdcCases =
    [
        ("google-adc.external-account-file-source-sts-and-impersonation", AdcFileSourceStsAndImpersonation),
        ("google-adc.external-account-url-json-source-workforce-user-project-and-no-expiry", AdcUrlSourceAndWorkforce),
        ("google-adc.external-account-configuration-and-exchange-errors", AdcExternalAccountErrors),
        ("google-adc.external-account-aws-environment-credentials-signed-get-caller-identity", AdcAwsEnvironment),
        ("google-adc.external-account-aws-imdsv2-region-role-and-credentials", AdcAwsMetadata),
        ("google-adc.external-account-executable-environment-output-file-and-errors", AdcExecutable),
        ("google-adc.external-account-certificate-source-chain-and-mtls", AdcCertificate),
        ("google-adc.impersonated-service-account-authorized-user-source-and-errors", AdcImpersonatedAuthorizedUser),
        ("google-adc.impersonated-service-account-external-account-source", AdcImpersonatedExternalAccount),
        ("google-adc.external-account-authorized-user-refresh-rotation-and-errors", AdcExternalAuthorizedUser),
    ];

    private const string AdcSts = "https://sts.googleapis.com/v1/token";
    private const string AdcStsOk = """{"access_token":"sts-token","issued_token_type":"urn:ietf:params:oauth:token-type:access_token","token_type":"Bearer","expires_in":3600}""";
    private const string AdcWorkloadAudience = "//iam.googleapis.com/projects/123456/locations/global/workloadIdentityPools/pool/providers/oidc";
    private const string AdcWorkforceAudience = "//iam.googleapis.com/locations/global/workforcePools/pool/providers/prov";
    private const string AdcScopeForm = "scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fcloud-platform";
    private const string AdcExchangeHead = "grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Atoken-exchange&audience=";
    private const string AdcRequestedType = "requested_token_type=urn%3Aietf%3Aparams%3Aoauth%3Atoken-type%3Aaccess_token";

    internal sealed record AdcRequest(string Method, string Url, Dictionary<string, string> Headers, string Body)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name.ToLowerInvariant());
    }

    /// <summary>A fake HTTP peer: requests are recorded; the most recently registered route for the exact method and URL answers.</summary>
    internal sealed class AdcHttp : HttpMessageHandler
    {
        private readonly List<(string Method, string Url, Func<AdcRequest, HttpResponseMessage> Respond)> _routes = [];
        public readonly List<AdcRequest> Requests = [];
        public AdcHttp On(string method, string url, Func<AdcRequest, HttpResponseMessage> respond) { lock (_routes) _routes.Add((method, url, respond)); return this; }
        public AdcHttp On(string method, string url, string body, HttpStatusCode status = HttpStatusCode.OK, string type = "application/json") =>
            On(method, url, _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, type) });
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .GroupBy(pair => pair.Key.ToLowerInvariant()).ToDictionary(group => group.Key, group => string.Join(", ", group.SelectMany(pair => pair.Value)));
            var recorded = new AdcRequest(request.Method.Method, request.RequestUri!.AbsoluteUri, headers, body);
            Func<AdcRequest, HttpResponseMessage>? respond;
            lock (_routes)
            {
                Requests.Add(recorded);
                respond = _routes.LastOrDefault(route => route.Method == recorded.Method && route.Url == recorded.Url).Respond;
            }
            return respond?.Invoke(recorded) ?? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no fake route for " + recorded.Url) };
        }
    }

    private static FixedTime AdcClock() => new(DateTimeOffset.Parse("2026-10-08T12:00:00Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds());

    private static GoogleApplicationDefaultCredentials AdcFor(AdcHttp http, Dictionary<string, string> files, Dictionary<string, string>? env = null, FixedTime? time = null,
        Func<GoogleExecutableRequest, CancellationToken, Task<GoogleExecutableResult>>? run = null, Func<X509Certificate2, HttpMessageInvoker>? mtls = null) =>
        new(new HttpMessageInvoker(http, disposeHandler: false), name => env?.GetValueOrDefault(name), "/home/adc", time ?? AdcClock(), path => files.GetValueOrDefault(path))
        {
            RunExecutable = run ?? ((_, _) => throw new CheckException("no executable expected")),
            MtlsHttp = mtls ?? (_ => throw new CheckException("no mTLS client expected"))
        };

    private static async Task<string> AdcError(string json, Dictionary<string, string>? env = null, AdcHttp? http = null, Dictionary<string, string>? files = null)
    {
        var all = new Dictionary<string, string>(files ?? []) { ["/adc.json"] = json };
        var adc = AdcFor(http ?? new AdcHttp(), all, env);
        return (await Throws<GoogleCredentialsException>(() => adc.AccessTokenAsync("/adc.json", default))).Message;
    }

    /// <summary>application/x-www-form-urlencoded decoding.</summary>
    private static Dictionary<string, string> AdcForm(string body) => body.Split('&').Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));

    private static string AdcMetrics(string source, bool impersonation, bool lifetime) =>
        $"{GoogleApplicationDefaultCredentials.MetricsPrefix} google-byoid-sdk source/{source} sa-impersonation/{(impersonation ? "true" : "false")} config-lifetime/{(lifetime ? "true" : "false")}";

    private static async Task AdcFileSourceStsAndImpersonation()
    {
        const string Iam = "https://iamcredentials.googleapis.com/v1/projects/-/serviceAccounts/sa@p.iam.gserviceaccount.com:generateAccessToken";
        var http = new AdcHttp().On("POST", AdcSts, AdcStsOk).On("POST", Iam, """{"accessToken":"sa-token","expireTime":"2026-10-08T12:30:00Z"}""");
        var files = new Dictionary<string, string>
        {
            ["/adc.json"] = $$"""
                {"type":"external_account","audience":"{{AdcWorkloadAudience}}","subject_token_type":"urn:ietf:params:oauth:token-type:jwt",
                 "token_url":"{{AdcSts}}","credential_source":{"file":"/var/run/token.jwt"},"client_id":"client~id","client_secret":"s3cr3t",
                 "service_account_impersonation_url":"{{Iam}}","service_account_impersonation":{"token_lifetime_seconds":1800},"quota_project_id":"quota"}
                """,
            ["/var/run/token.jwt"] = "file-subject\n"
        };
        var time = AdcClock();
        var adc = AdcFor(http, files, time: time);
        Equal("sa-token", await adc.AccessTokenAsync("/adc.json", default), "impersonated token");
        Equal(2, http.Requests.Count, "STS exchange then generateAccessToken");
        var sts = http.Requests[0];
        Equal("POST " + AdcSts, sts.Method + " " + sts.Url, "STS request");
        Equal(AdcExchangeHead + "%2F%2Fiam.googleapis.com%2Fprojects%2F123456%2Flocations%2Fglobal%2FworkloadIdentityPools%2Fpool%2Fproviders%2Foidc&" + AdcScopeForm +
            "&" + AdcRequestedType + "&subject_token=file-subject%0A&subject_token_type=urn%3Aietf%3Aparams%3Aoauth%3Atoken-type%3Ajwt", sts.Body,
            "STS form (scope is cloud-platform when impersonating; the file text is not trimmed; no options with client auth)");
        Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client~id:s3cr3t")), sts.Header("authorization"), "basic client authentication");
        Equal(AdcMetrics("file", true, true), sts.Header("x-goog-api-client"), "STS metrics header");
        Equal("application/x-www-form-urlencoded; charset=utf-8", sts.Header("content-type"), "STS content type");
        Equal("application/json", sts.Header("accept"), "STS accept");
        var iam = http.Requests[1];
        Equal("POST " + Iam, iam.Method + " " + iam.Url, "generateAccessToken request");
        Equal("""{"scope":["https://www.googleapis.com/auth/cloud-platform"],"lifetime":"1800s"}""", iam.Body, "generateAccessToken body (token_lifetime_seconds)");
        Equal("Bearer sts-token", iam.Header("authorization"), "STS token authorizes the impersonation");
        Equal("application/json", iam.Header("content-type"), "impersonation content type");
        Check(iam.Header("x-goog-user-project") is null, "quota_project_id is not sent while minting the token");
        time.Now += 24 * 60_000;
        Equal("sa-token", await adc.AccessTokenAsync("/adc.json", default), "cached until five minutes before expireTime");
        Equal(2, http.Requests.Count, "no refresh at 12:24");
        time.Now += 2 * 60_000;
        http.On("POST", Iam, """{"accessToken":"sa-token-2","expireTime":"2026-10-08T13:26:00Z"}""");
        Equal("sa-token-2", await adc.AccessTokenAsync("/adc.json", default), "refreshed inside the five minute window");
        Equal(4, http.Requests.Count, "the subject token is read and exchanged again");
    }

    private static async Task AdcUrlSourceAndWorkforce()
    {
        const string Url = "http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=api://pool";
        var http = new AdcHttp().On("GET", Url, """{"access_token":"url-subject","expires_in":"86399"}""").On("POST", AdcSts, """{"access_token":"sts-forever","token_type":"Bearer"}""");
        var files = new Dictionary<string, string> { ["/adc.json"] = $$"""
            {"type":"external_account","audience":"{{AdcWorkforceAudience}}","subject_token_type":"urn:ietf:params:oauth:token-type:id_token",
             "workforce_pool_user_project":"user-project",
             "credential_source":{"url":"{{Url}}","headers":{"Metadata":"True"},"format":{"type":"json","subject_token_field_name":"access_token"} } }
            """ };
        var time = AdcClock();
        var adc = AdcFor(http, files, time: time);
        Equal("sts-forever", await adc.AccessTokenAsync("/adc.json", default), "STS token without impersonation");
        var get = http.Requests[0];
        Equal("GET " + Url, get.Method + " " + get.Url, "subject token URL");
        Equal("True", get.Header("metadata"), "credential_source.headers");
        Equal("application/json", get.Header("accept"), "json format asks for JSON");
        var sts = http.Requests[1];
        Equal("POST " + AdcSts, sts.Method + " " + sts.Url, "default token_url");
        Equal(AdcExchangeHead + "%2F%2Fiam.googleapis.com%2Flocations%2Fglobal%2FworkforcePools%2Fpool%2Fproviders%2Fprov&" + AdcScopeForm + "&" + AdcRequestedType +
            "&subject_token=url-subject&subject_token_type=urn%3Aietf%3Aparams%3Aoauth%3Atoken-type%3Aid_token&options=%7B%22userProject%22%3A%22user-project%22%7D",
            sts.Body, "workforce userProject travels in options without client auth");
        Check(sts.Header("authorization") is null, "no client authentication");
        Equal(AdcMetrics("url", false, false), sts.Header("x-goog-api-client"), "metrics header");
        time.Now += 30L * 24 * 3600_000;
        Equal("sts-forever", await adc.AccessTokenAsync("/adc.json", default), "an STS token without expires_in never expires");
        Equal(2, http.Requests.Count, "no refresh");

        // Client authentication takes priority over workforce_pool_user_project; an absent client_secret is empty.
        var withClient = new AdcHttp().On("GET", Url, """{"access_token":"url-subject"}""").On("POST", AdcSts, AdcStsOk);
        files["/adc.json"] = files["/adc.json"].Replace("\"workforce_pool_user_project\"", "\"client_id\":\"cid\",\"workforce_pool_user_project\"", StringComparison.Ordinal);
        await AdcFor(withClient, files).AccessTokenAsync("/adc.json", default);
        Check(!withClient.Requests[1].Body.Contains("options=", StringComparison.Ordinal), "no options with client auth");
        Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("cid:")), withClient.Requests[1].Header("authorization"), "client_id with empty secret");

        // universe_domain picks the STS host; camelCase aliases are accepted; the text format takes the body as is.
        var universe = new AdcHttp().On("GET", "http://localhost:8080/token", "text-subject", type: "text/plain").On("POST", "https://sts.example.com/v1/token", AdcStsOk);
        files["/adc.json"] = $$"""
            {"type":"external_account","audience":"{{AdcWorkloadAudience}}","subjectTokenType":"urn:ietf:params:oauth:token-type:jwt","universe_domain":"example.com",
             "credential_source":{"url":"http://localhost:8080/token"} }
            """;
        Equal("sts-token", await AdcFor(universe, files).AccessTokenAsync("/adc.json", default), "universe token");
        Equal("text-subject", AdcForm(universe.Requests[1].Body)["subject_token"], "text subject");
        Equal("urn:ietf:params:oauth:token-type:jwt", AdcForm(universe.Requests[1].Body)["subject_token_type"], "camelCase subjectTokenType");
        Check(universe.Requests[0].Header("accept") is null, "text format sends no accept header");
    }

    private static async Task AdcExternalAccountErrors()
    {
        string Config(string source, string extra = "") =>
            $$"""{"type":"external_account","audience":"{{AdcWorkloadAudience}}","token_url":"{{AdcSts}}","credential_source":{{source}}{{extra}}}""";
        Equal("Invalid credential_source format \"xml\"", await AdcError(Config("""{"file":"/f","format":{"type":"xml"}}""")), "format type");
        Equal("Missing subject_token_field_name for JSON credential_source format", await AdcError(Config("""{"file":"/f","format":{"type":"json"}}""")), "json field");
        Equal("No valid Identity Pool \"credential_source\" provided, must be either file, url, or certificate.",
            await AdcError(Config("""{"file":"/f","url":"http://x"}""")), "file and url");
        Equal("No valid Identity Pool \"credential_source\" provided, must be either file, url, or certificate.", await AdcError(Config("{}")), "empty source");
        Equal("A credential source or subject token supplier must be specified.",
            await AdcError($$"""{"type":"external_account","audience":"{{AdcWorkloadAudience}}"}"""), "no source");
        Equal("Google credentials file is missing audience.", await AdcError("""{"type":"external_account","credential_source":{"file":"/f"}}"""), "audience");
        Equal("workforcePoolUserProject should not be set for non-workforce pool credentials.",
            await AdcError(Config("""{"file":"/f"}""", ",\"workforce_pool_user_project\":\"p\"")), "workforce project on a workload pool");
        Equal("The file at /missing does not exist, or it is not a file.", await AdcError(Config("""{"file":"/missing"}""")), "missing subject file");
        Equal("Unable to parse the subject_token from the credential_source file", await AdcError(Config("""{"file":"/empty"}"""), files: new() { ["/empty"] = "" }), "empty file");
        Equal("Unable to parse the subject_token from the credential_source file",
            await AdcError(Config("""{"file":"/t.json","format":{"type":"json","subject_token_field_name":"id_token"}}"""), files: new() { ["/t.json"] = """{"other":"x"}""" }), "json field absent");
        Equal("Unable to parse the subject_token from the credential_source URL",
            await AdcError(Config("""{"url":"http://meta/token","format":{"type":"json","subject_token_field_name":"id_token"}}"""), http: new AdcHttp().On("GET", "http://meta/token", "{}")), "url field absent");
        Equal("{\"error\":{\"message\":\"denied\"}}", await AdcError(Config("""{"url":"http://meta/token"}"""),
            http: new AdcHttp().On("GET", "http://meta/token", """{"error":{"message":"denied"}}""", HttpStatusCode.Forbidden)), "text responses keep the raw body as the gaxios message");
        Equal("denied", await AdcError(Config("""{"url":"http://meta/token","format":{"type":"json","subject_token_field_name":"t"}}"""),
            http: new AdcHttp().On("GET", "http://meta/token", """{"error":{"message":"denied"}}""", HttpStatusCode.Forbidden)), "json responses use error.message");
        var files = new Dictionary<string, string> { ["/f"] = "subject" };
        Equal("Error code invalid_grant: The audience in ID Token does not match. - https://cloud.google.com/iam/docs/troubleshooting", await AdcError(Config("""{"file":"/f"}"""), files: files,
            http: new AdcHttp().On("POST", AdcSts, """{"error":"invalid_grant","error_description":"The audience in ID Token does not match.","error_uri":"https://cloud.google.com/iam/docs/troubleshooting"}""",
                HttpStatusCode.BadRequest)), "STS OAuth error");
        Equal("Error code invalid_request", await AdcError(Config("""{"file":"/f"}"""), files: files,
            http: new AdcHttp().On("POST", AdcSts, """{"error":"invalid_request"}""", HttpStatusCode.BadRequest)), "STS OAuth error without description");
        Equal("Error code undefined", await AdcError(Config("""{"file":"/f"}"""), files: files,
            http: new AdcHttp().On("POST", AdcSts, "upstream unavailable", HttpStatusCode.ServiceUnavailable, "text/plain")), "STS non-JSON error");
        const string Iam = "https://iamcredentials.googleapis.com/v1/projects/-/serviceAccounts/sa@p.iam.gserviceaccount.com:generateAccessToken";
        Equal("Permission 'iam.serviceAccounts.getAccessToken' denied", await AdcError(Config("""{"file":"/f"}""", $",\"service_account_impersonation_url\":\"{Iam}\""), files: files,
            http: new AdcHttp().On("POST", AdcSts, AdcStsOk).On("POST", Iam, """{"error":{"code":403,"message":"Permission 'iam.serviceAccounts.getAccessToken' denied","status":"PERMISSION_DENIED"}}""",
                HttpStatusCode.Forbidden)), "impersonation failure carries the gaxios message");
        Equal("No valid AWS \"credential_source\" provided", await AdcError(Config("""{"environment_id":"gcp1","regional_cred_verification_url":"https://sts"}""")), "AWS environment id");
        Equal("No valid AWS \"credential_source\" provided", await AdcError(Config("""{"environment_id":"aws1"}""")), "AWS without regional_cred_verification_url");
        Equal("aws version \"2\" is not supported in the current build.",
            await AdcError(Config("""{"environment_id":"aws2","regional_cred_verification_url":"https://sts.{region}.amazonaws.com"}""")), "AWS version");
        Equal("No valid Pluggable Auth \"credential_source\" provided.", await AdcError(Config("""{"executable":{"command":""}}""")), "executable command");
        Equal("Timeout must be between 5000 and 120000 milliseconds.", await AdcError(Config("""{"executable":{"command":"run","timeout_millis":4999}}""")), "executable timeout");
        Equal("Timeout must be between 5000 and 120000 milliseconds.", await AdcError(Config("""{"executable":{"command":"run","timeout_millis":120001}}""")), "executable timeout max");
    }

    /// <summary>The library's own SigV4 (awsrequestsigner.ts), written independently of PiSharp's AwsSigV4.</summary>
    private static string AdcLibrarySigV4(string host, string query, string region, string accessKey, string secret, string? sessionToken, DateTimeOffset now)
    {
        static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
        static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture); var dateStamp = amzDate[..8];
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = host, ["x-amz-date"] = amzDate };
        if (sessionToken is not null) headers["x-amz-security-token"] = sessionToken;
        var signedHeaders = string.Join(';', headers.Keys);
        var canonical = $"POST\n/\n{query}\n{string.Concat(headers.Select(header => $"{header.Key}:{header.Value}\n"))}\n{signedHeaders}\n{Hex(SHA256.HashData([]))}";
        var scope = $"{dateStamp}/{region}/sts/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secret), dateStamp), region), "sts"), "aws4_request");
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={Hex(Hmac(key, toSign))}";
    }

    private static string AdcAwsSubject(string region, string authorization, string? sessionToken, string audience, DateTimeOffset now)
    {
        var host = $"sts.{region}.amazonaws.com";
        return $"{{\"url\":\"https://{host}?Action=GetCallerIdentity&Version=2011-06-15\",\"method\":\"POST\",\"headers\":[" +
            $"{{\"key\":\"authorization\",\"value\":\"{authorization}\"}},{{\"key\":\"host\",\"value\":\"{host}\"}}," +
            $"{{\"key\":\"x-amz-date\",\"value\":\"{now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}\"}}," +
            (sessionToken is null ? "" : $"{{\"key\":\"x-amz-security-token\",\"value\":\"{sessionToken}\"}},") +
            $"{{\"key\":\"x-goog-cloud-target-resource\",\"value\":\"{audience}\"}}]}}";
    }

    private const string AdcAwsAudience = "//iam.googleapis.com/projects/123456/locations/global/workloadIdentityPools/aws-pool/providers/aws";
    private static string AdcAwsConfig(string extraSource = "") => $$"""
        {"type":"external_account","audience":"{{AdcAwsAudience}}","subject_token_type":"urn:ietf:params:aws:token-type:aws4_request","token_url":"{{AdcSts}}",
         "credential_source":{"environment_id":"aws1","region_url":"http://169.254.169.254/latest/meta-data/placement/availability-zone",
          "url":"http://169.254.169.254/latest/meta-data/iam/security-credentials",
          "regional_cred_verification_url":"https://sts.{region}.amazonaws.com?Action=GetCallerIdentity&Version=2011-06-15"{{extraSource}} } }
        """;

    private static async Task AdcAwsEnvironment()
    {
        var http = new AdcHttp().On("POST", AdcSts, AdcStsOk);
        var env = new Dictionary<string, string> { ["AWS_DEFAULT_REGION"] = "us-east-2", ["AWS_ACCESS_KEY_ID"] = "AKIDEXAMPLE", ["AWS_SECRET_ACCESS_KEY"] = "env-secret",
            ["AWS_SESSION_TOKEN"] = "session/token+=" };
        var time = AdcClock();
        var adc = AdcFor(http, new() { ["/adc.json"] = AdcAwsConfig() }, env, time);
        Equal("sts-token", await adc.AccessTokenAsync("/adc.json", default), "token");
        Equal(1, http.Requests.Count, "environment region and credentials need no metadata requests");
        var sts = http.Requests[0];
        Check(sts.Body.Contains("&subject_token=%257B%2522url%2522%253A%2522https%253A%252F%252Fsts.us-east-2.amazonaws.com%253FAction%253DGetCallerIdentity%2526Version%253D2011-06-15%2522%252C",
            StringComparison.Ordinal), "the encodeURIComponent'd JSON is form-encoded again: " + sts.Body);
        var form = AdcForm(sts.Body);
        Check(form["subject_token"].All(character => char.IsAsciiLetterOrDigit(character) || "-_.!~*'()%".Contains(character)), "subject token is encodeURIComponent output");
        var now = time.GetUtcNow();
        var authorization = AdcLibrarySigV4("sts.us-east-2.amazonaws.com", "Action=GetCallerIdentity&Version=2011-06-15", "us-east-2", "AKIDEXAMPLE", "env-secret", "session/token+=", now);
        Equal(AdcAwsSubject("us-east-2", authorization, "session/token+=", AdcAwsAudience, now), Uri.UnescapeDataString(form["subject_token"]), "serialized signed GetCallerIdentity");
        Equal("urn:ietf:params:aws:token-type:aws4_request", form["subject_token_type"], "subject token type");
        Equal(AdcMetrics("aws", false, false), sts.Header("x-goog-api-client"), "metrics");
        // AWS_REGION wins over AWS_DEFAULT_REGION; no session token means no x-amz-security-token.
        var second = new AdcHttp().On("POST", AdcSts, AdcStsOk);
        env["AWS_REGION"] = "eu-west-1"; env.Remove("AWS_SESSION_TOKEN");
        await AdcFor(second, new() { ["/adc.json"] = AdcAwsConfig() }, env, time).AccessTokenAsync("/adc.json", default);
        var subject = Uri.UnescapeDataString(AdcForm(second.Requests[0].Body)["subject_token"]);
        Equal(AdcAwsSubject("eu-west-1", AdcLibrarySigV4("sts.eu-west-1.amazonaws.com", "Action=GetCallerIdentity&Version=2011-06-15", "eu-west-1", "AKIDEXAMPLE", "env-secret", null, now),
            null, AdcAwsAudience, now), subject, "AWS_REGION and no session token");
    }

    private static async Task AdcAwsMetadata()
    {
        const string Imds = "http://169.254.169.254";
        var http = new AdcHttp().On("PUT", Imds + "/latest/api/token", "imds-session", type: "text/plain")
            .On("GET", Imds + "/latest/meta-data/placement/availability-zone", "us-east-2b", type: "text/plain")
            .On("GET", Imds + "/latest/meta-data/iam/security-credentials", "gce-role", type: "text/plain")
            .On("GET", Imds + "/latest/meta-data/iam/security-credentials/gce-role",
                """{"Code":"Success","Type":"AWS-HMAC","AccessKeyId":"ASIAIMDS","SecretAccessKey":"imds-secret","Token":"imds-token","Expiration":"2026-10-08T18:00:00Z"}""")
            .On("POST", AdcSts, AdcStsOk);
        var time = AdcClock();
        var adc = AdcFor(http, new() { ["/adc.json"] = AdcAwsConfig($",\"imdsv2_session_token_url\":\"{Imds}/latest/api/token\"") }, new(), time);
        Equal("sts-token", await adc.AccessTokenAsync("/adc.json", default), "token");
        Equal(string.Join('\n', "PUT " + Imds + "/latest/api/token", "GET " + Imds + "/latest/meta-data/placement/availability-zone", "PUT " + Imds + "/latest/api/token",
            "GET " + Imds + "/latest/meta-data/iam/security-credentials", "GET " + Imds + "/latest/meta-data/iam/security-credentials/gce-role", "POST " + AdcSts),
            string.Join('\n', http.Requests.Select(request => request.Method + " " + request.Url)), "IMDSv2 session per lookup, region, role, credentials, STS");
        Check(http.Requests.Where(request => request.Method == "PUT").All(request => request.Header("x-aws-ec2-metadata-token-ttl-seconds") == "300"), "session token TTL");
        Check(http.Requests.Where(request => request.Method == "GET").All(request => request.Header("x-aws-ec2-metadata-token") == "imds-session"), "session token on metadata reads");
        Equal("application/json", http.Requests[4].Header("accept"), "credentials are read as JSON");
        Check(http.Requests[1].Header("accept") is null && http.Requests[3].Header("accept") is null, "region and role are read as text");
        var now = time.GetUtcNow();
        var authorization = AdcLibrarySigV4("sts.us-east-2.amazonaws.com", "Action=GetCallerIdentity&Version=2011-06-15", "us-east-2", "ASIAIMDS", "imds-secret", "imds-token", now);
        Equal(AdcAwsSubject("us-east-2", authorization, "imds-token", AdcAwsAudience, now), Uri.UnescapeDataString(AdcForm(http.Requests[5].Body)["subject_token"]),
            "availability zone minus its last letter is the region");
        Equal("Unable to determine AWS region due to missing \"options.credential_source.region_url\"",
            await AdcError(AdcAwsConfig().Replace("\"region_url\":", "\"unused\":", StringComparison.Ordinal)), "no region");
        Equal("Unable to determine AWS role name due to missing \"options.credential_source.url\"",
            await AdcError(AdcAwsConfig().Replace("\"url\":", "\"unused\":", StringComparison.Ordinal), new() { ["AWS_REGION"] = "us-east-1" }), "no role url");
    }

    private static async Task AdcExecutable()
    {
        const string Iam = "https://iamcredentials.googleapis.com/v1/projects/-/serviceAccounts/exec-sa@p.iam.gserviceaccount.com:generateAccessToken";
        const string Output = "/tmp/exec-out.json";
        var http = new AdcHttp().On("POST", AdcSts, AdcStsOk).On("POST", Iam, """{"accessToken":"exec-sa-token","expireTime":"2026-10-08T12:00:00Z"}""");
        var time = AdcClock();
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var files = new Dictionary<string, string> { ["/adc.json"] = $$"""
            {"type":"external_account","audience":"{{AdcWorkloadAudience}}","subject_token_type":"urn:ietf:params:oauth:token-type:id_token","token_url":"{{AdcSts}}",
             "service_account_impersonation_url":"{{Iam}}",
             "credential_source":{"executable":{"command":"\"/opt/my tools/token.sh\" --audience=\"a b\" -v","timeout_millis":5000,"output_file":"{{Output}}"} } }
            """ };
        var env = new Dictionary<string, string>();
        var runs = new List<GoogleExecutableRequest>();
        var result = new GoogleExecutableResult(0, "");
        var adc = AdcFor(http, files, env, time, (request, _) => { runs.Add(request); return Task.FromResult(result); });
        string Response(string fields) => "{\"version\":1," + fields + "}";
        string IdToken(string token, long expires) => Response($"\"success\":true,\"token_type\":\"urn:ietf:params:oauth:token-type:id_token\",\"id_token\":\"{token}\",\"expiration_time\":{expires}");
        async Task<string> Fails() => (await Throws<GoogleCredentialsException>(() => adc.AccessTokenAsync("/adc.json", default))).Message;
        string LastSubject() => AdcForm(http.Requests.Last(request => request.Url == AdcSts).Body)["subject_token"];

        Equal("Pluggable Auth executables need to be explicitly allowed to run by setting the GOOGLE_EXTERNAL_ACCOUNT_ALLOW_EXECUTABLES environment Variable to 1.",
            await Fails(), "executables are opt-in");
        env["GOOGLE_EXTERNAL_ACCOUNT_ALLOW_EXECUTABLES"] = "1";
        result = new(0, IdToken("exec-id-token", now + 3600));
        Equal("exec-sa-token", await adc.AccessTokenAsync("/adc.json", default), "token through the executable");
        var run = runs.Single();
        Equal("/opt/my tools/token.sh", run.Command, "quoted command component");
        Equal("--audience=\"a b\"|-v", string.Join('|', run.Arguments), "arguments split on spaces outside quotes; inner quotes stay");
        Equal(TimeSpan.FromMilliseconds(5000), run.Timeout, "timeout_millis");
        Equal($"GOOGLE_EXTERNAL_ACCOUNT_AUDIENCE={AdcWorkloadAudience};GOOGLE_EXTERNAL_ACCOUNT_TOKEN_TYPE=urn:ietf:params:oauth:token-type:id_token;" +
            $"GOOGLE_EXTERNAL_ACCOUNT_INTERACTIVE=0;GOOGLE_EXTERNAL_ACCOUNT_OUTPUT_FILE={Output};GOOGLE_EXTERNAL_ACCOUNT_IMPERSONATED_EMAIL=exec-sa@p.iam.gserviceaccount.com",
            string.Join(';', run.Environment.Select(pair => pair.Key + "=" + pair.Value)), "executable environment");
        Equal("exec-id-token", LastSubject(), "id_token is the subject token");
        Equal(AdcMetrics("executable", true, false), http.Requests.Last(request => request.Url == AdcSts).Header("x-goog-api-client"), "metrics");
        Equal("""{"scope":["https://www.googleapis.com/auth/cloud-platform"],"lifetime":"3600s"}""", http.Requests.Last().Body, "default lifetime");

        // A valid, unexpired output file is used instead of running the executable (expireTime "now" makes every call refresh).
        files[Output] = Response($"\"success\":true,\"token_type\":\"urn:ietf:params:oauth:token-type:saml2\",\"saml_response\":\"cached-saml\",\"expiration_time\":{now + 600}");
        await adc.AccessTokenAsync("/adc.json", default);
        Equal(1, runs.Count, "cached output file, no run");
        Equal("cached-saml", LastSubject(), "saml_response from the output file");
        files[Output] = IdToken("stale", now - 1);
        result = new(0, IdToken("fresh", now + 3600));
        await adc.AccessTokenAsync("/adc.json", default);
        Equal(2, runs.Count, "expired output file runs the executable");
        Equal("fresh", LastSubject(), "fresh subject");
        files[Output] = "";
        await adc.AccessTokenAsync("/adc.json", default);
        Equal(3, runs.Count, "an empty output file runs the executable");
        files[Output] = "not json";
        Equal("The output file contained an invalid response: not json", await Fails(), "invalid output file");
        files[Output] = """{"success":true}""";
        Equal("Executable response must contain a 'version' field.", await Fails(), "output file response is validated");
        files.Remove(Output);

        (string Output, string Message)[] failures =
        [
            ("not json", "The executable returned an invalid response: not json"),
            (Response("\"success\":false,\"code\":\"401\",\"message\":\"Caller not authorized.\""), "The executable failed with exit code: 401 and error message: Caller not authorized.."),
            ("{\"version\":2,\"success\":true,\"token_type\":\"urn:ietf:params:oauth:token-type:jwt\",\"id_token\":\"t\",\"expiration_time\":" + (now + 60) + "}",
                "Version of executable is not currently supported, maximum supported version is 1."),
            (Response("\"success\":true,\"token_type\":\"urn:ietf:params:oauth:token-type:jwt\",\"id_token\":\"t\""),
                "The executable response must contain the `expiration_time` field for successful responses when an output_file has been specified in the configuration."),
            (IdToken("old", now - 1), "Executable response is expired."),
            (Response("\"success\":true,\"token_type\":\"bearer\",\"id_token\":\"t\""), "Executable response must contain a 'token_type' field when successful and it must be one of " +
                "urn:ietf:params:oauth:token-type:id_token, urn:ietf:params:oauth:token-type:jwt, or urn:ietf:params:oauth:token-type:saml2."),
            (Response("\"success\":true,\"token_type\":\"urn:ietf:params:oauth:token-type:jwt\""),
                "Executable response must contain a 'id_token' field when token_type=urn:ietf:params:oauth:token-type:id_token or urn:ietf:params:oauth:token-type:jwt."),
            (Response("\"success\":true,\"token_type\":\"urn:ietf:params:oauth:token-type:saml2\""),
                "Executable response must contain a 'saml_response' field when token_type=urn:ietf:params:oauth:token-type:saml2."),
            (Response("\"success\":false,\"message\":\"m\""), "Executable response must contain a 'code' field when unsuccessful."),
            (Response("\"success\":false,\"code\":\"1\""), "Executable response must contain a 'message' field when unsuccessful."),
            ("{\"version\":1}", "Executable response must contain a 'success' field."),
        ];
        foreach (var (output, message) in failures)
        {
            result = new(0, output);
            Equal(message, await Fails(), "executable response: " + output);
        }
        result = new(1, "boom");
        Equal("The executable failed with exit code: 1 and error message: boom.", await Fails(), "non-zero exit");
        result = new(null, "partial");
        Equal("The executable failed to finish within the timeout specified.", await Fails(), "timeout");
        Equal("Provided command: \"   \" could not be parsed.", await AdcError($$"""
            {"type":"external_account","audience":"{{AdcWorkloadAudience}}","credential_source":{"executable":{"command":"   "} } }
            """), "unparsable command");
    }

    private static async Task AdcCertificate()
    {
        var now = AdcClock().GetUtcNow();
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=ADC Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        using var leafKey = RSA.Create(2048);
        using var leaf = new CertificateRequest("CN=adc-workload", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).Create(ca, now.AddDays(-1), now.AddDays(1), [1, 2, 3, 4]);
        string leafPem = leaf.ExportCertificatePem(), caPem = ca.ExportCertificatePem();
        string Base64(X509Certificate2 certificate) => Convert.ToBase64String(certificate.RawData);
        var files = new Dictionary<string, string>
        {
            ["/certs/config.json"] = """{"cert_configs":{"workload":{"cert_path":"/certs/leaf.pem","key_path":"/certs/leaf.key"}}}""",
            ["/certs/leaf.pem"] = leafPem, ["/certs/leaf.key"] = leafKey.ExportPkcs8PrivateKeyPem(), ["/certs/chain.pem"] = leafPem + "\n" + caPem,
        };
        string Config(string certificate) => $$"""
            {"type":"external_account","audience":"{{AdcWorkloadAudience}}","subject_token_type":"urn:ietf:params:oauth:token-type:mtls",
             "token_url":"https://sts.mtls.googleapis.com/v1/token","credential_source":{"certificate":{{certificate}} } }
            """;
        async Task<(AdcHttp Mtls, List<X509Certificate2> Clients, AdcHttp Plain)> Run(string certificate, Dictionary<string, string>? env = null)
        {
            var mtlsHttp = new AdcHttp().On("POST", "https://sts.mtls.googleapis.com/v1/token", AdcStsOk);
            var plain = new AdcHttp(); var clients = new List<X509Certificate2>();
            files["/adc.json"] = Config(certificate);
            Equal("sts-token", await AdcFor(plain, files, env, mtls: certificateWithKey => { clients.Add(certificateWithKey); return new HttpMessageInvoker(mtlsHttp, false); })
                .AccessTokenAsync("/adc.json", default), "token over mTLS");
            return (mtlsHttp, clients, plain);
        }
        var (mtls, used, direct) = await Run("""{"certificate_config_location":"/certs/config.json","trust_chain_path":"/certs/chain.pem"}""");
        Equal(0, direct.Requests.Count, "the exchange does not use the plain client");
        Check(used.Single().HasPrivateKey && used.Single().Thumbprint == leaf.Thumbprint, "the leaf and its key are the mTLS client certificate");
        var form = AdcForm(mtls.Requests.Single().Body);
        Equal($"[\"{Base64(leaf)}\",\"{Base64(ca)}\"]", form["subject_token"], "leaf first, then the chain");
        Equal("urn:ietf:params:oauth:token-type:mtls", form["subject_token_type"], "subject token type");
        Equal(AdcMetrics("certificate", false, false), mtls.Requests.Single().Header("x-goog-api-client"), "metrics");
        Equal($"[\"{Base64(leaf)}\"]", AdcForm((await Run("""{"certificate_config_location":"/certs/config.json"}""")).Mtls.Requests.Single().Body)["subject_token"], "leaf only");
        files["/certs/ca-only.pem"] = caPem;
        Equal($"[\"{Base64(leaf)}\",\"{Base64(ca)}\"]", AdcForm((await Run("""{"certificate_config_location":"/certs/config.json","trust_chain_path":"/certs/ca-only.pem"}"""))
            .Mtls.Requests.Single().Body)["subject_token"], "the leaf is prepended to a chain without it");
        Equal($"[\"{Base64(leaf)}\"]", AdcForm((await Run("""{"use_default_certificate_config":true}""", new() { ["GOOGLE_API_CERTIFICATE_CONFIG"] = "/certs/config.json" }))
            .Mtls.Requests.Single().Body)["subject_token"], "GOOGLE_API_CERTIFICATE_CONFIG");
        files["/certs/bad-order.pem"] = caPem + "\n" + leafPem;
        async Task<string> Fails(string certificate, Dictionary<string, string>? env = null)
        {
            files["/adc.json"] = Config(certificate);
            return (await Throws<GoogleCredentialsException>(() => AdcFor(new AdcHttp(), files, env, mtls: _ => new HttpMessageInvoker(new AdcHttp(), false))
                .AccessTokenAsync("/adc.json", default))).Message;
        }
        Equal("Leaf certificate exists in the trust chain but is not the first entry (found at index 1).",
            await Fails("""{"certificate_config_location":"/certs/config.json","trust_chain_path":"/certs/bad-order.pem"}"""), "leaf out of order");
        Equal("Either `useDefaultCertificateConfig` must be true or a `certificateConfigLocation` must be provided.", await Fails("""{"trust_chain_path":"/x"}"""), "no config");
        Equal("Both `useDefaultCertificateConfig` and `certificateConfigLocation` cannot be provided.",
            await Fails("""{"use_default_certificate_config":true,"certificate_config_location":"/certs/config.json"}"""), "both");
        Equal("Provided certificate config path is invalid: /nope.json", await Fails("""{"certificate_config_location":"/nope.json"}"""), "missing config location");
        Equal("Path from environment variable \"GOOGLE_API_CERTIFICATE_CONFIG\" is invalid: /nope.json",
            await Fails("""{"use_default_certificate_config":true}""", new() { ["GOOGLE_API_CERTIFICATE_CONFIG"] = "/nope.json" }), "invalid environment path");
        Equal($"Could not find certificate configuration file. Searched override path, the \"GOOGLE_API_CERTIFICATE_CONFIG\" env var, and the gcloud path ({Path.Combine("/gcloud", "certificate_config.json")}).",
            await Fails("""{"use_default_certificate_config":true}""", new() { ["CLOUDSDK_CONFIG"] = "/gcloud" }), "nothing found");
        files["/certs/no-key.json"] = """{"cert_configs":{"workload":{"cert_path":"/certs/leaf.pem"}}}""";
        Equal("Certificate config file (/certs/no-key.json) is missing required \"cert_path\" or \"key_path\" in the workload config.",
            await Fails("""{"certificate_config_location":"/certs/no-key.json"}"""), "key_path");
    }

    private static async Task AdcImpersonatedAuthorizedUser()
    {
        const string Target = "https://iamcredentials.googleapis.com/v1/projects/-/serviceAccounts/target@p.iam.gserviceaccount.com:generateAccessToken";
        const string Source = """{"type":"authorized_user","client_id":"cid","client_secret":"cs","refresh_token":"rt","quota_project_id":"src-quota"}""";
        var http = new AdcHttp().On("POST", "https://oauth2.googleapis.com/token", """{"access_token":"user-token","expires_in":3599}""")
            .On("POST", Target, """{"accessToken":"impersonated-token","expireTime":"2026-10-08T13:00:00Z"}""");
        var files = new Dictionary<string, string> { ["/adc.json"] = $$"""
            {"type":"impersonated_service_account","service_account_impersonation_url":"{{Target}}","delegates":["d1@p.iam.gserviceaccount.com"],"source_credentials":{{Source}}}
            """ };
        var adc = AdcFor(http, files);
        Equal("impersonated-token", await adc.AccessTokenAsync("/adc.json", default), "impersonated token");
        Equal("client_id=cid&client_secret=cs&refresh_token=rt&grant_type=refresh_token", http.Requests[0].Body, "source refresh");
        var iam = http.Requests[1];
        Equal("POST " + Target, iam.Method + " " + iam.Url, "generateAccessToken");
        Equal("""{"delegates":["d1@p.iam.gserviceaccount.com"],"scope":["https://www.googleapis.com/auth/cloud-platform"],"lifetime":"3600s"}""", iam.Body, "request body");
        Equal("Bearer user-token", iam.Header("authorization"), "source token");
        Equal("src-quota", iam.Header("x-goog-user-project"), "source quota project rides on the source client's request");
        Equal("application/json", iam.Header("content-type"), "JSON body");

        // The target principal comes from the URL (generateIdToken too); the request always goes to {endpoint}/v1/...:generateAccessToken.
        var other = new AdcHttp().On("POST", "https://oauth2.googleapis.com/token", """{"access_token":"user-token","expires_in":3599}""")
            .On("POST", "https://iamcredentials.googleapis.com/v1/projects/-/serviceAccounts/other@p.iam.gserviceaccount.com:generateAccessToken",
                """{"accessToken":"other-token","expireTime":"2026-10-08T13:00:00Z"}""");
        files["/adc.json"] = $$"""
            {"type":"impersonated_service_account","service_account_impersonation_url":"https://example.com/x/serviceAccounts/other@p.iam.gserviceaccount.com:generateIdToken",
             "lifetime":600,"source_credentials":{{Source}}}
            """;
        Equal("other-token", await AdcFor(other, files).AccessTokenAsync("/adc.json", default), "generateIdToken URL");
        Equal("""{"delegates":[],"scope":["https://www.googleapis.com/auth/cloud-platform"],"lifetime":"600s"}""", other.Requests[1].Body, "lifetime and empty delegates");

        Equal("The incoming JSON object does not contain a source_credentials field",
            await AdcError("""{"type":"impersonated_service_account","service_account_impersonation_url":"x"}"""), "no source");
        Equal("The incoming JSON object does not contain a service_account_impersonation_url field",
            await AdcError($$"""{"type":"impersonated_service_account","source_credentials":{{Source}}}"""), "no url");
        Equal("Cannot extract target principal from https://example.com/bad",
            await AdcError($$"""{"type":"impersonated_service_account","service_account_impersonation_url":"https://example.com/bad","source_credentials":{{Source}}}"""), "principal");
        var longUrl = "https://iamcredentials.googleapis.com/v1/projects/-/serviceAccounts/" + new string('a', 200) + "@p.iam.gserviceaccount.com:generateAccessToken";
        Equal("Target principal is too long: " + longUrl,
            await AdcError($$"""{"type":"impersonated_service_account","service_account_impersonation_url":"{{longUrl}}","source_credentials":{{Source}}}"""), "url length");
        Equal("Universe domain googleapis.com in source credentials does not match example.com universe domain set for impersonated credentials.",
            await AdcError($$"""{"type":"impersonated_service_account","universe_domain":"example.com","service_account_impersonation_url":"{{Target}}","source_credentials":{{Source}}}"""),
            "universe mismatch");
        string Config() => $$"""{"type":"impersonated_service_account","service_account_impersonation_url":"{{Target}}","source_credentials":{{Source}}}""";
        AdcHttp Peer(string body, HttpStatusCode status, string type) => new AdcHttp()
            .On("POST", "https://oauth2.googleapis.com/token", """{"access_token":"user-token","expires_in":3599}""").On("POST", Target, body, status, type);
        Equal("PERMISSION_DENIED: unable to impersonate: Permission 'iam.serviceAccounts.getAccessToken' denied on resource (or it may not exist).",
            await AdcError(Config(), http: Peer("""{"error":{"code":403,"message":"Permission 'iam.serviceAccounts.getAccessToken' denied on resource (or it may not exist).","status":"PERMISSION_DENIED"}}""",
                HttpStatusCode.Forbidden, "application/json")), "IAM error status and message");
        Equal("unable to impersonate: Error: backend error", await AdcError(Config(), http: Peer("backend error", HttpStatusCode.InternalServerError, "text/plain")), "IAM text error");
        Equal("unable to impersonate: Error: Google token request failed with status 400.",
            await AdcError(Config(), http: new AdcHttp().On("POST", "https://oauth2.googleapis.com/token", """{"error":"invalid_grant"}""", HttpStatusCode.BadRequest)), "source failure");
    }

    private static async Task AdcImpersonatedExternalAccount()
    {
        const string Target = "https://iamcredentials.googleapis.com/v1/projects/-/serviceAccounts/target@p.iam.gserviceaccount.com:generateAccessToken";
        var http = new AdcHttp().On("POST", AdcSts, AdcStsOk).On("POST", Target, """{"accessToken":"chained-token","expireTime":"2026-10-08T13:00:00Z"}""");
        var files = new Dictionary<string, string>
        {
            ["/adc.json"] = $$"""
                {"type":"impersonated_service_account","service_account_impersonation_url":"{{Target}}","delegates":[],
                 "source_credentials":{"type":"external_account","audience":"{{AdcWorkloadAudience}}","subject_token_type":"urn:ietf:params:oauth:token-type:jwt",
                  "token_url":"{{AdcSts}}","credential_source":{"file":"/token"} } }
                """,
            ["/token"] = "subject"
        };
        Equal("chained-token", await AdcFor(http, files).AccessTokenAsync("/adc.json", default), "external account source");
        Equal(AdcMetrics("file", false, false), http.Requests[0].Header("x-goog-api-client"), "source exchange metrics");
        Equal("subject", AdcForm(http.Requests[0].Body)["subject_token"], "source subject");
        Equal("Bearer sts-token", http.Requests[1].Header("authorization"), "STS token authorizes generateAccessToken");
        Equal("""{"delegates":[],"scope":["https://www.googleapis.com/auth/cloud-platform"],"lifetime":"3600s"}""", http.Requests[1].Body, "body");
    }

    private static async Task AdcExternalAuthorizedUser()
    {
        const string TokenUrl = "https://sts.googleapis.com/v1/oauthtoken";
        var http = new AdcHttp().On("POST", TokenUrl, """{"access_token":"ext-at","expires_in":3600,"refresh_token":"ext-rt-2"}""");
        var files = new Dictionary<string, string> { ["/adc.json"] = $$"""
            {"type":"external_account_authorized_user","audience":"{{AdcWorkforceAudience}}","refresh_token":"ext-rt","token_url":"{{TokenUrl}}",
             "token_info_url":"https://sts.googleapis.com/v1/introspect","revoke_url":"https://sts.googleapis.com/v1/revoke","client_id":"ext-cid","client_secret":"ext-cs",
             "quota_project_id":"q"}
            """ };
        var time = AdcClock();
        var adc = AdcFor(http, files, time: time);
        Equal("ext-at", await adc.AccessTokenAsync("/adc.json", default), "token");
        var first = http.Requests.Single();
        Equal("POST " + TokenUrl, first.Method + " " + first.Url, "token_url");
        Equal("grant_type=refresh_token&refresh_token=ext-rt", first.Body, "refresh form");
        Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ext-cid:ext-cs")), first.Header("authorization"), "basic client auth");
        Equal("application/json", first.Header("accept"), "accept");
        time.Now += 56 * 60_000;
        http.On("POST", TokenUrl, """{"access_token":"ext-at-2","expires_in":3600}""");
        Equal("ext-at-2", await adc.AccessTokenAsync("/adc.json", default), "refreshed");
        Equal("grant_type=refresh_token&refresh_token=ext-rt-2", http.Requests[1].Body, "the rotated refresh token is used");
        var defaults = new AdcHttp().On("POST", "https://sts.example.com/v1/oauthtoken", """{"access_token":"u"}""");
        files["/adc.json"] = """{"type":"external_account_authorized_user","refresh_token":"r","client_id":"c","universe_domain":"example.com"}""";
        Equal("u", await AdcFor(defaults, files).AccessTokenAsync("/adc.json", default), "default token_url from universe_domain");
        Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("c:")), defaults.Requests[0].Header("authorization"), "missing secret is empty");
        Equal("Error code invalid_grant: Bad refresh token - https://example.com/help", await AdcError(
            """{"type":"external_account_authorized_user","refresh_token":"r","client_id":"c","client_secret":"s"}""",
            http: new AdcHttp().On("POST", TokenUrl, """{"error":"invalid_grant","error_description":"Bad refresh token","error_uri":"https://example.com/help"}""", HttpStatusCode.BadRequest)),
            "OAuth error");
    }
}
