// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Samples.ModelCatalogMaintainer;

public static class CatalogEngine
{
    public static async Task<ScanResult> ScanAsync(MaintainerConfig config, JsonObject baseline, ISourceFetcher fetcher, CancellationToken cancellationToken = default)
    {
        config.Validate(); CatalogJson.ValidateOverlay(baseline);
        var overlay = (JsonObject)baseline.DeepClone();
        var providers = overlay["providers"]!.AsObject();
        var changes = new List<string>(); var warnings = new List<string>(); var errors = new List<string>();
        var priceChanges = new List<PriceChange>(); var evidence = new List<JsonObject>();
        foreach (var configProvider in config.Providers.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (!configProvider.Enabled || config.ExcludedProviders.Contains(configProvider.Id, StringComparer.Ordinal))
            {
                warnings.Add($"{configProvider.Id}: skipped by configuration; existing entries preserved.");
                continue;
            }
            try
            {
                var document = await fetcher.FetchAsync(configProvider, cancellationToken);
                if (document.StatusCode != 200 || document.Url != configProvider.Endpoint)
                    throw new InvalidDataException("Source status or URL does not match the approved endpoint.");
                evidence.Add(new JsonObject
                {
                    ["provider"] = configProvider.Id, ["url"] = document.Url, ["sha256"] = document.Sha256,
                    ["retrievedAt"] = document.RetrievedAt, ["statusCode"] = document.StatusCode, ["etag"] = document.ETag
                });
                var observations = SourceParser.Parse(configProvider, document, warnings);
                var models = providers[configProvider.Id] as JsonObject ?? new JsonObject();
                if (providers[configProvider.Id] is null) providers[configProvider.Id] = models;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var observation in observations.OrderBy(x => x.Id, StringComparer.Ordinal))
                {
                    seen.Add(observation.Id);
                    var previous = models[observation.Id] as JsonObject;
                    var metadata = previous?["metadata"] is JsonObject oldMeta ? (JsonObject)oldMeta.DeepClone() : new JsonObject();
                    foreach (var (key, value) in observation.Metadata)
                    {
                        // Source discovery is not runtime verification. Existing routes and model types need explicit human review.
                        if (key is "api" or "baseUrl" or "type" && metadata[key] is not null && !JsonNode.DeepEquals(metadata[key], value))
                        {
                            warnings.Add($"{observation.Provider}/{observation.Id}: source {key} differs from the existing route; preserving the existing value for explicit runtime review.");
                            continue;
                        }
                        if (key != "runtimeCandidate") metadata[key] = value?.DeepClone();
                    }
                    metadata["runtimeCandidate"] = metadata["type"]?.GetValue<string>() == "chat" && metadata["api"]?.GetValue<string>() is "openai-completions" or "openai-responses" or "anthropic-messages" or "google-generative-ai";
                    var prices = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                    if (previous?["prices"] is JsonArray priorPrices)
                        foreach (var p in priorPrices)
                        {
                            var normalized = Price.Normalize(p!.AsObject());
                            if (p["provenance"] is { } provenance) normalized["provenance"] = provenance.DeepClone();
                            prices.Add(Price.Key(normalized), normalized);
                        }
                    var observedPriceKeys = observation.Prices.Select(Price.Key).ToHashSet(StringComparer.Ordinal);
                    foreach (var retained in prices.Where(p => !observedPriceKeys.Contains(p.Key)))
                        warnings.Add($"{observation.Provider}/{observation.Id}: prior {retained.Value["kind"]} price ({retained.Value["currency"]}, {retained.Value["tier"]}, {retained.Value["region"]}) missing from current response; retained as prior evidence, current price unverified.");
                    foreach (var incoming in observation.Prices)
                    {
                        var key = Price.Key(incoming);
                        prices.TryGetValue(key, out var oldPrice);
                        var oldAmount = CatalogJson.Decimal(oldPrice?["amount"]);
                        var newAmount = CatalogJson.Decimal(incoming["amount"]);
                        // Missing/unknown values never erase a previously observed amount.
                        if (newAmount is null && oldPrice is not null) continue;
                        if (oldPrice is not null && oldAmount == newAmount) continue;
                        var next = (JsonObject)incoming.DeepClone(); next["provenance"] = observation.Provenance.DeepClone();
                        prices[key] = next;
                        if (newAmount is { } known)
                        {
                            var dimension = (JsonObject)incoming.DeepClone(); dimension.Remove("amount");
                            priceChanges.Add(new PriceChange(observation.Provider, observation.Id, dimension, oldAmount, known, document.Url));
                        }
                    }
                    var nextModel = new JsonObject
                    {
                        ["metadata"] = metadata,
                        ["prices"] = new JsonArray(prices.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (JsonNode?)x.Value).ToArray())
                    };
                    // Provenance changes only when meaningful catalog fields change. Retrieval time stays in the run report.
                    var priorComparable = previous is null ? null : (JsonObject)previous.DeepClone(); priorComparable?.Remove("provenance");
                    if (CatalogJson.Canonical(priorComparable) == CatalogJson.Canonical(nextModel)) continue;
                    nextModel["provenance"] = observation.Provenance.DeepClone();
                    models[observation.Id] = nextModel;
                    changes.Add($"{observation.Provider}/{observation.Id}: {(previous is null ? "added" : "updated")}");
                }
                foreach (var oldId in models.Select(x => x.Key).Where(id => !seen.Contains(id)).Order(StringComparer.Ordinal))
                    warnings.Add($"{configProvider.Id}/{oldId}: absent or excluded; retained without deletion.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or HttpRequestException or OperationCanceledException or FormatException or InvalidOperationException or OverflowException or ArgumentException)
            {
                errors.Add($"{configProvider.Id}: {SafeError(ex)}");
            }
        }
        // Never publish a partially refreshed snapshot as a successful scan.
        if (errors.Count != 0)
            return new ScanResult((JsonObject)baseline.DeepClone(), false, false, [], [], warnings, errors, evidence);
        var changed = CatalogJson.Canonical(baseline) != CatalogJson.Canonical(overlay);
        return new ScanResult(overlay, true, changed, priceChanges.OrderBy(x => x.Provider, StringComparer.Ordinal)
            .ThenBy(x => x.Model, StringComparer.Ordinal).ThenBy(x => CatalogJson.Canonical(x.Dimension), StringComparer.Ordinal).ToArray(), changes, warnings, errors, evidence);
    }

