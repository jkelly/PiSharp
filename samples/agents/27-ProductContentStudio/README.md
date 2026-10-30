# Product content studio

The default fixture demo writes product copy and an illustration specification without calling a provider.

Run `dotnet run --project samples/agents/27-ProductContentStudio` for the offline demo. Add `--self-test` to exercise the fixture tools and the native image adapter with fake HTTP, with no paid request.

To generate an actual image with PiSharp.AI's OpenRouter image API, set `OPENROUTER_API_KEY` and run:

```sh
dotnet run --project samples/agents/27-ProductContentStudio -- --generate-image --provider openrouter --model IMAGE_MODEL --key-env OPENROUTER_API_KEY --approve --workspace OUTPUT_DIRECTORY
```

Replace `IMAGE_MODEL` with an OpenRouter image model ID. This command makes a paid provider request, writes `product-image.png`, `.jpg`, or `.webp` plus `visual-asset.json`, and requires the explicit `--generate-image` and `--approve` flags. `--input` can supply a different product brief; `--base-url` can select a compatible endpoint. The Agent that sequences the two tools still uses the fixture transport, so its `usage` array does not account for image provider billing. Provider errors or missing image data fail the run and produce no claimed image artifact.
