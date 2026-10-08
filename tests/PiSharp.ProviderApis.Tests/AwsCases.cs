using System.Security.Cryptography;
using System.Text;
using PiSharp.AI.Protocols.Bedrock;

internal static partial class Program
{
    private const string StsResponse = """
        <AssumeRoleResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/"><AssumeRoleResult><Credentials>
        <AccessKeyId>ASIAROLE</AccessKeyId><SecretAccessKey>role-secret</SecretAccessKey><SessionToken>role-token</SessionToken>
        <Expiration>2026-10-08T13:00:00Z</Expiration></Credentials></AssumeRoleResult></AssumeRoleResponse>
        """;

    private static async Task AwsCredentialChainCases()
    {
        var home = Temp("aws-chain"); var aws = Path.Combine(home, ".aws"); Directory.CreateDirectory(aws);
        File.WriteAllText(Path.Combine(aws, "credentials"),
            "[default]\naws_access_key_id = AKIDDEFAULT\naws_secret_access_key = default-secret ; trailing comment\n\n[base]\naws_access_key_id=AKIDBASE\naws_secret_access_key=base-secret\n");
        File.WriteAllText(Path.Combine(aws, "config"), string.Join('\n',
            "[profile role]", "role_arn = arn:aws:iam::123456789012:role/dev", "source_profile = base", "region = eu-west-1",
            "[profile sso]", "sso_session = corp", "sso_account_id = 111122223333", "sso_role_name = Dev",
            "[sso-session corp]", "sso_start_url = https://corp.awsapps.com/start", "sso_region = us-east-2",
            "[profile proc]", "credential_process = /usr/bin/creds", "[profile web]", "role_arn = arn:aws:iam::1:role/web", "web_identity_token_file = ~/token.jwt", ""));
        File.WriteAllText(Path.Combine(home, "token.jwt"), "web-token\n");
        var cache = Path.Combine(aws, "sso", "cache"); Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes("corp"))) + ".json"),
            """{"accessToken":"sso-access","expiresAt":"2026-10-08T18:00:00Z","region":"us-east-2"}""");
        var time = new FixedTime(BedrockTime.ToUnixTimeMilliseconds());
        var http = new FakeHttp()
            .OnUrl("https://sts.eu-west-1.amazonaws.com/", request => Text(StsResponse, System.Net.HttpStatusCode.OK, "text/xml"))
            .OnUrl("https://sts.us-east-1.amazonaws.com/", request => Text(StsResponse.Replace("AssumeRole", "AssumeRoleWithWebIdentity").Replace("ASIAROLE", "ASIAWEB"), System.Net.HttpStatusCode.OK, "text/xml"))
            .OnUrl("https://portal.sso.us-east-2.amazonaws.com/federation/credentials", _ => Json("""{"roleCredentials":{"accessKeyId":"ASIASSO","secretAccessKey":"sso-secret","sessionToken":"sso-token","expiration":1791460800000}}"""))
            .OnUrl("http://169.254.170.2/creds", _ => Json("""{"AccessKeyId":"ASIAECS","SecretAccessKey":"ecs-secret","Token":"ecs-token","Expiration":"2026-10-08T12:03:00Z"}"""))
            .OnUrl("http://169.254.169.254/latest/api/token", _ => Text("imds-token", System.Net.HttpStatusCode.OK))
            .OnUrl("http://169.254.169.254/latest/meta-data/iam/security-credentials/role-a", _ => Json("""{"AccessKeyId":"ASIAIMDS","SecretAccessKey":"imds-secret","Token":"imds-token","Expiration":"2026-10-08T18:00:00Z"}"""))
            .OnUrl("http://169.254.169.254/latest/meta-data/iam/security-credentials/", _ => Text("role-a\n", System.Net.HttpStatusCode.OK));
        AwsEnvironment Env(Dictionary<string, string?> values) => new(name => values.GetValueOrDefault(name), home, new HttpMessageInvoker(http, disposeHandler: false), time);
        // fromEnv only without a configured profile.
        Equal("AKIDENV", (await new AwsCredentialChain(Env(new() { ["AWS_ACCESS_KEY_ID"] = "AKIDENV", ["AWS_SECRET_ACCESS_KEY"] = "s", ["AWS_SESSION_TOKEN"] = "t" })).ResolveAsync(default)).AccessKeyId, "env");
        Equal("AKIDDEFAULT", (await new AwsCredentialChain(Env(new())).ResolveAsync(default)).AccessKeyId, "default profile (inline comment stripped)");
        Equal("default-secret", (await new AwsCredentialChain(Env(new())).ResolveAsync(default)).SecretAccessKey, "comment stripped");
        // Assume role: STS signed with the source profile's keys in the profile's region.
        var role = await new AwsCredentialChain(Env(new() { ["AWS_ACCESS_KEY_ID"] = "AKIDENV", ["AWS_SECRET_ACCESS_KEY"] = "s" }), "role").ResolveAsync(default);
        Check(role is { AccessKeyId: "ASIAROLE", SecretAccessKey: "role-secret", SessionToken: "role-token" }, "assumed role credentials");
        var sts = http.All.Single(request => request.Url.StartsWith("https://sts.eu-west-1", StringComparison.Ordinal));
        Check(sts.Body.StartsWith("Action=AssumeRole&Version=2011-06-15&RoleArn=arn%3Aaws%3Aiam%3A%3A123456789012%3Arole%2Fdev&RoleSessionName=aws-sdk-js-", StringComparison.Ordinal) &&
            sts.Header("authorization")!.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDBASE/20261008/eu-west-1/sts/aws4_request", StringComparison.Ordinal), "STS request: " + sts.Body);
        // SSO: the cached token (sha1 of the session name) for role credentials.
        var sso = await new AwsCredentialChain(Env(new() { ["AWS_PROFILE"] = "sso" })).ResolveAsync(default);
        Check(sso is { AccessKeyId: "ASIASSO", SessionToken: "sso-token" } && sso.Expiration == DateTimeOffset.FromUnixTimeMilliseconds(1791460800000), "sso credentials");
        var portal = http.All.Last(request => request.Url.StartsWith("https://portal.sso", StringComparison.Ordinal));
        Check(portal.Url == "https://portal.sso.us-east-2.amazonaws.com/federation/credentials?account_id=111122223333&role_name=Dev" &&
            portal.Header("x-amz-sso_bearer_token") == "sso-access", "sso portal request");
        // The library refuses credential_process unless the host enables it.
        Check((await Throws<AwsCredentialsException>(() => new AwsCredentialChain(Env(new() { ["AWS_PROFILE"] = "proc" })).ResolveAsync(default))).Message
            .Contains("credential_process, which PiSharp does not run", StringComparison.Ordinal), "credential_process refused");
        var enabled = new AwsEnvironment(_ => null, home, new HttpMessageInvoker(http, disposeHandler: false), time)
        { RunCredentialProcess = (command, _) => Task.FromResult(command == "/usr/bin/creds" ? """{"Version":1,"AccessKeyId":"AKIDPROC","SecretAccessKey":"p"}""" : "{}") };
        Equal("AKIDPROC", (await new AwsCredentialChain(enabled, "proc").ResolveAsync(default)).AccessKeyId, "enabled credential_process");
        // The CLI enables it as the AWS SDK does (child_process.exec through the platform shell); a failing command is an error.
        var printed = await PiSharp.Cli.Commands.AwsCredentialProcess.RunAsync(OperatingSystem.IsWindows()
            ? "echo {\"Version\":1,\"AccessKeyId\":\"AKIDSHELL\",\"SecretAccessKey\":\"s\"}" : "printf '%s' '{\"Version\":1,\"AccessKeyId\":\"AKIDSHELL\",\"SecretAccessKey\":\"s\"}'", default);
        Check(printed.Contains("\"AccessKeyId\":\"AKIDSHELL\"", StringComparison.Ordinal), "cli credential_process output: " + printed);
        Check((await Throws<AwsCredentialsException>(() => PiSharp.Cli.Commands.AwsCredentialProcess.RunAsync("exit 3", default))).Message
            .StartsWith("Command failed: exit 3", StringComparison.Ordinal), "cli credential_process failure");
        // Web identity from a profile; then the environment's token file.
        Equal("ASIAWEB", (await new AwsCredentialChain(Env(new()), "web").ResolveAsync(default)).AccessKeyId, "profile web identity");
        var webRequest = http.All.Last(request => request.Url.StartsWith("https://sts.us-east-1", StringComparison.Ordinal));
        Check(webRequest.Body.Contains("WebIdentityToken=web-token", StringComparison.Ordinal) && webRequest.Header("authorization") is null, "unsigned web identity call");
        // Remote providers: the ECS endpoint (with its authorization token), then IMDSv2. Expiring credentials are refreshed.
        var ecs = new AwsCredentialChain(Env(new() { ["AWS_PROFILE"] = "missing", ["AWS_CONTAINER_CREDENTIALS_RELATIVE_URI"] = "/creds", ["AWS_CONTAINER_AUTHORIZATION_TOKEN"] = "ecs-auth" }));
        Equal("ASIAECS", (await ecs.ResolveAsync(default)).AccessKeyId, "ecs");
        Equal("ecs-auth", http.All.Last(request => request.Url.StartsWith("http://169.254.170.2", StringComparison.Ordinal)).Header("authorization"), "ecs authorization");
        var ecsCalls = http.All.Count(request => request.Url.StartsWith("http://169.254.170.2", StringComparison.Ordinal));
        await ecs.ResolveAsync(default);
        Equal(ecsCalls + 1, http.All.Count(request => request.Url.StartsWith("http://169.254.170.2", StringComparison.Ordinal)), "credentials expiring within five minutes are re-fetched");
        var imds = new AwsCredentialChain(Env(new() { ["AWS_PROFILE"] = "missing" }));
        Equal("ASIAIMDS", (await imds.ResolveAsync(default)).AccessKeyId, "imds");
        Equal("imds-token", http.All.Last(request => request.Url.EndsWith("/role-a", StringComparison.Ordinal)).Header("x-aws-ec2-metadata-token"), "imdsv2 token");
        var imdsCalls = http.All.Count; await imds.ResolveAsync(default);
        Equal(imdsCalls, http.All.Count, "cached credentials are reused");
        Check((await Throws<AwsCredentialsException>(() => new AwsCredentialChain(Env(new() { ["AWS_PROFILE"] = "missing", ["AWS_EC2_METADATA_DISABLED"] = "true" })).ResolveAsync(default))).Message
            == "Could not load credentials from any providers", "no provider");
        // The ini parser: sections, nested values and comments.
        var parsed = AwsSharedFiles.ParseIni("# top\n[a]\nkey = value # note\nservices =\n  bedrock = x\n[b]\nk=v\n");
        Check(parsed.Count == 2 && parsed[0].Values["key"] == "value" && parsed[0].Values["services.bedrock"] == "x" && parsed[1].Values["k"] == "v", "ini parse");
    }
}
