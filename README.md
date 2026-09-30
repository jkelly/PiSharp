# PiSharp

PiSharp is a planned native .NET 10 / C# port of the [Pi coding agent](https://github.com/earendil-works/pi).

This repository is at the planning stage. It contains project documentation only: there is no runnable implementation, and no feature parity or test coverage is claimed.

## Planned direction

- A native C# core that runs without Node.js.
- Native C# plugins through a dedicated plugin SDK.
- An optional Node.js bridge for interoperability; Node.js will only be needed when using that bridge.

These are design goals, not implemented capabilities. Read the [architecture and implementation strategy](docs/architecture.md) and the [planning index](docs/plans/README.md) for the proposed phases.

## Upstream baseline and attribution

The canonical upstream is [earendil-works/pi](https://github.com/earendil-works/pi). Planning is pinned to **v0.99.1**, commit [`d86654abb8862e201933517d6f1fce9f88dd117f`](https://github.com/earendil-works/pi/tree/d86654abb8862e201933517d6f1fce9f88dd117f).

Upstream Pi is MIT-licensed. Its [license at the planning baseline](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/LICENSE) identifies **Copyright (c) 2025 Mario Zechner**. No upstream implementation code is included in this initial repository. Any future copied or adapted material must retain the applicable upstream copyright and license notices.

PiSharp is an independent project; no upstream endorsement is claimed.

## Contributing

Planning feedback and documentation improvements are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before proposing changes.

## License

PiSharp's original contributions are available under the [MIT License](LICENSE).