    private static string SafeError(Exception ex) => ex switch
    {
        HttpRequestException => "Network request failed; prior catalog retained.",
        OperationCanceledException => "Source timeout; prior catalog retained.",
        InvalidDataException => ex.Message,
        _ => $"Invalid provider data ({ex.GetType().Name}); prior catalog retained."
    };
}

public static class ChangeReport
{
    public static string Markdown(ScanResult scan)
    {
        var text = new StringBuilder();
        text.AppendLine("# Model catalog maintenance").AppendLine();
        text.AppendLine(scan.Complete ? "All enabled provider adapters completed." : "Scan incomplete. No overlay update or publication is permitted.").AppendLine();
        text.AppendLine("Upstream pinned catalog shards are unchanged. Missing and excluded models are retained. Prices are comparable only within identical currency, unit, tier, region and conditions.").AppendLine();
        foreach (var change in scan.Changes) text.Append("- ").AppendLine(Escape(change));
        if (scan.PriceChanges.Count > 0)
        {
            text.AppendLine().AppendLine("| Provider/model | Dimension | Old | New | Absolute change | Percent | Source |");
            text.AppendLine("|---|---|---:|---:|---:|---:|---|");
            foreach (var change in scan.PriceChanges)
            {
                var dimension = change.Dimension;
                var label = $"{dimension["kind"]} / {dimension["currency"]} per {dimension["unit"]} / {dimension["tier"]} / {dimension["region"]} / {dimension["conditions"]?.ToJsonString()}";
                text.Append("| ").Append(Escape(change.Provider + "/" + change.Model)).Append(" | ").Append(Escape(label))
                    .Append(" | ").Append(Number(change.OldAmount, "unknown"))
                    .Append(" | ").Append(Number(change.NewAmount)).Append(" | ").Append(Number(change.Absolute))
                    .Append(" | ").Append(change.Percent is { } percent ? percent.ToString("0.######", CultureInfo.InvariantCulture) + "%" : "n/a (unknown or zero baseline)")
                    .Append(" | [official source](").Append(change.SourceUrl).AppendLine(") |");
            }
        }
        if (scan.Warnings.Count > 0)
        {
            text.AppendLine().AppendLine("## Review notes").AppendLine();
            foreach (var warning in scan.Warnings) text.Append("- ").AppendLine(Escape(warning));
        }
        if (scan.Errors.Count > 0)
        {
            text.AppendLine().AppendLine("## Failures").AppendLine();
            foreach (var error in scan.Errors) text.Append("- ").AppendLine(Escape(error));
        }
        text.AppendLine().AppendLine("## Evidence").AppendLine();
        foreach (var evidence in scan.Evidence)
            text.Append("- [").Append(Escape(evidence["provider"]!.GetValue<string>())).Append("](")
                .Append(evidence["url"]).Append("): SHA-256 `").Append(evidence["sha256"]).AppendLine("`.");
        text.AppendLine().AppendLine("Retrieval times and raw responses are in the run artifacts, outside the versioned overlay. No automatic merge is performed.");
        return text.ToString();
    }
    private static string Number(decimal? value, string missing = "n/a") => value?.ToString("0.############################", CultureInfo.InvariantCulture) ?? missing;
    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);
}
