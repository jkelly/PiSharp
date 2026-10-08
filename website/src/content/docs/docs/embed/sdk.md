---
title: Use the SDK
description: Embed PiSharp's libraries in your own .NET app.
---

PiSharp is split into libraries so you can take only the layers you need. Each is published to NuGet.

| If you want… | Reference |
| --- | --- |
| A streaming LLM client for several providers | `PiSharp.AI` |
| An agent loop with your own tools | `PiSharp.Agent`, `PiSharp.Tools` |
| Durable, branchable sessions | `PiSharp.Sessions` |
| The full coding agent | `PiSharp.CodingAgent` |
| To drive PiSharp from another process | `PiSharp.Rpc` |
| To write an extension | `PiSharp.Extensions.Abstractions` |

```powershell
dotnet add package PiSharp.CodingAgent
```

See the [SDK page](/sdk/) for every package and how the layers build on each other.

:::note[Examples coming]
End-to-end embedding examples will be added here as the public API settles.
:::
