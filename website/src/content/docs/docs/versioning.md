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
| `1.2.0` | Synced to Pi 1.2.0 |
| `1.2.0.1` | C#-only patch on Pi 1.2.0: the experimental durable port |
| `1.2.0.2` | C#-only patch on Pi 1.2.0: experimental media, service tiers, custom agents and the durable device server |
| `1.5.0` | Synced to Pi 1.5.0. Pi 1.3.0 and 1.4.0 had no PiSharp release; their changes are in 1.5.0 |
| `1.5.0.1` | Patch on Pi 1.5.0: reviewed gap corrections, Chord/hosting, Durable.Store, remote environments and evaluation tooling |
| `1.5.2` | Synced to Pi 1.5.2, including 1.5.1; source, hosted and package validation retain explicit limitations |

Bumping the third number instead (`0.99.2`) would claim to match a Pi release that PiSharp doesn't match. NuGet and .NET both order four-part versions correctly, so `0.99.1.2` sorts after `0.99.1` and before `1.1.0`.

The [changelog](/changelog/) labels each release as either a **Pi sync** or a **C# patch**. [Releasing](/docs/contribute/releasing/) describes how a release is prepared and published.

The current published release is **1.5.2**, on Pi **1.5.2**, including the intervening 1.5.1 changes. A source build reports `VersionPrefix` from `Directory.Build.props`. Features on `main` that are in no release yet are marked **Unreleased** or **candidate**; a source version bump alone is not a release or completed parity claim. Future candidate changelogs remain excluded from released RSS until publication is verified. See the [1.5.2 release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.5.2.md) and [earlier 1.5.0.1 updates](/docs/guides/gap-updates/).
