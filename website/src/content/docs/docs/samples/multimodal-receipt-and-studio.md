---
title: "Walkthrough: Vision receipts & media studio"
description: Annotated guide to samples/agents/16-ReceiptToExpense (multimodal image input + integer cents) and 27-ProductContentStudio (opt-in PiSharp.AI image generation).
---

Two practical samples illustrate opposite ends of **multimodal agents** in PiSharp:
- [`16-ReceiptToExpense`](https://github.com/jkelly/PiSharp/tree/main/samples/agents/16-ReceiptToExpense) accepts a **receipt image** (`--image receipt.png`), enforces strict **integer-cent** arithmetic in C#, and rejects unbalanced extractions before expense classification.
- [`27-ProductContentStudio`](https://github.com/jkelly/PiSharp/tree/main/samples/agents/27-ProductContentStudio) drafts constrained product copy offline and adds an **approval-gated image generation** path powered by `PiSharp.AI.ModelOperations.GenerateImagesAsync`.

## Part 1: Multimodal input & integer cents (`16-ReceiptToExpense`)

### Run it

```powershell
# Offline OCR-text fixture demo (yields balanced $17.82 Cedar Cafe Meals expense)
dotnet run --project samples/agents/16-ReceiptToExpense -- --workspace artifacts/receipt-demo

# Deterministic self-tests (checks fractional cent rejection and unbalanced sum rejection)
dotnet run --project samples/agents/16-ReceiptToExpense -- --self-test

# Live vision extraction on a real PNG, JPEG or WebP receipt photo
dotnet run --project samples/agents/16-ReceiptToExpense -- --live --api openai-responses --model gpt-6.2-sol --image path/to/receipt.png --workspace artifacts/receipt-live
```

### Why the schema swaps tools in image mode

When `--image` is omitted, `Workflow.Create` registers `parse_receipt_text` against the fixture's OCR text. When `--image` is supplied (which requires `--live`), `SampleHost` attaches the validated image bytes (up to 20 MiB) to the native multimodal request and swaps in **`capture_receipt`**:

```csharp
new("capture_receipt",
    "Submit merchant, date, totalCents, and line charges with integer amountCents read from the attached receipt image. " +
    "Use cents (17.82 becomes 1782), not fractional dollar numbers.",
    SampleJson.Schema("""
    {
      "merchant": {"type":"string"},
      "date": {"type":"string"},
      "totalCents": {"type":"integer","minimum":1,"maximum":9000000000000},
      "charges": {
        "type":"array",
        "items": {
          "type":"object",
          "properties": {"label":{"type":"string"},"amountCents":{"type":"integer","minimum":0,"maximum":9000000000000}},
          "required":["label","amountCents"],
          "additionalProperties":false
        }
      }
    }
    """, "merchant", "date", "totalCents", "charges"),
    (a, _) => Task.FromResult<JsonNode?>(Capture(c, a)))
```

Two deliberate engineering choices keep vision extraction reliable:
1. **Integer cents instead of floats:** `type: "integer"` prevents IEEE-754 rounding drift and lets `SchemaValidation` reject fractional cents (`1782.5`) before the handler runs.
2. **Sum verification before classification:** `Capture` sums every `amountCents` entry and rejects the receipt (`"Charges do not sum to total"`) if it differs from `totalCents` by more than 1 cent, prompting the vision model to re-check tax or line items before `classify_expense` can run.

## Part 2: Opt-in image generation (`27-ProductContentStudio`)

### Run it

```powershell
# Offline fixture demo (writes product-copy.json and illustration-specification visual-asset.json)
dotnet run --project samples/agents/27-ProductContentStudio -- --workspace artifacts/studio-demo

# Offline self-test (exercises ModelOperationApis.GenerateImagesAsync against a fake HttpMessageHandler)
dotnet run --project samples/agents/27-ProductContentStudio -- --self-test

# Opt-in live image generation via OpenRouter (requires OPENROUTER_API_KEY + --generate-image + --approve)
dotnet run --project samples/agents/27-ProductContentStudio -- --generate-image --provider openrouter --model IMAGE_MODEL --key-env OPENROUTER_API_KEY --approve --workspace artifacts/studio-live
```

### Calling `ModelOperationApis.GenerateImagesAsync` safely

Inside `Workflow.cs`, `prepare_visual_asset` refuses to invoke paid image generation unless **both** `--generate-image` and `--approve` are set, verifies the returned MIME type (`image/png`, `image/jpeg`, `image/webp`) and byte ceiling (10 MiB), and redacts any error message via `SecretRedactor.Redact`:

```csharp
var model = new ImageModel(c.Model, c.Model, OpenRouterImages.ApiId, "openrouter",
    string.IsNullOrWhiteSpace(c.BaseUrl) ? "https://openrouter.ai/api/v1" : c.BaseUrl,
    ["text"], new ModelCost(0, 0, 0, 0), ["image"]);

var output = await ModelOperationApis.GenerateImagesAsync(model,
    new ImagesContext([new ImagesTextBlock(brief)]),
    new ImagesOptions { ApiKey = apiKey, Http = http, MaxRetries = 0 }, ct);
```

And notice how `--self-test` verifies the entire request/response/base64-decode pipeline **offline** by injecting a fake `HttpMessageHandler` into `ImagesOptions.Http` — zero live API calls required in CI.

## Try breaking it

1. **Try `--image` without `--live` on sample 16:** verify it fails fast (`Image extraction requires --live and a vision-capable model.`) rather than silently pretending OCR ran.
2. **Try `--generate-image` without `--approve` on sample 27:** confirm `prepare_visual_asset` blocks the paid image API call.
3. **Explore `PiSharp.AI.Media` and 1.6.0 `FitImages`:** for video, TTS speech, STT transcription, embeddings, and reranking, see [Media library](/docs/embed/media/); run `dotnet run --project samples/Durable.Examples -- 47-fit-images` to see automatic image fitting for durable tool results.
