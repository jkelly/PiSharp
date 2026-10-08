---
title: How versions work
description: PiSharp's version is the Pi version it matches.
---

PiSharp's version is the version of Pi it matches. When PiSharp syncs to a new Pi release, its version moves to that release's number.

C#-only fixes that don't change the Pi baseline add a fourth number.

| Version | Meaning |
| --- | --- |
| `0.99.1` | Matches Pi v0.99.1 |
| `0.99.1.1` | First C#-only patch on the Pi v0.99.1 baseline |
| `0.99.1.2` | Second C#-only patch, same baseline |
| `1.1.0` | Synced to Pi 1.1.0 |

Bumping the third number instead (`0.99.2`) would claim to match a Pi release that PiSharp doesn't match. NuGet and .NET both order four-part versions correctly, so `0.99.1.2` sorts after `0.99.1` and before `1.1.0`.

The [changelog](/changelog/) labels each release as either a **Pi sync** or a **C# patch**.
